using SphereNet.Core.Configuration;

namespace SphereNet.Persistence.Formats;

/// <summary>
/// Sidecar descriptor written alongside sharded saves. Plain key=value text
/// (<c>FORMAT=</c>, <c>SHARDS=</c>, <c>FILE=</c>) — loads in any editor and
/// one can hand-edit shard lists for recovery. Lives at
/// <c>{base}.manifest</c> (e.g. <c>sphereworld.manifest</c>).
/// </summary>
public sealed class ShardManifest
{
    public SaveFormat Format { get; set; } = SaveFormat.Text;
    public int ShardCount { get; set; } = 1;
    public List<string> Files { get; set; } = new();

    public static string PathFor(string savePath, string baseName) =>
        Path.Combine(savePath, baseName + ".manifest");

    public void Save(string path)
    {
        using var sw = new StreamWriter(path);
        sw.WriteLine("// SphereNet shard manifest");
        sw.WriteLine($"FORMAT={Format}");
        sw.WriteLine($"SHARDS={ShardCount}");
        foreach (var f in Files)
            sw.WriteLine($"FILE={f}");
    }

    public static ShardManifest? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;
        var m = new ShardManifest { Files = new List<string>() };
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            switch (key.ToUpperInvariant())
            {
                case "FORMAT":
                    if (Enum.TryParse<SaveFormat>(val, ignoreCase: true, out var f))
                        m.Format = f;
                    break;
                case "SHARDS":
                    if (int.TryParse(val, out int n)) m.ShardCount = n;
                    break;
                case "FILE":
                    m.Files.Add(val);
                    break;
            }
        }
        return m;
    }

    /// <summary>Map a UID to its shard index. UID hashes via FNV-1a (fast, no
    /// collisions bias at these counts) so entity placement is deterministic
    /// across runs — a save and the subsequent migration land each object in
    /// the same file.</summary>
    public static int ShardIndexForUid(uint uid, int shardCount)
    {
        if (shardCount <= 1) return 0;
        unchecked
        {
            uint hash = 2166136261;
            hash = (hash ^ (uid & 0xFF)) * 16777619;
            hash = (hash ^ ((uid >> 8) & 0xFF)) * 16777619;
            hash = (hash ^ ((uid >> 16) & 0xFF)) * 16777619;
            hash = (hash ^ ((uid >> 24) & 0xFF)) * 16777619;
            return (int)(hash % (uint)shardCount);
        }
    }
}
