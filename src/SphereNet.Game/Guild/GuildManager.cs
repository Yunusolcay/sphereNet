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
    private readonly HashSet<Serial> _houses = [];
    private readonly HashSet<Serial> _ships = [];

    public Serial StoneUid => _stoneUid;

    /// <summary>True when this record belongs to a TOWN stone. Source-X keeps
    /// MEMORY_GUILD and MEMORY_TOWN independent: a character may be a guild
    /// member AND a town citizen at once, so membership checks must never mix
    /// the two pools.</summary>
    public bool IsTownStone { get; set; }

    public string Name { get => _name; set => _name = value; }
    public string Abbreviation { get => _abbreviation; set => _abbreviation = value; }
    public string Charter { get => _charter; set => _charter = value; }
    public string WebUrl { get => _webUrl; set => _webUrl = value; }
    public GuildAlign Align { get => _align; set => _align = value; }
    public IReadOnlyList<GuildMember> Members => _members;
    public int MemberCount => _members.Count;

    // Guild-owned structures (Source-X CItemStone multi storage). MaxHouses/
    // MaxShips are advisory caps exposed to scripts (0 = use the server default);
    // Source-X enforces them at placement, not on ADDHOUSE/ADDSHIP.
    public int MaxHouses { get; set; }
    public int MaxShips { get; set; }
    public IReadOnlyCollection<Serial> Houses => _houses;
    public IReadOnlyCollection<Serial> Ships => _ships;
    public int HouseCount => _houses.Count;
    public int ShipCount => _ships.Count;

    public GuildDef(Serial stoneUid)
    {
        _stoneUid = stoneUid;
    }

    /// <summary>Register a house multi to this guild (Source-X STC_ADDHOUSE /
    /// AddMulti). A uid can be a house or a ship, never both.</summary>
    public bool AddHouse(Serial uid)
    {
        if (!uid.IsValid) return false;
        _ships.Remove(uid);
        return _houses.Add(uid);
    }

    /// <summary>Register a ship multi to this guild (Source-X STC_ADDSHIP).</summary>
    public bool AddShip(Serial uid)
    {
        if (!uid.IsValid) return false;
        _houses.Remove(uid);
        return _ships.Add(uid);
    }

    /// <summary>Remove a structure by uid regardless of kind (Source-X
    /// ISV_DELHOUSE / ISV_DELSHIP both call DelMulti).</summary>
    public bool DelMulti(Serial uid)
    {
        bool removedHouse = _houses.Remove(uid);
        bool removedShip = _ships.Remove(uid);
        return removedHouse || removedShip;
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
        NormalizeMasters();
        return true;
    }

    /// <summary>Remove a member from the guild. If the master is removed, promotes next member.</summary>
    public bool RemoveMember(Serial charUid)
    {
        var member = FindMember(charUid);
        if (member == null) return false;
        bool wasMaster = member.Priv == GuildPriv.Master;
        _members.Remove(member);
        if (wasMaster && _members.Count > 0)
        {
            // Promote a real person — a full Member first, otherwise a waiting
            // candidate. Never fall back to _members[0]: that could be an
            // Enemy/Ally relationship record (Priv 100/101), which must not
            // become guild master. If no human remains, the guild is left
            // masterless and is reaped by the disband path.
            var next = _members.FirstOrDefault(m => m.Priv == GuildPriv.Member);
            if (next != null)
                next.Priv = GuildPriv.Master;
        }
        return true;
    }

    /// <summary>Set a new guild master. Old master becomes regular member.</summary>
    public void SetMaster(Serial charUid)
    {
        var newMaster = FindMember(charUid);
        if (newMaster == null || newMaster.Priv is not (GuildPriv.Member or GuildPriv.Master))
            return;

        var oldMaster = GetMaster();
        if (oldMaster != null)
            oldMaster.Priv = GuildPriv.Member;

        newMaster.Priv = GuildPriv.Master;
    }

    /// <summary>How many members hold exactly this role; -1 counts them all.
    ///
    /// Source-X compares GetPriv() to the requested value (CItemStone::r_WriteVal
    /// member.count, CItemStone.cpp:461). Treating the parameter as a MINIMUM turned
    /// the roles into a ladder, so a candidate query counted masters and members too
    /// and every script tally came out inflated.</summary>
    public int GetMemberCount(int priv)
    {
        if (priv < 0) return _members.Count;
        return _members.Count(m => (byte)m.Priv == priv);
    }

    /// <summary>Add a character directly as a full member.</summary>
    public GuildMember JoinAsMember(Serial charUid)
    {
        var existing = FindMember(charUid);
        if (existing != null)
        {
            if (existing.Priv is GuildPriv.Candidate or GuildPriv.Accepted)
                existing.Priv = GuildPriv.Member;
            NormalizeMasters();
            return existing;
        }
        var member = new GuildMember
        {
            CharUid = charUid,
            Priv = GuildPriv.Member,
            JoinTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        _members.Add(member);
        NormalizeMasters();
        return member;
    }

    /// <summary>Elect master based on LoyalTo votes.</summary>
    public void ElectMaster()
    {
        // Count votes: each member's LoyalTo counts as a vote for that character
        var eligible = _members
            .Where(m => m.Priv is GuildPriv.Member or GuildPriv.Master)
            .ToList();
        if (eligible.Count == 0) return;
        var eligibleUids = eligible.Select(m => m.CharUid).ToHashSet();
        var votes = new Dictionary<Serial, int>();
        foreach (var m in eligible)
        {
            var target = m.LoyalTo.IsValid && eligibleUids.Contains(m.LoyalTo)
                ? m.LoyalTo
                : m.CharUid;
            if (target == m.CharUid && m.LoyalTo != target)
                m.LoyalTo = Serial.Invalid;
            votes[target] = votes.GetValueOrDefault(target) + 1;
        }
        if (votes.Count == 0) return;

        // Only members can become master — filter out non-member votes
        int highest = votes.Values.Max();
        var winners = votes.Where(kv => kv.Value == highest).Select(kv => kv.Key).ToList();
        if (winners.Count == 1)
            SetMaster(winners[0]);
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

    /// <summary>The stones this guild holds a relation record for.</summary>
    public IEnumerable<Serial> RelationStones => _relations.Keys;

    /// <summary>Declare war on another guild (sets WeDeclaredWar).</summary>
    public void DeclareWar(Serial otherGuildStone)
    {
        if (otherGuildStone == _stoneUid) return;
        GetOrCreateRelation(otherGuildStone).WeDeclaredWar = true;
    }

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

    public void AddAlly(Serial otherGuildStone)
    {
        if (otherGuildStone == _stoneUid) return;
        GetOrCreateRelation(otherGuildStone).WeDeclaredAlliance = true;
    }

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

    internal void SetTheyDeclaredWar(Serial otherStoneUid, bool declared)
    {
        var rel = declared ? GetOrCreateRelation(otherStoneUid) : GetRelation(otherStoneUid);
        if (rel == null) return;
        rel.TheyDeclaredWar = declared;
        CleanupRelation(otherStoneUid, rel);
    }

    internal void SetTheyDeclaredAlliance(Serial otherStoneUid, bool declared)
    {
        var rel = declared ? GetOrCreateRelation(otherStoneUid) : GetRelation(otherStoneUid);
        if (rel == null) return;
        rel.TheyDeclaredAlliance = declared;
        CleanupRelation(otherStoneUid, rel);
    }

    internal void RemoveRelation(Serial otherStoneUid) => _relations.Remove(otherStoneUid);

    internal void NormalizeMasters()
    {
        var fullMembers = _members
            .Where(m => m.Priv is GuildPriv.Member or GuildPriv.Master)
            .ToList();
        if (fullMembers.Count == 0) return;
        var master = fullMembers.FirstOrDefault(m => m.Priv == GuildPriv.Master) ?? fullMembers[0];
        foreach (var member in fullMembers)
            member.Priv = member == master ? GuildPriv.Master : GuildPriv.Member;
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

    /// <summary>Find which GUILD a character belongs to. Town records are
    /// excluded — this feeds guild chat, corpse-loot relations and notoriety,
    /// where a town citizenship must never masquerade as a guild membership
    /// (Source-X keeps MEMORY_GUILD and MEMORY_TOWN independent).</summary>
    public GuildDef? FindGuildFor(Serial charUid) =>
        _guilds.Values.FirstOrDefault(g => !g.IsTownStone && g.IsMember(charUid));

    /// <summary>Find which TOWN a character is a citizen of.</summary>
    public GuildDef? FindTownFor(Serial charUid) =>
        _guilds.Values.FirstOrDefault(g => g.IsTownStone && g.IsMember(charUid));

    /// <summary>Find a guild record including pending candidates.</summary>
    public GuildDef? FindGuildRecordFor(Serial charUid) =>
        _guilds.Values.FirstOrDefault(g => g.FindMember(charUid) != null);

    /// <summary>Membership lookup within ONE pool: town stones or guild
    /// stones. A guild member may still become a town citizen and vice versa
    /// (Source-X MEMORY_GUILD / MEMORY_TOWN are independent).</summary>
    public GuildDef? FindGuildRecordFor(Serial charUid, bool townStones) =>
        _guilds.Values.FirstOrDefault(g =>
            g.IsTownStone == townStones && g.FindMember(charUid) != null);

    /// <summary>The bracketed guild abbreviation a character shows after their
    /// overhead name (" [ABC]"), or empty when they are in no guild, the guild has
    /// no abbreviation, or the member has hidden it (TOGGLEABBREVIATION). Relationship
    /// (enemy/ally) member records are excluded — only real members display it.</summary>
    public string GetAbbrevSuffix(Serial charUid)
    {
        // Overhead abbreviation is a GUILD surface — town citizenship must
        // not leak a town tag onto the name.
        var guild = _guilds.Values.FirstOrDefault(g =>
            !g.IsTownStone && g.IsMember(charUid));
        if (guild == null || string.IsNullOrWhiteSpace(guild.Abbreviation))
            return "";
        var member = guild.FindMember(charUid);
        if (member == null || !guild.IsMember(charUid) || !member.ShowAbbrev)
            return "";
        return $" [{guild.Abbreviation}]";
    }

    public GuildDef CreateGuild(Serial stoneUid, string name, Serial masterUid,
        bool isTownStone = false)
    {
        if (_guilds.TryGetValue(stoneUid, out var existing))
            return existing;
        var guild = new GuildDef(stoneUid) { Name = name, IsTownStone = isTownStone };
        var master = guild.AddRecruit(masterUid);
        master.Priv = GuildPriv.Master;
        _guilds[stoneUid] = guild;
        return guild;
    }

    public void RemoveGuild(Serial stoneUid) => RemoveGuild(stoneUid, null);

    /// <summary>Take a guild out of the world. When the stone survives, the tags that
    /// would rebuild the guild on the next load go with it: a disbanded guild that left
    /// its GUILD.NAME / GUILD.MEMBERS behind came back at the next world load, with its
    /// old master and membership intact.</summary>
    public void RemoveGuild(Serial stoneUid, World.GameWorld? world)
    {
        if (!_guilds.Remove(stoneUid)) return;
        foreach (var guild in _guilds.Values)
            guild.RemoveRelation(stoneUid);

        if (world?.FindItem(stoneUid) is { } stone)
            ClearGuildTags(stone);
    }

    private static readonly string[] GuildTagKeys =
    [
        "GUILD.NAME", "GUILD.ABBREV", "GUILD.ISTOWN", "GUILD.WEB", "GUILD.CHARTER",
        "GUILD.ALIGN", "GUILD.MAXHOUSES", "GUILD.MAXSHIPS", "GUILD.MEMBERS",
        "GUILD.RELATIONS", "GUILD.WARS", "GUILD.ALLIES",
    ];

    private static void ClearGuildTags(Objects.Items.Item stone)
    {
        foreach (var key in GuildTagKeys)
            stone.RemoveTag(key);
    }

    /// <summary>A member has left a guild.
    ///
    /// Source-X takes the membership record out and holds a fresh election
    /// (CStoneMember.cpp:400 -> ElectMaster, CItemStone.cpp:1135): the departing
    /// member's vote goes with them, and so do the votes that were loyal to them.
    /// SphereNet promoted whoever came next in the list instead, which is not
    /// necessarily who the remaining votes support.
    ///
    /// When the last FULL member goes, the guild is at war with nobody: the reference
    /// forces peace on a stone with no members (CItemStone.cpp:1226 -> WeDeclarePeace,
    /// :1494), on both sides of every declaration.</summary>
    public bool MemberLeft(GuildDef guild, Serial charUid)
    {
        if (!guild.RemoveMember(charUid))
            return false;

        guild.ElectMaster();

        if (guild.GetMemberCount((int)GuildPriv.Member) + guild.GetMemberCount((int)GuildPriv.Master) == 0)
            ClearRelationsBothWays(guild);
        return true;
    }

    /// <summary>Drop every war and alliance this guild is part of, from both ends.</summary>
    private void ClearRelationsBothWays(GuildDef guild)
    {
        foreach (var otherStone in guild.RelationStones.ToArray())
        {
            guild.RemoveRelation(otherStone);
            if (_guilds.TryGetValue(otherStone, out var other))
                other.RemoveRelation(guild.StoneUid);
        }
    }

    /// <summary>The guild whose stone this is has gone from the world.
    ///
    /// Source-X ties the membership records to the stone's own lifetime (CItemStone
    /// destructor, CItemStone.cpp:30). SphereNet keeps the guild in a manager beside
    /// the world, so deleting the stone left a guild nobody could reach still answering
    /// membership questions.</summary>
    public void OnStoneDeleted(Serial stoneUid)
    {
        if (_guilds.TryGetValue(stoneUid, out var guild))
            ClearRelationsBothWays(guild);
        RemoveGuild(stoneUid);
    }

    public bool DeclareWar(Serial fromStoneUid, Serial toStoneUid)
    {
        if (fromStoneUid == toStoneUid || !_guilds.TryGetValue(fromStoneUid, out var from) ||
            !_guilds.TryGetValue(toStoneUid, out var to))
            return false;
        from.DeclareWar(toStoneUid);
        to.SetTheyDeclaredWar(fromStoneUid, true);
        return true;
    }

    public bool DeclarePeace(Serial fromStoneUid, Serial toStoneUid)
    {
        if (!_guilds.TryGetValue(fromStoneUid, out var from) ||
            !_guilds.TryGetValue(toStoneUid, out var to))
            return false;
        from.DeclarePeace(toStoneUid);
        to.SetTheyDeclaredWar(fromStoneUid, false);
        return true;
    }

    public bool DeclareAlliance(Serial fromStoneUid, Serial toStoneUid)
    {
        if (fromStoneUid == toStoneUid || !_guilds.TryGetValue(fromStoneUid, out var from) ||
            !_guilds.TryGetValue(toStoneUid, out var to))
            return false;
        from.AddAlly(toStoneUid);
        to.SetTheyDeclaredAlliance(fromStoneUid, true);
        return true;
    }

    public bool WithdrawAlliance(Serial fromStoneUid, Serial toStoneUid)
    {
        if (!_guilds.TryGetValue(fromStoneUid, out var from) ||
            !_guilds.TryGetValue(toStoneUid, out var to))
            return false;
        from.RemoveAlly(toStoneUid);
        to.SetTheyDeclaredAlliance(fromStoneUid, false);
        return true;
    }

    public IEnumerable<GuildDef> GetAllGuilds() => _guilds.Values;

    // A member title is free text and the record uses ':' and ',' as separators, so
    // both are escaped. The escape CHARACTER has to be escaped as well, or a title that
    // genuinely contains a backslash comes back as a separator: "Ranger\camp" used to
    // load as "Ranger:amp".
    private static string EscapeField(string? text) =>
        (text ?? "").Replace("\\", "\\e").Replace(":", "\\c").Replace(",", "\\m");

    private static string UnescapeField(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i + 1 >= raw.Length)
            {
                sb.Append(raw[i]);
                continue;
            }
            char code = raw[++i];
            sb.Append(code switch
            {
                'c' => ':',
                'm' => ',',
                'e' => '\\',
                // An older save wrote no escape for the backslash itself; keep the pair
                // as it stands rather than losing a character.
                _ => "\\" + code,
            });
        }
        return sb.ToString();
    }

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
            if (guild.IsTownStone)
                stone.SetTag("GUILD.ISTOWN", "1");
            else
                stone.RemoveTag("GUILD.ISTOWN");
            if (!string.IsNullOrEmpty(guild.WebUrl))
                stone.SetTag("GUILD.WEB", guild.WebUrl);
            else
                stone.RemoveTag("GUILD.WEB");
            if (!string.IsNullOrEmpty(guild.Charter))
                stone.SetTag("GUILD.CHARTER", guild.Charter);
            else
                stone.RemoveTag("GUILD.CHARTER");
            if (guild.Align != GuildAlign.Standard)
                stone.SetTag("GUILD.ALIGN", ((byte)guild.Align).ToString());
            else
                stone.RemoveTag("GUILD.ALIGN");

            // Guild-owned structures.
            if (guild.HouseCount > 0)
                stone.SetTag("GUILD.HOUSES", string.Join(",", guild.Houses.Select(u => $"0{u.Value:X}")));
            else
                stone.RemoveTag("GUILD.HOUSES");
            if (guild.ShipCount > 0)
                stone.SetTag("GUILD.SHIPS", string.Join(",", guild.Ships.Select(u => $"0{u.Value:X}")));
            else
                stone.RemoveTag("GUILD.SHIPS");
            if (guild.MaxHouses > 0)
                stone.SetTag("GUILD.MAXHOUSES", guild.MaxHouses.ToString());
            else
                stone.RemoveTag("GUILD.MAXHOUSES");
            if (guild.MaxShips > 0)
                stone.SetTag("GUILD.MAXSHIPS", guild.MaxShips.ToString());
            else
                stone.RemoveTag("GUILD.MAXSHIPS");

            // Members: uid:priv:title:accountgold:loyalto:showabbrev
            // Escape the field separator (:) AND the record separator (,) in the
            // free-text title, otherwise a comma in a player-set title splits the
            // record on load and drops the trailing members.
            var memberStrs = guild.Members.Select(m =>
                $"0{m.CharUid.Value:X}:{(byte)m.Priv}:{EscapeField(m.Title)}:{m.AccountGold}:{(m.LoyalTo == Serial.Invalid ? "0" : $"0{m.LoyalTo.Value:X}")}:{(m.ShowAbbrev ? "1" : "0")}");
            stone.SetTag("GUILD.MEMBERS", string.Join(",", memberStrs));

            // Relations: uid:wewar:theywar:weally:theyally
            if (guild.Relations.Count > 0)
            {
                var relStrs = guild.Relations.Values.Select(r =>
                    $"0{r.OtherStoneUid.Value:X}:{(r.WeDeclaredWar ? "1" : "0")}:{(r.TheyDeclaredWar ? "1" : "0")}:{(r.WeDeclaredAlliance ? "1" : "0")}:{(r.TheyDeclaredAlliance ? "1" : "0")}");
                stone.SetTag("GUILD.RELATIONS", string.Join(",", relStrs));
            }
            else
            {
                stone.RemoveTag("GUILD.RELATIONS");
            }

            // Legacy compat: also write GUILD.WARS/ALLIES for older saves
            var wars = guild.Wars.ToList();
            if (wars.Count > 0)
                stone.SetTag("GUILD.WARS", string.Join(",", wars.Select(s => $"0{s.Value:X}")));
            else
                stone.RemoveTag("GUILD.WARS");
            var allies = guild.Allies.ToList();
            if (allies.Count > 0)
                stone.SetTag("GUILD.ALLIES", string.Join(",", allies.Select(s => $"0{s.Value:X}")));
            else
                stone.RemoveTag("GUILD.ALLIES");
        }
    }

    /// <summary>Rebuild guilds from guild stone items after world load.</summary>
    public void DeserializeFromWorld(World.GameWorld world)
    {
        _guilds.Clear();
        // One claim set PER POOL: a character may be a guild member AND a
        // town citizen — a single shared set silently dropped the second
        // membership on reload (whichever stone loaded first won).
        var claimedGuildChars = new HashSet<uint>();
        var claimedTownChars = new HashSet<uint>();
        foreach (var obj in world.GetAllObjects().OrderBy(o => o.Uid.Value))
        {
            if (obj is not Objects.Items.Item item) continue;
            if (!item.TryGetTag("GUILD.NAME", out string? guildName)) continue;
            if (string.IsNullOrWhiteSpace(guildName)) continue;

            var safeName = guildName.Trim();
            var guild = new GuildDef(item.Uid)
            {
                Name = safeName[..Math.Min(40, safeName.Length)],
                // Town records stay in their own membership pool (tag first,
                // stone item type as the legacy fallback).
                IsTownStone = item.TryGetTag("GUILD.ISTOWN", out _) ||
                              item.ItemType == Core.Enums.ItemType.StoneTown,
            };

            // What the write side accepted, the read side keeps: cutting the
            // abbreviation to four characters and the charter to two hundred here
            // silently changed data that had been stored in full, so a guild's text
            // shrank every time the world was loaded. Any limit belongs where the text
            // is set, not where it is read back.
            if (item.TryGetTag("GUILD.ABBREV", out string? abbrev))
                guild.Abbreviation = (abbrev ?? "").Trim();
            if (item.TryGetTag("GUILD.CHARTER", out string? charter))
                guild.Charter = charter ?? "";
            if (item.TryGetTag("GUILD.WEB", out string? web))
                guild.WebUrl = web ?? "";
            if (item.TryGetTag("GUILD.ALIGN", out string? alignStr) &&
                byte.TryParse(alignStr, out byte alignVal) &&
                Enum.IsDefined((GuildAlign)alignVal))
                guild.Align = (GuildAlign)alignVal;

            // Guild-owned structures. Only re-link multis that still exist so a
            // decayed/deleted house or ship drops off the guild on reload.
            ParseSerialSet(item, "GUILD.HOUSES", uid =>
            {
                if (world.FindItem(uid) != null) guild.AddHouse(uid);
            });
            ParseSerialSet(item, "GUILD.SHIPS", uid =>
            {
                if (world.FindItem(uid) != null) guild.AddShip(uid);
            });
            if (item.TryGetTag("GUILD.MAXHOUSES", out string? maxHousesStr) &&
                int.TryParse(maxHousesStr, out int maxHouses) && maxHouses > 0)
                guild.MaxHouses = maxHouses;
            if (item.TryGetTag("GUILD.MAXSHIPS", out string? maxShipsStr) &&
                int.TryParse(maxShipsStr, out int maxShips) && maxShips > 0)
                guild.MaxShips = maxShips;

            // Parse members
            var claimedCharacters = guild.IsTownStone ? claimedTownChars : claimedGuildChars;
            if (item.TryGetTag("GUILD.MEMBERS", out string? membersStr) && !string.IsNullOrEmpty(membersStr))
            {
                foreach (var part in membersStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var segs = part.Split(':', 6);
                    if (segs.Length < 2) continue;
                    uint uid = ParseHexSerial(segs[0]);
                    if (uid == 0 || world.FindChar(new Serial(uid)) == null || claimedCharacters.Contains(uid))
                        continue;
                    byte priv = byte.TryParse(segs[1], out byte p) ? p : (byte)1;
                    if (priv != (byte)GuildPriv.Candidate && priv != (byte)GuildPriv.Member &&
                        priv != (byte)GuildPriv.Master && priv != (byte)GuildPriv.Accepted)
                        continue;
                    claimedCharacters.Add(uid);
                    string title = segs.Length > 2 ? UnescapeField(segs[2]) : "";

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
                    if (ruid == 0 || ruid == item.Uid.Value) continue;
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

            guild.NormalizeMasters();
            _guilds[item.Uid] = guild;
        }

        // Repair/migrate the mirrored directional flags. Older SphereNet saves
        // wrote only each side's "we" bit, so mutual wars/alliances could never
        // become active after reload.
        foreach (var guild in _guilds.Values.ToList())
        {
            foreach (var relation in guild.Relations.Values.ToList())
            {
                if (!_guilds.TryGetValue(relation.OtherStoneUid, out var other) || other == guild)
                {
                    guild.RemoveRelation(relation.OtherStoneUid);
                    continue;
                }
                var reverse = other.GetOrCreateRelation(guild.StoneUid);
                if (relation.WeDeclaredWar) reverse.TheyDeclaredWar = true;
                if (relation.TheyDeclaredWar) reverse.WeDeclaredWar = true;
                if (relation.WeDeclaredAlliance) reverse.TheyDeclaredAlliance = true;
                if (relation.TheyDeclaredAlliance) reverse.WeDeclaredAlliance = true;
            }
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
