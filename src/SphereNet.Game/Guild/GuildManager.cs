using SphereNet.Core.Types;

namespace SphereNet.Game.Guild;

/// <summary>
/// Guild member privilege. Maps to STONEPRIV_TYPE in Source-X CStoneMember.h.
/// </summary>
public enum GuildPriv : byte
{
    Candidate = 0,
    Member = 1,
    Master = 2,
    Unused = 3,
    Accepted = 4,
    Enemy = 100,
    Ally = 101,
}

/// <summary>
/// Guild alignment type. Maps to STONEALIGN_TYPE in Source-X.
/// </summary>
public enum GuildAlign : byte
{
    Standard = 0,
    Order = 1,
    Chaos = 2,
}

/// <summary>
/// A guild member record. Maps to CStoneMember in Source-X.
/// </summary>
public sealed class GuildMember
{
    public Serial CharUid { get; init; }
    public GuildPriv Priv { get; set; } = GuildPriv.Member;
    public string Title { get; set; } = "";
    public long JoinTime { get; set; }
    public int AccountGold { get; set; }
    public Serial LoyalTo { get; set; } = Serial.Invalid;
    public bool ShowAbbrev { get; set; } = true;
}

/// <summary>
/// Tracks directional war/alliance relationship between two guilds.
/// Maps to CStoneMember with STONEPRIV_ENEMY/ALLY in Source-X.
/// </summary>
public sealed class GuildRelation
{
    public Serial OtherStoneUid { get; init; }
    public bool WeDeclaredWar { get; set; }
    public bool TheyDeclaredWar { get; set; }
    public bool WeDeclaredAlliance { get; set; }
    public bool TheyDeclaredAlliance { get; set; }

    /// <summary>True if both sides declared war (mutual).</summary>
    public bool IsEnemy => WeDeclaredWar && TheyDeclaredWar;
    /// <summary>True if both sides declared alliance (mutual).</summary>
    public bool IsAlly => WeDeclaredAlliance && TheyDeclaredAlliance;
}

/// <summary>
/// Guild definition. Maps to CItemStone in Source-X CItemStone.h.
/// Manages members, wars, alliances, and guild properties.
/// </summary>
public sealed class GuildDef
{
    private readonly Serial _stoneUid;
    private string _name = "";
    private string _abbreviation = "";
    private string _charter = "";
    private string _webUrl = "";
    private GuildAlign _align = GuildAlign.Standard;
    private readonly List<GuildMember> _members = [];
    private readonly Dictionary<Serial, GuildRelation> _relations = [];

    public Serial StoneUid => _stoneUid;
    public string Name { get => _name; set => _name = value; }
    public string Abbreviation { get => _abbreviation; set => _abbreviation = value; }
    public string Charter { get => _charter; set => _charter = value; }
    public string WebUrl { get => _webUrl; set => _webUrl = value; }
    public GuildAlign Align { get => _align; set => _align = value; }
    public IReadOnlyList<GuildMember> Members => _members;
    public int MemberCount => _members.Count;

    public GuildDef(Serial stoneUid)
    {
        _stoneUid = stoneUid;
    }

    /// <summary>Get the guild master member.</summary>
    public GuildMember? GetMaster() =>
        _members.FirstOrDefault(m => m.Priv == GuildPriv.Master);

    /// <summary>Find a member by character UID.</summary>
    public GuildMember? FindMember(Serial charUid) =>
        _members.FirstOrDefault(m => m.CharUid == charUid);

    public bool IsMember(Serial charUid) =>
        _members.Any(m => m.CharUid == charUid && m.Priv is GuildPriv.Member or GuildPriv.Master);

