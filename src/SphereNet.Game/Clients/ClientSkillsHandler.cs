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
/// Skills handler extracted from the GameClient.Skills partial
/// (decomposition phase 3 - see docs/GAMECLIENT_DECOMPOSITION_TR.md).
/// Info/active skill flows (target cursor, stroke loop, delayed completion,
/// tracking menu), overhead object messages, help request, AOS tooltips and
/// the small trade/party/version packet handlers. Method bodies moved
/// verbatim; the private context shims below enumerate exactly what this
/// handler needs from GameClient. The InfoSkillSink glue type stays nested
/// on GameClient (engines and other handlers construct it by that name).
/// </summary>
public sealed class ClientSkillsHandler
{
    private readonly IClientContext _client;

    internal ClientSkillsHandler(IClientContext client)
    {
        _client = client;
    }

    // --- context shims (the GameClient surface this handler depends on) ---
    private Character? _character => _client.Character;
    private GameWorld _world => _client.World;
    private NetState _netState => _client.NetState;
    private TriggerDispatcher? _triggerDispatcher => _client.Triggers;
    private SkillHandlers? _skillHandlers => _client.SkillH;
    private TradeManager? _tradeManager => _client.TradeM;
    private PartyManager? _partyManager => _client.PartyM;
    private ILogger _logger => _client.Log;
    private ClientTargetState Targets => _client.Targets;
    private ClientViewCache View => _client.View;
    private Action<Point3D, int, SphereNet.Network.Packets.PacketWriter, uint>? BroadcastNearby => _client.BroadcastNearby;
    private Action<Serial, SphereNet.Network.Packets.PacketWriter>? SendToChar => _client.SendToChar;
    private void SysMessage(string text) => _client.SysMessage(text);
    private void SetPendingTarget(Action<uint, short, short, sbyte, ushort> callback, byte cursorType = 1) => _client.SetPendingTarget(callback, cursorType);
    private void SendGump(GumpBuilder gump, Action<uint, uint[], (ushort, string)[]>? callback = null) => _client.SendGump(gump, callback);
    private void InitiateTrade(Character partner, Item? firstItem = null) => _client.InitiateTrade(partner, firstItem);
    private bool OpenNamedDialog(string dialogId, int requestedPage = 0) => _client.OpenNamedDialog(dialogId, requestedPage);
    private Item? GetTopContainer(Item item) => _client.GetTopContainer(item);
    private static uint StableStringHash(string s) => GameClient.StableStringHash(s);


