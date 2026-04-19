using SphereNet.Core.Enums;
using SphereNet.Core.Interfaces;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Game.World.Sectors;

/// <summary>
/// A geographic sector of the world. Maps to CSector in Source-X.
/// World is divided into sectors (default 64x64 tiles each).
/// Each sector tracks its items, characters, and timed objects.
/// Implements IScriptObj for Sphere script property access.
/// </summary>
public sealed class Sector : IScriptObj
{
    public const int SectorSize = 64;

    private readonly int _x, _y;
    private readonly byte _mapIndex;
    private readonly List<Character> _characters = [];
    private readonly List<Item> _items = [];

    // Weather/environment per-sector (Source-X CSector)
    private byte _weather;      // 0=dry, 1=rain, 2=snow
    private byte _season;       // 0=spring, 1=summer, 2=fall, 3=winter, 4=desolation
    private byte _light = 0;    // 0=bright, 30=dark (0 = use global)
    private short _rainChance = 15;
    private short _coldChance = 5;
    private bool _isSleeping;

    public int SectorX => _x;
    public int SectorY => _y;
    public byte MapIndex => _mapIndex;
    public int Number => _y * 96 + _x; // sector index (assuming 96 cols)

    public IReadOnlyList<Character> Characters => _characters;
    public IReadOnlyList<Item> Items => _items;

    public int CharacterCount => _characters.Count;
    public int ItemCount => _items.Count;
    public int ClientCount => _characters.Count(c => c.IsPlayer && c.IsOnline);
    public bool IsEmpty => _characters.Count == 0 && _items.Count == 0;

    public byte Weather { get => _weather; set => _weather = value; }
    public byte Season { get => _season; set => _season = value; }
    public byte Light { get => _light; set => _light = value; }
    public short RainChance { get => _rainChance; set => _rainChance = value; }
    public short ColdChance { get => _coldChance; set => _coldChance = value; }
    public bool IsSleeping { get => _isSleeping; set => _isSleeping = value; }

    /// <summary>Callback for world time queries (WorldHour, WorldMinute).</summary>
    public Func<(int Hour, int Minute)>? GetWorldTime { get; set; }

    public Sector(int x, int y, byte mapIndex)
    {
        _x = x;
        _y = y;
        _mapIndex = mapIndex;
    }

    public void AddCharacter(Character ch)
    {
        if (!_characters.Contains(ch))
            _characters.Add(ch);
    }

    public void RemoveCharacter(Character ch) => _characters.Remove(ch);

    public void AddItem(Item item)
    {
        if (!_items.Contains(item))
            _items.Add(item);
    }

    public void RemoveItem(Item item) => _items.Remove(item);

    /// <summary>Get all objects within a range from a point inside this sector.</summary>
    public IEnumerable<ObjBase> GetObjectsInRange(Point3D center, int range)
    {
        // Snapshot to avoid "Collection was modified" when AI ticks move characters
        var chars = _characters.ToArray();
        var items = _items.ToArray();

        foreach (var ch in chars)
        {
            if (!ch.IsDeleted && center.GetDistanceTo(ch.Position) <= range)
                yield return ch;
        }
        foreach (var item in items)
        {
            if (!item.IsDeleted && item.IsOnGround && center.GetDistanceTo(item.Position) <= range)
                yield return item;
        }
    }

