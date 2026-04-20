using Microsoft.Extensions.Logging;
using SphereNet.Core.Collections;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Parsing;

namespace SphereNet.Scripting.Resources;

/// <summary>
/// Central resource registry. Maps to CResourceHolder + CServerConfig.LoadResourceSection in Source-X.
/// Manages all loaded script resources indexed by ResourceId.
/// </summary>
public sealed class ResourceHolder
{
    private readonly SortedResourceHash<ResourceLink> _resources = new();
    private readonly Dictionary<string, ResourceId> _defNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _defTexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceScript> _scriptFiles = [];
    private readonly Dictionary<string, string> _defMessages = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    public string ScpBaseDir { get; set; } = "";

    public ResourceHolder(ILogger<ResourceHolder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Map a section name to its RES_TYPE.
    /// </summary>
    public static ResType SectionToResType(string sectionName) => sectionName.ToUpperInvariant() switch
    {
        "ITEMDEF" => ResType.ItemDef,
        "CHARDEF" => ResType.CharDef,
        "SPELL" => ResType.SpellDef,
        "SKILL" or "SKILLDEF" => ResType.SkillDef,
        "TEMPLATE" => ResType.Template,
        "TYPEDEF" => ResType.TypeDef,
        "DIALOG" => ResType.Dialog,
        "EVENTS" => ResType.Events,
        "FUNCTION" => ResType.Function,
        "DEFNAME" or "DEFNAMES" => ResType.DefName,
        "RESDEFNAME" => ResType.DefName,
        "REGIONTYPE" => ResType.RegionType,
        "REGIONRESOURCE" => ResType.RegionResource,
        "AREADEF" or "AREA" => ResType.Area,
        "ROOMDEF" or "ROOM" => ResType.RoomDef,
        "MULTIDEF" or "MULTI" => ResType.MultiDef,
        "SKILLCLASS" => ResType.SkillClass,
        "SKILLMENU" => ResType.SkillMenu,
        "MENU" => ResType.Menu,
        "SPHERE" => ResType.Sphere,
        "SCROLL" => ResType.Scroll,
        "BOOK" => ResType.Book,
        "TIP" => ResType.Tip,
        "SPEECH" => ResType.Speech,
        "NEWBIE" => ResType.NewBie,
        "PLEVEL" => ResType.PlevelCfg,
        "NAMES" => ResType.Names,
        "OBSCENE" => ResType.Obscene,
        "WEBPAGE" => ResType.WebPage,
        "RESOURCELIST" or "RESOURCES" => ResType.ResourceList,
        "SERVERS" => ResType.ServerConfig,
        "BLOCKIP" => ResType.ServerConfig,
        "SPAWN" => ResType.Spawn,
        "COMMENT" => ResType.Comment,
        "ADVANCE" or "FAME" or "KARMA" or "NOTOTITLES" or "RUNES" or "DEFMESSAGE" => ResType.Sphere,
        "TYPEDEFS" => ResType.Sphere,
        "STARTS" or "MOONGATES" or "TELEPORTERS" => ResType.WorldScript,
        _ => ResType.Unknown
    };

    /// <summary>
    /// Resource types that use numeric hex IDs (body ID / item ID / spell number).
    /// All other types use string names that get auto-hashed.
    /// </summary>
    private static bool IsNumericIdType(ResType t) => t is
        ResType.ItemDef or ResType.CharDef or ResType.SpellDef or ResType.SkillDef;

    /// <summary>
    /// Definition types whose keys should be retained on the ResourceLink
    /// for fast access by DefinitionLoader (avoids re-reading script files).
    /// </summary>
    private static bool IsDefinitionType(ResType t) => t is
        ResType.ItemDef or ResType.CharDef or ResType.SpellDef or ResType.SkillClass or ResType.SkillDef or ResType.Names or ResType.Speech or ResType.Template;

    /// <summary>
    /// Load all sections from a script file.
    /// </summary>
    public int LoadResourceFile(string filePath)
    {
        string fullPath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.Combine(ScpBaseDir, filePath);

        var resScript = new ResourceScript(fullPath);
        _scriptFiles.Add(resScript);

        ScriptFile file;
        try
        {
            file = resScript.Open();
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("Script file not found: {Path}", fullPath);
            return 0;
        }

        try
        {
            return LoadResourcesFromFile(file, fullPath);
        }
        finally
        {
            resScript.Close();
        }
    }

    private int LoadResourcesFromFile(ScriptFile file, string filePath)
    {
        int count = 0;
        var sections = file.ReadAllSections();

        foreach (var section in sections)
        {
            if (section.Name.Equals("DEFMESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                LoadDefMessages(section);
                count++;
                continue;
            }

            ResType resType = SectionToResType(section.Name);

            if (resType == ResType.DefName)
            {
                LoadDefNames(section);
                count++;
                continue;
            }

            if (resType == ResType.Sphere || resType == ResType.ServerConfig ||
                resType == ResType.ResourceList || resType == ResType.Comment ||
                resType == ResType.Book)
            {
                count++;
                continue;
            }

            if (resType == ResType.Unknown)
            {
                _logger.LogWarning("Unknown section [{Name}] in {File}:{Line}",
                    section.Name, filePath, section.Context.LineNumber);
                continue;
            }

            string rawArg = section.Argument;
            int index = ParseResourceIndex(rawArg, resType);
            if (index < 0) continue;

            var rid = new ResourceId(resType, index);

            // SPAWN sections get parsed directly into SpawnGroupDef
            if (resType == ResType.Spawn)
            {
                var spawnDef = new SpawnGroupDef(rid)
                {
                    ScriptFilePath = filePath,
                    ScriptLineNumber = section.Context.LineNumber
                };

                foreach (var key in section.Keys)
                    spawnDef.LoadFromKey(key.Key, key.Arg);

                string spawnName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(spawnName))
                {
                    spawnDef.DefName = spawnName;
                    _defNames[spawnName] = rid;
                }

                _resources.Add(rid, spawnDef);

                if (!string.IsNullOrEmpty(spawnDef.DefName))
                    _defNames[spawnDef.DefName] = rid;

                count++;
                continue;
            }

            var link = new ResourceLink(rid)
            {
                ScriptFilePath = filePath,
                ScriptLineNumber = section.Context.LineNumber
            };

            link.ScanSection(section, retainKeys: IsDefinitionType(resType));

            // For string-named resources, auto-register the name as a DEFNAME
            if (!IsNumericIdType(resType) && !string.IsNullOrEmpty(rawArg))
            {
                string defName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(defName))
                {
                    link.DefName = defName;
                    _defNames[defName] = rid;
                }
            }

            _resources.Add(rid, link);

            if (!string.IsNullOrEmpty(link.DefName))
                _defNames[link.DefName] = rid;

            count++;
        }

        _logger.LogInformation("Loaded {File}: {Count} sections", Path.GetFileName(filePath), count);
        return count;
    }

