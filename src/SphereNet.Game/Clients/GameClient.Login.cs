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

    public void HandleLoginRequest(string account, string password)
    {
        _account = _accountManager.Authenticate(account, password);
        if (_account == null)
        {
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        _account.LastIp = _netState.RemoteEndPoint?.Address.ToString() ?? "";
        // Keep login-server list deterministic for local development.
        // 0.0.0.0 (or unstable interface picks) can make some clients hang.
        _netState.Send(new PacketServerList("SphereNet", 0x7F000001));
    }

    public void HandleGameLogin(string account, string password, uint authId)
    {
        _logger.LogDebug("HandleGameLogin: account='{Account}' authId=0x{AuthId:X8}", account, authId);
        _account = _accountManager.Authenticate(account, password);
        if (_account == null)
        {
            _logger.LogDebug("HandleGameLogin: AUTH FAILED for '{Account}'", account);
            _netState.Send(new PacketLoginDenied(3));
            _netState.MarkClosing();
            return;
        }

        // Feature enable (0xB9) — must come before char list.
        // Prefer config-driven FEATURE* OR from sphere.ini (set via
        // ServerFeatureFlags during startup). Fall back to a client-version
        // mapping if the config is empty (e.g. test harness without ini).
        uint featureFlags;
        if (ServerFeatureFlags != 0)
        {
            featureFlags = ServerFeatureFlags;
        }
        else if (_netState.IsClientPost7090)
            featureFlags = 0x0244; // SA+ML
        else if (_netState.IsClientPost6017)
            featureFlags = 0x0044; // ML
        else if (_netState.ClientVersionNumber >= 50_000_000)
            featureFlags = 0x0004; // context menus (SE)
        else if (_netState.ClientVersionNumber >= 40_000_000)
            featureFlags = 0x0001; // T2A (AOS)
        else
            featureFlags = 0x0000; // minimal
        _netState.Send(new PacketFeatureEnable(featureFlags, _netState.IsClientPost60142));

        var charNames = _account.GetCharNames(uid => _world.FindChar(uid)?.GetName());
        var charListPacket = new PacketCharList(charNames);
        var built = charListPacket.Build();
        _netState.Send(built);
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

        if (_character == null)
        {
            _character = _world.CreateCharacter();
            _character.Name = string.IsNullOrWhiteSpace(name) ? _account.Name : name;
            _character.IsPlayer = true;

            var info = PendingCharCreate;
            PendingCharCreate = null;

            if (info != null)
            {
                _character.BodyId = info.Female ? (ushort)0x0191 : (ushort)0x0190;
                if (info.SkinHue != 0) _character.Hue = new Color(info.SkinHue);

                int str = Math.Clamp((int)info.Str, 10, 60);
                int dex = Math.Clamp((int)info.Dex, 10, 60);
                int intl = Math.Clamp((int)info.Int, 10, 60);
                int total = str + dex + intl;
                if (total > 80) { double s = 80.0 / total; str = (int)(str * s); dex = (int)(dex * s); intl = 80 - str - dex; }
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
                    if (id < (int)SkillType.Qty && val > 0)
                    {
                        int scaled = skillScale < 1.0 ? (int)(val * skillScale) : val;
                        _character.SetSkill((SkillType)id, (ushort)(scaled * 10));
                    }
                }
            }
            else
            {
                _character.BodyId = 0x0190;
                _character.Str = 50; _character.Dex = 50; _character.Int = 50;
            }

            _character.MaxHits = _character.Str; _character.MaxMana = (short)_character.Int; _character.MaxStam = _character.Dex;
            _character.Hits = _character.MaxHits; _character.Mana = _character.MaxMana; _character.Stam = _character.MaxStam;

            var startPos = BotSpawnLocationProvider?.Invoke(_account.Name)
                ?? new Point3D(1495, 1629, 10, 0);
            _world.PlaceCharacter(_character, startPos);
            int assignSlot = slot >= 0 ? slot : _account.FindFreeSlot();
            if (assignSlot >= 0)
                _account.SetCharSlot(assignSlot, _character.Uid);

            EquipPlayerNewbieItems(_character, info?.Female ?? false);

            _logger.LogInformation("Created char '{Name}' for account '{Acct}'", _character.Name, _account.Name);
        }
        else
        {
            PendingCharCreate = null;
            var botPos = BotSpawnLocationProvider?.Invoke(_account.Name);
            if (botPos.HasValue)
                _world.MoveCharacter(_character, botPos.Value);
        }

        EnterWorld();
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
        _character.IsOnline = true;
        _world.AddOnlinePlayer(_character); // activates tick for this player's sectors
        OnCharacterOnline?.Invoke(_character, this);
        // Ensure character is in correct sector (may have been removed or stale after save/load)
        _world.PlaceCharacter(_character, _character.Position);
        EnsurePlayerBackpack(_character);
        _mountEngine?.EnsureMountedState(_character);

        if (_account != null)
        {
            var accLvl = _account.PrivLevel;
            var chLvl = _character.PrivLevel;
            var max = chLvl >= accLvl ? chLvl : accLvl;
            if (chLvl != max || accLvl != max)
            {
                _logger.LogInformation(
                    "[LOGIN] PrivLevel sync: account='{Acct}' PLEVEL={AccLvl} char=0x{Char:X8} PRIVLEVEL={ChLvl} -> {Max}",
                    _account.Name, accLvl, _character.Uid.Value, chLvl, max);
            }
            if (_account.PrivLevel != max) _account.PrivLevel = max;
            if (_character.PrivLevel != max) _character.PrivLevel = max;
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

        // Snap Z to the nearest walkable surface unless the character is
        // clearly on an upper-level structure (roof / bridge / second floor).
        // Rule:
        //   diff < 0                        → snap up  (character is below ground)
        //   0 < diff <= RoofSnapTolerance   → snap down (saved Z is stale / hovers)
        //   diff > RoofSnapTolerance        → keep (assume legitimate upper floor)
        // Without the downward snap, saves written with an out-of-band Z (e.g.
        // old dismount code that zeroed Z) keep that Z after login and every
        // subsequent CanWalkTo projects collision onto wall foundations.
        const int RoofSnapTolerance = 5;
        var mapData = _world.MapData;
        if (mapData != null)
        {
            sbyte terrainZ = mapData.GetEffectiveZ(_character.MapIndex, _character.X, _character.Y, _character.Z);
            int diff = terrainZ - _character.Z;
            if (diff != 0 && diff >= -RoofSnapTolerance)
            {
                _logger.LogInformation("Login Z correction: {OldZ} -> {NewZ} for '{Name}' at {X},{Y}",
                    _character.Z, terrainZ, _character.Name, _character.X, _character.Y);
                _character.Position = new Point3D(_character.X, _character.Y, terrainZ, _character.MapIndex);
            }
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

        // Send paperdoll info on login so the client has name/title immediately.
        SendPaperdoll(_character);
        _netState.Send(new PacketGlobalLight(_world.GlobalLight));
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
        _netState.Send(new PacketSeason((byte)_world.CurrentSeason));

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

        _knownChars.Clear();
        _knownItems.Clear();
        _lastKnownPos.Clear();
        _lastKnownItemState.Clear();

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
        foreach (uint uid in _knownChars)
            _netState.Send(new PacketDeleteObject(uid));
        foreach (uint uid in _knownItems)
            _netState.Send(new PacketDeleteObject(uid));

        _knownChars.Clear();
        _knownItems.Clear();
        _lastKnownPos.Clear();
        _lastKnownItemState.Clear();

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
        _netState.Send(new PacketGlobalLight(_world.GlobalLight));
        _netState.Send(new PacketPersonalLight(_character.Uid.Value, _character.LightLevel));
        _netState.Send(new PacketSeason((byte)_world.CurrentSeason, playSound: false));

        // 5. Reset walk sequence (0 = resync sentinel, client must send seq 0 next)
        _netState.WalkSequence = 0;
        _nextMoveTime = 0;

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
        _nextMoveTime = 0;
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
        _nextMoveTime = 0;
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

    /// <summary>Valid human/elf/gargoyle hair graphic IDs.</summary>
    private static bool IsValidHairGraphic(ushort id) =>
        (id >= 0x203B && id <= 0x204A) || (id >= 0x2FBF && id <= 0x2FD1);

    /// <summary>Valid human/elf/gargoyle beard graphic IDs.</summary>
    private static bool IsValidBeardGraphic(ushort id) =>
        (id >= 0x203E && id <= 0x2041) || (id >= 0x204B && id <= 0x204D);

    private void EquipPlayerNewbieItems(Character ch, bool female)
    {
        if (_commands?.Resources == null) return;
        var resources = _commands.Resources;

        string sectionName = female ? "FEMALE_DEFAULT" : "MALE_DEFAULT";
        var rid = resources.ResolveDefName(sectionName);
        if (!rid.IsValid)
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
