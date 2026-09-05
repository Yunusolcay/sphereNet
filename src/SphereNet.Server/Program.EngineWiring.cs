using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Crafting;
using SphereNet.Game.Death;
using SphereNet.Game.Definitions;
using SphereNet.Game.Guild;
using SphereNet.Game.Housing;
using SphereNet.Game.Messages;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Party;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.Packets.Outgoing;
using System.Collections.Concurrent;
using SphereNet.Network.State;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using SphereNet.Scripting.Execution;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;
using GameRegion = SphereNet.Game.World.Regions.Region;
using SphereNet.Game.World.Regions;
using SphereNet.Panel;
using SphereNet.Server.Admin;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;


namespace SphereNet.Server;

public static partial class Program
{
    private static bool TryGetClientFor(Character ch, out GameClient client) =>
        _clientsByCharUid.TryGetValue(ch.Uid, out client!) && client.Character == ch;

    private static void ConfigureGlobalScriptHooks(TriggerDispatcher dispatcher, SphereConfig config)
    {
        LoadResourceList(dispatcher.GlobalPlayerEvents, config.EventsPlayer, ResType.Events);
        LoadResourceList(dispatcher.GlobalPetEvents, config.EventsPet, ResType.Events);
        LoadResourceList(dispatcher.GlobalItemEvents, config.EventsItem, ResType.Events);
        LoadResourceList(dispatcher.GlobalRegionEvents, config.EventsRegion, ResType.Events);
        LoadResourceList(dispatcher.SpeechSelfResources, config.SpeechSelf, ResType.Speech);
        LoadResourceList(dispatcher.SpeechPetResources, config.SpeechPet, ResType.Speech);
    }

    private static void LoadResourceList(List<ResourceId> target, string rawValue, ResType type)
    {
        target.Clear();
        if (string.IsNullOrWhiteSpace(rawValue))
            return;

        foreach (var token in rawValue.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var rid = ResourceId.FromString(token, type);
            if (rid.IsValid && !target.Contains(rid))
                target.Add(rid);
        }
    }

