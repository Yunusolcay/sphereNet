using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Components;

/// <summary>
/// Champion spawn component — port of Source-X CCChampion. Rides the item's
/// SpawnComponent for actual NPC generation (Source-X CCSpawn::GenerateChar)
/// and drives the wave/candle/level state machine on top of it:
///   kills → white candles (4 whites = 1 red) → red candles → levels →
///   boss (CHAMPIONID) at LEVELMAX → @Complete.
/// State persists through item TAGs (CHAMPION_*) so the save format needs no
/// new record types; candles re-link by uid on load like Source-X
/// ADDRED/WHITECANDLE loaders.
/// </summary>
public sealed class ChampionComponent
{
    // Source-X CCChampion.cpp constants
    private const int CandlesNextRed = 4;      // white candles per red candle
    private const int DefaultSpawnsMax = 2400; // MAXSPAWN
    private const int DefaultLevelMax = 5;     // MAXLEVEL
    private const ushort SkullCandleId = 0x1853; // ITEMID_SKULL_CANDLE
    private const ushort RedCandleHue = 33;
    private const long DecayTimeoutMs = 600_000; // 10-minute decay tick

    /// <summary>Candle removal reasons (Source-X CANDLEDELREASON_TYPE).</summary>
    public const int CandleDelTimeout = 0;
    public const int CandleDelCommand = 1;
    public const int CandleDelClear = 2;

    /// <summary>Trigger bridge — reuses the spawn trigger delegate wired by the
    /// host (item trigger dispatch with O1 = candle / N1..N3 payload).</summary>
    public static Func<Item, ItemTrigger, SpawnTriggerArgs, TriggerResult>? OnChampionTrigger
        => SpawnComponent.OnSpawnTrigger;

    private readonly Item _item;
    private readonly GameWorld _world;
    private ResourceHolder? _resources;

    public bool Active { get; private set; }
    public int Level { get; private set; } = 1;
    public int LevelMax { get; set; } = DefaultLevelMax;
    public int SpawnsMax { get; set; } = DefaultSpawnsMax;
    public int SpawnsCur { get; set; }
    public int DeathCount { get; set; }
    public int SpawnsNextWhite { get; set; }
    public int SpawnsNextRed { get; set; }
    public int CandlesNextLevel { get; set; }
    public long LastActivationTime { get; set; }
    /// <summary>Boss chardef index (CHAMPIONID).</summary>
    public int ChampionId { get; set; }
    public Serial ChampionSummoned { get; set; } = Serial.Invalid;
    /// <summary>The [CHAMPION x] def this altar is linked to (MORE1).</summary>
    public string ChampionDefName { get; private set; } = "";
    public string ChampionName { get; private set; } = "";

    private readonly Dictionary<int, List<int>> _spawnGroups = [];
    private readonly List<Serial> _whiteCandles = [];
    private readonly List<Serial> _redCandles = [];
    private int[] _monstersList = [];
    private int[] _candleList = [];

    public IReadOnlyList<Serial> WhiteCandles => _whiteCandles;
    public IReadOnlyList<Serial> RedCandles => _redCandles;

    public ChampionComponent(Item item, GameWorld world)
    {
        _item = item;
        _world = world;
    }

    // ------------------------------------------------------------------
    // Def linkage / init (Source-X CCChampion::Init + CCChampionDef data)
    // ------------------------------------------------------------------

