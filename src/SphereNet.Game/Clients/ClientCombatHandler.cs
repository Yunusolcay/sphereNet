using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Objects;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

/// <summary>
/// Combat handler extracted from the GameClient.Combat partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// HOT PATH: movement validation (throttle/credit/queue, reject taxonomy),
/// speech, attack/swing loop, death/resurrect transitions, spell casting and
/// the per-tick client state pump. Method bodies moved verbatim; the private
/// context shims below enumerate exactly what this handler needs from
/// GameClient. The public static movement/speed-hack config surface stays on
/// GameClient (Program.cs wiring + ResetEngineStatics discipline).
/// </summary>
public sealed class ClientCombatHandler
{
    private readonly IClientContext _client;

    internal ClientCombatHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private MovementEngine? _movement => _client.MoveEng;
    private SpeechEngine? _speech => _client.SpeechEng;
    private SpellEngine? _spellEngine => _client.Spells;
    private DeathEngine? _deathEngine => _client.DeathEng;
    private Mounts.MountEngine? _mountEngine => _client.MountE;
    private ILogger _logger => _client.Log;
    private ClientViewCache View => _client.View;
    private ClientTargetState Targets => _client.Targets;
    private bool IsPlaying => _client.IsPlaying;
    private const int UpdateRange = GameClient.UpdateRange;