    /// <summary>
    /// Fires trigger chain (PreStart/Start/Stroke) for the information skill,
    /// then asks the client for a target cursor. Selected target is resolved
    /// to the actual Character/Item and pushed into <see cref="SkillHandlers.UseInfoSkill"/>.
    /// </summary>
    internal void BeginInfoSkill(SkillType skill, int skillId)
    {
        if (_character == null || _character.HasActiveSkillPending()) return;

        var scriptProperties = new List<(uint ClilocId, string Args)>();
        _client.ScriptTooltipProperties = scriptProperties;
        if (_triggerDispatcher != null)
        {
            var pre = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (pre == TriggerResult.True) return;

            var start = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (start == TriggerResult.True) return;
        }

        SendSkillPrompt(DefinitionLoader.GetSkillDef(skillId),
            $"What do you wish to use your {skill} skill on?");
        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;
            Targets.SkillCancelId = -1;

            var uid = new Serial(serial);
            Objects.ObjBase? target = uid.IsValid ? _world.FindObject(uid) : null;

            if (TryScheduleActiveSkillDelay(skill, skillId, uid, null, isInfo: true))
                return;

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new GameClient.InfoSkillSink(_client, _character);
            bool ok = _skillHandlers?.UseInfoSkill(sink, skill, target) ?? false;

            if (_triggerDispatcher != null)
            {
                var trigger = ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail;
                _triggerDispatcher.FireCharTrigger(_character, trigger,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }
        });
        Targets.SkillCancelId = skillId;
    }

    /// <summary>
    /// Active-skill driver. Skills with <see cref="SkillHandlers.ActiveSkillTargetKind.None"/>
    /// run immediately (Hiding, Meditation, ...). Character/Item-target skills
    /// open a target cursor and resolve the picked Serial via the world before
    /// invoking <see cref="SkillHandlers.UseActiveSkill"/>. Trigger chain
    /// (PreStart/Start/Stroke/Success/Fail) is preserved.
    /// </summary>
    internal void BeginActiveSkill(SkillType skill, int skillId, SkillHandlers.ActiveSkillTargetKind kind)
    {
        if (_character == null || _character.HasActiveSkillPending()) return;
        _character.ResetSkillStrokeCount();

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
            if (TryScheduleActiveSkillDelay(skill, skillId, Serial.Invalid, null))
                return;

            FireActiveSkillStroke(skillId);
            var sink0 = new GameClient.InfoSkillSink(_client, _character);
            bool ok0 = _skillHandlers?.UseActiveSkill(sink0, skill, null) ?? false;
            FireActiveSkillResult(skillId, ok0);
            return;
        }

        // Menu path: show category selection gump (Tracking).
        if (kind == SkillHandlers.ActiveSkillTargetKind.Menu)
        {
            ShowTrackingMenu(skill, skillId);
            return;
        }

        // Target-required path.
        var def = DefinitionLoader.GetSkillDef(skillId);
        string fallbackPrompt = skill == SkillType.Poisoning
            ? "Which poison potion do you wish to use?"
            : kind switch
        {
            SkillHandlers.ActiveSkillTargetKind.Item => $"What item do you wish to use your {skill} skill on?",
            SkillHandlers.ActiveSkillTargetKind.Object => $"What do you wish to use your {skill} skill on?",
            SkillHandlers.ActiveSkillTargetKind.Ground => $"Where do you wish to use your {skill} skill?",
            _ => $"Whom do you wish to use your {skill} skill on?"
        };
        SendSkillPrompt(def, fallbackPrompt);

        SetPendingTarget((serial, x, y, z, graphic) =>
            ResolveActiveSkillTarget(skill, skillId, serial, x, y, z));
        Targets.SkillCancelId = skillId;
    }

    /// <summary>Resolve a target-required active skill against a picked target
    /// (Serial + position). Shared by the target-cursor callback and the
    /// targeted-skill fast path (0xBF 0x2E). Bard/Herding/Poisoning multi-step
    /// skills branch to their own follow-up target.</summary>
    private void ResolveActiveSkillTarget(SkillType skill, int skillId, uint serial, short x, short y, sbyte z)
    {
        if (_character == null) return;
        Targets.SkillCancelId = -1;

        var uid = new Serial(serial);
        Objects.ObjBase? target = uid.IsValid ? _world.FindObject(uid) : null;
        var point = new Point3D(x, y, z, _character.MapIndex);

        if (skill == SkillType.Herding && target is Character herdAnimal)
        {
            BeginHerdingDestination(skill, skillId, herdAnimal);
            return;
        }
        if (skill == SkillType.Provocation && target is Character provokeSource)
        {
            BeginProvocationTarget(skill, skillId, provokeSource);
            return;
        }
        if (skill == SkillType.Poisoning && target is Item poisonPotion)
        {
            BeginPoisoningTarget(skill, skillId, poisonPotion);
            return;
        }

        ResolveActiveSkill(skill, skillId, uid, target, point);
    }

    /// <summary>Targeted-skill fast path (0xBF 0x2E PacketTargetedSkill): a skill
    /// used on an already-picked target, no cursor round-trip. Fires the same
    /// PreStart/Start trigger chain, then resolves directly against the target.
    /// Non-targeting skills fall back to the normal cursor-less path.</summary>
    internal void BeginTargetedSkill(SkillType skill, int skillId, Serial targetUid)
    {
        if (_character == null || _character.HasActiveSkillPending()) return;

        var kind = SkillHandlers.GetActiveSkillTarget(skill);
        if (kind is not (SkillHandlers.ActiveSkillTargetKind.Character
                      or SkillHandlers.ActiveSkillTargetKind.Object
                      or SkillHandlers.ActiveSkillTargetKind.Item
                      or SkillHandlers.ActiveSkillTargetKind.Ground))
        {
            BeginActiveSkill(skill, skillId, kind);
            return;
        }

        _character.ResetSkillStrokeCount();
        if (_triggerDispatcher != null)
        {
            var pre = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillPreStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (pre == TriggerResult.True) return;

            var start = _triggerDispatcher.FireCharTrigger(_character, CharTrigger.SkillStart,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            if (start == TriggerResult.True) return;
        }

        var targetObj = targetUid.IsValid ? _world.FindObject(targetUid) : null;
        var pos = targetObj?.Position ?? _character.Position;
        ResolveActiveSkillTarget(skill, skillId, targetUid.Value, (short)pos.X, (short)pos.Y, (sbyte)pos.Z);
    }

    private void SendSkillPrompt(SkillDef? def, string fallback)
    {
        string prompt = def?.PromptMsg?.Trim() ?? string.Empty;
        if (prompt.Length > 0)
        {
            SysMessage(prompt);
            return;
        }
        if (uint.TryParse(def?.PromptCliloc, out uint cliloc) && cliloc > 0)
        {
            _netState.Send(new PacketClilocMessage(
                0xFFFFFFFF, 0xFFFF, 6, 0x03B2, 3, cliloc, "System", ""));
            return;
        }
        SysMessage(fallback);
    }

    private void BeginHerdingDestination(SkillType skill, int skillId, Character animal)
    {
        if (_character == null) return;
        SysMessage("Where do you wish the animal to go?");
        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;
            Targets.SkillCancelId = -1;
            _character.ActPrv = animal.Uid;
            var point = new Point3D(x, y, z, _character.MapIndex);
            ResolveActiveSkill(skill, skillId, animal.Uid, animal, point);
        });
        Targets.SkillCancelId = skillId;
    }

    private void BeginProvocationTarget(SkillType skill, int skillId, Character source)
    {
        if (_character == null) return;
        SysMessage("Whom do you wish it to attack?");
        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;
            Targets.SkillCancelId = -1;
            var uid = new Serial(serial);
            var target = uid.IsValid ? _world.FindChar(uid) : null;
            _character.ActPrv = source.Uid;
            ResolveActiveSkill(skill, skillId, uid, target,
                target?.Position ?? new Point3D(x, y, z, _character.MapIndex));
        });
        Targets.SkillCancelId = skillId;
    }

    private void BeginPoisoningTarget(SkillType skill, int skillId, Item potion)
    {
        if (_character == null) return;
        SysMessage("What item do you wish to poison?");
        SetPendingTarget((serial, x, y, z, graphic) =>
        {
            if (_character == null) return;
            Targets.SkillCancelId = -1;
            var uid = new Serial(serial);
            var target = uid.IsValid ? _world.FindItem(uid) : null;
            _character.ActPrv = potion.Uid;
            ResolveActiveSkill(skill, skillId, uid, target,
                target?.Position ?? new Point3D(x, y, z, _character.MapIndex));
        });
        Targets.SkillCancelId = skillId;
    }

    private void ResolveActiveSkill(SkillType skill, int skillId, Serial targetUid,
        Objects.ObjBase? target, Point3D? point)
    {
        if (_character == null) return;
        if (TryScheduleActiveSkillDelay(skill, skillId, targetUid, point))
            return;
        FireActiveSkillStroke(skillId);
        var sink = new GameClient.InfoSkillSink(_client, _character);
        bool ok = _skillHandlers?.UseActiveSkill(sink, skill, target, point) ?? false;
        FireActiveSkillResult(skillId, ok);
    }

    private void FireActiveSkillStroke(int skillId)
    {
        int strokeCount = _character?.IncrementSkillStrokeCount() ?? 0;
        _triggerDispatcher?.FireCharTrigger(_character!, CharTrigger.SkillStroke,
            new TriggerArgs { CharSrc = _character, N1 = skillId, N2 = strokeCount });

        if (_character != null)
        {
            ushort animId = GetSkillStrokeAnimation((SkillType)skillId);
            if (animId != 0)
                BroadcastNearby?.Invoke(_character.Position, 18,
                    new PacketAnimation(_character.Uid.Value, animId), 0);

            if ((SkillType)skillId == SkillType.Fishing &&
                _character.TryGetSkillPendingPoint(out Point3D splashAt))
            {
                // Source-X Skill_Stroke: each fishing stroke drops an
                // ITEMID_FX_SPLASH water-wash item that decays after 1s at
                // the cast point (CCharSkill.cpp:3620-3628).
                var splash = _world.CreateItem();
                splash.BaseId = 0x352d;
                splash.ItemType = ItemType.WaterWash;
                splash.SetAttr(ObjAttributes.Move_Never | ObjAttributes.Decay);
                _world.PlaceItemWithDecay(splash, splashAt, 1000);
            }
        }
    }

    private static ushort GetSkillStrokeAnimation(SkillType skill) => skill switch
    {
        _ when SkillEngine.HasFlag(skill, SkillFlag.NoAnim) => 0,
        SkillType.Mining => (ushort)AnimationType.Attack1HBash,
        SkillType.Lumberjacking => (ushort)AnimationType.Attack1HBash,
        SkillType.Fishing => (ushort)AnimationType.Attack2HBash,
        SkillType.Blacksmithing => (ushort)AnimationType.Attack1HBash,
        SkillType.Hiding => 0,
        SkillType.Meditation => 0,
        _ => 0,
    };

    private void FireActiveSkillResult(int skillId, bool ok)
    {
        if (_triggerDispatcher == null || _character == null) return;
        _triggerDispatcher.FireCharTrigger(_character,
            ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
            new TriggerArgs { CharSrc = _character, N1 = skillId });
    }

    private bool TryScheduleActiveSkillDelay(SkillType skill, int skillId, Serial targetUid,
        Point3D? point, bool isInfo = false)
    {
        if (_character == null || _character.HasActiveSkillPending()) return false;
        int delayMs = SkillEngine.GetSkillDelayMs(skill, _character.GetSkill(skill));
        if (delayMs <= 0) return false;

        // Source-X: gather skills run strokeCount × DELAY — one animation+sound
        // per stroke, DELAY apart (fishing 1-2 strokes, mining/lumberjack 2-6;
        // CCharSkill.cpp:1463/1568/1667, Skill_Stroke re-arms with the full
        // delay). Other delayed skills time out once with no repeated strokes
        // (Skill_Stage routes SKTRIG_STROKE only for SKF_CRAFT/SKF_GATHER).
        bool isGather = SkillEngine.HasFlag(skill, SkillFlag.Gather);
        int strokes = isGather ? SkillEngine.RollStrokeCount(skill) : 1;
        long now = Environment.TickCount64;
        _character.BeginSkillPending(
            skillId,
            now + (long)delayMs * strokes,
            isGather ? now + delayMs : long.MaxValue,
            targetUid,
            point,
            isInfo);

        FireActiveSkillStroke(skillId);
        return true;
    }

    /// <summary>Advance delayed active skills (@SkillStroke loop + completion).</summary>
    public void TickPendingSkill()
    {
        TickTrackingArrow();
        if (_character == null || !_character.HasActiveSkillPending())
            return;

        int skillId = _character.SkillPendingId;
        var skill = (SkillType)skillId;
        long now = Environment.TickCount64;

        if (now >= _character.SkillStrokeNext)
        {
            FireActiveSkillStroke(skillId);
            _character.SetSkillStrokeNext(now + SkillEngine.GetSkillStrokeIntervalMs(skill, _character.GetSkill(skill)));
        }

        if (now < _character.SkillDelayEnd)
        {
            // The action is still in progress. @SkillWait belongs to attempts
            // to start another action and is dispatched by HandleUseSkill.
            return;
        }

        CompletePendingSkill(skill, skillId);
    }

    private void CompletePendingSkill(SkillType skill, int skillId)
    {
        if (_character == null) return;

        Serial targetUid = _character.SkillPendingTarget;
        bool isInfo = _character.SkillPendingIsInfo;
        Point3D? point = null;
        if (_character.TryGetSkillPendingPoint(out Point3D pt))
            point = pt;

        _character.ClearActiveSkillPending();

        Objects.ObjBase? target = targetUid.IsValid ? _world.FindObject(targetUid) : null;
        var sink = new GameClient.InfoSkillSink(_client, _character);
        bool ok = isInfo
            ? _skillHandlers?.UseInfoSkill(sink, skill, target) ?? false
            : _skillHandlers?.UseActiveSkill(sink, skill, target, point) ?? false;
        FireActiveSkillResult(skillId, ok);
    }

    private void ShowTrackingMenu(SkillType skill, int skillId)
    {
        if (_character == null) return;
        _character.SetTag("SKILL_MENU_PENDING", skillId.ToString());

        // @SkillMenu (Source-X) — a skill opened a selection menu. N1 = skill.
        TriggerResult menuResult = _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillMenu,
            new TriggerArgs { CharSrc = _character, N1 = skillId }) ?? TriggerResult.Default;
        if (menuResult == TriggerResult.True)
        {
            _character.RemoveTag("SKILL_MENU_PENDING");
            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillAbort,
                new TriggerArgs { CharSrc = _character, N1 = skillId });
            return;
        }

        var gump = new GumpBuilder(_character.Uid.Value, 0, 300, 220);
        gump.AddResizePic(0, 0, 5054, 300, 220);
        gump.AddText(30, 15, 0, "What do you wish to track?");
        gump.AddButton(30, 55, 4005, 4007, 1);
        gump.AddText(70, 55, 0, "Animals");
        gump.AddButton(30, 85, 4005, 4007, 2);
        gump.AddText(70, 85, 0, "Monsters");
        gump.AddButton(30, 115, 4005, 4007, 3);
        gump.AddText(70, 115, 0, "Humans");
        gump.AddButton(30, 145, 4005, 4007, 4);
        gump.AddText(70, 145, 0, "Players");
        gump.AddButton(150, 175, 4017, 4019, 0);
        gump.AddText(190, 175, 0, "Cancel");

        SendGump(gump, (buttonId, switches, textEntries) =>
        {
            if (_character == null) return;
            if (!_character.TryGetTag("SKILL_MENU_PENDING", out string? pendingText) ||
                !int.TryParse(pendingText, out int pendingId) || pendingId != skillId)
                return;
            _character.RemoveTag("SKILL_MENU_PENDING");
            if (buttonId == 0)
            {
                _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillAbort,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
                return;
            }

            var category = buttonId switch
            {
                1 => Skills.Information.ActiveSkillEngine.TrackingCategory.Animals,
                2 => Skills.Information.ActiveSkillEngine.TrackingCategory.Monsters,
                3 => Skills.Information.ActiveSkillEngine.TrackingCategory.Humans,
                4 => Skills.Information.ActiveSkillEngine.TrackingCategory.Players,
                _ => Skills.Information.ActiveSkillEngine.TrackingCategory.Animals,
            };

            _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.SkillStroke,
                new TriggerArgs { CharSrc = _character, N1 = skillId });

            var sink = new GameClient.InfoSkillSink(_client, _character);
            var targets = Skills.Information.ActiveSkillEngine.FindTrackingTargets(sink, category);
            bool ok = Skills.Information.ActiveSkillEngine.Tracking(sink, category);

            if (_triggerDispatcher != null)
            {
                _triggerDispatcher.FireCharTrigger(_character,
                    ok ? CharTrigger.SkillSuccess : CharTrigger.SkillFail,
                    new TriggerArgs { CharSrc = _character, N1 = skillId });
            }

            if (ok)
                ShowTrackingTargets(targets);
        });
    }

    private void ShowTrackingTargets(IReadOnlyList<Character> targets)
    {
        if (_character == null || targets.Count == 0) return;
        var visible = targets.Take(15).ToArray();
        int height = 70 + visible.Length * 24;
        var gump = new GumpBuilder(_character.Uid.Value, 0, 360, height);
        gump.AddResizePic(0, 0, 5054, 360, height);
        gump.AddText(25, 15, 0, "What do you wish to track?");
        for (int i = 0; i < visible.Length; i++)
        {
            gump.AddButton(25, 50 + i * 24, 4005, 4007, i + 1);
            int distance = _character.Position.GetDistanceTo(visible[i].Position);
            gump.AddText(65, 50 + i * 24, 0, $"{visible[i].Name} ({distance})");
        }
        SendGump(gump, (buttonId, switches, entries) =>
        {
            if (_character == null || buttonId == 0 || buttonId > (uint)visible.Length) return;
            var target = visible[(int)buttonId - 1];
            if (target.IsDeleted || target.IsDead || target.MapIndex != _character.MapIndex) return;
            _character.SetTag("TRACKING_TARGET", target.Uid.Value.ToString());
            _character.SetTag("TRACKING_UNTIL", (Environment.TickCount64 + 30_000).ToString());
            _character.SetTag("TRACKING_ARROW_NEXT", "0");
            TickTrackingArrow();
        });
    }

    private void TickTrackingArrow()
    {
        if (_character == null || !_character.TryGetTag("TRACKING_TARGET", out string? uidText) ||
            !uint.TryParse(uidText, out uint uid))
            return;
        long now = Environment.TickCount64;
        if (_character.TryGetTag("TRACKING_ARROW_NEXT", out string? nextText) &&
            long.TryParse(nextText, out long next) && now < next)
            return;
        bool expired = !_character.TryGetTag("TRACKING_UNTIL", out string? untilText) ||
            !long.TryParse(untilText, out long until) || now >= until;
        var target = expired ? null : _world.FindChar(new Serial(uid));
        if (target == null || target.IsDeleted || target.IsDead || target.MapIndex != _character.MapIndex)
        {
            _netState.Send(new PacketArrowQuest(false, 0, 0, _netState.IsClientPost7090));
            _character.RemoveTag("TRACKING_TARGET");
            _character.RemoveTag("TRACKING_UNTIL");
            _character.RemoveTag("TRACKING_ARROW_NEXT");
            return;
        }
        _netState.Send(new PacketArrowQuest(true, (ushort)target.X, (ushort)target.Y, _netState.IsClientPost7090));
        _character.SetTag("TRACKING_ARROW_NEXT", (now + 1000).ToString());
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
            if (_triggerDispatcher.Runner.TryRunFunction("f_onclient_helppage", _character, _client, trigArgs, out _))
                return;
        }

        OpenNamedDialog("d_helppage", 1);
    }

    // ==================== AOS Tooltip ====================

    public void HandleAOSTooltip(uint serial)
    {
        var obj = _world.FindObject(new Serial(serial));
        if (obj == null) return;
        SendAosTooltip(obj, requested: true);
    }

    public void SendAosTooltip(ObjBase obj, bool requested, bool invalidate = false)
    {
        if (_character == null || obj == null || obj.IsDeleted) return;
        if (_world.ToolTipMode == 0 || (GameClient.ServerFeatureAOS & 0x02) == 0) return;
        if (!_netState.SupportsAosTooltip || !CanSendAosTooltip(obj)) return;

        uint serial = obj.Uid.Value;
        // Invalidate forces a rebuild but keeps the previous entry in place: the
        // rebuild below compares against its hash so the revision bumps and 0xDC
        // observers know to re-request. Nulling it would reset the revision to 1
        // while clients still cache the old revision-1 props.

        // Object-keyed cache (Source-X SetPropertyList): one build serves every
        // observer and survives view-exit; only the ToolTipCache TTL expires it.
        long now = Environment.TickCount64;
        long cacheMs = Math.Max(0, _world.ToolTipCache) * 1000L;
        if (!invalidate && cacheMs > 0 && obj.TooltipCache is { } cached &&
            now - cached.BuiltAt < cacheMs)
        {
            SendAosTooltipEntry(serial, cached, requested, hashChanged: false);
            return;
        }

        var scriptProperties = new List<(uint ClilocId, string Args)>();
        _client.ScriptTooltipProperties = scriptProperties;

        if (_triggerDispatcher != null)
        {
            // IsTrigUsed-gated: view-apply builds a tooltip for every object entering
            // view, so firing @ClientTooltip unconditionally re-parses the CHARDEF/ITEMDEF
            // section and allocates per object even when no script hooks it. When the
            // trigger is unused the fire is a no-op (it cannot add script properties), so
            // skipping it is behaviour-preserving — mirrors the gated single-click path.
            TriggerResult triggerResult = obj switch
            {
                Character ch when _triggerDispatcher.IsCharTriggerUsed(CharTrigger.ClientTooltip) =>
                    _triggerDispatcher.FireCharTrigger(ch, CharTrigger.ClientTooltip,
                        new TriggerArgs { CharSrc = _character, ScriptConsole = _client, N1 = requested ? 1 : 0 }),
                Item tooltipItem when _triggerDispatcher.IsItemTriggerUsed(ItemTrigger.ClientTooltip) =>
                    _triggerDispatcher.FireItemTrigger(tooltipItem, ItemTrigger.ClientTooltip,
                        new TriggerArgs { CharSrc = _character, ItemSrc = tooltipItem, ScriptConsole = _client, N1 = requested ? 1 : 0 }),
                _ => TriggerResult.Default
            };

            if (triggerResult == TriggerResult.True)
            {
                _client.ScriptTooltipProperties = null;
                return;
            }
        }

        var propList = new List<(uint ClilocId, string Args)>
        {
            // Pile items show their stack amount on the tooltip header
            // ("1234 gold coins"), matching the single-click label.
            (1050045, obj is Item nameItem ? nameItem.GetDisplayName() : obj.GetName())
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
                    // Weapon damage - try reading from tags or CombatEngine lookup
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

                case ItemType.CommCrystal:
                {
                    // Source-X CClientMsg_AOSTooltip: active when linked to another
                    // crystal, else inactive; always shows the broadcast line.
                    var link = item.Link.IsValid ? _world.FindObject(item.Link) as Item : null;
                    bool active = link != null && link.ItemType == ItemType.CommCrystal;
                    propList.Add((active ? 1060742u : 1060743u, "")); // activated / deactivated
                    propList.Add((1060745u, "")); // broadcast
                    break;
                }
            }

            if (_triggerDispatcher != null &&
                _triggerDispatcher.IsItemTriggerUsed(ItemTrigger.ClientTooltipAfterDefault))
                _triggerDispatcher.FireItemTrigger(item, ItemTrigger.ClientTooltipAfterDefault,
                    new TriggerArgs { CharSrc = _character, ItemSrc = item, ScriptConsole = _client, N1 = requested ? 1 : 0 });
        }

        propList.AddRange(scriptProperties);
        _client.ScriptTooltipProperties = null;

        var props = propList.ToArray();
        // Deterministic hash - .NET GetHashCode() is randomized per process
        uint hash = StableStringHash(obj.GetName());
        foreach (var (clilocId, args) in props)
            hash = hash * 31 + (uint)clilocId + StableStringHash(args);

        uint revision = 1;
        bool hashChanged = true;
        if (obj.TooltipCache is { } previous)
        {
            hashChanged = previous.Hash != hash;
            revision = hashChanged ? unchecked(previous.Revision + 1) : previous.Revision;
        }
        if (revision == 0) revision = 1;

        // Stored even with the TTL off (cacheMs == 0): the read above stays
        // gated, but revision continuity needs the last build to survive, or a
        // changed tooltip would reuse revision 1 and stick in the client cache.
        var entry = new TooltipCacheEntry(hash, revision, now, props);
        obj.TooltipCache = entry;
        SendAosTooltipEntry(serial, entry, requested, hashChanged);
    }

    private void SendAosTooltipEntry(uint serial, TooltipCacheEntry entry, bool requested,
        bool hashChanged)
    {
        // Source-X TOOLTIPMODE_SENDVERSION (CClientMsg_AOSTooltip.cpp:176-207): an
        // unrequested push carries only the OPL revision (0xDC) and the client
        // pulls the full 0xD6 when its own cache misses that revision. Full data
        // goes out when the client asked for it, when this build just changed the
        // object's hash (the stale-UID cache-collision guard, :148-157), or in
        // send-full mode (our ToolTipMode 2). This is what keeps a login burst at
        // one tiny version packet per object instead of a full OPL each.
        bool sendFull = requested || _world.ToolTipMode == 2 || hashChanged;

        if (sendFull)
            _netState.Send(new PacketOPLData(serial, entry.Revision, entry.Properties));
        else
            _netState.Send(new PacketOPLInfo(serial, entry.Revision));
    }

    private bool CanSendAosTooltip(ObjBase obj)
    {
        if (_character == null) return false;

        if (obj is Character target)
        {
            if (target == _character) return true;
            if (target.MapIndex != _character.MapIndex) return false;
            bool concealed = target.IsInvisible || target.IsStatFlag(StatFlag.Hidden);
            if (concealed && !_character.AllShow && _character.PrivLevel < PrivLevel.Counsel)
                return false;
            // Source-X gates tooltips on distance + CanSee visibility only — no LOS
            // raycast (CClientMsg_AOSTooltip.cpp:55 uses GetDistSight vs visual range;
            // the 0xD6 request handler checks CanSee, receive.cpp:3628). The client
            // draws the object regardless of walls, so a raycast here only desyncs
            // the tooltip from the draw and burns a per-object raycast in view-apply.
            return _character.Position.GetDistanceTo(target.Position) <= _netState.ViewRange;
        }

        if (obj is not Item item) return false;
        if (item.IsAttr(ObjAttributes.Static) && !_character.AllMove && _character.PrivLevel < PrivLevel.GM)
            return false;
        // Match the view-send audience (AllShow OR GM+): a GM sees invisible
        // items without AllShow, so the skill-target can-see gate must let them
        // through too, or targeting one desyncs it out of the client's view.
        if (item.IsAttr(ObjAttributes.Invis) && !_character.AllShow &&
            _character.PrivLevel < PrivLevel.GM)
            return false;

        Item top = GetTopContainer(item) ?? item;
        Character? owner = top.ContainedIn.IsValid ? _world.FindChar(top.ContainedIn) : null;
        if (owner == _character) return true;
        Point3D point = owner?.Position ?? top.Position;
        if (point.Map != _character.MapIndex) return false;
        // Distance-only, like the character branch above (Source-X does no LOS
        // raycast anywhere in the tooltip paths).
        return _character.Position.GetDistanceTo(point) <= _netState.ViewRange;
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
        if (target == null || !target.IsPlayer || target == _character || target.IsDeleted) return;
        var party = _partyManager.FindParty(_character.Uid);
        if (party != null && (party.Master != _character.Uid || party.IsFull)) return;
        if (_partyManager.FindParty(target.Uid) != null) return;
        if (_triggerDispatcher?.FireCharTrigger(target, CharTrigger.PartyInvite,
            new TriggerArgs { CharSrc = _character }) == TriggerResult.True)
            return;
        target.SetTag("PARTY_INVITE_FROM", _character.Uid.Value.ToString());
        target.SetTag("PARTY_INVITE_TIME", Environment.TickCount64.ToString());
        SendToChar?.Invoke(target.Uid, new PacketPartyInvitation(_character.Uid.Value));
        SysMessage(ServerMessages.GetFormatted("party_invite", target.Name));
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
            bool couldSendBuffs = _netState.SupportsBuffIcon;
            if (TryParseClientVersionNumber(version, out uint parsedVersion))
            {
                _netState.ClientVersionNumber = parsedVersion;
                _logger.LogInformation("Client version detected from 0xBD: {Ver} -> {Num}", version, _netState.ClientVersionNumber);
                // Source-X CClientLog.cpp:918 stores the reported version on the
                // account so the next connection does not have to ask again.
                _client.StoreReportedClientVersion();
                if (GameClient.ServerAutoResDisp && _client.Account != null)
                    _ = _client.HandleResolvedClientVersion();

                // The buff bar is gated on the client version (Source-X
                // PacketBuff::CanSendTo / MINCLIVER_BUFFS), and the game socket
                // only learns that version from this 0xBD reply — the classic
                // client sends the bare 4-byte seed there, not the 0xEF seed
                // that carries a version. Anything sent before the reply landed
                // was therefore dropped, so rebuild the bar now that the gate
                // opened. No-op when the character is not in world yet: the
                // regular login resend covers that case.
                if (!couldSendBuffs && _netState.SupportsBuffIcon && _client.Character != null)
                    _client.Spells?.ResendBuffs(_client.Character);
            }
        }
    }

    private static bool TryParseClientVersionNumber(string version, out uint result)
    {
        result = 0;
        string clean = version.Trim().Split(' ', 2)[0];
        string[] parts = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !uint.TryParse(parts[0], out uint major) ||
            !uint.TryParse(parts[1], out uint minor) ||
            !TryParseVersionPart(parts[2], out uint revision, out uint revisionSuffix))
            return false;

        uint patch = revisionSuffix;
        if (parts.Length > 3)
        {
            if (!TryParseVersionPart(parts[3], out patch, out uint patchSuffix))
                return false;
            if (patchSuffix != 0)
                patch = patchSuffix;
        }

        result = major * 10_000_000 + minor * 1_000_000 + revision * 1_000 + patch;
        return true;
    }

    private static bool TryParseVersionPart(string token, out uint number, out uint letterPatch)
    {
        number = 0;
        letterPatch = 0;
        int digitCount = 0;
        while (digitCount < token.Length && char.IsAsciiDigit(token[digitCount]))
            digitCount++;
        if (digitCount == 0 || !uint.TryParse(token[..digitCount], out number))
            return false;
        if (digitCount < token.Length && char.IsAsciiLetter(token[digitCount]))
            letterPatch = (uint)(char.ToLowerInvariant(token[digitCount]) - 'a' + 1);
        return true;
    }

    // ==================== Client Update Loop ====================
}
