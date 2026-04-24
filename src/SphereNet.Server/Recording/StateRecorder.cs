using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Recording;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Server.Recording;

public sealed class StateRecorder : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger _logger;
    private SqliteConnection? _db;

    private const int MoveScanIntervalMs = 500;
    private const int SnapshotIntervalMs = 10_000;
    private const int FlushIntervalMs = 3_000;
    private const int CleanupIntervalMs = 600_000;
    private const int RetentionDays = 3;

    private long _lastMoveScanTick;
    private long _lastSnapshotTick;
    private long _lastFlushTick;
    private long _lastCleanupTick;

    private readonly List<MoveRecord> _moveBuffer = new(512);
    private readonly List<SnapshotRecord> _snapshotBuffer = new(256);
    private readonly Dictionary<uint, (short X, short Y, sbyte Z, byte Map, byte Dir)> _lastPositions = [];

    private SqliteCommand? _insertMoveCmd;
    private SqliteCommand? _insertSnapshotCmd;

    public StateRecorder(string dbPath, ILogger logger)
    {
        _dbPath = dbPath;
        _logger = logger;
    }

    public void Initialize()
    {
        _db = new SqliteConnection($"Data Source={_dbPath}");
        _db.Open();

        using var pragma = _db.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-8000;";
        pragma.ExecuteNonQuery();

        using var ddl = _db.CreateCommand();
        ddl.CommandText = """
            CREATE TABLE IF NOT EXISTS char_moves (
                char_uid INTEGER NOT NULL,
                ts       INTEGER NOT NULL,
                x        INTEGER NOT NULL,
                y        INTEGER NOT NULL,
                z        INTEGER NOT NULL,
                map      INTEGER NOT NULL,
                dir      INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS char_snapshots (
                char_uid  INTEGER NOT NULL,
                ts        INTEGER NOT NULL,
                hour_key  TEXT    NOT NULL,
                x         INTEGER NOT NULL,
                y         INTEGER NOT NULL,
                z         INTEGER NOT NULL,
                map       INTEGER NOT NULL,
                dir       INTEGER NOT NULL,
                body_id   INTEGER NOT NULL,
                hue       INTEGER NOT NULL,
                name      TEXT    NOT NULL,
                is_player INTEGER NOT NULL,
                flags     INTEGER NOT NULL,
                hits      INTEGER NOT NULL,
                max_hits  INTEGER NOT NULL,
                mana      INTEGER NOT NULL,
                max_mana  INTEGER NOT NULL,
                stam      INTEGER NOT NULL,
                max_stam  INTEGER NOT NULL,
                equipment TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS pinned_periods (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                start_ts   INTEGER NOT NULL,
                end_ts     INTEGER NOT NULL,
                label      TEXT    NOT NULL,
                pinned_by  TEXT    NOT NULL,
                is_public  INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS shared_views (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                char_uid   INTEGER NOT NULL,
                start_ts   INTEGER NOT NULL,
                end_ts     INTEGER NOT NULL,
                label      TEXT    NOT NULL,
                shared_by  TEXT    NOT NULL,
                created_at INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_moves_uid_ts     ON char_moves(char_uid, ts);
            CREATE INDEX IF NOT EXISTS idx_moves_ts          ON char_moves(ts);
            CREATE INDEX IF NOT EXISTS idx_snapshots_uid_ts  ON char_snapshots(char_uid, ts);
            CREATE INDEX IF NOT EXISTS idx_snapshots_ts      ON char_snapshots(ts);
            CREATE INDEX IF NOT EXISTS idx_snapshots_hour    ON char_snapshots(hour_key);
            """;
        ddl.ExecuteNonQuery();

        PrepareStatements();
        _logger.LogInformation("StateRecorder initialized: {Path}", _dbPath);
    }

    private void PrepareStatements()
    {
        _insertMoveCmd = _db!.CreateCommand();
        _insertMoveCmd.CommandText =
            "INSERT INTO char_moves(char_uid,ts,x,y,z,map,dir) VALUES(@u,@t,@x,@y,@z,@m,@d)";
        _insertMoveCmd.Parameters.Add("@u", SqliteType.Integer);
        _insertMoveCmd.Parameters.Add("@t", SqliteType.Integer);
        _insertMoveCmd.Parameters.Add("@x", SqliteType.Integer);
        _insertMoveCmd.Parameters.Add("@y", SqliteType.Integer);
        _insertMoveCmd.Parameters.Add("@z", SqliteType.Integer);
        _insertMoveCmd.Parameters.Add("@m", SqliteType.Integer);
        _insertMoveCmd.Parameters.Add("@d", SqliteType.Integer);
        _insertMoveCmd.Prepare();

        _insertSnapshotCmd = _db.CreateCommand();
        _insertSnapshotCmd.CommandText = """
            INSERT INTO char_snapshots(char_uid,ts,hour_key,x,y,z,map,dir,
                body_id,hue,name,is_player,flags,hits,max_hits,mana,max_mana,stam,max_stam,equipment)
            VALUES(@u,@t,@h,@x,@y,@z,@m,@d,@bi,@hu,@n,@ip,@f,@hp,@mhp,@mn,@mmn,@st,@mst,@eq)
            """;
        _insertSnapshotCmd.Parameters.Add("@u", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@t", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@h", SqliteType.Text);
        _insertSnapshotCmd.Parameters.Add("@x", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@y", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@z", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@m", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@d", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@bi", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@hu", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@n", SqliteType.Text);
        _insertSnapshotCmd.Parameters.Add("@ip", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@f", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@hp", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@mhp", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@mn", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@mmn", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@st", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@mst", SqliteType.Integer);
        _insertSnapshotCmd.Parameters.Add("@eq", SqliteType.Text);
        _insertSnapshotCmd.Prepare();
    }

    // ----------------------------------------------------------------
    //  Main tick — called every server tick (~100ms)
    // ----------------------------------------------------------------

    public void Tick(long nowMs, IEnumerable<Character> allCharacters)
    {
        if (_db == null) return;

        if (nowMs - _lastMoveScanTick >= MoveScanIntervalMs)
        {
            ScanMovements(allCharacters);
            _lastMoveScanTick = nowMs;
        }

        if (nowMs - _lastSnapshotTick >= SnapshotIntervalMs)
        {
            TakeSnapshots(allCharacters);
            _lastSnapshotTick = nowMs;
        }

        if (nowMs - _lastFlushTick >= FlushIntervalMs)
        {
            Flush();
            _lastFlushTick = nowMs;
        }

        if (nowMs - _lastCleanupTick >= CleanupIntervalMs)
        {
            Cleanup();
            _lastCleanupTick = nowMs;
        }
    }

    private void ScanMovements(IEnumerable<Character> chars)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var ch in chars)
        {
            if (ch.IsDeleted) continue;
            uint uid = ch.Uid.Value;
            var cur = (ch.X, ch.Y, ch.Z, ch.Position.Map, (byte)ch.Direction);

            if (_lastPositions.TryGetValue(uid, out var prev))
            {
                if (prev.X == cur.X && prev.Y == cur.Y && prev.Z == cur.Z &&
                    prev.Map == cur.Map && prev.Dir == (byte)cur.Item5)
                    continue;
            }

            _lastPositions[uid] = cur;
            _moveBuffer.Add(new MoveRecord(uid, ts, cur.X, cur.Y, cur.Z, cur.Map, (byte)cur.Item5));
        }
    }

    private void TakeSnapshots(IEnumerable<Character> chars)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string hourKey = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HH");
        foreach (var ch in chars)
        {
            if (ch.IsDeleted) continue;
            byte flags = 0;
            if (ch.IsInWarMode) flags |= 0x40;
            if (ch.IsInvisible) flags |= 0x80;

            _snapshotBuffer.Add(new SnapshotRecord(
                ch.Uid.Value, ts, hourKey,
                ch.X, ch.Y, ch.Z, ch.Position.Map, (byte)ch.Direction,
                ch.BodyId, ch.Hue, ch.Name ?? "?", ch.IsPlayer,
                flags, ch.Hits, ch.MaxHits, ch.Mana, ch.MaxMana,
                ch.Stam, ch.MaxStam, EncodeEquipment(ch)));
        }
    }

    private void Flush()
    {
        if (_moveBuffer.Count == 0 && _snapshotBuffer.Count == 0) return;

        try
        {
            using var tx = _db!.BeginTransaction();

            if (_moveBuffer.Count > 0)
            {
                _insertMoveCmd!.Transaction = tx;
                foreach (var r in _moveBuffer)
                {
                    _insertMoveCmd.Parameters["@u"].Value = (long)r.CharUid;
                    _insertMoveCmd.Parameters["@t"].Value = r.Ts;
                    _insertMoveCmd.Parameters["@x"].Value = (int)r.X;
                    _insertMoveCmd.Parameters["@y"].Value = (int)r.Y;
                    _insertMoveCmd.Parameters["@z"].Value = (int)r.Z;
                    _insertMoveCmd.Parameters["@m"].Value = (int)r.Map;
                    _insertMoveCmd.Parameters["@d"].Value = (int)r.Dir;
                    _insertMoveCmd.ExecuteNonQuery();
                }
            }

            if (_snapshotBuffer.Count > 0)
            {
                _insertSnapshotCmd!.Transaction = tx;
                foreach (var r in _snapshotBuffer)
                {
                    _insertSnapshotCmd.Parameters["@u"].Value = (long)r.CharUid;
                    _insertSnapshotCmd.Parameters["@t"].Value = r.Ts;
                    _insertSnapshotCmd.Parameters["@h"].Value = r.HourKey;
                    _insertSnapshotCmd.Parameters["@x"].Value = (int)r.X;
                    _insertSnapshotCmd.Parameters["@y"].Value = (int)r.Y;
                    _insertSnapshotCmd.Parameters["@z"].Value = (int)r.Z;
                    _insertSnapshotCmd.Parameters["@m"].Value = (int)r.Map;
                    _insertSnapshotCmd.Parameters["@d"].Value = (int)r.Dir;
                    _insertSnapshotCmd.Parameters["@bi"].Value = (int)r.BodyId;
                    _insertSnapshotCmd.Parameters["@hu"].Value = (int)r.Hue;
                    _insertSnapshotCmd.Parameters["@n"].Value = r.Name;
                    _insertSnapshotCmd.Parameters["@ip"].Value = r.IsPlayer ? 1 : 0;
                    _insertSnapshotCmd.Parameters["@f"].Value = (int)r.Flags;
                    _insertSnapshotCmd.Parameters["@hp"].Value = (int)r.Hits;
                    _insertSnapshotCmd.Parameters["@mhp"].Value = (int)r.MaxHits;
                    _insertSnapshotCmd.Parameters["@mn"].Value = (int)r.Mana;
                    _insertSnapshotCmd.Parameters["@mmn"].Value = (int)r.MaxMana;
                    _insertSnapshotCmd.Parameters["@st"].Value = (int)r.Stam;
                    _insertSnapshotCmd.Parameters["@mst"].Value = (int)r.MaxStam;
                    _insertSnapshotCmd.Parameters["@eq"].Value = r.Equipment;
                    _insertSnapshotCmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StateRecorder flush failed ({Moves} moves, {Snaps} snapshots)",
                _moveBuffer.Count, _snapshotBuffer.Count);
        }
        finally
        {
            _moveBuffer.Clear();
            _snapshotBuffer.Clear();
        }
    }

    private void Cleanup()
    {
        long cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays).ToUnixTimeMilliseconds();

        try
        {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = """
                DELETE FROM char_moves WHERE ts < @cutoff
                AND NOT EXISTS (
                    SELECT 1 FROM pinned_periods
                    WHERE char_moves.ts BETWEEN start_ts AND end_ts
                );
                DELETE FROM char_snapshots WHERE ts < @cutoff
                AND NOT EXISTS (
                    SELECT 1 FROM pinned_periods
                    WHERE char_snapshots.ts BETWEEN start_ts AND end_ts
                );
                """;
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            int deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                _logger.LogInformation("StateRecorder cleanup: {Deleted} old records removed", deleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StateRecorder cleanup failed");
        }
    }

    // ----------------------------------------------------------------
    //  Pinned periods
    // ----------------------------------------------------------------

    public void PinPeriod(long startTs, long endTs, string label, string pinnedBy, bool isPublic = false)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pinned_periods(start_ts, end_ts, label, pinned_by, is_public, created_at)
            VALUES(@s, @e, @l, @p, @pub, @c)
            """;
        cmd.Parameters.AddWithValue("@s", startTs);
        cmd.Parameters.AddWithValue("@e", endTs);
        cmd.Parameters.AddWithValue("@l", label);
        cmd.Parameters.AddWithValue("@p", pinnedBy);
        cmd.Parameters.AddWithValue("@pub", isPublic ? 1 : 0);
        cmd.Parameters.AddWithValue("@c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public bool UnpinPeriod(int pinId)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "DELETE FROM pinned_periods WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", pinId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<(int Id, long StartTs, long EndTs, string Label, string PinnedBy, bool IsPublic)> GetPinnedPeriods()
    {
        var result = new List<(int, long, long, string, string, bool)>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT id, start_ts, end_ts, label, pinned_by, is_public FROM pinned_periods ORDER BY start_ts DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetInt32(0), r.GetInt64(1), r.GetInt64(2),
                r.GetString(3), r.GetString(4), r.GetInt32(5) != 0));
        return result;
    }

    // ----------------------------------------------------------------
    //  Shared views
    // ----------------------------------------------------------------

    public void ShareView(uint charUid, long startTs, long endTs, string label, string sharedBy)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO shared_views(char_uid, start_ts, end_ts, label, shared_by, created_at)
            VALUES(@u, @s, @e, @l, @sb, @c)
            """;
        cmd.Parameters.AddWithValue("@u", (long)charUid);
        cmd.Parameters.AddWithValue("@s", startTs);
        cmd.Parameters.AddWithValue("@e", endTs);
        cmd.Parameters.AddWithValue("@l", label);
        cmd.Parameters.AddWithValue("@sb", sharedBy);
        cmd.Parameters.AddWithValue("@c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public bool UnshareView(int shareId)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "DELETE FROM shared_views WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", shareId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public List<(int Id, uint CharUid, string Label, long StartTs, long EndTs, string SharedBy)> GetSharedViews()
    {
        var result = new List<(int, uint, string, long, long, string)>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT id, char_uid, label, start_ts, end_ts, shared_by FROM shared_views ORDER BY created_at DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add((r.GetInt32(0), (uint)r.GetInt64(1), r.GetString(2),
                r.GetInt64(3), r.GetInt64(4), r.GetString(5)));
        return result;
    }

    public bool CanView(PrivLevel viewerPrivLevel, uint targetCharUid, long startTs, long endTs)
    {
        if (viewerPrivLevel >= PrivLevel.Admin) return true;

        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM shared_views
            WHERE char_uid = @uid AND start_ts <= @s AND end_ts >= @e
            """;
        cmd.Parameters.AddWithValue("@uid", (long)targetCharUid);
        cmd.Parameters.AddWithValue("@s", startTs);
        cmd.Parameters.AddWithValue("@e", endTs);
        return (long)(cmd.ExecuteScalar() ?? 0) > 0;
    }

    // ----------------------------------------------------------------
    //  Query helpers
    // ----------------------------------------------------------------

    public List<(uint Uid, string Name, bool IsPlayer, long LastTs, int Records)> GetRecordedCharacters(int limit = 30)
    {
        var result = new List<(uint, string, bool, long, int)>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            SELECT char_uid, name, is_player, MAX(ts) as last_ts, COUNT(*) as cnt
            FROM char_snapshots
            GROUP BY char_uid
            ORDER BY last_ts DESC
            LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("@lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(((uint)r.GetInt64(0), r.GetString(1), r.GetInt32(2) != 0,
                r.GetInt64(3), r.GetInt32(4)));
        return result;
    }

    public List<(string HourKey, long StartTs, int SnapshotCount, int MoveCount)> GetHourBuckets(uint charUid, int limit = 72)
    {
        var result = new List<(string, long, int, int)>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            SELECT hour_key, MIN(ts) as start_ts, COUNT(*) as cnt
            FROM char_snapshots
            WHERE char_uid = @uid
            GROUP BY hour_key
            ORDER BY hour_key DESC
            LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("@uid", (long)charUid);
        cmd.Parameters.AddWithValue("@lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string hk = r.GetString(0);
            long st = r.GetInt64(1);
            int snapCount = r.GetInt32(2);

            using var mc = _db.CreateCommand();
            mc.CommandText = "SELECT COUNT(*) FROM char_moves WHERE char_uid=@u AND ts>=@s AND ts<@e";
            mc.Parameters.AddWithValue("@u", (long)charUid);
            mc.Parameters.AddWithValue("@s", st);
            mc.Parameters.AddWithValue("@e", st + 3_600_000);
            int moveCount = (int)(long)(mc.ExecuteScalar() ?? 0);

            result.Add((hk, st, snapCount, moveCount));
        }
        return result;
    }

    public uint? FindCharUidByName(string name)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT char_uid FROM char_snapshots WHERE name = @n COLLATE NOCASE ORDER BY ts DESC LIMIT 1";
        cmd.Parameters.AddWithValue("@n", name);
        var val = cmd.ExecuteScalar();
        return val != null ? (uint)(long)val : null;
    }

    public long GetDbSizeBytes()
    {
        try { return new System.IO.FileInfo(_dbPath).Length; }
        catch { return 0; }
    }

    // ----------------------------------------------------------------
    //  Replay session builder
    // ----------------------------------------------------------------

    public RecordingSession? BuildReplaySession(uint charUid, long startMs, long endMs)
    {
        var initSnap = GetClosestSnapshot(charUid, startMs);
        if (initSnap == null) return null;

        var session = new RecordingSession
        {
            Id = $"state_{charUid:X8}_{startMs}",
            RecorderName = initSnap.Value.Name,
            RecorderUid = charUid,
            Center = new Point3D(initSnap.Value.X, initSnap.Value.Y, initSnap.Value.Z, initSnap.Value.Map),
            StartTick = Environment.TickCount64,
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime
        };

        long baseTs = startMs;
        uint fakeItemSerial = 0x40000001;

        var snap = initSnap.Value;
        var equipTuples = DecodeEquipment(snap.Equipment, ref fakeItemSerial);
        var drawPkt = new PacketDrawObject(
            charUid, (ushort)snap.BodyId, snap.X, snap.Y, snap.Z,
            snap.Dir, (ushort)snap.Hue, snap.Flags, 0x01, equipTuples);
        session.Packets.Add(new RecordedPacket { TickOffset = 0, Data = drawPkt.Build().Span.ToArray() });

        var moves = GetMoves(charUid, startMs, endMs);
        ushort lastBody = (ushort)snap.BodyId;
        ushort lastHue = (ushort)snap.Hue;
        foreach (var mv in moves)
        {
            int offset = (int)(mv.Ts - baseTs);
            if (offset < 0) offset = 0;
            var movePkt = new PacketMobileMoving(
                charUid, lastBody, mv.X, mv.Y, mv.Z, mv.Dir,
                lastHue, 0, 0x01);
            session.Packets.Add(new RecordedPacket { TickOffset = offset, Data = movePkt.Build().Span.ToArray() });
        }

        var snapshots = GetSnapshots(charUid, startMs, endMs);
        string lastEquip = snap.Equipment;
        foreach (var s in snapshots)
        {
            lastBody = (ushort)s.BodyId;
            lastHue = (ushort)s.Hue;
            if (s.Equipment != lastEquip)
            {
                int offset = (int)(s.Ts - baseTs);
                if (offset < 0) offset = 0;
                var eqTuples = DecodeEquipment(s.Equipment, ref fakeItemSerial);
                var updPkt = new PacketDrawObject(
                    charUid, (ushort)s.BodyId, s.X, s.Y, s.Z,
                    s.Dir, (ushort)s.Hue, s.Flags, 0x01, eqTuples);
                session.Packets.Add(new RecordedPacket { TickOffset = offset, Data = updPkt.Build().Span.ToArray() });
                lastEquip = s.Equipment;
            }
        }

        session.Packets.Sort((a, b) => a.TickOffset.CompareTo(b.TickOffset));
        return session;
    }

    // ----------------------------------------------------------------
    //  Internal DB queries for replay builder
    // ----------------------------------------------------------------

    private SnapRow? GetClosestSnapshot(uint charUid, long ts)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            SELECT ts,x,y,z,map,dir,body_id,hue,name,flags,equipment
            FROM char_snapshots WHERE char_uid=@u AND ts<=@t ORDER BY ts DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@u", (long)charUid);
        cmd.Parameters.AddWithValue("@t", ts);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
        {
            cmd.CommandText = """
                SELECT ts,x,y,z,map,dir,body_id,hue,name,flags,equipment
                FROM char_snapshots WHERE char_uid=@u ORDER BY ts ASC LIMIT 1
                """;
            using var r2 = cmd.ExecuteReader();
            if (!r2.Read()) return null;
            return ReadSnapRow(r2);
        }
        return ReadSnapRow(r);
    }

    private static SnapRow ReadSnapRow(SqliteDataReader r) => new(
        r.GetInt64(0), (short)r.GetInt32(1), (short)r.GetInt32(2), (sbyte)r.GetInt32(3),
        (byte)r.GetInt32(4), (byte)r.GetInt32(5), r.GetInt32(6), r.GetInt32(7),
        r.GetString(8), (byte)r.GetInt32(9), r.GetString(10));

    private List<MoveRow> GetMoves(uint charUid, long startMs, long endMs)
    {
        var result = new List<MoveRow>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT ts,x,y,z,map,dir FROM char_moves WHERE char_uid=@u AND ts>=@s AND ts<=@e ORDER BY ts";
        cmd.Parameters.AddWithValue("@u", (long)charUid);
        cmd.Parameters.AddWithValue("@s", startMs);
        cmd.Parameters.AddWithValue("@e", endMs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new MoveRow(r.GetInt64(0), (short)r.GetInt32(1), (short)r.GetInt32(2),
                (sbyte)r.GetInt32(3), (byte)r.GetInt32(4), (byte)r.GetInt32(5)));
        return result;
    }

    private List<SnapRow> GetSnapshots(uint charUid, long startMs, long endMs)
    {
        var result = new List<SnapRow>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = """
            SELECT ts,x,y,z,map,dir,body_id,hue,name,flags,equipment
            FROM char_snapshots WHERE char_uid=@u AND ts>=@s AND ts<=@e ORDER BY ts
            """;
        cmd.Parameters.AddWithValue("@u", (long)charUid);
        cmd.Parameters.AddWithValue("@s", startMs);
        cmd.Parameters.AddWithValue("@e", endMs);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(ReadSnapRow(r));
        return result;
    }

    // ----------------------------------------------------------------
    //  Equipment encoding
    // ----------------------------------------------------------------

    private static string EncodeEquipment(Character ch)
    {
        var sb = new StringBuilder();
        for (int i = 1; i <= (int)Layer.Horse; i++)
        {
            var item = ch.GetEquippedItem((Layer)i);
            if (item == null) continue;
            if (sb.Length > 0) sb.Append('|');
            sb.Append(i).Append(':').Append(item.DispIdFull).Append(':').Append(item.Hue);
        }
        return sb.ToString();
    }

    private static (uint Serial, ushort ItemId, byte Layer, ushort Hue)[] DecodeEquipment(
        string equipStr, ref uint nextSerial)
    {
        if (string.IsNullOrEmpty(equipStr)) return [];

        var result = new List<(uint, ushort, byte, ushort)>();
        foreach (var part in equipStr.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split(':');
            if (fields.Length < 3) continue;
            if (!byte.TryParse(fields[0], out byte layer)) continue;
            if (!ushort.TryParse(fields[1], out ushort itemId)) continue;
            if (!ushort.TryParse(fields[2], out ushort hue)) continue;
            result.Add((nextSerial++, itemId, layer, hue));
        }
        return result.ToArray();
    }

    // ----------------------------------------------------------------
    //  Dispose
    // ----------------------------------------------------------------

    public void Dispose()
    {
        Flush();
        _insertMoveCmd?.Dispose();
        _insertSnapshotCmd?.Dispose();
        _db?.Dispose();
    }

    // ----------------------------------------------------------------
    //  Internal record types
    // ----------------------------------------------------------------

    private readonly record struct MoveRecord(uint CharUid, long Ts, short X, short Y, sbyte Z, byte Map, byte Dir);

    private readonly record struct SnapshotRecord(
        uint CharUid, long Ts, string HourKey,
        short X, short Y, sbyte Z, byte Map, byte Dir,
        ushort BodyId, ushort Hue, string Name, bool IsPlayer,
        byte Flags, short Hits, short MaxHits, short Mana, short MaxMana,
        short Stam, short MaxStam, string Equipment);

    private readonly record struct MoveRow(long Ts, short X, short Y, sbyte Z, byte Map, byte Dir);

    private readonly record struct SnapRow(
        long Ts, short X, short Y, sbyte Z, byte Map, byte Dir,
        int BodyId, int Hue, string Name, byte Flags, string Equipment);
}