    /// <summary>Record a corpse this client was just shown directly (outside the
    /// view delta). Both halves of the view cache have to move together: an entry
    /// in KnownItems with no LastKnownItemState makes the delta treat the corpse
    /// as "already up to date" and never send it again, so a later change to its
    /// look would never reach this client until a resync.</summary>
    private void MarkCorpseKnown(Item corpse)
    {
        View.KnownItems.Add(corpse.Uid.Value);
        View.LastKnownItemState[corpse.Uid.Value] =
            (corpse.X, corpse.Y, corpse.Z, corpse.DispIdFull, corpse.Hue, corpse.Amount, corpse.Direction);
    }
    private const int VitalsPacketIntervalMs = GameClient.VitalsPacketIntervalMs;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint, Character>? BroadcastMoveNearby => _client.BroadcastMoveNearby;
    private Action<Character>? BroadcastCharacterAppear => _client.BroadcastCharacterAppear;
    private Action<Point3D, int, uint, Action<Character, GameClient>>? ForEachClientInRange => _client.ForEachClientInRange;
    private Action<Character>? OnCharacterDeathOfOther => _client.OnCharacterDeathOfOther;
    private static Action<Character>? OnWakeNpc => GameClient.OnWakeNpc;
    private static int WalkBufferMax => GameClient.WalkBufferMax;
    private static int WalkRegenPerSecond => GameClient.WalkRegenPerSecond;
    private static int MoveToleranceMs => GameClient.MoveToleranceMs;
    private static int MovePermissiveOffsetMs => GameClient.MovePermissiveOffsetMs;
    private static int MoveRejectResyncMs => GameClient.MoveRejectResyncMs;
    private static int MoveViolationKickThreshold => GameClient.MoveViolationKickThreshold;
    private static Func<long> MoveClock => GameClient.MoveClock;
    private static bool MovementCreditEnabled => GameClient.MovementCreditEnabled;
    private static int MovementCreditBaseMs => GameClient.MovementCreditBaseMs;
    private static int MovementCreditMaxMs => GameClient.MovementCreditMaxMs;
    private static int MovementQueueCapacity => GameClient.MovementQueueCapacity;
    private static bool SpeedHackDetectionEnabled => GameClient.SpeedHackDetectionEnabled;
    private static double SpeedHackRateThreshold => GameClient.SpeedHackRateThreshold;
    private static int SpeedHackBurstWindow => GameClient.SpeedHackBurstWindow;
    private static int SpeedHackHistorySize => GameClient.SpeedHackHistorySize;
    private static int SpeedHackCooldownMs => GameClient.SpeedHackCooldownMs;
    private short _lastHits { get => _client.LastHits; set => _client.LastHits = value; }
    private short _lastMana { get => _client.LastMana; set => _client.LastMana = value; }
    private short _lastStam { get => _client.LastStam; set => _client.LastStam = value; }
    private long _lastVitalsPacketTick { get => _client.LastVitalsPacketTick; set => _client.LastVitalsPacketTick = value; }
    private void SysMessage(string text) => _client.SysMessage(text);
    private void Send(SphereNet.Network.Packets.PacketWriter packet) => _client.Send(packet);
    private void NpcSpeech(Character npc, string text) => _client.NpcSpeech(npc, text);
    private void SendCharacterStatus(Character ch) => _client.SendCharacterStatus(ch);
    private byte GetNotoriety(Character ch) => _client.GetNotoriety(ch);
    private static byte BuildMobileFlags(Character ch) => GameClient.BuildMobileFlags(ch);
    private void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1) => _client.SetPendingTarget(callback, cursorType);
    private void ClearPendingTargetState() => _client.ClearPendingTargetState();
    private void SendDrawObject(Character ch) => _client.SendDrawObject(ch);
    private Character? DismountCharacter() => _client.DismountCharacter();
    private bool TryHandlePetCommand(string text) => _client.TryHandlePetCommand(text);
    private bool TryHandleCommandSpeech(string text) => _client.TryHandleCommandSpeech(text);
    private void SetWarMode(bool warMode, bool syncClients, bool preserveTarget) => _client.SetWarMode(warMode, syncClients, preserveTarget);
    private void FaceTarget(Character target) => _client.FaceTarget(target);
    private static int GetSwingDelayMs(Character attacker, Item? weapon) => GameClient.GetSwingDelayMs(attacker, weapon);
    private static ushort GetSwingAction(Character attacker, Item? weapon) => GameClient.GetSwingAction(attacker, weapon);
    private static ushort GetWeaponHitSound(Item? weapon) => GameClient.GetWeaponHitSound(weapon);
    private static ushort GetWeaponMissSound(Item? weapon) => GameClient.GetWeaponMissSound(weapon);
    private static ushort GetDefenderHitSound(Character defender) => GameClient.GetDefenderHitSound(defender);
    private static (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] BuildEquipmentList(Character ch) => GameClient.BuildEquipmentList(ch);
    private void BroadcastAnimation(Character actor, ushort legacyAction, NewAnimationGesture gesture, byte mode = 0, byte animDelay = 0) => _client.BroadcastAnimation(actor, legacyAction, gesture, mode, animDelay);
    private static void GetDirectionDelta(Direction dir, out short dx, out short dy) => GameClient.GetDirectionDelta(dir, out dx, out dy);
    private void TickPendingSkill() => _client.TickPendingSkill();
    private void TickPendingCraft() => _client.TickPendingCraft();

    private uint _lastCombatNotifyTarget;

    /// <summary>Movement throttle state (decomposition phase 1). Token bucket
    /// seeded with WalkBufferMax at construction — same timing as the former
    /// field initializer.</summary>
    private ClientMovementThrottle Throttle => _throttle ??= new ClientMovementThrottle(WalkBufferMax);
    private ClientMovementThrottle? _throttle;

    private long _moveRejectResyncUntil;
    private long? _movementBatchNow;

    // Credit-based movement state (active only when MovementCreditEnabled=true)
    private int _movementCreditMs;
    private long _movementCreditLastTick;
    private Movement.MovementHistory? _movementHistory;
    private Movement.SpeedHackDetector? _speedHackDetector;

    public void HandleMovementBatch(IReadOnlyList<SphereNet.Network.State.MovementStep> steps)
    {
        if (_character == null || steps.Count == 0)
            return;

        long baseNow = MoveClock();
        long virtualNow = baseNow;
        for (int i = 0; i < steps.Count; i++)
        {
            _movementBatchNow = virtualNow;
            var step = steps[i];
            bool accepted = HandleMove(step.Direction, step.Sequence, step.FastWalkKey);
            if (!accepted)
            {
                _logger.LogWarning("[move_batch_stop] char={Char} step={Step}/{Total} seq={Seq} dir=0x{Dir:X2} mode={Mode} pos={X},{Y},{Z}",
                    _character.Name, i + 1, steps.Count, step.Sequence, step.Direction, step.Mode,
                    _character.X, _character.Y, _character.Z);
                break;
            }

            bool running = (step.Direction & 0x80) != 0;
            virtualNow += GetMoveDelay(running);
        }
        _movementBatchNow = null;
    }

    public void QueueMoveRequest(byte dir, byte seq, uint fastWalkKey)
    {
        if (_character == null) return;

        // NOTE: staff/GM movement is intentionally NOT short-circuited to an
        // immediate HandleMove anymore. Bypassing the queue meant GM steps were
        // accepted the instant they arrived — including the client's bursty
        // "doublet" packets at direction/speed transitions (two steps ~47ms
        // apart). Unpaced, those rendered as a forward "teleport" on screen.
        // Routing GM through the same queue lets Throttle.NextMoveTime hold the early
        // step to the natural move cadence (smoothing the doublet) exactly like
        // a normal player; the throttle inside HandleMove is still skipped for
        // GM, so GM is paced but never throttle-rejected. Movement speed is
        // unchanged — only the burst is smoothed.

        long now = MoveClock();
        _netState.LastActivityTick = now;

        if (_moveRejectResyncUntil > 0 && now < _moveRejectResyncUntil)
        {
            // Silently drop stale in-flight steps during the post-reject window.
            // Do NOT echo a 0x21 here: the original RejectMove already sent one
            // corrective reject, and re-sending one for every buffered step makes
            // the client resend → which we reject again → a tight 0x21 feedback
            // storm on low-latency links that freezes the player and reads as a
            // teleport. One reject, then absorb.
            _netState.WalkSequence = 0;
            return;
        }

        byte expectedSeq = _netState.WalkSequence;
        if (expectedSeq == 0 && seq > 1)
        {
            RejectStaleMove(seq, dir, now);
            return;
        }

        Throttle.Queue ??= new Movement.MovementQueueProcessor(MovementQueueCapacity);
        if (!Throttle.Queue.Enqueue(dir, seq, fastWalkKey, now))
        {
            RejectMove(seq, now);
            Throttle.Queue.Clear();
            return;
        }
    }

    public bool HandleMove(byte dir, byte seq, uint fastWalkKey)
    {
        if (_character == null) return false;
        // NOTE: do NOT silently drop walk packets when IsDead. Source-X
        // ghosts walk freely; if the server eats the request without
        // sending either 0x22 (MoveAck) or 0x21 (MoveReject) the client's
        // walk sequence stalls and the player ends up frozen on screen
        // even though their ghost body is rendered correctly. The
        // post-death "client cannot move" symptom in the death log was
        // exactly this: 0x2C death status arrived, then every subsequent
        // walk packet from the client was silently swallowed here.

        var direction = (Direction)(dir & 0x07);
        bool running = (dir & 0x80) != 0;

        long now = _movementBatchNow ?? MoveClock();
        _netState.LastActivityTick = now;

        if (_moveRejectResyncUntil > 0 && now < _moveRejectResyncUntil)
        {
            // Silently drop — no 0x21 echo (see HandleMovementBatch for why):
            // echoing a reject per buffered step causes a feedback storm.
            _netState.WalkSequence = 0;
            return false;
        }

        byte expectedSeq = _netState.WalkSequence;

        // After a reject, WalkSequence resets to 0. The client's first
        // move post-reject will be seq 0 or 1. Anything higher is a stale
        // speculative move still in flight from before the rejection —
        // processing it from the corrected position would move the
        // character in unexpected directions (teleportation). Drop them.
        if (expectedSeq == 0 && seq > 1)
        {
            RejectStaleMove(seq, dir, now);
            return false;
        }

        // Strict sequence validation (ServUO-style): reject out-of-order walk packets.
        if (expectedSeq != 0 && seq != expectedSeq)
        {
            RejectMove(seq, now);
            return false;
        }

        // Fast-walk replay check intentionally dropped: the server never ships
        // the 6-key stack via 0xBF sub 0x01 to the client, so modern clients
        // either send key=0 (skipped by the !=0 guard) or emit locally-generated
        // keys whose rotation we cannot predict. False positives manifested as
        // mid-run "square jumping" rejects. The time-based throttle below is
        // the speedhack barrier.
        _netState.LastFastWalkKey = fastWalkKey;

        // Source-X parity: a walk request whose direction differs from the
        // character's current facing is a turn-in-place, not a tile step.
        // Direction-change packets often arrive as a short burst while running;
        // treating them as movement consumes walk buffer and advances the server
        // one tile farther than the client, which feels like a hitch or side
        // teleport near stairs/walls.
        if (((byte)_character.Direction & 0x07) != ((byte)direction & 0x07))
        {
            AcceptTurn(direction, seq);
            return true;
        }

        // Fastwalk throttle: reject if moving too fast.
        if (_character.PrivLevel < PrivLevel.GM)
        {
            int moveDelay = GetMoveDelay(running);

            if (MovementCreditEnabled)
            {
                EnsureCreditState();
                if (!MovementCreditSystem.TryConsumeCredit(
                        ref _movementCreditMs, ref _movementCreditLastTick,
                        MovementCreditBaseMs, MovementCreditMaxMs, moveDelay, now))
                {
                    if (Throttle.Queue!.IsFull || !Throttle.Queue.Enqueue(dir, seq, fastWalkKey, now))
                    {
                        Throttle.ViolationCount++;
                        RejectMove(seq, now);
                        if (MoveViolationKickThreshold > 0 && Throttle.ViolationCount >= MoveViolationKickThreshold)
                            _netState.MarkClosing();
                        return false;
                    }
                    return false;
                }
            }
            else
            {
                RefillWalkTokens(now);

                string? rejectReason = null;
                if (Throttle.NextMoveTime > 0 && now + MoveToleranceMs < Throttle.NextMoveTime)
                    rejectReason = "throttle";
                else if (Throttle.WalkTokens <= 0)
                    rejectReason = "walk_buffer";

                if (rejectReason != null)
                {
                    // Source-X @UserExWalkLimit — the client exceeded the walk
                    // rate (token bucket dry). IsTrigUsed-gated: rejects can
                    // storm during client-side speed bursts.
                    if (rejectReason == "walk_buffer" &&
                        _triggerDispatcher?.IsCharTriggerUsed(CharTrigger.UserExWalkLimit) == true)
                        _triggerDispatcher.FireCharTrigger(_character, CharTrigger.UserExWalkLimit,
                            new TriggerArgs { CharSrc = _character, ScriptConsole = _client });

                    Throttle.ViolationCount++;
                    RejectMove(seq, now);
                    if (MoveViolationKickThreshold > 0 && Throttle.ViolationCount >= MoveViolationKickThreshold)
                        _netState.MarkClosing();
                    return false;
                }
            }
        }

        // Execute the move
        bool moved;
        short oldX = _character.X;
        short oldY = _character.Y;
        sbyte oldZ = _character.Z;
        SphereNet.Game.Movement.WalkCheck.Diagnostic moveDiag = default;
        if (_movement != null)
            moved = _movement.TryMoveDetailed(_character, direction, running, seq, out moveDiag);
        else
        {
            GetDirectionDelta(direction, out short dx, out short dy);
            var newPos = new Point3D((short)(_character.X + dx), (short)(_character.Y + dy), _character.Z, _character.MapIndex);
            _character.Direction = direction;
            _world.MoveCharacter(_character, newPos);
            moved = true;
        }

        if (moved)
        {
            _character.LastMoveTick = now;
            int moveDelay = GetMoveDelay(running);
            if (!MovementCreditEnabled && _character.PrivLevel < PrivLevel.GM && Throttle.WalkTokens > 0)
                Throttle.WalkTokens--;
            // Source-X parity: pace the next allowed walk to (delay - 30ms) so
            // client ping / server-tick jitter doesn't false-throttle a step
            // arriving a few ms early (CClientEvent.cpp Event_CheckWalkBuffer
            // sets m_timeNextEventWalk = now + iDelay with iDelay -= 30). The
            // walk-token bucket still caps the sustained rate.
            int pacedDelay = Math.Max(1, moveDelay - MovePermissiveOffsetMs);
            Throttle.NextMoveTime = now + pacedDelay;
            Throttle.ViolationCount = 0;

            bool speedMounted = _character.IsMounted || ((_character.SpeedMode & 0x01) != 0);
            _movementHistory?.Record(now, direction, running, speedMounted);
            if (SpeedHackDetectionEnabled && _speedHackDetector != null && _movementHistory != null)
            {
                var verdict = _speedHackDetector.Analyze(_movementHistory, speedMounted, running, now);
                if (verdict == Movement.SpeedVerdict.Violation)
                {
                    _logger.LogWarning("[speed_hack] char={Name} verdict={Verdict} avg={Avg:F0}ms burst={Burst}",
                        _character.Name, verdict,
                        _movementHistory.AverageIntervalMs(5),
                        _movementHistory.CountBurstMoves(moveDelay / 2, 5));
                    GameClient.RaiseSpeedHackDetected(_character, verdict);
                }
                else if (verdict == Movement.SpeedVerdict.Kick)
                {
                    _logger.LogWarning("[speed_hack_kick] char={Name}", _character.Name);
                    GameClient.RaiseSpeedHackDetected(_character, verdict);
                    _netState.MarkClosing();
                    return true;
                }
            }

            GetDirectionDelta(direction, out short expectedDx, out short expectedDy);
            short expectedX = (short)(oldX + expectedDx);
            short expectedY = (short)(oldY + expectedDy);

            if (_character.X != expectedX || _character.Y != expectedY)
            {
                _logger.LogWarning(
                    "[move_teleport] seq={Seq} dir={Dir} from {OldX},{OldY},{OldZ} expected {ExpX},{ExpY} actual {ActX},{ActY},{ActZ} — telepad/script moved character during CheckLocationEffects, suppressing MoveAck",
                    seq, direction, oldX, oldY, oldZ, expectedX, expectedY,
                    _character.X, _character.Y, _character.Z);
                return true;
            }

            byte notoriety = GetNotoriety(_character);

            _netState.SendPriority(new PacketMoveAck(seq, notoriety));

            byte flags = BuildMobileFlags(_character);
            byte dir77 = (byte)((byte)_character.Direction | (running ? 0x80 : 0));
            var movePacket = new PacketMobileMoving(
                _character.Uid.Value, _character.BodyId,
                _character.X, _character.Y, _character.Z, dir77,
                _character.Hue, flags, notoriety);

            if (BroadcastMoveNearby != null)
                BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
            else
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

            byte nextSeq = (byte)(seq + 1);
            if (nextSeq == 0) nextSeq = 1;
            _netState.WalkSequence = nextSeq;
            return true;
        }
        else
        {
            RejectMove(seq, now);
            ResendBlockingItems(direction);
            return false;
        }
    }

    /// <summary>Self-heal for the "invisible door" class of view desync: the
    /// client purges ground items on its own (distance culling, resync races),
    /// while the server's known-set still believes they were delivered — the
    /// player then bumps into something they cannot see. On a rejected step,
    /// re-send the dynamic items on the forward tile; a redraw of an item the
    /// client already has is a visual no-op.</summary>
    private void ResendBlockingItems(Direction direction)
    {
        if (_character == null) return;
        GetDirectionDelta(direction, out short bdx, out short bdy);
        var tile = new Point3D(
            (short)(_character.X + bdx), (short)(_character.Y + bdy),
            _character.Z, _character.MapIndex);
        foreach (var item in _world.GetItemsInRange(tile, 0))
        {
            if (item.IsDeleted || item.IsEquipped || item.ContainedIn.IsValid) continue;
            if (item.IsAttr(Core.Enums.ObjAttributes.Invis)) continue;
            _netState.Send(new PacketWorldItem(
                item.Uid.Value, item.DispIdFull, item.Amount,
                item.X, item.Y, item.Z, item.Hue));
        }
    }

    private void RejectMove(byte seq, long now, bool redrawSelf = false)
    {
        if (_character == null) return;
        _netState.SendPriority(new PacketMoveReject(seq, _character.X, _character.Y, _character.Z, CurrentFacingDir()));
        if (redrawSelf)
        {
            _netState.Send(new PacketDrawPlayer(
                _character.Uid.Value, _character.BodyId, _character.Hue,
                BuildMobileFlags(_character),
                _character.X, _character.Y, _character.Z, CurrentFacingDir()));
            SendDrawObject(_character);
        }
        _netState.WalkSequence = 0;
        int resyncMs = Math.Max(0, MoveRejectResyncMs);
        _moveRejectResyncUntil = resyncMs > 0 ? now + resyncMs : 0;
    }

    private void RejectStaleMove(byte seq, byte dir, long now)
    {
        if (_character == null) return;

        // A seq > 1 arriving while WalkSequence == 0 is an in-flight step the
        // client predicted BEFORE it processed our single corrective 0x21. The
        // client (ClassicUO) has already cleared its step queue, snapped to the
        // rejected position and reset its sequence to 0 — it is now resending
        // from seq 0. Replying here at all is harmful:
        //   * another 0x21 → the client ClearSteps + snaps AGAIN, and
        //   * a 0x20 DrawPlayer (the old redrawSelf) → a hard reposition/redraw,
        // and a burst of either makes ConfirmWalk see "bad steps" and fire a
        // full client-initiated resync request — that cascade is the visible
        // "walking teleport" / flash. So we DROP SILENTLY: emit nothing, keep
        // WalkSequence pinned at 0, and let the fresh seq-0 stream resume the
        // walk cleanly. On TCP the original 0x21 is guaranteed delivered, so the
        // client always resets — there is no deadlock to guard against.
        _netState.WalkSequence = 0;
    }

    private byte CurrentFacingDir()
    {
        return _character == null ? (byte)0 : (byte)((byte)_character.Direction & 0x07);
    }

    private void AcceptTurn(Direction direction, byte seq)
    {
        if (_character == null) return;

        _character.Direction = direction;
        byte notoriety = GetNotoriety(_character);
        _netState.SendPriority(new PacketMoveAck(seq, notoriety));

        byte flags = BuildMobileFlags(_character);
        var movePacket = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z, (byte)_character.Direction,
            _character.Hue, flags, notoriety);

        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value, _character);
        else
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, movePacket, _character.Uid.Value);

        byte nextSeq = (byte)(seq + 1);
        if (nextSeq == 0) nextSeq = 1;
        _netState.WalkSequence = nextSeq;

        _logger.LogDebug(
            "[move_turn] seq={Seq} dir={Dir} pos={X},{Y},{Z}",
            seq, direction, _character.X, _character.Y, _character.Z);
    }

    internal void ResetWalkValidator()
    {
        Throttle.NextMoveTime = 0;
        Throttle.WalkTokens = Math.Max(1, WalkBufferMax);
        Throttle.WalkTokenLastMs = 0;
        Throttle.ViolationCount = 0;
        _moveRejectResyncUntil = 0;

        _movementCreditMs = MovementCreditMaxMs;
        _movementCreditLastTick = 0;
        Throttle.Queue?.Clear();
        _movementHistory?.Clear();
        _speedHackDetector?.Reset();
    }

    private void EnsureCreditState()
    {
        Throttle.Queue ??= new Movement.MovementQueueProcessor(MovementQueueCapacity);
        _movementHistory ??= new Movement.MovementHistory(SpeedHackHistorySize);
        if (SpeedHackDetectionEnabled)
            _speedHackDetector ??= new Movement.SpeedHackDetector(
                SpeedHackRateThreshold, SpeedHackBurstWindow, SpeedHackCooldownMs);
    }

    private int GetMoveDelay(bool running)
    {
        if (_character == null)
            return MovementEngine.GetMoveDelay(false, running);
        return MovementEngine.GetMoveDelay(
            _character.IsMounted,
            running,
            _character.IsInWarMode,
            _character.SpeedMode);
    }

    public void TickMovementQueue(long nowMs)
    {
        if (Throttle.Queue == null || Throttle.Queue.Count == 0)
            return;

        if (Throttle.NextMoveTime > 0 && nowMs < Throttle.NextMoveTime)
            return;

        if (Throttle.Queue.TryDequeue(out byte qDir, out byte qSeq, out uint qKey))
        {
            bool accepted = HandleMove(qDir, qSeq, qKey);
            if (!accepted)
            {
                Throttle.Queue.Clear();
            }
            else if (_netState.WalkSequence == 0 && Throttle.Queue.Count > 0)
            {
                Throttle.Queue.Clear();
            }
        }
    }

    private void RefillWalkTokens(long now)
    {
        int maxTokens = Math.Max(1, WalkBufferMax);
        if (Throttle.WalkTokenLastMs <= 0)
        {
            Throttle.WalkTokens = maxTokens;
            Throttle.WalkTokenLastMs = now;
            return;
        }

        int regen = Math.Max(0, WalkRegenPerSecond);
        if (regen == 0 || Throttle.WalkTokens >= maxTokens)
        {
            Throttle.WalkTokenLastMs = now;
            Throttle.WalkTokens = Math.Min(Throttle.WalkTokens, maxTokens);
            return;
        }

        long elapsed = Math.Max(0, now - Throttle.WalkTokenLastMs);
        int add = (int)(elapsed * regen / 1000);
        if (add <= 0)
            return;

        Throttle.WalkTokens = Math.Min(maxTokens, Throttle.WalkTokens + add);
        Throttle.WalkTokenLastMs += add * 1000L / regen;
    }

    // ==================== Speech ====================

    public void HandleSpeech(byte type, ushort hue, ushort font, string text)
    {
        if (_character == null) return;

        // No speech rate limit — Source-X Event_Talk has none, and the invented
        // 10-per-5s burst cap here silently swallowed messages during fast
        // conversations (commands consumed the same quota). Extreme packet
        // floods are already handled at the network layer (MaxPacketsPerTick /
        // FloodDetectionCount).

        if (text.Length > 256)
            text = text[..256];

        if (TryHandleCommandSpeech(text))
            return;

        bool isGhost = _character.IsDead;
        // A ghost's words are garbled to the living who can't hear the dead. Keep the
        // clear text for the speaker, other ghosts and staff; route a garbled copy
        // through ProcessSpeech so NPCs/guild chat see scrambled text as before.
        string clearText = text;
        string routeText = text;
        if (isGhost)
        {
            hue = 0x0481; // ghost speech hue
            routeText = GhostSpeech.Garble(clearText);
        }

        _character.ClearHiddenState();

        // Pet commands — "all follow", "all guard", "petname follow" etc.
        if (!isGhost && TryHandlePetCommand(text))
        {
            // Still broadcast the speech so others hear it
        }

        // ProcessSpeech returns true when the utterance must NOT be broadcast —
        // either the speaker's @Speech self-trigger cancelled it (Source-X
        // Event_Talk RETURN 1) or it was routed as guild/alliance chat. finalText is
        // the utterance after the @Speech trigger may have rewritten it via ARGS.
        string finalText = routeText;
        if (_speech != null)
        {
            if (_speech.ProcessSpeech(_character, routeText, (TalkMode)type, hue, font, out finalText))
                return;
        }
        // A living speaker's rewritten text becomes what is broadcast. Ghost speech
        // stays garbled per-recipient (the rewrite ran on the scrambled copy), so its
        // clear text is left untouched.
        if (!isGhost)
            clearText = finalText;

        // Guild/alliance chat is non-spatial: SpeechEngine.RouteChannelMessage
        // delivers it per member (speaker echo included), so the local echo
        // and nearby broadcast below must not run for those modes.
        if ((TalkMode)type is TalkMode.Guild or TalkMode.Alliance)
            return;

        // Broadcast speech to nearby clients
        int range = type switch
        {
            8 => 3,  // whisper
            9 => 48, // yell
            _ => 18  // say
        };
        // Source-X CChar::Speak (CCharAct.cpp:3575): a Counsel+ yell is promoted
        // to TALKMODE_BROADCAST — every client on the server reads it. Only the
        // client packet goes global; NPC/item hearing stays at yell earshot
        // (SpeechEngine caps it), so a GM yell can't stall the world.
        if (type == 9 && _character.PrivLevel >= PrivLevel.Counsel)
            range = int.MaxValue;

        if (isGhost && ForEachClientInRange != null)
        {
            // Per-recipient ghost speech: the speaker (a ghost) and any other ghost /
            // staff observer reads the clear words; every other living listener gets
            // an independently-scrambled garble (Source-X MutateSpeech per listener).
            // ForEachClientInRange walks sectors by range, so the GM-broadcast
            // sentinel range must not reach it — a ghost yell stays at earshot.
            Send(MakeSpeechPacket(type, hue, font, clearText));
            ForEachClientInRange.Invoke(_character.Position, Math.Min(range, 48), _character.Uid.Value,
                (recipient, recipientClient) =>
                {
                    string heard = GhostSpeech.HearsGhostClearly(recipient)
                        ? clearText
                        : GhostSpeech.Garble(clearText);
                    recipientClient.Send(MakeSpeechPacket(type, hue, font, heard));
                });
            return;
        }

        // The speaker always sees their own words clearly; a ghost without a
        // per-recipient broadcaster falls back to garbling for every nearby listener.
        Send(MakeSpeechPacket(type, hue, font, clearText));
        BroadcastNearby?.Invoke(_character.Position, range,
            MakeSpeechPacket(type, hue, font, isGhost ? routeText : clearText),
            _character.Uid.Value);
    }

    private PacketSpeechUnicodeOut MakeSpeechPacket(byte type, ushort hue, ushort font, string text) =>
        new(_character!.Uid.Value, _character.BodyId, type, hue, font, "TRK", _character.Name, text);

    // ==================== Combat ====================

    public void HandleAttack(uint targetUid)
    {
        if (_character == null || CombatHelper.IsInvalidSwingParticipant(_character, asTarget: false))
            return;

        // Source-X style target clear: attacking 0 resets current fight target.
        if (targetUid == 0 || targetUid == 0xFFFFFFFF)
        {
            _character.FightTarget = Serial.Invalid;
            _character.ClearPendingHit();
            _character.NextAttackTime = 0;
            return;
        }

        var target = _world.FindChar(new Serial(targetUid));
        if (target == null || target == _character || target.MapIndex != _character.MapIndex ||
            CombatHelper.IsInvalidSwingParticipant(target, asTarget: true))
        {
            Send(new PacketAttackResponse(0));
            return;
        }

        if ((target.IsStatFlag(StatFlag.Hidden) || target.IsStatFlag(StatFlag.Invisible))
            && target.PrivLevel >= PrivLevel.Counsel
            && _character.PrivLevel < PrivLevel.Counsel)
        {
            Send(new PacketAttackResponse(0));
            return;
        }

        if (CombatHelper.IsCombatBlockedByRegion(_world, _character, target))
        {
            SysMessage(ServerMessages.Get("combat_nopvp"));
            Send(new PacketAttackResponse(0));
            return;
        }

        if (_triggerDispatcher != null && _character.FightTarget != target.Uid)
        {
            var attackResult = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.Attack,
                new TriggerArgs { CharSrc = target, O1 = target });
            if (attackResult == TriggerResult.True)
            {
                Send(new PacketAttackResponse(0));
                return;
            }
        }

        // A cancelled combat start is only intent: do not mutate ownership,
        // notoriety or fight memories before scripts accept the engagement.
        if (_triggerDispatcher != null && _character.FightTarget != target.Uid)
        {
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.CombatStart,
                new TriggerArgs { CharSrc = target, O1 = target });
            if (result == TriggerResult.True)
            {
                Send(new PacketAttackResponse(0));
                return;
            }
        }

        // COMBAT_NOPETDESERT (Source-X OnHarmedBy, CCharFight.cpp:312):
        // attacking your own pet makes it desert you — unless the flag allows
        // friendly fire. @PetDesert may still RETURN 1 to keep it serving.
        if (!target.IsPlayer && target.OwnerSerial == _character.Uid &&
            !CombatHelper.IsCombatFlagSet(CombatFlags.NoPetDesert))
        {
            if (Character.OnPetDesert == null || !Character.OnPetDesert(target, _character))
            {
                SysMessage(ServerMessages.GetFormatted("pet_gone_wild", target.Name));
                target.ClearOwnership(clearFriends: false);
            }
        }

        // Repeated 0x05 packets for the active target are acknowledgements,
        // not new attacks. Pet-desert is checked above because the combat flag
        // may be changed at runtime; crime and start triggers must not repeat.
        if (_character.IsInWarMode && _character.FightTarget == target.Uid)
        {
            Send(new PacketAttackResponse(target.Uid.Value));
            return;
        }

        // Region PvP enforcement
        if (target.IsPlayer && _character.IsPlayer)
        {
            var region = _world.FindRegion(_character.Position);
            if (region != null && region.IsFlag(Core.Enums.RegionFlag.NoPvP))
            {
                SysMessage(ServerMessages.Get("combat_nopvp"));
                Send(new PacketAttackResponse(0));
                return;
            }
            // Source-X CCharFight: attacking is a crime only when the target is
            // NOTO_GOOD (innocent blue) FROM THE ATTACKER'S OWN VIEW. GetNotoriety
            // folds in the attacker's personal grey — a target they hold SawCrime
            // or HarmedBy of (one who struck first, or whose crime they witnessed)
            // is no longer innocent to them, so retaliating is self-defence, not a
            // crime. A globally criminal/murderer/guild/party target is likewise
            // not NOTO_GOOD. The aggressor↔victim IAggressor/HarmedBy memory is
            // stamped by Memory_Fight_Start below regardless of region. Config
            // gate: ATTACKINGISACRIME.
            //
            // The criminal FLAG itself is region-independent (ServUO
            // Mobile.CriminalAction sets Criminal=true unconditionally; only the
            // guard RESPONSE is gated on a guarded region, handled separately). A
            // prior guarded-region gate here let a player attack an innocent in the
            // wilderness with no grey flag, diverging from Source-X and from the
            // harmful-spell path, which already flags everywhere.
            // COMBAT_ATTACK_NOAGGREIVED (old sphere, Source-X OnAttackedBy
            // CCharFight.cpp:349): skips the aggrieved bookkeeping entirely,
            // so starting a fight never marks the attacker criminal here.
            bool targetIsInnocent = GetNotoriety(target) == 1; // NOTO_GOOD
            if (Character.AttackingIsACrimeEnabled && targetIsInnocent &&
                !CombatHelper.IsCombatFlagSet(CombatFlags.AttackNoAggreived))
            {
                _character.MakeCriminal();
            }

        }

        if (!_character.IsInWarMode)
            SetWarMode(true, syncClients: true, preserveTarget: true);

        _character.FightTarget = target.Uid;
        _character.Memory_Fight_Start(target);
        target.Memory_Fight_Start(_character);
        Send(new PacketSwing(_character.Uid.Value, target.Uid.Value));
        Send(new PacketAttackResponse(target.Uid.Value));

        // Set initial swing delay so the first hit isn't instant (unless COMBATFLAGS allows it)
        if (_character.NextAttackTime == 0)
        {
            bool instantFirst = (Character.CombatFlags & (int)CombatFlags.FirstHitInstant) != 0;
            if (!instantFirst)
            {
                var w = _character.GetEquippedItem(Layer.OneHanded)
                     ?? _character.GetEquippedItem(Layer.TwoHanded);
                _character.BeginEquipSwingWait(Environment.TickCount64, GetSwingDelayMs(_character, w), noWait: false);
            }
            else
            {
                _character.BeginEquipSwingWait(Environment.TickCount64, 0, noWait: true);
            }
        }

        // Range check — only swing now if already close enough
        var atkWeapon = _character.GetEquippedItem(Layer.OneHanded)
                     ?? _character.GetEquippedItem(Layer.TwoHanded);
        int atkMaxRange = CombatHelper.GetWeaponRange(atkWeapon).Max;
        int atkDist = CombatHelper.GetChebyshevDistance(_character, target);
        if (atkDist > atkMaxRange)
            return;

        TrySwingAt(target);
    }

    /// <summary>
    /// Auto-attack tick. Called every server tick — if the player has a
    /// valid FightTarget and the swing timer has elapsed, automatically
    /// performs the next melee/ranged swing. Maps to CChar::Fight_HitTry
    /// in Source-X which runs every tick for any character with a fight
    /// target, giving continuous combat without requiring repeated 0x05
    /// packets from the client.
    /// </summary>
    public void TickCombat()
    {
        if (_character == null) return;
        if (CombatHelper.IsInvalidSwingParticipant(_character, asTarget: false))
        {
            _character.ClearPendingHit();
            return;
        }

        long now = Environment.TickCount64;
        _character.RefreshCombatSwingState(now);

        if (!_character.FightTarget.IsValid || !_character.IsInWarMode)
        {
            _character.ClearPendingHit();
            return;
        }

        // Two-phase swing: land a started swing's hit once its windup elapses,
        // before gating the next swing on recoil. Atomic swings carry no pending
        // hit (resolved inline), so this is a no-op for the default case.
        if (_character.HasPendingHit && now >= _character.SwingHitTime)
            ResolvePlayerHit(now);

        if (!_character.FightTarget.IsValid || !_character.IsInWarMode)
            return;

        if (now < _character.NextAttackTime) return;

        var target = _world.FindChar(_character.FightTarget);
        if (target == null || target.MapIndex != _character.MapIndex ||
            CombatHelper.IsInvalidSwingParticipant(target, asTarget: true))
        {
            _character.FightTarget = Serial.Invalid;
            return;
        }

        var weapon = _character.GetEquippedItem(Layer.OneHanded)
                  ?? _character.GetEquippedItem(Layer.TwoHanded);
        int maxRange = CombatHelper.GetWeaponRange(weapon).Max;
        int dist = CombatHelper.GetChebyshevDistance(_character, target);
        // COMBAT_SWING_NORANGE: a swing may start even when out of range. A
        // hooked @HitCheck also opens this fast-path gate — the script may
        // enable it per-swing via LOCAL.Recoil_NoRange (Source-X fires
        // @HitCheck before Fight_CanHit's range validation).
        if (dist > maxRange && !CombatHelper.SwingIgnoresStartRange() &&
            _triggerDispatcher?.IsCharTriggerUsed(CharTrigger.HitCheck) != true)
            return;

        TrySwingAt(target);
    }

    private void TrySwingAt(Character target)
    {
        if (_character == null) return;

        long now = Environment.TickCount64;
        _character.RefreshCombatSwingState(now);
        // A started swing whose hit hasn't landed yet (windup in flight) must not
        // be overwritten by a new swing — TickCombat pumps the pending hit.
        if (_character.HasPendingHit)
            return;
        if (now < _character.NextAttackTime)
            return;
        if (_character.CombatSwingState is SwingState.Swinging or SwingState.Equipping)
            return;

        // Source-X CChar::Fight_CanHit gates: dead / paralyzed / sleeping
        // attackers can't swing. Also a STAM<=0 char collapses (CCharAct.cpp
        // OnTick "Stat_GetVal(STAT_DEX) <= 0"), so block the swing entirely
        // and re-check next tick — don't burn the recoil timer.
        if (CombatHelper.IsInvalidSwingParticipant(_character, asTarget: false)) return;

        // Manifest ghost protection: a dead target (peace OR war manifest)
        // is never a valid combat target. Source-X CChar::Fight_IsAttackable
        // returns false on m_pPlayer && IsStatFlag(STATF_DEAD); the
        // translucent manifest is purely cosmetic and exists so plain
        // observers can SEE the ghost without being able to hit it.
        // Without this guard a manifested ghost would take damage and
        // produce a "kill the dead" loop with no corpse / no resurrect.
        if (target == null || target.MapIndex != _character.MapIndex ||
            CombatHelper.IsInvalidSwingParticipant(target, asTarget: true))
        {
            _character.FightTarget = Serial.Invalid;
            return;
        }
        if (_character.Stam <= 0)
        {
            _character.NextAttackTime = now + 500;
            return;
        }
        // COMBAT_PARALYZE_CANSWING (old-sphere): a paralyzed (Freeze) attacker
        // can keep swinging; sleeping always blocks. Without the flag both
        // freeze and sleep stop the swing.
        // A sleeping TARGET is checked here too: Source-X Fight_CanHit
        // (CCharFight.cpp:1697) answers SWINGING for STATF_SLEEPING on either side,
        // so a sleeper is not swung at from any entry point — the pending-hit path
        // enforces the same rule in CombatHelper.EvaluateHitTime.
        bool paralyzeCanSwing = CombatHelper.IsCombatFlagSet(CombatFlags.ParalyzeCanSwing);
        if ((_character.IsStatFlag(StatFlag.Freeze) && !paralyzeCanSwing) ||
            _character.IsStatFlag(StatFlag.Sleeping) ||
            target.IsStatFlag(StatFlag.Sleeping))
        {
            _character.NextAttackTime = now + 250;
            return;
        }

        // Spell casting blocks weapon swings (Source-X Skill_Magery /
        // SKTRIG_START path: the cast skill owns m_atFight while it runs).
        // We tolerate the swing finishing the *current* recoil but won't
        // start a new one mid-cast.
        if (_character.IsCasting)
        {
            _character.NextAttackTime = now + 500;
            return;
        }

        var weapon = _character.GetEquippedItem(Layer.OneHanded)
                  ?? _character.GetEquippedItem(Layer.TwoHanded);
        int swingDelayMs = GetSwingDelayMs(_character, weapon);

        // Source-X @HitCheck (Fight_Hit entry, BEFORE Fight_CanHit's range/LoS
        // validation): SRC = the victim, ARGN1 = war swing state, ARGN2 =
        // damage type, LOCAL.Recoil_NoRange = the per-swing SWING_NORANGE
        // decision — seeded from the combat flags and read back to drive the
        // range validation and windup window below. RETURN 1 forces a miss
        // (SphereNet adaptation of the Source-X swing-state return).
        bool swingNoRange = CombatHelper.SwingIgnoresStartRange();
        if (_triggerDispatcher != null)
        {
            var hitCheckLocals = new SphereNet.Scripting.Variables.VarMap();
            hitCheckLocals.SetInt("Recoil_NoRange", swingNoRange ? 1 : 0);
            var hitCheckArgs = new TriggerArgs
            {
                CharSrc = target,
                O1 = weapon,
                ItemSrc = weapon,
                N1 = (int)_character.CombatSwingState,
                N2 = (int)CombatEngine.GetWeaponDamageType(weapon),
                Locals = hitCheckLocals,
            };
            if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.HitCheck, hitCheckArgs) == TriggerResult.True)
            {
                EmitMissFeedback(target, weapon);
                _triggerDispatcher.FireCharTrigger(_character, CharTrigger.HitMiss,
                    new TriggerArgs { CharSrc = target, O1 = weapon, ItemSrc = weapon });
                _character.NextAttackTime = now + swingDelayMs;
                return;
            }
            swingNoRange = hitCheckLocals.GetInt("Recoil_NoRange") != 0;
        }

        var prep = CombatHelper.ValidateSwingPrep(
            _world, _character, target, weapon, _character.PrivLevel, now, _world.CanSeeLOS,
            ignoreRangeLos: swingNoRange);
        switch (prep.Result)
        {
            case CombatHelper.SwingPrepResult.Abort:
                if (prep.MessageKey != null)
                    SysMessage(ServerMessages.Get(prep.MessageKey));
                _character.FightTarget = Serial.Invalid;
                return;
            case CombatHelper.SwingPrepResult.RetryLater:
                if (prep.MessageKey != null)
                    SysMessage(ServerMessages.Get(prep.MessageKey));
                else if (CombatHelper.IsRangedWeapon(weapon))
                    SysMessage("You cannot see that target.");
                _character.NextAttackTime = now + Math.Max(prep.RetryMs, 250);
                return;
        }

        CombatHelper.RevealOnAttack(_character, _character.PrivLevel);

        int swingDelayTenths = Math.Max(1, swingDelayMs / 100);
        int animOverride = -1;
        int animDelayOverride = -1;
        if (_triggerDispatcher != null)
        {
            // Source-X @HitTry contract (CCharFight Init(iARGN1Var,0,0,pWeapon),
            // OnTrigger(..., pCharTarg)): SRC = the victim, ARGO = the weapon,
            // ARGN1 = swing tenths (writable), LOCAL.Anim / LOCAL.AnimDelay
            // (tenths) override the swing animation and its frame pacing.
            var hitTryLocals = new SphereNet.Scripting.Variables.VarMap();
            hitTryLocals.SetInt("Anim", -1);
            hitTryLocals.SetInt("AnimDelay", 7);
            var hitTryArgs = new TriggerArgs
            {
                CharSrc = target,
                O1 = weapon,
                ItemSrc = weapon,
                N1 = swingDelayTenths,
                Locals = hitTryLocals,
            };
            if (_triggerDispatcher.FireCharTrigger(_character, CharTrigger.HitTry, hitTryArgs) == TriggerResult.True)
            {
                _character.NextAttackTime = now + 250;
                return;
            }
            swingDelayTenths = Math.Clamp(hitTryArgs.N1, 1, short.MaxValue);
            swingDelayMs = swingDelayTenths * 100;
            animOverride = (int)hitTryLocals.GetInt("Anim", -1);
            long animDelay = hitTryLocals.GetInt("AnimDelay", 7);
            if (animDelay != 7)
                animDelayOverride = (int)Math.Clamp(animDelay, 0, 255);
        }

        // COMBAT_NODIRCHANGE: do not auto-rotate the attacker to face the target.
        if (!CombatHelper.IsCombatFlagSet(CombatFlags.NoDirChange))
            FaceTarget(target);

        (ushort BaseId, ItemType FallbackType, ushort Gfx)? ammo = null;
        if (CombatHelper.IsRangedWeapon(weapon) && !CombatHelper.IsThrowingWeapon(weapon))
        {
            ammo = ResolveAmmo(weapon!);
            if (!HasAmmoInPack(ammo.Value.BaseId, ammo.Value.FallbackType))
            {
                SysMessage(ServerMessages.Get(Msg.CombatArchNoammo));
                // Advance the swing timer even though no swing happened. Without
                // this NextAttackTime never moves, so the combat tick re-enters
                // every server tick — spamming the "no ammo" message and burning
                // CPU in a tight loop (the reported "infinite loop when out of
                // arrows"). Source-X returns WAR_SWING_INVALID, which its fight
                // loop still paces by the swing timer.
                _character.NextAttackTime = now + swingDelayMs;
                return;
            }
        }

        // Two-phase swing (Source-X windup -> hit): commit the swing now (animation
        // + recoil + a pending hit). With a zero windup the hit resolves in this
        // same call (atomic — the flagless default and PREHIT); STAYINRANGE /
        // SWING_NORANGE (or a @HitCheck LOCAL.Recoil_NoRange override) open a
        // window so the hit lands later from TickCombat's pending-hit pump.
        // `ammo` (presence) was validated just above.
        int hitDelayMs = CombatHelper.GetSwingHitDelayMs(swingDelayMs, swingNoRange);
        var committedRange = CombatHelper.GetWeaponRange(weapon);
        _character.BeginSwingWindup(now, hitDelayMs, swingDelayMs, target.Uid,
            now + swingDelayMs * 2L, weapon != null ? weapon.Uid : Serial.Invalid, swingNoRange,
            committedRange.Min, committedRange.Max);

        // @HitTry LOCAL.Anim / LOCAL.AnimDelay override the swing animation and
        // its frame pacing (Source-X reads them back after the trigger).
        ushort swingAction = animOverride >= 0
            ? (ushort)Math.Clamp(animOverride, 0, ushort.MaxValue)
            : GetSwingAction(_character, weapon);
        // COMBAT_ANIM_HIT_SMOOTH paces the swing animation to the swing time.
        byte swingAnimDelay = animDelayOverride >= 0
            ? (byte)animDelayOverride
            : CombatHelper.GetSwingAnimDelay(swingDelayMs);
        BroadcastAnimation(_character, swingAction, NewAnimationGesture.Attack,
            animDelay: swingAnimDelay);
        // Source-X plays a single combat sound per swing: the per-weapon hit
        // sound on a hit, the miss whoosh on a miss (emitted below). No extra
        // unconditional swing sound.

        if (now >= _character.SwingHitTime)
            ResolvePlayerHit(now);
    }

    /// <summary>Resolve a started swing's hit (Source-X hit phase). Re-checks
    /// reach/LoS per the combat flags (STAYINRANGE -> miss, SWING_NORANGE -> wait),
    /// consumes ammo, runs ResolveAttack and emits all hit/miss feedback. Called
    /// inline for an atomic swing, or from TickCombat once the windup elapses.</summary>
    private void ResolvePlayerHit(long now)
    {
        if (_character == null || !_character.HasPendingHit) return;

        var target = _world.FindChar(_character.PendingHitTarget);
        Serial committedWeaponUid = _character.PendingHitWeapon;
        bool weaponCaptured = _character.PendingHitWeaponCaptured;
        bool swingNoRange = _character.PendingHitSwingNoRange;
        (int Min, int Max)? committedRange =
            _character.PendingHitRangeMin >= 0 && _character.PendingHitRangeMax >= 0
                ? (_character.PendingHitRangeMin, _character.PendingHitRangeMax)
                : null;
        Item? weapon = weaponCaptured
            ? (committedWeaponUid.IsValid ? _world.FindItem(committedWeaponUid) : null)
            : _character.GetEquippedItem(Layer.OneHanded) ?? _character.GetEquippedItem(Layer.TwoHanded);
        if (weaponCaptured && committedWeaponUid.IsValid && (weapon == null || weapon.IsDeleted))
        {
            _character.ClearPendingHit();
            return;
        }

        switch (CombatHelper.EvaluateHitTime(_world, _character, target, weapon,
            _character.PrivLevel, now, _character.PendingHitDeadline, _world.CanSeeLOS,
            swingNoRange, committedRange))
        {
            case CombatHelper.HitTimeDecision.Wait:
                return; // keep the pending hit; retry on a later tick
            case CombatHelper.HitTimeDecision.Drop:
                _character.ClearPendingHit();
                return;
            case CombatHelper.HitTimeDecision.Miss:
                _character.ClearPendingHit();
                if (target != null)
                {
                    // A SPENT swing, not a failed hit roll: the target left reach or
                    // LoS, or the archer walked, during the windup. Source-X returns
                    // WAR_SWING_EQUIPPING for both (CCharFight.cpp:1896 and :1857),
                    // and both return BEFORE the pAmmo block at :1862 — so no
                    // ammunition is touched, for any ranged weapon. An arrow is spent
                    // only on a genuine miss roll (:2023 m_Act_Difficulty < 0), which
                    // reaches this handler by a different route.
                    //
                    // (A real miss roll never produces HitTimeDecision.Miss; that
                    // decision is only ever returned for an unspent swing.)
                    if (!HandleMissTriggerAndAmmo(target, weapon, null))
                    {
                        SysMessage(ServerMessages.GetFormatted(Msg.CombatMisss, target.Name));
                        EmitMissSound(weapon);
                    }
                }
                return;
        }

        _character.ClearPendingHit();
        if (target == null) return;

        // Projectile now; the ammo stack is located here but only consumed
        // after the hit/miss outcome resolves, so @HitMiss can expose its UID
        // as LOCAL.Arrow and a script may take over its fate (ArrowHandled) —
        // Source-X finds pAmmo during the swing and consumes it per-branch.
        Item? ammoStack = null;
        if (CombatHelper.IsRangedWeapon(weapon))
        {
            var ammoSpec = ResolveAmmo(weapon!);
            if (CombatHelper.IsThrowingWeapon(weapon))
            {
                // The thrown weapon IS the projectile — no pack ammo (pack
                // TDATA3 empty). TDATA4/AMMOANIM overrides the flight art,
                // else the weapon's own graphic flies; the reverse leg
                // mirrors Source-X's returning-weapon animation.
                ushort throwGfx = ammoSpec.Gfx != 0 ? ammoSpec.Gfx : weapon!.DispIdFull;
                EmitRangedProjectile(target, throwGfx);
                EmitReturningProjectile(target, throwGfx);
            }
            else
            {
                ammoStack = FindAmmoInPack(ammoSpec.BaseId, ammoSpec.FallbackType);
                if (ammoStack == null)
                {
                    SysMessage(ServerMessages.Get(Msg.CombatArchNoammo));
                    return;
                }
                EmitRangedProjectile(target, ammoSpec.Gfx);
            }
        }

        int damage = CombatEngine.ResolveAttack(
            _character,
            target,
            weapon,
            CombatHelper.ActiveCombatFlags,
            -1, -1,
            ammoStack?.Uid.Value ?? 0,
            out bool ammoHandled);

        // A landed swing — damaging or fully absorbed — spends the ammo. A
        // @Hit-chain LOCAL.ArrowHandled=1 hands the ammo to the script instead
        // (Source-X nulls pAmmo); the miss branch below handles its own
        // consumption the same way via the @HitMiss locals.
        if ((damage >= 0 || damage == CombatEngine.AttackResolvedByProc) &&
            ammoStack != null && !ammoHandled)
            ConsumeFromStack(ammoStack);

        if (damage > 0)
        {
            if (_lastCombatNotifyTarget != target.Uid.Value)
            {
                _lastCombatNotifyTarget = target.Uid.Value;
                // Reference Attacker_Add messaging: BOTH lines render over the
                // ATTACKER. "*X is attacking Y!*" goes to every observer except
                // the victim's client, in the emote hue; the victim's client
                // alone gets "*X is attacking you!*" (%s = attacker). The old
                // code formatted the "attacking you" template with the
                // VICTIM's name and broadcast it to everyone.
                const ushort emoteHue = 0x0022;
                string atkName = _character.Name ?? "";
                var emoteOthers = new PacketSpeechUnicodeOut(
                    _character.Uid.Value, _character.BodyId, 2, emoteHue, 3, "TRK",
                    atkName, ServerMessages.GetFormatted(Msg.CombatAttacko, atkName, target.Name));
                var emoteVictim = new PacketSpeechUnicodeOut(
                    _character.Uid.Value, _character.BodyId, 2, emoteHue, 3, "TRK",
                    atkName, ServerMessages.GetFormatted(Msg.CombatAttacks, atkName));
                uint victimUid = target.Uid.Value;
                ForEachClientInRange?.Invoke(_character.Position, UpdateRange, 0,
                    (obsCh, obsClient) => obsClient.Send(
                        obsCh.Uid.Value == victimUid ? emoteVictim : emoteOthers));
            }

            _spellEngine?.TryInterruptFromDamage(target, damage);
            if (target.HasActiveSkillPending())
            {
                int abortedSkill = target.ClearActiveSkillPending();
                if (abortedSkill >= 0)
                    Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
            }

            if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
            {
                target.FightTarget = _character.Uid;
                target.NextNpcActionTime = 0;
                OnWakeNpc?.Invoke(target);
            }

            // @Hit / @GetHit and the weapon/armor item triggers now fire inside
            // CombatEngine.ResolveAttack (via CombatEngine.OnHitDamage), before
            // HP is applied, so a script can modify or cancel the damage. The
            // value returned here is already the post-trigger final damage.

            _logger.LogDebug("{Attacker} hit {Target} for {Dmg} damage",
                _character.Name, target.Name, damage);

            ushort hitSound = GetWeaponHitSound(weapon);
            var hitSoundPacket = new PacketSound(hitSound, target.X, target.Y, target.Z);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, hitSoundPacket, 0);

            ushort getHitAction = target.IsMounted
                ? (ushort)AnimationType.HorseSlap
                : BodyAnimTranslator.Translate(target.BodyId, (ushort)AnimationType.GetHit);
            BroadcastAnimation(target, getHitAction, NewAnimationGesture.Impact);

            // Source-X CRESND_GETHIT: the struck target's pain vocalization
            // (human "oomf" / creature SOUNDGETHIT). Only on a damaging hit.
            ushort painSound = GetDefenderHitSound(target);
            if (painSound != 0)
                BroadcastNearby?.Invoke(target.Position, UpdateRange,
                    new PacketSound(painSound, target.X, target.Y, target.Z), 0);

            SphereNet.Game.Objects.Characters.Character.BroadcastDamageNearby?.Invoke(
                target.Position, UpdateRange, target.Uid.Value, damage, 0);

            var healthPacket = new PacketUpdateHealth(
                target.Uid.Value, target.MaxHits, target.Hits);
            BroadcastNearby?.Invoke(target.Position, UpdateRange, healthPacket, 0);

            GameClient.EmitBloodSplat(_world, target);

            // Source-X hit economy: 40% of arrows that strike an NPC stick in
            // the body — they ride in its pack and surface on the corpse loot.
            // Skipped when a @Hit script took the ammo over (ArrowHandled).
            if (CombatHelper.IsRangedWeapon(weapon) && !CombatHelper.IsThrowingWeapon(weapon) &&
                !target.IsPlayer && !ammoHandled &&
                target.Backpack != null && Random.Shared.Next(100) < 40)
            {
                var stuckAmmo = ResolveAmmo(weapon!);
                var stuck = _world.CreateItem();
                if (stuckAmmo.BaseId != 0)
                    stuck.BaseId = stuckAmmo.BaseId;
                else
                {
                    stuck.ItemType = stuckAmmo.FallbackType;
                    stuck.BaseId = stuckAmmo.FallbackType == ItemType.WeaponArrow ? (ushort)0x0F3F : (ushort)0x1BFB;
                }
                stuck.Amount = 1;
                var actual = target.Backpack.AddItemWithStack(stuck);
                if (actual != stuck || stuck.ContainedIn != target.Backpack.Uid)
                    _world.RemoveItem(stuck);
            }

            if (target.Hits <= 0 && !target.IsDead && _deathEngine != null)
            {
                var targetPos = target.Position;
                byte targetDir = (byte)((byte)target.Direction & 0x07);
                // @Kill/@Death fire inside ProcessDeath; @Death RETURN 1 can cancel
                // the death (corpse == null + a still-living target), in which case
                // the target keeps fighting and is not removed from view.
                var corpse = _deathEngine.ProcessDeath(target, _character);
                if (target.IsDead)
                {
                    View.KnownChars.Remove(target.Uid.Value);
                    _character.FightTarget = Serial.Invalid;
                }

                if (corpse != null)
                {
                    uint corpseWireSerial = corpse.Uid.Value;
                    if (corpse.Amount > 1)
                        corpseWireSerial |= 0x80000000u;

                    if (target.IsPlayer)
                    {
                        MarkCorpseKnown(corpse);
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            targetDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpsePacket, 0);

                        // Player corpse: send contents + equip map for paperdoll corpse rendering.
                        // 0x25 add-to-container is 21 bytes for >=6.0.1.7 clients (extra grid-index
                        // byte) and 20 for older ones. A single shared broadcast buffer would desync
                        // whichever era it does not match, so dispatch a per-recipient variant sized
                        // to each observer's version instead of one shared packet.
                        ForEachClientInRange?.Invoke(targetPos, UpdateRange, 0, (_, observerClient) =>
                        {
                            bool grid = observerClient.NetState.IsClientPost6017;
                            foreach (var corpseItem in corpse.Contents)
                            {
                                observerClient.Send(new PacketContainerItem(
                                    corpseItem.Uid.Value, corpseItem.DispIdFull, 0,
                                    corpseItem.Amount, corpseItem.X, corpseItem.Y,
                                    corpse.Uid.Value, corpseItem.Hue, useGridIndex: grid));
                            }
                        });

                        var corpseEquipEntries = new List<(byte Layer, uint ItemSerial)>();
                        var usedLayers = new HashSet<byte>();
                        foreach (var item in corpse.Contents)
                        {
                            byte layer = (byte)item.EquipLayer;
                            if (layer == (byte)Layer.None || layer == (byte)Layer.Face || layer == (byte)Layer.Pack)
                                continue;
                            if (!usedLayers.Add(layer))
                                continue;
                            corpseEquipEntries.Add((layer, item.Uid.Value));
                        }

                        var corpseEquip = new PacketCorpseEquipment(corpse.Uid.Value, corpseEquipEntries);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpseEquip, 0);

                        // NOTE: 0xAF DeathAnimation is NOT broadcast here.
                        // OnCharacterDeath below runs a per-observer dispatch
                        // that sends 0xAF to plain players (which remaps the
                        // mobile in ClassicUO so it disappears) and a
                        // 0x1D + 0x78 ghost mobile pair to staff observers
                        // (which avoids the 0xAF serial-remap so staff can
                        // see the ghost without the duplicate-mobile bug
                        // documented in the death plan). A blanket
                        // BroadcastNearby would defeat that distinction.
                    }
                    else
                    {
                        // NPC corpse — matches both Source-X (PacketDeath +
                        // RemoveFromView) and ServUO (DeathAnimation + Delete
                        // -> RemovePacket) reference flow:
                        //   1) 0x1A WorldItem  (corpse appears in world)
                        //   2) 0xAF DeathAnim  (mobile -> corpse transition)
                        //   3) 0x1D DeleteObj  (remove the dead mobile)
                        // Source-X CObjBase::DeletePrepare() calls
                        // RemoveFromView() which broadcasts 0x1D to all in
                        // range, and ServUO's Mobile.Kill() / OnDeath() chain
                        // ends with NPC.Delete() which sends 0x1D as well.
                        // Without 0x1D the dead mobile lingers in client
                        // collections (ClassicUO's 0xAF only re-keys the
                        // mobile under 0x80000000|serial; the visual entity
                        // is still there until the client receives 0x1D).
                        //
                        // 0x89/0x3C (CorpseEquipment/ContainerContent) are
                        // only sent for human-body corpses; sending them for
                        // monster corpses corrupts the client's input state.
                        MarkCorpseKnown(corpse);
                        var corpsePacket = new PacketWorldItem(
                            corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                            corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                            targetDir);
                        BroadcastNearby?.Invoke(targetPos, UpdateRange, corpsePacket, 0);

                        // A bonded pet is kept alive server-side as a ghost
                        // (ProcessDeath does not delete it). Skip the 0xAF death
                        // animation + 0x1D delete so ghost-capable observers
                        // (staff / dead / SpiritSpeak) don't flicker — the
                        // view-delta filters the dead pet from plain players and
                        // re-draws it on resurrection.
                        if (!target.IsBonded)
                        {
                            var dirToKiller = target.Position.GetDirectionTo(_character.Position);
                            uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                            var deathAnim = new PacketDeathAnimation(target.Uid.Value, corpse.Uid.Value, npcFallDir);
                            BroadcastNearby?.Invoke(targetPos, UpdateRange, deathAnim, 0);

                            var removeMobile = new PacketDeleteObject(target.Uid.Value);
                            BroadcastNearby?.Invoke(targetPos, UpdateRange, removeMobile, 0);
                        }
                    }
                }

                // PvP: notify the dying player's own client so it transitions
                // to ghost (body+hue swap, 0x77 broadcast, 0x20 self, 0x2C
                // death status). Without this the killer sees the corpse but
                // the victim's screen freezes with a still-alive paperdoll.
                if (corpse != null && target.IsPlayer && OnCharacterDeathOfOther != null)
                    OnCharacterDeathOfOther.Invoke(target);
            }

            // Reactive armor may have killed the attacker
            if (_character.Hits <= 0 && !_character.IsDead && _deathEngine != null)
            {
                // @Kill/@Death fire inside ProcessDeath; @Death RETURN 1 cancels it.
                _deathEngine.ProcessDeath(_character, target);
                if (_character.IsDead)
                    OnCharacterDeath();
            }
        }
        else if (damage == 0)
        {
            // Source-X: a connecting hit that armor fully absorbed still lands.
            // Play the weapon hit sound and the target's get-hit animation, but
            // no pain vocalization, damage number or blood (no damage was dealt).
            BroadcastNearby?.Invoke(target.Position, UpdateRange,
                new PacketSound(GetWeaponHitSound(weapon), target.X, target.Y, target.Z), 0);

            ushort absorbGetHit = target.IsMounted
                ? (ushort)AnimationType.HorseSlap
                : BodyAnimTranslator.Translate(target.BodyId, (ushort)AnimationType.GetHit);
            BroadcastAnimation(target, absorbGetHit, NewAnimationGesture.Impact);
        }
        else if (damage == CombatEngine.AttackMiss)
        {
            // Source-X @HitMiss: SRC = the victim, ARGO = the weapon.
            // LOCAL.Arrow = the UID of the pack ammo stack the shot came from
            // (CCharFight.cpp:2032). LOCAL.ArrowHandled=1 — or RETURN 1 —
            // hands the ammo's fate to the script: nothing is consumed or
            // dropped by the engine then.
            if (!HandleMissTriggerAndAmmo(target, weapon, ammoStack))
            {
                SysMessage(ServerMessages.GetFormatted(Msg.CombatMisss, target.Name));
                EmitMissSound(weapon);
            }
        }
    }

    /// <summary>Shared @HitMiss + ranged ammo economy for both a natural miss
    /// and STAYINRANGE's hit-time miss.</summary>
    private bool HandleMissTriggerAndAmmo(Character target, Item? weapon, Item? ammoStack)
    {
        if (_character == null) return false;

        bool rangedMiss = CombatHelper.IsRangedWeapon(weapon);
        var missLocals = new SphereNet.Scripting.Variables.VarMap();
        if (rangedMiss && ammoStack != null)
            missLocals.SetInt("Arrow", ammoStack.Uid.Value);
        var missResult = _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.HitMiss,
            new TriggerArgs { CharSrc = target, O1 = weapon, ItemSrc = weapon, Locals = missLocals })
            ?? TriggerResult.Default;

        if (missResult == TriggerResult.True)
        {
            _character.BeginEquipSwingWait(Environment.TickCount64, 0, noWait: true);
            return true;
        }

        if (!rangedMiss || ammoStack == null ||
            missLocals.GetInt("ArrowHandled") != 0)
            return false;

        ConsumeFromStack(ammoStack);
        if (Random.Shared.Next(100) < 40)
        {
            var missAmmo = ResolveAmmo(weapon!);
            DropRecoveredAmmo(missAmmo.BaseId, missAmmo.FallbackType, target.Position);
        }
        return false;
    }

    /// <summary>Materialise one recovered ammo item (Source-X MoveToDecay of the
    /// split arrow): dropped on the ground at <paramref name="pos"/> with the
    /// normal decay window.</summary>
    private void DropRecoveredAmmo(ushort baseId, ItemType fallbackType, Point3D pos)
    {
        var ammo = _world.CreateItem();
        if (baseId != 0)
            ammo.BaseId = baseId;
        else
        {
            ammo.ItemType = fallbackType;
            ammo.BaseId = fallbackType == ItemType.WeaponArrow ? (ushort)0x0F3F : (ushort)0x1BFB;
        }
        ammo.Amount = 1;
        _world.PlaceItemWithDecay(ammo, pos);
    }

    private void EmitMissFeedback(Character target, Item? weapon)
    {
        if (_character == null) return;

        ushort missAction = GetSwingAction(_character, weapon);
        BroadcastAnimation(_character, missAction, NewAnimationGesture.Attack);

        EmitMissSound(weapon);
    }

    private void EmitMissSound(Item? weapon)
    {
        if (_character == null) return;

        // Source-X plays a single per-weapon miss whoosh from the attacker.
        var missSound = new PacketSound(GetWeaponMissSound(weapon), _character.X, _character.Y, _character.Z);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, missSound, 0);
    }

    // Resolve which ammo a ranged weapon fires and the projectile graphic.
    // ITEMDEF AMMOTYPE names the exact ammo item to consume (resolved to a
    // baseid) and AMMOANIM overrides the in-flight graphic; absent either, the
    // legacy arrow-for-bows / bolt-for-crossbows defaults apply.
    private (ushort BaseId, ItemType FallbackType, ushort Gfx) ResolveAmmo(Item weapon) =>
        CombatHelper.ResolveAmmoSpec(
            SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(weapon.BaseId),
            weapon.ItemType,
            Item.ResolveDefName);

    // Ammo presence/consumption keyed by a specific baseid when AMMOTYPE
    // resolved one, otherwise by the legacy ammo ItemType.
    private bool HasAmmoInPack(ushort baseId, ItemType fallbackType)
        => CombatHelper.FindAmmoInContainer(_character?.Backpack, baseId, fallbackType) != null;

    /// <summary>The pack ammo stack a shot draws from (Source-X pAmmo) — found
    /// at the shot, consumed only once the hit/miss outcome resolves so the
    /// @HitMiss LOCAL.Arrow contract can expose (and a script can take over)
    /// the live item.</summary>
    private Item? FindAmmoInPack(ushort baseId, ItemType fallbackType)
        => CombatHelper.FindAmmoInContainer(_character?.Backpack, baseId, fallbackType);

    private void ConsumeFromStack(Item stack)
    {
        if (stack.Amount <= 1) _world.RemoveItem(stack);
        else stack.Amount = (ushort)(stack.Amount - 1);
    }

    /// <summary>The throwing return leg (Source-X client returning-weapon
    /// anim): the same art flies back from the target to the thrower.</summary>
    private void EmitReturningProjectile(Character target, ushort effectId)
    {
        if (_character == null) return;

        var projectile = new PacketEffect(
            type: 0,
            srcSerial: target.Uid.Value,
            dstSerial: _character.Uid.Value,
            effectId: effectId,
            srcX: target.X,
            srcY: target.Y,
            srcZ: target.Z,
            dstX: _character.X,
            dstY: _character.Y,
            dstZ: _character.Z,
            speed: 18,
            duration: 1,
            fixedDir: false,
            explode: false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, projectile, 0);
    }

    private void EmitRangedProjectile(Character target, ushort effectId)
    {
        if (_character == null) return;

        var projectile = new PacketEffect(
            type: 0,
            srcSerial: _character.Uid.Value,
            dstSerial: target.Uid.Value,
            effectId: effectId,
            srcX: _character.X,
            srcY: _character.Y,
            srcZ: _character.Z,
            dstX: target.X,
            dstY: target.Y,
            dstZ: target.Z,
            speed: 18,
            duration: 1,
            // Source-X writeBasicEffect sets oneDirection=false for EFFECT_BOLT
            // so the arrow graphic rotates to face its flight path.
            fixedDir: false,
            explode: false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, projectile, 0);
    }

    /// <summary>
    /// Handle player death — body/hue ghost transition and death sound,
    /// per-observer dispatch (plain players get 0xAF, staff get 0x78 + 0x77
    /// ghost updates), self ghost render (0x20 + 0x77 and optional 0x2C),
    /// and view-cache invalidation. Corpse + corpse equipment are already
    /// broadcast by the kill site (TrySwingAt PvP path or Program.OnNpcKill).
    /// </summary>
    public void OnCharacterDeath()
    {
        if (_character == null) return;

        if (_character.IsMounted && _mountEngine != null)
        {
            var mountNpc = DismountCharacter();
            if (mountNpc != null)
            {
                mountNpc.ClearStatFlag(Core.Enums.StatFlag.Ridden);
                BroadcastCharacterAppear?.Invoke(mountNpc);
            }
        }

        // ---------------------------------------------------------------
        //   Source-X CChar::Death (CCharAct.cpp) reference order:
        //     1) MakeCorpse + UpdateCanSee(PacketDeath)   ← caller did this
        //     2) SetID(ghost) + SetHue(HUE_DEFAULT)       ← below
        //     3) addPlayerWarMode(off) + addTargCancel    ← below
        //     4) Per-observer dispatch (UpdateCanSee)     ← below
        //     5) PacketDeathMenu(Dead) on own client      ← below
        //
        //   Hue note: 0x4001 (HUE_TRANSLUCENT|1) makes the sprite
        //   see-through, NOT grey. ClassicUO renders the ghost body
        //   (0x192/0x193) as a proper grey shroud when hue == 0
        //   (HUE_DEFAULT). The "transparent ghost" bug from the early
        //   death logs was caused by sending 0x4001 here.
        // ---------------------------------------------------------------

        ushort deathSkinHue = _character.Hue.Value;
        _spellEngine?.ClearAllEffectsOnDeath(_character);
        if (deathSkinHue == 0)
            deathSkinHue = _character.Hue.Value;

        // Capture gender BEFORE the ghost-body swap — the death cry is gender-specific
        // and IsFemale reads the live body, which is about to become a ghost body.
        bool deathSoundFemale = _character.IsFemale;

        ushort ghostBody = _character.BodyId == 0x0191 ? (ushort)0x0193 : (ushort)0x0192;
        _character.BodyId = ghostBody;
        _character.OSkin = deathSkinHue;
        _character.Hue = Core.Types.Color.Default;

        // Source-X CChar::Death equips a death shroud on the ghost. The real
        // robe (if any) already dropped to the corpse in ProcessDeath, so the
        // Robe layer is free. Equipped HERE — before the per-observer
        // BuildEquipmentList(ghostEquipment) below — so the shroud rides the
        // ghost appearance broadcast (staff observers) without a separate
        // packet, and the player keeps a robe through resurrection.
        _deathEngine?.EquipDeathShroud(_character);

        // pClient->addPlayerWarMode(off). We only need the local
        // state flip + the 0x72 PacketWarMode echo to the dying
        // client — the per-observer dispatch below carries the
        // post-death flags (War=off implicit, Female bit derived
        // from ghost body) through its 0x78 PacketDrawObject. A
        // syncClients=true here would inject an early 0x77 to staff
        // observers that mutates their cached mobile (Hue/Flags
        // updated, Graphic NOT updated) — that intermediate state
        // can leave ClassicUO's animation atlas pointing at the
        // alive body even after the follow-up 0x1D + 0x78. So we
        // suppress the broadcast and rely on per-observer dispatch.
        if (_character.IsInWarMode)
            SetWarMode(false, syncClients: false, preserveTarget: false);
        // Source-X CChar::Death: the fresh ghost starts insubstantial
        // (invisible to the living) until it manifests via the war toggle.
        _character.SetStatFlag(Core.Enums.StatFlag.Insubstantial);
        // The 0x72 echo is mandatory regardless — ClassicUO's input
        // handler latches on it to release the war-mode toggle and
        // unblock the death menu.
        _netState.Send(new PacketWarModeResponse(false));

        // pClient->addTargCancel. CRITICAL: PacketTarget(0,0) with
        // flags=0 (Neutral) does NOT cancel in ClassicUO — it OPENS
        // a brand-new target cursor (TargetManager.SetTargeting:165:
        // `IsTargeting = cursorType < TargetType.Cancel;`). We use
        // flags=3 (Cancel). The Targets.CursorActive guard avoids a
        // spurious 0x6C when no cursor was open — that flash was the
        // "ölen karakterde target çÄ±kÄ±yor" symptom.
        if (Targets.CursorActive)
        {
            _netState.Send(new PacketTarget(0x00, 0x00000000, flags: 3));
            ClearPendingTargetState();
        }

        // Source-X only plays the character's death sound here. It does not
        // create a persistent 0x3735 particle at the corpse position.

        ushort deathSoundId = (ushort)SphereNet.Game.Death.DeathEngine.GetHumanDeathSound(deathSoundFemale, Random.Shared);
        var deathSound = new PacketSound(deathSoundId, _character.X, _character.Y, _character.Z);
        _netState.Send(deathSound);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, deathSound, _character.Uid.Value);

        // ---------------------------------------------------------------
        //   Per-observer dispatch (mirror of CChar::UpdateCanSee with the
        //   ghost-visibility filter). ClassicUO's 0xAF DisplayDeath remaps
        //   the dying mobile to (serial | 0x80000000) and removes the
        //   original key from world.Mobiles (PacketHandlers.cs:3711). So:
        //
        //     - PLAIN observer  → 0xAF (mobile vanishes via remap, only
        //                         the corpse + death anim remain visible)
        //                       + server-side cache cleanup (no 0x1D
        //                         needed; the slot is already empty).
        //     - STAFF observer  → 0x78 ghost draw + 0x77 moving update
        //                         under the original serial. No 0xAF is
        //                         sent, so there is no serial remap collision.
        //                       + cache marked as ghost so the next view-
        //                         delta tick sees no body change.
        //     - SELF (handled below the loop, not inside) → 0x20 + 0x77,
        //       followed by 0x2C only when PACKETDEATHANIMATION is enabled.
        //
        //   This is the correct mapping for "staff sees ghosts, plain
        //   players don't" (the user's confirmed visibility rule) without
        //   triggering the duplicate-mobile bug that the 0xAF + 0x78
        //   combo produced on the same observer.
        // ---------------------------------------------------------------
        byte ghostFlags = BuildMobileFlags(_character);
        byte ghostNoto = GetNotoriety(_character);
        byte ghostDir = (byte)_character.Direction;
        short cx = _character.X, cy = _character.Y;
        sbyte cz = _character.Z;
        uint victimUid = _character.Uid.Value;

        // Find the corpse the kill site just created so we can wire the
        // 0xAF DeathAnimation correctly (plain observers need the
        // corpse serial to anchor the falling-body animation). One tile
        // search covers the corpse — DeathEngine.PlaceItem positions it
        // exactly at the victim's tile.
        uint corpseSerial = 0;
        if (_world != null)
        {
            foreach (var item in _world.GetItemsInRange(_character.Position, 0))
            {
                if (item.ItemType != ItemType.Corpse) continue;
                if (!item.TryGetTag("OWNER_UID", out string? ownerStr)) continue;
                if (!uint.TryParse(ownerStr, out uint ownerUid)) continue;
                if (ownerUid != victimUid) continue;
                corpseSerial = item.Uid.Value;
                break;
            }
        }

        // Source-X g_Rand.GetValFast(2) — 0/1 forward/backward fall.
        uint fallDir = (uint)Random.Shared.Next(2);
        var deathAnim = new PacketDeathAnimation(victimUid, corpseSerial, fallDir);

        var ghostEquipment = BuildEquipmentList(_character);

        // Follow-up 0x77 — even though ClassicUO's 0x78 path already
        // calls CheckGraphicChange() when GetOrCreateMobile spawns a
        // fresh entity (mobile.Graphic == 0 branch), some 4.x builds
        // skip the animation-atlas reset on the freshly-spawned ghost.
        // A redundant 0x77 with the same body re-runs CheckGraphicChange
        // against the now-current 0x192/0x193 graphic and forces the
        // animation cache to drop whatever leftover frames the alive
        // body left behind. Cheap to send, fully eliminates the
        // "staff still sees alive sprite" symptom.
        var ghostMovingBroadcast = new PacketMobileMoving(
            victimUid, ghostBody,
            cx, cy, cz, ghostDir,
            _character.Hue, ghostFlags, ghostNoto);

        ForEachClientInRange?.Invoke(_character.Position, UpdateRange, victimUid,
            (observerCh, observerClient) =>
            {
                bool canSeeGhost = observerCh.AllShow ||
                    observerCh.PrivLevel >= Core.Enums.PrivLevel.Counsel;
                if (canSeeGhost)
                {
                    // Staff path: send 0x78 ghost draw directly on the
                    // existing mobile serial. ClassicUO's DrawObject
                    // handler calls GetOrCreateMobile which returns the
                    // existing entity, updates Graphic to 0x192/0x193,
                    // and runs CheckGraphicChange() to reload the
                    // animation atlas. No 0x1D needed — sending
                    // DeleteObject first destroys the client-side mobile
                    // and the follow-up 0x78 recreates it, but some
                    // ClassicUO builds don't fully reset the animation
                    // cache on delete+recreate, leaving the alive sprite
                    // visible despite the ghost body being set.
                    observerClient.Send(new PacketDrawObject(
                        victimUid, ghostBody,
                        cx, cy, cz, ghostDir,
                        _character.Hue, ghostFlags, ghostNoto,
                        ghostEquipment, observerClient.NetState.SupportsNewMobileIncoming));
                    observerClient.Send(ghostMovingBroadcast);
                    observerClient.UpdateKnownCharRender(victimUid, ghostBody, _character.Hue,
                        ghostDir, cx, cy, cz);
                }
                else
                {
                    observerClient.Send(deathAnim);
                    observerClient.RemoveKnownChar(victimUid, sendDelete: false);
                }
            });

        // ---------------------------------------------------------------
        //   Self updates — make the ghost form actually render on the
        //   dying player's own screen.
        //
        //   ClassicUO graphic-update reality (verified against
        //   PacketHandlers.cs in 4.x):
        //
        //   * 0x77 (UpdateCharacter) — for self does NOT touch
        //     world.Player.Graphic in older builds; only NotorietyFlag
        //     and (sometimes) flags get applied. CheckGraphicChange is
        //     called against the OLD graphic, leaving the male/human
        //     state in place.
        //
        //   * 0x78 (UpdateObject) — for an existing (non-zero-graphic)
        //     mobile, the body update path is gated by
        //     `mobile.Graphic == 0`, i.e. only fresh spawns get a real
        //     graphic switch. For self the existing mobile always has
        //     the alive body cached, so the ghost graphic NEVER lands.
        //
        //   * 0x20 (UpdatePlayer) — the ONLY canonical path that sets
        //     world.Player.Graphic = newGraphic and follows it with a
        //     CheckGraphicChange + animation-atlas reset. This must be
        //     the first body-bearing packet sent to the dying player.
        //
        //   * 0x88 (OpenPaperdoll) — forces the paperdoll gump to
        //     re-render against the now-updated body so the dying
        //     player sees the grey ghost on the paperdoll too.
        //
        //   * 0x2C (DeathScreen) — opens the death menu UI; the client
        //     echoes RequestWarMode(false) in response. Keep this as the
        //     last normal-tier packet so the short ClassicUO timer starts
        //     after the redraw/status/message chatter has been queued.
        //
        //   Send order is therefore 0x20 → 0x77 (CheckGraphicChange
        //   re-trigger, harmless if already correct) → status/message →
        //   0x2C. The previous order (0x77 → 0x20 → 0x78) left the
        //   ghost graphic stuck on the dying client because 0x78 self
        //   was a no-op and 0x77 was racing 0x20.
        // ---------------------------------------------------------------
        var drawPacket = new PacketDrawPlayer(
            victimUid, ghostBody, _character.Hue,
            ghostFlags, cx, cy, cz, ghostDir);
        _netState.Send(drawPacket);

        var ghostMoving = new PacketMobileMoving(
            victimUid, ghostBody,
            cx, cy, cz, ghostDir,
            _character.Hue, ghostFlags, ghostNoto);
        _netState.Send(ghostMoving);

        _netState.Send(new PacketSeason((byte)SeasonType.Desolation, playSound: true));
        _netState.Send(new PacketGlobalLight(0));
        if (corpseSerial != 0 && _netState.SupportsMapWaypoints)
        {
            var corpse = _world?.FindItem(new Serial(corpseSerial));
            if (corpse != null)
            {
                _netState.Send(new PacketWaypointAdd(corpseSerial, corpse.X, corpse.Y, corpse.Z,
                    corpse.MapIndex, type: 1, corpse.GetName()));
            }
        }

        SendCharacterStatus(_character);
        SysMessage(ServerMessages.Get("combat_dead"));
        // sphere.ini PACKETDEATHANIMATION (Source-X m_iPacketDeathAnimation):
        // 0x2C makes ClassicUO suspend world rendering for 1.5s (its death
        // screen). When disabled, the ghost redraw above already carries the
        // full transition — Source-X's addPlayerUpdate() fallback.
        if (Character.PacketDeathAnimationEnabled)
            _netState.Send(new PacketDeathStatus(PacketDeathStatus.ActionDead));
    }

    /// <summary>
    /// Handle resurrection — body restore (ghost → human), Source-X
    /// "Resurrect with Corpse" auto re-equip, self redraw (0x77 + 0x20
    /// + 0x78 self), per-observer dispatch (single 0x78 fresh draw,
    /// works for both plain — never had the ghost — and staff — had
    /// the ghost mobile, 0x78 overwrites it), and view-cache resync so
    /// the next BuildViewDelta tick sees the new living body.
    /// </summary>
    /// <summary>The character's own corpse within rejoin range, if any (for the
    /// @Resurrect trigger's ARGO). Matches by owner UUID, then UID.</summary>
    private Item? FindOwnCorpseNear(Character ch)
    {
        foreach (var it in _world.GetItemsInRange(ch.Position, 2))
        {
            if (it.ItemType != ItemType.Corpse) continue;
            if ((it.TryGetTag("OWNER_UUID", out string? u) && Guid.TryParse(u, out var g) && g == ch.Uuid) ||
                (it.TryGetTag("OWNER_UID", out string? o) && uint.TryParse(o, out uint ou) && ou == ch.Uid.Value))
                return it;
        }
        return null;
    }

    public void OnResurrect()
    {
        if (_character == null || !_character.IsDead) return;

        Item? ownCorpse = FindOwnCorpseNear(_character);

        // @Resurrect — Source-X passes the character's own corpse (ARGO) and the
        // post-rez hit% (ARGN1, default 50 = Resurrect()'s MaxHits/2). RETURN 1
        // blocks the resurrection; otherwise a script may override the hit% via
        // ARGN1 (read back through the char-trigger arg copy-back).
        int rezHitPct = 50;
        if (_triggerDispatcher != null)
        {
            var rezArgs = new TriggerArgs
            {
                CharSrc = _character,
                O1 = ownCorpse,
                N1 = rezHitPct,
            };
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.Resurrect, rezArgs);
            if (result == TriggerResult.True)
                return;
            rezHitPct = rezArgs.N1;
        }

        _character.Resurrect();
        if (rezHitPct > 0 && _character.MaxHits > 0)
            _character.Hits = (short)Math.Clamp(_character.MaxHits * rezHitPct / 100, 1, _character.MaxHits);

        ushort restoredBody = _character.BodyId switch
        {
            0x0193 => (ushort)0x0191,
            0x0192 => (ushort)0x0190,
            _      => _spellEngine?.GetResurrectBody(_character) ?? _character.BodyId,
        };
        _character.BodyId = restoredBody;
        if (_character.OBody != 0 && _character.BodyId == _character.OBody)
            _character.OBody = 0;
        _character.ClearStatFlag(StatFlag.Polymorph);
        _character.Hue = _character.OSkin != 0
            ? new Core.Types.Color(_character.OSkin)
            : Core.Types.Color.Default;
        _character.OSkin = 0;

        // === Source-X "Resurrect with Corpse" — auto re-equip ===
        // If the resurrected character is standing on (or one tile of)
        // their own corpse, every item that was equipped at death goes
        // back to its original slot via the EQUIPLAYER tag, the rest
        // returns to the backpack, and the (now-empty) corpse is
        // deleted. Returns true iff the corpse was found — used only
        // for the SysMessage and for deciding whether to broadcast the
        // 0x1D corpse-delete (the corpse's own decay path will already
        // emit it, but we want it gone NOW so the resurrected player
        // isn't standing on a "ghost" corpse on every observer's
        // screen).
        // Strip the death shroud first so a robe restored from the corpse can
        // reclaim the Robe layer (RestoreFromCorpse skips occupied layers).
        _deathEngine?.RemoveDeathShroud(_character);

        bool corpseRestored = _deathEngine?.RestoreFromCorpse(_character) ?? false;

        // Source-X Spell_Resurrection hands out a robe when no body covering came
        // back (no corpse rejoined, or the corpse held no robe) so the player
        // isn't resurrected naked. Done before BuildEquipmentList below so the
        // robe rides the resurrection appearance broadcast.
        _deathEngine?.EnsureResurrectionRobe(_character);

        byte resFlags = BuildMobileFlags(_character);
        byte resNoto = GetNotoriety(_character);
        byte resDir = (byte)_character.Direction;
        short cx = _character.X, cy = _character.Y;
        sbyte cz = _character.Z;
        uint uid = _character.Uid.Value;
        var resEquipment = BuildEquipmentList(_character);

        // === Self redraw ===
        // Symmetrical to OnCharacterDeath: 0x20 is the ONLY packet
        // that actually swaps world.Player.Graphic in ClassicUO, so
        // it MUST go first. 0x77 then triggers a redundant
        // CheckGraphicChange (cheap insurance for older builds), 0x78
        // delivers the restored equipment list, and SendPaperdoll
        // forces the gump to re-render against the new (alive) body.
        _netState.Send(new PacketDrawPlayer(
            uid, restoredBody, _character.Hue,
            resFlags, cx, cy, cz, resDir));

        var resMoving = new PacketMobileMoving(
            uid, restoredBody,
            cx, cy, cz, resDir,
            _character.Hue, resFlags, resNoto);
        _netState.Send(resMoving);

        _netState.Send(new PacketDrawObject(
            uid, restoredBody,
            cx, cy, cz, resDir,
            _character.Hue, resFlags, resNoto,
            resEquipment, _netState.SupportsNewMobileIncoming));

        _netState.Send(new PacketSeason((byte)_world.CurrentSeason, playSound: true));
        _netState.Send(new PacketGlobalLight(_world.GetLightLevel(_character.Position)));
        if (corpseRestored && ownCorpse != null && _netState.SupportsMapWaypoints)
            _netState.Send(new PacketWaypointRemove(ownCorpse.Uid.Value));

        // === Per-observer dispatch ===
        // Plain observer: never saw the ghost (filter dropped it during
        // BuildViewDelta) → 0x78 spawns a brand-new living mobile under
        // the original serial.
        // Staff observer: had the ghost mobile in their world.Mobiles
        // (we sent 0x78 + 0x77 ghost updates during death and never sent 0xAF
        // so no remap happened) → 0x78 overwrites the body+equipment
        // in-place via UpdateGameObject. Same packet, same outcome,
        // single dispatch path.
        // Either way we update the cache so the next view-delta tick
        // doesn't see a stale ghost-body entry and re-emit a duplicate.
        ForEachClientInRange?.Invoke(_character.Position, UpdateRange, uid,
            (observerCh, observerClient) =>
            {
                observerClient.Send(new PacketDrawObject(
                    uid, restoredBody,
                    cx, cy, cz, resDir,
                    _character.Hue, resFlags, resNoto,
                    resEquipment, observerClient.NetState.SupportsNewMobileIncoming));
                observerClient.UpdateKnownCharRender(uid, restoredBody, _character.Hue,
                    resDir, cx, cy, cz);
            });

        // === Resurrect-with-Corpse: client-side state sync ===
        // RestoreFromCorpse mutated the data layer (Equip + AddItem) but
        // did NOT push any wire updates. Without the broadcasts below,
        // ClassicUO observers don't know that backpack/armor came back —
        // the killer would still see a "naked" resurrected mobile, and
        // the resurrected player would see an empty backpack until they
        // close+reopen it (which forces the 0x3C ContainerContent
        // refresh). Source-X CChar::ContentAdd issues the same packet
        // pair: addObject (0x2E PacketWornItem) for layered gear,
        // addContents (0x25 PacketContainerItem) for backpack/loose
        // contents.
        if (corpseRestored)
        {
            // 1) Broadcast every equipped item (skip layers that wouldn't
            //    appear on a paperdoll: None / Face / Pack — Pack itself
            //    rides on the 0x78 above, its CONTENTS need 0x25 below).
            for (int layerIdx = 1; layerIdx <= (int)Layer.Horse; layerIdx++)
            {
                var layer = (Layer)layerIdx;
                if (layer == Layer.Pack || layer == Layer.Face) continue;
                var equip = _character.GetEquippedItem(layer);
                if (equip == null) continue;

                var wornPacket = new PacketWornItem(
                    equip.Uid.Value, equip.DispIdFull, (byte)layer,
                    uid, equip.Hue);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, wornPacket, 0);
            }

            // 2) Stream the backpack contents back to the resurrecting
            //    player. We push 0x25 unconditionally (Sphere/ServUO
            //    both do) so even if no gump is currently open, the
            //    drag layer / hot-bar references are valid the moment
            //    a gump is opened. Containers nested inside the
            //    backpack (e.g. a pouch) also need their own contents
            //    pushed — we recurse via FindContentItem semantics.
            var pack = _character.Backpack;
            if (pack != null)
            {
                foreach (var child in _world.GetContainerContents(pack.Uid))
                {
                    _netState.Send(new PacketContainerItem(
                        child.Uid.Value, child.DispIdFull, 0,
                        child.Amount, child.X, child.Y,
                        pack.Uid.Value, child.Hue,
                        _netState.IsClientPost6017));
                }
            }
        }

        // Resurrection visual + sound — anchored fixed effect (0x376A
        // heal particle) and chime (0x0214). BroadcastNearby with
        // excludeUid=0 reaches the resurrected player too, so no extra
        // _netState.Send needed.
        var resEffect = new PacketEffect(
            0x03,
            uid, 0,
            0x376A,
            cx, cy, (short)cz,
            0, 0, 0,
            10, 30, true, false);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, resEffect, 0);

        var resSound = new PacketSound(0x0214, cx, cy, cz);
        BroadcastNearby?.Invoke(_character.Position, UpdateRange, resSound, 0);

        SendCharacterStatus(_character);
        SysMessage(ServerMessages.Get(corpseRestored
            ? "combat_resurrected_with_corpse"
            : "combat_resurrected"));
    }

    public void HandleWarMode(bool warMode)
    {
        if (_character == null) return;
        _logger.LogDebug("[war_toggle_request] client={ClientId} char=0x{Char:X8} requested={Requested} current={Current}",
            _netState.Id, _character.Uid.Value, warMode ? "war" : "peace", _character.IsInWarMode ? "war" : "peace");
        // @UserWarmode fires before the state flip so a script can abort
        // the toggle by returning 1. Matches Source-X @UserWarmode in
        // CClient::Event_WalkToggleWarmode.
        var triggerArgs = new TriggerArgs { CharSrc = _character, N1 = warMode ? 1 : 0 };
        if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.UserWarmode, triggerArgs) == TriggerResult.True)
            return;
        SetWarMode(warMode, syncClients: true, preserveTarget: false);
        SysMessage(warMode ? ServerMessages.Get("combat_warmode_on") : ServerMessages.Get("combat_warmode_off"));
    }

    // ==================== Spell Casting ====================

    public void HandleCastSpell(SpellType spell, uint targetUid)
    {
        if (_character == null || _spellEngine == null) return;

        // @SpellSelect (Source-X) — a spell was chosen. Fires before @SpellCast and
        // the mana/skill/reagent checks so a script can cancel early. N1 = spell.
        if (_triggerDispatcher != null &&
            _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SpellSelect,
                new TriggerArgs { CharSrc = _character, N1 = (int)spell }) == TriggerResult.True)
            return;

        var spellDef = _spellEngine.GetSpellDef(spell);

        // Fire @SpellCast — if script blocks, don't cast. Source-X contract:
        // ARGN1 = spell, ARGN2 = difficulty (skill req / 10), ARGN3 = cast
        // wait time in tenths — writable, the classic "change the cast delay
        // from script" hook. LOCAL.WOP carries the power words: a script may
        // rewrite them or clear them for a silent cast.
        int castTimeOverrideMs = 0;
        string? wopOverride = null;
        ushort wopHue = 0;
        byte wopFont = 0;
        if (_triggerDispatcher != null)
        {
            int seededWaitTenths = spellDef != null
                ? SpellEngine.CalculateCastTimeTenths(_character, spellDef)
                : 0;
            string seededWop = spellDef?.GetPowerWords() ?? "";
            var castLocals = new SphereNet.Scripting.Variables.VarMap();
            castLocals.Set("WOP", seededWop);
            castLocals.SetInt("WOPColor", 0);
            castLocals.SetInt("WOPFont", 0);
            var castArgs = new TriggerArgs
            {
                CharSrc = _character,
                N1 = (int)spell,
                N2 = (spellDef?.GetDifficulty() ?? 0) / 10,
                N3 = seededWaitTenths,
                Locals = castLocals,
            };
            var result = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SpellCast, castArgs);
            if (result == TriggerResult.True)
                return;
            if (castArgs.N3 != seededWaitTenths && castArgs.N3 > 0)
                castTimeOverrideMs = castArgs.N3 * 100;
            // VarMap removes a key set to "" — a cleared WOP means silence.
            string wopNow = castLocals.Get("WOP") ?? "";
            if (wopNow != seededWop)
                wopOverride = wopNow;
            wopHue = (ushort)Math.Clamp(castLocals.GetInt("WOPColor", 0), 0, ushort.MaxValue);
            wopFont = (byte)Math.Clamp(castLocals.GetInt("WOPFont", 0), 0, byte.MaxValue);
        }

        // Reference Cmd_Skill_Magery: Polymorph/Summon casts open their
        // script selection menu when one exists (@SkillMenu with the menu
        // name fires first and can veto). The menu entries do the work
        // (POLY/SUMMON verbs); without a script menu the normal cast flow
        // continues below.
        if (targetUid == 0)
        {
            string? skillMenuName = spell switch
            {
                SpellType.Polymorph => "sm_polymorph",
                SpellType.SummonCreature => "sm_summon",
                _ => null,
            };
            if (skillMenuName != null)
            {
                if (_triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillMenu,
                        new TriggerArgs { CharSrc = _character, S1 = skillMenuName }) == TriggerResult.True)
                    return;
                // Mark the menu as CAST-driven: the POLY/SUMMON verb the menu
                // entry runs must stash the pick and start the REAL cast
                // (mana/reagents/cast time/fizzle), not apply directly —
                // Source-X Cmd_Skill_Magery stores the selection and calls
                // Spell_CastStart.
                _character.SetTag("CAST_MENU_SPELL", ((int)spell).ToString());
                if (_client.TryExecuteScriptCommand(_character, "SKILLMENU", skillMenuName, null))
                    return;
                _character.RemoveTag("CAST_MENU_SPELL"); // no menu — normal flow
            }
        }

        // Precast: power words + animation first, target cursor after timer.
        if (targetUid == 0 && spellDef != null && SpellEngine.IsPrecastEnabled(spellDef))
        {
            StartPrecast(spell, castTimeOverrideMs, wopOverride, wopHue, wopFont);
            return;
        }

        // If no explicit target provided, check if the spell needs a target cursor
        if (targetUid == 0)
        {
            bool needsTarget = spellDef != null &&
                (spellDef.IsFlag(SpellFlag.TargChar) || spellDef.IsFlag(SpellFlag.TargObj) ||
                 spellDef.IsFlag(SpellFlag.Area) || spellDef.IsFlag(SpellFlag.Field));

            if (needsTarget)
            {
                SetPendingTarget((serial, x, y, z, graphic) =>
                {
                    if (_character == null) return;
                    // Source-X Spell_TargCheck (DEFMSG_SPELL_TARG_OBJ): an
                    // object-target spell aborts on a ground pick unless
                    // TARG_XYZ allows it — it must never silently become a
                    // self-cast.
                    if (serial == 0 && spellDef != null &&
                        spellDef.IsFlag(SpellFlag.TargObj) && !spellDef.IsFlag(SpellFlag.TargXYZ))
                    {
                        SysMessage(ServerMessages.Get("target_must_object"));
                        return;
                    }
                    _character.SetCastTargetPosPending(new Point3D(x, y, z, _character.MapIndex));
                    HandleCastSpell(spell, serial != 0 ? serial : _character.Uid.Value);
                });
                return;
            }

            // Self-buff spell — target self
            targetUid = _character.Uid.Value;
        }

        var targetPos = _character.Position;
        if (_character.TryTakeCastTargetPosPending(out Point3D pendingPos))
            targetPos = pendingPos;
        else
        {
            var targetChar = _world.FindChar(new Serial(targetUid));
            if (targetChar != null)
                targetPos = targetChar.Position;
        }

        int castTime = _spellEngine.CastStart(_character, spell, new Serial(targetUid), targetPos,
            wopOverride, wopHue, wopFont);
        if (castTime > 0)
        {
            if (castTimeOverrideMs > 0)
                castTime = castTimeOverrideMs; // @SpellCast ARGN3 rewrite
            _character.SetCastTimerEnd(Environment.TickCount64 + castTime);
        }
        else
        {
            // Fire @SpellFail
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
                new TriggerArgs { CharSrc = _character, N1 = (int)spell });
            SysMessage(ServerMessages.Get("spell_cant_cast"));
        }
    }

    private void StartPrecast(SpellType spell, int castTimeOverrideMs = 0, string? wopOverride = null,
        ushort wopHue = 0, byte wopFont = 0)
    {
        if (_character == null || _spellEngine == null) return;

        int castTime = _spellEngine.CastStart(_character, spell, _character.Uid, _character.Position,
            wopOverride, wopHue, wopFont);
        if (castTime > 0)
        {
            if (castTimeOverrideMs > 0)
                castTime = castTimeOverrideMs; // @SpellCast ARGN3 rewrite
            _character.SpellPrecast = true;
            _character.SetCastTimerEnd(Environment.TickCount64 + castTime);
            return;
        }

        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SpellFail,
            new TriggerArgs { CharSrc = _character, N1 = (int)spell });
        SysMessage(ServerMessages.Get("spell_cant_cast"));
    }

    private void PromptPrecastTarget(SpellType spell, Magic.SpellDef? spellDef)
    {
        if (_character == null || _spellEngine == null) return;

        bool needsTarget = spellDef != null &&
            (spellDef.IsFlag(SpellFlag.TargChar) || spellDef.IsFlag(SpellFlag.TargObj) ||
             spellDef.IsFlag(SpellFlag.Area) || spellDef.IsFlag(SpellFlag.Field));

        if (!needsTarget)
        {
            FinishPrecastCast(spell, _character.Uid.Value, _character.Position);
            return;
        }

        SysMessage(spellDef!.TargetPrompt.Length > 0
            ? spellDef.TargetPrompt
            : "Choose your target.");

        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;
            // Source-X Spell_TargCheck: the precast finished but the pick is
            // not a valid object target — the spell aborts (never self-casts).
            if (serial == 0 && spellDef != null &&
                spellDef.IsFlag(SpellFlag.TargObj) && !spellDef.IsFlag(SpellFlag.TargXYZ))
            {
                SysMessage(ServerMessages.Get("target_must_object"));
                _character.ClearCastState();
                return;
            }
            var targetPos = new Point3D(x, y, z, _character.MapIndex);
            uint uid = serial != 0 ? serial : _character.Uid.Value;
            var targetChar = _world.FindChar(new Serial(uid));
            if (targetChar != null)
                targetPos = targetChar.Position;
            FinishPrecastCast(spell, uid, targetPos);
        });
    }

    private void FinishPrecastCast(SpellType spell, uint targetUid, Point3D targetPos)
    {
        if (_character == null || _spellEngine == null) return;

        _character.UpdateCastTarget(new Serial(targetUid), targetPos);

        var spellDef = _spellEngine.GetSpellDef(spell);
        var targetChar = _world.FindChar(new Serial(targetUid));

        bool castOk = _spellEngine.CastDone(_character);
        if (castOk)
        {
            string spellName = spellDef?.Name ?? spell.ToString();
            SysMessage(ServerMessages.GetFormatted("spell_cast_ok", spellName));
        }
        // @SpellFail on a failed completion is now fired by the engine
        // (SpellEngine.CastDone → OnCastResolved), shared with the NPC path.
    }

    public void TickSpellCast()
    {
        if (_character == null || _spellEngine == null) return;

        if (_character.IsCastTimerExpired(Environment.TickCount64))
        {
            _character.SetCastTimerEnd(0);

            if (_character.SpellPrecast)
            {
                _character.SpellPrecast = false;
                SpellType preSpell = default;
                if (_character.TryGetCastingSpell(out SpellType pre))
                    preSpell = pre;
                PromptPrecastTarget(preSpell, _spellEngine.GetSpellDef(preSpell));
                return;
            }

            // Retrieve spell ID before CastDone clears state
            int spellId = 0;
            if (_character.TryGetCastingSpell(out SpellType castingSpell))
                spellId = (int)castingSpell;

            // Get spell def + target BEFORE CastDone clears state
            var spellDef = _spellEngine.GetSpellDef((SpellType)spellId);
            uint targetUidRaw = _character.CastTargetUid.Value;
            var targetChar = targetUidRaw != 0 ? _world.FindChar(new Serial(targetUidRaw)) : null;

            bool castOk = _spellEngine.CastDone(_character);

            if (castOk)
            {
                // --- Spell name message ---
                string spellName = spellDef?.Name ?? $"Spell #{spellId}";
                SysMessage(ServerMessages.GetFormatted("spell_cast_ok", spellName));

                // --- Visual effect (0x70) on target ---
                var effectTarget = targetChar ?? _character;
                ushort effectGraphic = spellDef?.EffectId ?? 0;
                if (effectGraphic != 0)
                {
                    // type 3 = effect at location (on char), type 1 = bolt from src to dst
                    byte effectType = (spellDef != null && spellDef.IsFlag(SpellFlag.FxBolt)) ? (byte)1 : (byte)3;
                    var effectPacket = new PacketEffect(
                        effectType,
                        effectType == 1 ? _character.Uid.Value : effectTarget.Uid.Value,
                        effectTarget.Uid.Value,
                        effectGraphic,
                        effectTarget.X, effectTarget.Y, (short)effectTarget.Z,
                        effectTarget.X, effectTarget.Y, (short)effectTarget.Z,
                        10, 30, true, false);
                    _netState.Send(effectPacket);
                    BroadcastNearby?.Invoke(effectTarget.Position, UpdateRange, effectPacket, _character.Uid.Value);
                }

                // @SpellEffect / @SpellSuccess and the wand-charge / scroll
                // consumption are now fired by the engine in SpellEngine.CastDone
                // (OnCastResolved), so NPC and precast casts reach them too — the
                // client only renders the spell's visuals above.
            }
        }
    }

    /// <summary>
    /// Consolidated client tick: runs combat, spell casting, and stat updates.
    /// Call this once per server tick instead of calling TickCombat, TickSpellCast,
    /// and TickStatUpdate separately. This ensures consistent tick order and
    /// simplifies maintenance (single place to modify if new tick types are added).
    /// </summary>
    public void TickClientState()
    {
        TickMovementQueue(MoveClock());
        TickCombat();
        TickSpellCast();
        TickPendingSkill();
        TickPendingCraft();
        TickStatUpdate();
    }

    /// <summary>
    /// Detect stat changes (from regen, combat, etc.) and send updates to client.
    /// Called each server tick.
    /// </summary>
    public void TickStatUpdate()
    {
        if (_character == null || !IsPlaying) return;

        bool hitsChanged = _character.Hits != _lastHits;
        bool manaChanged = _character.Mana != _lastMana;
        bool stamChanged = _character.Stam != _lastStam;
        if (hitsChanged || manaChanged || stamChanged)
        {
            long now = Environment.TickCount64;
            if (_lastVitalsPacketTick > 0 && now - _lastVitalsPacketTick < VitalsPacketIntervalMs)
                return;

            _lastHits = _character.Hits;
            _lastMana = _character.Mana;
            _lastStam = _character.Stam;
            _lastVitalsPacketTick = now;

            // Send only changed vital packets (avoid A1/A2/A3 spam).
            if (hitsChanged)
            {
                var healthPacket = new PacketUpdateHealth(
                    _character.Uid.Value, _character.MaxHits, _character.Hits);
                _netState.Send(healthPacket);
                BroadcastNearby?.Invoke(_character.Position, UpdateRange, healthPacket, _character.Uid.Value);
            }
            if (manaChanged)
            {
                _netState.Send(new PacketUpdateMana(
                    _character.Uid.Value, _character.MaxMana, _character.Mana));
            }
            if (stamChanged)
            {
                _netState.Send(new PacketUpdateStamina(
                    _character.Uid.Value, _character.MaxStam, _character.Stam));
            }
        }
    }

    // ==================== Double Click / Item Use ====================
}
