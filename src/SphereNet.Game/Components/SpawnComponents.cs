using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Game.Components;

/// <summary>
/// Spawn point component for IT_SPAWN_CHAR items.
/// Maps to CItemSpawn in Source-X. Periodically creates NPCs within range.
/// Supports both single chardef (MORE1 = body ID) and spawn groups (MORE1 → SPAWN defname).
/// </summary>
public sealed class SpawnComponent
{
    private readonly Item _spawnItem;
    private readonly GameWorld _world;
    private readonly List<Serial> _spawnedUids = [];

    private ushort _charDefId;
    private SpawnGroupDef? _spawnGroup;
    private int _maxCount = 1;
    private int _spawnRange = 5;
    private int _minDelaySec = 300;
    private int _maxDelaySec = 600;
    private long _nextSpawnTick;
    private readonly Random _rand = new();
    private ResourceHolder? _resources;

    public int CurrentCount => _spawnedUids.Count;
    public int MaxCount { get => _maxCount; set => _maxCount = value; }
    public ushort CharDefId { get => _charDefId; set => _charDefId = value; }
    public int SpawnRange { get => _spawnRange; set => _spawnRange = value; }
    public SpawnGroupDef? SpawnGroup { get => _spawnGroup; set => _spawnGroup = value; }

    public SpawnComponent(Item spawnItem, GameWorld world)
    {
        _spawnItem = spawnItem;
        _world = world;
        SetNextSpawnTime();
    }

    /// <summary>Called each tick from the item's OnTick.</summary>
    public void OnTick(long currentTick)
    {
        CleanupDead();

        if (currentTick < _nextSpawnTick) return;
        if (_spawnedUids.Count >= _maxCount) return;
        if (_charDefId == 0 && _spawnGroup == null) return;

        SpawnOne();
        SetNextSpawnTime();
    }

    private void SpawnOne()
    {
        ushort bodyId = _charDefId;
        int defIndex = bodyId;

        // If we have a spawn group, pick a random member from it
        if (_spawnGroup != null)
        {
            string? memberName = _spawnGroup.SelectRandomMember(_rand);
            if (string.IsNullOrEmpty(memberName))
                return;

            // Resolve member defname to a chardef index
            if (_resources != null)
            {
                var rid = _resources.ResolveDefName(memberName);
                if (rid.IsValid && rid.Type == ResType.CharDef)
                {
                    defIndex = rid.Index;
                    bodyId = (ushort)Math.Clamp(defIndex, 0, ushort.MaxValue);
                }
                else
                    return;
            }
            else
                return;
        }

        var ch = _world.CreateCharacter();
        ch.BaseId = bodyId;
        ch.BodyId = bodyId;
        ch.IsPlayer = false;

        var charDef = DefinitionLoader.GetCharDef(defIndex);
        if (charDef != null)
        {
            if (charDef.DispIndex > 0)
            {
                ch.BodyId = charDef.DispIndex;
                ch.BaseId = charDef.DispIndex;
            }
            if (!string.IsNullOrWhiteSpace(charDef.Name))
                ch.Name = DefinitionLoader.ResolveNames(charDef.Name);
            else
                ch.Name = $"Spawn_{bodyId:X}";

            int strVal = charDef.StrMax > 0 ? charDef.StrMax : Math.Max(1, charDef.StrMin);
            int dexVal = charDef.DexMax > 0 ? charDef.DexMax : Math.Max(1, charDef.DexMin);
            int intVal = charDef.IntMax > 0 ? charDef.IntMax : Math.Max(1, charDef.IntMin);

            ch.Str = (short)Math.Clamp(strVal, 1, short.MaxValue);
            ch.Dex = (short)Math.Clamp(dexVal, 1, short.MaxValue);
            ch.Int = (short)Math.Clamp(intVal, 1, short.MaxValue);

            int hits = charDef.HitsMax > 0 ? charDef.HitsMax : Math.Max(1, strVal);
            ch.MaxHits = (short)Math.Clamp(hits, 1, short.MaxValue);
            ch.Hits = ch.MaxHits;
            ch.MaxMana = ch.Int;
            ch.Mana = ch.Int;
            ch.MaxStam = ch.Dex;
            ch.Stam = ch.Dex;

            if (charDef.NpcBrain != NpcBrainType.None)
                ch.NpcBrain = charDef.NpcBrain;
        }
        else
        {
            ch.Name = $"Spawn_{bodyId:X}";
            ch.Str = 50; ch.Dex = 50; ch.Int = 20;
            ch.MaxHits = 50; ch.MaxMana = 20; ch.MaxStam = 50;
            ch.Hits = 50; ch.Mana = 20; ch.Stam = 50;
        }

        if (ch.NpcBrain == NpcBrainType.None)
            ch.NpcBrain = NpcBrainType.Monster;

        ch.SetStatFlag(StatFlag.Spawned);

        // Random position within range
        short dx = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
        short dy = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
        var pos = new Point3D(
            (short)(_spawnItem.X + dx),
            (short)(_spawnItem.Y + dy),
            _spawnItem.Z,
            _spawnItem.MapIndex
        );

        _world.PlaceCharacter(ch, pos);
        _spawnedUids.Add(ch.Uid);
    }