    /// <summary>Pull LEVELMAX/SPAWNSMAX/CHAMPIONID/NPCGROUP[n] from the
    /// [CHAMPION defname] resource section, then restore persisted state.</summary>
    public bool InitFromDef(ResourceHolder resources, string defName)
    {
        _resources = resources;
        defName = defName.Trim();
        if (defName.Length == 0)
            return false;

        var rid = resources.ResolveDefName(defName);
        if (!rid.IsValid || rid.Type != ResType.Champion)
            return false;
        var link = resources.GetResource(rid);
        if (link?.StoredKeys == null)
            return false;

        ChampionDefName = defName;
        LevelMax = DefaultLevelMax;
        SpawnsMax = DefaultSpawnsMax;
        _spawnGroups.Clear();

        foreach (var key in link.StoredKeys)
        {
            string k = key.Key.ToUpperInvariant();
            switch (k)
            {
                case "NAME":
                    ChampionName = key.Arg;
                    break;
                case "LEVELMAX":
                    if (int.TryParse(key.Arg, out int lm) && lm > 0) LevelMax = lm;
                    break;
                case "SPAWNSMAX":
                    if (int.TryParse(key.Arg, out int sm) && sm > 0) SpawnsMax = sm;
                    break;
                case "CHAMPIONID":
                {
                    var bossRid = resources.ResolveDefName(key.Arg.Trim());
                    if (bossRid.IsValid && bossRid.Type == ResType.CharDef)
                        ChampionId = bossRid.Index;
                    break;
                }
                default:
                    if (k.StartsWith("NPCGROUP", StringComparison.Ordinal))
                    {
                        // NPCGROUP[n] or NPCGROUPn — level index then a comma
                        // list of chardefs (invalid entries silently dropped,
                        // Source-X CCChampionDef::r_LoadVal).
                        string idxTok = k[8..].Trim('[', ']', '.');
                        if (!int.TryParse(idxTok, out int groupLevel))
                            break;
                        var members = new List<int>();
                        foreach (var name in key.Arg.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                        {
                            var mRid = resources.ResolveDefName(name);
                            if (mRid.IsValid && mRid.Type == ResType.CharDef)
                                members.Add(mRid.Index);
                        }
                        if (members.Count > 0)
                            _spawnGroups[groupLevel] = members;
                    }
                    break;
            }
        }

        InitializeLists();
        RestoreStateFromTags();
        return true;
    }

    /// <summary>Source-X CCChampion::InitializeLists — per-level monster
    /// percentage and red-candle requirement vectors.</summary>
    private void InitializeLists()
    {
        int levels = Math.Max(2, LevelMax);

        // _MonstersList: percentages of SpawnsMax per level (levels 1..max-1).
        var monsters = new List<int>();
        int perc = 100 / levels;
        int total = 0;
        for (int i = levels - 2; i >= 1; i--)
        {
            int m = perc / i + (levels - (i + 1));
            monsters.Insert(0, m);
            total += m;
        }
        monsters.Insert(0, 100 - total);
        _monstersList = [.. monsters];

        // _CandleList: red candles needed per level (16 total). Source-X
        // (CCChampion::InitializeLists) inserts each computed value at the FRONT
        // so the vector reads leftover, v(i=2), v(i=3)... — appending instead
        // reverses the tail and mis-assigns the per-level requirement.
        var candles = new List<int>();
        int candleTotal = 0;
        for (int i = levels - 1; i >= 2; i--)
        {
            int c = (16 - candleTotal) / i;
            candles.Insert(0, c);
            candleTotal += c;
        }
        candles.Insert(0, 16 - candleTotal);
        _candleList = [.. candles];
    }

    private int GetMonstersCount()
    {
        // The empty list has to be answered BEFORE the index is clamped: with a length
        // of zero the upper bound became -1 and Math.Clamp threw, so starting a
        // champion whose definition had not been linked left it half-armed - Active
        // already true, nothing running (Source-X answers a safe value instead,
        // CCChampion.cpp:640).
        if (_monstersList.Length == 0)
            return SpawnsMax;
        int idx = Math.Clamp(Level - 1, 0, _monstersList.Length - 1);
        return _monstersList[idx] * SpawnsMax / 100;
    }

    private int GetCandlesCount()
    {
        if (_candleList.Length == 0) return 16;
        int idx = Level - 1;
        return idx >= 0 && idx < _candleList.Length ? _candleList[idx] : 16;
    }

    // ------------------------------------------------------------------
    // Lifecycle (Start / Stop / Complete / SetLevel)
    // ------------------------------------------------------------------

    public void Start(Character? src = null)
    {
        if (Active)
            return;

        LastActivationTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Active = true;
        SpawnsNextRed = GetCandlesCount();
        SetLevel(1);

        // Source-X fires @Start AFTER the state is armed; RET_TRUE only
        // aborts the initial spawn burst.
        if (FireTrigger(ItemTrigger.Start, new SpawnTriggerArgs { SpawnedChar = src }) == TriggerResult.True)
        {
            SaveStateToTags();
            return;
        }

        // Source-X quirk kept verbatim: the loop counter races the quota that
        // SpawnNPC decrements, so the initial burst is ceil(quota/2).
        for (int i = 0; i < SpawnsNextWhite; i++)
            SpawnNpc();
        SaveStateToTags();
    }

    public void Stop(Character? src = null)
    {
        if (FireTrigger(ItemTrigger.Stop, new SpawnTriggerArgs { SpawnedChar = src }) == TriggerResult.True)
            return;

        KillChildren();
        ClearData();
        _item.SetTimeout(0);
        ClearWhiteCandles();
        ClearRedCandles();
        SaveStateToTags();
    }

    private void ClearData()
    {
        Active = false;
        Level = 1;
        SpawnsCur = 0;
        DeathCount = 0;
        SpawnsNextWhite = 0;
        SpawnsNextRed = 0;
        CandlesNextLevel = 0;
        ChampionSummoned = Serial.Invalid;
    }

    /// <summary>Boss died — stop and fire @Complete (rewards are script-side;
    /// Source-X leaves them TODO as well).</summary>
    public void Complete()
    {
        if (Active)
            Stop();
        FireTrigger(ItemTrigger.Complete, new SpawnTriggerArgs());
        SaveStateToTags();
    }

    public void SetLevel(int level)
    {
        Level = Math.Max(1, level);
        int levelMonsters = GetMonstersCount();
        CandlesNextLevel += GetCandlesCount();

        var args = new SpawnTriggerArgs
        {
            N1 = Level,
            N2 = levelMonsters,
            N3 = CandlesNextLevel
        };
        FireTrigger(ItemTrigger.Level, args);

        if (Level >= LevelMax)
        {
            // Final level: field cleared, boss comes out.
            KillChildren();
            ClearWhiteCandles();
            ClearRedCandles();
            SpawnNpc();
            SaveStateToTags();
            return;
        }

        int redMonsters = CandlesNextLevel > 0 ? levelMonsters / CandlesNextLevel : levelMonsters;
        SpawnsNextRed = redMonsters;
        SpawnsNextWhite = redMonsters / (CandlesNextRed + 1);
        _item.SetTimeout(Environment.TickCount64 + DecayTimeoutMs);
        SaveStateToTags();
    }

    // ------------------------------------------------------------------
    // Spawning & kill accounting
    // ------------------------------------------------------------------

    /// <summary>Source-X CCChampion::SpawnNPC — one wave member (or the boss
    /// at LEVELMAX) through the item's spawn component.</summary>
    public void SpawnNpc()
    {
        var spawn = _item.SpawnChar;
        if (spawn == null)
            return;

        int defIndex;
        if (Level >= LevelMax)
        {
            if (ChampionSummoned.IsValid)
                return; // boss already out
            if (ChampionId == 0)
                return;
            // The shared counter update below applies to the boss too
            // (CCChampion.cpp:337), so these are set one short on purpose.
            SpawnsNextWhite = 1;
            SpawnsCur = SpawnsMax - 1;
            defIndex = ChampionId;
        }
        else
        {
            if (SpawnsNextWhite <= 0)
                return;
            if (SpawnsCur >= SpawnsMax)
            {
                SpawnsNextWhite = 0;
                return;
            }
            if (!_spawnGroups.TryGetValue(Level, out var group) || group.Count == 0)
                return;
            defIndex = group[Random.Shared.Next(group.Count)];
        }

        var npc = spawn.SpawnSpecific(defIndex);
        if (npc == null)
            return;

        if (Level >= LevelMax)
            ChampionSummoned = npc.Uid;
        // Source-X updates the counters after ANY successful spawn, the boss included
        // (CCChampion.cpp:337) - they sat inside the ordinary branch, so a script or a
        // status window reading SPAWNSCUR / KILLSNEXTWHITE was left one short for good
        // once the boss came out.
        SpawnsCur++;
        SpawnsNextWhite--;
        SaveStateToTags();
    }

    /// <summary>Death-path entry: a spawned wave member (or the boss) died.
    /// Source-X credits at object destroy (CObjBase dtor → DelObj); we credit
    /// at death via the SPAWNITEM back-link.</summary>
    public void OnMemberDeath(Character victim)
    {
        if (!Active)
            return;
        _item.SpawnChar?.DelObj(victim.Uid);
        OnKill(victim.Uid);
    }

    /// <summary>Source-X CCChampion::OnKill.</summary>
    public void OnKill(Serial uid)
    {
        if (uid == ChampionSummoned && uid.IsValid)
        {
            Complete();
            return;
        }

        if (SpawnsNextWhite == 0)
            AddWhiteCandle();

        DeathCount++;
        // Force the boss when the field's total kill budget is exhausted.
        if (DeathCount >= SpawnsMax && !ChampionSummoned.IsValid)
            SetLevel(LevelMax);

        SpawnNpc();
        SaveStateToTags();
    }

    // ------------------------------------------------------------------
    // Candles
    // ------------------------------------------------------------------

    // White ring: 1 tile out — SW, SE, NW, NE by index.
    private static readonly (int X, int Y)[] WhiteOffsets =
        [(-1, 1), (1, 1), (-1, -1), (1, -1)];

    // Red ring: 2 tiles out, 16 compass positions clockwise from NW.
    private static readonly (int X, int Y)[] RedOffsets =
    [
        (-2, -2), (-1, -2), (0, -2), (1, -2), (2, -2),
        (2, -1), (2, 0), (2, 1), (2, 2),
        (1, 2), (0, 2), (-1, 2), (-2, 2),
        (-2, 1), (-2, 0), (-2, -1)
    ];

    public void AddWhiteCandle(Serial existing = default)
    {
        // Re-linking a candle read back from a save is a separate path and is not
        // subject to the level gate (Source-X takes the uid branch first,
        // CCChampion.cpp:437).
        Item? candle = existing.IsValid ? _world.FindItem(existing) : null;
        if (candle != null)
        {
            _whiteCandles.Add(candle.Uid);
            SpawnsNextWhite = SpawnsNextRed / (CandlesNextRed + 1);
            SaveStateToTags();
            return;
        }

        // The boss is out; the progress ring is finished and takes no more candles
        // (:346).
        if (Level >= LevelMax)
            return;

        if (_whiteCandles.Count >= CandlesNextRed)
        {
            AddRedCandle();
            return;
        }

        candle = CreateCandle(WhiteOffsets[Math.Min(_whiteCandles.Count, WhiteOffsets.Length - 1)], red: false);
        if (candle == null)
            return;
        if (FireTrigger(ItemTrigger.AddWhiteCandle, new SpawnTriggerArgs { SpawnedItem = candle }) == TriggerResult.True)
        {
            _world.DeleteObject(candle);
            candle.Delete();
            return;
        }
        // The candle's own ITEMDEF @Create, which upstream runs through
        // GenerateScript right after placing it (:411). @AddWhiteCandle on the
        // champion is a different hook and does not stand in for it.
        candle.FireCreateTrigger();

        _whiteCandles.Add(candle.Uid);
        // Next white candle needs another kill quota (Source-X recomputes).
        SpawnsNextWhite = SpawnsNextRed / (CandlesNextRed + 1);
        SaveStateToTags();
    }

    public void AddRedCandle(Serial existing = default)
    {
        // A candle read back from a save is re-linked and nothing else (:433).
        // The uid has to RESOLVE, not merely be non-sentinel: Serial.IsValid only
        // rules out the clear value, so an omitted argument reports valid.
        if (existing.IsValid && _world.FindItem(existing) is { } restored)
        {
            _redCandles.Add(restored.Uid);
            SaveStateToTags();
            return;
        }

        // The level threshold is measured against the candles ALREADY standing, before
        // this one joins them (:441). Counting the new candle first advanced the level
        // one candle early, so the wave change and @Level fired ahead of the
        // reference's own progression.
        if (Active && _redCandles.Count >= CandlesNextLevel && Level < LevelMax)
            SetLevel(Level + 1);

        // Once the boss is out the ring is finished (:443).
        if (Level >= LevelMax)
            return;

        var candle = CreateCandle(RedOffsets[Math.Min(_redCandles.Count, RedOffsets.Length - 1)], red: true);
        if (candle == null)
        {
            // Upstream forces the boss out rather than stalling the event when the
            // candle cannot be made (:551).
            SetLevel(LevelMax);
            return;
        }
        if (FireTrigger(ItemTrigger.AddRedCandle, new SpawnTriggerArgs { SpawnedItem = candle }) == TriggerResult.True)
        {
            _world.DeleteObject(candle);
            candle.Delete();
            return;
        }
        candle.FireCreateTrigger();
        // A fresh red candle consumes the white ring (OSI progression).
        ClearWhiteCandles();

        _redCandles.Add(candle.Uid);
        SaveStateToTags();
    }

    public void DelWhiteCandle(int reason)
    {
        if (_whiteCandles.Count == 0)
            return;
        var uid = _whiteCandles[^1];
        var candle = _world.FindItem(uid);
        if (FireTrigger(ItemTrigger.DelWhiteCandle,
                new SpawnTriggerArgs { SpawnedItem = candle, N1 = reason }) == TriggerResult.True)
            return; // script keeps the candle
        _whiteCandles.RemoveAt(_whiteCandles.Count - 1);
        RemoveCandleItem(candle);
        SaveStateToTags();
    }

    public void DelRedCandle(int reason)
    {
        if (_redCandles.Count == 0)
            return;
        var uid = _redCandles[^1];
        var candle = _world.FindItem(uid);
        if (FireTrigger(ItemTrigger.DelRedCandle,
                new SpawnTriggerArgs { SpawnedItem = candle, N1 = reason }) == TriggerResult.True)
            return;
        _redCandles.RemoveAt(_redCandles.Count - 1);
        RemoveCandleItem(candle);
        SaveStateToTags();
    }

    public void ClearWhiteCandles()
    {
        while (_whiteCandles.Count > 0)
        {
            int before = _whiteCandles.Count;
            DelWhiteCandle(CandleDelClear);
            if (_whiteCandles.Count == before)
                break; // a script vetoed the removal — avoid spinning
        }
    }

    public void ClearRedCandles()
    {
        while (_redCandles.Count > 0)
        {
            int before = _redCandles.Count;
            DelRedCandle(CandleDelClear);
            if (_redCandles.Count == before)
                break;
        }
    }

    /// <summary>Source-X raises a placed candle by this much (:409/:529).</summary>
    private const int CandleZOffset = 4;

    private Item? CreateCandle((int X, int Y) offset, bool red)
    {
        var item = _world.CreateItem();
        item.BaseId = SkullCandleId;
        if (red)
            item.Hue = new Color(RedCandleHue);
        item.Attributes |= ObjAttributes.Move_Never;
        item.Link = _item.Uid;
        var p = _item.Position;
        // Source-X lifts a candle four above whatever it was placed on
        // (SetTopZ(GetTopZ() + 4), :409/:529), so it stands on the altar platform
        // rather than sinking into it.
        _world.PlaceItem(item, new Point3D(
            (short)(p.X + offset.X), (short)(p.Y + offset.Y),
            (sbyte)Math.Clamp(p.Z + CandleZOffset, sbyte.MinValue, sbyte.MaxValue), p.Map));
        return item;
    }

    /// <summary>The altar is going away: take its candles with it. Source-X does this
    /// in ~CCChampion (CCChampion.cpp:92), so it happens however the multi dies -
    /// .nuke, a script REMOVE or a normal stop - and not only through STOP.</summary>
    public void OnAltarDeleted()
    {
        foreach (var uid in _redCandles.Concat(_whiteCandles).ToList())
            RemoveCandleItem(_world.FindItem(uid));
        _redCandles.Clear();
        _whiteCandles.Clear();
    }

    private void RemoveCandleItem(Item? candle)
    {
        if (candle == null || candle.IsDeleted)
            return;
        _world.DeleteObject(candle);
        candle.Delete();
    }

    // ------------------------------------------------------------------
    // Decay tick (Source-X OnTickComponent: 10-minute red-candle decay)
    // ------------------------------------------------------------------

    public void OnTick(long now)
    {
        if (!Active)
            return;
        if (_redCandles.Count > 0)
        {
            SpawnsCur = Math.Max(0, SpawnsCur - SpawnsNextRed);
            DeathCount = Math.Max(0, DeathCount - SpawnsNextRed);
            DelRedCandle(CandleDelTimeout);
            _item.SetTimeout(now + DecayTimeoutMs);
        }
        else
        {
            Stop();
        }
    }

    public void KillChildren() => _item.SpawnChar?.KillAll();

    private TriggerResult FireTrigger(ItemTrigger trigger, SpawnTriggerArgs args) =>
        OnChampionTrigger?.Invoke(_item, trigger, args) ?? TriggerResult.Default;

    // ------------------------------------------------------------------
    // Persistence — item TAGs (CHAMPION_*), restored by InitFromDef.
    // ------------------------------------------------------------------

    private bool _restoring;

    private void SaveStateToTags()
    {
        if (_restoring)
            return;
        // LEVELMAX and SPAWNSMAX ride along at the end: a live change to either used
        // to live only in memory, and InitFromDef put the definition's values back on
        // the next load (Source-X writes both in r_Write, CCChampion.cpp:794/796).
        // Appended, so a state line written before this still parses.
        _item.SetTag("CHAMPION_STATE",
            $"{(Active ? 1 : 0)}|{Level}|{SpawnsCur}|{DeathCount}|{SpawnsNextWhite}|{SpawnsNextRed}|{CandlesNextLevel}|{LastActivationTime}|0{ChampionSummoned.Value:x8}|{LevelMax}|{SpawnsMax}");
        _item.SetTag("CHAMPION_REDCANDLES",
            string.Join(',', _redCandles.Select(c => $"0{c.Value:x8}")));
        _item.SetTag("CHAMPION_WHITECANDLES",
            string.Join(',', _whiteCandles.Select(c => $"0{c.Value:x8}")));
    }

    private void RestoreStateFromTags()
    {
        if (!_item.TryGetTag("CHAMPION_STATE", out string? state) || string.IsNullOrEmpty(state))
            return;
        _restoring = true;
        try
        {
            var f = state.Split('|');
            if (f.Length >= 9)
            {
                Active = f[0] == "1";
                if (int.TryParse(f[1], out int lv)) Level = Math.Max(1, lv);
                if (int.TryParse(f[2], out int sc)) SpawnsCur = sc;
                if (int.TryParse(f[3], out int dc)) DeathCount = dc;
                if (int.TryParse(f[4], out int nw)) SpawnsNextWhite = nw;
                if (int.TryParse(f[5], out int nr)) SpawnsNextRed = nr;
                if (int.TryParse(f[6], out int cn)) CandlesNextLevel = cn;
                if (long.TryParse(f[7], out long la)) LastActivationTime = la;
                ChampionSummoned = ParseSerial(f[8]);
                if (f.Length >= 11)
                {
                    if (int.TryParse(f[9], out int lmax) && lmax > 0) LevelMax = lmax;
                    if (int.TryParse(f[10], out int smax) && smax > 0) SpawnsMax = smax;
                    InitializeLists();
                }
            }

            RestoreCandleList("CHAMPION_REDCANDLES", _redCandles);
            RestoreCandleList("CHAMPION_WHITECANDLES", _whiteCandles);
        }
        finally
        {
            _restoring = false;
        }
    }

    private void RestoreCandleList(string tag, List<Serial> target)
    {
        target.Clear();
        if (!_item.TryGetTag(tag, out string? csv) || string.IsNullOrEmpty(csv))
            return;
        foreach (var tok in csv.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var uid = ParseSerial(tok);
            if (uid.IsValid && _world.FindItem(uid) != null)
                target.Add(uid);
        }
    }

    private static Serial ParseSerial(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        else if (s.StartsWith('0') && s.Length > 1) s = s[1..];
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint v)
            ? new Serial(v)
            : Serial.Invalid;
    }

