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
    /// Fires trigger chain (PreStart/Start/Stroke) for the information skill,
    /// then asks the client for a target cursor. Selected target is resolved
    /// to the actual Character/Item and pushed into <see cref="SkillHandlers.UseInfoSkill"/>.
    /// </summary>
    private void BeginInfoSkill(SkillType skill, int skillId)
    {
        if (_character == null) return;

        if (_triggerDispatcher != null)
        {
            var pre = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (pre == TriggerResult.True) return;

            var start = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (start == TriggerResult.True) return;
        }

        SysMessage($"What do you wish to use your {skill} skill on?");
        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;

            var uid = new Serial(serial);
            Objects.ObjBase? target = uid.IsValid ? _world.FindObject(uid) : null;

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new InfoSkillSink(this, _character);
            bool ok = _skillHandlers?.UseInfoSkill(sink, skill, target) ?? false;

            if (_triggerDispatcher != null)
            {
                var trigger = ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail;
                _triggerDispatcher.FireCharTrigger(_character, trigger,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
        });
    }

    /// <summary>
    /// Active-skill driver. Skills with <see cref="SkillHandlers.ActiveSkillTargetKind.None"/>
    /// run immediately (Hiding, Meditation, ...). Character/Item-target skills
    /// open a target cursor and resolve the picked Serial via the world before
    /// invoking <see cref="SkillHandlers.UseActiveSkill"/>. Trigger chain
    /// (PreStart/Start/Stroke/Success/Fail) is preserved.
    /// </summary>
    private void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind)
    {
        if (_character == null) return;

        if (_triggerDispatcher != null)
        {
            var pre = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (pre == TriggerResult.True) return;

            var start = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (start == TriggerResult.True) return;
        }

        // No-target path: fire stroke, run engine, fire success/fail.
        if (kind == SkillHandlers.ActiveSkillTargetKind.None)
        {
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink0 = new InfoSkillSink(this, _character);
            bool ok0 = _skillHandlers?.UseActiveSkill(sink0, skill, null) ?? false;

            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(_character,
                    ok0 ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
            return;
        }

        // Menu path: show category selection gump (Tracking).
        if (kind == SkillHandlers.ActiveSkillTargetKind.Menu)
        {
            ShowTrackingMenu(skill, skillId);
            return;
        }

        // Target-required path.
        SysMessage(kind switch
        {
            SkillHandlers.ActiveSkillTargetKind.Item => $"What item do you wish to use your {skill} skill on?",
            SkillHandlers.ActiveSkillTargetKind.Ground => $"Where do you wish to use your {skill} skill?",
            _ => $"Whom do you wish to use your {skill} skill on?"
        });

        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;

            var uid = new Serial(serial);
            Objects.ObjBase? target = uid.IsValid ? _world.FindObject(uid) : null;
            var point = new Point3D(x, y, z);

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new InfoSkillSink(this, _character);
            bool ok = _skillHandlers?.UseActiveSkill(sink, skill, target, point) ?? false;

            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(_character,
                    ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
        });
    }

    private void ShowTrackingMenu(SkillType skill, int skillId)
    {
        if (_character == null) return;

        var gump = new GumpBuilder(_character.Uid.Value, 0, 300, 220);
        gump.AddResizePic(0, 0, 5054, 300, 220);
        gump.AddText(30, 15, 0, "What do you wish to track?");
        gump.AddButton(30, 55, 4005, 4007, 1);
        gump.AddText(70, 55, 0, "Animals");
        gump.AddButton(30, 85, 4005, 4007, 2);
        gump.AddText(70, 85, 0, "Monsters");
        gump.AddButton(30, 115, 4005, 4007, 3);
        gump.AddText(70, 115, 0, "Humans");
        gump.AddButton(150, 175, 4017, 4019, 0);
        gump.AddText(190, 175, 0, "Cancel");

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (_character == null || buttonId == 0) return;

            var category = buttonId switch
            {
                1 => Skills.Information.ActiveSkillEngine.TrackingCategory.Animals,
                2 => Skills.Information.ActiveSkillEngine.TrackingCategory.Monsters,
                3 => Skills.Information.ActiveSkillEngine.TrackingCategory.Humans,
                _ => Skills.Information.ActiveSkillEngine.TrackingCategory.Animals,
            };

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new InfoSkillSink(this, _character);
            bool ok = Skills.Information.ActiveSkillEngine.Tracking(sink, category);

            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(_character,
                    ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
        });
    }

    /// <summary>
    /// Glue between the skill engines and the client's network layer.
    /// Implements both <see cref="Skills.Information.IInfoSkillSink"/> and
    /// <see cref="Skills.Information.IActiveSkillSink"/> so the engines can
    /// emit overhead text, emote poses, sounds, and consume backpack items.
    /// </summary>
    private sealed class InfoSkillSink : Skills.Information.IActiveSkillSink
    {
        private readonly GameClient _client;
        public InfoSkillSink(GameClient client, Character self) { _client = client; Self = self; }
        public Character Self { get; }
        public Random Random => System.Random.Shared;
        public Game.World.GameWorld World => _client._world;

        public void SysMessage(string text) => _client.SysMessage(text);
        public void ObjectMessage(Objects.ObjBase target, string text) => _client.ObjectMessage(target, text);
        public void Emote(string text) => _client.NpcSpeech(Self, text);
        public void Sound(ushort soundId) =>
            _client._netState.Send(new PacketSound(soundId, (short)Self.Position.X, (short)Self.Position.Y, Self.Position.Z));

        public Item? FindBackpackItem(Core.Enums.ItemType type)
        {
            var pack = Self.Backpack;
            if (pack == null) return null;
            foreach (var it in pack.Contents)
            {
                if (it.ItemType == type) return it;
            }
            // One level deep so common pouches resolve.
            foreach (var it in pack.Contents)
            {
                if (it.ItemType is Core.Enums.ItemType.Container or Core.Enums.ItemType.ContainerLocked)
                {
                    foreach (var inner in it.Contents)
                        if (inner.ItemType == type) return inner;
                }
            }
            return null;
        }

        public void ConsumeAmount(Item item, ushort amount = 1)
        {
            if (item.Amount > amount)
            {
                item.Amount = (ushort)(item.Amount - amount);
                return;
            }
            // Drop from container.
            var holder = _client._world.FindObject(item.ContainedIn);
            if (holder is Item parent) parent.RemoveItem(item);
            item.Delete();
        }

        public void DeliverItem(Item item)
        {
            var pack = Self.Backpack;
            if (pack == null)
            {
                _client._world.PlaceItemWithDecay(item, Self.Position);
                return;
            }

            var actual = pack.AddItemWithStack(item);
            if (actual != item)
                item.Delete();

            _client._netState.Send(new PacketContainerItem(
                actual.Uid.Value, actual.DispIdFull, 0,
                actual.Amount, actual.X, actual.Y,
                pack.Uid.Value, actual.Hue,
                _client._netState.IsClientPost6017));
        }
    }

    /// <summary>Source-X addObjMessage: overhead speech over any ObjBase.</summary>
    internal void ObjectMessage(Objects.ObjBase target, string text)
    {
        uint uid;
        ushort body;
        string name;
        switch (target)
        {
            case Character ch:
                uid = ch.Uid.Value; body = ch.BodyId; name = ch.GetName();
                break;
            case Item it:
                uid = it.Uid.Value; body = it.BaseId; name = it.Name ?? "";
                break;
            default:
                SysMessage(text); return;
        }
        var packet = new PacketSpeechUnicodeOut(uid, body, 0, 0x03B2, 3, "ENU", name, text);
        _netState.Send(packet);
    }

    // ==================== Help Menu ====================

    public void HandleHelpRequest()
    {
        if (_character == null) return;

        // Script-first parity:
        // If [FUNCTION f_onclient_helppage] exists and runs, skip the built-in fallback.
        if (_triggerDispatcher?.Runner != null)
        {
            var trigArgs = new ExecTriggerArgs(_character, 0, 0, string.Empty)
            {
                Object1 = _character,
                Object2 = _character
            };
            if (_triggerDispatcher.Runner.TryRunFunction("f_onclient_helppage", _character, this, trigArgs, out _))
                return;
        }

        OpenNamedDialog("d_helppage", 1);
    }

    // ==================== AOS Tooltip ====================

    public void HandleAOSTooltip(uint serial)
    {
        if (_character == null) return;
        if (_world.ToolTipMode == 0) return;

        var obj = _world.FindObject(new Serial(serial));
        if (obj == null) return;

        var propList = new List<(uint ClilocId, string Args)>
        {
            (1050045, obj.GetName()) // generic name cliloc
        };

        // Enrich tooltips for items
        if (obj is Item item)
        {
            switch (item.ItemType)
            {
                case ItemType.WeaponMaceSmith:
                case ItemType.WeaponMaceSharp:
                case ItemType.WeaponSword:
                case ItemType.WeaponFence:
                case ItemType.WeaponBow:
                case ItemType.WeaponAxe:
                case ItemType.WeaponXBow:
                case ItemType.WeaponMaceStaff:
                case ItemType.WeaponMaceCrook:
                case ItemType.WeaponMacePick:
                case ItemType.WeaponThrowing:
                case ItemType.WeaponWhip:
                    // Weapon damage — try reading from tags or CombatEngine lookup
                    if (item.TryGetTag("DAM", out string? damStr) && damStr != null)
                        propList.Add((1061168, $"\t{damStr}")); // weapon damage cliloc
                    if (item.TryGetTag("SPEED", out string? speedStr) && speedStr != null)
                        propList.Add((1061167, $"\t{speedStr}")); // weapon speed cliloc
                    break;

                case ItemType.Armor:
                case ItemType.ArmorLeather:
                case ItemType.ArmorBone:
                case ItemType.ArmorChain:
                case ItemType.ArmorRing:
                case ItemType.Shield:
                    if (item.TryGetTag("ARMOR", out string? armorStr) && armorStr != null)
                        propList.Add((1060448, $"\t{armorStr}")); // physical resist
                    if (item.TryGetTag("DURABILITY", out string? durStr) && durStr != null)
                        propList.Add((1060639, $"\t{durStr}")); // durability
                    break;

                case ItemType.Container:
                case ItemType.ContainerLocked:
                    propList.Add((1050044, $"\t{item.ContentCount}\t125")); // items/max items
                    propList.Add((1072789, $"\t{item.TotalWeight}")); // weight
                    break;
            }
        }

        var props = propList.ToArray();
        // Deterministic hash — .NET GetHashCode() is randomized per process
        uint hash = StableStringHash(obj.GetName());
        foreach (var (clilocId, args) in props)
            hash = hash * 31 + (uint)clilocId + StableStringHash(args);

        // Skip entirely if we already sent this exact hash for this serial.
        // The client already has the tooltip data — no need to resend 0xDC.
        if (_tooltipHashCache.TryGetValue(serial, out uint cachedHash) && cachedHash == hash)
            return;
        _tooltipHashCache[serial] = hash;

        _netState.Send(new PacketOPLData(serial, hash, props));
        _netState.Send(new PacketOPLInfo(serial, hash));
    }

    // ==================== Trade ====================

    public void HandleTradeRequest(uint targetUid)
    {
        if (_character == null || _tradeManager == null) return;
        var target = _world.FindChar(new Serial(targetUid));
        if (target == null || !target.IsPlayer) return;
        InitiateTrade(target);
    }

    // ==================== Party ====================

    public void HandlePartyInvite(uint targetUid)
    {
        if (_character == null || _partyManager == null) return;
        var target = _world.FindChar(new Serial(targetUid));
        if (target == null || !target.IsPlayer) return;
        _triggerDispatcher?.FireCharTrigger(target, CharTrigger.PartyInvite,
            new TriggerArgs { CharSrc = _character });
        _partyManager.AcceptInvite(_character.Uid, target.Uid);
    }

    public void HandlePartyLeave()
    {
        if (_character == null || _partyManager == null) return;
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.PartyLeave,
            new TriggerArgs { CharSrc = _character });
        _partyManager.Leave(_character.Uid);
    }

    // ==================== Client Version ====================

    public void HandleClientVersion(string version)
    {
        _logger.LogDebug("Client version: {Ver}", version);

        // Parse version string (e.g. "7.0.20.0") into the numeric format used by NetState
        if (!string.IsNullOrEmpty(version) && _netState.ClientVersionNumber == 0)
        {
            var parts = version.Split('.');
            if (parts.Length >= 3 &&
                uint.TryParse(parts[0], out uint major) &&
                uint.TryParse(parts[1], out uint minor) &&
                uint.TryParse(parts[2], out uint rev))
            {
                uint patch = parts.Length > 3 && uint.TryParse(parts[3], out uint p) ? p : 0;
                _netState.ClientVersionNumber = major * 10_000_000 + minor * 1_000_000 + rev * 1_000 + patch;
                _logger.LogInformation("Client version detected from 0xBD: {Ver} → {Num}", version, _netState.ClientVersionNumber);
            }
        }
    }

    // ==================== Client Update Loop ====================
}