    private void CleanupDead()
    {
        _spawnedUids.RemoveAll(uid =>
        {
            var ch = _world.FindChar(uid);
            return ch == null || ch.IsDeleted || ch.IsDead;
        });
    }

    private void SetNextSpawnTime()
    {
        int delaySec = _rand.Next(_minDelaySec, _maxDelaySec + 1);
        _nextSpawnTick = Environment.TickCount64 + delaySec * 1000;
    }

    /// <summary>Kill all spawned creatures (for despawn/delete).</summary>
    public void KillAll()
    {
        foreach (var uid in _spawnedUids)
        {
            var ch = _world.FindChar(uid);
            if (ch != null && !ch.IsDead)
                ch.Kill();
        }
        _spawnedUids.Clear();
    }

    /// <summary>
    /// Resolve MORE1 value as either a spawn group defname or a single chardef ID.
    /// Called during item initialization/load.
    /// </summary>
    public void SetFromMore1(uint more1, ResourceHolder resources)
    {
        _resources = resources;

        // Try to find a spawn group matching this more1 value
        // First, try direct resource lookup by iterating spawn groups
        foreach (var res in resources.GetAllResources())
        {
            if (res.Id.Type == ResType.Spawn && res is SpawnGroupDef sgd)
            {
                // Check if the more1 matches the hash of this spawn group's defname
                if (!string.IsNullOrEmpty(sgd.DefName))
                {
                    var spawnRid = resources.ResolveDefName(sgd.DefName);
                    if (spawnRid.IsValid && (uint)spawnRid.Index == more1)
                    {
                        _spawnGroup = sgd;
                        return;
                    }
                }
            }
        }

        // Not a spawn group — treat as direct chardef ID
        _charDefId = (ushort)(more1 & 0xFFFF);
    }

    /// <summary>
    /// Get the spawn definition name (group defname or chardef hex).
    /// </summary>
    public string GetSpawnDefName()
    {
        if (_spawnGroup != null && !string.IsNullOrEmpty(_spawnGroup.DefName))
            return _spawnGroup.DefName;
        return _charDefId > 0 ? $"0{_charDefId:X}" : "";
    }

    /// <summary>Force an immediate spawn tick (for SPAWNRESET).</summary>
    public void ForceSpawn()
    {
        _nextSpawnTick = 0;
    }
}

/// <summary>
/// Item spawn component for IT_SPAWN_ITEM items.
/// Periodically creates items within range.
/// </summary>
public sealed class ItemSpawnComponent
{
    private readonly Item _spawnItem;
    private readonly GameWorld _world;
    private readonly List<Serial> _spawnedUids = [];
    private readonly Random _rand = new();

    private ushort _itemDefId;
    private int _maxCount = 1;
    private int _spawnRange = 2;
    private long _nextSpawnTick;

    public ushort ItemDefId { get => _itemDefId; set => _itemDefId = value; }
    public int MaxCount { get => _maxCount; set => _maxCount = value; }

    public ItemSpawnComponent(Item spawnItem, GameWorld world)
    {
        _spawnItem = spawnItem;
        _world = world;
    }

    public void OnTick(long currentTick)
    {
        CleanupDeleted();

        if (currentTick < _nextSpawnTick) return;
        if (_spawnedUids.Count >= _maxCount) return;
        if (_itemDefId == 0) return;

        var item = _world.CreateItem();
        item.BaseId = _itemDefId;
        item.Name = $"Spawned_{_itemDefId:X}";

        short dx = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
        short dy = (short)_rand.Next(-_spawnRange, _spawnRange + 1);
        var pos = new Point3D(
            (short)(_spawnItem.X + dx),
            (short)(_spawnItem.Y + dy),
            _spawnItem.Z,
            _spawnItem.MapIndex
        );

        _world.PlaceItem(item, pos);
        _spawnedUids.Add(item.Uid);

        _nextSpawnTick = Environment.TickCount64 + _rand.Next(60, 300) * 1000;
    }

    private void CleanupDeleted()
    {
        _spawnedUids.RemoveAll(uid =>
        {
            var item = _world.FindItem(uid);
            return item == null || item.IsDeleted;
        });
    }
}