    /// <summary>Add a new recruit (candidate).</summary>
    public GuildMember AddRecruit(Serial charUid)
    {
        var existing = FindMember(charUid);
        if (existing != null) return existing;

        var member = new GuildMember
        {
            CharUid = charUid,
            Priv = GuildPriv.Candidate,
            JoinTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        _members.Add(member);
        return member;
    }

    /// <summary>Accept a candidate as full member.</summary>
    public bool AcceptMember(Serial charUid)
    {
        var member = FindMember(charUid);
        if (member == null || member.Priv != GuildPriv.Candidate) return false;
        member.Priv = GuildPriv.Member;
        return true;
    }

    /// <summary>Remove a member from the guild.</summary>
    public bool RemoveMember(Serial charUid)
    {
        var member = FindMember(charUid);
        if (member == null) return false;
        _members.Remove(member);
        return true;
    }

    /// <summary>Set a new guild master. Old master becomes regular member.</summary>
    public void SetMaster(Serial charUid)
    {
        var oldMaster = GetMaster();
        if (oldMaster != null)
            oldMaster.Priv = GuildPriv.Member;

        var newMaster = FindMember(charUid);
        if (newMaster != null)
            newMaster.Priv = GuildPriv.Master;
    }

    /// <summary>Count members with at least the given priv level. -1 = all.</summary>
    public int GetMemberCount(int minPriv)
    {
        if (minPriv < 0) return _members.Count;
        return _members.Count(m => (byte)m.Priv >= minPriv && (byte)m.Priv < 100);
    }

    /// <summary>Add a character directly as a full member.</summary>
    public GuildMember JoinAsMember(Serial charUid)
    {
        var existing = FindMember(charUid);
        if (existing != null) { existing.Priv = GuildPriv.Member; return existing; }
        var member = new GuildMember
        {
            CharUid = charUid,
            Priv = GuildPriv.Member,
            JoinTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        _members.Add(member);
        return member;
    }

    /// <summary>Elect master based on LoyalTo votes.</summary>
    public void ElectMaster()
    {
        // Count votes: each member's LoyalTo counts as a vote for that character
        var votes = new Dictionary<Serial, int>();
        foreach (var m in _members)
        {
            if ((byte)m.Priv >= 100) continue; // skip enemy/ally entries
            var target = m.LoyalTo.IsValid ? m.LoyalTo : m.CharUid; // self-vote if no loyalty
            votes[target] = votes.GetValueOrDefault(target) + 1;
        }
        if (votes.Count == 0) return;

        var winner = votes.MaxBy(kv => kv.Value).Key;
        SetMaster(winner);
    }

    // --- Relations (War / Alliance) ---

    /// <summary>Get or create the relation record for another guild.</summary>
    public GuildRelation GetOrCreateRelation(Serial otherStoneUid)
    {
        if (!_relations.TryGetValue(otherStoneUid, out var rel))
        {
            rel = new GuildRelation { OtherStoneUid = otherStoneUid };
            _relations[otherStoneUid] = rel;
        }
        return rel;
    }

    public GuildRelation? GetRelation(Serial otherStoneUid) =>
        _relations.GetValueOrDefault(otherStoneUid);

    /// <summary>Declare war on another guild (sets WeDeclaredWar).</summary>
    public void DeclareWar(Serial otherGuildStone) =>
        GetOrCreateRelation(otherGuildStone).WeDeclaredWar = true;

    /// <summary>Declare peace with another guild.</summary>
    public void DeclarePeace(Serial otherGuildStone)
    {
        if (_relations.TryGetValue(otherGuildStone, out var rel))
        {
            rel.WeDeclaredWar = false;
            CleanupRelation(otherGuildStone, rel);
        }
    }

    public bool IsAtWarWith(Serial otherGuildStone) =>
        _relations.TryGetValue(otherGuildStone, out var rel) && rel.IsEnemy;

    public IEnumerable<Serial> Wars =>
        _relations.Where(kv => kv.Value.WeDeclaredWar).Select(kv => kv.Key);

    public void AddAlly(Serial otherGuildStone) =>
        GetOrCreateRelation(otherGuildStone).WeDeclaredAlliance = true;

    public void RemoveAlly(Serial otherGuildStone)
    {
        if (_relations.TryGetValue(otherGuildStone, out var rel))
        {
            rel.WeDeclaredAlliance = false;
            CleanupRelation(otherGuildStone, rel);
        }
    }

    public bool IsAlliedWith(Serial otherGuildStone) =>
        _relations.TryGetValue(otherGuildStone, out var rel) && rel.IsAlly;

    public IEnumerable<Serial> Allies =>
        _relations.Where(kv => kv.Value.WeDeclaredAlliance).Select(kv => kv.Key);

    public IReadOnlyDictionary<Serial, GuildRelation> Relations => _relations;

    private void CleanupRelation(Serial uid, GuildRelation rel)
    {
        if (!rel.WeDeclaredWar && !rel.TheyDeclaredWar &&
            !rel.WeDeclaredAlliance && !rel.TheyDeclaredAlliance)
            _relations.Remove(uid);
    }
}

/// <summary>
/// Manages all guilds on the server.
/// </summary>
public sealed class GuildManager
{
    private readonly Dictionary<Serial, GuildDef> _guilds = [];

    public GuildDef? GetGuild(Serial stoneUid) => _guilds.GetValueOrDefault(stoneUid);
    public int GuildCount => _guilds.Count;

    /// <summary>Find which guild a character belongs to.</summary>
    public GuildDef? FindGuildFor(Serial charUid) =>
        _guilds.Values.FirstOrDefault(g => g.FindMember(charUid) != null);

    public GuildDef CreateGuild(Serial stoneUid, string name, Serial masterUid)
    {
        var guild = new GuildDef(stoneUid) { Name = name };
        var master = guild.AddRecruit(masterUid);
        master.Priv = GuildPriv.Master;
        _guilds[stoneUid] = guild;
        return guild;
    }

    public void RemoveGuild(Serial stoneUid) => _guilds.Remove(stoneUid);

    public IEnumerable<GuildDef> GetAllGuilds() => _guilds.Values;

    // --- Save/Load via item TAGs (guild stone items) ---

    /// <summary>Serialize all guilds to their stone items' TAGs for persistence.</summary>
    public void SerializeAllToTags(World.GameWorld world)
    {
        foreach (var (stoneUid, guild) in _guilds)
        {
            var stone = world.FindItem(stoneUid);
            if (stone == null) continue;

            stone.SetTag("GUILD.NAME", guild.Name);
            stone.SetTag("GUILD.ABBREV", guild.Abbreviation);
            if (!string.IsNullOrEmpty(guild.Charter))
                stone.SetTag("GUILD.CHARTER", guild.Charter);
            if (guild.Align != GuildAlign.Standard)
                stone.SetTag("GUILD.ALIGN", ((byte)guild.Align).ToString());

            // Members: uid:priv:title:accountgold:loyalto:showabbrev
            var memberStrs = guild.Members.Select(m =>
                $"0{m.CharUid.Value:X}:{(byte)m.Priv}:{m.Title}:{m.AccountGold}:{(m.LoyalTo == Serial.Invalid ? "0" : $"0{m.LoyalTo.Value:X}")}:{(m.ShowAbbrev ? "1" : "0")}");
            stone.SetTag("GUILD.MEMBERS", string.Join(",", memberStrs));

            // Relations: uid:wewar:theywar:weally:theyally
            if (guild.Relations.Count > 0)
            {
                var relStrs = guild.Relations.Values.Select(r =>
                    $"0{r.OtherStoneUid.Value:X}:{(r.WeDeclaredWar ? "1" : "0")}:{(r.TheyDeclaredWar ? "1" : "0")}:{(r.WeDeclaredAlliance ? "1" : "0")}:{(r.TheyDeclaredAlliance ? "1" : "0")}");
                stone.SetTag("GUILD.RELATIONS", string.Join(",", relStrs));
            }

            // Legacy compat: also write GUILD.WARS/ALLIES for older saves
            var wars = guild.Wars.ToList();
            if (wars.Count > 0)
                stone.SetTag("GUILD.WARS", string.Join(",", wars.Select(s => $"0{s.Value:X}")));
            var allies = guild.Allies.ToList();
            if (allies.Count > 0)
                stone.SetTag("GUILD.ALLIES", string.Join(",", allies.Select(s => $"0{s.Value:X}")));
        }
    }

    /// <summary>Rebuild guilds from guild stone items after world load.</summary>
    public void DeserializeFromWorld(World.GameWorld world)
    {
        _guilds.Clear();
        foreach (var obj in world.GetAllObjects())
        {
            if (obj is not Objects.Items.Item item) continue;
            if (!item.TryGetTag("GUILD.NAME", out string? guildName)) continue;
            if (string.IsNullOrWhiteSpace(guildName)) continue;

            var guild = new GuildDef(item.Uid) { Name = guildName };

            if (item.TryGetTag("GUILD.ABBREV", out string? abbrev))
                guild.Abbreviation = abbrev ?? "";
            if (item.TryGetTag("GUILD.CHARTER", out string? charter))
                guild.Charter = charter ?? "";
            if (item.TryGetTag("GUILD.ALIGN", out string? alignStr) &&
                byte.TryParse(alignStr, out byte alignVal))
                guild.Align = (GuildAlign)alignVal;

            // Parse members
            if (item.TryGetTag("GUILD.MEMBERS", out string? membersStr) && !string.IsNullOrEmpty(membersStr))
            {
                foreach (var part in membersStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var segs = part.Split(':', 6);
                    if (segs.Length < 2) continue;
                    uint uid = ParseHexSerial(segs[0]);
                    if (uid == 0) continue;
                    byte priv = byte.TryParse(segs[1], out byte p) ? p : (byte)1;
                    string title = segs.Length > 2 ? segs[2] : "";

                    var member = guild.AddRecruit(new Serial(uid));
                    member.Priv = (GuildPriv)priv;
                    member.Title = title;
                    if (segs.Length > 3 && int.TryParse(segs[3], out int ag))
                        member.AccountGold = ag;
                    if (segs.Length > 4)
                    {
                        uint loyalUid = ParseHexSerial(segs[4]);
                        member.LoyalTo = loyalUid != 0 ? new Serial(loyalUid) : Serial.Invalid;
                    }
                    if (segs.Length > 5)
                        member.ShowAbbrev = segs[5] != "0";
                }
            }

            // Parse relations (new format: uid:wewar:theywar:weally:theyally)
            if (item.TryGetTag("GUILD.RELATIONS", out string? relStr) && !string.IsNullOrEmpty(relStr))
            {
                foreach (var rpart in relStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var rsegs = rpart.Split(':', 5);
                    if (rsegs.Length < 5) continue;
                    uint ruid = ParseHexSerial(rsegs[0]);
                    if (ruid == 0) continue;
                    var rel = guild.GetOrCreateRelation(new Serial(ruid));
                    rel.WeDeclaredWar = rsegs[1] == "1";
                    rel.TheyDeclaredWar = rsegs[2] == "1";
                    rel.WeDeclaredAlliance = rsegs[3] == "1";
                    rel.TheyDeclaredAlliance = rsegs[4] == "1";
                }
            }
            else
            {
                // Legacy fallback: simple GUILD.WARS/ALLIES (WeDeclared only)
                ParseSerialSet(item, "GUILD.WARS", uid => guild.DeclareWar(uid));
                ParseSerialSet(item, "GUILD.ALLIES", uid => guild.AddAlly(uid));
            }

            _guilds[item.Uid] = guild;
        }
    }

    private static uint ParseHexSerial(string? str)
    {
        if (string.IsNullOrWhiteSpace(str)) return 0;
        str = str.Trim().TrimStart('0').TrimStart('x', 'X');
        if (string.IsNullOrEmpty(str)) return 0;
        if (uint.TryParse(str, System.Globalization.NumberStyles.HexNumber, null, out uint val))
            return val;
        return 0;
    }

    private static void ParseSerialSet(Objects.Items.Item item, string tagName, Action<Serial> action)
    {
        if (!item.TryGetTag(tagName, out string? listStr) || string.IsNullOrWhiteSpace(listStr))
            return;
        foreach (var part in listStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            uint val = ParseHexSerial(part);
            if (val != 0) action(new Serial(val));
        }
    }
}