    private static void InitializeGameEngines(string basePath)
    {
            // --- 7b. Game Engines ---
            _log.LogInformation("Initializing game engines...");
            GameClient.WalkBufferMax = Math.Max(1, _config.WalkBuffer);
            GameClient.WalkRegenPerSecond = Math.Max(0, _config.WalkRegen);
            GameClient.ClientLingerSeconds = Math.Max(0, _config.ClientLinger);
            GameClient.MoveToleranceMs = 80;
            // Source-X paces the next allowed walk 30ms early (iDelay -= 30) so
            // player ping/jitter doesn't false-throttle a step. Match it.
            GameClient.MovePermissiveOffsetMs = 30;
            // After a movement rejection the client (ClassicUO) clears its step
            // queue, resets its walk sequence to 0 and resends from seq 0. The
            // in-flight steps it had already predicted (seq > 1) arrive next and
            // are now dropped SILENTLY by the stale-seq path — no extra 0x21, no
            // 0x20 redraw — until that fresh seq-0 stream begins. A fixed time
            // window is therefore unnecessary and counter-productive (it also
            // swallowed the fresh seq-0/1 steps, delaying recovery), so it is
            // disabled. See RejectStaleMove for the reasoning.
            GameClient.MoveRejectResyncMs = 0;
            GameClient.MoveViolationKickThreshold = 0;
            GameClient.MovementCreditEnabled = _config.MovementCreditEnabled;
            GameClient.MovementCreditBaseMs = _config.MovementCreditBaseMs;
            GameClient.MovementCreditMaxMs = _config.MovementCreditMaxMs;
            GameClient.MovementQueueCapacity = _config.MovementQueueCapacity;
            GameClient.SpeedHackDetectionEnabled = _config.SpeedHackDetectionEnabled;
            GameClient.SpeedHackRateThreshold = _config.SpeedHackRateThreshold;
            GameClient.SpeedHackBurstWindow = _config.SpeedHackBurstWindow;
            GameClient.SpeedHackHistorySize = _config.SpeedHackHistorySize;
            GameClient.SpeedHackCooldownMs = _config.SpeedHackCooldownMs;
            GameClient.NotorietyHues = new NotorietyHueSettings(
                _config.ColorNotoGood,
                _config.ColorNotoGoodNpc,
                _config.ColorNotoGuildSame,
                _config.ColorNotoNeutral,
                _config.ColorNotoCriminal,
                _config.ColorNotoGuildWar,
                _config.ColorNotoEvil,
                _config.ColorNotoInvul,
                _config.ColorNotoInvulGameMaster,
                _config.ColorNotoDefault);

            MovementEngine.WalkDelayFoot = _config.WalkDelayFoot;
            MovementEngine.WalkDelayMount = _config.WalkDelayMount;
            MovementEngine.RunDelayFoot = _config.RunDelayFoot;
            MovementEngine.RunDelayMount = _config.RunDelayMount;

            NetState.RttPingIntervalMs = _config.RttPingIntervalMs;

            // SECTORSLEEP (minutes; 0 disables) was read into config but never
            // applied, so sectors always used the hardcoded 10-minute default.
            SphereNet.Game.World.Sectors.Sector.SleepDelayMs = _config.SectorSleepMs;

            _triggerDispatcher = new TriggerDispatcher();
            _triggerDispatcher.Resources = _resources;
            var exprParser = new ExpressionParser
            {
                DiagnosticLogger = msg =>
                {
                    // Surface unresolved script expressions both in the log and in
                    // the GUI console so the user spots missing properties while a
                    // specific command/dialog is running.
                    _log?.LogWarning("{Msg}", msg);
                    ConsoleAppend(msg);
                }
            };
            var scriptInterpreter = new ScriptInterpreter(exprParser, _loggerFactory.CreateLogger<ScriptInterpreter>());
            _triggerRunner = new TriggerRunner(scriptInterpreter, _resources, _loggerFactory.CreateLogger<TriggerRunner>());
            _triggerRunner.ScriptDebug = _config.ScriptDebug;
            _triggerDispatcher.ScriptDebug = _config.ScriptDebug;
            _triggerDispatcher.DebugLog = msg => _log?.LogDebug("{Msg}", msg);
            CharDefHelper.AfterApplyDefName = ch =>
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = ch });
            // Item counterpart of the chardef @Create above — Source-X
            // CItem::GenerateScript fires ITRIG_Create on every materialised item,
            // which is where a magic weapon's @Create body (MOREY/ATTR/HITPOINTS/
            // COLOR) actually applies. ItemDefHelper.ApplyInstanceMetadata invokes
            // this once per fresh instance; the world load never routes through it.
            SphereNet.Game.Objects.Items.Item.CreateTriggerHook = item =>
                _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item });
            _systemHooks = new ScriptSystemHooks(_triggerRunner);
            RegisterDbProviders();
            _scriptDb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
            _scriptLdb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
            // Source-X MDB: a second, independent MySQL reference object —
            // scripts connect it explicitly (no ini auto-connect).
            _scriptMdb = new ScriptDbAdapter(_loggerFactory.CreateLogger<ScriptDbAdapter>());
            InitDbConnections(_config, _scriptDb);
            if (_config.HasFileCommands)
            {
                string fileBasePath = Path.Combine(Path.GetDirectoryName(_config.ScpFilesDir) ?? ".", "files");
                _scriptFile = new ScriptFileHandle(fileBasePath);
                ScriptFileHandle.Diagnostic = msg => _log.LogDebug("[script_file] {Message}", msg);
                _log.LogInformation("Script FILE commands enabled, base path: {Path}", fileBasePath);
            }
            scriptInterpreter.CallFunction = (name, target, source, args) =>
                _triggerRunner.TryRunFunction(name, target, source, args, out var callResult)
                    ? callResult
                    : TriggerResult.Default;
            scriptInterpreter.CallFunctionWithScope = (name, target, source, args, scope) =>
                _triggerRunner.TryRunFunction(name, target, source, args, scope, out var callResult)
                    ? callResult
                    : TriggerResult.Default;
            scriptInterpreter.ResolveFunctionExpression = (name, argString, target, source, args) =>
                _triggerRunner.TryEvaluateFunction(name, argString, target, source, args, out var value)
                    ? value
                    : null;
            scriptInterpreter.ResolveFunctionExpressionWithScope = (name, argString, target, source, args, scope) =>
                _triggerRunner.TryEvaluateFunction(name, argString, target, source, args, scope, out var value)
                    ? value
                    : null;
            scriptInterpreter.ServerPropertyResolver = ResolveServerProperty;
            _triggerDispatcher.Runner = _triggerRunner;
            ConfigureGlobalScriptHooks(_triggerDispatcher, _config);

            // Build the IsTrigUsed cache now that every script resource is loaded,
            // then wire @NotoSend ONLY if a script actually hooks it. ComputeNotoriety
            // is a per-observer hot path; installing the override hook only when used
            // keeps it to a null check otherwise. N1 carries the noto for the script
            // to rewrite; the (possibly rewritten) ARGN1 becomes the displayed colour.
            _triggerDispatcher.BuildUsedTriggerCache();
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NotoSend))
            {
                SphereNet.Game.Objects.Characters.Character.OnNotoSend = (viewer, subject, noto) =>
                {
                    var args = new TriggerArgs { CharSrc = viewer, N1 = noto };
                    _triggerDispatcher.FireCharTrigger(subject, CharTrigger.NotoSend, args);
                    return (byte)Math.Clamp(args.N1, 0, 255);
                };
            }
            // <NOTOGETFLAG uid> script property → full Noto_GetFlag of the subject
            // as seen by the given viewer.
            SphereNet.Game.Objects.Characters.Character.ResolveNotoFlag =
                (subject, viewer) => GameClient.ComputeNotoriety(_world, viewer, subject);
            // @EffectAdd — fired per applied buff; gate to a null check when unhooked.
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.EffectAdd))
            {
                SphereNet.Game.Objects.Characters.Character.OnEffectAdd = (target, spellId) =>
                    _triggerDispatcher.FireCharTrigger(target, CharTrigger.EffectAdd,
                        new TriggerArgs { CharSrc = target, N1 = spellId });
            }
            // @Reveal — fired before hidden/invisible state drops; RETURN 1
            // keeps the character concealed (Source-X CChar::Reveal).
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.Reveal))
            {
                SphereNet.Game.Objects.Characters.Character.OnRevealing = ch =>
                    _triggerDispatcher.FireCharTrigger(ch, CharTrigger.Reveal,
                        new TriggerArgs { CharSrc = ch }) != TriggerResult.True;
            }
            // @SpellEffectAdd / @SpellEffectRemove — timed buff lifecycle
            // (Source-X CCharSpell). ARGN1 = spell id; SRC = caster on add.
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SpellEffectAdd))
            {
                SphereNet.Game.Objects.Characters.Character.OnSpellEffectAdd = (target, caster, spellId) =>
                    _triggerDispatcher.FireCharTrigger(target, CharTrigger.SpellEffectAdd,
                        new TriggerArgs { CharSrc = caster ?? target, N1 = spellId });
            }
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SpellEffectRemove))
            {
                SphereNet.Game.Objects.Characters.Character.OnSpellEffectRemove = (target, spellId) =>
                    _triggerDispatcher.FireCharTrigger(target, CharTrigger.SpellEffectRemove,
                        new TriggerArgs { CharSrc = target, N1 = spellId });
            }
            // @SpellEffectTick — Source-X SPELLFLAG_TICK bridge on the native
            // poison tick (the era's only TICK consumer). Script contract:
            // ARGN1 = spell id, ARGN2 = strength, ARGO = memory shim (BASEID =
            // the spell's RUNE_ITEM, MOREY = strength, LINK = poisoner);
            // LOCAL.EFFECT/DELAY/CHARGES/DAMAGETYPE seeded, script writes read
            // back from the shared pool; RETURN 1 destroys the effect (cure).
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SpellEffectTick))
            {
                SphereNet.Game.Objects.Characters.Character.OnSpellEffectTick = (victim, ctx) =>
                {
                    var locals = new SphereNet.Scripting.Variables.VarMap();
                    locals.Set("EFFECT", ctx.Damage.ToString());
                    locals.Set("DELAY", (ctx.DelayMs / 1000.0).ToString(
                        "0.###", System.Globalization.CultureInfo.InvariantCulture));
                    locals.Set("CHARGES", ctx.Charges.ToString());
                    locals.Set("DAMAGETYPE", "08"); // dam_poison
                    var spellDef = _spellEngine?.GetSpellDef((SpellType)ctx.SpellId);
                    var memory = new SpellMemoryShim
                    {
                        SpellId = ctx.SpellId,
                        BaseId = spellDef?.RuneItemId ?? 0,
                        MoreY = ctx.Strength,
                        LinkUid = ctx.SourceUid.IsValid ? ctx.SourceUid.Value : 0,
                        Name = spellDef?.Name ?? "poison",
                    };
                    var args = new TriggerArgs
                    {
                        CharSrc = victim,
                        N1 = ctx.SpellId,
                        N2 = ctx.Strength,
                        O1 = memory,
                        Locals = locals,
                    };
                    if (_triggerDispatcher.FireCharTrigger(victim, CharTrigger.SpellEffectTick, args)
                        == TriggerResult.True)
                        return false;
                    // [SPELL n] @EffectTick resource-section stage (Source-X
                    // SPTRIG_EFFECTTICK) — shares the same args/LOCAL pool, so
                    // a section script can adjust EFFECT/DELAY/CHARGES too.
                    if (_triggerDispatcher.FireSpellTrigger((SpellType)ctx.SpellId, "EffectTick",
                            victim, args) == TriggerResult.True)
                        return false;
                    ctx.Damage = (int)locals.GetInt("EFFECT", ctx.Damage);
                    ctx.Charges = (int)locals.GetInt("CHARGES", ctx.Charges);
                    if (double.TryParse(locals.Get("DELAY"),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out double delaySec) && delaySec > 0)
                        ctx.DelayMs = (int)(delaySec * 1000);
                    return true;
                };
            }
            // @MemoryEquip — memory items are created frequently in combat; install
            // the fire only when a script hooks the item trigger (item IsTrigUsed gate).
            if (_triggerDispatcher.IsItemTriggerUsed(ItemTrigger.MemoryEquip))
            {
                SphereNet.Game.Objects.Characters.Character.OnMemoryEquip = mem =>
                    _triggerDispatcher.FireItemTrigger(mem, ItemTrigger.MemoryEquip,
                        new TriggerArgs { ItemSrc = mem });
            }
            // @SkillUseQuick — fires per quick skill check; install only when hooked
            // (IsTrigUsed gate). N1 = skill, N2 = difficulty; RETURN 1 cancels the use.
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SkillUseQuick))
            {
                // N1 = skill, N2 = difficulty, N3 = rolled result (1/0). RETURN 1
                // cancels the use; otherwise ARGN3 is read back as the final result.
                SphereNet.Game.Objects.Characters.Character.OnSkillUseQuickDetailed =
                    (SphereNet.Game.Objects.Characters.Character ch, int skillId, ref int difficulty, int result) =>
                {
                    var args = new TriggerArgs { CharSrc = ch, N1 = skillId, N2 = difficulty, N3 = result };
                    var triggerResult = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillUseQuick, args);
                    difficulty = args.N2;
                    if (triggerResult == TriggerResult.True) return -1;
                    return Math.Clamp(args.N3, 0, 1);
                };
            }
            // @NPCSeeNewPlayer — install only when hooked so the per-NPC perception
            // scan is skipped entirely otherwise. O1 = the newly-seen player.
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCSeeNewPlayer))
            {
                SphereNet.Game.Objects.Characters.Character.OnNpcSeeNewPlayer = (npc, player) =>
                    _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCSeeNewPlayer,
                        new TriggerArgs { CharSrc = npc, O1 = player });
            }
            // @PersonalSpace — fired on a shove; low frequency, no gate needed.
            SphereNet.Game.Objects.Characters.Character.OnPersonalSpace = (mover, blocker) =>
                _triggerDispatcher.FireCharTrigger(mover, CharTrigger.PersonalSpace,
                    new TriggerArgs { CharSrc = mover, O1 = blocker });

            // @PetDesert — fired on the pet when loyalty hits zero; RETURN 1 cancels
            // the desertion. O1 = owner (may be null if it could not be resolved).
            SphereNet.Game.Objects.Characters.Character.OnPetDesert = (pet, owner) =>
                _triggerDispatcher.FireCharTrigger(pet, CharTrigger.PetDesert,
                    new TriggerArgs { CharSrc = pet, O1 = owner }) == TriggerResult.True;

            // @Jail — fired on a character sent to jail. N1 = sentence minutes (0 = indefinite).
            SphereNet.Game.Objects.Characters.Character.OnJailed = (ch, minutes) =>
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.Jail,
                    new TriggerArgs { CharSrc = ch, N1 = minutes });

            // @EnvironChange — fired when a character's perceived light level changes
            // (surface/dungeon boundary). N1 = new light level. Only fires on an
            // actual change (UpdateEnvironLight), which is infrequent.
            SphereNet.Game.Objects.Characters.Character.OnEnvironChange = (ch, light) =>
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.EnvironChange,
                    new TriggerArgs { CharSrc = ch, N1 = light });

            // @SkillGain (Source-X Skill_Experience) — pre-roll hook: fires before the
            // gain roll so a script can tune the gain chance (ARGN2) / effective cap
            // (ARGN3) or RETURN 1 to cancel the attempt. Installed only when hooked so
            // unscripted shards skip it on every gain attempt.
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SkillGain))
            {
                SkillEngine.OnSkillGainCheck =
                    (SphereNet.Game.Objects.Characters.Character ch, SkillType skill, ref int chance, ref int skillMax) =>
                {
                    var args = new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = chance, N3 = skillMax };
                    bool cancel = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillGain, args) == TriggerResult.True;
                    chance = args.N2;
                    skillMax = args.N3;
                    return cancel;
                };
            }

            // @SkillChange as a cancelable SETTER guard (Source-X) for RUNTIME value
            // changes — GM .ADDSKILL and script property assignment (MAGERY=80) — so a
            // script can adjust the new value (ARGN2) or RETURN 1 to veto it. Gated so
            // unscripted shards skip it; load/spawn/decay/gain use the raw setter and do
            // NOT fire here (gain keeps its own post @SkillChange notification below).
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.SkillChange))
            {
                SphereNet.Game.Objects.Characters.Character.OnSkillChange =
                    (SphereNet.Game.Objects.Characters.Character ch, SkillType skill, int oldVal, ref int newVal) =>
                {
                    var args = new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = newVal, N3 = newVal - oldVal };
                    bool cancel = _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillChange, args) == TriggerResult.True;
                    newVal = args.N2;
                    return cancel;
                };
            }

            // Post-gain notification (Source-X parity): @SkillChange + blue system
            // message + skill-list refresh. @SkillGain itself fires PRE-roll above.
            SkillEngine.OnSkillGain = (ch, skill, newVal) =>
            {
                // @SkillChange (Source-X CTRIG_SkillChange) fires on a runtime skill
                // value change. Wired here on the gain hook (the dominant runtime
                // change); load/spawn use the raw setter and do not fire.
                // N1 = skill, N2 = new value, N3 = delta.
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillChange,
                    new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = newVal, N3 = 1 });

                // Source-X sends NO gain text — Skill_SetBase only refreshes the
                // skill window (addSkillWindow, CCharSkill.cpp:222). The old
                // "Your skill in X has increased" line was invented; scripts can
                // add one via @SkillChange if a shard wants it.
                if (ch.IsPlayer && _clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                    gc.SendSkillList();
            };
            SkillEngine.OnSkillDecrease = (ch, skill, newVal) =>
            {
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.SkillChange,
                    new TriggerArgs { CharSrc = ch, N1 = (int)skill, N2 = newVal, N3 = -1 });
                if (ch.IsPlayer && _clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                    gc.SendSkillList();
            };
            // Wire stat gain message (Source-X: "You feel stronger/more agile/smarter")
            SkillEngine.OnStatGain = (ch, statIdx, newVal) =>
            {
                // @StatChange (Source-X CTRIG_StatChange) fires on a runtime stat
                // value change. Wired on the stat-gain hook (the runtime change
                // point); load/spawn set stats via the raw setter and do not fire.
                // N1 = stat index (0=Str,1=Dex,2=Int), N2 = new value, N3 = delta.
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.StatChange,
                    new TriggerArgs { CharSrc = ch, N1 = statIdx, N2 = newVal, N3 = 1 });

                if (ch.IsPlayer && _clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                {
                    string msg = statIdx switch
                    {
                        0 => "You feel stronger.",
                        1 => "You feel more agile.",
                        2 => "You feel smarter.",
                        _ => ""
                    };
                    if (msg.Length > 0)
                        gc.SysMessage(msg, 0x0480);
                    gc.SendCharacterStatus(ch);
                }
            };
            SkillEngine.OnStatDecrease = (ch, statIdx, newVal) =>
            {
                _triggerDispatcher.FireCharTrigger(ch, CharTrigger.StatChange,
                    new TriggerArgs { CharSrc = ch, N1 = statIdx, N2 = newVal, N3 = -1 });
                if (ch.IsPlayer && _clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                    gc.SendCharacterStatus(ch);
            };
            // Wire scripted (custom) skill trigger chain
            SkillHandlers.OnScriptedSkillUse = (ch, skill) =>
            {
                // The client action driver owns Select/PreStart/Start and the
                // final Success/Fail dispatch. This callback resolves the roll.
                int difficulty = ch.ActDiff != 0 ? ch.ActDiff : 50;

                bool success = SkillEngine.CheckSuccess(ch, skill, difficulty);

                // Gain experience (fires @SkillGain via existing callback)
                SkillEngine.GainExperience(ch, skill, success ? difficulty : -difficulty);

                return success;
            };
            SkillHandlers.OnCraftSkillUsed = (ch, skill) =>
            {
                // Find the GameClient for this character and open crafting gump
                foreach (var client in _clients.Values)
                {
                    if (client.Character == ch)
                    {
                        client.OpenCraftingGump(skill);
                        break;
                    }
                }
            };
            _terrain = new TerrainEngine(_mapData);
            _movement = new MovementEngine(_world, _triggerDispatcher);
            // NOTE: _movement.SpellEngine is wired AFTER SpellEngine is constructed
            // (see below). Assigning it here bound null — SpellEngine doesn't exist
            // yet — which silently disabled movement-based cast interruption.
            _movement.OnSysMessage = (mover, text) =>
            {
                // Source-X CClient::SysMessage routes region-enter/PvP/guard text
                // only to the moving player's own client.
                if (TryGetClientFor(mover, out var c))
                    c.SysMessage(text);
            };

            _movement.CanEnterHouse = (mover, dest) =>
            {
                if (mover.PrivLevel >= PrivLevel.GM) return true;
                var house = _housingEngine?.FindHouseAt(dest);
                if (house == null) return true;
                return house.CanAccess(mover.Uid);
            };

            _movement.CanBoardShip = (mover, dest) =>
            {
                if (mover.PrivLevel >= PrivLevel.GM) return true;
                var ship = _shipEngine?.FindShipAt(dest);
                if (ship == null) return true;
                return ship.CanBoard(mover.Uid);
            };

            _movement.OnTeleport = (mover, dest, oldMap) =>
            {
                if (TryGetClientFor(mover, out var c))
                {
                    if (oldMap != mover.MapIndex)
                        c.HandleMapChanged();
                    else
                        c.Resync();
                    var snd = new PacketSound(0x01FE, mover.X, mover.Y, mover.Z);
                    BroadcastNearby(mover.Position, 18, snd, 0);
                }
            };

            // Script TRIGGER verb — fire arbitrary named triggers through the
            // dispatcher's by-name chain (Source-X CV_TRIGGER).
            SphereNet.Game.Objects.ObjBase.OnScriptTrigger = (obj, trigName, console) =>
            {
                var src = (console as GameClient)?.Character ?? obj as Character;
                var targs = new SphereNet.Game.Scripting.TriggerArgs
                {
                    CharSrc = src,
                    ScriptConsole = console
                };
                if (obj is Character tch)
                    _triggerDispatcher.FireCharTriggerByName(tch, trigName, targs);
                else if (obj is Item titem)
                    _triggerDispatcher.FireItemTriggerByName(titem, trigName, targs);
            };

            // Region/Room ALLCLIENTS — deliver or execute the payload for
            // every online client character inside the area.
            SphereNet.Game.World.Regions.Region.OnAllClients = (region, payload, console) =>
                RunAllClientsPayload(payload, ch => _world.FindRegion(ch.Position) == region);
            SphereNet.Game.World.Regions.Room.OnAllClients = (room, payload, console) =>
                RunAllClientsPayload(payload, ch => _world.FindRoom(ch.Position) == room);

            // Source-X CCharBase::Region_Notify centralised at world level so
            // walk, .go teleport, recall and gate all hit one notify path.
            _world.OnRegionChanged = (mover, oldRegion, newRegion) =>
            {
                _log.LogDebug(
                    "[REGION_CHANGE] {Name} {Old} -> {New} player={IsPlayer}",
                    mover.Name,
                    oldRegion?.Name ?? "<null>",
                    newRegion?.Name ?? "<null>",
                    mover.IsPlayer);

                if (!mover.IsPlayer || newRegion == null) return;

                // @EnvironChange — the perceived light level differs between surface
                // and dungeon regions (mirrors WeatherEngine.GetLightLevel).
                byte regionLight = mover.IsDead ? (byte)0 : _world.GetLightLevel(mover.Position);
                var (weatherType, weatherIntensity, weatherTemp) = _weatherEngine.GetWeatherForRegion(newRegion.Name);
                mover.UpdateEnvironment(regionLight, (byte)weatherType,
                    mover.IsDead ? (byte)SeasonType.Desolation : (byte)_weatherEngine.CurrentSeason);

                if (!TryGetClientFor(mover, out var gc)) { _log.LogDebug("[REGION_CHANGE] no GameClient for {Name}", mover.Name); return; }

                gc.Send(new PacketGlobalLight(regionLight));
                gc.Send(new PacketSeason(mover.IsDead
                    ? (byte)SeasonType.Desolation
                    : (byte)_weatherEngine.CurrentSeason, playSound: false));
                gc.Send(new PacketWeather((byte)weatherType, weatherIntensity, weatherTemp));

                // Source-X CCharAct: the region-name callout fires ONLY when the
                // region sets REGION_FLAG_ANNOUNCE (TAG.ANNOUNCEMENT overrides
                // the text). The old unconditional send spammed the name on
                // every boundary crossing, named or not.
                if (newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Announce))
                {
                    string announce = newRegion.TryGetTag("ANNOUNCEMENT", out var custom) &&
                        !string.IsNullOrEmpty(custom) ? custom! : newRegion.Name;
                    gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                        SphereNet.Game.Messages.Msg.MsgRegionEnter, announce));
                }
                // Guarded callout only on the unguarded→guarded transition —
                // crossing between two adjacent guarded sub-regions stays quiet
                // (Source-X gates on pNewArea->IsGuarded() != m_pArea->IsGuarded()).
                if (newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded) &&
                    (oldRegion == null || !oldRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded)))
                {
                    // Source-X CChar::Region_Notify: SysMessagef(DEFMSG_REGION_GUARDS_1,
                    // <GUARDOWNER tag value>). When the AREADEF omits TAG.GUARDOWNER
                    // it falls back to DEFMSG_REGION_GUARD_ART ("the") so the literal
                    // "%s" never reaches the player.
                    string guardOwner;
                    if (!newRegion.TryGetTag("GUARDOWNER", out var owner) || string.IsNullOrEmpty(owner))
                        guardOwner = SphereNet.Game.Messages.ServerMessages.Get(
                            SphereNet.Game.Messages.Msg.MsgRegionGuardArt);
                    else
                        guardOwner = owner!;
                    gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                        SphereNet.Game.Messages.Msg.MsgRegionGuards1, guardOwner));
                }
                if (newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.NoPvP))
                    gc.SysMessage(SphereNet.Game.Messages.ServerMessages.Get(
                        SphereNet.Game.Messages.Msg.MsgRegionPvpsafe));

                // Source-X also fires DEFMSG_REGION_GUARDS_2 when leaving a guarded
                // zone for an unguarded one — keeps the "you have left the
                // protection of the city guards" callout symmetric.
                if (oldRegion != null &&
                    oldRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded) &&
                    !newRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.Guarded))
                {
                    string guardOwner;
                    if (!oldRegion.TryGetTag("GUARDOWNER", out var owner) || string.IsNullOrEmpty(owner))
                        guardOwner = SphereNet.Game.Messages.ServerMessages.Get(
                            SphereNet.Game.Messages.Msg.MsgRegionGuardArt);
                    else
                        guardOwner = owner!;
                    gc.SysMessage(SphereNet.Game.Messages.ServerMessages.GetFormatted(
                        SphereNet.Game.Messages.Msg.MsgRegionGuards2, guardOwner));
                }
            };

            // Source-X parity: spawn-driven NPCs (CItemSpawn) need the same
            // trigger sequence the manual ".add" pipeline runs. Without this
            // hook a vendor that comes from a SPAWN never fires @NPCRestock,
            // so its stock list stays empty and "buy" responds with "no
            // goods". Mirrors the GameClient.CreateNpcFromDef ordering.
            _world.OnNpcSpawned = npc =>
            {
                try
                {
                    _triggerDispatcher?.FireCharTrigger(
                        npc, SphereNet.Core.Enums.CharTrigger.Create,
                        new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

                    if (npc.NpcBrain == SphereNet.Core.Enums.NpcBrainType.None)
                        npc.NpcBrain = SphereNet.Core.Enums.NpcBrainType.Animal;

                    // Source-X NPC_LoadScript fires @NPCRestock for EVERY new
                    // NPC (CCharNPC.cpp:289-290) — monster chardefs put gear
                    // (ITEMNEWBIE) and backpack loot (ITEM) in ON=@NPCRestock,
                    // so the old vendor-only gate spawned empty monsters.
                    _triggerDispatcher?.FireCharTrigger(
                        npc, SphereNet.Core.Enums.CharTrigger.NPCRestock,
                        new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

                    _triggerDispatcher?.FireCharTrigger(
                        npc, SphereNet.Core.Enums.CharTrigger.CreateLoot,
                        new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });

                    // Classic packs assign STR/DEX/INT inside @Create, which
                    // raises the max vitals AFTER SpawnResolved seeded them from
                    // the (stat-less) chardef section — leaving a fresh spawn at
                    // 1/102 hp. Top off like the .add pipeline does post-@Create.
                    npc.Hits = npc.MaxHits;
                    npc.Stam = npc.MaxStam;
                    npc.Mana = npc.MaxMana;

                    var spawnCharDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
                    if (spawnCharDef != null)
                    {
                        foreach (int spellId in spawnCharDef.NpcSpells)
                        {
                            // SpellType is ushort-backed; Enum.IsDefined throws on a
                            // boxed int, so range-check and cast first.
                            if (spellId >= 0 && spellId <= ushort.MaxValue &&
                                Enum.IsDefined(typeof(SpellType), (ushort)spellId))
                                npc.NpcSpellAdd((SpellType)spellId);
                        }
                    }

                    npc.Hits = npc.MaxHits;
                    npc.Stam = npc.MaxStam;
                    npc.Mana = npc.MaxMana;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[spawn_hook] failed for {Name}", npc.Name);
                }
            };

            // Wire spawn trigger dispatch (@PreSpawn, @Spawn, @AddObj, @DelObj)
            SphereNet.Game.Components.SpawnComponent.OnSpawnTrigger = (item, trigger, args) =>
            {
                if (_triggerDispatcher == null) return TriggerResult.Default;
                var targs = new SphereNet.Game.Scripting.TriggerArgs();
                if (args.SpawnedChar != null)
                {
                    targs.O1 = args.SpawnedChar;
                    targs.CharSrc = args.SpawnedChar;
                }
                else if (args.SpawnedItem != null)
                {
                    targs.O1 = args.SpawnedItem;
                    targs.ItemSrc = args.SpawnedItem;
                }
                // Champion triggers carry explicit N1..N3 payloads (@Level,
                // candle reasons); plain spawn triggers keep N1 = def index.
                if (args.N1 != 0 || args.N2 != 0 || args.N3 != 0)
                {
                    targs.N1 = args.N1;
                    targs.N2 = args.N2;
                    targs.N3 = args.N3;
                }
                else
                {
                    targs.N1 = args.SpawnDefIndex;
                }
                return _triggerDispatcher.FireItemTrigger(item, trigger, targs);
            };

            _partyManager = new PartyManager();
            _guildManager = new GuildManager();
            _guildManager.DeserializeFromWorld(_world);
            if (_guildManager.GuildCount > 0)
                _log.LogInformation("Restored {Count} guilds from world save", _guildManager.GuildCount);
            _speech = new SpeechEngine(_world);
            // Config-driven hearing distances (were hardcoded consts).
            _speech.DistanceSay = _config.DistanceTalk;
            _speech.DistanceWhisper = _config.DistanceWhisper;
            _speech.DistanceYell = _config.DistanceYell;
            _speech.NpcDistanceHear = _config.NpcDistanceHear;
            _speech.PartyManager = _partyManager;
            _speech.GuildManager = _guildManager;
            _speech.OnNpcHear += OnNpcHearSpeech;
            _speech.OnPlayerSpeech = OnPlayerSpeech;
            // @Hear on nearby items (Source-X item/multi OnHear) + the native
            // comm-crystal relay (CItemCommCrystal::OnHear): a linked crystal
            // re-speaks anything said near its partner. The handler is installed
            // unconditionally, but SpeechEngine only runs the per-utterance item
            // scan when a sector listen-item (crystal/multi) is in earshot —
            // Source-X CSector::m_ListenItems — or when ScanAllItemsOnHear is set
            // below because some item def scripts @Hear (then any ground item may
            // listen). S1 = spoken text, N1 = talk mode.
            bool itemHearScripted = _triggerDispatcher.IsItemTriggerUsed(ItemTrigger.Hear);
            _speech.ScanAllItemsOnHear = itemHearScripted;
            _speech.OnItemHear = (speaker, item, text, mode) =>
            {
                TriggerResult r = TriggerResult.Default;
                var sourceConsole = FindGameClient(speaker);

                if (item.ItemType is SphereNet.Core.Enums.ItemType.Multi or
                    SphereNet.Core.Enums.ItemType.MultiCustom or SphereNet.Core.Enums.ItemType.Ship)
                {
                    r = _triggerDispatcher.FireMultiSpeechTrigger(item, speaker, text,
                        (int)mode, sourceConsole);
                }

                if (r != TriggerResult.True && itemHearScripted)
                    r = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Hear,
                        new TriggerArgs
                        {
                            CharSrc = speaker, ItemSrc = item, S1 = text, N1 = (int)mode,
                            ScriptConsole = sourceConsole
                        });

                if (r != TriggerResult.True &&
                    item.ItemType == SphereNet.Core.Enums.ItemType.CommCrystal &&
                    item.Link.IsValid)
                {
                    var dest = _world.FindItem(item.Link);
                    // Source-X CItemCommCrystal::OnHear only relays to a linked
                    // partner that is itself a comm crystal (the generic LINK verb
                    // can point elsewhere).
                    if (dest != null && !dest.IsDeleted && dest != item &&
                        dest.ItemType == SphereNet.Core.Enums.ItemType.CommCrystal)
                    {
                        string crystalName = string.IsNullOrEmpty(dest.Name) ? "a communication crystal" : dest.Name;
                        BroadcastNearby(dest.Position, 12,
                            new PacketSpeechUnicodeOut(dest.Uid.Value, 0, 0, 0x03B2, 3, "TRK",
                                crystalName, text), 0);
                    }
                }
            };
            _speech.OnChannelMessage += (speaker, recipient, text, mode) =>
            {
                if (!TryGetClientFor(recipient, out var c))
                    return;
                // 0xAE with type 0xD/0xE — the client routes guild/alliance
                // text to its chat channel and applies the profile color.
                c.Send(new PacketSpeechUnicodeOut(
                    speaker.Uid.Value,
                    speaker.BodyId,
                    (byte)mode,
                    speaker.SpeechColor != 0 ? speaker.SpeechColor : (ushort)0x03B2,
                    3,
                    "TRK",
                    speaker.Name ?? "",
                    text));
            };
            _commands = new CommandHandler();
            _commands.TriggerDispatcher = _triggerDispatcher;
            _commands.CommandPrefix = string.IsNullOrEmpty(_config.CommandPrefix) ? '.' : _config.CommandPrefix[0];
            _commands.Resources = _resources;
            _commands.ScriptFallbackExecutor = (gm, commandLine) =>
            {
                int spaceIdx = commandLine.IndexOf(' ');
                string verb = (spaceIdx > 0 ? commandLine[..spaceIdx] : commandLine).Trim();
                if (string.IsNullOrEmpty(verb))
                    return false;
                string args = spaceIdx > 0 ? commandLine[(spaceIdx + 1)..].Trim() : "";

                // Run script function in the active client context when possible,
                // so client-bound verbs (DIALOG, TARGET*, SERV.ALLCLIENTS, etc.)
                // and compatibility vars (GETREFTYPE/ISDIALOGOPEN) resolve correctly.
                TryGetClientFor(gm, out var scriptConsole);

                if (exprParser.DebugUnresolved)
                {
                    _log.LogDebug("[script_call] verb={Verb} char=0x{Char:X8} sourceConsole={HasConsole} args='{Args}'",
                        verb, gm.Uid.Value, scriptConsole != null, args);
                }

                var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(gm, 0, 0, args)
                {
                    Object1 = gm,
                    Object2 = gm
                };

                if (!_triggerRunner.TryRunFunction(verb, gm, scriptConsole, trigArgs, out var result))
                {
                    if (exprParser.DebugUnresolved)
                        _log.LogDebug("[script_call] verb={Verb} not found", verb);
                    return false;
                }

                if (exprParser.DebugUnresolved)
                    _log.LogDebug("[script_call] verb={Verb} result={Result}", verb, result);

                // Sphere parity:
                // Script verbs are typically written as [FUNCTION ADMIN], [FUNCTION SHOW], etc.,
                // and many do not use explicit RETURN 1. If the function exists and ran, treat
                // it as handled. Built-ins are still preserved because SpeechEngine invokes
                // script fallback first, then built-ins only when script function is missing.
                if (verb.StartsWith("f_", StringComparison.OrdinalIgnoreCase) ||
                    !verb.Contains('_'))
                    return true;

                return result == TriggerResult.True;
            };
            // Source-X CommandTrigger (sphere.ini CommandTrigger=f_oncommand):
            // a global hook run before every command. Fires the named function
            // with ARGS = the command line; a RETURN 1 (TriggerResult.True)
            // consumes the command. Unset = no hook.
            if (!string.IsNullOrWhiteSpace(_config.CommandTrigger))
            {
                string cmdTrigFunc = _config.CommandTrigger.Trim();
                _commands.CommandTriggerHook = (gm, commandLine) =>
                {
                    TryGetClientFor(gm, out var cmdConsole);
                    var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(gm, 1, 0, commandLine)
                    {
                        Object1 = gm,
                        Object2 = gm,
                    };
                    return _triggerRunner.TryRunFunction(cmdTrigFunc, gm, cmdConsole, trigArgs,
                        out var trigResult) && trigResult == TriggerResult.True;
                };
                _log.LogInformation("CommandTrigger hook wired to function '{Func}'.", cmdTrigFunc);
            }

            _commands.RegisterDefaults(_world);
            int scriptCmdCount = _commands.LoadScriptCommandPrivileges(_resources);
            _log.LogInformation("Loaded {Count} script command privilege entries from [PLEVEL] sections.", scriptCmdCount);
            _commands.OnResyncCommand += PerformScriptResync;
            _commands.OnSysMessage += (ch, msg) =>
            {
                // Also log so diagnostic command output (e.g. .statics) is visible
                // in the log file without decoding the 0xAE unicode speech packet.
                _log.LogDebug("[sysmsg → {Name}] {Message}", ch.GetName(), msg);
                if (TryGetClientFor(ch, out var c))
                    c.SysMessage(msg);
            };
            _commands.OnCharVisualUpdate += (ch) =>
            {
                if (TryGetClientFor(ch, out var c))
                    c.SendSelfRedraw();
            };
            Item.OnVisualUpdate = item =>
            {
                foreach (var c in _clients.Values)
                    c.SendItemVisualUpdate(item);
            };
            // @Unequip on engine/script unequip paths (Source-X ITRIG_UnEquip
            // from ItemBounce / RemoveSelf). The client pickup path fires its
            // own; this covers the .UNEQUIP verb and worn .REMOVE so worn-item
            // cleanup blocks run.
            Item.OnItemUnequipped = (item, wearer) =>
                _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Unequip,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = wearer, ItemSrc = item });
            // Script OPEN / DCLICK / USE verbs: resolve the acting console to
            // its GameClient and replay the real client paths. Non-client
            // consoles (telnet, headless script runs) stay ack-only.
            Item.OnScriptOpen = (item, console) =>
            {
                if (console is SphereNet.Game.Clients.GameClient gc)
                    gc.OpenContainerFromScript(item);
            };
            Item.OnScriptDClick = (item, console) =>
            {
                if (console is SphereNet.Game.Clients.GameClient gc)
                    gc.HandleDoubleClick(item.Uid.Value);
            };
            ObjBase.OnScriptSingleClick = (obj, console) =>
            {
                if (console is not SphereNet.Game.Clients.GameClient gc)
                    return false;
                gc.HandleSingleClick(obj.Uid.Value);
                return true;
            };
            _commands.OnScriptParityWarning += (ch, verb, reason) =>
            {
                _log.LogWarning("Script parity warning: char=0x{Char:X8} cmd={Cmd} reason={Reason}",
                    ch.Uid.Value, verb, reason);
            };
            _commands.OnCharacterResyncRequested += target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.Resync();
            };
            _commands.OnCharacterMapChanged += target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.HandleMapChanged();
            };
            _commands.OnCharacterSelfRedraw += target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.SendSelfRedraw();
            };
            _commands.OnTeleportTargetRequested += gm =>
            {
                if (TryGetClientFor(gm, out var c))
                    c.BeginTeleportTarget();
            };
            _commands.OnAddTargetRequested += (gm, addToken) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginAddTarget(addToken);
                        break;
                    }
                }
            };
            _commands.OnShowDialogRequested += (gm, title, lines) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.ShowTextDialog(title, lines);
                        break;
                    }
                }
            };
            _commands.OnShowTargetRequested += (gm, args) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginShowTarget(args);
                        break;
                    }
                }
            };
            _commands.OnEditTargetRequested += (gm, args) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginEditTarget(args);
                        break;
                    }
                }
            };
            _commands.OnEditRequested += (gm, uid, page) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.ShowInspectDialog(uid, page);
                        break;
                    }
                }
            };
            // Source-X parity (CClient.cpp:921) — generic X-prefix verb fallback.
            _commands.OnAddVerbTargetRequested += (gm, verb, verbArgs) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginXVerbTarget(verb, verbArgs);
                        break;
                    }
                }
            };
            // Phase C: NUKE / NUKECHAR / NUDGE area-target verbs.
            _commands.OnAreaTargetRequested += (gm, verb, range) =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginAreaTarget(verb, range); break; }
            };
            _commands.OnSummonToTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginSummonToTarget(); break; }
            };
            _commands.OnControlTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginControlTarget(); break; }
            };
            _commands.OnDupeTargetRequested += (gm, count) =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginDupeTarget(count); break; }
            };
            _commands.OnHealTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginHealTarget(); break; }
            };
            _commands.OnBankTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginBankTarget(); break; }
            };
            _commands.OnBankSelfRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.OpenBankBox(); break; }
            };
            _commands.OnUnmountRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.UnmountSelf(); break; }
            };
            _commands.OnAnimRequested += (gm, animId) =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.PlayOwnAnimation(animId); break; }
            };
            _commands.OnMountTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginMountTarget(); break; }
            };
            _commands.OnOpenPaperdollRequested += (gm, target) =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.SendPaperdoll(target); break; }
            };
            _commands.OnShowSkillsRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.SendSkillList(); break; }
            };
            _commands.OnPageReceived += (player, message) =>
            {
                var pageMsg = $"[PAGE from {player.GetName()}] {message}";
                _log.LogInformation("{PageMessage}", pageMsg);
                var staffNotified = false;
                foreach (var c in _clients.Values)
                {
                    if (c.Character != null && c.Character != player &&
                        c.Character.PrivLevel >= PrivLevel.Counsel)
                    {
                        c.SysMessage(pageMsg);
                        staffNotified = true;
                    }
                }
                if (!staffNotified)
                {
                    foreach (var c in _clients.Values)
                    {
                        if (c.Character == player)
                        {
                            c.SysMessage("No staff members are currently online. Your page has been logged.");
                            break;
                        }
                    }
                }
            };
            _commands.OnSummonCageTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                    if (c.Character == gm) { c.BeginSummonCageTarget(); break; }
            };
            _commands.OnInspectRequested += (gm, uid) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        var obj = _world.FindObject(new Serial(uid));
                        if (obj != null)
                            c.OpenInspectPropDialog(obj, 0);
                        break;
                    }
                }
            };
            _commands.OnInspectTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginInspectTarget();
                        break;
                    }
                }
            };
            _commands.OnRemoveTargetRequested += gm =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == gm)
                    {
                        c.BeginRemoveTarget();
                        break;
                    }
                }
            };
            _commands.OnKillRequested += (gm, targetUid) =>
            {
                if (!targetUid.HasValue || targetUid.Value.Value == 0)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                    {
                        gmClient.SysMessage("Select target to kill.");
                        gmClient.BeginKillTarget();
                    }
                    return;
                }

                var victim = _world.FindChar(targetUid.Value);

                if (victim == null)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                        gmClient.SysMessage("Kill: target not found.");
                    return;
                }

                if (victim.IsDead || victim.IsDeleted)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient2))
                        gmClient2.SysMessage($"'{victim.Name}' is already dead.");
                    return;
                }

                BroadcastLightningStrike(victim);
                ProcessDeathWithEffects(victim, gm);
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient3))
                    gmClient3.SysMessage($"Killed '{victim.Name}'.");
            };
            _commands.OnResurrectRequested += (gm, targetUid) =>
            {
                // No UID  → resurrect self.
                // With UID → resurrect that character (GM-only command, so we
                // don't gate on PrivLevel here; SpeechEngine.Register already
                // restricts the verb).
                var victim = !targetUid.HasValue || targetUid.Value.Value == 0
                    ? gm
                    : _world.FindChar(targetUid.Value);
                if (victim == null)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                        gmClient.SysMessage("Resurrect: target not found.");
                    return;
                }

                if (!_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                {
                    // Offline / NPC — no client-side ghost transition needed,
                    // just clear the dead state on the character object so the
                    // next login or AI tick sees them alive.
                    if (victim.IsDead)
                        victim.Resurrect();
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient2))
                        gmClient2.SysMessage($"Resurrected '{victim.Name}'.");
                    return;
                }

                if (!victim.IsDead)
                {
                    if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient3))
                        gmClient3.SysMessage($"'{victim.Name}' is not dead.");
                    return;
                }

                victimClient.OnResurrect();
            };
            _commands.OnResurrectTargetRequested += gm =>
            {
                if (_clientsByCharUid.TryGetValue(gm.Uid, out var gmClient))
                    gmClient.BeginResurrectTarget();
            };
            _commands.OnCastRequested += (ch, spellId) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == ch)
                    {
                        c.HandleCastSpell((SpellType)spellId, 0);
                        break;
                    }
                }
            };
            _commands.OnScriptDialogRequested += (ch, dialogName, page) =>
            {
                foreach (var c in _clients.Values)
                {
                    if (c.Character == ch)
                    {
                        if (c.TryShowScriptDialog(dialogName, page))
                            break;

                        // Collect a few close-match suggestions so the user
                        // can tell if it's a typo ("d_moongate" vs
                        // "d_moongates") or a truly missing dialog.
                        var suggestions = CollectDialogSuggestions(dialogName, maxCount: 5);
                        string hint = suggestions.Count == 0
                            ? ""
                            : "  Similar: " + string.Join(", ", suggestions);
                        c.SysMessage($"Dialog '{dialogName}' not found.{hint}");
                        break;
                    }
                }
            };
            // Reuse the registry if LoadDefinitions already created + populated it
            // (definitions now load before the world save so legacy NPC bodies
            // resolve). A plain `new` here would drop the loaded spell defs.
            _spellRegistry ??= new SpellRegistry();
            _spellEngine = new SpellEngine(_world, _spellRegistry);
            // Movement-based cast interruption (deferred from MovementEngine creation:
            // SpellEngine did not exist there). MovementEngine.cs reads SpellEngine.
            _movement.SpellEngine = _spellEngine;
            // Typed field step/stand effects (fire burns, poison poisons,
            // paralyze freezes, barriers inert) — Source-X field spell touch.
            SphereNet.Game.Objects.Characters.Character.FieldTouchHook =
                (ch, fieldItem) => _spellEngine?.ApplyFieldTouch(ch, fieldItem) ?? false;
            _world.OnSectorLight = (character, level) =>
            {
                if (TryGetClientFor(character, out var lightClient))
                    lightClient.Send(new PacketGlobalLight(level));
            };
            _saver.GetSpellEffectRecords = _spellEngine.GetPersistedEffectRecords;
            _spellEngine.TriggerDispatcher = _triggerDispatcher;
            _spellEngine.OnPlaySound = (pos, soundId) =>
            {
                var pkt = new PacketSound(soundId, pos.X, pos.Y, pos.Z);
                BroadcastNearby(pos, 18, pkt, 0);
            };
            _spellEngine.OnItemRemoved = item =>
            {
                if (item == null || item.IsDeleted) return;
                BroadcastNearby(item.Position, 18, new PacketDeleteObject(item.Uid.Value), 0);
                _world.DeleteObject(item);
                item.Delete();
            };
            _spellEngine.OnSpellInterrupt = (caster, _) =>
            {
                _triggerDispatcher?.FireCharTrigger(caster, CharTrigger.SpellInterrupt,
                    new TriggerArgs { CharSrc = caster });
            };
            // Shared cast-resolution pipeline (Source-X Spell_CastDone). Fires the
            // @SpellSuccess / @SpellFail char triggers AND the per-spell [SPELL]
            // ON= block for EVERY caster — player, NPC, direct. @SpellEffect is
            // NOT fired here: Source-X fires it on each AFFECTED char with the
            // full LOCAL contract, which SpellEngine.ApplyCharEffect now does.
            _spellEngine.OnCastResolved = (caster, spell, success) =>
            {
                if (_triggerDispatcher == null) return;
                int spellId = (int)spell;
                if (success)
                {
                    _triggerDispatcher.FireCharTrigger(caster, CharTrigger.SpellSuccess,
                        new TriggerArgs { CharSrc = caster, N1 = spellId });
                    _triggerDispatcher.FireSpellTrigger(spell, "Success",
                        caster, new TriggerArgs { CharSrc = caster, N1 = spellId });
                }
                else
                {
                    _triggerDispatcher.FireCharTrigger(caster, CharTrigger.SpellFail,
                        new TriggerArgs { CharSrc = caster, N1 = spellId });
                    _triggerDispatcher.FireSpellTrigger(spell, "Fail",
                        caster, new TriggerArgs { CharSrc = caster, N1 = spellId });
                }
            };
            // CANCAST.<spell> property backend: mana, primary-skill requirement
            // and region antimagic — the checks Spell_CanCast front-loads.
            SphereNet.Game.Objects.Characters.Character.OnCanCastCheck = (ch, spellId) =>
            {
                var canCastDef = _spellEngine?.GetSpellDef((SpellType)spellId);
                if (canCastDef == null || ch.IsDead || ch.IsStatFlag(StatFlag.Freeze))
                    return false;
                if (ch.Mana < canCastDef.ManaCost)
                    return false;
                foreach (var (reqSkill, reqVal) in canCastDef.SkillReq)
                    if (ch.GetSkill(reqSkill) < reqVal)
                        return false;
                var castRegion = _world.FindRegion(ch.Position);
                if (castRegion != null && castRegion.IsFlag(SphereNet.Core.Enums.RegionFlag.NoMagic))
                    return false;
                return true;
            };

            _spellEngine.OnPersonalLightChanged = target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.SendPersonalLight();
            };
            _spellEngine.OnSysMessage = (recipient, text) =>
            {
                // Route Recall/Mark/Gate/Poison spell messages to the recipient's
                // own client, mirroring Source-X CClientMsg::SysMessage semantics.
                if (TryGetClientFor(recipient, out var c))
                    c.SysMessage(text);
            };
            _spellEngine.OnPlaySoundTo = (recipient, sound) =>
            {
                // Client-only sound (Source-X m_pClient->addSound): the
                // hallucination trip must not be heard by bystanders.
                if (TryGetClientFor(recipient, out var c))
                    c.NetState.Send(new PacketSound(sound,
                        recipient.X, recipient.Y, recipient.Z));
            };
            _spellEngine.OnViewRefresh = recipient =>
            {
                // Source-X addChar/addPlayerSee on the hallucination tick and
                // at effect add/remove: re-send nearby mobiles AND ground
                // items so the random bodies/hues re-roll — and so the last
                // trip colors clear the moment the effect ends.
                if (!TryGetClientFor(recipient, out var c))
                    return;
                foreach (var other in _world.GetCharsInRange(recipient.Position, 18))
                    if (!other.IsDeleted)
                        c.SendCharacterView(other);
                foreach (var item in _world.GetItemsInRange(recipient.Position, 18))
                    if (!item.IsDeleted)
                        c.SendItemView(item);
            };
            _spellEngine.OnOverheadEmote = (speaker, text) =>
            {
                // Overhead line from the character, visible nearby (Source-X
                // CChar::Speak — the drunk *hic*).
                var pkt = new PacketSpeechUnicodeOut(
                    speaker.Uid.Value, speaker.BodyId,
                    0x02, // emote-style speech
                    0x0022, 3, "ENU",
                    speaker.Name ?? "", text);
                BroadcastNearby(speaker.Position, 18, pkt, 0);
            };
            SphereNet.Game.Objects.Characters.Character.SpellMemoryEffectRemover =
                mem => _spellEngine.RemoveEffectByMemory(mem);
            SphereNet.Game.Objects.Characters.Character.NpcWantThisItem =
                (npc, wantedItem) => _npcAI.GetWantScore(npc, wantedItem);
            SphereNet.Game.Objects.Characters.Character.NpcCanEatFood =
                (npc, foodItem) => _npcAI.NpcCanEat(npc, foodItem);
            // Gem-spawned NPCs run the same chardef script init as GM .add
            // (Source-X NPC_LoadScript): @Create, @CreateLoot, then
            // @NPCRestock — the pack's monster gear and backpack loot live in
            // those bodies, so without this every spawned monster dropped
            // nothing.
            SphereNet.Game.Components.SpawnComponent.OnNpcScriptInit = npc =>
            {
                _triggerDispatcher.FireCharTrigger(npc,
                    SphereNet.Core.Enums.CharTrigger.Create,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
                _triggerDispatcher.FireCharTrigger(npc,
                    SphereNet.Core.Enums.CharTrigger.CreateLoot,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
                _triggerDispatcher.FireCharTrigger(npc,
                    SphereNet.Core.Enums.CharTrigger.NPCRestock,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = npc });
            };
            _spellEngine.OnCasterFacingChanged = caster =>
            {
                // Source-X UpdateMove(GetTopPoint()) — broadcast new facing only.
                // Reuse the lightweight 0x77 MobileMoving so nearby clients
                // re-render the mobile in its new direction without a full
                // 0x78 char info refresh.
                byte dirByte = (byte)((byte)caster.Direction & 0x07);
                byte flags = 0;
                if (caster.IsInWarMode) flags |= 0x40;
                if (caster.IsDead) flags |= 0x02;
                byte noto = caster.IsPlayer ? (byte)1 : (byte)3;
                var pkt = new PacketMobileMoving(
                    caster.Uid.Value, caster.BodyId,
                    caster.X, caster.Y, caster.Z, dirByte,
                    caster.Hue, flags, noto);
                BroadcastNearby(caster.Position, 18, pkt, 0);
            };
            // Styled hook: @SpellCast LOCAL.WOPColor / LOCAL.WOPFont override the
            // mantra's hue and font; 0 falls back to the caster defaults.
            _spellEngine.OnSpellWordsEx = (caster, words, wopHue, wopFont) =>
            {
                ushort hue = wopHue != 0
                    ? wopHue
                    : caster.SpeechColor != 0 ? caster.SpeechColor : (ushort)0x03B2;
                byte font = wopFont != 0 ? wopFont : (byte)3;
                var pkt = new PacketSpeechUnicodeOut(
                    caster.Uid.Value,
                    caster.BodyId,
                    0x00,
                    hue,
                    font,
                    "TRK",
                    caster.Name ?? "",
                    words);
                BroadcastNearby(caster.Position, 18, pkt, 0);
            };
            // NPC cast completion FX — mirrors the player completion path's
            // bolt/impact packet (ClientCombatHandler.TickSpellCast) so NPC
            // spells are visible to observers.
            _spellEngine.OnNpcCastFx = (caster, target, def) =>
            {
                ushort gfx = def.EffectId;
                if (gfx == 0) return;
                var dst = target ?? caster;
                byte effectType = def.IsFlag(SpellFlag.FxBolt) ? (byte)1 : (byte)3;
                var fx = new PacketEffect(
                    effectType,
                    effectType == 1 ? caster.Uid.Value : dst.Uid.Value,
                    dst.Uid.Value,
                    gfx,
                    dst.X, dst.Y, (short)dst.Z,
                    dst.X, dst.Y, (short)dst.Z,
                    10, 30, true, false);
                BroadcastNearby(dst.Position, 18, fx, 0);
            };
            _spellEngine.OnCastAnimation = (caster, animId) =>
            {
                // Legacy 0x6E gets the body-translated group; KR/EC clients
                // get the body-agnostic 0xE2 Spell gesture instead.
                ushort anim = caster.IsMounted
                    ? MapAnimToMounted(animId)
                    : BodyAnimTranslator.Translate(caster.BodyId, animId);
                GameClient.BroadcastAnimation(caster, anim, NewAnimationGesture.Spell, 18,
                    BroadcastNearby, ForEachClientInRange);
            };
            _spellEngine.OnSpellTeleport = (caster, dest, oldMap) =>
            {
                if (TryGetClientFor(caster, out var c))
                {
                    if (oldMap != caster.MapIndex)
                        c.HandleMapChanged();
                    else
                        c.Resync();
                    var snd = new PacketSound(0x01FE, caster.X, caster.Y, caster.Z);
                    BroadcastNearby(caster.Position, 18, snd, 0);
                }
            };
            _spellEngine.OnTargetKilled = (victim, killer) =>
            {
                _log.LogDebug("[death_path] spell-damage victim=0x{V:X} killer=0x{K:X}",
                    victim.Uid.Value, killer?.Uid.Value ?? 0);
                var effectiveKiller = killer != null ? ResolveEffectiveOffender(killer) : null;

                // @Kill/@Death are fired inside ProcessDeath so RETURN 1 can skip
                // killer credit / cancel the death (a still-living victim after the
                // call means the death was vetoed).
                var victimPos = victim.Position;
                byte victimDir = (byte)((byte)victim.Direction & 0x07);
                var corpse = _deathEngine.ProcessDeath(victim, effectiveKiller);
                if (!victim.IsDead) return; // @Death cancelled the death
                if (effectiveKiller != null)
                    effectiveKiller.FightTarget = Serial.Invalid;

                BroadcastDeathEffects(victim, effectiveKiller, corpse, victimPos, victimDir);
            };
            Character.OnLifecycleKill = (victim, killer) =>
            {
                if (victim.IsDead) return;
                _log.LogDebug("[death_path] lifecycle victim=0x{V:X} killer=0x{K:X}",
                    victim.Uid.Value, killer?.Uid.Value ?? 0);

                var effectiveKiller = killer != null ? ResolveEffectiveOffender(killer) : null;

                // @Kill/@Death are fired inside ProcessDeath so RETURN 1 can skip
                // killer credit / cancel the death (a still-living victim after the
                // call means the death was vetoed).
                var victimPos = victim.Position;
                byte victimDir = (byte)((byte)victim.Direction & 0x07);
                var corpse = _deathEngine.ProcessDeath(victim, effectiveKiller);
                if (!victim.IsDead) return; // @Death cancelled the death
                if (effectiveKiller != null)
                    effectiveKiller.FightTarget = Serial.Invalid;

                BroadcastDeathEffects(victim, effectiveKiller, corpse, victimPos, victimDir);
            };
            Character.OnLifecycleResurrect = victim =>
            {
                if (!victim.IsDead) return;
                if (_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                    victimClient.OnResurrect();
                else
                    victim.Resurrect();
            };
            Character.OnJailReleaseRequested = inmate =>
            {
                inmate.ClearStatFlag(StatFlag.Freeze);
                inmate.RemoveTag("JAIL_RELEASE");
                inmate.RemoveTag("JAIL_CELL");
                var spawnPos = new Point3D(1495, 1629, 10, 0);
                _world.MoveCharacter(inmate, spawnPos);
                if (_clientsByCharUid.TryGetValue(inmate.Uid, out var inmateClient))
                    inmateClient.Resync();
            };
            Character.OnHungerDecay = ch =>
            {
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Hunger,
                    new TriggerArgs { CharSrc = ch });
            };
            Character.OnCriminalCheck = ch =>
            {
                // ARGN1 = criminal-flag duration seconds (default); RETURN 1 cancels
                // the flag, otherwise the (possibly script-overridden) ARGN1 sets
                // how long the criminal flag lasts.
                var args = new TriggerArgs { CharSrc = ch, N1 = Character.CriminalTimerSeconds };
                if (_triggerDispatcher?.FireCharTrigger(ch, CharTrigger.Criminal, args) == TriggerResult.True)
                    return null;
                if (_world != null && _triggerDispatcher != null)
                {
                    // Nearby NPCs/guards witness the crime (@SeeCrime, <src> = criminal).
                    foreach (var witness in _world.GetCharsInRange(ch.Position, 12))
                    {
                        if (witness == ch || witness.IsPlayer || witness.IsDead) continue;
                        _triggerDispatcher.FireCharTrigger(witness, CharTrigger.SeeCrime,
                            new TriggerArgs { CharSrc = ch });
                    }
                }
                return args.N1;
            };

            // A witness who noticed a crime (CrimeWitnessService.CheckCrimeSeen)
            // fires @SeeCrime — or @SeeSnoop for a snoop — on the witness, with
            // <src> = the criminal and ARGO = the victim. @SeeSnoop RETURN 1 makes
            // that witness ignore the snoop; ARGN1 on either asks the engine to
            // flag the criminal globally (call guards) instead of personal grey.
            SphereNet.Game.Objects.Characters.CrimeWitnessService.OnCrimeNoticed =
                (witness, criminal, mark, isSnoop) =>
                {
                    var trig = isSnoop ? CharTrigger.SeeSnoop : CharTrigger.SeeCrime;
                    var args = new TriggerArgs { CharSrc = criminal, O1 = mark, N1 = 0 };
                    var result = _triggerDispatcher?.FireCharTrigger(witness, trig, args);
                    if (isSnoop && result == TriggerResult.True)
                        return null; // @SeeSnoop RETURN 1 — witness ignores it
                    return args.N1 != 0;
                };
            GameRegion.ClientCountProvider = regionObj =>
            {
                int count = 0;
                foreach (var c in _clients.Values)
                {
                    if (c.Character == null || !c.IsPlaying) continue;
                    switch (regionObj)
                    {
                        case GameRegion reg when reg.Contains(c.Character.Position):
                        case Room room when room.Contains(c.Character.Position):
                            count++;
                            break;
                    }
                }
                return count;
            };
            _deathEngine = new DeathEngine(_world);
            // NOTE: DeathEngine.OnDeath fires from inside ProcessDeath, after the
            // corpse object is created but BEFORE the corpse spawn packets are
            // broadcast. We deliberately do NOT call GameClient.OnCharacterDeath
            // here, because:
            //   * Source-X order is: corpse appears → 0xAF death anim → ghost
            //     transition. Calling OnCharacterDeath inside this callback
            //     flips the player to a ghost body BEFORE the killer's client
            //     has even received the corpse packet, so observers briefly
            //     see "ghost without a corpse on the floor".
            //   * Both code paths that trigger player death (NpcAI.OnNpcKill
            //     in Program.cs and Player-vs-Player in GameClient.TrySwingAt)
            //     already invoke c.OnCharacterDeath() AFTER they finish sending
            //     corpse + 0xAF packets. Doing it here would call it twice.
            // This hook is kept for non-visual side effects (logging, party
            // bookkeeping, etc.) — currently nothing else needs it.
            // Dying in the saddle: drop the rider from the mount before the
            // corpse snapshot, delete the mount-layer item for observers and
            // re-show the mount NPC (Source-X Death → Horse_UnMount order).
            _deathEngine.DismountHook = victim =>
            {
                uint mountItemUid = victim.GetEquippedItem(Layer.Horse)?.Uid.Value ?? 0;
                var mountNpc = _mountEngine?.Dismount(victim);
                if (mountItemUid != 0)
                    BroadcastNearby(victim.Position, 18, new PacketDeleteObject(mountItemUid), 0);
                if (mountNpc != null)
                {
                    mountNpc.ClearStatFlag(StatFlag.Ridden);
                    BroadcastCharacterAppear(mountNpc);
                }
            };
            _deathEngine.LootingIsACrime = _config.LootingIsACrime;
            _deathEngine.CorpseDecayNPC = _config.CorpseNpcDecay * 60;
            _deathEngine.CorpseDecayPlayer = _config.CorpsePlayerDecay * 60;
            _deathEngine.PartyManager = _partyManager;
            _deathEngine.TriggerDispatcher = _triggerDispatcher;
            _tradeManager = new TradeManager();
            // Source-X kill record: log the player death and echo it to the
            // victim's party (LOGM_KILLS + m_pParty->SysMessageAll).
            _deathEngine.KillMessageHook = (victim, msg) =>
            {
                _log.LogInformation("[kill] {Message}", msg);
                var party = _partyManager?.FindParty(victim.Uid);
                if (party == null) return;
                foreach (var member in party.Members)
                    if (_clientsByCharUid.TryGetValue(member, out var memberClient))
                        memberClient.SysMessage(msg);
            };
            // Source-X MakeCorpse: a corpseless summon bursts the spell-fizzle
            // effect (ITEMID_FX_SPELL_FAIL 0x3735) instead of just vanishing.
            _deathEngine.ConjuredVanishEffectHook = victim =>
                BroadcastNearby(victim.Position, 18, new PacketEffect(
                    3, victim.Uid.Value, victim.Uid.Value, 0x3735,
                    victim.X, victim.Y, victim.Z, victim.X, victim.Y, victim.Z,
                    1, 30, false, false), 0);
            // Source-X CChar::Death Trade_Delete: an open secure trade is
            // cancelled before the corpse forms so the returned items reach
            // the loot drop. Client route closes both windows; the clientless
            // fallback (offline/GM kill) just returns the items.
            _deathEngine.CancelTradesHook = victim =>
            {
                var trade = _tradeManager?.FindTradeFor(victim);
                if (trade == null || _tradeManager == null) return;
                if (_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                {
                    victimClient.CancelActiveTradeOnDeath();
                    return;
                }
                TradeManager.ReturnTradeItems(_world, trade);
                trade.Cancel();
                _tradeManager.EndTrade(trade);
                _world.RemoveItem(trade.InitiatorContainer);
                _world.RemoveItem(trade.PartnerContainer);
            };
            _npcAI = new NpcAI(_world, _config);
            _npcTimerWheel = new SphereNet.Game.Scheduling.TimerWheel(Environment.TickCount64);
            _recordingEngine = new SphereNet.Game.Recording.RecordingEngine(
                Path.Combine(ResolvePath(basePath, _config.WorldSaveDir), "recordings"));
            _recordingEngine.SnapshotNearbyCharacters = (center, range) =>
            {
                var result = new List<byte[]>();
                foreach (var ch in _world.GetCharsInRange(center, range))
                {
                    if (ch.IsPlayer && !ch.IsOnline) continue;
                    if (ch.IsInvisible || ch.IsStatFlag(StatFlag.Hidden)) continue;
                    var equip = new List<(uint, ushort, byte, ushort)>();
                    for (int layer = 1; layer <= (int)Layer.Horse; layer++)
                    {
                        var item = ch.GetEquippedItem((Layer)layer);
                        if (item != null)
                            equip.Add((item.Uid.Value, item.DispIdFull, (byte)layer, item.Hue));
                    }
                    byte flags = 0;
                    if (ch.IsInWarMode) flags |= 0x40;
                    if (ch.IsInvisible) flags |= 0x80;
                    var pkt = new PacketDrawObject(
                        ch.Uid.Value, ch.BodyId, ch.X, ch.Y, ch.Z,
                        (byte)ch.Direction, ch.Hue, flags, 0x01,
                        equip.ToArray());
                    result.Add(pkt.Build().Span.ToArray());
                }
                return result;
            };
            if (_config.StateRecordingEnabled)
            {
                try
                {
                    var stateDbPath = Path.Combine(ResolvePath(basePath, _config.WorldSaveDir), "state_recording.db");
                    _stateRecorder = new SphereNet.Server.Recording.StateRecorder(
                        stateDbPath, _loggerFactory.CreateLogger("StateRecorder"),
                        _config.StateRecordPlayersOnly,
                        _config.StateRecordMoveScanMs,
                        _config.StateRecordSnapshotMs);
                    _stateRecorder.Initialize();
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "StateRecorder init failed — state recording disabled");
                    _stateRecorder = null;
                }
            }

            if (_config.MacroEnabled)
                _macroEngine = new SphereNet.Server.Macro.MacroEngine(_config.MacroMaxSteps, _config.MacroMaxLoopMinutes);

            // Per-action NPC AI triggers (@NPCActWander fires every wander step
            // of every active NPC, @NPCLookAtChar/Item every scan hit, the Act
            // triggers every combat/follow/cast action). Install the fire only
            // when a script hooks the trigger (IsTrigUsed gate) — otherwise a
            // 20-30K NPC world pays a full dispatch walk per idle NPC per
            // second for nothing.
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCLookAtChar))
                _npcAI.OnNpcLookAtChar = (npc, target) =>
                    _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCLookAtChar,
                        new TriggerArgs { CharSrc = target, N1 = target.Uid.Value > int.MaxValue ? 0 : (int)target.Uid.Value }) == TriggerResult.True;
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActFight))
                _npcAI.OnNpcActFight = (npc, target, dist, motivation) =>
                {
                    // Source-X NPC_Act_Fight args: ARGN1=distance, ARGN2=motivation,
                    // ARGO=target. The script may flip motivation (ARGN2 readback,
                    // wired through RunWrapped) or force a cast via LOCAL.skill +
                    // LOCAL.spell. RETURN 1 fully handles the action.
                    var locals = new SphereNet.Scripting.Variables.VarMap();
                    var args = new TriggerArgs
                    {
                        CharSrc = target, O1 = target,
                        N1 = dist, N2 = motivation, Locals = locals
                    };
                    var res = _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActFight, args);
                    var forcedSkill = locals.Has("skill")
                        ? (SkillType)(int)locals.GetInt("skill") : SkillType.None;
                    var forcedSpell = locals.Has("spell")
                        ? (SpellType)(int)locals.GetInt("spell") : SpellType.None;
                    // LOCAL.skiphardcoded = bypass the engine's breath/throw specials
                    // (Source-X fSkipHardcoded) while keeping flee/magery/melee.
                    bool skipHardcoded = locals.Has("skiphardcoded") && locals.GetInt("skiphardcoded") != 0;
                    return new NpcAI.NpcFightDecision(
                        res == TriggerResult.True, args.N2, forcedSkill, forcedSpell, skipHardcoded);
                };
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActWander))
                _npcAI.OnNpcActWander = npc =>
                    _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActWander,
                        new TriggerArgs { CharSrc = npc }) == TriggerResult.True;
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActFollow))
                _npcAI.OnNpcActFollow = (npc, target) =>
                    _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActFollow,
                        new TriggerArgs { CharSrc = target, O1 = target }) == TriggerResult.True;
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCActCast))
                _npcAI.OnNpcActCast = (npc, target, spell, wandUse) =>
                {
                    // Source-X NPC_FightMagery args: ARGN1=spell, ARGN2=wand-use,
                    // ARGO=target, LOCAL.HealThreshold seeded from config. RETURN 1
                    // aborts the cast and reverts to melee; otherwise ARGN1 carries
                    // the (possibly script-overridden) spell back (via RunWrapped),
                    // and LOCAL.target = a uid redirects the cast (Source-X REF1).
                    var locals = new SphereNet.Scripting.Variables.VarMap();
                    locals.SetInt("HealThreshold", _config.NpcHealThreshold);
                    var args = new TriggerArgs
                    {
                        CharSrc = target, O1 = target, N1 = (int)spell,
                        N2 = wandUse ? 1 : 0, Locals = locals
                    };
                    var res = _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCActCast, args);
                    if (res == TriggerResult.True)
                        return new NpcAI.NpcCastDecision(true, spell, target); // abort → melee
                    var newSpell = (SpellType)args.N1;
                    if (newSpell == SpellType.None) newSpell = spell;
                    var newTarget = target;
                    if (locals.Has("target"))
                    {
                        var redirect = _world.FindChar(new Serial((uint)locals.GetInt("target")));
                        if (redirect != null && !redirect.IsDeleted)
                            newTarget = redirect;
                    }
                    return new NpcAI.NpcCastDecision(false, newSpell, newTarget);
                };
            bool npcLookAtItemUsed = _triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCLookAtItem);
            bool npcSeeWantItemUsed = _triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCSeeWantItem);
            if (npcLookAtItemUsed || npcSeeWantItemUsed)
                _npcAI.OnNpcLookAtItem = (npc, item, dist, want) =>
                {
                    // Source-X NPC_LookAtItem contract (CCharNPCAct.cpp:954):
                    // ARGN1 = distance, ARGN2 = want-score (writable), ARGO =
                    // the item. RETURN 1 = script took the item over, RETURN 0
                    // = ignore it; otherwise ARGN2 reads back.
                    var args = new TriggerArgs
                    {
                        CharSrc = npc, O1 = item, ItemSrc = item, N1 = dist, N2 = want
                    };
                    var res = npcLookAtItemUsed
                        ? _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCLookAtItem, args)
                        : TriggerResult.Default;
                    return new NpcAI.NpcLookDecision(
                        res == TriggerResult.True, res == TriggerResult.False, args.N2);
                };
            if (npcSeeWantItemUsed)
                _npcAI.OnNpcSeeWantItem = (npc, item) =>
                    _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCSeeWantItem,
                        new TriggerArgs { CharSrc = npc, O1 = item, ItemSrc = item }) == TriggerResult.True;
            if (_triggerDispatcher.IsCharTriggerUsed(CharTrigger.NPCAction))
                _npcAI.OnNpcAction = npc =>
                    _triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCAction,
                        new TriggerArgs { CharSrc = npc }) == TriggerResult.True;

            _npcAI.OnNpcSay = (npc, text) =>
            {
                NpcSpeak(npc, text);
            };
            _npcAI.OnGuardLightningStrike = target =>
            {
                BroadcastLightningStrike(target);
            };
            _npcAI.OnNpcTeleport = npc =>
            {
                BroadcastCharacterAppear(npc);
            };
            _npcAI.OnNpcFacingChanged = npc =>
            {
                BroadcastFacingUpdate(npc);
            };
            _npcAI.OnNpcOpenDoor = (npc, door) =>
            {
                if (!DoorHelper.TryOpenDoorState(door))
                    return false;
                BroadcastNearby(door.Position, 18,
                    new PacketSound(0x00EA, door.X, door.Y, door.Z), 0);
                BroadcastNearby(door.Position, 18,
                    new PacketWorldItem(door.Uid.Value, door.DispIdFull, door.Amount,
                        door.X, door.Y, door.Z, door.Hue), 0);
                return true;
            };
            // NPC_AI_EXTRA night detection is wired AFTER WeatherEngine is constructed
            // (see below). The old `if (_weatherEngine != null)` guard was always false
            // here (the engine doesn't exist yet), so it was skipped entirely and NPCs
            // saw permanent daylight. NPC_AI_MOVEOBSTACLES broadcast:
            _npcAI.OnNpcMovedItem = (npc, item) =>
                BroadcastNearby(item.Position, 18,
                    new PacketWorldItem(item.Uid.Value, item.DispIdFull, item.Amount,
                        item.X, item.Y, item.Z, item.Hue), 0);
            _npcAI.OnNpcFidget = npc =>
            {
                var fidget = Random.Shared.Next(2) == 0
                    ? AnimationType.Fidget1
                    : AnimationType.FidgetYawn;
                ushort anim = npc.IsMounted
                    ? MapAnimToMounted((ushort)fidget)
                    : BodyAnimTranslator.Translate(npc.BodyId, (ushort)fidget);
                GameClient.BroadcastAnimation(npc, anim, NewAnimationGesture.Fidget, 18,
                    BroadcastNearby, ForEachClientInRange);
            };
            // Source-X @HitTry/@HitCheck contract: the trigger runs on the
            // attacker but SRC = the victim and ARGO = the weapon.
            _npcAI.OnNpcHitTry = (attacker, target, weapon, swingTenths) =>
            {
                if (_triggerDispatcher == null) return swingTenths;
                var args = new TriggerArgs { CharSrc = target, O1 = weapon, ItemSrc = weapon, N1 = swingTenths };
                if (_triggerDispatcher.FireCharTrigger(attacker, CharTrigger.HitTry, args) == TriggerResult.True)
                    return -1; // RETURN 1 aborts the swing
                return Math.Max(1, args.N1);
            };
            _npcAI.OnNpcHitCheck = (attacker, target, weapon, swingNoRange) =>
            {
                if (_triggerDispatcher == null) return (false, swingNoRange);
                var locals = new SphereNet.Scripting.Variables.VarMap();
                locals.SetInt("Recoil_NoRange", swingNoRange ? 1 : 0);
                var args = new TriggerArgs
                {
                    CharSrc = target,
                    O1 = weapon,
                    ItemSrc = weapon,
                    N1 = (int)attacker.CombatSwingState,
                    N2 = (int)CombatEngine.GetWeaponDamageType(weapon),
                    Locals = locals,
                };
                bool forceMiss = _triggerDispatcher.FireCharTrigger(attacker, CharTrigger.HitCheck, args) == TriggerResult.True;
                return (forceMiss, locals.GetInt("Recoil_NoRange") != 0);
            };
            _npcAI.OnNpcAttack = (attacker, target, weapon, damage, ammoUid) =>
            {
                ushort swingAnim = GameClient.GetNpcSwingAction(attacker, weapon);
                // COMBAT_ANIM_HIT_SMOOTH paces the swing animation to the swing time.
                byte animDelay = CombatHelper.GetSwingAnimDelay(GameClient.GetSwingDelayMs(attacker, weapon));
                GameClient.BroadcastAnimation(attacker, swingAnim, NewAnimationGesture.Attack, 18,
                    BroadcastNearby, ForEachClientInRange, animDelay: animDelay);

                if (weapon != null &&
                    (weapon.ItemType == ItemType.WeaponBow || weapon.ItemType == ItemType.WeaponXBow))
                    GameClient.BroadcastRangedProjectile(attacker, target, weapon, BroadcastNearby);

                // Source-X plays one combat sound per swing: the per-weapon miss
                // whoosh on a miss, the per-weapon hit sound on a hit (below). A
                // -1 is a true miss, -2 a full parry; 0 is a connecting hit
                // that armor fully absorbed.
                if (damage == CombatEngine.AttackMiss)
                {
                    // Source-X @HitMiss: SRC = the victim, ARGO = the weapon.
                    var missLocals = new SphereNet.Scripting.Variables.VarMap();
                    if (ammoUid != 0)
                        missLocals.SetInt("Arrow", ammoUid);
                    var missResult = _triggerDispatcher?.FireCharTrigger(attacker, CharTrigger.HitMiss,
                        new TriggerArgs
                        {
                            CharSrc = target,
                            O1 = weapon,
                            ItemSrc = weapon,
                            Locals = missLocals
                        })
                        ?? TriggerResult.Default;
                    if (missResult == TriggerResult.True)
                    {
                        attacker.BeginEquipSwingWait(Environment.TickCount64, 0, noWait: true);
                        return;
                    }
                    BroadcastNearby(attacker.Position, 18,
                        new PacketSound(GameClient.GetWeaponMissSoundPublic(weapon),
                            attacker.X, attacker.Y, attacker.Z), 0);
                    return;
                }
                if (damage == CombatEngine.AttackParried)
                    return; // @HitParry already emitted the block effect.
                if (damage == CombatEngine.AttackResolvedByProc)
                    return; // The proc ran its own damage/death feedback.

                ushort getHitAction = target.IsMounted
                    ? MapAnimToMounted((ushort)AnimationType.GetHit)
                    : BodyAnimTranslator.Translate(target.BodyId, (ushort)AnimationType.GetHit);
                BroadcastNearby(target.Position, 18, new PacketAnimation(target.Uid.Value, getHitAction), 0);

                // Only an armed strike makes a weapon sound; an unarmed creature
                // vocalizes via its own NPC Hit sound (CharDef SOUNDHIT), so don't
                // overlay a human fist sound on a clawed monster.
                if (weapon != null)
                    BroadcastNearby(attacker.Position, 18,
                        new PacketSound(GameClient.GetWeaponHitSoundPublic(weapon),
                            attacker.X, attacker.Y, attacker.Z), 0);

                if (damage > 0)
                {
                    _spellEngine?.TryInterruptFromDamage(target, damage);
                    if (target.HasActiveSkillPending())
                    {
                        int abortedSkill = target.ClearActiveSkillPending();
                        if (abortedSkill >= 0)
                            Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
                    }
                }

                if (damage > 0)
                {
                    // Source-X CRESND_GETHIT: the target's pain vocalization
                    // (human "oomf" / creature SOUNDGETHIT), only on real damage.
                    ushort painSound = GameClient.GetDefenderHitSoundPublic(target);
                    if (painSound != 0)
                        BroadcastNearby(target.Position, 18,
                            new PacketSound(painSound, target.X, target.Y, target.Z), 0);

                    BroadcastDamageNearby(target.Position, 18, target.Uid.Value, damage, 0);
                }

                BroadcastNearby(target.Position, 18,
                    new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits), 0);

                if (damage > 0)
                    GameClient.EmitBloodSplat(_world, target);

                // @Hit / @GetHit and the weapon/armor item triggers fire inside
                // CombatEngine.ResolveAttack (CombatEngine.OnHitDamage) for both
                // the player and NPC paths, before HP is applied — see the hook
                // wired above. The damage passed in here is already final.
            };
            _npcAI.OnNpcAttackNotify = (attacker, target) =>
            {
                // Same Attacker_Add messaging as the player attack path
                // (ClientCombatHandler): both lines render over the ATTACKER in
                // the emote hue; the victim's client alone gets the
                // "*X is attacking you!*" variant.
                const ushort emoteHue = 0x0022;
                string atkName = attacker.Name ?? "";
                var emoteOthers = new PacketSpeechUnicodeOut(
                    attacker.Uid.Value, attacker.BodyId, 2, emoteHue, 3, "TRK",
                    atkName, ServerMessages.GetFormatted(Msg.CombatAttacko, atkName, target.Name));
                var emoteVictim = new PacketSpeechUnicodeOut(
                    attacker.Uid.Value, attacker.BodyId, 2, emoteHue, 3, "TRK",
                    atkName, ServerMessages.GetFormatted(Msg.CombatAttacks, atkName));
                uint victimUid = target.Uid.Value;
                ForEachClientInRange(attacker.Position, 18, 0,
                    (obsCh, obsClient) => obsClient.Send(
                        obsCh.Uid.Value == victimUid ? emoteVictim : emoteOthers));
            };
            _npcAI.OnNpcKill = (killer, victim) =>
            {
                ProcessDeathWithEffects(victim, killer);
                victim.FightTarget = Serial.Invalid;
                if (killer.FightTarget == victim.Uid)
                    killer.FightTarget = Serial.Invalid;
            };
            _npcAI.OnHealerAction = (healer, target, isResurrect) =>
            {
                ushort healAnim = healer.IsMounted ? MapAnimToMounted(16) : BodyAnimTranslator.Translate(healer.BodyId, 16);
                var anim = new PacketAnimation(healer.Uid.Value, healAnim, 4, 1, false, false, 0);
                BroadcastNearby(healer.Position, 18, anim, 0);
                var sound = new PacketSound(isResurrect ? (ushort)0x0214 : (ushort)0x01F2,
                    healer.X, healer.Y, healer.Z);
                BroadcastNearby(healer.Position, 18, sound, 0);

                if (isResurrect && target.IsDead)
                {
                    if (_clientsByCharUid.TryGetValue(target.Uid, out var victimClient))
                        victimClient.OnResurrect();
                    else
                        target.Resurrect();
                }
            };
            _npcAI.OnHealerCure = (healer, target) =>
            {
                ushort healAnim = healer.IsMounted ? MapAnimToMounted(16) : BodyAnimTranslator.Translate(healer.BodyId, 16);
                var anim = new PacketAnimation(healer.Uid.Value, healAnim, 4, 1, false, false, 0);
                BroadcastNearby(healer.Position, 18, anim, 0);
                var sound = new PacketSound(0x01E0, healer.X, healer.Y, healer.Z);
                BroadcastNearby(healer.Position, 18, sound, 0);
            };
            _npcAI.OnVendorRestock = vendor =>
            {
                _triggerDispatcher?.FireCharTrigger(vendor, CharTrigger.NPCRestock,
                    new TriggerArgs { CharSrc = vendor });
            };
            _npcAI.OnWitnessCrime = (witness, criminal) =>
            {
                _npcAI.AlertGuardsInRange(witness.Position, criminal);
            };
            _npcAI.OnWakeNpc = WakeNpc;
            _npcAI.OnNpcSound = (npc, type) =>
            {
                var charDef = DefinitionLoader.GetCharDef(npc.CharDefIndex);
                if (charDef == null) return;
                ushort soundId = type switch
                {
                    CreatureSoundType.Idle => charDef.SoundIdle,
                    CreatureSoundType.Notice => charDef.SoundNotice,
                    CreatureSoundType.Hit => charDef.SoundHit,
                    CreatureSoundType.GetHit => charDef.SoundGetHit,
                    CreatureSoundType.Die => charDef.SoundDie,
                    _ => 0
                };
                if (soundId == 0) return;
                var snd = new PacketSound(soundId, npc.X, npc.Y, npc.Z);
                BroadcastNearby(npc.Position, 18, snd, 0);
            };
            _npcAI.OnNpcBreath = (npc, target, damage) =>
            {
                // @NPCSpecialAction (Source-X) — fires before a special attack.
                // N1 = 1 (breath). RETURN 1 cancels the special (effect + damage).
                if (_triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCSpecialAction,
                        new TriggerArgs { CharSrc = npc, O1 = target, N1 = 1 }) == TriggerResult.True)
                    return;

                FaceAndBroadcastToward(npc, target);

                // Fire breath effect: moving fireball from NPC to target
                var fx = new PacketEffect(0, npc.Uid.Value, target.Uid.Value, 0x36D4,
                    npc.X, npc.Y, (short)(npc.Z + 10),
                    target.X, target.Y, (short)(target.Z + 10),
                    7, 10, true, true);
                BroadcastNearby(npc.Position, 18, fx, 0);
                var breathSound = new PacketSound(0x0227, npc.X, npc.Y, npc.Z);
                BroadcastNearby(npc.Position, 18, breathSound, 0);

                target.Hits -= (short)Math.Min(damage, target.Hits);
                _spellEngine?.TryInterruptFromDamage(target, damage);
                if (target.HasActiveSkillPending())
                {
                    int abortedSkill = target.ClearActiveSkillPending();
                    if (abortedSkill >= 0)
                        Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
                }
                if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
                {
                    target.FightTarget = npc.Uid;
                    target.NextNpcActionTime = 0;
                    WakeNpc(target);
                }
                BroadcastDamageNearby(target.Position, 18, target.Uid.Value, damage, 0);
                var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
                BroadcastNearby(target.Position, 18, healthPkt, 0);

                if (target.Hits <= 0 && !target.IsDead)
                {
                    _log.LogDebug("[death_path] breath victim=0x{V:X} dmg={Dmg}", target.Uid.Value, damage);
                    _npcAI.OnNpcKill?.Invoke(npc, target);
                }
            };
            _npcAI.OnNpcThrow = (npc, target, damage) =>
            {
                // @NPCSpecialAction (Source-X) — N1 = 2 (thrown object).
                // RETURN 1 cancels the special (effect + damage).
                if (_triggerDispatcher.FireCharTrigger(npc, CharTrigger.NPCSpecialAction,
                        new TriggerArgs { CharSrc = npc, O1 = target, N1 = 2 }) == TriggerResult.True)
                    return;

                FaceAndBroadcastToward(npc, target);

                ushort throwGfx = 0x0F51;
                if (npc.TryGetTag("THROWOBJ", out string? objStr) && !string.IsNullOrWhiteSpace(objStr))
                {
                    var cleanObj = objStr.Trim();
                    if (cleanObj.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                        cleanObj = cleanObj[2..];
                    if (ushort.TryParse(cleanObj, System.Globalization.NumberStyles.HexNumber, null, out ushort gfx) && gfx > 0)
                        throwGfx = gfx;
                }
                var fx = new PacketEffect(0, npc.Uid.Value, target.Uid.Value, throwGfx,
                    npc.X, npc.Y, (short)(npc.Z + 10),
                    target.X, target.Y, (short)(target.Z + 10),
                    10, 5, true, false);
                BroadcastNearby(npc.Position, 18, fx, 0);

                target.Hits -= (short)Math.Min(damage, target.Hits);
                _spellEngine?.TryInterruptFromDamage(target, damage);
                if (target.HasActiveSkillPending())
                {
                    int abortedSkill = target.ClearActiveSkillPending();
                    if (abortedSkill >= 0)
                        Character.ActiveSkillAborted?.Invoke(target, abortedSkill);
                }
                if (!target.IsPlayer && !target.IsDead && !target.FightTarget.IsValid)
                {
                    target.FightTarget = npc.Uid;
                    target.NextNpcActionTime = 0;
                    WakeNpc(target);
                }
                BroadcastDamageNearby(target.Position, 18, target.Uid.Value, damage, 0);
                var healthPkt = new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits);
                BroadcastNearby(target.Position, 18, healthPkt, 0);

                if (target.Hits <= 0 && !target.IsDead)
                    _npcAI.OnNpcKill?.Invoke(npc, target);
            };
            _npcAI.ResolveNpcSpellFlags = spell => _spellEngine.GetSpellDef(spell)?.Flags;
            _npcAI.OnNpcTryStartSpellCast = (npc, target, spell) =>
            {
                int castMs = _spellEngine.CastStart(npc, spell, target.Uid, target.Position);
                _log.LogDebug("[npc_cast] npc=0x{Npc:X} '{Name}' spell={Spell} target=0x{Tgt:X} castMs={Ms}",
                    npc.Uid.Value, npc.Name, spell, target.Uid.Value, castMs);
                if (castMs > 0)
                    npc.SetCastTimerEnd(Environment.TickCount64 + castMs);
                return castMs >= 0;
            };
            _npcAI.OnNpcTickSpellCast = npc => _spellEngine.TickCastTimer(npc);
            var gatheringEngine = new GatheringEngine(_world, _triggerDispatcher);
            _skillHandlers = new SkillHandlers(_world, gatheringEngine);
            Character.OnScriptSkillUse = (ch, skill) => _skillHandlers.UseSkill(ch, skill);
            _craftingEngine = new CraftingEngine(_world);
            // NOTE: LoadRecipesFromDefs is called AFTER defLoader.LoadAll() populates
            // DefinitionLoader.AllItemDefs — see post-definition-load block below.
            _weatherEngine = new WeatherEngine(_world);
            // NPC_AI_EXTRA night detection (deferred from NpcAI wiring: WeatherEngine
            // did not exist there). NpcAI.cs reads GetLightLevel to decide day/night;
            // without this NPCs always assumed daylight.
            _npcAI.GetLightLevel = _weatherEngine.GetLightLevel;
            _weatherEngine.Configure(
                _config.SeasonMode,
                (SeasonType)Math.Clamp(_config.SeasonDefault, (byte)SeasonType.Spring, (byte)SeasonType.Desolation),
                checked(_config.SeasonChangeIntervalMinutes * 60 * 1000));
            _weatherEngine.OnWeatherChanged = (regionName, type, intensity, temp) =>
            {
                var pkt = new PacketWeather((byte)type, intensity, temp);
                foreach (var c in _clients.Values)
                {
                    if (!c.IsPlaying || c.Character == null) continue;
                    var r = _world.FindRegion(c.Character.Position);
                    if (r != null && r.Name == regionName)
                    {
                        byte light = c.Character.IsDead ? (byte)0 : _world.GetLightLevel(c.Character.Position);
                        c.Character.UpdateEnvironment(light, (byte)type,
                            c.Character.IsDead ? (byte)SeasonType.Desolation : (byte)_weatherEngine.CurrentSeason);
                        c.Send(pkt);
                    }
                }
            };
            VendorEngine.World = _world;
            _world.ObjectCreated += OnWorldObjectCreated;
            _world.ObjectDeleting += OnWorldObjectDeleting;
            _world.CharacterMoved += OnCharacterMoved;
            _world.CharacterPlaced += BroadcastCharacterAppear;
            _accounts.AccountCreated += account => _systemHooks.DispatchAccount("create", account);
            _accounts.AccountLogin += account => _systemHooks.DispatchAccount("login", account);
            _accounts.AccountDeleted += account => _systemHooks.DispatchAccount("delete", account);
            _accounts.AccountPasswordChanged += account => _systemHooks.DispatchAccount("pwchange", account);
            _accounts.AccountBlocked += account => _systemHooks.DispatchAccount("block", account);
            _accounts.AccountUnblocked += account => _systemHooks.DispatchAccount("unblock", account);
            _accounts.AccountsChanged += SaveAccountsToDisk;

            // Wire config values to engines
            SkillEngine.SkillSumMaxOverride = _config.MaxBaseSkill > 0 ? _config.MaxBaseSkill : 7000;
            _world.MapData = _mapData;

            // Wire combat weapon damage lookup from ItemDef definitions
            CombatEngine.WeaponDefLookup = (baseId) =>
            {
                var link = _resources.GetResource(ResType.ItemDef, baseId);
                if (link == null) return null;
                using var sf = link.OpenAtStoredPosition();
                if (sf == null) return null;
                var sections = sf.ReadAllSections();
                int damMin = 0, damMax = 0;
                foreach (var sec in sections)
                {
                    foreach (var key in sec.Keys)
                    {
                        if (key.Key.Equals("DAM", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = key.Arg.Split(',');
                            int.TryParse(parts[0].Trim(), out damMin);
                            if (parts.Length > 1) int.TryParse(parts[1].Trim(), out damMax);
                            else damMax = damMin;
                        }
                    }
                }
                return damMax > 0 ? (damMin, damMax) : null;
            };

            CombatEngine.NpcDamageDefLookup = (defIndex) =>
            {
                var charDef = DefinitionLoader.GetCharDef(defIndex);
                if (charDef == null || (charDef.AttackMin == 0 && charDef.AttackMax == 0))
                    return null;
                return (charDef.AttackMin, Math.Max(charDef.AttackMin, charDef.AttackMax));
            };

            // NPC paid training from sphere.ini (Source-X TRAINSKILL* keys).
            SphereNet.Game.Trade.VendorTrainingEngine.TrainSkillPercent = _config.TrainSkillPercent;
            SphereNet.Game.Trade.VendorTrainingEngine.TrainSkillMax = _config.TrainSkillMax;
            SphereNet.Game.Trade.VendorTrainingEngine.TrainSkillCost = _config.TrainSkillCost;

            // Ground decay window from sphere.ini DecayTimer (minutes) —
            // Source-X m_iDecay_Item; previously the ini value was read but
            // never wired, so a hardcoded 10 minutes applied everywhere.
            GameWorld.DefaultDecayTimeMs = Math.Max(1, _config.DecayTimer) * 60_000L;

            // Item durability from config
            CombatEngine.DurabilityEnabled = _config.ItemDurabilityEnabled;
            CombatEngine.DurabilityLossChance = _config.ItemDurabilityLossChance;
            CombatEngine.DurabilityLossMin = _config.ItemDurabilityLossMin;
            CombatEngine.DurabilityLossMax = _config.ItemDurabilityLossMax;
            CombatEngine.BreakOnZeroHits = _config.ItemBreakOnZeroHits;
            CombatEngine.DefaultHits = _config.ItemDefaultHits;

            CombatEngine.OnItemDamaged = (item, loss) =>
            {
                if (_triggerDispatcher == null) return false;
                var args = new TriggerArgs { ItemSrc = item, N1 = loss };
                var result = _triggerDispatcher.FireItemTrigger(item, ItemTrigger.Damage, args);
                return result == TriggerResult.True;
            };

            CombatEngine.OnItemBroken = (item) =>
            {
                var owner = item.ContainedIn.IsValid ? _world.FindChar(item.ContainedIn) : null;
                if (owner == null)
                {
                    var equipOwner = _world.GetAllObjects()
                        .OfType<SphereNet.Game.Objects.Characters.Character>()
                        .FirstOrDefault(c => c.GetEquippedItem(item.EquipLayer) == item);
                    owner = equipOwner;
                }

                if (owner != null && _clientsByCharUid.TryGetValue(owner.Uid, out var ownerClient))
                    ownerClient.SysMessage($"Your {item.GetName()} has been destroyed!");

                // Source-X CItem::OnTakeDamage destroy branch emotes
                // ITEM_DMG_DESTROYED over the object — for worn gear that is
                // the wearer, visible to everyone nearby.
                if (owner != null)
                {
                    string emote = $"*{item.GetName()} " +
                        $"{SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.ItemDmgDestroyed)}*";
                    var emotePkt = new PacketSpeechUnicodeOut(
                        owner.Uid.Value, owner.BodyId, 2, 0x0022, 3, "TRK",
                        owner.Name ?? "", emote);
                    BroadcastNearby(owner.Position, 18, emotePkt, 0);
                }

                if (item.EquipLayer != SphereNet.Core.Enums.Layer.None)
                    owner?.Unequip(item.EquipLayer);
                var parent = item.ContainedIn.IsValid ? _world.FindObject(item.ContainedIn) as SphereNet.Game.Objects.Items.Item : null;
                parent?.RemoveItem(item);
                _world.RemoveItem(item);
                item.Delete();
            };

            CombatEngine.OnHitParry = (defender, attacker, blockedDamage) =>
            {
                // Visible block spark on a successful parry (ModernUO 0x37B9).
                GameClient.BroadcastParryEffect(defender, BroadcastNearby);
                if (_triggerDispatcher == null)
                    return 0; // full block
                // @HitParry: ARGN1 is the damage allowed through the parry — 0 (the
                // default) is a full block; a script may raise it for a partial block.
                var args = new TriggerArgs { CharSrc = attacker, O1 = attacker, N1 = 0 };
                _triggerDispatcher.FireCharTrigger(defender, CharTrigger.HitParry, args);
                return Math.Max(0, args.N1);
            };

            // Shared on-hit damage pipeline for both the player and NPC swing
            // paths. Fires @Hit / @GetHit and the weapon/armor item hooks after
            // armor/parry but before HP is applied, threading the running damage
            // through ARGN1 so a script can raise, lower or cancel it (RETURN 1).
            // The pipeline itself lives in TriggerDispatcher.RunHitDamageTriggers
            // (Source-X @GetHit armor-damage locals + item @GetHit on the rolled
            // LOCAL.ItemDamageLayer piece).
            CombatEngine.OnHitDamage = ctx =>
                _triggerDispatcher?.RunHitDamageTriggers(ctx) ?? ctx.Damage;

            // CObjBase DAMAGE is not a weapon swing: fire only the victim's
            // @GetHit stage, then let CombatEngine apply split/resist and HP.
            CombatEngine.OnDirectDamage = ctx =>
            {
                var locals = new SphereNet.Scripting.Variables.VarMap();
                locals.SetInt("DamagePercentPhysical", ctx.PhysicalPercent);
                locals.SetInt("DamagePercentFire", ctx.FirePercent);
                locals.SetInt("DamagePercentCold", ctx.ColdPercent);
                locals.SetInt("DamagePercentPoison", ctx.PoisonPercent);
                locals.SetInt("DamagePercentEnergy", ctx.EnergyPercent);
                var triggerArgs = new TriggerArgs
                {
                    CharSrc = ctx.Source,
                    N1 = ctx.Damage,
                    N2 = (int)ctx.DamageType,
                    Locals = locals
                };
                if (_triggerDispatcher?.FireCharTrigger(
                        ctx.Target, CharTrigger.GetHit, triggerArgs) == TriggerResult.True)
                {
                    ctx.Cancelled = true;
                    return 0;
                }
                ctx.PhysicalPercent = (int)locals.GetInt("DamagePercentPhysical");
                ctx.FirePercent = (int)locals.GetInt("DamagePercentFire");
                ctx.ColdPercent = (int)locals.GetInt("DamagePercentCold");
                ctx.PoisonPercent = (int)locals.GetInt("DamagePercentPoison");
                ctx.EnergyPercent = (int)locals.GetInt("DamagePercentEnergy");
                return triggerArgs.N1;
            };

            CombatEngine.OnDirectCharacterDamageApplied = (target, source, damage) =>
            {
                _spellEngine?.TryInterruptFromDamage(target, damage);
                BroadcastDamageNearby(target.Position, 18, target.Uid.Value, damage, 0);
                BroadcastNearby(target.Position, 18,
                    new PacketUpdateHealth(target.Uid.Value, target.MaxHits, target.Hits), 0);
                GameClient.EmitBloodSplat(_world, target);

                if (source != null && source != target && !target.IsPlayer &&
                    !target.IsDead && !target.FightTarget.IsValid)
                {
                    target.FightTarget = source.Uid;
                    target.NextNpcActionTime = 0;
                    WakeNpc(target);
                }
                if (target.Hits <= 0 && !target.IsDead)
                    _deathEngine?.ProcessDeath(target, source);
            };

            // AOS on-hit hooks (Source-X Fight_Hit tail, CCharFight.cpp:2270).
            // Leech feedback: Source-X plays 0x44D at the attacker.
            CombatEngine.OnLeechEffect = ch =>
                BroadcastNearby(ch.Position, 18, new PacketSound(0x044D, ch.X, ch.Y, ch.Z), 0);

            // HITFIREBALL/HARM/LIGHTNING/MAGICARROW/DISPEL: cast the spell's
            // effect directly on the victim at the attacker's Magery.
            CombatEngine.OnHitSpell = (attacker, target, spellId) =>
                _spellEngine?.ApplyOnHitSpell(attacker, target, (SpellType)spellId);

            // HITAREA* splash (Source-X OnTakeDamageInflictArea, 5-tile ML
            // radius): everyone around the struck victim takes the half-damage
            // through their own resists — skipping the two principals, the
            // attacker's pets and the dead. Per-element impact sound at the
            // epicenter (0x10E/0x11D/0xFC/0x205/0x1F1).
            CombatEngine.OnHitAreaDamage = (attacker, epicenter, dmg, dmgType) =>
            {
                if (dmg <= 0) return;
                bool hitAny = false;
                foreach (var ch in _world.GetCharsInRange(epicenter.Position, 5))
                {
                    if (ch == attacker || ch == epicenter || ch.IsDead) continue;
                    if (ch.OwnerSerial == attacker.Uid) continue;
                    if (!_world.CanSeeLOS(attacker.Position, ch.Position)) continue;

                    int dealt = CombatEngine.ApplyElementalResist(ch, dmg, dmgType);
                    if (dealt <= 0) continue;
                    ch.Hits -= (short)Math.Min(dealt, short.MaxValue);
                    ch.RecordAttack(attacker.Uid, dealt);
                    hitAny = true;

                    // Source-X sparkle (ITEMID_FX_SPARKLE_2 = 0x3779) on each
                    // splashed victim.
                    BroadcastNearby(ch.Position, 18, new PacketEffect(
                        3, ch.Uid.Value, ch.Uid.Value, 0x3779,
                        ch.X, ch.Y, ch.Z, ch.X, ch.Y, ch.Z,
                        1, 15, false, false), 0);

                    if (!ch.IsPlayer && !ch.IsDead && !ch.FightTarget.IsValid)
                    {
                        ch.FightTarget = attacker.Uid;
                        ch.NextNpcActionTime = 0;
                    }
                    if (ch.Hits <= 0 && !ch.IsDead)
                        _deathEngine?.ProcessDeath(ch, attacker);
                }

                if (hitAny)
                {
                    ushort sound = dmgType switch
                    {
                        DamageType.Fire => 0x011D,
                        DamageType.Cold => 0x00FC,
                        DamageType.Poison => 0x0205,
                        DamageType.Energy => 0x01F1,
                        _ => 0x010E,
                    };
                    BroadcastNearby(epicenter.Position, 18,
                        new PacketSound(sound, epicenter.X, epicenter.Y, epicenter.Z), 0);
                }
            };

            // Housing — load multi definitions from multi.mul
            var multiRegistry = new SphereNet.Game.Housing.MultiRegistry();
            if (_mapData != null)
            {
                int multiCount = multiRegistry.LoadFromMapData(_mapData);
                _log.LogInformation("Loaded {Count} multi definitions from multi.mul", multiCount);
            }
            // Overlay script [MULTIDEF] metadata (name / type / storage / vendors) onto
            // the binary geometry, so placed houses/ships use script-defined values.
            int multiMeta = multiRegistry.MergeScriptMetadata(_resources);
            _log.LogInformation("Merged {Count} multi script definitions ([MULTIDEF])", multiMeta);
            _housingEngine = new HousingEngine(_world, multiRegistry)
            {
                MaxHousesPerPlayer = _config.MaxHousesPlayer,
                MaxHousesPerAccount = _config.MaxHousesAccount
            };
            _housingEngine.OnAddMulti = (owner, multi, privilege) =>
                _triggerDispatcher.FireCharTrigger(owner, CharTrigger.AddMulti,
                    new TriggerArgs
                    {
                        CharSrc = owner, O1 = multi, N1 = 1, N2 = (int)privilege, N3 = 1,
                        ScriptConsole = FindGameClient(owner)
                    });
            _housingEngine.DeserializeFromWorld();
            if (_housingEngine.HouseCount > 0)
                _log.LogInformation("Restored {Count} houses from world save", _housingEngine.HouseCount);
            _customHousing = new CustomHousingEngine(_world, _housingEngine);
            _chatEngine = new SphereNet.Game.Chat.ChatEngine("General");
            // Committed custom-house designs become virtual walk geometry
            // (the tiles are not real items — clients render them from 0xD8).
            SphereNet.Game.Movement.WalkCheck.ResolveCustomDesign =
                multi => _customHousing.GetCommittedTiles(multi);

            // Ships
            _shipEngine = new SphereNet.Game.Ships.ShipEngine(_world, multiRegistry, _mapData)
            {
                MaxShipsPerPlayer = _config.MaxShipsPlayer,
                MaxShipsPerAccount = _config.MaxShipsAccount,
            };
            _shipEngine.OnAddMulti = (owner, multi, privilege) =>
                _triggerDispatcher.FireCharTrigger(owner, CharTrigger.AddMulti,
                    new TriggerArgs
                    {
                        CharSrc = owner, O1 = multi, N1 = 1, N2 = (int)privilege, N3 = 1,
                        ScriptConsole = FindGameClient(owner)
                    });
            _shipEngine.OnTillerSpeak = (ship, text) =>
            {
                // Source-X CItemShip::Speak uses CObjBase::Speak with COLOR_TEXT_DEF.
                // Broadcast as overhead unicode text from the tillerman item so
                // every client near the multi sees the line, mirroring upstream.
                var origin = ship.MultiItem.Position;
                var pkt = new PacketSpeechUnicodeOut(
                    ship.MultiItem.Uid.Value,
                    ship.MultiItem.DispIdFull,
                    0x06, // ASCII speech
                    0x0481, // grey hue
                    3, // small font
                    "ENU",
                    ship.MultiItem.Name ?? "Tillerman",
                    text);
                BroadcastNearby(origin, 18, pkt, 0);
            };
            _shipEngine.OnShipMoved = ship =>
            {
                var mi = ship.MultiItem;
                // B11 (Source-X PacketMoveShip): carry every deck object
                // (components, passengers, cargo) so smooth-sailing clients move
                // them with the hull instead of waiting for per-object resends.
                var shipObjs = _shipEngine.ListShipObjects(ship);
                var entries = new (uint, short, short, sbyte)[Math.Max(0, shipObjs.Count - 1)];
                for (int i = 1; i < shipObjs.Count; i++)
                {
                    var o = shipObjs[i];
                    entries[i - 1] = (o.Uid.Value, o.X, o.Y, o.Z);
                }
                var pkt = new PacketBoatSmoothMove(
                    mi.Uid.Value,
                    (byte)ship.SpeedMode,
                    (byte)((byte)ship.DirMove & 0x07),
                    (byte)((byte)ship.DirFace & 0x07),
                    mi.X, mi.Y, (ushort)(mi.Z < 0 ? 0 : mi.Z),
                    entries);
                BroadcastNearby(mi.Position, 18, pkt, 0);
                // @ShipMove (Source-X) — fired on the ship multi as it advances.
                _triggerDispatcher?.FireItemTrigger(mi, ItemTrigger.ShipMove,
                    new TriggerArgs { ItemSrc = mi });
            };
            _shipEngine.OnShipStopped = ship =>
                _triggerDispatcher?.FireItemTrigger(ship.MultiItem, ItemTrigger.ShipStop,
                    new TriggerArgs { ItemSrc = ship.MultiItem });
            _shipEngine.OnShipSound = (ship, soundId) =>
            {
                var pos = ship.MultiItem.Position;
                BroadcastNearby(pos, 18, new PacketSound(soundId, pos.X, pos.Y, pos.Z), 0);
            };
            _shipEngine.OnShipTurned = ship =>
                _triggerDispatcher?.FireItemTrigger(ship.MultiItem, ItemTrigger.ShipTurn,
                    new TriggerArgs { ItemSrc = ship.MultiItem });
            // @RegionLeave / @RegionEnter — Source-X scopes these item triggers
            // to movable multis: they fire on the ship multi when a move step
            // crosses a region boundary, ARGO = the region, SRC = the pilot,
            // RETURN 1 blocks the step. Installed only when hooked so the
            // per-step cost on unhooked shards is a null check in MoveDelta.
            if (_triggerDispatcher != null &&
                (_triggerDispatcher.IsItemTriggerUsed(ItemTrigger.RegionLeave) ||
                 _triggerDispatcher.IsItemTriggerUsed(ItemTrigger.RegionEnter)))
            {
                _shipEngine.OnShipRegionChange = (ship, oldRegion, newRegion) =>
                {
                    var pilot = ship.Pilot.IsValid ? _world.FindChar(ship.Pilot) : null;
                    if (oldRegion != null &&
                        _triggerDispatcher.FireItemTrigger(ship.MultiItem, ItemTrigger.RegionLeave,
                            new TriggerArgs { ItemSrc = ship.MultiItem, CharSrc = pilot, O1 = oldRegion })
                        == TriggerResult.True)
                        return false;
                    if (newRegion != null &&
                        _triggerDispatcher.FireItemTrigger(ship.MultiItem, ItemTrigger.RegionEnter,
                            new TriggerArgs { ItemSrc = ship.MultiItem, CharSrc = pilot, O1 = newRegion })
                        == TriggerResult.True)
                        return false;
                    return true;
                };
            }

            // @Redeed (Source-X) — a house collapsed/redeeded into a deed item.
            SphereNet.Game.Housing.House.OnRedeed = deed =>
                _triggerDispatcher?.FireItemTrigger(deed, ItemTrigger.Redeed,
                    new TriggerArgs { ItemSrc = deed });
            // @HouseCheck — a script may veto house placement (RETURN 1) after the
            // engine's NoBuild / footprint / terrain checks pass; ARGN1/2/3 = x/y/z.
            SphereNet.Game.Housing.HousingEngine.OnHouseCheck = (placer, pos) =>
                _triggerDispatcher?.FireCharTriggerByName(placer, "HouseCheck",
                    new TriggerArgs { CharSrc = placer, N1 = pos.X, N2 = pos.Y, N3 = pos.Z }) == TriggerResult.True;
            _shipEngine.DeserializeFromWorld();
            if (_shipEngine.ShipCount > 0)
                _log.LogInformation("Restored {Count} ships from world save", _shipEngine.ShipCount);

            SphereNet.Game.Objects.Characters.Character.ResolveHouseUidsByOwner = ownerUid =>
                _housingEngine?.GetHousesByOwner(ownerUid)
                    .Select(house => house.MultiItem.Uid)
                    .ToArray()
                ?? [];
            SphereNet.Game.Objects.Characters.Character.ResolveShipUidsByOwner = ownerUid =>
                _shipEngine?.AllShips
                    .Where(ship => ship.Owner == ownerUid)
                    .Select(ship => ship.MultiItem.Uid)
                    .ToArray()
                ?? [];

            // Wire Item static delegates for ship resolution
            SphereNet.Game.Objects.Items.Item.ResolveShip = uid => _shipEngine.GetShip(uid);
            SphereNet.Game.Objects.Items.Item.ResolveHouse = uid => _housingEngine?.GetHouse(uid);
            SphereNet.Game.Objects.Items.Item.RedeedHouse = uid => _housingEngine?.RedeedFromScript(uid);
            SphereNet.Game.Objects.Items.Item.RedeedShip = uid => _shipEngine?.RedeedFromScript(uid);
            // Script NEWNPC: route through the invoker's client spawn pipeline
            // (any online client works — the method only touches world state);
            // on an empty server the spawn is skipped.
            SphereNet.Game.Objects.Characters.Character.SpawnNpcFromScript = (invoker, defName) =>
            {
                if (string.IsNullOrWhiteSpace(defName)) return null;
                if (_clientsByCharUid.TryGetValue(invoker.Uid, out var cli))
                    return cli.SpawnNpcForScript(defName, invoker.Position);
                foreach (var anyClient in _clientsByCharUid.Values)
                    return anyClient.SpawnNpcForScript(defName, invoker.Position);
                return null;
            };
            if (_housingEngine != null)
                _housingEngine.IsShipAt = pt => _shipEngine?.FindShipAt(pt) != null;
            // MULTICREATE verb -> HousingEngine runtime registration
            SphereNet.Game.Objects.Items.Item.OnHouseRegister =
                item => _housingEngine?.RegisterExistingMulti(item);
            // Runtime TYPE=t_spawn_char/item → initialize spawn component
            SphereNet.Game.Objects.Items.Item.OnSpawnTypeChanged = item =>
                item.InitializeSpawnComponent(_world, _resources);

            // Char UID -> GameClient index, used by BroadcastNearby /
            // SendPacketToChar to skip the full _clients.Values scan.
            SphereNet.Game.Clients.GameClient.OnCharacterOnline = (ch, client) =>
            {
                _clientsByCharUid[ch.Uid] = client;
                var region = _world.FindRegion(ch.Position);
                var weather = region != null
                    ? _weatherEngine.GetWeatherForRegion(region.Name).Type
                    : WeatherType.None;
                ch.UpdateEnvironment(ch.IsDead ? 0 : _world.GetLightLevel(ch.Position),
                    (byte)weather,
                    ch.IsDead ? (byte)SeasonType.Desolation : (byte)_weatherEngine.CurrentSeason);
            };
            SphereNet.Game.Clients.GameClient.OnCharacterOffline =
                ch => { _clientsByCharUid.Remove(ch.Uid); _macroEngine?.OnCharDisconnect(ch.Uid.Value); };
            SphereNet.Game.Objects.Characters.Character.OnDamageActionInterrupt = ch =>
            {
                _spellEngine?.BreakParalyze(ch);
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var client))
                    client.CancelPendingCraftOnInterrupt();
            };
            SphereNet.Game.Objects.Characters.Character.OnHiddenStateCleared = ch =>
                _spellEngine?.BreakInvisibility(ch);
            _world.ClientLingerExpired += ch =>
                BroadcastNearby(ch.Position, 18, new PacketDeleteObject(ch.Uid.Value), ch.Uid.Value);
            SphereNet.Game.Clients.GameClient.OnWakeNpc = WakeNpc;
            SphereNet.Game.Clients.GameClient.ServerListProvider = BuildServerList;
            SphereNet.Game.Clients.GameClient.BotSpawnLocationProvider = acctName =>
            {
                if (_botEngine == null || !SphereNet.Game.Diagnostics.BotClient.IsBotAccountName(acctName))
                    return null;
                var (x, y, z) = _botEngine.GetRandomSpawnLocation(new Random(acctName.GetHashCode()));
                return new Point3D(x, y, z, 0);
            };

            // Corpse decay -> drop contents to the ground. Invoked by the
            // per-item decay timer in Item.OnTick; replaces the old per-tick
            // full-world scan in DeathEngine.ProcessDecay.
            SphereNet.Game.Objects.Items.Item.OnCorpseDecay = corpse =>
            {
                bool isPlayerCorpse = false;
                GameClient? ownerClient = null;
                if (corpse.TryGetTag("OWNER_UID", out string? ownerStr) &&
                    uint.TryParse(ownerStr, out uint ownerUid))
                {
                    var owner = _world.FindChar(new Serial(ownerUid));
                    if (owner != null && owner.IsPlayer)
                    {
                        isPlayerCorpse = true;
                        _clientsByCharUid.TryGetValue(owner.Uid, out ownerClient);
                    }
                }
                bool staged = corpse.TryGetTag("NOREJOIN", out _);

                if (isPlayerCorpse && ownerClient?.NetState.SupportsMapWaypoints == true)
                    ownerClient.Send(new PacketWaypointRemove(corpse.Uid.Value));

                // Source-X CItem two-stage player-corpse decay: on the FIRST decay a
                // player corpse with loot turns into a bones pile its owner can no
                // longer rejoin, keeping its contents for a second decay window —
                // it does NOT scatter the loot yet. (NPC corpses and the bones'
                // second decay fall through to the scatter/delete below.)
                if (isPlayerCorpse && !staged && corpse.Contents.Count > 0)
                {
                    corpse.BaseId = 0x0ECA;       // bone pile graphic
                    corpse.Name = "bones";
                    corpse.SetTag("NOREJOIN", "1");
                    corpse.RemoveTag("OWNER_UID"); // cut the owner link (no rejoin)
                    corpse.RemoveTag("OWNER_UUID");
                    corpse.DecayTime = Environment.TickCount64 + GameWorld.DefaultDecayTimeMs;
                    return false; // keep — staged to bones
                }

                // Player loot scatters to the ground; NPC corpse contents are deleted.
                bool dropToGround = isPlayerCorpse || staged;
                foreach (var child in corpse.Contents.ToArray())
                {
                    corpse.RemoveItem(child);
                    if (dropToGround)
                    {
                        _world.PlaceItem(child, corpse.Position);
                        if (child.DecayTime <= 0)
                            child.DecayTime = Environment.TickCount64 + GameWorld.DefaultDecayTimeMs;
                    }
                    else
                    {
                        _world.RemoveItem(child);
                    }
                }
                return true; // consumed — delete the corpse/bones
            };
            SphereNet.Game.Objects.Items.Item.OnTimerExpired = item =>
            {
                return _triggerDispatcher?.FireItemTrigger(item,
                    ItemTrigger.Timer,
                    new TriggerArgs { ItemSrc = item });
            };
            _world.TimerFExpired = (obj, entry) =>
            {
                if (_triggerRunner == null)
                    return;
                var trigArgs = new SphereNet.Scripting.Execution.TriggerArgs(obj)
                {
                    ArgString = entry.Args
                };
                // Source-X TIMERF/TIMERFMS runs a delayed VERB or function. Try the
                // payload as a [FUNCTION] first; if no such function exists, fall back
                // to running it as a command on the object (e.g. "SAY hi" / "REMOVE"),
                // so bare-verb timers work and no longer silently no-op.
                if (_triggerRunner.TryRunFunction(entry.FunctionName, obj, null, trigArgs, out _))
                    return;
                try
                {
                    obj.TryExecuteCommand(entry.FunctionName, entry.Args, new RefExecConsole());
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Delayed TIMERF verb '{Verb}' failed", entry.FunctionName);
                }
            };
            SphereNet.Game.Objects.Items.Item.ResolveShipEngine = () => _shipEngine;
            SphereNet.Game.Objects.Items.Item.ResolveWorld = () => _world;
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => _world;
            // Debug, not Warning: script @Create renames (e.g. "Smoldering
            // Fire Gargoyle") are routine pack behavior — at Warning level a
            // RESPAWN FULL floods the log with thousands of these.
            SphereNet.Game.Objects.ObjBase.OnNameChangeWarning = msg =>
                _log.LogDebug("[NAME_CHANGE] {Details}", msg);

            // Route Character-emitted diagnostic lines (vendor restock,
            // trigger verb dispatch, etc.) into the main logger so they
            // appear next to the trig_runner / npc_spawn traces we already
            // rely on while debugging service NPC behaviour.
            SphereNet.Game.Objects.Characters.Character.Diagnostic = msg =>
                _log.LogDebug("{Details}", msg);
            SphereNet.Game.Definitions.TemplateEngine.Diagnostic = msg =>
                _log.LogDebug("{Details}", msg);

            // Guild member properties
            SphereNet.Game.Objects.Characters.Character.ResolveGuildManager = _ => _guildManager;

            // Party properties & commands
            SphereNet.Game.Objects.Characters.Character.ResolvePartyFinder = uid => _partyManager.FindParty(uid);
            SphereNet.Game.Objects.Characters.Character.ResolvePartyManager = () => _partyManager;

            // Character lookup by UID — used for ACCOUNT.CHAR.N.NAME chain and
            // other admin-dialog ref resolution paths.
            SphereNet.Game.Objects.Characters.Character.ResolveCharByUid = uid =>
                _world?.FindChar(uid);

            // Packet delivery back into the character's owning client, for
            // script verbs like ADDBUFF/REMOVEBUFF/SYSMESSAGELOC/ARROWQUEST.
            // No-op when the character has no connected client.
            SphereNet.Game.Objects.Characters.Character.SendPacketToOwner = (target, packet) =>
            {
                if (TryGetClientFor(target, out var c))
                {
                    if (packet is SphereNet.Network.Packets.Outgoing.PacketBuffIcon &&
                        (!GameClient.ServerOptionFlags.HasFlag(OptionFlags.Buffs) ||
                         !c.NetState.SupportsBuffIcon))
                        return;
                    c.Send(packet);
                }
            };

            SphereNet.Game.Objects.Characters.Character.OnClientBuffChanged =
                (target, icon, add, durationSeconds, buffArgs) =>
                {
                    if (!GameClient.ServerOptionFlags.HasFlag(OptionFlags.Buffs) ||
                        !TryGetClientFor(target, out var c) || !c.NetState.SupportsBuffIcon)
                        return;

                    if (!ClientBuffCatalog.TryGet(icon, out var definition))
                        return;

                    c.Send(new PacketBuffIcon(target.Uid.Value, icon, add, durationSeconds,
                        definition.TitleCliloc, definition.DescriptionCliloc,
                        buffArgs ?? []));
                };

            SphereNet.Game.Objects.Characters.Character.OnOwnViewRefreshNeeded = target =>
            {
                if (TryGetClientFor(target, out var c))
                    c.ViewNeedsRefresh = true;
            };

            SphereNet.Game.Objects.Characters.Character.SendOwnerMessage = (target, msg) =>
            {
                if (_clientsByCharUid.TryGetValue(target.Uid, out var gc))
                    gc.SysMessage(msg);
            };

            // Health-bar colour (0x17): re-tint the character's bar for every SA+/KR
            // observer when poison/freeze state flips. Older clients don't parse
            // 0x17, so gate on the SA-era client version (Source-X CanSendTo).
            SphereNet.Game.Objects.Characters.Character.OnHealthBarStatusChanged = ch =>
            {
                if (ch == null || _world == null) return;
                bool poisoned = ch.IsStatFlag(StatFlag.Poisoned);
                bool yellow = ch.IsStatFlag(StatFlag.Freeze);
                ForEachClientInRange(ch.Position, 18, 0, (_, observerClient) =>
                {
                    var ns = observerClient.NetState;
                    if (ns.ClientVersionNumber >= 60_000_000 ||
                        ns.IsKingdomRebornClient || ns.IsEnhancedClient)
                        observerClient.Send(new PacketHealthBarStatus(ch.Uid.Value, poisoned, yellow));
                });
            };

            SphereNet.Game.Objects.Characters.Character.NotoSaveUpdate = ch =>
            {
                if (ch == null || _world == null) return;
                ForEachClientInRange(ch.Position, 18, 0, (observerCh, observerClient) =>
                {
                    byte noto = GameClient.ComputeNotoriety(_world, observerCh, ch);
                    byte dir = (byte)((byte)ch.Direction & 0x07);
                    byte flags = 0;
                    if (ch.IsInWarMode) flags |= 0x40;
                    if (ch.IsDead) flags |= 0x02;
                    if (ch.BodyId == 0x0191 || ch.BodyId == 0x0193) flags |= 0x02;
                    if (ch.IsStatFlag(StatFlag.Freeze)) flags |= 0x01;
                    observerClient.Send(new PacketMobileMoving(
                        ch.Uid.Value, ch.BodyId,
                        ch.X, ch.Y, ch.Z, dir,
                        ch.Hue, flags, noto));
                });
            };

            SphereNet.Game.Objects.Characters.Character.ActiveSkillAborted = (ch, skillId) =>
            {
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.SkillAbort,
                    new TriggerArgs { CharSrc = ch, N1 = skillId });
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var gc))
                    gc.SysMessage("You stop what you were doing.");
            };

            SphereNet.Game.Objects.Characters.Character.OnStepStealth = ch =>
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.StepStealth,
                    new TriggerArgs { CharSrc = ch });

            // @FameChange / @KarmaChange — fired before a kill applies the delta.
            // N1 = proposed delta; a script may rewrite ARGN1 or RETURN 1 to cancel.
            SphereNet.Game.Objects.Characters.Character.OnFameChanging = (ch, delta) =>
            {
                var args = new TriggerArgs { CharSrc = ch, N1 = delta };
                if (_triggerDispatcher?.FireCharTrigger(ch, CharTrigger.FameChange, args) == TriggerResult.True)
                    return null;
                return args.N1;
            };
            SphereNet.Game.Objects.Characters.Character.OnKarmaChanging = (ch, delta) =>
            {
                var args = new TriggerArgs { CharSrc = ch, N1 = delta };
                if (_triggerDispatcher?.FireCharTrigger(ch, CharTrigger.KarmaChange, args) == TriggerResult.True)
                    return null;
                return args.N1;
            };

            // @ExpChange / @ExpLevelChange — N1 = proposed delta (script may
            // rewrite ARGN1 or RETURN 1 to cancel) / N1 = the new level.
            SphereNet.Game.Objects.Characters.Character.OnExpChanging = (ch, delta) =>
            {
                var args = new TriggerArgs { CharSrc = ch, N1 = delta };
                if (_triggerDispatcher?.FireCharTrigger(ch, CharTrigger.ExpChange, args) == TriggerResult.True)
                    return null;
                return args.N1;
            };
            SphereNet.Game.Objects.Characters.Character.OnExpLevelChanged = (ch, level) =>
                _triggerDispatcher?.FireCharTrigger(ch, CharTrigger.ExpLevelChange,
                    new TriggerArgs { CharSrc = ch, N1 = level });

            // @NPCLostTeleport — a severely lost NPC is about to teleport home;
            // RETURN 1 cancels (the NPC walks back instead).
            SphereNet.Game.Objects.Characters.Character.OnNpcLostTeleport = npc =>
                _triggerDispatcher?.FireCharTrigger(npc, CharTrigger.NPCLostTeleport,
                    new TriggerArgs { CharSrc = npc }) == TriggerResult.True;

            // @Start / @Stop — the spawner START/STOP verbs toggled spawning.
            Item.OnSpawnStartStop = (spawnItem, started) =>
                _triggerDispatcher?.FireItemTrigger(spawnItem,
                    started ? ItemTrigger.Start : ItemTrigger.Stop,
                    new TriggerArgs { ItemSrc = spawnItem });

            // @HitIgnore — an attacker flagged ATTACKER.n.IGNORE landed a hit.
            // O1 = the attacker; RETURN 1 clears the ignore flag.
            SphereNet.Game.Objects.Characters.Character.OnHitIgnored = (victim, attackerUid) =>
            {
                var args = new TriggerArgs { CharSrc = victim, O1 = _world?.FindChar(attackerUid) };
                return _triggerDispatcher?.FireCharTrigger(victim, CharTrigger.HitIgnore, args)
                    == TriggerResult.True;
            };

            // @MurderMark — fired on a player-vs-player kill before the murder
            // count is recorded. N1 = proposed count (script may rewrite ARGN1),
            // O1 = victim; RETURN 1 blocks the mark and the criminal flag.
            SphereNet.Game.Objects.Characters.Character.OnMurderMark = (killer, victim, proposed) =>
            {
                // N1 = proposed murder count, N2 = make-criminal toggle (default 1).
                // RETURN 1 blocks the mark + criminal flag; ARGN2=0 records the
                // murder without arming the temporary criminal flag (Source-X).
                var args = new TriggerArgs { CharSrc = killer, N1 = proposed, N2 = 1, O1 = victim };
                if (_triggerDispatcher?.FireCharTrigger(killer, CharTrigger.MurderMark, args) == TriggerResult.True)
                    return new SphereNet.Game.Objects.Characters.Character.MurderMarkDecision(null, false);
                return new SphereNet.Game.Objects.Characters.Character.MurderMarkDecision(args.N1, args.N2 != 0);
            };

            // Combat (attacker) list lifecycle — @CombatAdd / @CombatDelete fire on
            // the character whose list changed, O1 = the other combatant; @CombatEnd
            // fires when the list empties. The list is mutated inside Character with
            // no dispatcher, so it resolves the attacker UID via the world here.
            SphereNet.Game.Objects.Characters.Character.OnCombatAdd = (self, attackerUid) =>
                _triggerDispatcher?.FireCharTrigger(self, CharTrigger.CombatAdd,
                    new TriggerArgs { CharSrc = self, O1 = _world?.FindChar(attackerUid) });
            SphereNet.Game.Objects.Characters.Character.OnCombatDelete = (self, attackerUid) =>
                _triggerDispatcher?.FireCharTrigger(self, CharTrigger.CombatDelete,
                    new TriggerArgs { CharSrc = self, O1 = _world?.FindChar(attackerUid) });
            SphereNet.Game.Objects.Characters.Character.OnCombatEnd = self =>
                _triggerDispatcher?.FireCharTrigger(self, CharTrigger.CombatEnd,
                    new TriggerArgs { CharSrc = self });

            // @MurderDecay — one murder count aged off. N1 = new kill count;
            // ARGN2 (read back) overrides the seconds until the next decay.
            SphereNet.Game.Objects.Characters.Character.OnMurderDecay = (self, newKills) =>
            {
                var args = new TriggerArgs { CharSrc = self, N1 = newKills, N2 = 0 };
                _triggerDispatcher?.FireCharTrigger(self, CharTrigger.MurderDecay, args);
                return args.N2;
            };

            // Account resolution from character UID
            SphereNet.Game.Objects.Characters.Character.ResolveAccountForChar = uid =>
            {
                var ch = _world.FindChar(uid);
                if (ch == null) return null;
                if (ch.TryGetTag("ACCOUNT", out string? acctName) && !string.IsNullOrEmpty(acctName))
                    return _accounts.FindAccount(acctName);
                foreach (var acct in _accounts.GetAllAccounts())
                {
                    for (int i = 0; i < 7; i++)
                    {
                        if (acct.GetCharSlot(i) == uid)
                            return acct;
                    }
                }
                return null;
            };
            SphereNet.Game.Objects.Items.Item.ResolveGuild = uid => _guildManager.GetGuild(uid);
            SphereNet.Game.Objects.Items.Item.ResolveGuildManager = () => _guildManager;
            SphereNet.Game.Objects.Items.Item.ResolveGuildCharacter = uid => _world.FindChar(uid);
            SphereNet.Game.Objects.Items.Item.ExecuteGuildMemberCommand = (_, memberUid, command) =>
            {
                var ch = _world.FindChar(memberUid);
                return ch != null && _commands.TryExecute(ch, command) == CommandResult.Executed;
            };
            SphereNet.Game.Objects.Items.Item.ExecuteGuildRelationCommand = (_, stoneUid, command) =>
            {
                var guild = _guildManager.GetGuild(stoneUid);
                var masterUid = guild?.GetMaster()?.CharUid ?? Serial.Invalid;
                var ch = masterUid.IsValid ? _world.FindChar(masterUid) : null;
                return ch != null && _commands.TryExecute(ch, command) == CommandResult.Executed;
            };


            // --- Admin dialog verb hooks (DISCONNECT/KICK/RESENDTOOLTIP/...).
            // Each delegate routes a CChar verb back through the right
            // engine. They are nullable for tests that spin up Character
            // without a network/admin context.
            SphereNet.Game.Objects.Characters.Character.DisconnectClient = (target, ban) =>
            {
                if (TryGetClientFor(target, out var c))
                {
                    if (ban)
                    {
                        // Ban semantics: pull the account via the same
                        // resolver Sphere uses for ACCOUNT.* lookups.
                        var acct = SphereNet.Game.Objects.Characters.Character
                            .ResolveAccountForChar?.Invoke(target.Uid);
                        if (acct != null)
                            acct.IsBanned = true;
                    }
                    c.NetState.MarkClosing();
                }
            };

            SphereNet.Game.Objects.Characters.Character.ResendTooltipForAll = target =>
            {
                target.MarkDirty(SphereNet.Core.Enums.DirtyFlag.StatFlags);
                ForEachClientInRange(target.Position, 18, 0,
                    (_, observerClient) => observerClient.SendAosTooltip(
                        target, requested: false, invalidate: true));
            };

            SphereNet.Game.Objects.Characters.Character.OnScriptSpellEffect = (target, raw, console) =>
            {
                if (_spellEngine == null || _resources == null) return;
                string[] parts = raw.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) return;
                int spellId = 0;
                if (!int.TryParse(parts[0], out spellId))
                {
                    var rid = _resources.ResolveDefName(parts[0]);
                    if (rid.IsValid && rid.Type == ResType.SpellDef)
                        spellId = rid.Index;
                    else
                    {
                        string named = parts[0].StartsWith("s_", StringComparison.OrdinalIgnoreCase)
                            ? parts[0][2..]
                            : parts[0];
                        if (Enum.TryParse<SpellType>(named, true, out var parsed)) spellId = (int)parsed;
                    }
                }
                int skill = target.GetSkill(SkillType.Magery);
                if (parts.Length > 1)
                {
                    if (int.TryParse(parts[1], out int parsedSkill))
                        skill = parsedSkill;
                    else if (decimal.TryParse(parts[1], System.Globalization.NumberStyles.Number,
                                 System.Globalization.CultureInfo.InvariantCulture, out decimal displayedSkill))
                        skill = (int)Math.Round(displayedSkill * 10m);
                }

                Character caster = target;
                if (parts.Length > 2 && parts[2] != "-1" &&
                    TryParseSerial(parts[2], out Serial casterUid) &&
                    _world?.FindChar(casterUid) is Character explicitCaster)
                {
                    caster = explicitCaster;
                }
                else if (parts.Length <= 2 && console is GameClient gc && gc.Character != null)
                {
                    caster = gc.Character;
                }
                _spellEngine.ApplyScriptSpellEffect(caster, target, (SpellType)spellId, skill);
            };

            SphereNet.Game.Objects.Characters.Character.OpenInfoDialog = target =>
            {
                // Show inspect dialog on the GM watching the target. The
                // target here is who we want to inspect, not the requester:
                // walk every active client and dispatch the inspect dialog
                // for the target. Caller side (admin dialog) typically
                // chains <Src.info> from the GM's own session, so the GM
                // is the active speech-source — matching ShowInspectDialog
                // expectations.
                if (TryGetClientFor(target, out var c))
                    c.ShowInspectDialog(target.Uid.Value);
            };

            SphereNet.Game.Objects.Characters.Character.OnAppearanceChanged = ch =>
            {
                const int range = 18;
                foreach (var c in _clients.Values)
                {
                    if (!c.IsPlaying || c.Character == null) continue;
                    if (c.Character == ch)
                    {
                        c.SendSelfRedraw();
                        continue;
                    }
                    if (c.Character.MapIndex == ch.MapIndex &&
                        c.Character.Position.GetDistanceTo(ch.Position) <= range)
                    {
                        c.NotifyCharacterAppear(ch);
                    }
                }
            };

            SphereNet.Game.Objects.Characters.Character.BeginTeleTarget = gm =>
            {
                if (TryGetClientFor(gm, out var c))
                    c.BeginTeleportTarget();
            };

            SphereNet.Game.Objects.Characters.Character.SummonCageAround = gm =>
            {
                if (TryGetClientFor(gm, out var c))
                    c.BeginSummonCageTarget();
            };

            SphereNet.Game.Objects.Characters.Character.ResolveClientInfo = ch =>
            {
                if (!TryGetClientFor(ch, out var c))
                    return (0, SphereNet.Game.Objects.Characters.Character.ClientType.ClassicWindows);

                var ct = c.NetState.ParsedClientType switch
                {
                    1 => SphereNet.Game.Objects.Characters.Character.ClientType.Classic3D,
                    2 => SphereNet.Game.Objects.Characters.Character.ClientType.KingdomReborn,
                    3 => SphereNet.Game.Objects.Characters.Character.ClientType.Enhanced,
                    _ => SphereNet.Game.Objects.Characters.Character.ClientType.ClassicWindows,
                };
                return ((int)c.NetState.ClientVersionNumber, ct);
            };

            SphereNet.Game.Objects.Characters.Character.FollowUid = (gm, uid) =>
            {
                // No persistent follow engine yet — Source-X CChar follow
                // skill ticks aren't implemented. Best-effort fallback:
                // teleport the GM next to the target so the dialog row
                // ("Follow this player") still has a useful side effect.
                var targetSerial = new SphereNet.Core.Types.Serial(uid);
                var target = _world?.FindChar(targetSerial);
                if (target == null) return;
                gm.MoveTo(target.Position);
            };

            // --- Observer-visible verbs (ANIM/SOUND/EFFECT/BOW/SALUTE/BARK)
            // and per-owner UI verbs (DCLICK/PACK/BANK) ---
            SphereNet.Game.Objects.ObjBase.BroadcastNearby = (origin, range, pkt, exclude) =>
            {
                BroadcastNearby(origin, range, pkt, exclude);
            };
            SphereNet.Game.Objects.Characters.Character.BroadcastNearby = (origin, range, pkt, exclude) =>
            {
                BroadcastNearby(origin, range, pkt, exclude);
            };
            SphereNet.Game.Objects.Characters.Character.BroadcastDamageNearby = (origin, range, serial, damage, exclude) =>
            {
                BroadcastDamageNearby(origin, range, serial, damage, exclude);
            };
            SphereNet.Game.Objects.Characters.Character.OnFacingChanged = ch =>
            {
                BroadcastFacingUpdate(ch);
            };
            SphereNet.Game.Objects.Characters.Character.OpenPaperdollForOwner = ch =>
            {
                if (TryGetClientFor(ch, out var c))
                    c.SendPaperdoll(ch);
            };
            // HEAR verb routing (Source-X CHV_HEAR): players get a private
            // sysmessage, NPCs process the line as heard speech.
            SphereNet.Game.Objects.Characters.Character.OnHearRouted = (ch, text, srcChar) =>
            {
                if (string.IsNullOrWhiteSpace(text)) return;
                if (TryGetClientFor(ch, out var hearClient))
                {
                    hearClient.SysMessage(text);
                    return;
                }
                if (!ch.IsPlayer)
                    _speech?.DeliverNpcHear(srcChar ?? ch, ch, text);
            };
            // GOCLI / GOSOCK resolvers — client enumeration lives here.
            SphereNet.Game.Objects.Characters.Character.FindCharByClientIndex = index =>
            {
                int i = 0;
                foreach (var kv in _clientsByCharUid.OrderBy(kv => kv.Key.Value))
                {
                    var c = kv.Value.Character;
                    if (c == null || c.IsDeleted) continue;
                    if (i++ == index) return c;
                }
                return null;
            };
            SphereNet.Game.Objects.Characters.Character.FindCharBySocketId = socketId =>
            {
                foreach (var kv in _clientsByCharUid)
                {
                    var gc = kv.Value;
                    if (gc.Character != null && gc.NetState.Id == socketId)
                        return gc.Character;
                }
                return null;
            };
            // PROPLIST/TAGLIST "log" argument sink (Source-X server console).
            SphereNet.Game.Objects.ObjBase.DiagnosticLog = line => _log.LogInformation("{Line}", line);
            SphereNet.Game.Objects.Characters.Character.OpenBackpackForOwner = ch =>
            {
                if (TryGetClientFor(ch, out var c))
                {
                    var pack = ch.Backpack;
                    if (pack != null)
                        c.NetState.Send(new SphereNet.Network.Packets.Outgoing.PacketOpenContainer(
                            pack.Uid.Value, 0x003C, c.NetState.IsClientPost7090));
                }
            };
            SphereNet.Game.Objects.Characters.Character.OpenBankboxForOwner = ch =>
            {
                if (TryGetClientFor(ch, out var c))
                    c.OpenBankBox();
            };

            // Mounts
            _mountEngine = new SphereNet.Game.Mounts.MountEngine(_world);
            // Script DISMOUNT verb: prefer the client path (fires @Dismount and
            // refreshes the rider's view); fall back to the engine for NPCs and
            // offline riders so the mount NPC is still restored.
            SphereNet.Game.Objects.Characters.Character.OnScriptDismount = ch =>
            {
                if (TryGetClientFor(ch, out var c))
                    c.UnmountSelf();
                else
                    _mountEngine.Dismount(ch);
            };
            // Script MOUNT verb on an NPC: seat its owner (Source-X Horse_Mount).
            // The staff-horse script runs REF1.MOUNT after making the mount a
            // pet of SRC; resolve the owner and route through the engine.
            SphereNet.Game.Objects.Characters.Character.OnScriptMount = npc =>
            {
                var owner = npc.OwnerSerial.IsValid
                    ? _world.FindChar(npc.OwnerSerial)
                    : null;
                return owner != null && _mountEngine.TryMount(owner, npc);
            };

            // Script EQUIP verb (Source-X CHV_EQUIP): resolve the item's
            // definition layer, equip it, fire @Equip, and show the worn item
            // to nearby clients. Layer/trigger plumbing lives here so the
            // object model stays free of the dispatcher dependency.
            SphereNet.Game.Objects.Characters.Character.ScriptEquipItem = (wearer, item) =>
            {
                var layer = item.EquipLayer;
                if (layer == SphereNet.Core.Enums.Layer.None || layer == 0)
                {
                    var idef = SphereNet.Game.Definitions.DefinitionLoader.GetItemDef(
                        SphereNet.Game.Definitions.ItemDefHelper.ResolveInstanceDefIndex(item, _resources));
                    layer = idef?.Layer ?? SphereNet.Core.Enums.Layer.None;
                }
                if (layer == SphereNet.Core.Enums.Layer.None || layer == 0)
                    return false;
                if (!wearer.Equip(item, layer))
                    return false;
                _triggerDispatcher?.FireItemTrigger(item, ItemTrigger.Equip,
                    new SphereNet.Game.Scripting.TriggerArgs { CharSrc = wearer, ItemSrc = item });
                foreach (var oc in _clients.Values)
                    oc.SendItemVisualUpdate(item);
                return true;
            };

            // Script BOUNCE/DROP verbs — release the dragged item through the
            // owning client (drag-cursor cancel + view updates); headless
            // fallback re-packs the item silently.
            SphereNet.Game.Objects.Characters.Character.OnDragRelease = (ch, toGround) =>
            {
                if (TryGetClientFor(ch, out var c))
                {
                    c.ReleaseDraggedItem(toGround);
                    return;
                }
                if (!ch.TryGetTag("DRAGGING", out string? dragSer) ||
                    !uint.TryParse(dragSer, out uint dragUid))
                    return;
                ch.RemoveTag("DRAGGING");
                var dragged = _world.FindItem(new Core.Types.Serial(dragUid));
                if (dragged == null || dragged.IsDeleted)
                    return;
                if (toGround || ch.Backpack == null)
                    _world.PlaceItemWithDecay(dragged, ch.Position);
                else if (!ch.Backpack.TryAddItem(dragged))
                    _world.PlaceItemWithDecay(dragged, ch.Position);
            };

            // Stress-test harness (.stress / .stressreport / .stressclean)
            _stressEngine = new SphereNet.Game.Diagnostics.StressTestEngine(_world, _loggerFactory);
            _commands.OnStressGenerateRequested += (items, npcs) => _stressEngine.QueueGenerate(items, npcs);
            _commands.OnStressReportRequested   += () => _stressEngine.LogReport();
            _commands.OnStressCleanupRequested  += () => _stressEngine.QueueCleanup();

            // Bot stress test engine (.bot / .botmenu)
            _botEngine = new SphereNet.Game.Diagnostics.BotEngine(_loggerFactory.CreateLogger<SphereNet.Game.Diagnostics.BotEngine>());
            // Soak placement (--botcity/--botcluster): the server positions incoming
            // bot logins (BotSpawnLocationProvider -> this engine), so clustering an
            // out-of-process fleet is configured here, not on the runner.
            if (!string.IsNullOrEmpty(_botSpawnCity))
            {
                var sc = _botSpawnCity.ToUpperInvariant() switch
                {
                    "BRITAIN" => SphereNet.Game.Diagnostics.BotSpawnCity.Britain,
                    "TRINSIC" => SphereNet.Game.Diagnostics.BotSpawnCity.Trinsic,
                    "MOONGLOW" => SphereNet.Game.Diagnostics.BotSpawnCity.Moonglow,
                    "YEW" => SphereNet.Game.Diagnostics.BotSpawnCity.Yew,
                    "MINOC" => SphereNet.Game.Diagnostics.BotSpawnCity.Minoc,
                    "VESPER" => SphereNet.Game.Diagnostics.BotSpawnCity.Vesper,
                    "SKARA" => SphereNet.Game.Diagnostics.BotSpawnCity.Skara,
                    "JHELOM" => SphereNet.Game.Diagnostics.BotSpawnCity.Jhelom,
                    _ => SphereNet.Game.Diagnostics.BotSpawnCity.All,
                };
                _botEngine.SetSpawnCity(sc);
            }
            if (_botSpawnCluster)
                _botEngine.SetClusterSpawn(true);
            _commands.OnBotCommandRequested += HandleBotCommand;
            _commands.OnBotMenuRequested += ShowBotManagerDialog;
            _commands.OnSectorListRequested += ShowSectorListDialog;

            _commands.OnRecordDialogRequested += ShowRecordingDialog;
            _commands.OnStateRecordRequested += HandleStateRecordCommand;
            _commands.OnMacroRequested += HandleMacroCommand;
            _commands.OnSaveCommand += RequestSaveOnMainLoop;
            // .SHUTDOWN / .BROADCAST were registered (Admin/GM) but had no subscriber,
            // so they were accepted and silently did nothing. Wire them: shutdown ends
            // the main loop (which now saves on exit, see Program.Tick); broadcast sends
            // the message to every in-world client.
            _commands.OnShutdownCommand += () => _mainLoopActions.Enqueue(() => _running = false);
            _commands.OnBroadcastCommand += message =>
            {
                if (string.IsNullOrWhiteSpace(message))
                    return;
                foreach (var c in _clients.Values)
                    if (c.Character != null)
                        c.SysMessage(message);
            };
            _commands.OnSaveFormatChangeRequested += RequestSaveFormatChangeOnMainLoop;
            _commands.OnScriptDebugToggleRequested += on =>
            {
                exprParser.DebugUnresolved = on;
                _log.LogInformation("Script debug logging: {State}", on ? "ON" : "OFF");
            };

            ValidateEngineWiring();
    }

    /// <summary>A5 fail-fast composition-root validation. EngineWiring is long
    /// and order-sensitive; a hook assigned before its engine exists stays null
    /// and the feature dies silently (the A1/A2 bug class: cast-interrupt-on-move
    /// and NPC night lights shipped dead for that exact reason). This validates
    /// the REAL production graph once, after all wiring, and refuses to boot
    /// half-wired instead of failing quietly at runtime.</summary>
    private static void ValidateEngineWiring()
    {
        var missing = new List<string>();
        void Require(bool wired, string name)
        {
            if (!wired) missing.Add(name);
        }

        Require(_movement.SpellEngine != null, "MovementEngine.SpellEngine (cast interrupt on move)");
        Require(_npcAI.GetLightLevel != null, "NpcAI.GetLightLevel (NPC night lights)");
        Require(_npcAI.OnWakeNpc != null, "NpcAI.OnWakeNpc (retaliation wake)");
        Require(_speech.NpcHearWired, "SpeechEngine.OnNpcHear (NPC speech)");
        Require(_speech.OnItemHear != null, "SpeechEngine.OnItemHear (comm crystal / multi speech)");
        Require(_speech.OnPlayerSpeech != null, "SpeechEngine.OnPlayerSpeech (@Speech self-trigger)");
        Require(_commands.ShutdownCommandWired, "CommandHandler.OnShutdownCommand (.shutdown)");
        Require(_commands.BroadcastCommandWired, "CommandHandler.OnBroadcastCommand (.broadcast)");
        Require(_triggerDispatcher != null, "TriggerDispatcher");
        Require(SphereNet.Game.Objects.ObjBase.ResolveWorld != null, "ObjBase.ResolveWorld");
        Require(Item.ResolveShipEngine != null, "Item.ResolveShipEngine (deed dry-dock)");

        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Engine wiring incomplete — refusing to boot half-wired: " + string.Join("; ", missing));

        _log.LogInformation("Engine wiring validated: {Count} mandatory hooks connected.", 11);
    }

    /// <summary>
    /// Full death pipeline with client-visible effects, shared by every
    /// non-combat-handler kill path (NpcAI kills, GM kill command). Sequence:
    /// @Kill/@Death triggers → DeathEngine.ProcessDeath → corpse + contents +
    /// equipment broadcast (player victims) or corpse + 0xAF + mobile delete
    /// (NPC victims) → victim client ghost transition via OnCharacterDeath.
    /// Killer may be null or the victim itself (self-kill, reflected damage);
    /// the kill is then unattributed.
    /// </summary>
    private static void ProcessDeathWithEffects(Character victim, Character? killer)
    {
        // Diagnostic for the delayed-death / missing-visuals reports: names
        // the kill entry point and the wall-clock so wire logs can be lined
        // up against the killing-damage packet.
        _log.LogDebug("[death_path] pipeline victim=0x{V:X} '{VName}' killer=0x{K:X}",
            victim.Uid.Value, victim.Name, killer?.Uid.Value ?? 0);

        var actualKiller = killer != null && ReferenceEquals(killer, victim) ? null : killer;
        var effectiveKiller = actualKiller != null ? ResolveEffectiveOffender(actualKiller) : null;

        // @Kill/@Death fire inside ProcessDeath; RETURN 1 can skip killer credit
        // or cancel the death (a still-living victim after the call = vetoed).
        var victimPos = victim.Position;
        byte victimDir = (byte)((byte)victim.Direction & 0x07);
        var corpse = _deathEngine.ProcessDeath(victim, effectiveKiller);
        if (!victim.IsDead) return; // @Death cancelled the death

        BroadcastDeathEffects(victim, effectiveKiller, corpse, victimPos, victimDir);
    }

    /// <summary>
    /// Broadcast a freshly-created corpse and transition the dying mobile to its
    /// ghost / removed state for every nearby observer. Single source of truth
    /// for the player-vs-NPC corpse packet order, the bonded-pet ghost gate, and
    /// the victim-client ghost transition — shared by the three death entry
    /// points (OnTargetKilled, OnLifecycleKill, ProcessDeathWithEffects). The
    /// @Kill/@Death trigger firing, kill credit and killer-resolution stay at
    /// each call site since they differ per entry point.
    /// </summary>
    private static void BroadcastDeathEffects(
        Character victim, Character? effectiveKiller, Item? corpse,
        Point3D victimPos, byte victimDir)
    {
        if (corpse != null)
        {
            if (victim.IsPlayer)
            {
                var corpsePacket = new PacketWorldItem(
                    corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                    corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                    victimDir);
                BroadcastNearby(victimPos, 18, corpsePacket, 0);

                // Player corpse: send contents + equip map for paperdoll corpse rendering.
                // 0x25 add-to-container is 21 bytes for >=6.0.1.7 clients (extra grid-index
                // byte) and 20 for older ones. A single shared broadcast buffer would desync
                // whichever era it does not match, so dispatch a per-recipient variant sized
                // to each observer's version instead of one shared packet.
                ForEachClientInRange(victimPos, 18, 0, (_, observerClient) =>
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
                BroadcastNearby(victimPos, 18, corpseEquip, 0);

                // 0xAF is NOT broadcast here — OnCharacterDeath below
                // runs a per-observer dispatch that sends 0xAF to plain
                // players and 0x1D + 0x78 ghost mobile to staff. A
                // blanket BroadcastNearby would hit staff with 0xAF
                // BEFORE 0x1D+0x78, causing ClassicUO to remap the
                // serial (0x80000000|serial) so the follow-up 0x1D
                // becomes a no-op and the alive body lingers under the
                // remapped key alongside the new ghost mobile.
                // Mirrors the PvP (TrySwingAt) path which also defers
                // 0xAF to OnCharacterDeath's per-observer dispatch.
            }
            else
            {
                // NPC corpse — keep the same packet order the PvP path
                // uses for non-player victims so the client sees:
                //   1) corpse world item
                //   2) death animation
                //   3) delete dead mobile
                var corpsePacket = new PacketWorldItem(
                    corpse.Uid.Value, corpse.DispIdFull, corpse.Amount,
                    corpse.X, corpse.Y, corpse.Z, corpse.Hue,
                    victimDir);
                BroadcastNearby(victimPos, 18, corpsePacket, 0);

                // Bonded pets are kept alive server-side as ghosts; skip the
                // death animation + explicit delete so ghost-capable observers
                // don't flicker (view-delta owns their appearance / resurrection).
                if (!victim.IsBonded)
                {
                    var dirToKiller = effectiveKiller != null
                        ? victimPos.GetDirectionTo(effectiveKiller.Position)
                        : victim.Direction;
                    uint npcFallDir = (uint)dirToKiller <= 3 ? 1u : 0u;
                    var deathAnim = new PacketDeathAnimation(victim.Uid.Value, corpse.Uid.Value, npcFallDir);
                    BroadcastNearby(victimPos, 18, deathAnim, 0);

                    var removeMobile = new PacketDeleteObject(victim.Uid.Value);
                    BroadcastNearby(victimPos, 18, removeMobile, 0);
                }
            }
        }

        foreach (var c in _clients.Values)
        {
            if (c.Character == victim)
            {
                c.OnCharacterDeath();
                break;
            }
        }
    }

    /// <summary>Build spell/item/char/skill definitions from the loaded scripts.
    /// MUST run before the world save loads: legacy Sphere saves carry an NPC's
    /// body only in the <c>[WORLDCHAR &lt;defname&gt;]</c> header (no per-char
    /// BODY=), and the loader resolves it through <see cref="DefinitionLoader"/>,
    /// whose char/item tables are empty until <c>LoadAll</c> runs. Loading defs
    /// afterwards left every legacy NPC at body 0 (invisible) — item BaseId has a
    /// rid.Index fallback so items survived, char bodies had none, hence the
    /// "doors show but NPCs don't" symptom.</summary>
    private static void LoadDefinitions()
    {
            // Spell defs register into the shared registry; create it here if the
            // engines haven't yet (InitializeGameEngines reuses this instance).
            _spellRegistry ??= new SpellRegistry();

            var defSw = Stopwatch.StartNew();
            // Wire diagnostic BEFORE the loader runs — script-load tracing
            // (template body inspection, etc.) only fires during LoadAll.
            DefinitionLoader.Diagnostic = msg => _log.LogDebug("{Details}", msg);
            var defLoader = new DefinitionLoader(_resources, _spellRegistry);
            defLoader.LoadAll();
            defSw.Stop();
            _log.LogInformation(
                "Definitions loaded in {Ms}ms: {Spells} spells, {Items} itemdefs, {Chars} chardefs, {Skills} skilldefs",
                defSw.ElapsedMilliseconds, defLoader.SpellsLoaded, defLoader.ItemDefsLoaded,
                defLoader.CharDefsLoaded, defLoader.SkillDefsLoaded);
    }

    /// <summary>Region/room definitions and craft recipes. Runs after the engines
    /// (needs <c>_craftingEngine</c>) and after <see cref="LoadDefinitions"/>.</summary>
    private static void LoadRegionsAndRecipes()
    {
            // Load AREADEF definitions as regions from scripts
            LoadRegionDefs();

            // Load ROOMDEF definitions from scripts
            LoadRoomDefs();

            // Source-X region nesting: regions flagged INHERIT_PARENT_* pull their
            // containing region's flags/tags/events (one-time pass after all load).
            _world.ApplyRegionInheritance();

            // Load craft recipes — must come AFTER defLoader.LoadAll() so AllItemDefs is populated
            int recipeCount = _craftingEngine.LoadRecipesFromDefs(_resources);
            if (recipeCount > 0)
                _log.LogInformation("Loaded {Count} craft recipes from SKILLMAKE definitions", recipeCount);
    }

    private static bool StartNetwork()
    {
            // --- 8. Network ---
            _log.LogInformation("Starting network...");
            int maxClients = _config.ClientMax > 0 ? _config.ClientMax : 256;
            _network = new NetworkManager(maxClients, _loggerFactory);
            _network.CryptConfig = _cryptConfig;
            _network.UseCrypt = _config.UseCrypt;
            _network.UseNoCrypt = _config.UseNoCrypt;
            _network.DefaultClientEra = _config.ClientEra;
            _network.DebugPackets = _config.DebugPackets;
            _network.DebugPacketOpcodeFilter = ParseDebugPacketOpcodes(_config.DebugPacketOpcodes);
            _network.MaxPacketsPerTick = _config.MaxPacketsPerTick;
            _network.SlowPacketWarnMs = _config.SlowPacketWarnMs;
            _network.FloodDetectionCount = _config.FloodDetectionCount;
            _network.FloodDetectionWindowMs = _config.FloodDetectionWindowMs;
            _network.ClientMaxIP = _config.ClientMaxIP;
            _network.PacketScriptHook = HandlePacketScriptHook;
            _log.LogInformation("Crypto keys loaded: {Count}, UseCrypt={UC}, UseNoCrypt={UNC}",
                _cryptConfig.Keys.Count, _config.UseCrypt, _config.UseNoCrypt);
            _network.SetHandlers(
                loginRequest: OnLoginRequest,
                gameLogin: OnGameLogin,
                charSelect: OnCharSelect,
                moveRequest: OnMoveRequest,
                movementBatch: OnMovementBatch,
                speech: OnSpeech,
                attackRequest: OnAttackRequest,
                warMode: OnWarMode,
                doubleClick: OnDoubleClick,
                singleClick: OnSingleClick,
                mailMessage: OnMailMessage,
                itemPickup: OnItemPickup,
                itemDrop: OnItemDrop,
                itemEquip: OnItemEquip,
                statusRequest: OnStatusRequest,
                targetResponse: OnTargetResponse,
                gumpResponse: OnGumpResponse,
                clientVersion: OnClientVersion,
                aosTooltip: OnAOSTooltip,
                textCommand: OnTextCommand,
                extendedCommand: OnExtendedCommand,
                resyncRequest: OnResyncRequest,
                logoutRequest: OnLogoutRequest,
                helpRequest: OnHelpRequest,
                serverSelect: OnServerSelect,
                charCreate: OnCharCreate,
                viewRange: OnViewRange,
                vendorBuy: OnVendorBuy,
                vendorSell: OnVendorSell,
                secureTrade: OnSecureTrade,
                rename: OnRename,
                profileRequest: OnProfileRequest,
                // Phase 1
                deathMenu: OnDeathMenu,
                charDelete: OnCharDelete,
                dyeResponse: OnDyeResponse,
                promptResponse: OnPromptResponse,
                equipMacro: OnEquipMacro,
                unequipMacro: OnUnequipMacro,
                publicHouseContent: OnPublicHouseContent,
                menuChoice: OnMenuChoice,
                // Phase 2
                bookPage: OnBookPage,
                bookHeader: OnBookHeader,
                bulletinBoardRequestHead: OnBulletinBoardRequestHead,
                bulletinBoardRequestMessage: OnBulletinBoardRequestMessage,
                bulletinBoardPost: OnBulletinBoardPost,
                bulletinBoardDelete: OnBulletinBoardDelete,
                mapDetail: OnMapDetail,
                mapPinEdit: OnMapPinEdit,
                // Phase 3
                gumpTextEntry: OnGumpTextEntry,
                allNamesRequest: OnAllNamesRequest,
                encodedCommand: OnEncodedCommand,
                crashReport: state =>
                {
                    if (_clients.TryGetValue(state.Id, out var c))
                        c.HandleCrashReport();
                },
                clientUiButton: (state, opcode) =>
                {
                    if (_clients.TryGetValue(state.Id, out var c))
                        c.HandleClientUiButton(opcode);
                },
                chatAction: (state, cmd, text) =>
                {
                    if (_clients.TryGetValue(state.Id, out var c))
                        c.HandleChatAction(cmd, text);
                }
            );

            _network.OnConnectionClosed += OnConnectionClosed;
            _network.OnConnectionAccepted += state =>
            {
                string ip = state.RemoteEndPoint?.Address.ToString() ?? "";
                long currentMs = Environment.TickCount64;
                var attempt = _connectionAttempts.Get(ip);
                long previousMs = attempt.CurrentMs == 0 ? currentMs : attempt.PreviousMs;
                _systemHooks.DispatchServer("connection_acquired", _serverHookContext, ip,
                    (int)Math.Clamp(currentMs - previousMs, 0, int.MaxValue), unchecked((int)currentMs));
            };
            _network.OnUnknownPacket += OnUnknownPacket;
            _network.OnPacketQuotaExceeded += OnPacketQuotaExceeded;

            if (!_network.Start("0.0.0.0", _config.ServPort))
            {
                _log.LogError("Failed to start network listener. Exiting.");
            return false;
            }

            _serverStartTime = DateTime.UtcNow;
    #if WINFORMS
            _consoleForm?.SetServerStartTime(_serverStartTime);
    #endif
            _log.LogInformation("SphereNet ready. Listening on port {Port}.", _config.ServPort);
            _systemHooks.DispatchServer("start", _serverHookContext, _config.ServName);
        return true;
    }

    /// <summary>REGION/ROOM ALLCLIENTS payload: "SYSMESSAGE &lt;text&gt;" delivers
    /// the text; anything else runs as a script function once per in-scope
    /// client character (Source-X sector ALLCLIENTS semantics).</summary>
    private static void RunAllClientsPayload(string payload, Func<Character, bool> inScope)
    {
        payload = payload?.Trim() ?? "";
        if (payload.Length == 0)
            return;
        bool sysMessage = payload.StartsWith("SYSMESSAGE", StringComparison.OrdinalIgnoreCase);
        string text = sysMessage ? payload["SYSMESSAGE".Length..].Trim() : "";
        int sp = payload.IndexOf(' ');
        string funcName = sp > 0 ? payload[..sp] : payload;
        string funcArgs = sp > 0 ? payload[(sp + 1)..].Trim() : "";

        foreach (var c in _clients.Values)
        {
            var ch = c.Character;
            if (ch == null || !c.IsPlaying || !inScope(ch))
                continue;
            if (sysMessage)
            {
                c.SysMessage(text);
                continue;
            }
            var callArgs = new SphereNet.Scripting.Execution.TriggerArgs(ch, 0, 0, funcArgs)
            {
                Object1 = ch
            };
            _ = _triggerRunner.TryRunFunction(funcName, ch, c, callArgs, out _);
        }
    }

    private static void FaceAndBroadcastToward(Character actor, Character target, int range = 18)
    {
        if (actor == null || target == null)
            return;
        if (actor.Position.Equals(target.Position))
            return;

        var newDir = actor.Position.GetDirectionTo(target.Position);
        if (newDir == actor.Direction)
            return;

        actor.Direction = newDir;
        BroadcastFacingUpdate(actor, range);
    }

    private static void BroadcastFacingUpdate(Character actor, int range = 18)
    {
        byte dirByte = (byte)((byte)actor.Direction & 0x07);
        byte flags = 0;
        if (actor.IsInvisible) flags |= 0x80;
        if (actor.IsInWarMode) flags |= 0x40;
        if (actor.BodyId == 0x0191 || actor.BodyId == 0x0193) flags |= 0x02;
        if (actor.IsStatFlag(StatFlag.Freeze)) flags |= 0x01;

        ForEachClientInRange(actor.Position, range, 0, (observerCh, observerClient) =>
        {
            byte noto = GameClient.ComputeNotoriety(_world, observerCh, actor);
            observerClient.Send(new PacketMobileMoving(
                actor.Uid.Value, actor.BodyId,
                actor.X, actor.Y, actor.Z, dirByte,
                actor.Hue, flags, noto));
        });
    }
}