    // ------------------------------------------------------------------
    // Script surface (Source-X ICHMPL_* keys / ICHMPV_* verbs)
    // ------------------------------------------------------------------

    public bool TryGetProperty(string key, out string value)
    {
        // NPCGROUP<n>[.<i>] reads the i-th chardef of level n's spawn group
        // (Source-X ICHMPL_NPCGROUP r_WriteVal); -1 when out of range.
        if (key.StartsWith("NPCGROUP", StringComparison.OrdinalIgnoreCase))
        {
            string rest = key[8..].Trim('[', ']', ' ');
            var parts = rest.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !int.TryParse(parts[0], out int grp) ||
                !_spawnGroups.TryGetValue(grp, out var group))
            {
                value = "-1";
                return true;
            }
            int npc = 0;
            if (parts.Length > 1) int.TryParse(parts[1], out npc);
            if (npc < 0 || npc >= group.Count)
            {
                value = "-1";
                return true;
            }
            // Reverse-resolve the stored chardef index to its defname via the
            // resource registry (Source-X g_Cfg.ResourceGetName).
            value = _resources?.GetResource(ResType.CharDef, group[npc])?.DefName
                ?? Definitions.DefinitionLoader.GetCharDef(group[npc])?.DefName
                ?? group[npc].ToString();
            return true;
        }

