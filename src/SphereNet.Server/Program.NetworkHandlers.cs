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
    private static void OnWorldObjectCreated(SphereNet.Game.Objects.ObjBase obj)
    {
        _systemHooks.DispatchObject("create", obj);
        if (obj.IsItem)
        {
            _systemHooks.DispatchItem("create", obj);
            MarkNearbyClientsRefresh(obj.Position);
        }
        else if (obj is Character npc && !npc.IsPlayer)
        {
            if (_npcTimerWheel != null)
                _npcTimerWheel.Schedule(npc, Environment.TickCount64 + 500);
        }
    }

    private static void OnWorldObjectDeleting(SphereNet.Game.Objects.ObjBase obj)
    {
        _systemHooks.DispatchObject("delete", obj);
        if (obj.IsItem)
        {
            _systemHooks.DispatchItem("delete", obj);
            // A guild lives on its stone: Source-X ties the membership records to the
            // stone's own lifetime (CItemStone destructor, CItemStone.cpp:30). Nothing
            // connected the world's deletion to the separate guild manager here, so a
            // deleted stone left a guild nobody could reach still answering membership
            // questions - and still at war.
            _guildManager?.OnStoneDeleted(obj.Uid);
            // A champion altar takes its candles with it however it dies - .nuke, a
            // script REMOVE, a stop - because upstream clears them from the component's
            // destructor (CCChampion.cpp:92). Only the STOP path used to clean up, so
            // deleting the altar left ownerless, immovable candles standing.
            if (obj is Item deletedItem)
                deletedItem.Champion?.OnAltarDeleted();
            MarkNearbyClientsRefresh(obj.Position);
        }
        else if (obj is Character ch && !ch.IsPlayer)
        {
            _npcTimerWheel?.Remove(ch);
            MarkNearbyClientsRefresh(ch.Position);
        }
    }

    private static void OnUnknownPacket(NetState state, byte opcode, byte[] raw)
    {
        if (!_clients.TryGetValue(state.Id, out var client))
            return;
        IScriptObj? src = client.Character ?? (IScriptObj?)client.Account;
        if (src == null)
            return;
        _systemHooks.DispatchClient("unkdata", src, client.Character, $"0x{opcode:X2}", opcode, raw.Length);
    }

    private static void OnPacketQuotaExceeded(NetState state, int processed)
    {
        if (!_clients.TryGetValue(state.Id, out var client))
            return;
        IScriptObj? src = client.Character ?? (IScriptObj?)client.Account;
        if (src == null)
            return;
        _systemHooks.DispatchClient("quotaexceed", src, client.Character, processed.ToString(), processed);
    }

    private static bool HandlePacketScriptHook(NetState state, byte opcode, byte[] packet)
    {
        if (opcode != 0x03 && opcode != 0xAD && opcode != 0x6C && opcode != 0x72 && opcode != 0x22)
            return false;

        if (!_clients.TryGetValue(state.Id, out var client))
            return false;

        IScriptObj? src = client.Character ?? (IScriptObj?)client.Account;
        if (src == null)
            return false;

        string payloadHex = Convert.ToHexString(packet);
        bool handled = _systemHooks.DispatchPacket(opcode, src, client.Character, payloadHex);

        // Keep script hook visibility for war/peace packets, but do not allow
        // script short-circuit to block core war mode state changes.
        if (opcode == 0x72)
            return false;

        return handled;
    }

    private static string? ResolveDefMessage(string key)
    {
        return _resources.TryGetDefMessage(key, out var message) ? message : null;
    }

    private static void RegisterDbProviders()
    {
        // Register SQLite provider for ADO.NET DbProviderFactories
        if (!DbProviderFactories.TryGetFactory("Microsoft.Data.Sqlite", out _))
        {
            DbProviderFactories.RegisterFactory("Microsoft.Data.Sqlite", SqliteFactory.Instance);
            _log.LogDebug("Registered SQLite database provider");
        }

        // Classic Sphere packs write SQL string literals in DOUBLE quotes
        // (INSERT ... VALUES ("<LOCAL.ID>", ...)). Microsoft.Data.Sqlite's
        // bundled sqlite disables that legacy quirk (DQS), so every such
        // literal fails with "no such column". Source-X links an sqlite with
        // DQS on — restore it on every script DB connection.
        SphereNet.Scripting.Execution.ScriptDbAdapter.OnConnectionOpened = connection =>
        {
            if (connection is not Microsoft.Data.Sqlite.SqliteConnection sqliteConn ||
                sqliteConn.Handle == null)
                return;
            const int SQLITE_DBCONFIG_DQS_DML = 1013;
            const int SQLITE_DBCONFIG_DQS_DDL = 1014;
            SQLitePCL.raw.sqlite3_db_config(sqliteConn.Handle, SQLITE_DBCONFIG_DQS_DML, 1, out _);
            SQLitePCL.raw.sqlite3_db_config(sqliteConn.Handle, SQLITE_DBCONFIG_DQS_DDL, 1, out _);
        };
    }

    private static void InitDbConnections(SphereConfig config, ScriptDbAdapter db)
    {
        if (config.DbConnections.Count == 0)
        {
            _log.LogDebug("No DB connections configured.");
            return;
        }

        foreach (var connCfg in config.DbConnections)
        {
            db.RegisterConnection(connCfg);

            if (connCfg.AutoConnect)
            {
                string displayInfo = connCfg.IsSqlite
                    ? connCfg.Database
                    : $"{connCfg.Host}/{connCfg.Database}";

                if (db.Connect(connCfg.Name, out string err))
                    _log.LogInformation("DB '{Name}' connected ({Info})",
                        connCfg.Name, displayInfo);
                else
                    _log.LogWarning("DB '{Name}' auto-connect failed: {Error}", connCfg.Name, err);
            }
        }

        _log.LogInformation("Registered {Count} DB connection(s)", config.DbConnections.Count);
    }

    private static HashSet<byte>? ParseDebugPacketOpcodes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var set = new HashSet<byte>();
        foreach (var token in raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            string part = token.Trim();
            if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                part = part[2..];

            if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out byte opcode))
            {
                set.Add(opcode);
            }
        }

        return set.Count > 0 ? set : null;
    }

    // --- Network Event Handlers ---

    private static void OnConnectionClosed(int stateId)
    {
        if (_clients.TryGetValue(stateId, out var client))
        {
            client.OnDisconnect();
            _clients.Remove(stateId);
        }
    }

    private static GameClient GetOrCreateClient(NetState state)
    {
        if (!_clients.TryGetValue(state.Id, out var client))
        {
            client = new GameClient(state, _world, _accounts,
                _loggerFactory.CreateLogger<GameClient>());
            client.SetEngines(_movement, _speech, _commands, _spellEngine, _deathEngine, _partyManager, _tradeManager,
                _skillHandlers, _craftingEngine, _housingEngine, _triggerDispatcher, _guildManager, _mountEngine,
                _customHousing, _chatEngine);
            client.SetScriptServices(_systemHooks, _scriptDb, ResolveDefMessage, _scriptFile, _scriptLdb,
                _scriptDirs.Count > 0 ? _scriptDirs[0] : Path.GetDirectoryName(_config.ScpFilesDir),
                _scriptMdb);
            client.BroadcastNearby = BroadcastNearby;
            client.BroadcastMoveNearby = BroadcastMoveNearby;
            client.ForEachClientInRange = ForEachClientInRange;
            client.SendToChar = SendPacketToChar;
            client.BroadcastCharacterAppear = BroadcastCharacterAppear;
            client.OnCharacterDeathOfOther = victim =>
            {
                // Resolve the victim's own client and run its death sequence
                // (ghost transition, 0x77 broadcast, 0x20/0x2C self packets).
                if (_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                    victimClient.OnCharacterDeath();
            };
            client.OnResurrectOther = victim =>
            {
                if (_clientsByCharUid.TryGetValue(victim.Uid, out var victimClient))
                    victimClient.OnResurrect();
                else if (victim.IsDead)
                    victim.Resurrect(); // offline / NPC fallback
            };
            client.OnKillTarget = (killer, victim) =>
            {
                if (victim.IsDead || victim.IsDeleted)
                {
                    client.SysMessage($"'{victim.Name}' is already dead.");
                    return;
                }
                BroadcastLightningStrike(victim);
                ProcessDeathWithEffects(victim, killer);
                client.SysMessage($"Killed '{victim.Name}'.");
            };

            client.SendTradeToPartner = (partner, initiator, cont1, cont2) =>
            {
                if (_clientsByCharUid.TryGetValue(partner.Uid, out var pc))
                {
                    pc.NetState.Send(new PacketWorldItem(cont1.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
                    pc.NetState.Send(new PacketWorldItem(cont2.Uid.Value, 0x1E5E, 1, 0, 0, 0, 0));
                    pc.NetState.Send(new PacketSecureTradeOpen(
                        initiator.Uid.Value, cont2.Uid.Value, cont1.Uid.Value, initiator.GetName()));
                }
            };
            client.SendTradeItemToPartner = (partner, item, container) =>
            {
                if (_clientsByCharUid.TryGetValue(partner.Uid, out var pc))
                    pc.NetState.Send(new PacketContainerItem(
                        item.Uid.Value, item.DispIdFull, 0,
                        item.Amount, 30, 30,
                        container.Uid.Value, item.Hue, pc.NetState.IsClientPost6017));
            };
            client.SendTradeCloseToPartner = (partner, containerSerial) =>
            {
                if (_clientsByCharUid.TryGetValue(partner.Uid, out var pc))
                    pc.NetState.Send(new PacketSecureTradeClose(containerSerial));
            };
            client.SendTradeUpdateToPartner = (partner, trade) =>
            {
                if (_clientsByCharUid.TryGetValue(partner.Uid, out var pc))
                {
                    var theirCont = trade.GetOwnContainer(partner);
                    bool theirAcc = partner == trade.Initiator ? trade.InitiatorAccepted : trade.PartnerAccepted;
                    bool myAcc = partner == trade.Initiator ? trade.PartnerAccepted : trade.InitiatorAccepted;
                    pc.NetState.Send(new PacketSecureTradeUpdate(theirCont.Uid.Value, theirAcc, myAcc));
                }
            };
            client.SendTradeMessageToPartner = (partner, msg) =>
            {
                if (_clientsByCharUid.TryGetValue(partner.Uid, out var pc))
                    pc.SysMessage(msg);
            };
            client.RefreshBackpackForPartner = partner =>
            {
                if (_clientsByCharUid.TryGetValue(partner.Uid, out var pc))
                {
                    var pack = partner.Backpack;
                    if (pack != null)
                    {
                        foreach (var child in _world.GetContainerContents(pack.Uid))
                        {
                            pc.NetState.Send(new PacketContainerItem(
                                child.Uid.Value, child.DispIdFull, 0,
                                child.Amount, child.X, child.Y,
                                pack.Uid.Value, child.Hue,
                                pc.NetState.IsClientPost6017));
                        }
                    }
                }
            };

            _clients[state.Id] = client;
        }
        return client;
    }

    [ThreadStatic] private static List<GameClient>? _broadcastRecipients;

    /// <summary>Range at or above which BroadcastNearby delivers to every playing
    /// client on the server (all maps) instead of walking sectors — the sector
    /// loop iterates (range/SectorSize)^2 cells, so a huge range must never
    /// reach it. Used by GM yell (Source-X TALKMODE_BROADCAST).</summary>
    internal const int GlobalBroadcastRange = 10_000;

    private static void BroadcastNearby(Point3D center, int range, PacketWriter packet, uint excludeUid)
    {
        if (_recordingEngine.HasActiveRecordings)
        {
            var built = packet.Build();
            _recordingEngine.CaptureFromBroadcast(center, range, built.Span.ToArray());
            built.ReturnToPool();
        }

        if (range >= GlobalBroadcastRange)
        {
            var all = _broadcastRecipients ??= new List<GameClient>(256);
            all.Clear();
            foreach (var c in _clients.Values)
            {
                if (c.IsPlaying && c.Character != null && c.Character.Uid.Value != excludeUid)
                    all.Add(c);
            }
            if (all.Count == 0) return;
            var sharedGlobal = packet.Build();
            sharedGlobal.MarkShared(all.Count);
            foreach (var c in all)
                c.NetState.EnqueueShared(sharedGlobal);
            all.Clear();
            return;
        }

        int secRadius = (range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        int cx = center.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
        int cy = center.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;

        // Collect the in-range recipients first, then build the packet ONCE and
        // share it across them: the bytes are identical for every recipient of a
        // given event, so re-building (and re-compressing) per recipient is pure
        // waste. See PacketBuffer.MarkShared / NetState.EnqueueShared.
        var recipients = _broadcastRecipients ??= new List<GameClient>(256);
        recipients.Clear();

        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(center.Map, sx, sy);
            if (sector == null) continue;
            foreach (var ch in sector.OnlinePlayers)
            {
                if (ch.Uid.Value == excludeUid) continue;
                if (center.GetDistanceTo(ch.Position) > range) continue;
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var c) && c.IsPlaying)
                    recipients.Add(c);
            }
        }

        if (recipients.Count == 0) return;

        var shared = packet.Build();
        shared.MarkShared(recipients.Count);
        foreach (var c in recipients)
            c.NetState.EnqueueShared(shared);
        recipients.Clear();
    }

    /// <summary>
    /// Per-observer dispatch helper. Walks every online player whose character
    /// is within <paramref name="range"/> tiles of <paramref name="center"/>
    /// and invokes <paramref name="action"/> with both the observer Character
    /// and its GameClient. Used by the death/resurrect pipeline where the
    /// packet sent depends on the observer (plain player vs Counsel+ staff
    /// vs the dying player itself) — the standard BroadcastNearby helper
    /// can only dispatch a single packet to everyone.
    ///
    /// <paramref name="excludeUid"/> behaves like BroadcastNearby — pass 0
    /// to include everyone (the action can decide what to send to the
    /// dying player), or a specific UID to skip a single character.
    /// </summary>
    // Reusable per-version buckets for the damage broadcast. The combat apply
    // phase (where damage is broadcast) runs single-threaded on the main tick, so
    // these plain static lists are safe and avoid per-hit allocation.
    private static List<GameClient>? _dmgNewRecipients;
    private static List<GameClient>? _dmgOldRecipients;

    /// <summary>
    /// Broadcast a combat damage popup, selecting the wire format per recipient:
    /// 0x0B (7 bytes) for >=4.0.7a clients, 0xBF.0x22 (11 bytes) for 4.0.0-4.0.6,
    /// and nothing for older clients — matching Source-X CClient::addShowDamage.
    /// A single shared buffer is still built per version bucket, so the common
    /// all-modern case keeps the shared-broadcast fast path (one build, no
    /// per-recipient rebuild).
    /// </summary>
    private static void BroadcastDamageNearby(Point3D center, int range, uint defenderSerial, int damage, uint excludeUid)
    {
        if (_recordingEngine.HasActiveRecordings)
        {
            var built = new PacketDamage(defenderSerial, (ushort)Math.Min(damage, ushort.MaxValue)).Build();
            _recordingEngine.CaptureFromBroadcast(center, range, built.Span.ToArray());
            built.ReturnToPool();
        }

        var newBucket = _dmgNewRecipients ??= new List<GameClient>(256);
        var oldBucket = _dmgOldRecipients ??= new List<GameClient>(16);
        newBucket.Clear();
        oldBucket.Clear();

        ForEachClientInRange(center, range, excludeUid, (_, c) =>
        {
            if (c.NetState.SendsNewDamagePacket) newBucket.Add(c);
            else if (c.NetState.SendsOldDamagePacket) oldBucket.Add(c);
            // else: client predates 4.0.0 and gets no damage packet at all.
        });

        if (newBucket.Count > 0)
        {
            var shared = new PacketDamage(defenderSerial, (ushort)Math.Min(damage, ushort.MaxValue)).Build();
            shared.MarkShared(newBucket.Count);
            foreach (var c in newBucket) c.NetState.EnqueueShared(shared);
            newBucket.Clear();
        }
        if (oldBucket.Count > 0)
        {
            var shared = new PacketDamageOld(defenderSerial, (byte)Math.Min(damage, byte.MaxValue)).Build();
            shared.MarkShared(oldBucket.Count);
            foreach (var c in oldBucket) c.NetState.EnqueueShared(shared);
            oldBucket.Clear();
        }
    }

    private static void ForEachClientInRange(Point3D center, int range, uint excludeUid,
        Action<Character, GameClient> action)
    {
        int secRadius = (range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        int cx = center.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
        int cy = center.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;
        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(center.Map, sx, sy);
            if (sector == null) continue;
            foreach (var ch in sector.OnlinePlayers)
            {
                if (ch.Uid.Value == excludeUid) continue;
                if (center.GetDistanceTo(ch.Position) > range) continue;
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var c) && c.IsPlaying)
                    action(ch, c);
            }
        }
    }

    /// <summary>
    /// Movement-specific broadcast: sends 0x77 AND updates each receiving client's
    /// _lastKnownPos so the view delta won't send a duplicate 0x77 for the same step.
    /// Only sends to clients that already know this mobile — new-in-range receivers
    /// get a 0x78 (DrawObject) from the view delta instead, avoiding a race where
    /// 0x77 arrives before the client has spawned the mobile.
    /// </summary>
    [ThreadStatic] private static List<GameClient>? _broadcastMoveRecipients;

    private static void BroadcastMoveNearby(Point3D center, int range, PacketWriter packet,
        uint excludeUid, Character movingChar)
    {
        if (_recordingEngine.HasActiveRecordings)
        {
            var built = packet.Build();
            _recordingEngine.CaptureFromBroadcast(center, range, built.Span.ToArray(), movingChar.Uid.Value);
            built.ReturnToPool();
        }

        uint movingUid = movingChar.Uid.Value;
        int secRadius = (range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        int cx = center.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
        int cy = center.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;

        // The move packet is identical for every recipient; the per-recipient
        // work is the appearance precondition (a client that doesn't yet know the
        // mover gets a full appearance first) and the known-position bookkeeping.
        // Collect the eligible recipients — running the appearance side effect as
        // we go — then build + share the move packet across them.
        var recipients = _broadcastMoveRecipients ??= new List<GameClient>(256);
        recipients.Clear();

        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(center.Map, sx, sy);
            if (sector == null) continue;
            foreach (var ch in sector.OnlinePlayers)
            {
                if (ch.Uid.Value == excludeUid) continue;
                if (center.GetDistanceTo(ch.Position) > range) continue;
                if (!_clientsByCharUid.TryGetValue(ch.Uid, out var c) || !c.IsPlaying) continue;
                if (!c.HasKnownChar(movingUid))
                {
                    c.NotifyCharacterAppear(movingChar);
                    if (!c.HasKnownChar(movingUid))
                        continue;
                }
                recipients.Add(c);
            }
        }

        if (recipients.Count == 0) return;

        var shared = packet.Build();
        shared.MarkShared(recipients.Count);
        foreach (var c in recipients)
        {
            c.NetState.EnqueueShared(shared);
            c.UpdateKnownCharPosition(movingChar);
        }
        recipients.Clear();
    }

    /// <summary>
    /// Notify all nearby clients that a character appeared (login/teleport).
    /// Each client renders from its own perspective (notoriety, equipment, etc.).
    /// </summary>
    private static void BroadcastCharacterAppear(Character ch)
    {
        const int Range = 18;
        const int secSize = SphereNet.Game.World.Sectors.Sector.SectorSize;
        const int secRadius = (Range / secSize) + 1;
        int cx = ch.Position.X / secSize;
        int cy = ch.Position.Y / secSize;
        byte mapId = ch.Position.Map;
        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(mapId, sx, sy);
            if (sector == null || sector.OnlinePlayers.Count == 0) continue;
            foreach (var other in sector.OnlinePlayers)
            {
                if (other == ch) continue;
                if (ch.Position.GetDistanceTo(other.Position) > Range) continue;
                if (_clientsByCharUid.TryGetValue(other.Uid, out var c) && c.IsPlaying)
                    c.NotifyCharacterAppear(ch);
            }
        }
    }

    /// <summary>
    /// Object-centric movement handler: when any character moves, notify nearby clients
    /// directly instead of waiting for per-tick BuildViewDelta. For player movement,
    /// marks the player's own client for a full view refresh. For NPC movement, sends
    /// enter/leave/update packets to each nearby client.
    /// Player still-in-range 0x77 is handled by BroadcastMoveNearby (called after
    /// MoveCharacter in the walk handler), so OnCharacterMoved only handles the
    /// enter-range (0x78) and leave-range (0x1D) cases for players.
    /// </summary>
    private static void OnCharacterMoved(Character ch, Point3D oldPos)
    {
        bool isPlayer = ch.IsPlayer;

        if (isPlayer && ch.IsOnline)
        {
            if (_clientsByCharUid.TryGetValue(ch.Uid, out var ownClient))
                ownClient.ViewNeedsRefresh = true;
        }

        const int range = 18;
        const int secSize = SphereNet.Game.World.Sectors.Sector.SectorSize;
        const int secRadius = (range / secSize) + 1;

        int newCx = ch.Position.X / secSize;
        int newCy = ch.Position.Y / secSize;
        int oldCx = oldPos.X / secSize;
        int oldCy = oldPos.Y / secSize;

        byte mapId = ch.Position.Map;
        bool crossMap = oldPos.Map != mapId;

        // Same-map move: one sweep spanning old+new sectors. Cross-map move:
        // the old/new coordinates are unrelated, so spanning min..max would
        // walk a huge sector rectangle — sweep each map around its own point
        // instead (the old-map sweep delivers the leave-range delete that the
        // new-map sweep can never produce).
        int minSx = crossMap ? newCx - secRadius : Math.Min(newCx, oldCx) - secRadius;
        int maxSx = crossMap ? newCx + secRadius : Math.Max(newCx, oldCx) + secRadius;
        int minSy = crossMap ? newCy - secRadius : Math.Min(newCy, oldCy) - secRadius;
        int maxSy = crossMap ? newCy + secRadius : Math.Max(newCy, oldCy) + secRadius;

        NotifyMovedInSectors(ch, oldPos, isPlayer, mapId, minSx, maxSx, minSy, maxSy);
        if (crossMap)
            NotifyMovedInSectors(ch, oldPos, isPlayer, oldPos.Map,
                oldCx - secRadius, oldCx + secRadius, oldCy - secRadius, oldCy + secRadius);
    }

    private static void NotifyMovedInSectors(Character ch, Point3D oldPos, bool isPlayer,
        byte mapId, int minSx, int maxSx, int minSy, int maxSy)
    {
        for (int sx = minSx; sx <= maxSx; sx++)
        for (int sy = minSy; sy <= maxSy; sy++)
        {
            var sector = _world.GetSector(mapId, sx, sy);
            if (sector == null || sector.OnlinePlayers.Count == 0) continue;
            foreach (var other in sector.OnlinePlayers)
            {
                if (other == ch) continue;
                if (!_clientsByCharUid.TryGetValue(other.Uid, out var c) || !c.IsPlaying) continue;
                if (isPlayer)
                    c.NotifyCharEnterLeave(ch, oldPos);
                else
                    c.NotifyCharMoved(ch, oldPos);
            }
        }
    }

    /// <summary>Mark nearby clients for a view refresh when an object at the given position changes.</summary>
    private static void MarkNearbyClientsRefresh(Point3D pos)
    {
        const int Range = 18;
        const int secSize = SphereNet.Game.World.Sectors.Sector.SectorSize;
        const int secRadius = (Range / secSize) + 1;
        int cx = pos.X / secSize;
        int cy = pos.Y / secSize;
        for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
        for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
        {
            var sector = _world.GetSector(pos.Map, sx, sy);
            if (sector == null || sector.OnlinePlayers.Count == 0) continue;
            foreach (var ch in sector.OnlinePlayers)
            {
                if (pos.GetDistanceTo(ch.Position) > Range) continue;
                if (_clientsByCharUid.TryGetValue(ch.Uid, out var c) && c.IsPlaying)
                    c.ViewNeedsRefresh = true;
            }
        }
    }

    /// <summary>
    /// Mark clients near dirty (non-movement) objects for a view refresh.
    /// </summary>
    private static void MarkClientsNearDirtyObjects(IReadOnlyList<ObjBase> dirtyObjects)
    {
        if (_clientsByCharUid.Count == 0 || dirtyObjects.Count == 0)
            return;

        const int Range = 18;
        int secRadius = (Range / SphereNet.Game.World.Sectors.Sector.SectorSize) + 1;
        foreach (var obj in dirtyObjects)
        {
            var pos = obj.Position;
            bool tooltipChanged = (obj.LastConsumedDirtyFlags &
                ~(DirtyFlag.Position | DirtyFlag.Direction)) != DirtyFlag.None;
            int cx = pos.X / SphereNet.Game.World.Sectors.Sector.SectorSize;
            int cy = pos.Y / SphereNet.Game.World.Sectors.Sector.SectorSize;
            for (int sx = cx - secRadius; sx <= cx + secRadius; sx++)
            for (int sy = cy - secRadius; sy <= cy + secRadius; sy++)
            {
                var sector = _world.GetSector(pos.Map, sx, sy);
                if (sector == null) continue;
                foreach (var ch in sector.OnlinePlayers)
                {
                    if (pos.GetDistanceTo(ch.Position) > Range) continue;
                    if (_clientsByCharUid.TryGetValue(ch.Uid, out var c) && c.IsPlaying)
                    {
                        c.ViewNeedsRefresh = true;
                        if (tooltipChanged)
                            c.SendAosTooltip(obj, requested: false, invalidate: true);
                    }
                }
            }
        }
    }

    /// <summary>Send a packet to a specific character by UID.</summary>
    private static void SendPacketToChar(Serial charUid, PacketWriter packet)
    {
        if (_clientsByCharUid.TryGetValue(charUid, out var c) && c.IsPlaying)
            c.Send(packet);
    }

    private static void OnLoginRequest(NetState state, string account, string password)
    {
        var client = GetOrCreateClient(state);
        client.HandleLoginRequest(account, password);
    }

    /// <summary>Resolve an advertised/relay IP as a big-endian uint. A blank or
    /// 0.0.0.0 config IP falls back to the socket's own bound address (Source-X
    /// local-address substitution), then 127.0.0.1. Keeps the 0xA8 list and the
    /// 0x8C relay agreeing on the same address.</summary>
    private static uint ResolveAdvertisedIp(NetState state, string ipStr)
    {
        if (string.IsNullOrEmpty(ipStr) || ipStr == "0.0.0.0")
        {
            var localEp = state.LocalEndPoint;
            if (localEp != null)
            {
                var b = localEp.Address.GetAddressBytes();
                return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
            }
            return 0x7F000001; // 127.0.0.1
        }
        if (System.Net.IPAddress.TryParse(ipStr, out var addr))
        {
            var b = addr.GetAddressBytes();
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }
        return 0x7F000001;
    }

    /// <summary>Build the 0xA8 server list: this shard first (config ServName/ServIP/
    /// ServPort), then any configured extra shards (Source-X send.cpp:3289 self + defs).</summary>
    private static IReadOnlyList<ServerListEntry> BuildServerList(NetState state)
    {
        var list = new List<ServerListEntry>
        {
            new(_config.ServName, ResolveAdvertisedIp(state, _config.ServIP), (ushort)_config.ServPort),
        };
        foreach (var def in _config.ServerList)
            list.Add(new ServerListEntry(def.Name, ResolveAdvertisedIp(state, def.Ip), (ushort)def.Port));
        return list;
    }

    private static void OnServerSelect(NetState state, ushort serverIndex)
    {
        // Honor the selected list index (Source-X Server_GetDef); out-of-range falls
        // back to this shard. Extra shards relay to their own configured ip/port.
        var servers = BuildServerList(state);
        var chosen = serverIndex < servers.Count ? servers[serverIndex] : servers[0];
        uint ip = chosen.Ip;
        ushort port = chosen.Port != 0 ? chosen.Port : (ushort)_config.ServPort;
        uint authId = (uint)Random.Shared.Next(1, int.MaxValue);
        state.AuthId = authId;

        // Store login crypto keys for the game connection (Source-X RelayGameCryptStart)
        SphereNet.Network.Encryption.CryptoState.StoreRelayKeys(authId, state.Crypto.Key1, state.Crypto.Key2, state.ClientVersionNumber);
        _log.LogDebug("Relay #{Id}: server[{Idx}] ip=0x{IP:X8}, port={Port}, authId=0x{AuthId:X8}",
            state.Id, serverIndex, ip, port, authId);

        state.Send(new PacketRelay(ip, port, authId));

        // Login connection is no longer needed after relay — the client will open
        // a new TCP connection for the game server.  Mark this one for closure so it
        // doesn't linger until the idle-timeout fires.
        state.MarkClosing();
    }

    private static void OnGameLogin(NetState state, string account, string password, uint authId)
    {
        var client = GetOrCreateClient(state);
        client.HandleGameLogin(account, password, authId);
        if (client.Account != null)
            _systemHooks.DispatchAccount("connect", client.Account, client.Character);
    }

    /// <summary>
    /// Kick any existing client playing the same character.
    /// Allows multi-client with different characters on the same account.
    /// </summary>
    private static void KickDuplicateCharacter(uint charUid, int excludeStateId)
    {
        foreach (var kvp in _clients.ToArray())
        {
            if (kvp.Key == excludeStateId) continue;
            var existing = kvp.Value;
            if (existing.Character != null &&
                existing.Character.Uid.Value == charUid)
            {
                _log.LogInformation("Kicking duplicate character 0x{Uid:X8} (old connection #{Id})",
                    charUid, kvp.Key);
                existing.OnDisconnect();
                _clients.Remove(kvp.Key);
                existing.NetState.MarkClosing();
            }
        }
    }

    private static void OnCharCreate(NetState state, CharCreateInfo info)
    {
        var client = GetOrCreateClient(state);
        client.PendingCharCreate = info;
        client.HandleCharSelect(-1, info.Name);
    }

    private static void OnCharSelect(NetState state, int slot, string name)
    {
        var client = GetOrCreateClient(state);

        // Aynı karakter zaten online ise eski bağlantıyı kick et
        if (client.Account != null && slot >= 0)
        {
            var charUid = client.Account.GetCharSlot(slot);
            if (charUid.IsValid)
                KickDuplicateCharacter(charUid.Value, state.Id);
        }

        client.HandleCharSelect(slot, name);
    }

    private static void OnMoveRequest(NetState state, byte dir, byte seq, uint fastWalkKey)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.QueueMoveRequest(dir, seq, fastWalkKey);
    }

    private static void OnMovementBatch(NetState state, IReadOnlyList<MovementStep> steps)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMovementBatch(steps);
    }

    private static void OnSpeech(NetState state, byte type, ushort hue, ushort font, string text)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleSpeech(type, hue, font, text);
    }

    private static void OnAttackRequest(NetState state, uint targetUid)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleAttack(targetUid);
    }

    /// <summary>Scan loaded scripts for [DIALOG &lt;name&gt;] section names
    /// and return up to <paramref name="maxCount"/> that share a prefix
    /// with the (case-insensitive) query. Used by the ".dialog" admin
    /// command's not-found message so singular/plural typos can be
    /// fixed from the hint instead of grepping scripts by hand.</summary>
    private static List<string> CollectDialogSuggestions(string query, int maxCount)
    {
        var results = new List<string>();
        if (_resources == null || string.IsNullOrEmpty(query))
            return results;

        string q = query.ToLowerInvariant();
        string qPrefix = q.Length > 3 ? q[..3] : q;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var script in _resources.ScriptFiles)
        {
            var file = script.Open();
            try
            {
                foreach (var section in file.ReadAllSections())
                {
                    if (results.Count >= maxCount) break;
                    if (!section.Name.Equals("DIALOG", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string name = section.Argument.Split(' ', 2)[0].Trim();
                    if (name.Length == 0 || !seen.Add(name)) continue;
                    if (name.ToLowerInvariant().Contains(qPrefix))
                        results.Add(name);
                }
            }
            finally { script.Close(); }
            if (results.Count >= maxCount) break;
        }
        return results;
    }

    private static void OnWarMode(NetState state, bool warMode)
    {
        if (_clients.TryGetValue(state.Id, out var client))
        {
            var ch = client.Character;
            if (ch != null && _recordingEngine.IsReplaying(ch.Uid.Value))
            {
                FinishReplay(ch);
                SendSysMessage(ch, "Replay stopped.");
                return;
            }
            client.HandleWarMode(warMode);
        }
    }

    private static void OnDoubleClick(NetState state, uint serial)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;
        if (_macroEngine != null && client.Character != null &&
            _macroEngine.IsRecording(client.Character.Uid.Value))
        {
            var item = _world.FindItem(new Serial(serial));
            if (item != null)
                _macroEngine.CaptureUseObject(client.Character.Uid.Value, item.DispIdFull);
        }
        client.HandleDoubleClick(serial);
    }

    private static void OnSingleClick(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleSingleClick(serial);
    }

    private static void OnMailMessage(NetState state, uint targetSerial, uint attachmentSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMailMessage(targetSerial, attachmentSerial);
    }

    private static void OnItemPickup(NetState state, uint serial, ushort amount)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleItemPickup(serial, amount);
    }

    private static void OnItemDrop(NetState state, uint serial, short x, short y, sbyte z, uint container)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleItemDrop(serial, x, y, z, container);
    }

    private static void OnItemEquip(NetState state, uint serial, byte layer, uint charSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleItemEquip(serial, layer, charSerial);
    }

    private static void OnStatusRequest(NetState state, byte type, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleStatusRequest(type, serial);
    }

    private static void OnProfileRequest(NetState state, byte mode, uint serial, string bioText)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleProfileRequest(mode, serial, bioText);
    }

    private static void OnTargetResponse(NetState state, byte type, uint targetId, uint serial,
        short x, short y, sbyte z, ushort graphic)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;
        if (_macroEngine != null && client.Character != null &&
            _macroEngine.IsRecording(client.Character.Uid.Value))
        {
            _macroEngine.CaptureTarget(client.Character.Uid.Value, serial, x, y, z, graphic,
                client.Character.Uid.Value);
        }
        client.HandleTargetResponse(type, targetId, serial, x, y, z, graphic);
    }

    private static void OnGumpResponse(NetState state, uint serial, uint gumpId, uint buttonId,
        uint[] switches, (ushort Id, string Text)[] textEntries)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleGumpResponse(serial, gumpId, buttonId, switches, textEntries);
    }

    private static void OnClientVersion(NetState state, string version)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleClientVersion(version);
    }

    private static void OnAOSTooltip(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleAOSTooltip(serial);
    }

    private static void OnTextCommand(NetState state, byte type, string command)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;

        switch (type)
        {
            case 0x24: // UseSkill
                if (int.TryParse(command.Split(' ')[0], out int skillId))
                {
                    if (_macroEngine != null && client.Character != null &&
                        _macroEngine.IsRecording(client.Character.Uid.Value))
                        _macroEngine.CaptureUseSkill(client.Character.Uid.Value, skillId);
                    client.HandleUseSkill(skillId);
                }
                break;
            case 0x56: // CastSpell
                if (int.TryParse(command.Split(' ')[0], out int spellId) && spellId > 0)
                    client.HandleCastSpell((SpellType)spellId, 0);
                break;
            case 0x58: // OpenDoor
                client.OpenDoor();
                break;
            case 0xF4: // 0x12 ext-type 0xF4: either the SphereNet-internal SKILLLOCK
                       // funnel (re-emitted from the binary 0x3A packet) or the real
                       // client's EXTCMD_INVOKE_VIRTUE (Source-X CClientEvent.cpp:3127).
                var parts = command.Split(' ');
                if (parts.Length >= 3 && parts[0] == "SKILLLOCK" &&
                    ushort.TryParse(parts[1], out ushort sid) &&
                    sid < 58 &&
                    byte.TryParse(parts[2], out byte lockVal) && lockVal <= 2)
                {
                    client.Character?.SetSkillLock((SkillType)sid, lockVal);
                }
                else if (parts.Length >= 1 && parts[0].Length == 1 && char.IsDigit(parts[0][0]))
                {
                    // Virtue hotkey: a lone digit char (1=Honor, 2=Sacrifice, 3=Valor).
                    client.HandleVirtueInvoke(parts[0][0] - '0');
                }
                break;
        }
    }

    private static void OnExtendedCommand(NetState state, ushort subCmd, PacketBuffer buffer)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;

        byte[] remaining = buffer.ReadBytes(buffer.Remaining);
        client.HandleExtendedCommand(subCmd, remaining);
    }

    /// <summary>0xD7 — custom-house design editor commands (Build, Commit, ...).</summary>
    private static void OnEncodedCommand(NetState state, ushort subCmd, uint serial, PacketBuffer payload)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;
        client.HandleEncodedCommand(subCmd, serial, payload);
    }

    private static void OnResyncRequest(NetState state)
    {
        if (!_clients.TryGetValue(state.Id, out var client)) return;
        _log.LogWarning("[resync_request] Client #{Id} requested resync — walk seq was {Seq}",
            state.Id, state.WalkSequence);
        client.Resync();
    }

    /// <summary>
    /// 0xD1 — Client requested to return to character select. Send the accept
    /// reply and tear down the in-world client state (mark offline, notify
    /// nearby players) while keeping the TCP connection alive so the client
    /// can receive the char-list without reconnecting.
    /// </summary>
    private static void OnLogoutRequest(NetState state)
    {
        // Always acknowledge so the client transitions out of world.
        state.Send(new PacketLogoutAck());

        if (_clients.TryGetValue(state.Id, out var client))
        {
            client.OnDisconnect();
            // Client object is recycled on next login/char-select; leave the
            // NetState entry in _clients so future packets still route.
        }
    }

    private static void OnHelpRequest(NetState state)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleHelpRequest();
    }

    private static void BroadcastSeasonChange(bool playSound)
    {
        foreach (var client in _clients.Values)
        {
            if (!client.IsPlaying || client.Character == null) continue;
            bool dead = client.Character.IsDead;
            byte light = dead ? (byte)0 : _world.GetLightLevel(client.Character.Position);
            var r = _world.FindRegion(client.Character.Position);
            var weather = _weatherEngine.GetWeatherForRegion(r);
            client.Character.UpdateEnvironment(light, (byte)weather.Item1,
                dead ? (byte)SeasonType.Desolation : (byte)_weatherEngine.CurrentSeason);
            client.Send(new PacketSeason(dead
                ? (byte)SeasonType.Desolation
                : (byte)_weatherEngine.CurrentSeason, playSound));
            client.Send(new PacketGlobalLight(light));

            if (r != null && !string.IsNullOrEmpty(r.Name))
            {
                client.Send(new PacketWeather((byte)weather.Item1, weather.Item2, weather.Item3));
            }
        }
    }

    private static void OnViewRange(NetState state, byte range)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleViewRange(range);
    }

    private static void OnVendorBuy(NetState state, uint vendorSerial, byte flag, List<VendorBuyEntry> items)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleVendorBuy(vendorSerial, flag, items);
    }

    private static void OnVendorSell(NetState state, uint vendorSerial, List<VendorSellEntry> items)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleVendorSell(vendorSerial, items);
    }

    private static void OnSecureTrade(NetState state, byte action, uint sessionId, uint param)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleSecureTrade(action, sessionId, param);
    }

    private static void OnRename(NetState state, uint serial, string name)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleRename(serial, name);
    }

    // ==================== Phase 1: Critical Stability ====================

    private static void OnDeathMenu(NetState state, byte action)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleDeathMenu(action);
    }

    private static void OnCharDelete(NetState state, int charIndex, string password)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleCharDelete(charIndex, password);
    }

    private static void OnDyeResponse(NetState state, uint itemSerial, ushort hue)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleDyeResponse(itemSerial, hue);
    }

    private static void OnPromptResponse(NetState state, uint serial, uint promptId, uint type, string text)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandlePromptResponse(serial, promptId, type, text);
    }

    private static void OnEquipMacro(NetState state, IReadOnlyList<uint> serials)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleEquipMacro(serials);
    }

    private static void OnUnequipMacro(NetState state, IReadOnlyList<ushort> layers)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleUnequipMacro(layers);
    }

    private static void OnPublicHouseContent(NetState state, bool show)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.ShowPublicHouseContent = show;
    }

    private static void OnMenuChoice(NetState state, uint serial, ushort menuId, ushort index, ushort modelId)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMenuChoice(serial, menuId, index, modelId);
    }

    // ==================== Phase 2: Content Features ====================

    private static void OnBookPage(NetState state, uint serial, List<(ushort PageNum, string[] Lines)> pages)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBookPage(serial, pages);
    }

    private static void OnBookHeader(NetState state, uint serial, bool writable, string title, string author)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBookHeader(serial, writable, title, author);
    }

    private static void OnBulletinBoardRequestHead(NetState state, uint boardSerial, uint msgSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardRequestHead(boardSerial, msgSerial);
    }

    private static void OnBulletinBoardRequestMessage(NetState state, uint boardSerial, uint msgSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardRequestMessage(boardSerial, msgSerial);
    }

    private static void OnBulletinBoardPost(NetState state, uint boardSerial, uint replyTo, string subject, string[] bodyLines)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardPost(boardSerial, replyTo, subject, bodyLines);
    }

    private static void OnBulletinBoardDelete(NetState state, uint boardSerial, uint msgSerial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleBulletinBoardDelete(boardSerial, msgSerial);
    }

    private static void OnMapDetail(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMapDetail(serial);
    }

    private static void OnMapPinEdit(NetState state, uint serial, byte action, byte pinId, ushort x, ushort y)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleMapPinEdit(serial, action, pinId, x, y);
    }

    // ==================== Phase 3: Client Compatibility ====================

    private static void OnGumpTextEntry(NetState state, uint serial, ushort context, byte action, string text)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleGumpTextEntry(serial, context, action, text);
    }

    private static void OnAllNamesRequest(NetState state, uint serial)
    {
        if (_clients.TryGetValue(state.Id, out var client))
            client.HandleAllNamesRequest(serial);
    }

    /// <summary>
    /// NPC keyword/conversation handler. Routes speech to NPCs for keyword responses.
    /// Maps to Source-X NPC_OnHear / @NPCHearGreeting / @NPCHearUnknown triggers.
    /// </summary>
    /// <summary>Look up the GameClient that owns the given character (player only).</summary>
}
