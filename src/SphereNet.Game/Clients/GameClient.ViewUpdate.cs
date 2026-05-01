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

public sealed partial class GameClient
{

    /// <summary>
    /// Source-X CClient::addObjMessage loop. Sends newly visible objects and
    /// removes objects that went out of range. Called each server tick.
    /// </summary>
    public void UpdateClientView()
    {
        var delta = BuildViewDelta();
        if (delta != null)
            ApplyViewDelta(delta);
    }

    public sealed class ClientViewDelta
    {
        public HashSet<uint> CurrentChars { get; } = [];
        public HashSet<uint> CurrentItems { get; } = [];
        public List<(Character Character, bool HiddenAsAllShow)> NewChars { get; } = [];
        public List<Character> UpdatedChars { get; } = [];
        public List<(Item Item, bool HiddenAsAllShow)> NewItems { get; } = [];
    }

    /// <summary>
    /// Build a readonly visibility delta. Safe for parallel build phase.
    /// Only runs for clients with ViewNeedsRefresh — idle clients skip entirely.
    /// </summary>
    public ClientViewDelta? BuildViewDelta()
    {
        if (_character == null || !IsPlaying) return null;
        if (_character.IsReplaySpectator) return null;

        int range = _netState.ViewRange;
        var center = _character.Position;
        var delta = new ClientViewDelta();

        foreach (var ch in _world.GetCharsInRange(center, range))
        {
            if (ch == _character || ch.IsDeleted) continue;
            if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) continue;

            bool isOfflinePlayer = ch.IsPlayer && !ch.IsOnline;
            if (isOfflinePlayer && !_character.AllShow)
                continue;

            bool isHidden = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
            bool canSeeHidden = _character.AllShow ||
                (_character.PrivLevel >= Core.Enums.PrivLevel.Counsel &&
                 _character.PrivLevel >= ch.PrivLevel);

            if (isHidden && !canSeeHidden)
                continue;

            bool ghostManifested = ch.IsDead && ch.IsInWarMode;
            if (ch.IsDead && !_character.IsDead && !ghostManifested)
            {
                bool canSeeGhosts = _character.AllShow ||
                    _character.PrivLevel >= Core.Enums.PrivLevel.Counsel ||
                    _character.IsStatFlag(Core.Enums.StatFlag.SpiritSpeak);
                if (!canSeeGhosts)
                    continue;
            }

            uint uid = ch.Uid.Value;
            delta.CurrentChars.Add(uid);

            bool hiddenAsAllShow = isOfflinePlayer || (isHidden && canSeeHidden);
            if (!_knownChars.Contains(uid))
                delta.NewChars.Add((ch, hiddenAsAllShow));
            else
                delta.UpdatedChars.Add(ch);
        }

        bool isStaff = _character.PrivLevel >= Core.Enums.PrivLevel.Counsel;
        foreach (var item in _world.GetItemsInRange(center, range))
        {
            if (item.IsDeleted || item.IsEquipped || !item.IsOnGround) continue;
            bool isInvis = item.IsAttr(Core.Enums.ObjAttributes.Invis);
            if (isInvis && !_character.AllShow && !isStaff)
                continue;

            uint uid = item.Uid.Value;
            delta.CurrentItems.Add(uid);
            if (!_knownItems.Contains(uid))
                delta.NewItems.Add((item, isInvis && (_character.AllShow || isStaff)));
        }