        switch (key.ToUpperInvariant())
        {
            case "ACTIVE": value = Active ? "1" : "0"; return true;
            case "LEVEL": value = Level.ToString(); return true;
            case "LEVELMAX": value = LevelMax.ToString(); return true;
            case "SPAWNSCUR": value = SpawnsCur.ToString(); return true;
            case "SPAWNSMAX": value = SpawnsMax.ToString(); return true;
            case "DEATHCOUNT": value = DeathCount.ToString(); return true;
            case "KILLSNEXTWHITE": value = SpawnsNextWhite.ToString(); return true;
            case "KILLSNEXTRED": value = SpawnsNextRed.ToString(); return true;
            case "CANDLESNEXTLEVEL": value = CandlesNextLevel.ToString(); return true;
            case "REDCANDLES": value = _redCandles.Count.ToString(); return true;
            case "WHITECANDLES": value = _whiteCandles.Count.ToString(); return true;
            case "LASTACTIVATIONTIME": value = LastActivationTime.ToString(); return true;
            case "CHAMPIONSUMMONED": value = $"0{ChampionSummoned.Value:x8}"; return true;
            case "CHAMPIONSPAWN": value = ChampionDefName; return true;
            case "CHAMPIONID":
            {
                var def = Definitions.DefinitionLoader.GetCharDef(ChampionId);
                value = def?.DefName ?? ChampionId.ToString();
                return true;
            }
            default:
                value = "";
                return false;
        }
    }

    public bool TrySetProperty(string key, string value)
    {
        // NPCGROUP<n>=def1,def2,... overrides the spawn group for level n
        // (Source-X ICHMPL_NPCGROUP r_LoadVal). Invalid chardefs are dropped;
        // an empty list clears the override.
        if (key.StartsWith("NPCGROUP", StringComparison.OrdinalIgnoreCase))
        {
            string idxTok = key[8..].Trim('[', ']', '.', ' ');
            if (!int.TryParse(idxTok, out int groupLevel))
                return true;
            var members = new List<int>();
            foreach (var name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var mRid = _resources?.ResolveDefName(name) ?? ResourceId.Invalid;
                if (mRid.IsValid && mRid.Type == ResType.CharDef)
                    members.Add(mRid.Index);
            }
            if (members.Count > 0) _spawnGroups[groupLevel] = members;
            else _spawnGroups.Remove(groupLevel);
            return true;
        }

        switch (key.ToUpperInvariant())
        {
            // Re-link (or create) a candle by uid — Source-X ADDREDCANDLE /
            // ADDWHITECANDLE load-keys used to restore candle placement.
            case "ADDREDCANDLE":
                AddRedCandle(ParseSerial(value));
                return true;
            case "ADDWHITECANDLE":
                AddWhiteCandle(ParseSerial(value));
                return true;
            case "LEVEL":
                if (int.TryParse(value, out int lv)) SetLevel(lv);
                return true;
            // Every live write is persisted on the spot. The state snapshot used to be
            // taken only on a candle or a kill, so a staff change to the running event
            // was lost unless something else happened to save afterwards.
            case "LEVELMAX":
                if (int.TryParse(value, out int lm) && lm > 0)
                { LevelMax = lm; InitializeLists(); SaveStateToTags(); }
                return true;
            case "SPAWNSMAX":
                if (int.TryParse(value, out int sm) && sm > 0) { SpawnsMax = sm; SaveStateToTags(); }
                return true;
            case "DEATHCOUNT":
                int.TryParse(value, out int dc); DeathCount = dc; SaveStateToTags(); return true;
            case "KILLSNEXTWHITE":
                int.TryParse(value, out int nw); SpawnsNextWhite = nw; SaveStateToTags(); return true;
            case "KILLSNEXTRED":
                int.TryParse(value, out int nr); SpawnsNextRed = nr; SaveStateToTags(); return true;
            case "CANDLESNEXTLEVEL":
                int.TryParse(value, out int cn); CandlesNextLevel = cn; SaveStateToTags(); return true;
            case "CHAMPIONID":
            {
                var rid = _resources?.ResolveDefName(value.Trim()) ?? ResourceId.Invalid;
                if (rid.IsValid && rid.Type == ResType.CharDef) ChampionId = rid.Index;
                return true;
            }
            case "CHAMPIONSPAWN":
                if (_resources != null)
                    InitFromDef(_resources, value);
                return true;
            default:
                return false;
        }
    }

    public bool TryExecuteVerb(string verb, string args, Character? src)
    {
        switch (verb.ToUpperInvariant())
        {
            case "START": Start(src); return true;
            case "STOP": Stop(src); return true;
            case "INIT":
                if (_resources != null && ChampionDefName.Length > 0)
                    InitFromDef(_resources, ChampionDefName);
                return true;
            case "ADDSPAWN": SpawnNpc(); return true;
            // Source-X MULTICREATE is a stub (ECS multi-link FIXME) — accept the
            // verb so scripts don't error, but there is nothing to build yet.
            case "MULTICREATE": return true;
            case "DELREDCANDLE": DelRedCandle(CandleDelCommand); return true;
            case "DELWHITECANDLE": DelWhiteCandle(CandleDelCommand); return true;
            case "ADDOBJ":
            case "DELOBJ":
            {
                var uid = ParseSerial(args);
                if (!uid.IsValid) return true;
                if (verb.Equals("ADDOBJ", StringComparison.OrdinalIgnoreCase))
                    _item.SpawnChar?.RegisterExisting(uid);
                else
                {
                    _item.SpawnChar?.DelObj(uid);
                    OnKill(uid); // Source-X DELOBJ counts as a kill
                }
                return true;
            }
            default:
                return false;
        }
    }
}
