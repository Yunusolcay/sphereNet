using System.Diagnostics.CodeAnalysis;
using SphereNet.Core.Enums;

namespace SphereNet.Core.Types;

/// <summary>
/// Packed resource identifier. Maps to CResourceID in Source-X.
/// Encodes RES_TYPE (high 8 bits) + index (low 24 bits) + optional page.
/// </summary>
public readonly struct ResourceId : IEquatable<ResourceId>
{
    private const int TypeShift = 24;
    private const uint IndexMask = 0x00FFFFFF;

    public static readonly ResourceId Invalid = new(0);

    private readonly uint _packed;
    private readonly ushort _page;

    public ResourceId(ResType type, int index, ushort page = 0)
    {
        _packed = ((uint)type << TypeShift) | ((uint)index & IndexMask);
        _page = page;
    }

    private ResourceId(uint packed, ushort page = 0)
    {
        _packed = packed;
        _page = page;
    }

    public ResType Type => (ResType)(_packed >> TypeShift);
    public int Index => (int)(_packed & IndexMask);
    public ushort Page => _page;
    public bool IsValid => Type != ResType.Unknown;

    public ResourceId WithPage(ushort page) => new(_packed, page);

    /// <summary>Create a ResourceId for an EVENTS section by name (e.g. "e_carnivores").</summary>
    public static ResourceId FromEventName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Invalid;
        int hash = GenerateStringHash(name, ResType.Events);
        return new ResourceId(ResType.Events, hash);
    }

    /// <summary>Create a ResourceId from a generic string reference (defname-style).</summary>
    public static ResourceId FromString(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Invalid;
        // Try hex number first (e.g. "0x1234")
        var span = name.AsSpan().Trim();
        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            if (int.TryParse(span[2..], System.Globalization.NumberStyles.HexNumber, null, out int hexVal))
                return new ResourceId(ResType.DefName, hexVal);
        }
        int h = GenerateStringHash(name, ResType.DefName);
        return new ResourceId(ResType.DefName, h);
    }

    /// <summary>Create a ResourceId from a string name with a specific resource type.</summary>
    public static ResourceId FromString(string name, ResType type)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Invalid;
        int h = GenerateStringHash(name, type);
        return new ResourceId(type, h);
    }

    private static int GenerateStringHash(string name, ResType resType)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ (uint)resType) * 16777619u;
            foreach (char c in name)
            {
                hash = (hash ^ char.ToLowerInvariant(c)) * 16777619u;
            }
            int result = (int)(hash & 0x00FFFFFF);
            return result == 0 ? 1 : result;
        }
    }

    public bool Equals(ResourceId other) => _packed == other._packed && _page == other._page;
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is ResourceId r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(_packed, _page);

    public static bool operator ==(ResourceId left, ResourceId right) => left.Equals(right);
    public static bool operator !=(ResourceId left, ResourceId right) => !left.Equals(right);

    public override string ToString() => $"{Type}:{Index}" + (_page > 0 ? $":{_page}" : "");
}