        return delta;
    }

    /// <summary>
    /// Apply previously built delta and perform packet I/O + known-set mutation.
    /// Must run on single-thread apply phase.
    /// </summary>
    public void ApplyViewDelta(ClientViewDelta delta)
    {
        if (_character == null || !IsPlaying) return;

        foreach (var (ch, hiddenAsAllShow) in delta.NewChars)
        {
            if (hiddenAsAllShow)
                SendDrawObjectHidden(ch);
            else
                SendDrawObject(ch);

            uint uid = ch.Uid.Value;
            _knownChars.Add(uid);
            _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
        }

        foreach (var ch in delta.UpdatedChars)
        {
            uint uid = ch.Uid.Value;
            bool posChanged = false;
            bool bodyChanged = false;
            if (_lastKnownPos.TryGetValue(uid, out var last))
            {
                posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                bodyChanged = last.Body != ch.BodyId || last.Hue != ch.Hue;
            }
            else
            {
                posChanged = true;
            }

            bool manifestGhost = ch.IsDead && ch.IsInWarMode &&
                !_character!.AllShow &&
                _character.PrivLevel < Core.Enums.PrivLevel.Counsel &&
                !_character.IsDead;
            bool isOfflinePlayer = ch.IsPlayer && !ch.IsOnline;
            bool isHidden = ch.IsInvisible || ch.IsStatFlag(Core.Enums.StatFlag.Hidden);
            bool canSeeHidden = _character.AllShow ||
                (_character.PrivLevel >= Core.Enums.PrivLevel.Counsel &&
                 _character.PrivLevel >= ch.PrivLevel);
            bool hiddenAsAllShow = isOfflinePlayer || (isHidden && canSeeHidden);

            if (bodyChanged)
            {
                if (hiddenAsAllShow)
                    SendDrawObjectHidden(ch);
                else if (manifestGhost)
                    SendDrawObjectWithHue(ch, 0x4001);
                else
                    SendDrawObject(ch);
            }
            else if (posChanged)
            {
                if (hiddenAsAllShow)
                    SendUpdateMobileHidden(ch);
                else if (manifestGhost)
                    SendUpdateMobileWithHue(ch, 0x4001);
                else
                    SendUpdateMobile(ch);
            }

            if (posChanged || bodyChanged)
                _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
        }

        foreach (var (item, hiddenAsAllShow) in delta.NewItems)
        {
            if (hiddenAsAllShow)
                SendWorldItemWithHue(item, 0x4001);
            else
                SendWorldItem(item);
            _knownItems.Add(item.Uid.Value);
        }

        var staleChars = new List<uint>();
        foreach (uint uid in _knownChars)
        {
            if (!delta.CurrentChars.Contains(uid))
            {
                _netState.Send(new PacketDeleteObject(uid));
                staleChars.Add(uid);
            }
        }
        foreach (uint uid in staleChars)
        {
            _knownChars.Remove(uid);
            _lastKnownPos.Remove(uid);
        }

        var staleItems = new List<uint>();
        foreach (uint uid in _knownItems)
        {
            if (!delta.CurrentItems.Contains(uid))
            {
                _netState.Send(new PacketDeleteObject(uid));
                staleItems.Add(uid);
            }
        }
        foreach (uint uid in staleItems)
            _knownItems.Remove(uid);
    }

    /// <summary>
    /// Update this client's _lastKnownPos for a character that was just broadcast via 0x77.
    /// Prevents the view delta from sending a duplicate 0x77 for the same position.
    /// </summary>
    public void UpdateKnownCharPosition(Character ch)
    {
        uint uid = ch.Uid.Value;
        if (_knownChars.Contains(uid))
            _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
    }

    /// <summary>Returns true if this client already tracks the given mobile (has sent 0x78 spawn).</summary>
    public bool HasKnownChar(uint uid) => _knownChars.Contains(uid);

    /// <summary>
    /// Update this client's known-character cache to reflect a body/hue
    /// change that we already broadcast out-of-band — e.g. the ghost
    /// transition during death (body=0x192, hue=0) or the living-body
    /// restore during resurrect (body=0x190, hue=skin). This prevents
    /// the next BuildViewDelta tick from detecting a stale
    /// <c>bodyChanged</c> and re-emitting a duplicate 0x78 with the new
    /// body — which would race the per-observer dispatch and either
    /// produce a duplicate ghost mobile (after 0xAF remap) or just
    /// repeat the spawn packet for no reason.
    ///
    /// If the UID is not currently in <c>_knownChars</c> the call is a
    /// no-op (use this in resurrect to safely re-sync everyone, including
    /// observers who never had the mobile in cache because the ghost
    /// was hidden from them).
    /// </summary>
    public void UpdateKnownCharRender(uint uid, ushort newBody, ushort newHue, byte direction, short x, short y, sbyte z)
    {
        if (_knownChars.Contains(uid))
            _lastKnownPos[uid] = (x, y, z, direction, newBody, newHue);
    }

    /// <summary>
    /// Drop the given character from this client's known-character set,
    /// optionally emitting a 0x1D PacketDeleteObject so ClassicUO removes
    /// the mobile from world.Mobiles immediately.
    ///
    /// <paramref name="sendDelete"/> = false: only clears server-side
    /// cache. Use this in the death dispatch for plain observers — the
    /// 0xAF DisplayDeath we already sent re-keys the mobile to
    /// <c>serial | 0x80000000</c> in ClassicUO, so a follow-up 0x1D with
    /// the original serial would target a now-empty slot (no-op, just
    /// wasted bandwidth). Without this option we'd ALSO double-clean the
    /// killer's view on every PvP death.
    ///
    /// <paramref name="sendDelete"/> = true (default): emit the 0x1D as
    /// well — useful when the dying mobile was never announced via 0xAF
    /// to this observer (e.g. cleanup after a teleport, or for a plain
    /// observer who came in range AFTER the death animation had already
    /// played for everyone else).
    ///
    /// Idempotent: safe to call when the UID is not currently known.
    /// </summary>
    public void RemoveKnownChar(uint uid, bool sendDelete = true)
    {
        if (_knownChars.Remove(uid))
        {
            _lastKnownPos.Remove(uid);
            if (sendDelete)
                _netState.Send(new PacketDeleteObject(uid));
        }
    }

    /// <summary>
    /// Called by BroadcastCharacterAppear to immediately show a character on this client.
    /// Each client renders from its own perspective (notoriety, AllShow, etc.).
    /// </summary>
    public void NotifyCharacterAppear(Character ch)
    {
        if (_character == null || !IsPlaying) return;
        if (ch == _character) return;
        if (ch.Position.Map != _character.Position.Map) return;
        if (!InRange(_character.Position, ch.Position, _netState.ViewRange)) return;

        // === Source-X ghost visibility (mirror of BuildViewDelta filter) ===
        // A dead/ghost character is invisible to LIVING observers unless
        // the observer is staff (Counsel+) or has AllShow toggled, OR the
        // ghost has manifested (war mode). Without this guard, a
        // login/teleport BroadcastCharacterAppear would push the ghost
        // mobile to plain players and cause exactly the duplicate-mobile
        // bug 0xAF was supposed to prevent.
        bool isStaffViewer = _character.AllShow ||
            _character.PrivLevel >= Core.Enums.PrivLevel.Counsel;
        bool ghostManifested = ch.IsDead && ch.IsInWarMode;

        if (ch.IsDead && !_character.IsDead && !ghostManifested && !isStaffViewer)
            return;

        uint uid = ch.Uid.Value;
        // Manifested ghost renders translucent grey (hue 0x4001) for plain
        // observers; staff already see ghosts in their normal hue (HUE_DEFAULT).
        if (ghostManifested && !isStaffViewer && !_character.IsDead)
            SendDrawObjectWithHue(ch, 0x4001);
        else
            SendDrawObject(ch);

        _knownChars.Add(uid);
        _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
    }

    /// <summary>
    /// Object-centric move notification for NPC movement. Handles enter-range (0x78),
    /// leave-range (0x1D), and position-update (0x77).
    /// </summary>
    public void NotifyCharMoved(Character ch, Point3D oldPos)
    {
        if (_character == null || !IsPlaying) return;
        if (ch == _character) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _netState.ViewRange;
        bool wasInRange = InRange(_character.Position, oldPos, range) && oldPos.Map == _character.Position.Map;
        bool nowInRange = InRange(_character.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
        {
            NotifyCharacterAppear(ch);
        }
        else if (wasInRange && !nowInRange)
        {
            RemoveKnownChar(uid, sendDelete: true);
        }
        else if (wasInRange && nowInRange && _knownChars.Contains(uid))
        {
            if (_lastKnownPos.TryGetValue(uid, out var last))
            {
                bool posChanged = last.X != ch.X || last.Y != ch.Y || last.Z != ch.Z || last.Dir != (byte)ch.Direction;
                if (!posChanged) return;
            }
            SendUpdateMobile(ch);
            _lastKnownPos[uid] = (ch.X, ch.Y, ch.Z, (byte)ch.Direction, ch.BodyId, ch.Hue);
        }
    }

    /// <summary>
    /// Player enter/leave range notification. Only handles enter-range (0x78) and
    /// leave-range (0x1D). Still-in-range 0x77 is handled by BroadcastMoveNearby.
    /// </summary>
    public void NotifyCharEnterLeave(Character ch, Point3D oldPos)
    {
        if (_character == null || !IsPlaying) return;
        if (ch == _character) return;
        if (ch.IsDeleted) return;
        if (ch.IsStatFlag(Core.Enums.StatFlag.Ridden)) return;

        int range = _netState.ViewRange;
        bool wasInRange = InRange(_character.Position, oldPos, range) && oldPos.Map == _character.Position.Map;
        bool nowInRange = InRange(_character.Position, ch.Position, range);

        uint uid = ch.Uid.Value;

        if (!wasInRange && nowInRange)
            NotifyCharacterAppear(ch);
        else if (wasInRange && !nowInRange)
            RemoveKnownChar(uid, sendDelete: true);
    }

    private static bool InRange(Point3D a, Point3D b, int range)
    {
        if (a.Map != b.Map) return false;
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return dx <= range && dy <= range;
    }

    // ==================== Outgoing Packet Helpers ====================
}