    /// <summary>Tick all objects in this sector. Characters always tick for regen.</summary>
    public void OnTick(long currentTime)
    {
        for (int i = _characters.Count - 1; i >= 0; i--)
        {
            var ch = _characters[i];
            if (ch.IsDeleted) { _characters.RemoveAt(i); continue; }
            if (!ch.IsSleeping)
                ch.OnTick();
        }

        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (item.IsDeleted) { _items.RemoveAt(i); continue; }
            if (!item.IsSleeping)
            {
                if (!item.OnTick())
                    _items.RemoveAt(i);
            }
        }
    }

    // ==================== IScriptObj Implementation ====================

    public string GetName() => $"Sector({_x},{_y},{_mapIndex})";

    public bool TryGetProperty(string key, out string value)
    {
        value = "";
        string upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "NUMBER":
                value = Number.ToString();
                return true;
            case "CLIENTS":
                value = ClientCount.ToString();
                return true;
            case "COMPLEXITY":
                value = CharacterCount.ToString();
                return true;
            case "COMPLEXITY.HIGH":
                value = CharacterCount < 5 ? "1" : "0";
                return true;
            case "COMPLEXITY.MEDIUM":
                value = CharacterCount < 10 ? "1" : "0";
                return true;
            case "COMPLEXITY.LOW":
                value = CharacterCount >= 10 ? "1" : "0";
                return true;
            case "ITEMCOUNT":
                value = ItemCount.ToString();
                return true;
            case "WEATHER":
                value = _weather.ToString();
                return true;
            case "SEASON":
                value = _season.ToString();
                return true;
            case "LIGHT":
                value = _light.ToString();
                return true;
            case "RAINCHANCE":
                value = _rainChance.ToString();
                return true;
            case "COLDCHANCE":
                value = _coldChance.ToString();
                return true;
            case "ISSLEEPING":
                value = _isSleeping ? "1" : "0";
                return true;
            case "CANSLEEP":
                value = (ClientCount == 0) ? "1" : "0";
                return true;
            case "ISDARK":
            {
                byte effectiveLight = _light > 0 ? _light : GetGlobalLight();
                value = effectiveLight >= 12 ? "1" : "0";
                return true;
            }
            case "ISNIGHTTIME":
            {
                var (hour, _) = GetWorldTime?.Invoke() ?? (12, 0);
                value = (hour >= 21 || hour < 7) ? "1" : "0";
                return true;
            }
            case "LOCALTIME":
            {
                var (hour, minute) = GetWorldTime?.Invoke() ?? (12, 0);
                string period = hour switch
                {
                    >= 5 and < 7 => "dawn",
                    >= 7 and < 12 => "morning",
                    12 => "noon",
                    >= 13 and < 17 => "afternoon",
                    >= 17 and < 20 => "evening",
                    >= 20 and < 22 => "dusk",
                    _ => "night"
                };
                value = $"{hour:D2}:{minute:D2} ({period})";
                return true;
            }
            case "LOCALTOD":
            {
                var (hour, minute) = GetWorldTime?.Invoke() ?? (12, 0);
                value = (hour * 60 + minute).ToString();
                return true;
            }
            default:
                return false;
        }
    }

    public bool TrySetProperty(string key, string val)
    {
        string upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "WEATHER":
                if (byte.TryParse(val, out byte w)) { _weather = w; return true; }
                return false;
            case "SEASON":
                if (byte.TryParse(val, out byte s)) { _season = s; return true; }
                return false;
            case "LIGHT":
                if (byte.TryParse(val, out byte l)) { _light = l; return true; }
                return false;
            case "RAINCHANCE":
                if (short.TryParse(val, out short rc)) { _rainChance = rc; return true; }
                return false;
            case "COLDCHANCE":
                if (short.TryParse(val, out short cc)) { _coldChance = cc; return true; }
                return false;
            default:
                return false;
        }
    }

    public bool TryExecuteCommand(string key, string args, ITextConsole source)
    {
        string upper = key.ToUpperInvariant();

        switch (upper)
        {
            case "DRY":
                _weather = 0;
                return true;
            case "RAIN":
                _weather = 1;
                return true;
            case "SNOW":
                _weather = 2;
                return true;
            case "ALLCHARS":
                // Execute command on all characters — handled by caller via iteration
                return true;
            case "ALLCHARSIDLE":
                // Execute command on all idle (offline) characters — handled by caller
                return true;
            case "ALLCLIENTS":
                // Execute command on all connected players — handled by caller
                return true;
            case "ALLITEMS":
                // Execute command on all items — handled by caller
                return true;
            case "RESPAWN":
                // Respawn dead NPCs in this sector
                foreach (var ch in _characters.ToArray())
                {
                    if (!ch.IsPlayer && ch.IsDead)
                        ch.Resurrect();
                }
                return true;
            case "RESTOCK":
                // Restock NPCs — trigger via callback
                return true;
            default:
                return false;
        }
    }

    public TriggerResult OnTrigger(int triggerType, IScriptObj? source, ITriggerArgs? args)
        => TriggerResult.Default;

    private byte GetGlobalLight()
    {
        var (hour, _) = GetWorldTime?.Invoke() ?? (12, 0);
        if (hour >= 6 && hour < 18) return 0;
        if (hour >= 18 && hour < 20) return (byte)((hour - 18) * 10);
        if (hour >= 4 && hour < 6) return (byte)((6 - hour) * 10);
        return 25;
    }
}