    private void LoadDefNames(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            if (!string.IsNullOrEmpty(key.Key) && key.HasArg)
            {
                if (ScriptKey.TryParseNumber(key.Arg.AsSpan(), out long val))
                {
                    _defNames[key.Key] = new ResourceId(ResType.DefName, (int)val);
                }
                else
                {
                    _defTexts[key.Key] = key.Arg;
                }
            }
        }
    }

    private void LoadDefMessages(ScriptSection section)
    {
        foreach (var key in section.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.Key))
                continue;
            _defMessages[key.Key.Trim()] = key.Arg ?? "";
        }
    }

    private int ParseResourceIndex(string arg, ResType resType)
    {
        if (string.IsNullOrEmpty(arg))
            return -1;

        // For numeric ID types (ITEMDEF, CHARDEF, SPELL, SKILL) — parse as hex
        if (IsNumericIdType(resType))
        {
            var span = arg.AsSpan().Trim();

            // Strip 0x prefix if present
            if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
                span = span[2..];

            if (long.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out long hexVal))
                return (int)hexVal;

            // Try as DEFNAME for numeric types too (e.g. [CHARDEF c_guard] where c_guard was in DEFNAME)
            if (_defNames.TryGetValue(arg, out var rid))
                return rid.Index;

            // For CHARDEF/ITEMDEF with string names, auto-assign an index and register
            string cleanName = arg.Split(' ', 2)[0].Trim();
            if (!string.IsNullOrEmpty(cleanName))
            {
                int hash = GenerateStringHash(cleanName, resType);
                _defNames[cleanName] = new ResourceId(resType, hash);
                return hash;
            }

            _logger.LogWarning("Cannot resolve resource index '{Arg}' for {Type}", arg, resType);
            return -1;
        }

        // For string-named types (FUNCTION, DIALOG, EVENTS, TYPEDEF, etc.)
        // Take only the first word as the name (e.g. "r_default_grass t_grass" → "r_default_grass")
        string name = arg.Split(' ', 2)[0].Trim();
        if (string.IsNullOrEmpty(name))
            return -1;

        // If already registered, return existing
        if (_defNames.TryGetValue(name, out var existingRid) && existingRid.Type == resType)
            return existingRid.Index;

        // Generate stable hash from name + type
        int index = GenerateStringHash(name, resType);
        _defNames[name] = new ResourceId(resType, index);
        return index;
    }

    /// <summary>
    /// Generate a stable hash for a string name, packed into 24-bit range (0x000001-0xFFFFFF).
    /// Uses FNV-1a for good distribution and collision avoidance.
    /// </summary>
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
            // Fit into 24-bit positive range (ResourceId.Index max), avoid 0
            int result = (int)(hash & 0x00FFFFFF);
            return result == 0 ? 1 : result;
        }
    }

    // --- Public accessors ---

    public ResourceLink? GetResource(ResourceId rid) => _resources.Get(rid);

    public ResourceLink? GetResource(ResType type, int index) =>
        _resources.Get(new ResourceId(type, index));

    public ResourceId ResolveDefName(string name)
    {
        return _defNames.TryGetValue(name, out var rid) ? rid : ResourceId.Invalid;
    }

    public bool RegisterDefName(string name, ResourceId rid)
    {
        if (_defNames.ContainsKey(name))
            return false;
        _defNames[name] = rid;
        return true;
    }

    public void ReplaceResource(ResourceId rid, ResourceLink newLink)
    {
        _resources.Replace(rid, newLink);
        if (!string.IsNullOrEmpty(newLink.DefName))
            _defNames[newLink.DefName] = rid;
    }

    private static readonly Random _rng = new();

    /// <summary>
    /// Get a random name from a [NAMES xxx] section.
    /// Used to resolve #NAMES_HUMANMALE etc. in NPC names.
    /// </summary>
    public string? GetRandomName(string namesId)
    {
        // NAMES sections use string-hashed IDs
        int hash = GenerateStringHash(namesId, ResType.Names);
        var link = _resources.Get(new ResourceId(ResType.Names, hash));
        if (link?.StoredKeys == null || link.StoredKeys.Count == 0)
            return null;

        // StoredKeys contains the name entries (first line might be count, skip it)
        var names = new List<string>();
        foreach (var key in link.StoredKeys)
        {
            string entry = key.Key.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            // Skip numeric-only lines (count header)
            if (int.TryParse(entry, out _)) continue;
            names.Add(entry);
        }

        if (names.Count == 0) return null;
        return names[_rng.Next(names.Count)];
    }

    /// <summary>
    /// Resolve #NAMES_xxx placeholders in a string.
    /// e.g. "#NAMES_HUMANMALE the Banker" → "Aaron the Banker"
    /// </summary>
    public string ResolveNamesInString(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('#'))
            return input;

        int idx = input.IndexOf('#');
        while (idx >= 0 && idx < input.Length)
        {
            // Find the end of the #NAMES_xxx token
            int end = idx + 1;
            while (end < input.Length && input[end] != ' ' && input[end] != ',')
                end++;

            string token = input[(idx + 1)..end]; // without #
            string? replacement = GetRandomName(token);
            if (replacement != null)
            {
                input = input[..idx] + replacement + input[end..];
                idx = input.IndexOf('#', idx + replacement.Length);
            }
            else
            {
                idx = input.IndexOf('#', end);
            }
        }

        return input;
    }

    public IEnumerable<ResourceLink> GetAllResources() => _resources.GetAll();
    public int ResourceCount => _resources.Count;
    public int DefNameCount => _defNames.Count;
    public bool TryGetDefMessage(string key, out string value) => _defMessages.TryGetValue(key, out value!);
    public IReadOnlyDictionary<string, string> GetAllDefMessages() => _defMessages;
    public bool TryGetDefValue(string key, out string value) => _defTexts.TryGetValue(key, out value!);

    public IReadOnlyList<ResourceScript> ScriptFiles => _scriptFiles;

    /// <summary>
    /// Log a summary of loaded resources by type.
    /// </summary>
    public void LogResourceSummary()
    {
        var counts = new Dictionary<ResType, int>();
        foreach (var link in _resources.GetAll())
        {
            var t = link.Id.Type;
            counts.TryGetValue(t, out int c);
            counts[t] = c + 1;
        }

        _logger.LogInformation("Resource summary: {Total} resources, {DefNames} defnames",
            _resources.Count, _defNames.Count);
        foreach (var (type, count) in counts.OrderByDescending(x => x.Value))
        {
            _logger.LogInformation("  {Type}: {Count}", type, count);
        }
    }

    /// <summary>
    /// ReSync: reload all script files that changed on disk since last load.
    /// Maps to CServerConfig::Resync in Source-X.
    /// Returns the number of files reloaded.
    /// </summary>
    public int Resync()
    {
        int reloaded = 0;

        foreach (var script in _scriptFiles)
        {
            if (!script.NeedsReSync())
                continue;

            _logger.LogInformation("ReSync: reloading {File}", script.FilePath);

            try
            {
                var file = script.Open();
                try
                {
                    ReloadResourcesFromFile(file, script.FilePath);
                    reloaded++;
                }
                finally
                {
                    script.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ReSync failed for {File}: {Error}", script.FilePath, ex.Message);
            }
        }

        return reloaded;
    }

    /// <summary>
    /// Full resync: reload ALL script files regardless of modification time.
    /// </summary>
    public int ResyncAll()
    {
        int reloaded = 0;

        foreach (var script in _scriptFiles)
        {
            _logger.LogInformation("ReSync: reloading {File}", script.FilePath);

            try
            {
                var file = script.Open();
                try
                {
                    ReloadResourcesFromFile(file, script.FilePath);
                    reloaded++;
                }
                finally
                {
                    script.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("ReSync failed for {File}: {Error}", script.FilePath, ex.Message);
            }
        }

        return reloaded;
    }

    private void ReloadResourcesFromFile(ScriptFile file, string filePath)
    {
        var sections = file.ReadAllSections();

        foreach (var section in sections)
        {
            if (section.Name.Equals("DEFMESSAGE", StringComparison.OrdinalIgnoreCase))
            {
                LoadDefMessages(section);
                continue;
            }

            ResType resType = SectionToResType(section.Name);

            if (resType == ResType.DefName)
            {
                LoadDefNames(section);
                continue;
            }

            if (resType == ResType.Unknown || resType == ResType.Sphere ||
                resType == ResType.ServerConfig || resType == ResType.ResourceList ||
                resType == ResType.Comment || resType == ResType.Book)
                continue;

            string rawArg = section.Argument;
            int index = ParseResourceIndex(rawArg, resType);
            if (index < 0) continue;

            var rid = new ResourceId(resType, index);

            if (resType == ResType.Spawn)
            {
                var spawnDef = new SpawnGroupDef(rid)
                {
                    ScriptFilePath = filePath,
                    ScriptLineNumber = section.Context.LineNumber
                };

                foreach (var key in section.Keys)
                    spawnDef.LoadFromKey(key.Key, key.Arg);

                string spawnName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(spawnName))
                {
                    spawnDef.DefName = spawnName;
                    _defNames[spawnName] = rid;
                }

                _resources.Replace(rid, spawnDef);

                if (!string.IsNullOrEmpty(spawnDef.DefName))
                    _defNames[spawnDef.DefName] = rid;
                continue;
            }

            var link = new ResourceLink(rid)
            {
                ScriptFilePath = filePath,
                ScriptLineNumber = section.Context.LineNumber
            };

            link.ScanSection(section, retainKeys: IsDefinitionType(resType));

            if (!IsNumericIdType(resType) && !string.IsNullOrEmpty(rawArg))
            {
                string defName = rawArg.Split(' ', 2)[0].Trim();
                if (!string.IsNullOrEmpty(defName))
                {
                    link.DefName = defName;
                    _defNames[defName] = rid;
                }
            }

            _resources.Replace(rid, link);

            if (!string.IsNullOrEmpty(link.DefName))
                _defNames[link.DefName] = rid;
        }
    }
}
