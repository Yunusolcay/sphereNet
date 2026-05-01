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
    private bool CanSendStatusFor(Character ch)
    {
        if (_character == null)
            return false;
        if (ch == _character)
            return true;
        if (ch.MapIndex != _character.MapIndex)
            return false;

        int range = Math.Max(5, (int)_netState.ViewRange);
        return _character.Position.GetDistanceTo(ch.Position) <= range;
    }

    private static string ResolveStatusName(Character ch)
    {
        string name = ch.GetName()
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();

        if (name.Length > 0)
            return name;

        var def = DefinitionLoader.GetCharDef(ch.CharDefIndex);
        name = def?.Name?.Trim() ?? "";
        if (name.Length == 0)
            name = def?.DefName?.Trim() ?? "";

        if (name.Length == 0)
            name = ch.IsPlayer ? "Player" : $"NPC_{ch.BodyId:X}";

        return name.Length > 30 ? name[..30] : name;
    }

    public void SendSkillList()
    {
        if (_character == null) return;

        var skills = new List<(ushort Id, ushort Value, ushort RawValue, byte Lock, ushort Cap)>();
        for (int i = 0; i < (int)SkillType.Qty && i < 58; i++)
        {
            ushort val = _character.GetSkill((SkillType)i);
            byte lockState = _character.GetSkillLock((SkillType)i);
            skills.Add(((ushort)i, val, val, lockState, 1000)); // cap=100.0 (1000 in tenths)
        }
        _netState.Send(new PacketSkillList(skills.ToArray()));
    }

    private void SendPickupFailed(byte reason)
    {
        var buf = new PacketBuffer(2);
        buf.WriteByte(0x27);
        buf.WriteByte(reason);
        _netState.Send(buf);
    }

    private static (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] BuildEquipmentList(Character ch)
    {
        var list = new List<(uint, ushort, byte, ushort)>();
        for (int i = 0; i <= (int)Layer.Horse; i++)
        {
            var item = ch.GetEquippedItem((Layer)i);
            if (item == null) continue;
            list.Add((item.Uid.Value, item.DispIdFull, (byte)i, item.Hue));
        }
        return list.ToArray();
    }

    /// <summary>
    /// Build the network MobileFlags byte (0x77/0x78/0x20 packets).
    ///
    /// Wire format mirrors ClassicUO <c>MobileFlags</c>:
    ///   0x01 = Frozen
    ///   0x02 = Female  (NOT "Dead" — ghost state is read from body ID
    ///                   0x192/0x193 client-side; setting 0x02 on a male
    ///                   ghost makes ClassicUO short-circuit
    ///                   <c>CheckGraphicChange()</c> because the cached
    ///                   IsFemale state contradicts the male body, and
    ///                   the sprite stays on the previous human atlas —
    ///                   the root cause of the "ghost body never
    ///                   renders" bug for both self and staff observers
    ///                   in the death rebuild logs.)
    ///   0x04 = Flying / Poisoned (client interprets per body type)
    ///   0x08 = YellowBar
    ///   0x10 = IgnoreMobiles
    ///   0x40 = WarMode
    ///   0x80 = Hidden / Invisible
    ///
    /// Female is derived from the body ID (Source-X
    /// <c>CChar::IsFemale()</c> returns the same lookup): human female
    /// = 0x191, female ghost = 0x193.
    /// </summary>
    private static byte BuildMobileFlags(Character ch)
    {
        byte flags = 0;
        if (ch.IsInvisible) flags |= 0x80;
        if (ch.IsInWarMode) flags |= 0x40;
        if (ch.BodyId == 0x0191 || ch.BodyId == 0x0193) flags |= 0x02;
        if (ch.IsStatFlag(StatFlag.Freeze)) flags |= 0x01;
        return flags;
    }

    /// <summary>
    /// Turn the player to face <paramref name="target"/> and broadcast the
    /// new facing to nearby clients via 0x77. Mirrors Source-X
    /// <c>CChar::UpdateDir(pCharTarg)</c> -> <c>UpdateMove(GetTopPoint())</c>:
    /// when an NPC starts a swing or a spell, the engine first turns the
    /// caster/attacker so that the animation plays in the correct
    /// direction. Without this, melee/cast animations look broken from
    /// the side and bow shots may visually fly the wrong way.
    /// Skips the broadcast (but still updates state) when facing is
    /// already correct, to avoid packet spam during continuous combat.
    /// </summary>
    public void FaceTarget(Character target)
    {
        if (_character == null || target == null) return;
        if (target.Position.Equals(_character.Position)) return;

        var newDir = _character.Position.GetDirectionTo(target.Position);
        if (newDir == _character.Direction) return;

        _character.Direction = newDir;

        byte flags = BuildMobileFlags(_character);
        byte noto = GetNotoriety(_character);
        byte dirByte = (byte)((byte)_character.Direction & 0x07);

        var pkt = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z, dirByte,
            _character.Hue, flags, noto);

        if (BroadcastMoveNearby != null)
            BroadcastMoveNearby.Invoke(_character.Position, UpdateRange, pkt, _character.Uid.Value, _character);
        else
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, pkt, _character.Uid.Value);
    }

    private bool TryHandleCommandSpeech(string text)
    {
        if (_character == null || _commands == null)
            return false;

        // Some clients may prepend invisible/null whitespace-like chars in unicode speech.
        // Normalize before checking command prefix to keep command parsing resilient.
        string normalized = text
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .TrimStart(' ', '\t', '\r', '\n');
        if (normalized.Length <= 1)
            return false;

        char prefix = _commands.CommandPrefix;
        // Accept '.' and '/' regardless of configured prefix for Source-X compatibility.
        if (normalized[0] != prefix && normalized[0] != '.' && normalized[0] != '/')
            return false;

        string commandLine = normalized[1..]
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrEmpty(commandLine))
            return true;

        _logger.LogDebug("[command_dispatch] account={Account} char=0x{Char:X8} raw='{Raw}' normalized='{Norm}' prefix='{Prefix}' cmd='{Cmd}'",
            _account?.Name ?? "?", _character.Uid.Value, text, normalized, prefix, commandLine);

        var posBefore = _character.Position;
        var result = _commands.TryExecute(_character, commandLine);
        switch (result)
        {
            case CommandResult.Executed:
                if (!_character.Position.Equals(posBefore))
                {
                    // Teleport-like commands (.GO, .JAIL, script-based moves, etc.) must
                    // force a client-side world refresh so the player actually relocates.
                    Resync();
                    _mountEngine?.EnsureMountedState(_character);
                    BroadcastDrawObject(_character);
                }
                return true;

            case CommandResult.InsufficientPriv:
                var required = _commands.GetRequiredPrivLevel(commandLine) ?? PrivLevel.Counsel;
                SysMessage(ServerMessages.GetFormatted("gm_insuf_priv", required, _character.PrivLevel));
                _logger.LogDebug("[command_priv_reject] account={Account} accountPLEVEL={AccLvl} char=0x{Char:X8} charPrivLevel={ChLvl} cmd='{Cmd}' required={Req}",
                    _account?.Name ?? "?", _account?.PrivLevel, _character.Uid.Value, _character.PrivLevel, commandLine, required);
                return true;

            case CommandResult.NotFound:
                SysMessage(ServerMessages.GetFormatted("cmd_invalid", commandLine));
                _logger.LogDebug("[speech_fallback] account={Account} char=0x{Char:X8} unknownCmd='{Cmd}'",
                    _account?.Name ?? "?", _character.Uid.Value, commandLine);
                return true;

            case CommandResult.Failed:
                _logger.LogDebug("[command_failed] account={Account} char=0x{Char:X8} cmd='{Cmd}'",
                    _account?.Name ?? "?", _character.Uid.Value, commandLine);
                return true;

            default:
                return false;
        }
    }

    private void SetWarMode(bool warMode, bool syncClients, bool preserveTarget)
    {
        if (_character == null) return;

        bool oldState = _character.IsInWarMode;
        if (warMode)
            _character.SetStatFlag(StatFlag.War);
        else
            _character.ClearStatFlag(StatFlag.War);

        if (!warMode && !preserveTarget)
        {
            _character.FightTarget = Serial.Invalid;
            _character.NextAttackTime = 0;
        }

        if (!syncClients) return;

        // Send 0x72 war mode confirmation — client expects this to actually toggle
        _netState.Send(new PacketWarModeResponse(warMode));

        // Broadcast appearance update to nearby players. For a LIVING
        // character a single 0x77 update is enough — every observer
        // already has the mobile in their world.Mobiles.
        //
        // Ghosts are special. While in peace mode the ghost is hidden
        // from plain observers via the BuildViewDelta filter (their
        // _knownChars never tracked the mobile). A blanket 0x77
        // broadcast on a peace→war transition would target a serial
        // that ClassicUO doesn't know about and silently drop.
        // So for a manifesting/un-manifesting ghost we skip the
        // BroadcastNearby and use per-observer dispatch instead:
        //
        //   peace → war (manifest):
        //     plain observer  → 0x78 PacketDrawObject (hue 0x4001
        //                       translucent grey) + cache add so the
        //                       next view-delta tick doesn't double-spawn
        //     staff observer  → 0x77 normal update (already had the
        //                       ghost mobile in cache)
        //
        //   war → peace (un-manifest):
        //     plain observer  → 0x1D delete + cache drop
        //     staff observer  → 0x77 normal update
        //
        //   self                → 0x77 always (own client always knows
        //                         the mobile)
        byte warFlags = BuildMobileFlags(_character);
        byte warNoto = GetNotoriety(_character);
        var mobileMoving = new PacketMobileMoving(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z, (byte)_character.Direction,
            _character.Hue, warFlags, warNoto);

        if (_character.IsDead && ForEachClientInRange != null)
        {
            uint selfUid = _character.Uid.Value;

            ForEachClientInRange.Invoke(_character.Position, UpdateRange, selfUid,
                (observerCh, observerClient) =>
                {
                    bool isStaff = observerCh.AllShow ||
                        observerCh.PrivLevel >= Core.Enums.PrivLevel.Counsel;
                    if (isStaff)
                    {
                        observerClient.Send(mobileMoving);
                        return;
                    }

                    if (warMode)
                    {
                        // Manifest: spawn ghost as translucent grey on this
                        // plain observer's client and start tracking it so
                        // BuildViewDelta will keep it in sync (manifested
                        // ghosts are now in the delta filter's allow-list).
                        // NotifyCharacterAppear handles the hue=0x4001 draw
                        // and _knownChars insert in one call (mirroring the
                        // login/teleport entry path).
                        observerClient.NotifyCharacterAppear(_character);
                    }
                    else
                    {
                        // Un-manifest: drop the mobile from the plain
                        // observer's view so they no longer see it.
                        observerClient.RemoveKnownChar(selfUid, sendDelete: true);
                    }
                });
        }
        else
        {
            BroadcastNearby?.Invoke(_character.Position, UpdateRange, mobileMoving,
                _character.Uid.Value);
        }

        _logger.LogDebug("[war_mode] client={ClientId} char=0x{Char:X8} {Old}->{New}",
            _netState.Id, _character.Uid.Value, oldState ? "war" : "peace", _character.IsInWarMode ? "war" : "peace");
    }

    public static int GetSwingDelayMs(Character attacker, Item? weapon)
        => CombatEngine.GetSwingDelayMs(attacker, weapon);

    /// <summary>Map a weapon (or bare fists) to the correct humanoid
    /// 0x6E animation action index. Ranged weapons trigger the nock/fire
    /// action; blades use the slash; blunt/maces use the overhead swing;
    /// unarmed uses the wrestling punch. Exact values come from
    /// ServUO MobileAnimation / Source-X AnimationRange tables.</summary>
    private static ushort GetSwingAction(Character attacker, Item? weapon)
    {
        bool mounted = attacker.IsMounted;

        if (weapon == null)
            return mounted ? (ushort)AnimationType.HorseSlap : (ushort)AnimationType.AttackWrestle;

        bool twoHand = weapon.EquipLayer == Layer.TwoHanded;

        if (mounted)
        {
            return weapon.ItemType switch
            {
                ItemType.WeaponBow => (ushort)AnimationType.HorseAttackBow,
                ItemType.WeaponXBow => (ushort)AnimationType.HorseAttackXBow,
                _ => (ushort)AnimationType.HorseAttack,
            };
        }

        return weapon.ItemType switch
        {
            ItemType.WeaponBow => (ushort)AnimationType.AttackBow,
            ItemType.WeaponXBow => (ushort)AnimationType.AttackXBow,
            ItemType.WeaponSword => twoHand
                ? (ushort)AnimationType.Attack2HSlash : (ushort)AnimationType.AttackWeapon,
            ItemType.WeaponAxe => twoHand
                ? (ushort)AnimationType.Attack2HPierce : (ushort)AnimationType.Attack1HPierce,
            ItemType.WeaponFence => twoHand
                ? (ushort)AnimationType.Attack2HPierce : (ushort)AnimationType.Attack1HPierce,
            ItemType.WeaponMaceSmith or ItemType.WeaponMaceSharp or
            ItemType.WeaponMaceStaff or ItemType.WeaponMaceCrook or
            ItemType.WeaponMacePick or ItemType.WeaponWhip => twoHand
                ? (ushort)AnimationType.Attack2HBash : (ushort)AnimationType.Attack1HBash,
            ItemType.WeaponThrowing => (ushort)AnimationType.Attack2HBash,
            _ => (ushort)AnimationType.AttackWeapon,
        };
    }

    public static ushort GetNpcSwingAction(Character npc)
    {
        bool isHumanBody = npc.BodyId == 400 || npc.BodyId == 401;
        if (!isHumanBody)
            return 4; // ANIM_MON_ATTACK1

        var weapon = npc.GetEquippedItem(Layer.OneHanded) ?? npc.GetEquippedItem(Layer.TwoHanded);
        return GetSwingAction(npc, weapon);
    }

    private static ushort GetSwingSound(Item? weapon)
    {
        if (weapon == null)
            return 0x023A; // unarmed whoosh
        return weapon.ItemType switch
        {
            Core.Enums.ItemType.WeaponBow or
            Core.Enums.ItemType.WeaponXBow => 0x0223,  // bow draw
            Core.Enums.ItemType.WeaponSword or
            Core.Enums.ItemType.WeaponAxe or
            Core.Enums.ItemType.WeaponFence => 0x023B, // blade swing
            Core.Enums.ItemType.WeaponMaceSmith or
            Core.Enums.ItemType.WeaponMaceSharp or
            Core.Enums.ItemType.WeaponMaceStaff or
            Core.Enums.ItemType.WeaponMaceCrook or
            Core.Enums.ItemType.WeaponMacePick or
            Core.Enums.ItemType.WeaponWhip => 0x023D,  // mace swing
            Core.Enums.ItemType.WeaponThrowing => 0x0238,
            _ => 0x023B,
        };
    }

    /// <summary>Compute the UO notoriety byte for <paramref name="ch"/> as
    /// seen by this client's character. The client reads this byte (part of
    /// 0x77/0x78/etc.) to pick the overhead-name hue:
    /// 1=blue/innocent, 2=green/friend, 3=grey/neutral NPC,
    /// 4=grey/criminal, 5=orange/enemy-guild, 6=red/murderer,
    /// 7=yellow/invul. Returning 1 for everyone (as we did until now)
    /// rendered every mobile in neutral grey. Source-X:
    /// CChar::Noto_GetFlag / Noto_CalcFlag in CCharNotoriety.cpp.</summary>
    private byte GetNotoriety(Character ch)
    {
        if (_character == null || ch == _character)
            return 1; // self reads as innocent (blue)

        // Invulnerable first — always yellow regardless of karma/guild.
        if (ch.IsStatFlag(StatFlag.Invul))
            return 7;

        // Incognito — always grey (Source-X: STATF_INCOGNITO → NOTO_NEUTRAL)
        if (ch.IsStatFlag(StatFlag.Incognito))
            return 3;

        // Arena region — everyone neutral (Source-X: REGION_FLAG_ARENA)
        var targetRegion = _world?.FindRegion(ch.Position);
        if (targetRegion != null && targetRegion.IsFlag(RegionFlag.Arena))
            return 3;

        // Red zone — reversed notoriety (Source-X: REGION_FLAG_RED)
        bool isRedZone = targetRegion != null && targetRegion.IsFlag(RegionFlag.RedZone);
        if (isRedZone)
        {
            if (ch.IsMurderer) return 1; // murderers are "innocent" in red zones
            if (ch.Karma > 0) return 6;  // good karma is "evil" in red zones
        }

        // Murderers are red even if they share a guild with the viewer.
        if (ch.IsMurderer)
            return 6;

        // Active criminal flag from MakeCriminal()
        if (ch.IsCriminal || ch.IsStatFlag(StatFlag.Criminal))
            return 4;

        // Guild relations: same guild / ally = green, at-war = orange.
        var guildMgr = Character.ResolveGuildManager?.Invoke(_character.Uid);
        if (guildMgr != null)
        {
            var myGuild = guildMgr.FindGuildFor(_character.Uid);
            var theirGuild = guildMgr.FindGuildFor(ch.Uid);
            if (myGuild != null && theirGuild != null)
            {
                if (myGuild == theirGuild) return 2; // same guild → green
                if (myGuild.IsAlliedWith(theirGuild.StoneUid)) return 2; // ally → green
                if (myGuild.IsAtWarWith(theirGuild.StoneUid)) return 5; // enemy → orange
            }
        }

        // Party members read as green.
        var myParty = Character.ResolvePartyFinder?.Invoke(_character.Uid);
        if (myParty != null && myParty.IsMember(ch.Uid))
            return 2;

        // PermaGrey tag — Source-X: NOTO.PERMAGREY
        if (ch.TryGetTag("NOTO.PERMAGREY", out string? pg) && pg == "1")
            return 3;

        bool isActuallyPlayer = ch.IsPlayer || ch.TryGetTag("ACCOUNT", out _);
        if (!isActuallyPlayer)
            return GetNpcNotoriety(ch);

        return 1; // default player → innocent / blue
    }

    /// <summary>Notoriety for non-player mobiles. Source-X Noto_CalcFlag
    /// for NPCs mixes brain type and karma:
    ///  - monster / berserk / dragon brain → red (always hostile)
    ///  - healer / banker → yellow (protected / invul-by-role)
    ///  - vendor / stable / guard / human → blue (friendly townfolk)
    ///  - animal → grey (neutral wildlife, huntable)
    ///  - karma overrides: very negative → red, negative → grey criminal,
    ///    very positive → blue — lets scripts flip a normally-blue
    ///    townsfolk into a red renegade via SET KARMA.</summary>
    /// <summary>Source-X Noto_IsEvil + Noto_CalcFlag for NPCs. Evil thresholds
    /// differ per brain type: Monster/Dragon karma&lt;0, Berserk always,
    /// Animal karma&lt;=-800, NPC karma&lt;=-3000.</summary>
    private static byte GetNpcNotoriety(Character ch)
    {
        switch (ch.NpcBrain)
        {
            case NpcBrainType.Monster:
            case NpcBrainType.Dragon:
            case NpcBrainType.Berserk:
                return 6; // always hostile / red
            case NpcBrainType.Healer:
            case NpcBrainType.Banker:
                return 7; // yellow — invul by role
            case NpcBrainType.Guard:
                return 1; // blue — law enforcement
            case NpcBrainType.Vendor:
            case NpcBrainType.Stable:
            case NpcBrainType.Human:
                if (ch.Karma <= -3000) return 6; // evil NPC
                if (ch.Karma <= -500) return 4;  // criminal NPC
                return 1; // friendly
            case NpcBrainType.Animal:
                if (ch.Karma <= -800) return 6; // evil animal
                return 3; // neutral wildlife
            default:
                if (ch.Karma <= -3000) return 6;
                if (ch.Karma <= -500) return 4;
                return ch.Karma > 500 ? (byte)1 : (byte)3;
        }
    }

    // ==================== ITextConsole ====================

    public PrivLevel GetPrivLevel() => _account?.PrivLevel ?? PrivLevel.Guest;

    public void SysMessage(string text)
    {
        string msg = ResolveMessage(text);
        _netState.Send(new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, 0x0035, 3, "TRK", "System", msg
        ));
    }

    public void SysMessage(string text, ushort hue)
    {
        string msg = ResolveMessage(text);
        _netState.Send(new PacketSpeechUnicodeOut(
            0xFFFFFFFF, 0xFFFF, 6, hue, 3, "TRK", "System", msg
        ));
    }

    /// <summary>Send speech from an NPC to this client (overhead text above the NPC).</summary>
    private void NpcSpeech(Character npc, string text)
    {
        var packet = new PacketSpeechUnicodeOut(
            npc.Uid.Value, npc.BodyId, 0, 0x03B2, 3, "TRK", npc.GetName(), text);
        BroadcastNearby?.Invoke(npc.Position, 18, packet, 0);
    }

    public string GetName() => _account?.Name ?? "?";

    public bool TryExecuteScriptCommand(IScriptObj target, string key, string args, ITriggerArgs? triggerArgs)
    {
        if (_character == null) return false;

        string cmd = key.Trim();
        string upper = cmd.ToUpperInvariant();

        if (upper == "OBJ")
        {
            if (triggerArgs?.Object1 is Character objCh)
                _character.SetTag("OBJ", $"0{objCh.Uid.Value:X}");
            else if (triggerArgs?.Object1 is Item objItem)
                _character.SetTag("OBJ", $"0{objItem.Uid.Value:X}");
            return true;
        }

        // Source-X SET meta-verb: "Src.set <verb> [args]" pops a target
        // cursor and re-dispatches the verb against the picked object.
        // Sphere admin dialogs lean on this for "set dupe", "set
        // remove", "set xinfo" rows on the player tweak panel.
        if (upper == "SET" || upper == "SETUID")
        {
            string raw = args?.Trim() ?? "";
            if (raw.Length == 0) return true;
            int sp = raw.IndexOfAny(new[] { ' ', '\t' });
            string verb = sp > 0 ? raw[..sp] : raw;
            string verbArgs = sp > 0 ? raw[(sp + 1)..].TrimStart() : "";
            BeginXVerbTarget(verb, verbArgs);
            return true;
        }

        // Sphere MESSAGE command: overhead text on the target object.
        // Syntax: message @<hue>[,<type>,<font>] <text>
        //   e.g.  message @0481,1,1 [Nimloth]
        //   e.g.  message @080a [Invis]
        if (upper == "MESSAGE")
        {
            string raw = args.Trim();
            ushort hue = 0x03B2;
            byte speechType = 0; // normal overhead speech
            ushort font = 3;
            string text = raw;

            if (raw.StartsWith('@'))
            {
                int spaceIdx = raw.IndexOf(' ');
                string colorSpec = spaceIdx > 0 ? raw[1..spaceIdx] : raw[1..];
                text = spaceIdx > 0 ? raw[(spaceIdx + 1)..].Trim() : "";

                var colorParts = colorSpec.Split(',');
                if (colorParts.Length >= 1)
                {
                    string huePart = colorParts[0].Trim();
                    if (huePart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                        ushort.TryParse(huePart.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out ushort hx))
                    {
                        hue = hx;
                    }
                    else if (ushort.TryParse(huePart, System.Globalization.NumberStyles.HexNumber, null, out ushort hHex))
                    {
                        hue = hHex;
                    }
                    else if (ushort.TryParse(huePart, out ushort hDec))
                    {
                        hue = hDec;
                    }
                }
                if (colorParts.Length >= 2 && byte.TryParse(colorParts[1], out byte t))
                    speechType = t;
                if (colorParts.Length >= 3 && ushort.TryParse(colorParts[2], out ushort f))
                    font = f;
            }

            // Sphere compatibility: MESSAGE should appear overhead on target.
            // Many script packs use type=1 or type=6 here, but UO clients can render
            // those as system/label text instead of overhead speech.
            if (speechType is 1 or 6)
                speechType = 0;

            if (text.Length > 0)
            {
                uint serial = _character.Uid.Value;
                ushort bodyId = _character.BodyId;
                Point3D origin = _character.Position;
                if (target is Character ch)
                {
                    serial = ch.Uid.Value;
                    bodyId = ch.BodyId;
                    origin = ch.Position;
                }
                else if (target is Item item)
                {
                    serial = item.Uid.Value;
                    bodyId = 0;
                    origin = item.Position;
                }
                var packet = new PacketSpeechUnicodeOut(serial, bodyId, speechType, hue, font,
                    "TRK", target.GetName(), text);
                _netState.Send(packet);
                BroadcastNearby?.Invoke(origin, 18, packet, _character.Uid.Value);
            }
            return true;
        }

        // SAYUA — overhead speech with hue/type/font/lang
        // Format: sayua <hue>,<type>,<font>,<lang> <text>
        if (upper == "SAYUA")
        {
            string raw = args.Trim();
            int firstSpace = raw.IndexOf(' ');
            ushort hue = 0x03B2;
            byte speechType = 0;
            ushort font = 3;
            string text = raw;

            if (firstSpace > 0)
            {
                string paramsPart = raw[..firstSpace];
                text = raw[(firstSpace + 1)..].TrimStart();
                string[] parms = paramsPart.Split(',');
                if (parms.Length > 0 && ushort.TryParse(parms[0], out ushort h)) hue = h;
                if (parms.Length > 1 && byte.TryParse(parms[1], out byte t)) speechType = t;
                if (parms.Length > 2 && ushort.TryParse(parms[2], out ushort f)) font = f;
            }

            if (text.Length > 0)
            {
                uint serial = _character.Uid.Value;
                ushort bodyId = _character.BodyId;
                Point3D origin = _character.Position;
                if (target is Character ch)
                {
                    serial = ch.Uid.Value;
                    bodyId = ch.BodyId;
                    origin = ch.Position;
                }
                else if (target is Item item)
                {
                    serial = item.Uid.Value;
                    bodyId = 0;
                    origin = item.Position;
                }
                var packet = new PacketSpeechUnicodeOut(serial, bodyId, speechType, hue, font,
                    "TRK", target.GetName(), text);
                _netState.Send(packet);
                BroadcastNearby?.Invoke(origin, 18, packet, _character.Uid.Value);
            }
            return true;
        }

        // INPDLG <prop> <maxLength> — open a Source-X style text-entry
        // gump on this client. The reply (0xAC) writes the user-typed
        // value into <prop> on the script verb's target object.
        // Source-X: CObjBase.cpp:OV_INPDLG → CClient::addGumpInputVal.
        if (upper == "INPDLG")
        {
            string raw = args.Trim();
            if (raw.Length == 0)
                return true;

            string propName;
            int maxLen = 1;
            int sp = raw.IndexOf(' ');
            if (sp > 0)
            {
                propName = raw[..sp].Trim();
                if (!int.TryParse(raw[(sp + 1)..].Trim(), out maxLen) || maxLen <= 0)
                    maxLen = 1;
            }
            else
            {
                propName = raw;
            }

            SendInputPromptGump(target, propName, maxLen);
            return true;
        }

        if (upper == "TRYSRC")
        {
            // Source-X compatibility: execute the provided verb line against SRC,
            // but never fail the caller when the verb is missing.
            string payload = args.Trim();
            if (payload.Length == 0)
                return true;

            if (payload[0] is '.' or '/')
                payload = payload[1..].TrimStart();
            // Proper TRYSRC semantics:
            //   TRYSRC <srcRef> <verb...>
            // where <srcRef> can be UID/REF/etc. Examples from scripts:
            //   TRYSRC <UID> DIALOGCLOSE d_spawn
            //   TRYSRC <REF2> EFFECT 0,i_fx_fireball,10,16,0,044,4
            // If the first token resolves to an object reference, execute
            // the remaining command line against that object. Otherwise,
            // keep the legacy fallback and run the whole payload as a GM
            // command line.
            int firstSpace = payload.IndexOf(' ');
            if (firstSpace > 0)
            {
                string srcRefToken = payload[..firstSpace].Trim();
                string rest = payload[(firstSpace + 1)..].Trim();
                if (rest.Length > 0 && TryFindObjectByScriptRef(srcRefToken, out var srcRefObj))
                {
                    int cmdSpace = rest.IndexOf(' ');
                    string subCmd = cmdSpace > 0 ? rest[..cmdSpace] : rest;
                    string subArg = cmdSpace > 0 ? rest[(cmdSpace + 1)..].Trim() : "";
                    if (subCmd.Length > 0)
                    {
                        if (srcRefObj.TrySetProperty(subCmd, subArg))
                            return true;
                        if (srcRefObj.TryExecuteCommand(subCmd, subArg, this))
                            return true;
                        _ = TryExecuteScriptCommand(srcRefObj, subCmd, subArg, triggerArgs);
                    }
                    return true;
                }
            }

            if (_commands != null)
            {
                _ = _commands.TryExecute(_character, payload);
                return true;
            }

            string fallbackCmd = payload;
            int fallbackSpace = fallbackCmd.IndexOf(' ');
            string cmd2 = fallbackSpace > 0 ? fallbackCmd[..fallbackSpace] : fallbackCmd;
            string arg2 = fallbackSpace > 0 ? fallbackCmd[(fallbackSpace + 1)..].Trim() : "";
            IScriptObj srcObj = triggerArgs?.Source ?? target;
            if (cmd2.Length > 0)
            {
                if (srcObj.TrySetProperty(cmd2, arg2))
                    return true;
                if (srcObj.TryExecuteCommand(cmd2, arg2, this))
                    return true;
                _ = TryExecuteScriptCommand(srcObj, cmd2, arg2, triggerArgs);
            }
            return true;
        }

        if (upper is "TARGETF" or "TARGETFG")
        {
            string[] parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return true;
            if (_targetCursorActive)
                return true;
            ClearPendingTargetState();
            _pendingTargetFunction = parts[0];
            _pendingTargetArgs = parts.Length > 1 ? parts[1].Trim() : "";
            _pendingTargetAllowGround = upper == "TARGETFG";
            _pendingTargetItemUid = target is Item ti ? ti.Uid : Serial.Invalid;
            _targetCursorActive = true;
            byte tType = (byte)(upper == "TARGETFG" ? 1 : 0);
            _netState.Send(new PacketTarget(tType, (uint)Random.Shared.Next(1, int.MaxValue)));
            return true;
        }

        if (upper is "TARGET" or "TARGETG")
        {
            if (_targetCursorActive)
                return true;
            ClearPendingTargetState();
            _pendingTargetAllowGround = upper == "TARGETG";
            _targetCursorActive = true;
            byte tType = (byte)(upper == "TARGETG" ? 1 : 0);
            _netState.Send(new PacketTarget(tType, (uint)Random.Shared.Next(1, int.MaxValue)));
            return true;
        }

        if (upper == "MENU")
        {
            string menuDefname = args.Trim();
            if (string.IsNullOrWhiteSpace(menuDefname))
            {
                _logger.LogWarning("[menu] MENU command with no argument");
                return true;
            }

            if (!TryFindMenuSection(menuDefname, out var menuSection))
            {
                _logger.LogWarning("[menu] Section [MENU {Defname}] not found", menuDefname);
                return true;
            }

            // Parse the MENU section:
            //   First key = title/question
            //   ON=0 text          → text-based item (modelId=0, hue=0)
            //   ON=baseid text     → item-based
            //   ON=baseid @hue, text → item-based with hue
            //   Lines after ON until next ON = script to execute

            var keys = menuSection.Keys;
            if (keys.Count == 0)
            {
                _logger.LogWarning("[menu] Empty MENU section {Defname}", menuDefname);
                return true;
            }

            string question = keys[0].Arg.Length > 0 ? $"{keys[0].Key} {keys[0].Arg}" : keys[0].Key;
            var options = new List<MenuOptionEntry>();
            MenuOptionEntry? current = null;

            for (int i = 1; i < keys.Count; i++)
            {
                var k = keys[i];
                if (k.Key.StartsWith("ON", StringComparison.OrdinalIgnoreCase) && k.Key.Length == 2)
                {
                    // Flush previous option
                    if (current != null) options.Add(current);

                    // Parse: ON=baseid text  or  ON=baseid @hue, text  or  ON=0 text
                    string onArg = k.Arg.Trim();
                    ushort modelId = 0;
                    ushort hue = 0;
                    string text = "";

                    int firstSpace = onArg.IndexOf(' ');
                    if (firstSpace < 0)
                    {
                        // ON=baseid with no text
                        _ = ushort.TryParse(onArg, System.Globalization.NumberStyles.HexNumber, null, out modelId);
                    }
                    else
                    {
                        string idPart = onArg[..firstSpace].Trim();
                        string rest = onArg[(firstSpace + 1)..].Trim();

                        if (idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || idPart.StartsWith("0", StringComparison.OrdinalIgnoreCase))
                            _ = ushort.TryParse(idPart.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? idPart[2..] : idPart, System.Globalization.NumberStyles.HexNumber, null, out modelId);
                        else
                            _ = ushort.TryParse(idPart, out modelId);

                        // Check for @hue prefix: @hue, text  or  @hue text
                        if (rest.StartsWith('@'))
                        {
                            int comma = rest.IndexOf(',');
                            int space = rest.IndexOf(' ');
                            int sep = comma >= 0 ? comma : space;
                            if (sep > 1)
                            {
                                string huePart = rest[1..sep];
                                _ = ushort.TryParse(huePart, System.Globalization.NumberStyles.HexNumber, null, out hue);
                                text = rest[(sep + 1)..].TrimStart(' ', ',');
                            }
                            else
                            {
                                text = rest;
                            }
                        }
                        else
                        {
                            text = rest;
                        }
                    }

                    current = new MenuOptionEntry(modelId, hue, text, []);
                }
                else if (current != null)
                {
                    // Script line belonging to current ON block
                    current.Script.Add(k);
                }
            }
            if (current != null) options.Add(current);

            if (options.Count == 0)
            {
                _logger.LogWarning("[menu] MENU {Defname} has no ON entries", menuDefname);
                return true;
            }

            // Store pending state
            _pendingMenuId = (ushort)(Math.Abs(menuDefname.GetHashCode()) & 0xFFFF);
            _pendingMenuDefname = menuDefname;
            _pendingMenuOptions = options;

            // Build and send 0x7C packet
            var items = new List<MenuItemEntry>(options.Count);
            foreach (var opt in options)
                items.Add(new MenuItemEntry(opt.ModelId, opt.Hue, opt.Text));

            _netState.Send(new PacketMenuDisplay(_character.Uid.Value, _pendingMenuId, question, items));
            return true;
        }

        if (upper == "DIALOGCLOSE")
        {
            // Compatibility bridge: many scripts call DIALOGCLOSE before reopening.
            // We don't currently keep a server-side open-dialog registry, so treat as no-op.
            return true;
        }

        // SDIALOG = "send dialog", a Sphere alias for DIALOG used by some
        // shards' script packs. Accept both so imported scripts don't
        // need to be rewritten.
        if (upper == "DIALOG" || upper == "SDIALOG")
        {
            string raw = args.Trim();
            string dialogId = "script_dialog";
            string closeSpec = "";
            int requestedPage = 1;

            if (!string.IsNullOrWhiteSpace(raw))
            {
                int sep = raw.IndexOfAny([' ', ',']);
                if (sep < 0)
                {
                    dialogId = raw;
                }
                else
                {
                    dialogId = raw[..sep];
                    closeSpec = raw[(sep + 1)..].TrimStart(' ', ',');
                }
            }

            dialogId = dialogId.Trim().Trim(',', ';');
            if (string.IsNullOrWhiteSpace(dialogId))
                dialogId = "script_dialog";

            if (!string.IsNullOrWhiteSpace(closeSpec))
            {
                string[] dialogTokens = closeSpec.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (dialogTokens.Length > 0 && int.TryParse(dialogTokens[0], out int parsedPage))
                    requestedPage = parsedPage;
            }

            if (OpenNamedDialog(dialogId, requestedPage))
                return true;

            string closeFn = "";
            if (!string.IsNullOrWhiteSpace(closeSpec))
            {
                string[] tokens = closeSpec.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length > 0)
                {
                    if (tokens[0].Equals("DIALOGCLOSE", StringComparison.OrdinalIgnoreCase))
                    {
                        closeFn = tokens.Length > 1 ? tokens[1] : "";
                    }
                    else
                    {
                        closeFn = tokens[0];
                    }
                }
            }

            _pendingDialogCloseFunction = string.IsNullOrWhiteSpace(closeFn)
                ? $"f_dialogclose_{dialogId}"
                : closeFn.Trim().Trim(',', ';');
            _pendingDialogArgs = dialogId;
            string title = $"Dialog {dialogId}";

            uint gumpId = (uint)Math.Abs(dialogId.GetHashCode());
            var gump = new GumpBuilder(_character.Uid.Value, gumpId, 360, 180);
            gump.AddResizePic(0, 0, 5054, 360, 180);
            gump.AddText(20, 20, 0, title);
            gump.AddText(20, 60, 0, $"[{dialogId}]");
            gump.AddButton(140, 130, 4005, 4007, 1);
            SendGump(gump);
            return true;
        }

        if (upper == "GO" && target is Character goChar)
        {
            if (TryParsePoint(args, goChar.Position, out var dst))
            {
                _world.MoveCharacter(goChar, dst);
                if (goChar == _character)
                {
                    Resync();
                    BroadcastDrawObject(_character);
                }
            }
            return true;
        }

        if (upper == "GONAME" && target is Character goNameChar)
        {
            string targetName = args.Trim();
            if (targetName.Length > 0)
            {
                var dst = _world.GetAllObjects()
                    .OfType<Character>()
                    .FirstOrDefault(c => c.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                if (dst != null)
                {
                    _world.MoveCharacter(goNameChar, dst.Position);
                    if (goNameChar == _character)
                        Resync();
                }
            }
            return true;
        }

        if (upper == "SERV.NEWITEM")
        {
            string defName = args.Trim();
            if (_commands?.Resources == null || defName.Length == 0)
                return true;
            var rid = _commands.Resources.ResolveDefName(defName);
            if (!rid.IsValid) return true;

            var item = _world.CreateItem();
            item.BaseId = (ushort)rid.Index;
            item.Name = defName;
            _pendingScriptNewItem = item;
            return true;
        }

        if (upper.StartsWith("NEW.", StringComparison.Ordinal))
        {
            if (_pendingScriptNewItem == null) return true;
            string sub = cmd[4..].ToUpperInvariant();
            switch (sub)
            {
                case "EQUIP":
                    _character.Backpack ??= _world.CreateItem();
                    _character.Backpack.Name = "Backpack";
                    _character.Equip(_character.Backpack, Layer.Pack);
                    _character.Backpack.AddItem(_pendingScriptNewItem);
                    _pendingScriptNewItem = null;
                    return true;
                case "CONT":
                {
                    var trimmed = args.Trim();
                    if (trimmed.Length > 0 && trimmed != "-1")
                    {
                        uint cval = ObjBase.ParseHexOrDecUInt(trimmed);
                        var cont = _world.FindObject(new Serial(cval)) as Item;
                        if (cont != null) { cont.AddItem(_pendingScriptNewItem); return true; }
                    }
                    _character.Backpack ??= _world.CreateItem();
                    _character.Backpack.AddItem(_pendingScriptNewItem);
                    return true;
                }
                default:
                    _pendingScriptNewItem.TrySetProperty(sub, args);
                    return true;
            }
        }

        if (upper == "SERV.ALLCLIENTS" || upper.StartsWith("SERV.ALLCLIENTS ", StringComparison.Ordinal))
        {
            string payload = args.Trim();
            if (upper.StartsWith("SERV.ALLCLIENTS ", StringComparison.Ordinal))
                payload = cmd["SERV.ALLCLIENTS ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}");

            if (payload.StartsWith("SOUND", StringComparison.OrdinalIgnoreCase))
            {
                string[] ps = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (ps.Length >= 2 && ushort.TryParse(ps[1], out ushort snd))
                {
                    var pkt = new PacketSound(snd, _character.X, _character.Y, _character.Z);
                    BroadcastNearby?.Invoke(_character.Position, 9999, pkt, 0);
                }
            }
            else if (payload.Length > 0)
            {
                // Source-X parity: SERV.ALLCLIENTS <function> runs the function once
                // for each online player character as target, with SRC as current char.
                int firstSpace = payload.IndexOf(' ');
                string funcName = firstSpace > 0 ? payload[..firstSpace].Trim() : payload.Trim();
                string funcArgs = firstSpace > 0 ? payload[(firstSpace + 1)..].Trim() : "";

                var runner = _triggerDispatcher?.Runner;
                if (runner != null && funcName.Length > 0)
                {
                    foreach (var player in _world.GetAllObjects().OfType<Character>())
                    {
                        if (!player.IsPlayer || !player.IsOnline)
                            continue;

                        var callArgs = new ExecTriggerArgs(_character, 0, 0, funcArgs)
                        {
                            Object1 = player,
                            Object2 = _character
                        };

                        _ = runner.TryRunFunction(funcName, player, this, callArgs, out _);
                    }
                }
                else
                {
                    string msg = payload.StartsWith("SYSMESSAGE", StringComparison.OrdinalIgnoreCase)
                        ? payload["SYSMESSAGE".Length..].Trim()
                        : payload;
                    SysMessage(msg);
                }
            }
            else
            {
                string msg = payload.StartsWith("SYSMESSAGE", StringComparison.OrdinalIgnoreCase)
                    ? payload["SYSMESSAGE".Length..].Trim()
                    : payload;
                SysMessage(msg);
            }
            return true;
        }

        if (upper == "SERV.LOG" || upper.StartsWith("SERV.LOG ", StringComparison.Ordinal))
        {
            string msg = upper == "SERV.LOG" ? args : cmd["SERV.LOG ".Length..] + (string.IsNullOrEmpty(args) ? "" : $" {args}");
            _logger.LogInformation("[SCRIPT] {Message}", msg.Trim());
            return true;
        }

        if (upper == "BANKSELF")
        {
            OpenBankBox();
            return true;
        }

        if (upper == "BUY")
        {
            // Vendor buy — placeholder until full vendor buy/sell packet support
            return true;
        }

        if (upper == "BYE")
        {
            // End NPC interaction
            return true;
        }

        if (upper.StartsWith("SERV.", StringComparison.Ordinal))
        {
            // Known but not yet fully implemented service verbs should not crash scripts.
            _logger.LogDebug("Script SERV verb not fully implemented: {Verb} {Args}", key, args);
            return true;
        }

        if (upper.StartsWith("FILE.", StringComparison.Ordinal))
        {
            if (_scriptFile == null)
            {
                _logger.LogWarning("FILE commands not enabled (OF_FileCommands not set in OptionFlags).");
                return true;
            }

            string fileVerb = upper.Length > 5 ? upper[5..] : "";
            switch (fileVerb)
            {
                case "OPEN":
                    _scriptFile.Open(args);
                    return true;
                case "CLOSE":
                    _scriptFile.Close();
                    return true;
                case "WRITE":
                    _scriptFile.Write(args);
                    return true;
                case "WRITELINE":
                    _scriptFile.WriteLine(args);
                    return true;
                case "WRITECHR":
                    if (int.TryParse(args, out int chrVal))
                        _scriptFile.WriteChr(chrVal);
                    return true;
                case "FLUSH":
                    _scriptFile.Flush();
                    return true;
                case "DELETEFILE":
                    ScriptFileHandle.DeleteFile(_scriptFile.FilePath != "" ? Path.GetDirectoryName(_scriptFile.FilePath) ?? "" : "", args);
                    return true;
                case "MODE.APPEND":
                    _scriptFile.ModeAppend = args != "0";
                    return true;
                case "MODE.CREATE":
                    _scriptFile.ModeCreate = args != "0";
                    return true;
                case "MODE.READFLAG":
                    _scriptFile.ModeRead = args != "0";
                    return true;
                case "MODE.WRITEFLAG":
                    _scriptFile.ModeWrite = args != "0";
                    return true;
                case "MODE.SETDEFAULT":
                    _scriptFile.SetModeDefault();
                    return true;
            }
            return true;
        }

        if (upper.StartsWith("DB.", StringComparison.Ordinal))
        {
            if (_scriptDb == null)
            {
                _logger.LogWarning("DB adapter is not configured for script runtime.");
                return true;
            }

            string dbVerb = upper.Length > 3 ? upper[3..] : "";
            switch (dbVerb)
            {
                case "CONNECT":
                {
                    bool ok;
                    string err;
                    string trimmed = args.Trim();
                    string[] dbArgs = trimmed.Split('|', 2, StringSplitOptions.TrimEntries);
                    if (dbArgs.Length == 2)
                        ok = _scriptDb.Connect(dbArgs[0], dbArgs[1], out err);
                    else if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.Contains('='))
                        ok = _scriptDb.Connect(trimmed, out err);
                    else
                        ok = _scriptDb.ConnectDefault(out err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                {
                    string name = args.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        _scriptDb.Close();
                    else if (name.Equals("*", StringComparison.Ordinal))
                        _scriptDb.CloseAll();
                    else
                        _scriptDb.Close(name);
                    return true;
                }
                case "SELECT":
                {
                    string name = args.Trim();
                    if (!_scriptDb.Select(name, out string err))
                        SysMessage(err);
                    return true;
                }
                case "QUERY":
                {
                    bool ok = _scriptDb.Query(args, out int rows, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("DB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    bool ok = _scriptDb.Execute(args, out int affected, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("DB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        if (upper.StartsWith("LDB.", StringComparison.Ordinal))
        {
            if (_scriptLdb == null)
            {
                _logger.LogWarning("SQLite (LDB) adapter is not configured for script runtime.");
                return true;
            }

            string ldbVerb = upper.Length > 4 ? upper[4..] : "";
            switch (ldbVerb)
            {
                case "CONNECT":
                {
                    string fileName = args.Trim();
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        SysMessage("LDB.CONNECT requires a filename.");
                        return true;
                    }
                    if (!_scriptLdb.ConnectFile(fileName, out string err))
                        SysMessage(ServerMessages.GetFormatted("db_connect_fail", err));
                    return true;
                }
                case "CLOSE":
                {
                    _scriptLdb.Close();
                    return true;
                }
                case "QUERY":
                {
                    bool ok = _scriptLdb.Query(args, out int rows, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_query_fail", err));
                    else
                        _logger.LogDebug("LDB.QUERY returned {Rows} rows", rows);
                    return true;
                }
                case "EXECUTE":
                {
                    bool ok = _scriptLdb.Execute(args, out int affected, out string err);
                    if (!ok)
                        SysMessage(ServerMessages.GetFormatted("db_execute_fail", err));
                    else
                        _logger.LogDebug("LDB.EXECUTE affected {Rows} rows", affected);
                    return true;
                }
            }
            return true;
        }

        return false;
    }

    public bool TryResolveScriptVariable(string varName, IScriptObj target, ITriggerArgs? triggerArgs, out string value)
    {
        value = "";
        if (_character == null) return false;

        // Common Sphere runtime constants used by admin/dialog scripts.
        // GETREFTYPE — match Source-X [DEFNAME ref_types] bit layout so
        // <GetRefType> == <Def.TRef_Char> works straight from script.
        if (varName.Equals("GETREFTYPE", StringComparison.OrdinalIgnoreCase))
        {
            if (target is SphereNet.Game.Objects.Items.Item)
                value = "0" + 0x080000.ToString("X");
            else if (target is SphereNet.Game.Objects.Characters.Character)
                value = "0" + 0x040000.ToString("X");
            else
                value = "0" + 0x010000.ToString("X");
            return true;
        }

        // Generic DEF.X / DEF0.X lookup — covers everything in a [DEFNAME ...]
        // section (admin_hidehighpriv, admin_flag_1, tcolor_orange, …). Admin
        // dialogs hit these for virtually every label; without this every
        // <Def.X> fell back to unresolved = empty string, leaving the gump
        // full of gaps.
        if (varName.StartsWith("DEF.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DEF0.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = varName.IndexOf('.');
            string defKey = varName[(dot + 1)..];
            bool asNumeric = varName[..dot].Equals("DEF0", StringComparison.OrdinalIgnoreCase);

            if (_commands?.Resources != null)
            {
                // String defs (admin_flag_X = "Invulnerability", etc.)
                if (_commands.Resources.TryGetDefValue(defKey, out string defVal))
                {
                    value = defVal;
                    return true;
                }
                // Numeric defs (Admin_Hidehighpriv 1) — stored as ResourceId index.
                var rid = _commands.Resources.ResolveDefName(defKey);
                if (rid.IsValid)
                {
                    value = asNumeric
                        ? rid.Index.ToString()
                        : $"0{rid.Index:X}"; // <Def.X> legacy-hex form
                    return true;
                }
            }
            value = "0";
            return true; // answered as "0" rather than unresolved — matches Sphere behaviour
        }
        if (varName.StartsWith("ISDIALOGOPEN.", StringComparison.OrdinalIgnoreCase))
        {
            // We currently don't track open dialogs server-side; keep compatibility checks false.
            value = "0";
            return true;
        }

        if (varName.StartsWith("FILE.", StringComparison.OrdinalIgnoreCase))
        {
            if (_scriptFile == null)
            {
                value = "0";
                return true;
            }

            string fileProp = varName[5..].ToUpperInvariant();
            switch (fileProp)
            {
                case "OPEN":
                {
                    // FILE.OPEN as read property — returns "1" if file is open
                    value = _scriptFile.IsOpen ? "1" : "0";
                    return true;
                }
                case "INUSE":
                    value = _scriptFile.IsOpen ? "1" : "0";
                    return true;
                case "ISEOF":
                    value = _scriptFile.IsEof ? "1" : "0";
                    return true;
                case "FILEPATH":
                    value = _scriptFile.FilePath;
                    return true;
                case "POSITION":
                    value = _scriptFile.Position.ToString();
                    return true;
                case "LENGTH":
                    value = _scriptFile.Length.ToString();
                    return true;
                case "READCHAR":
                    value = _scriptFile.ReadChar();
                    return true;
                case "READBYTE":
                    value = _scriptFile.ReadByte();
                    return true;
                case "MODE.APPEND":
                    value = _scriptFile.ModeAppend ? "1" : "0";
                    return true;
                case "MODE.CREATE":
                    value = _scriptFile.ModeCreate ? "1" : "0";
                    return true;
                case "MODE.READFLAG":
                    value = _scriptFile.ModeRead ? "1" : "0";
                    return true;
                case "MODE.WRITEFLAG":
                    value = _scriptFile.ModeWrite ? "1" : "0";
                    return true;
                default:
                    // FILE.READLINE n, FILE.SEEK pos, FILE.FILELINES path, FILE.FILEEXIST path
                    if (fileProp.StartsWith("READLINE"))
                    {
                        string lineArg = fileProp.Length > 8 ? fileProp[8..].Trim() : "";
                        // Argument may also come after space: FILE.READLINE 3
                        if (string.IsNullOrEmpty(lineArg) && varName.Length > 13)
                            lineArg = varName[13..].Trim();
                        int lineNum = 0;
                        if (!string.IsNullOrEmpty(lineArg))
                            int.TryParse(lineArg, out lineNum);
                        value = _scriptFile.ReadLine(lineNum);
                        return true;
                    }
                    if (fileProp.StartsWith("SEEK"))
                    {
                        string seekArg = fileProp.Length > 4 ? fileProp[4..].Trim() : "";
                        if (string.IsNullOrEmpty(seekArg) && varName.Length > 9)
                            seekArg = varName[9..].Trim();
                        _scriptFile.Seek(seekArg);
                        value = _scriptFile.Position.ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILELINES"))
                    {
                        string flArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(flArg) && varName.Length > 14)
                            flArg = varName[14..].Trim();
                        value = ScriptFileHandle.GetFileLines(
                            Path.GetDirectoryName(_scriptFile.FilePath) ?? "", flArg).ToString();
                        return true;
                    }
                    if (fileProp.StartsWith("FILEEXIST"))
                    {
                        string feArg = fileProp.Length > 9 ? fileProp[9..].Trim() : "";
                        if (string.IsNullOrEmpty(feArg) && varName.Length > 14)
                            feArg = varName[14..].Trim();
                        value = ScriptFileHandle.FileExists(
                            Path.GetDirectoryName(_scriptFile.FilePath) ?? "", feArg) ? "1" : "0";
                        return true;
                    }
                    break;
            }
            value = "0";
            return true;
        }

        if (varName.Equals("DB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptDb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (varName.StartsWith("DB.CONNECTED.", StringComparison.OrdinalIgnoreCase) && _scriptDb != null)
        {
            string connName = varName[13..];
            value = _scriptDb.IsConnected_Named(connName) ? "1" : "0";
            return true;
        }
        if (varName.Equals("DB.ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptDb?.ActiveSessionName ?? "";
            return true;
        }
        if (varName.StartsWith("DB.ESCAPEDATA.", StringComparison.OrdinalIgnoreCase) && _scriptDb != null)
        {
            string rawData = varName[14..];
            value = _scriptDb.EscapeData(rawData);
            return true;
        }
        if (_scriptDb != null && _scriptDb.TryResolveRowValue(varName, out string dbVal))
        {
            value = dbVal;
            return true;
        }
        if (varName.Equals("LDB.CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            value = _scriptLdb?.IsConnected == true ? "1" : "0";
            return true;
        }
        if (_scriptLdb != null && varName.StartsWith("LDB.ROW.", StringComparison.OrdinalIgnoreCase))
        {
            string ldbKey = "db.row." + varName[8..];
            if (_scriptLdb.TryResolveRowValue(ldbKey, out string ldbVal))
            {
                value = ldbVal;
                return true;
            }
        }
        if (varName.StartsWith("ACCOUNT.", StringComparison.OrdinalIgnoreCase))
        {
            if (_account != null && _account.TryGetProperty(varName["ACCOUNT.".Length..], out string acctVal))
            {
                value = acctVal;
                return true;
            }
            return false;
        }

        if (varName.Equals("TARGP", StringComparison.OrdinalIgnoreCase))
        {
            var p = _lastScriptTargetPoint ?? _character.Position;
            value = $"{p.X},{p.Y},{p.Z},{p.Map}";
            return true;
        }

        if (varName.StartsWith("CTAG0.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("CTAG.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DCTAG0.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("DCTAG.", StringComparison.OrdinalIgnoreCase))
        {
            int dot = varName.IndexOf('.');
            if (dot > 0)
            {
                string tagName = varName[(dot + 1)..].Trim().Trim(',', ';');
                string? tagVal = _character.CTags.Get(tagName);
                if (tagVal != null)
                {
                    value = tagVal;
                    return true;
                }
            }
            return false;
        }

        int objDot = varName.IndexOf('.');
        if (objDot > 0)
        {
            string root = varName[..objDot].Trim();
            string prop = varName[(objDot + 1)..].Trim();
            if (_character.TryGetProperty($"TAG.{root}", out string objRef) && TryFindObjectByScriptRef(objRef, out var scopedObj))
            {
                if (scopedObj.TryGetProperty(prop, out string scopedVal))
                {
                    value = scopedVal;
                    return true;
                }
            }
        }

        if (varName.StartsWith("ARGO.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("ACT.", StringComparison.OrdinalIgnoreCase) ||
            varName.StartsWith("LINK.", StringComparison.OrdinalIgnoreCase))
        {
            IScriptObj? obj = null;
            int dot = varName.IndexOf('.');
            string root = dot > 0 ? varName[..dot].ToUpperInvariant() : varName.ToUpperInvariant();
            string sub = dot > 0 ? varName[(dot + 1)..] : "";
            if (root == "ARGO") obj = triggerArgs?.Object1;
            else if (root is "ACT" or "LINK") obj = triggerArgs?.Object2;

            if (obj == null) return false;

            if (sub.StartsWith("ACCOUNT.", StringComparison.OrdinalIgnoreCase) && obj is Character chAcct)
            {
                var acct = Character.ResolveAccountForChar?.Invoke(chAcct.Uid);
                if (acct != null && acct.TryGetProperty(sub["ACCOUNT.".Length..], out string acctVal))
                {
                    value = acctVal;
                    return true;
                }
                return false;
            }
            if (obj.TryGetProperty(sub, out string propVal))
            {
                value = propVal;
                return true;
            }
        }

        // Resolve object-scoped locals like OBJ.ISPLAYER where OBJ contains a UID string.
        int localDot = varName.IndexOf('.');
        if (localDot > 0)
        {
            string localName = varName[..localDot];
            if (triggerArgs != null && target.TryGetProperty($"TAG.{localName}", out string tagVal) && TryFindObjectByScriptRef(tagVal, out var refObj))
            {
                if (refObj.TryGetProperty(varName[(localDot + 1)..], out string scopedVal))
                {
                    value = scopedVal;
                    return true;
                }
            }
        }

        // Bare defname constants for general script execution paths
        // (outside dialog render), e.g. <statf_insubstantial>.
        if (_commands?.Resources != null && IsPlainDefToken(varName))
        {
            var rid = _commands.Resources.ResolveDefName(varName);
            if (rid.IsValid)
            {
                value = rid.Index.ToString();
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<IScriptObj> QueryScriptObjects(string query, IScriptObj target, string args, ITriggerArgs? triggerArgs)
    {
        if (_character == null) return Array.Empty<IScriptObj>();

        if (query.Equals("FORPLAYERS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => c.IsPlayer && c.MapIndex == _character.MapIndex &&
                            c.Position.GetDistanceTo(_character.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        if (query.Equals("FORINSTANCES", StringComparison.OrdinalIgnoreCase))
        {
            string def = args.Trim();
            if (def.Length == 0) return Array.Empty<IScriptObj>();

            int? itemBase = null;
            int? charBase = null;
            var rid = _commands?.Resources?.ResolveDefName(def) ?? ResourceId.Invalid;
            if (rid.IsValid)
            {
                if (rid.Type == Core.Enums.ResType.ItemDef) itemBase = rid.Index;
                else if (rid.Type == Core.Enums.ResType.CharDef) charBase = rid.Index;
            }
            else if (int.TryParse(def.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out int parsed))
            {
                itemBase = parsed;
                charBase = parsed;
            }

            return _world.GetAllObjects()
                .Where(o =>
                    (o is Item it && itemBase.HasValue && it.BaseId == (ushort)itemBase.Value) ||
                    (o is Character ch && charBase.HasValue && ch.BaseId == (ushort)charBase.Value))
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORCHARS — all characters (players + NPCs) within radius
        if (query.Equals("FORCHARS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => !c.IsDeleted && c.MapIndex == map &&
                            center.GetDistanceTo(c.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORCLIENTS — only online player characters within radius
        if (query.Equals("FORCLIENTS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Character>()
                .Where(c => c.IsPlayer && c.IsOnline && !c.IsDeleted &&
                            c.MapIndex == map && center.GetDistanceTo(c.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FORITEMS — all ground items within radius
        if (query.Equals("FORITEMS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            return _world.GetAllObjects()
                .OfType<Item>()
                .Where(it => !it.IsDeleted && it.IsOnGround &&
                             it.MapIndex == map && center.GetDistanceTo(it.Position) <= range)
                .Cast<IScriptObj>()
                .ToList();
        }

        // FOROBJS — all characters + items within radius
        if (query.Equals("FOROBJS", StringComparison.OrdinalIgnoreCase))
        {
            int range = 18;
            _ = int.TryParse(args, out range);
            range = Math.Clamp(range, 1, 9999);
            var center = (target as ObjBase)?.Position ?? _character.Position;
            byte map = (target as ObjBase)?.MapIndex ?? _character.MapIndex;
            var result = new List<IScriptObj>();
            foreach (var obj in _world.GetAllObjects())
            {
                if (obj.IsDeleted) continue;
                if (obj.MapIndex != map) continue;
                if (center.GetDistanceTo(obj.Position) > range) continue;
                if (obj is Item it && !it.IsOnGround) continue;
                result.Add(obj);
            }
            return result;
        }

        // FORCONT — all items inside a container (args: "uid [depth]")
        if (query.Equals("FORCONT", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            if (!TryFindObjectByScriptRef(parts[0], out var contObj) || contObj is not Item container)
                return Array.Empty<IScriptObj>();
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result);
            return result;
        }

        // FORCONTID — items in current target's backpack matching a BASEID (args: "baseid [depth]")
        if (query.Equals("FORCONTID", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            string defName = parts[0];
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            ushort? targetBaseId = ResolveBaseId(defName);
            if (!targetBaseId.HasValue) return Array.Empty<IScriptObj>();

            // Iterate the target character's backpack, or the target item as container
            Item? container = target is Character ch ? ch.Backpack : target as Item;
            if (container == null) return Array.Empty<IScriptObj>();
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result, baseIdFilter: targetBaseId.Value);
            return result;
        }

        // FORCONTTYPE — items in current target's backpack matching a TYPE (args: "type [depth]")
        if (query.Equals("FORCONTTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return Array.Empty<IScriptObj>();
            string typeName = parts[0];
            int depth = parts.Length > 1 && int.TryParse(parts[1], out int d) ? d : 0;
            int? typeFilter = ResolveItemType(typeName);
            if (!typeFilter.HasValue) return Array.Empty<IScriptObj>();

            Item? container = target is Character ch ? ch.Backpack : target as Item;
            if (container == null) return Array.Empty<IScriptObj>();
            var result = new List<IScriptObj>();
            CollectContainerItems(container, depth, result, typeFilter: typeFilter.Value);
            return result;
        }

        // FORCHARLAYER — items on a specific equipment layer of the target character
        if (query.Equals("FORCHARLAYER", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(args.Trim(), out int layerNum)) return Array.Empty<IScriptObj>();
            Character? ch = target as Character ?? _character;
            var item = ch.GetEquippedItem((Layer)layerNum);
            if (item == null) return Array.Empty<IScriptObj>();
            // Layer 30 (Special) can contain multiple memory items; single item for other layers
            return new List<IScriptObj> { item };
        }

        if (query.Equals("FORCHARMEMORYTYPE", StringComparison.OrdinalIgnoreCase))
        {
            Character? ch = target as Character ?? _character;
            if (ch == null)
                return Array.Empty<IScriptObj>();
            return ch.GetMemoryEntriesByType(args);
        }

        return Array.Empty<IScriptObj>();
    }

    private void CollectContainerItems(Item container, int depth, List<IScriptObj> result,
        ushort? baseIdFilter = null, int? typeFilter = null)
    {
        foreach (var item in container.Contents)
        {
            if (item.IsDeleted) continue;
            bool matches = true;
            if (baseIdFilter.HasValue && item.BaseId != baseIdFilter.Value) matches = false;
            if (typeFilter.HasValue && (int)item.ItemType != typeFilter.Value) matches = false;
            if (matches) result.Add(item);
            if (depth > 0 && item.ContentCount > 0)
                CollectContainerItems(item, depth - 1, result, baseIdFilter, typeFilter);
        }
    }

    private ushort? ResolveBaseId(string defName)
    {
        var rid = _commands?.Resources?.ResolveDefName(defName) ?? ResourceId.Invalid;
        if (rid.IsValid) return (ushort)rid.Index;
        if (ushort.TryParse(defName.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, null, out ushort v))
            return v;
        return null;
    }

    private int? ResolveItemType(string typeName)
    {
        // Try as enum name (e.g. "t_spellbook" → strip "t_" prefix, parse as ItemType)
        string name = typeName.TrimStart();
        if (name.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            name = name[2..];
        if (Enum.TryParse<Core.Enums.ItemType>(name, ignoreCase: true, out var itemType))
            return (int)itemType;
        // Try as numeric
        if (int.TryParse(typeName, out int num))
            return num;
        return null;
    }

    private bool TryFindObjectByScriptRef(string value, out IScriptObj obj)
    {
        obj = null!;
        string v = value.Trim();
        if (v.StartsWith("0", StringComparison.OrdinalIgnoreCase))
            v = v[1..];
        if (!uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint uid))
            return false;
        var found = _world.FindObject(new Serial(uid));
        if (found == null) return false;
        obj = found;
        return true;
    }

    private string ResolveMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _defMessageLookup == null)
            return text;

        string key = text.Trim();
        if (key.StartsWith('@'))
            key = key[1..];
        if (key.StartsWith("DEFMSG.", StringComparison.OrdinalIgnoreCase))
            key = key[7..];
        if (key.Contains(' '))
            return text;

        return _defMessageLookup(key) ?? text;
    }

    private static bool TryParsePoint(string args, Point3D current, out Point3D point)
    {
        point = current;
        var parts = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        if (!short.TryParse(parts[0], out short x) || !short.TryParse(parts[1], out short y))
            return false;
        sbyte z = parts.Length > 2 && sbyte.TryParse(parts[2], out sbyte tz) ? tz : current.Z;
        byte map = parts.Length > 3 && byte.TryParse(parts[3], out byte tm) ? tm : current.Map;
        point = new Point3D(x, y, z, map);
        return true;
    }

    private static string EscapeHtml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    // ==================== Phase 1: Critical Stability Handlers ====================
}
