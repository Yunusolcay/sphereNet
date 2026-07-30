using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Configuration;
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
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Party;
using SphereNet.Game.Skills;
using SphereNet.Game.Speech;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Game.Gumps;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Definitions;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Outgoing;
using SphereNet.Network.State;
using SphereNet.Core.Security;
using ExecTriggerArgs = SphereNet.Scripting.Execution.TriggerArgs;
using SphereNet.Game.Messages;
using ScriptDbAdapter = SphereNet.Scripting.Execution.ScriptDbAdapter;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    private static readonly LoginRateLimiter s_loginRateLimiter = new();

    /// <summary>Source-X account tags that carry the detected client version
    /// from the login socket to the game socket (CClientLog.cpp:916). The login
    /// connection learns the version from the 0xEF seed; the game connection
    /// gets only a bare 4-byte seed and would otherwise decide every
    /// version-dependent packet format — 0xB9 feature-flag width, the buff bar,
    /// AOS tooltips — with no version at all until the 0xBD reply lands.</summary>
    private const string ClientVersionTag = "CLIENTVERSION";
    private const string ReportedClientVersionTag = "REPORTEDCLIVER";

    /// <summary>Remember the connection's client version on the account so the
    /// next connection of the same account starts out knowing it.</summary>
    private void StoreAccountClientVersion(string tag)
    {
        if (_account == null || _netState.ClientVersionNumber == 0)
            return;
        _account.SetTag(tag, _netState.ClientVersionNumber.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Persist the version the client reported through 0xBD.</summary>
    internal void StoreReportedClientVersion() =>
        StoreAccountClientVersion(ReportedClientVersionTag);

    /// <summary>Seed this connection's version from the account tags when the
    /// socket has none of its own (Source-X send.cpp reads the same tags rather
    /// than the live socket). Returns true when a version was adopted.</summary>
    private bool RestoreAccountClientVersion()
    {
        if (_account == null || _netState.ClientVersionNumber != 0)
            return false;

        uint best = 0;
        foreach (string tag in new[] { ClientVersionTag, ReportedClientVersionTag })
        {
            if (_account.TryGetTag(tag, out string raw) &&
                uint.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out uint parsed))
                best = Math.Max(best, parsed);
        }
        if (best == 0)
            return false;

        _netState.ClientVersionNumber = best;
        _logger.LogDebug("Client version restored from account tags: {Num}", best);
        return true;
    }

    public void HandleLoginRequest(string account, string password)
    {
        if (IsLoginLimited(account))
            return;

        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
        {
            RegisterLoginFailure(account);
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        _account = _accountManager.Authenticate(account, password);
        if (_account == null)
        {
            RegisterLoginFailure(account);
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        RegisterLoginSuccess(account);
        _account.LastIp = _netState.RemoteEndPoint?.Address.ToString() ?? "";
        // The login socket's 0xEF seed carries the version; hand it to the game
        // socket through the account, exactly like Source-X does before the
        // relay ("pass detected client version to the game server").
        StoreAccountClientVersion(ClientVersionTag);
        if (_netState.ClientVersionNumber == 0)
            _netState.Send(new PacketClientVersionRequest());
        // Config-driven server list (self entry + any configured extra shards).
        // Falls back to a deterministic local entry when no provider is wired —
        // 0.0.0.0 (or unstable interface picks) can make some clients hang.
        var serverEntries = ServerListProvider?.Invoke(_netState);
        _netState.Send(serverEntries is { Count: > 0 }
            ? new PacketServerList(serverEntries)
            : new PacketServerList("SphereNet", 0x7F000001));
    }

    public void HandleGameLogin(string account, string password, uint authId)
    {
        if (IsLoginLimited(account))
            return;

        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
        {
            RegisterLoginFailure(account);
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        _logger.LogDebug("HandleGameLogin: account='{Account}' authId=0x{AuthId:X8}", account, authId);
        _account = _accountManager.Authenticate(account, password);
        if (_account == null)
        {
            _logger.LogDebug("HandleGameLogin: AUTH FAILED for '{Account}'", account);
            RegisterLoginFailure(account);
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        RegisterLoginSuccess(account);
        // Adopt the version the login connection detected BEFORE any capability
        // decision below: 0xB9 picks a 3- or 5-byte body from it, and a modern
        // client whose packet table expects 5 desyncs on a 3-byte one.
        RestoreAccountClientVersion();
        if (_netState.ClientVersionNumber == 0)
            _netState.Send(new PacketClientVersionRequest());
        // Feature enable (0xB9) — must come before char list.
        // Translate independent Source-X FEATURE* capability masks into the
        // 0xB9 and 0xA9 flags allowed by this account/client ResDisp.
        ResDisplayVersion resDisp = ResolveAccountResDisplay();
        int maxChars = GetEffectiveMaxChars();
        SendLoginCapabilities(resDisp, maxChars);
    }

    private ResDisplayVersion ResolveAccountResDisplay()
    {
        if (_account == null)
            return ResDisplayVersion.T2A;

        ResDisplayVersion res;
        if (ServerAutoResDisp)
        {
            res = DetectResDisplay(_netState.ClientVersionNumber, _netState.ClientEra);
            _account.ResDisp = (byte)res;
        }
        else if (_account.ResDisp < (byte)ResDisplayVersion.Qty)
        {
            res = (ResDisplayVersion)_account.ResDisp;
        }
        else
        {
            res = ResDisplayVersion.T2A;
        }

        _netState.ClientExpansion = res switch
        {
            ResDisplayVersion.PreT2A => Expansion.None,
            ResDisplayVersion.T2A => Expansion.T2A,
            ResDisplayVersion.LBR => Expansion.LBR,
            ResDisplayVersion.AOS => Expansion.AOS,
            ResDisplayVersion.SE => Expansion.SE,
            ResDisplayVersion.ML or ResDisplayVersion.KR => Expansion.ML,
            ResDisplayVersion.SA => Expansion.SA,
            ResDisplayVersion.HS => Expansion.HS,
            _ => Expansion.TOL,
        };
        return res;
    }

    public static ResDisplayVersion DetectResDisplay(uint version, ClientEra fallbackEra)
    {
        if (version >= 70_045_065) return ResDisplayVersion.TOL;
        if (version >= 70_009_000) return ResDisplayVersion.HS;
        if (version >= 70_000_000) return ResDisplayVersion.SA;
        if (version >= 50_000_000) return ResDisplayVersion.ML;
        if (version >= 40_005_000) return ResDisplayVersion.SE;
        if (version >= 40_000_000) return ResDisplayVersion.AOS;
        if (version >= 30_007_002) return ResDisplayVersion.LBR;
        if (version != 0) return ResDisplayVersion.T2A;
        return fallbackEra == ClientEra.Modern ? ResDisplayVersion.TOL : ResDisplayVersion.T2A;
    }

    public static uint BuildFeatureFlags(ResDisplayVersion res, int maxChars)
    {
        uint flags = 0;
        if (res >= ResDisplayVersion.T2A)
        {
            if ((ServerFeatureT2A & 0x01) != 0) flags |= 0x0004;
            if ((ServerFeatureT2A & 0x02) != 0) flags |= 0x0001;
        }
        if (res >= ResDisplayVersion.LBR)
        {
            if ((ServerFeatureLBR & 0x01) != 0) flags |= 0x0008;
            if ((ServerFeatureLBR & 0x02) != 0) flags |= 0x0002;
        }
        if (res >= ResDisplayVersion.AOS && (ServerFeatureAOS & 0x01) != 0)
            flags |= 0x8010;
        if (res >= ResDisplayVersion.SE && (ServerFeatureSE & 0x02) != 0)
            flags |= 0x0040;
        if (res >= ResDisplayVersion.ML && (ServerFeatureML & 0x01) != 0)
            flags |= 0x0080;
        if (res >= ResDisplayVersion.SA && (ServerFeatureSA & 0x01) != 0)
            flags |= 0x10000;
        if (res >= ResDisplayVersion.TOL && (ServerFeatureTOL & 0x01) != 0)
            flags |= 0x400000;

        if ((ServerFeatureExtra & 0x01) != 0) flags |= 0x000200;
        if ((ServerFeatureExtra & 0x02) != 0) flags |= 0x040000;
        if ((ServerFeatureExtra & 0x04) != 0) flags |= 0x080000;
        if ((ServerFeatureExtra & 0x08) != 0) flags |= 0x100000;
        if ((ServerFeatureExtra & 0x10) != 0) flags |= 0x200000;
        if ((ServerFeatureExtra & 0x20) != 0) flags |= 0x002000;
        if (maxChars >= 6) flags |= 0x0020;
        if (maxChars >= 7) flags |= 0x1000;
        return flags;
    }

    public static uint BuildCharacterListFlags(ResDisplayVersion res, int maxChars, bool tooltipsEnabled)
    {
        uint flags = 0;
        if (res >= ResDisplayVersion.AOS)
        {
            if ((ServerFeatureAOS & 0x04) != 0) flags |= 0x0008;
            if (tooltipsEnabled && (ServerFeatureAOS & 0x02) != 0) flags |= 0x0020;
        }
        if (res >= ResDisplayVersion.SE && (ServerFeatureSE & 0x02) != 0) flags |= 0x0080;
        if (res >= ResDisplayVersion.ML && (ServerFeatureML & 0x01) != 0) flags |= 0x0100;
        if (res >= ResDisplayVersion.KR)
        {
            if ((ServerFeatureKR & 0x01) != 0) flags |= 0x0200;
            if ((ServerFeatureKR & 0x02) != 0) flags |= 0x0400;
        }
        if (res >= ResDisplayVersion.SA && (ServerFeatureSA & 0x02) != 0) flags |= 0x4000;
        if (maxChars == 1) flags |= 0x0014;
        if (maxChars >= 6) flags |= 0x0040;
        if (maxChars >= 7) flags |= 0x1000;
        return flags;
    }

    internal int GetEffectiveMaxChars() => Math.Clamp(
        Math.Min(_account?.MaxChars ?? ServerMaxCharsPerAccount, ServerMaxCharsPerAccount), 1, 7);

    internal ResDisplayVersion HandleResolvedClientVersion()
    {
        var res = ResolveAccountResDisplay();
        if (_account != null && _character == null && _netState.ConnectionType == ConnectType.Game)
            SendLoginCapabilities(res, GetEffectiveMaxChars());
        return res;
    }

    private void SendLoginCapabilities(ResDisplayVersion resDisp, int maxChars)
    {
        if (_account == null) return;
        uint featureFlags = BuildFeatureFlags(resDisp, maxChars);
        uint charListFlags = BuildCharacterListFlags(resDisp, maxChars, ServerToolTipMode != 0);
        _netState.Send(new PacketFeatureEnable(featureFlags, _netState.IsClientPost60142));
        _netState.Send(new PacketCharList(
            _account.GetCharNames(uid => _world.FindChar(uid)?.GetName()),
            maxChars, _netState.SupportsNewCharacterList, charListFlags));
    }

    private bool IsLoginLimited(string account)
    {
        string key = LoginRateLimitKey(account);
        if (!s_loginRateLimiter.IsLimited(key, out var retryAfter))
            return false;

        _logger.LogWarning("[AUTH] Login throttled for {Key}; retry after {Seconds:F1}s",
            key, retryAfter.TotalSeconds);
        _netState.Send(new PacketLoginDenied(3));
        _netState.MarkClosing();
        return true;
    }

    private void RegisterLoginFailure(string account) =>
        s_loginRateLimiter.RegisterFailure(LoginRateLimitKey(account));

    private void RegisterLoginSuccess(string account) =>
        s_loginRateLimiter.RegisterSuccess(LoginRateLimitKey(account));

    private string LoginRateLimitKey(string account)
    {
        string ip = _netState.RemoteEndPoint?.Address.ToString() ?? "unknown";
        string name = string.IsNullOrWhiteSpace(account) ? "<empty>" : account.Trim();
        return $"{ip}:{name}";
    }

    public void HandleCharSelect(int slot, string name)
    {
        if (_account == null) return;

        // Dedup: if this client already has a live character, a retransmitted
        // 0x5D/0xF8 must not create a second one. Without this guard a bugged
        // client sending the create packet N times produced N characters and
        // N paperdoll-open packets — observed as "20 paperdolls opened".
        if (_character != null && _character.IsOnline)
        {
            _logger.LogDebug("[LOGIN] Ignoring duplicate CharSelect for account '{Acct}'",
                _account.Name);
            return;
        }

        var charUid = _account.GetCharSlot(slot);
        if (charUid.IsValid)
            _character = _world.FindChar(charUid);

        int effectiveMaxChars = GetEffectiveMaxChars();
        if (_character == null &&
            (_account.CharCount >= effectiveMaxChars || (slot >= 0 && slot >= effectiveMaxChars)))
        {
            _logger.LogWarning("[LOGIN] Character creation denied for account '{Acct}': {Count}/{Max}",
                _account.Name, _account.CharCount, effectiveMaxChars);
            PendingCharCreate = null;
            ReturnToCharacterList();
            return;
        }

        // Reconnecting during client linger resumes the same world object. Drop
        // the temporary active-sector membership before EnterWorld adds it back.
        if (_character?.IsClientLingering == true)
        {
            _character.RemoveTag("CLIENT_LINGER_UNTIL");
            _world.RemoveOnlinePlayer(_character);
        }

        if (_character == null)
        {
            _character = _world.CreateCharacter();
            string safeName = SanitizeCharacterName(string.IsNullOrWhiteSpace(name) ? _account.Name : name);
            _character.Name = safeName;
            _character.IsPlayer = true;
            _character.CreatedUtcSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var info = PendingCharCreate;
            PendingCharCreate = null;

            var createHookArgs = new SphereNet.Scripting.Execution.TriggerArgs(
                _character, unchecked((int)(info?.ClientFlags ?? 0)), info?.Profession ?? 0, _account.Name)
            {
                Number3 = info?.Race ?? 1
            };
            if (_triggerDispatcher?.Runner is { } initRunner &&
                initRunner.TryRunFunction(
                    "f_onchar_create_init", _character, this, createHookArgs, out var initResult) &&
                initResult == TriggerResult.True)
            {
                _world.DeleteObject(_character);
                _character = null;
                ReturnToCharacterList();
                return;
            }

            if (info != null)
            {
                // Body follows the chosen race (elf/gargoyle), not always human.
                _character.BodyId = (info.Race, info.Female) switch
                {
                    (2, false) => (ushort)0x025D, (2, true) => (ushort)0x025E, // elf m/f
                    (3, false) => (ushort)0x029A, (3, true) => (ushort)0x029B, // gargoyle m/f
                    (_, false) => (ushort)0x0190, (_, true) => (ushort)0x0191, // human m/f
                };
                if (info.SkinHue != 0) _character.Hue = new Color(info.SkinHue);

                int str = Math.Clamp((int)info.Str, 10, 60);
                int dex = Math.Clamp((int)info.Dex, 10, 60);
                int intl = Math.Clamp((int)info.Int, 10, 60);
                int total = str + dex + intl;
                if (total > 80) { double s = 80.0 / total; str = (int)(str * s); dex = (int)(dex * s); intl = 80 - str - dex; }
                str = Math.Max(10, str); dex = Math.Max(10, dex); intl = Math.Max(10, intl);
                _character.Str = (short)str; _character.Dex = (short)dex; _character.Int = (short)intl;

                if (info.HairStyle != 0 && IsValidHairGraphic(info.HairStyle))
                {
                    var hair = _world.CreateItem();
                    hair.BaseId = info.HairStyle;
                    if (info.HairHue != 0) hair.Hue = new Color(info.HairHue);
                    _character.Equip(hair, Layer.Hair);
                }
                if (info.BeardStyle != 0 && !info.Female && IsValidBeardGraphic(info.BeardStyle))
                {
                    var beard = _world.CreateItem();
                    beard.BaseId = info.BeardStyle;
                    if (info.BeardHue != 0) beard.Hue = new Color(info.BeardHue);
                    _character.Equip(beard, Layer.FacialHair);
                }

                // Enforce total skill points cap (UO standard: 120 for new characters)
                const int MaxTotalSkillPoints = 120;
                int totalSkill = 0;
                foreach (var (_, sv) in info.Skills)
                    totalSkill += sv;
                double skillScale = totalSkill > MaxTotalSkillPoints ? (double)MaxTotalSkillPoints / totalSkill : 1.0;
                foreach (var (id, val) in info.Skills)
                {
                    if (id >= 0 && id < SkillEngine.BaseSkillCount && val > 0)
                    {
                        int scaled = skillScale < 1.0 ? (int)(val * skillScale) : val;
                        _character.SetSkill((SkillType)id, (ushort)(scaled * 10));
                    }
                }
                // Diagnostic: what the client actually sent for the creation
                // skills. If these are all value=0 (or empty) the chosen skills
                // and their newbie kits won't apply — the client used a
                // profession preset the server doesn't expand, or sent the
                // values at a value we read as 0. [char_create_skills] makes the
                // real wire data visible for the no-skill/no-kit reports.
                _logger.LogDebug("[char_create_skills] name='{Name}' skills=[{Skills}]",
                    info.Name,
                    string.Join(", ", info.Skills.Select(s =>
                        $"{(s.Id < (int)SkillType.Qty ? ((SkillType)s.Id).ToString() : s.Id.ToString())}={s.Value}")));
            }
            else
            {
                _character.BodyId = 0x0190;
                _character.Str = 50; _character.Dex = 50; _character.Int = 50;
            }

            _character.MaxHits = _character.Str; _character.MaxMana = (short)_character.Int; _character.MaxStam = _character.Dex;
            _character.Hits = _character.MaxHits; _character.Mana = _character.MaxMana; _character.Stam = _character.MaxStam;

            // Spawn at the starting city the player picked (its index into the
            // 0xA9 city list), not always city 0. Bots keep their provider-driven
            // spawn; a missing/out-of-range index falls back to [STARTS]/Britain.
            Point3D? cityPos = null;
            if (info != null &&
                SphereNet.Network.Packets.Outgoing.PacketCharList.GetCity(info.City) is { } c)
                cityPos = new Point3D((short)c.X, (short)c.Y, (sbyte)c.Z, (byte)c.Map);

            var startPos = BotSpawnLocationProvider?.Invoke(_account.Name)
                ?? cityPos
                ?? _commands?.Resources?.Starts.FirstOrDefault()?.Point
                ?? new Point3D(1495, 1629, 10, 0);
            _world.PlaceCharacter(_character, startPos);

            // Starting stipend: [STARTSGOLD] rows are parallel to the [STARTS]
            // city list. A new character spawns at start index 0, so it gets
            // that row's amount as ONE pile — not the sum of every row.
            var startGoldList = _commands?.Resources?.StartGold;
            int startGoldAmount = startGoldList is { Count: > 0 } ? startGoldList[0].Amount : 0;
            if (startGoldAmount > 0)
            {
                var pack = _character.Backpack;
                if (pack == null)
                {
                    pack = _world.CreateItem();
                    pack.BaseId = 0x0E75;
                    _character.Equip(pack, Layer.Pack);
                }
                var gold = _world.CreateItem();
                gold.BaseId = 0x0EED;
                gold.Name = "gold";
                gold.Amount = (ushort)Math.Min(ushort.MaxValue, startGoldAmount);
                pack.AddItem(gold);
            }
            EquipPlayerNewbieItems(_character, info?.Female ?? false);
            EquipNewbieSkillKits(_character, info);

            // Tint the starter shirt/pants with the hues picked on the creation
            // screen (the client sends them in the create packet).
            if (info != null)
            {
                if (info.ShirtHue != 0 && _character.GetEquippedItem(Layer.Shirt) is { } shirt)
                    shirt.Hue = new Color(info.ShirtHue);
                if (info.PantsHue != 0 && _character.GetEquippedItem(Layer.Pants) is { } pants)
                    pants.Hue = new Color(info.PantsHue);
            }

            // Reference serv_triggers pipeline: every fresh player character
            // runs f_onchar_create_player and then f_onchar_setup_player once
            // it exists server-side — the pack's own start setup (skillclass,
            // Char_Start_Player) lives behind these functions.
            var createRunner = _triggerDispatcher?.Runner;
            if (createRunner != null)
            {
                if (createRunner.TryRunFunction("f_onchar_create", _character, this, createHookArgs, out var createResult) &&
                    createResult == TriggerResult.True)
                {
                    _world.DeleteObject(_character);
                    _character = null;
                    ReturnToCharacterList();
                    return;
                }
            }

            int assignSlot = slot >= 0 ? slot : _account.FindFreeSlot();
            if (assignSlot >= 0)
                _account.SetCharSlot(assignSlot, _character.Uid);

            _logger.LogInformation("Created char '{Name}' for account '{Acct}'", _character.Name, _account.Name);
        }
        else
        {
            PendingCharCreate = null;
            var botPos = BotSpawnLocationProvider?.Invoke(_account.Name);
            if (botPos.HasValue)
                _world.MoveCharacter(_character, botPos.Value);
        }

        // Bot accounts get a combat/magic buff so the load/test bots can actually
        // fight and cast (newbie stats can't out-damage monsters). Bot accounts
        // are ephemeral test accounts; re-applied on every login.
        if (_account != null && _character != null &&
            SphereNet.Game.Diagnostics.BotClient.IsBotAccountName(_account.Name))
            ApplyBotCombatBuff(_character);

        EnterWorld();
    }

    private void ReturnToCharacterList()
    {
        if (_account == null) return;
        var resDisp = ResolveAccountResDisplay();
        int maxChars = GetEffectiveMaxChars();
        _netState.Send(new PacketCharList(
            _account.GetCharNames(uid => _world.FindChar(uid)?.GetName()),
            maxChars, _netState.SupportsNewCharacterList,
            BuildCharacterListFlags(resDisp, maxChars, ServerToolTipMode != 0)));
    }

    private static void ApplyBotCombatBuff(Character ch)
    {
        ch.Str = 100; ch.Dex = 100; ch.Int = 100;
        ch.MaxHits = ch.Str; ch.MaxMana = (short)ch.Int; ch.MaxStam = ch.Dex;
        ch.Hits = ch.MaxHits; ch.Mana = ch.MaxMana; ch.Stam = ch.MaxStam;
        // Wrestling makes unarmed melee effective without needing a weapon item;
        // Magery/EvalInt enable spellcasting.
        ch.SetSkill(SphereNet.Core.Enums.SkillType.Wrestling, 1000);
        ch.SetSkill(SphereNet.Core.Enums.SkillType.Tactics, 1000);
        ch.SetSkill(SphereNet.Core.Enums.SkillType.Anatomy, 1000);
        ch.SetSkill(SphereNet.Core.Enums.SkillType.Magery, 1000);
        ch.SetSkill(SphereNet.Core.Enums.SkillType.EvalInt, 1000);
        ch.SetSkill(SphereNet.Core.Enums.SkillType.Healing, 1000);
    }

    private void EnterWorld()
    {
        if (_character == null) return;
        // Dedup re-entry: 7.0.x clients sometimes retransmit 0x5D/0xF8 during
        // handshake. Every repeat used to drive a fresh login packet burst
        // (including 0x88 OpenPaperdoll), producing N paperdoll windows on the
        // client. Once IsOnline is set, subsequent EnterWorld calls are no-ops.
        if (_character.IsOnline)
        {
            _logger.LogDebug("[LOGIN] Ignoring duplicate EnterWorld for '{Name}'", _character.Name);
            return;
        }

        if (_account != null)
        {
            _character.SetTag("ACCOUNT", _account.Name);
            bool slotFound = false;
            for (int i = 0; i < 7; i++)
            {
                if (_account.GetCharSlot(i) == _character.Uid)
                { slotFound = true; break; }
            }
            if (!slotFound)
            {
                int free = _account.FindFreeSlot();
                if (free >= 0)
                    _account.SetCharSlot(free, _character.Uid);
            }
        }

        _logger.LogInformation("[LOGIN] '{Name}' pos: {X},{Y},{Z} map={Map}",
            _character.Name, _character.X, _character.Y, _character.Z, _character.Position.Map);
        EngineTags.StripEphemeral(_character);
        _character.MigrateStatLockFromTags();
        _character.IsOnline = true;
        _world.AddOnlinePlayer(_character); // activates tick for this player's sectors
        OnCharacterOnline?.Invoke(_character, this);
        // Ensure character is in correct sector (may have been removed or stale after save/load)
        _world.PlaceCharacter(_character, _character.Position);
        EnsurePlayerBackpack(_character);
        _mountEngine?.EnsureMountedState(_character);
        RepairCharacterAppearance(_character);

        if (_account != null)
        {
            // Account is authoritative — character inherits from account, never elevates it
            if (_character.PrivLevel != _account.PrivLevel)
            {
                _logger.LogInformation(
                    "[LOGIN] PrivLevel sync: account='{Acct}' PLEVEL={AccLvl} char=0x{Char:X8} PRIVLEVEL={ChLvl} -> {AccLvl2}",
                    _account.Name, _account.PrivLevel, _character.Uid.Value, _character.PrivLevel, _account.PrivLevel);
                _character.PrivLevel = _account.PrivLevel;
            }
        }
        if (_character.TryGetTag("JAIL_RELEASE", out string? jailTag))
        {
            if (_character.IsJailExpired())
            {
                // Sentence already served while logged out — release on login
                // instead of re-applying an expired jail.
                _character.RemoveTag("JAIL_RELEASE");
                _character.ClearStatFlag(StatFlag.Freeze);
                var spawnPos = new Point3D(1495, 1629, 10, 0);
                _world.MoveCharacter(_character, spawnPos);
            }
            else
            {
                // Still serving — re-confine to the correct numbered cell (Source-X
                // re-runs Jail() on login via the JailCell account tag).
                int cell = 0;
                if (_character.TryGetTag("JAIL_CELL", out string? cellTag) && int.TryParse(cellTag, out int c))
                    cell = c;
                var jailPos = _world.GetJailPoint(cell);
                if (_character.Position.X != jailPos.X || _character.Position.Y != jailPos.Y)
                {
                    _world.MoveCharacter(_character, jailPos);
                    _character.SetStatFlag(StatFlag.Freeze);
                }
            }
        }

        _character.NormalizePlayerSkillClass();

        // Ensure Max stats are derived from attributes if missing (old saves)
        if (_character.MaxHits <= 0 && _character.Str > 0)
            _character.MaxHits = _character.Str;
        if (_character.MaxMana <= 0 && _character.Int > 0)
            _character.MaxMana = _character.Int;
        if (_character.MaxStam <= 0 && _character.Dex > 0)
            _character.MaxStam = _character.Dex;
        // Ensure current stats are at least 1 for a living character
        if (_character.Hits <= 0 && !_character.IsDead && _character.MaxHits > 0)
            _character.Hits = _character.MaxHits;
        if (_character.Mana <= 0 && _character.MaxMana > 0)
            _character.Mana = _character.MaxMana;
        if (_character.Stam <= 0 && _character.MaxStam > 0)
            _character.Stam = _character.MaxStam;

        // Sync _last* tracking fields so TickStatUpdate sends initial packets correctly
        _lastHits = _character.Hits;
        _lastMana = _character.Mana;
        _lastStam = _character.Stam;

        // Seat the character on its true standing surface, matching Source-X:
        // a character is always on a real floor/terrain, never hovering. The
        // client re-derives the player's surface Z on every 0x20 self-update
        // (sent whenever the character turns to face a used object), so if the
        // server Z is off-surface the first double-click snaps the player up or
        // down — the reported "Z jumps on dclick" bug.
        //
        // GetEffectiveZ already implements the Source-X climb model: it returns
        // the walkable floor/roof/bridge closest to the character's current Z
        // (so a legitimate upper storey is preserved) and only falls back to
        // terrain when nothing supports the character there — i.e. it was
        // hovering. An earlier ±12 tolerance kept that hover instead of
        // dropping it, which is exactly what produced the desync; settle on
        // the shared standing-surface resolver instead (the WalkCheck
        // geometry authority — it sees multis/dynamics/custom houses and
        // honors headroom, so a save on a house floor seats the RIGHT floor).
        var loginStand = _world.Standing.ResolveStandingSurface(_character,
            _character.MapIndex, _character.X, _character.Y, _character.Z,
            Movement.WalkCheck.StandingPolicy.Settle);
        if (loginStand.Found && loginStand.Z != _character.Z)
        {
            _logger.LogInformation("Login Z correction: {OldZ} -> {NewZ} for '{Name}' at {X},{Y}",
                _character.Z, loginStand.Z, _character.Name, _character.X, _character.Y);
            _character.Position = new Point3D(_character.X, _character.Y, loginStand.Z, _character.MapIndex);
        }

        // Map dimensions per map index (ML-expanded Felucca = 7168x4096)
        ushort mapW = _character.MapIndex switch
        {
            0 => 7168,  // Felucca (ML expanded)
            1 => 7168,  // Trammel (ML expanded)
            2 => 2304,  // Ilshenar
            3 => 2560,  // Malas
            4 => 1448,  // Tokuno
            5 => 1280,  // Ter Mur
            _ => 7168
        };
        ushort mapH = _character.MapIndex switch
        {
            0 => 4096,
            1 => 4096,
            2 => 1600,
            3 => 2048,
            4 => 1448,
            5 => 4096,
            _ => 4096
        };

        _netState.Send(new PacketLoginConfirm(
            _character.Uid.Value, _character.BodyId,
            _character.X, _character.Y, _character.Z,
            (byte)_character.Direction, mapW, mapH
        ));

        _netState.Send(new PacketMapChange((byte)_character.MapIndex));
        _netState.Send(new PacketMapPatches()); // no map diffs — all zeros

        SendCharacterStatus(_character);
        SendSkillList();

        _netState.Send(new PacketGlobalLight(_character.IsDead ? (byte)0 : _world.GetLightLevel(_character.Position)));
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
        _netState.Send(new PacketSeason(_character.IsDead
            ? (byte)SeasonType.Desolation
            : (byte)_world.CurrentSeason));

        // Send player's own character with equipment — client needs this to render worn items
        SendDrawObject(_character);

        // Send equipped items individually (0x2E) so client tracks them in inventory
        for (int i = 1; i <= (int)Layer.Horse; i++)
        {
            var equip = _character.GetEquippedItem((Layer)i);
            if (equip != null)
            {
                _netState.Send(new PacketWornItem(
                    equip.Uid.Value, equip.DispIdFull, (byte)i,
                    _character.Uid.Value, equip.Hue));
            }
        }

        _netState.Send(new PacketLoginComplete());
        SendSpeedMode();
        _spellEngine?.ResendBuffs(_character);

        // Source-X parity: send paperdoll on login so the client has name/title
        // data immediately (some clients restore the paperdoll window on reconnect).
        string paperdollTitle = string.IsNullOrEmpty(_character.Title)
            ? _character.GetName()
            : $"{_character.GetName()}, {_character.Title}";
        _netState.Send(new PacketOpenPaperdoll(
            _character.Uid.Value, paperdollTitle, 0x02));
        _paperdollThrottle[_character.Uid.Value] = Environment.TickCount64;

        View.KnownChars.Clear();
        View.KnownItems.Clear();
        View.LastKnownPos.Clear();
        View.LastKnownItemState.Clear();

        // Fire @LogIn trigger
        _triggerDispatcher?.FireCharTrigger(_character, CharTrigger.LogIn, new TriggerArgs { CharSrc = _character });
        _systemHooks?.DispatchClient("add", _character, _account);

        // Source-X CClient::Login: post LOGIN_PLAYER / LOGIN_PLAYERS so the new
        // arrival sees how many fellow players are already in the shard.
        int otherPlayers = 0;
        foreach (var c in _world.OnlinePlayers)
            if (c != _character && c.IsPlayer && c.IsOnline) otherPlayers++;
        if (otherPlayers == 1)
            SysMessage(ServerMessages.Get(Msg.LoginPlayer));
        else if (otherPlayers > 1)
            SysMessage(ServerMessages.GetFormatted(Msg.LoginPlayers, otherPlayers));

        // Source-X also stamps the previous login timestamp via LOGIN_LASTLOGGED.
        if (_account != null && _account.LastLogin > DateTime.MinValue)
        {
            SysMessage(ServerMessages.GetFormatted(Msg.LoginLastlogged,
                _account.LastLogin.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        if (_character.IsDead)
        {
            bool isFemale = _character.BodyId == 0x0191 || _character.BodyId == 0x025E || _character.BodyId == 0x029B;
            ushort ghostBody = isFemale ? (ushort)0x0193 : (ushort)0x0192;
            if (_character.OSkin == 0 && _character.Hue.Value != 0 &&
                _character.BodyId != 0x0192 && _character.BodyId != 0x0193)
                _character.OSkin = _character.Hue.Value;
            if (_character.BodyId != 0x0192 && _character.BodyId != 0x0193)
                _character.BodyId = ghostBody;
            _character.Hue = Core.Types.Color.Default;
            if (Character.PacketDeathAnimationEnabled)
                _netState.Send(new PacketDeathStatus(PacketDeathStatus.ActionDead));
        }

        // Ensure first world snapshot is fully consistent.
        // Some clients can show partially black map/chunk artifacts if they only
        // receive the minimal login packet set without a full nearby object refresh.
        Resync();
        _mountEngine?.EnsureMountedState(_character);

        // Immediate view update so this client sees all nearby characters/items
        // right away, without waiting for the next game-loop tick.
        UpdateClientView();

        _logger.LogInformation("Client '{Name}' entered world at {Pos}", _character.Name, _character.Position);
    }

    private void EnsurePlayerBackpack(Character ch)
    {
        if (!ch.IsPlayer)
            return;

        Item? pack = ch.GetEquippedItem(Layer.Pack) ?? ch.Backpack;
        if (pack == null)
        {
            // Recover backpack by containment link first to avoid creating duplicates.
            pack = _world.GetContainerContents(ch.Uid)
                .FirstOrDefault(i =>
                    !i.IsDeleted &&
                    (i.EquipLayer == Layer.Pack ||
                     i.BaseId == 0x0E75 ||
                     i.ItemType == ItemType.Container));
        }
        if (pack == null || pack.IsDeleted || _world.FindItem(pack.Uid) == null)
        {
            pack = _world.CreateItem();
            pack.BaseId = 0x0E75; // backpack
            pack.ItemType = ItemType.Container;
            pack.Name = "Backpack";
        }

        // Keep canonical backpack metadata consistent, then ensure it is equipped.
        pack.ItemType = ItemType.Container;
        if (pack.BaseId == 0)
            pack.BaseId = 0x0E75;
        if (string.IsNullOrWhiteSpace(pack.Name))
            pack.Name = "Backpack";

        ch.Backpack = pack;
        if (ch.GetEquippedItem(Layer.Pack) != pack)
            ch.Equip(pack, Layer.Pack);
    }

    /// <summary>
    /// Full client resync. Clears all known objects and re-sends the entire world state.
    /// Maps to CClient::addReSync in Source-X. Called when:
    ///   - Client requests resync (packet 0x22 / .RESYNC command)
    ///   - Movement desync detected
    ///   - Teleport/map change
    ///   - GM manually triggers it
    /// </summary>
    public void Resync()
    {
        if (_character == null || !IsPlaying) return;
        _mountEngine?.EnsureMountedState(_character);

        // 1. Delete all known objects on client side
        foreach (uint uid in View.KnownChars)
            _netState.Send(new PacketDeleteObject(uid));
        foreach (uint uid in View.KnownItems)
            _netState.Send(new PacketDeleteObject(uid));

        View.KnownChars.Clear();
        View.KnownItems.Clear();
        View.LastKnownPos.Clear();
        View.LastKnownItemState.Clear();

        // 2. Reposition player first, then send full appearance.
        // DrawPlayer (0x20) must come BEFORE DrawObject (0x78) because the
        // UO client redraws the local character on 0x20 without equipment —
        // sending 0x78 afterwards restores the full equipment visual including
        // the mount at Layer.Horse.
        _netState.Send(new PacketDrawPlayer(
            _character.Uid.Value, _character.BodyId, _character.Hue,
            BuildMobileFlags(_character),
            _character.X, _character.Y, _character.Z, (byte)_character.Direction));
        SendDrawObject(_character);

        // Send equipped items individually so client tracks them
        for (int i = 1; i <= (int)Layer.Horse; i++)
        {
            var equip = _character.GetEquippedItem((Layer)i);
            if (equip != null)
            {
                _netState.Send(new PacketWornItem(
                    equip.Uid.Value, equip.DispIdFull, (byte)i,
                    _character.Uid.Value, equip.Hue));
            }
        }

        // 3. Re-send full status
        SendCharacterStatus(_character);

        // 4. Re-send light & season
        _netState.Send(new PacketGlobalLight(_character.IsDead ? (byte)0 : _world.GetLightLevel(_character.Position)));
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
        _netState.Send(new PacketSeason(_character.IsDead
            ? (byte)SeasonType.Desolation
            : (byte)_world.CurrentSeason, playSound: false));

        // 5. Reset walk sequence (0 = resync sentinel, client must send seq 0 next)
        _netState.WalkSequence = 0;
        ResetWalkValidator();

        // 6. Final authoritative DrawObject — ensures mount at Layer.Horse renders.
        // Some clients skip mount rendering from the first 0x78 if it arrives
        // interleaved with status/light packets. This final 0x78 is sent after
        // all other visual updates, guaranteeing the equipment list (including
        // mount) is processed last.
        SendDrawObject(_character);

        // 7. Force full scan on next tick to re-populate all nearby objects
        ViewNeedsRefresh = true;

        BroadcastCharacterAppear?.Invoke(_character);

        _logger.LogDebug("Resync for client '{Name}'", _character.Name);
    }

    /// <summary>Resolve CHARDEF/body graphic and clear transient visual flags
    /// before login packets go out. Saves sometimes store a def hash
    /// (e.g. 0x03DB) in BODY instead of the display Id (0x0190); old saves
    /// can also persist spell/replay visuals such as hue 0x03EC + freeze.</summary>
    private void RepairCharacterAppearance(Character ch)
    {
        ushort bodyBefore = ch.BodyId;
        ushort hueBefore = ch.Hue.Value;
        StatFlag flagsBefore = ch.StatFlags;

        bool repaired = CharDefHelper.EnsureDisplayBody(ch, DefinitionLoader.StaticResources);
        bool visualCleaned = ch.ClearTransientVisualState();
        if (repaired || visualCleaned)
        {
            string defname = ch.TryGetTag("CHARDEF", out string? tag) ? tag ?? "" : "";
            _logger.LogInformation(
                "[appearance_repair] char=0x{Uid:X8} def='{Def}' body=0x{BodyBefore:X4}->0x{BodyAfter:X4} hue=0x{HueBefore:X4}->0x{HueAfter:X4} flags=0x{FlagsBefore:X8}->0x{FlagsAfter:X8}",
                ch.Uid.Value, defname, bodyBefore, ch.BodyId, hueBefore, ch.Hue.Value,
                (uint)flagsBefore, (uint)ch.StatFlags);
        }
    }

    /// <summary>
    /// Handle a map-boundary teleport: tell the client which map to render, then
    /// run a full resync so it drops objects from the old map and receives the
    /// new map's objects via the view-delta pipeline.
    /// </summary>
    public void HandleMapChanged()
    {
        if (_character == null || !IsPlaying) return;
        _netState.Send(new PacketMapChange((byte)_character.MapIndex));
        Resync();
    }

    /// <summary>
    /// Re-send DrawPlayer + DrawObject so the owner client re-renders the player
    /// with updated appearance flags (invisible → translucent, war mode, etc.).
    /// The client-side visual state only changes when it receives a fresh draw.
    /// </summary>
    public void SendSelfRedraw()
    {
        if (_character == null || !IsPlaying) return;
        _netState.Send(new PacketDrawPlayer(
            _character.Uid.Value, _character.BodyId, _character.Hue,
            BuildMobileFlags(_character),
            _character.X, _character.Y, _character.Z, (byte)_character.Direction));
        SendDrawObject(_character);
        // 0x20 causes the client to reset its walk sequence counter to 0.
        // Server-side must mirror that reset or the next client walk comes
        // in with seq=0 while expectedSeq still holds the pre-redraw value,
        // producing a seq_mismatch reject storm.
        _netState.WalkSequence = 0;
        ResetWalkValidator();
    }

    /// <summary>
    /// Partial resync — re-sends only position and nearby objects without full clear.
    /// Used for minor movement desync corrections.
    /// </summary>
    public void ResyncPosition()
    {
        if (_character == null) return;

        _netState.Send(new PacketDrawPlayer(
            _character.Uid.Value, _character.BodyId, _character.Hue,
            0, _character.X, _character.Y, _character.Z,
            (byte)_character.Direction));
        _netState.WalkSequence = 0;
        ResetWalkValidator();
    }

    /// <summary>Re-send the 0x4E PacketPersonalLight packet so the client
    /// applies the current <see cref="Character.LightLevel"/>. Called after
    /// effects that change personal brightness (e.g. Night Sight) —
    /// without this the server-side property change has no visible effect.</summary>
    public void SendPersonalLight()
    {
        if (_character == null || !IsPlaying) return;
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
    }

    private static string SanitizeCharacterName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "Unknown";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '\'')
                sb.Append(c);
        }
        string result = sb.ToString().Trim();
        if (result.Length > 30) result = result[..30];
        return result.Length == 0 ? "Unknown" : result;
    }

    /// <summary>Valid human/elf/gargoyle hair graphic IDs.</summary>
    private static bool IsValidHairGraphic(ushort id) =>
        (id >= 0x203B && id <= 0x204A) || (id >= 0x2FBF && id <= 0x2FD1);

    /// <summary>Valid human/elf/gargoyle beard graphic IDs.</summary>
    private static bool IsValidBeardGraphic(ushort id) =>
        (id >= 0x203E && id <= 0x2041) || (id >= 0x204B && id <= 0x204D);

    private void EquipPlayerNewbieItems(Character ch, bool female)
    {
        EquipNewbieSection(ch, female ? "FEMALE_DEFAULT" : "MALE_DEFAULT");
    }

    /// <summary>Sphere grants a starter kit per chosen creation skill through
    /// the [NEWBIE &lt;SKILL&gt;] sections (sphere_newb.scp) on top of the
    /// male/female default outfit. Unknown section names no-op.</summary>
    private void EquipNewbieSkillKits(Character ch, CharCreateInfo? info)
    {
        if (info == null) return;
        foreach (var (id, val) in info.Skills)
        {
            if (val <= 0 || id < 0 || id >= SkillEngine.BaseSkillCount) continue;
            EquipNewbieSection(ch, ((SkillType)id).ToString().ToUpperInvariant());
        }
    }

    /// <summary>NEWBIESKILL verb entry (Source-X CHV_NEWBIESKILL): apply a
    /// [NEWBIE name] resource section to a character at runtime.</summary>
    internal void ApplyNewbieSection(Character ch, string sectionName) =>
        EquipNewbieSection(ch, sectionName);

    private void EquipNewbieSection(Character ch, string sectionName)
    {
        if (_commands?.Resources == null) return;
        var resources = _commands.Resources;

        var rid = resources.ResolveDefName(sectionName);
        if (!rid.IsValid || rid.Type != Core.Enums.ResType.NewBie)
            return;

        var link = resources.GetResource(rid);
        if (link?.StoredKeys == null || link.StoredKeys.Count == 0)
            return;

        var entries = new List<NewbieItemEntry>();
        foreach (var sk in link.StoredKeys)
        {
            string key = sk.Key.ToUpperInvariant();
            if (key is "ITEMNEWBIE" or "ITEM")
            {
                var parts = sk.Arg.Split(',', StringSplitOptions.TrimEntries);
                var entry = new NewbieItemEntry
                {
                    DefName = parts.Length > 0 ? parts[0] : "",
                    Newbie = key == "ITEMNEWBIE",
                };
                if (parts.Length >= 2 && int.TryParse(parts[1], out int amt) && amt > 0)
                    entry.Amount = amt;
                if (parts.Length >= 3 && !string.IsNullOrWhiteSpace(parts[2]))
                    entry.Dice = parts[2];
                entries.Add(entry);
            }
            else if (key == "COLOR" && entries.Count > 0)
            {
                entries[^1].Color = sk.Arg.Trim();
            }
        }

        if (entries.Count > 0)
            EquipNewbieItems(ch, entries);
    }

    // ==================== Movement ====================
}
