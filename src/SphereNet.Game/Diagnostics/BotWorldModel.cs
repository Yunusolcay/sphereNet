namespace SphereNet.Game.Diagnostics;

public sealed class BotWorldModel
{
    public uint CharUid;
    public short X, Y;
    public sbyte Z;
    public byte Direction;
    public ushort Body;

    public short Hits, MaxHits;
    public short Mana, MaxMana;
    public short Stam, MaxStam;

    public bool IsDead;
    public bool IsWarMode;
    public bool IsMounted;

    public readonly Dictionary<uint, KnownMobile> Mobiles = new();

    public bool HasPendingTarget;
    public uint TargetCursorId;

    public int MoveRejectCount;

    public int DistanceTo(short tx, short ty)
    {
        int dx = Math.Abs(X - tx);
        int dy = Math.Abs(Y - ty);
        return Math.Max(dx, dy);
    }

    public KnownMobile? FindNearest(Func<KnownMobile, bool> predicate, int maxRange = 18)
    {
        KnownMobile? best = null;
        int bestDist = int.MaxValue;
        foreach (var m in Mobiles.Values)
        {
            if (!predicate(m)) continue;
            int d = DistanceTo(m.X, m.Y);
            if (d < bestDist && d <= maxRange) { bestDist = d; best = m; }
        }
        return best;
    }

    public void RemoveStale(long nowMs, long maxAgeMs = 60_000)
    {
        var remove = new List<uint>();
        foreach (var kv in Mobiles)
            if (nowMs - kv.Value.LastSeenMs > maxAgeMs) remove.Add(kv.Key);
        foreach (var id in remove) Mobiles.Remove(id);
    }

    public byte GetDirectionTo(short tx, short ty)
    {
        int dx = tx - X;
        int dy = ty - Y;
        if (dx == 0 && dy == 0) return Direction;

        int adx = Math.Abs(dx);
        int ady = Math.Abs(dy);

        if (ady == 0) return dx > 0 ? (byte)2 : (byte)6;
        if (adx == 0) return dy > 0 ? (byte)4 : (byte)0;
        if (adx > ady * 2) return dx > 0 ? (byte)2 : (byte)6;
        if (ady > adx * 2) return dy > 0 ? (byte)4 : (byte)0;

        if (dx > 0) return dy > 0 ? (byte)3 : (byte)1;
        return dy > 0 ? (byte)5 : (byte)7;
    }
}

public sealed class KnownMobile
{
    public uint Serial;
    public ushort Body;
    public short X, Y;
    public sbyte Z;
    public byte Notoriety;
    public long LastSeenMs;

    private static readonly HashSet<ushort> MountBodies =
        [0x00C8, 0x00E2, 0x00CC, 0x00E4, 0x00D2, 0x00DA, 0x00DC];

    public bool IsHumanBody => Body is 0x0190 or 0x0191;
    public bool IsMountBody => MountBodies.Contains(Body);
    public bool IsMonster => Notoriety is 5 or 6;
    public bool IsLikelyHealer => IsHumanBody && Notoriety == 7;
}
