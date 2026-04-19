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
        if (IsFull || _members.Contains(uid))
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
        if (canLoot)
            _lootRights.Add(uid);
        else
            _lootRights.Remove(uid);
    }

    public bool GetLootFlag(Serial uid) => _lootRights.Contains(uid);

    /// <summary>Change party master.</summary>
    public void SetMaster(Serial uid)
    {
        if (_members.Contains(uid))
            _master = uid;
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

    /// <summary>Force-add a member, bypassing size limit and removing from existing party.</summary>
    public void AddMemberForced(Serial uid)
    {
        if (!_members.Contains(uid))
            _members.Add(uid);
    }
}

/// <summary>
/// Manages all active parties on the server.
/// </summary>
public sealed class PartyManager
{
    private readonly List<PartyDef> _parties = [];

    /// <summary>Find the party a character belongs to.</summary>
    public PartyDef? FindParty(Serial charUid) =>
        _parties.FirstOrDefault(p => p.IsMember(charUid));

    /// <summary>Create a new party with the given master.</summary>
    public PartyDef CreateParty(Serial masterUid)
    {
        var party = new PartyDef(masterUid);
        _parties.Add(party);
        return party;
    }

    /// <summary>Handle an invitation accept.</summary>
    public bool AcceptInvite(Serial masterUid, Serial memberUid)
    {
        var party = FindParty(masterUid);
        if (party == null)
            party = CreateParty(masterUid);

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
