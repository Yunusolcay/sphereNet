using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Party;

/// <summary>
/// Party system. Maps to CPartyDef in Source-X CParty.h/cpp.
/// Manages party membership, loot rights, and party chat.
/// Max 10 members per Source-X.
/// </summary>
public sealed class PartyDef
{
    public const int MaxPartySize = 10;

    private readonly List<Serial> _members = [];
    private Serial _master = Serial.Invalid;
    private readonly HashSet<Serial> _lootRights = []; // members who share loot
    private readonly Dictionary<string, string> _tags = new(StringComparer.OrdinalIgnoreCase);

    public Serial Master => _master;
    public IReadOnlyList<Serial> Members => _members;
    public int MemberCount => _members.Count;
    public bool IsFull => _members.Count >= MaxPartySize;

    /// <summary>Create a new party with the given master.</summary>
    public PartyDef(Serial master)
    {
        _master = master;
        _members.Add(master);
    }

    /// <summary>Invite and add a member. Returns false if full or duplicate.</summary>
    public bool AddMember(Serial uid)
    {
        if (!uid.IsValid || IsFull || _members.Contains(uid))
            return false;
        _members.Add(uid);
        return true;
    }

    /// <summary>Remove a member. If master leaves, promotes next member.</summary>
    public bool RemoveMember(Serial uid)
    {
        if (!_members.Remove(uid))
            return false;
        _lootRights.Remove(uid);

        if (uid == _master && _members.Count > 0)
            _master = _members[0];

        return true;
    }

    /// <summary>Disband the entire party.</summary>
    public void Disband()
    {
        _members.Clear();
        _lootRights.Clear();
        _master = Serial.Invalid;
    }

    public bool IsMember(Serial uid) => _members.Contains(uid);

    /// <summary>Toggle loot sharing for a member.</summary>
    public void SetLootFlag(Serial uid, bool canLoot)
    {
        if (!_members.Contains(uid))
            return;
        if (canLoot)
            _lootRights.Add(uid);
        else
            _lootRights.Remove(uid);
    }

    public bool GetLootFlag(Serial uid) => _lootRights.Contains(uid);

    /// <summary>Change party master.
    ///
    /// Source-X moves the new leader to the FRONT of the member list (SetMaster,
    /// CParty.cpp:62): the list is what indexed access and the member packets read, so
    /// leaving it alone made PARTY.MEMBER.0 and PARTY.MASTER name two different
    /// people.</summary>
    public void SetMaster(Serial uid)
    {
        if (!_members.Contains(uid))
            return;
        _master = uid;
        _members.Remove(uid);
        _members.Insert(0, uid);
    }

    // --- TAG system ---

    public void SetTag(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            _tags.Remove(key);
        else
            _tags[key] = value;
    }

    public bool TryGetTag(string key, out string value) => _tags.TryGetValue(key, out value!);

    public void RemoveTag(string key) => _tags.Remove(key);

    public void ClearTags() => _tags.Clear();

    public int TagCount => _tags.Count;

    public (string Key, string Value) TagAt(int index)
    {
        if (index < 0 || index >= _tags.Count) return ("", "");
        var kvp = _tags.ElementAt(index);
        return (kvp.Key, kvp.Value);
    }

    /// <summary>Force-add a member, removing from existing party. Still respects max size.</summary>
    public bool AddMemberForced(Serial uid)
    {
        if (_members.Contains(uid)) return true;
        if (IsFull) return false;
        _members.Add(uid);
        return true;
    }
}

/// <summary>
/// Manages all active parties on the server.
/// </summary>
public sealed class PartyManager
{
    private readonly List<PartyDef> _parties = [];

    /// <summary>Every active party — used by the periodic party stats push
    /// (Source-X CPartyDef::AddStatsUpdate streams member HP bars).</summary>
    public IReadOnlyList<PartyDef> Parties => _parties;

    /// <summary>Find the party a character belongs to.</summary>
    public PartyDef? FindParty(Serial charUid) =>
        _parties.FirstOrDefault(p => p.IsMember(charUid));

    /// <summary>Create a new party with the given master.</summary>
    public PartyDef CreateParty(Serial masterUid)
    {
        var existing = FindParty(masterUid);
        if (existing != null)
            return existing;
        var party = new PartyDef(masterUid);
        _parties.Add(party);
        return party;
    }

    /// <summary>Handle an invitation accept.</summary>
    public bool AcceptInvite(Serial masterUid, Serial memberUid)
    {
        if (!masterUid.IsValid || !memberUid.IsValid || masterUid == memberUid)
            return false;
        // Already in a party — must leave first
        if (FindParty(memberUid) != null)
            return false;

        var party = FindParty(masterUid);
        if (party == null)
            party = CreateParty(masterUid);
        else if (party.Master != masterUid)
            return false;

        return party.AddMember(memberUid);
    }

    /// <summary>Handle a member leaving.</summary>
    public void Leave(Serial charUid)
    {
        var party = FindParty(charUid);
        if (party == null) return;

        party.RemoveMember(charUid);

        if (party.MemberCount <= 1)
        {
            party.Disband();
            _parties.Remove(party);
        }
    }

    /// <summary>Disband a party (master only or GM).</summary>
    public void Disband(Serial masterUid)
    {
        var party = FindParty(masterUid);
        if (party == null || party.Master != masterUid) return;

        party.Disband();
        _parties.Remove(party);
    }

    /// <summary>Force-add a target into a character's party, removing target from any existing party.</summary>
    public PartyDef ForceAddMember(Serial charUid, Serial targetUid)
    {
        var destination = FindParty(charUid);
        if (charUid == targetUid)
            return destination ?? CreateParty(charUid);
        if (destination != null && destination.IsMember(targetUid))
            return destination;
        if (destination != null && destination.IsFull)
            return destination;

        // Remove target from any existing party
        var existingParty = FindParty(targetUid);
        if (existingParty != null)
        {
            existingParty.RemoveMember(targetUid);
            if (existingParty.MemberCount <= 1)
            {
                existingParty.Disband();
                _parties.Remove(existingParty);
            }
        }

        // Find or create the char's party
        var party = FindParty(charUid) ?? CreateParty(charUid);
        party.AddMemberForced(targetUid);
        return party;
    }

    public int ActivePartyCount => _parties.Count;
}
