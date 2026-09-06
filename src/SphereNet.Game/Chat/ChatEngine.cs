using SphereNet.Core.Types;

namespace SphereNet.Game.Chat;

/// <summary>A chat conference channel: its members and moderation state
/// (owner, moderators, voiced members, password, default-voice).</summary>
public sealed class ChatChannel
{
    public required string Name { get; set; }
    /// <summary>Static channels survive emptying; ad-hoc ones are removed
    /// when the last member leaves.</summary>
    public bool IsStatic { get; init; }
    /// <summary>The member who created the channel — always a moderator.</summary>
    public Serial Owner { get; set; } = Serial.Invalid;
    /// <summary>Channel password (null/empty = open). Joining requires a match.</summary>
    public string? Password { get; set; }
    /// <summary>When true (default) every member may talk; when false only
    /// moderators and explicitly voiced members may.</summary>
    public bool DefaultVoice { get; set; } = true;

    private readonly List<Serial> _members = [];
    private readonly HashSet<Serial> _moderators = [];
    private readonly HashSet<Serial> _voiced = [];

    public IReadOnlyList<Serial> Members => _members;
    public bool HasPassword => !string.IsNullOrEmpty(Password);

    /// <summary>Whether this member holds the moderator role.
    ///
    /// Founding a channel is not a permanent rank: Source-X puts the creator in the
    /// moderator LIST like anyone else, so another moderator can revoke it
    /// (RevokeModerator, CChatChannel.cpp:517). Reading it off the Owner field made the
    /// founder's moderation impossible to remove - the call succeeded and changed
    /// nothing.</summary>
    public bool IsModerator(Serial uid) => _moderators.Contains(uid);
    public bool IsVoiced(Serial uid) => _voiced.Contains(uid);

    /// <summary>Whether a member may talk here.
    ///
    /// Source-X asks the member's OWN no-voice record (HasVoice,
    /// CChatChannel.cpp:272); the channel default only decides what a member starts
    /// with. Recomputing it from the default on every message meant flipping the
    /// default silenced everyone who was already talking - without anyone being
    /// individually muted.</summary>
    public bool CanSpeak(Serial uid) => IsModerator(uid) || _voiced.Contains(uid);

    /// <summary>0xB2 AddUser userType for a member: 1 = moderator, 2 = voiced
    /// (when the channel is moderated), 0 = ordinary speaker.</summary>
    public ushort UserType(Serial uid)
    {
        if (IsModerator(uid)) return 1;
        if (!DefaultVoice && _voiced.Contains(uid)) return 2;
        return 0;
    }

    /// <summary>Give the founder the moderator record every other moderator has, so
    /// the role can be granted and revoked uniformly.</summary>
    internal void SeatFounder(Serial uid)
    {
        Owner = uid;
        _moderators.Add(uid);
        _voiced.Add(uid);
    }

    internal bool Add(Serial uid)
    {
        if (_members.Contains(uid))
            return false;
        _members.Add(uid);
        // A member arrives with the voice the channel currently hands out; taking the
        // default away later does not reach back and mute them.
        if (DefaultVoice)
            _voiced.Add(uid);
        return true;
    }

    internal bool Remove(Serial uid)
    {
        _moderators.Remove(uid);
        _voiced.Remove(uid);
        return _members.Remove(uid);
    }

    internal void SetModerator(Serial uid, bool on)
    {
        if (on) _moderators.Add(uid); else _moderators.Remove(uid);
    }

    internal void SetVoice(Serial uid, bool on)
    {
        if (on) _voiced.Add(uid); else _voiced.Remove(uid);
    }
}

/// <summary>
/// UO chat (conference) system state: channels, membership, moderation and talk
/// routing. Maps to the OSI chat system the client drives with 0xB3/0xB5 and
/// renders from 0xB2. Packet work stays in GameClient; this class is pure state.
/// </summary>
public sealed class ChatEngine
{
    private readonly Dictionary<string, ChatChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Serial, ChatChannel> _memberChannel = [];
    /// <summary>Per-character chat display name ("chat handle").</summary>
    private readonly Dictionary<Serial, string> _chatNames = [];
    /// <summary>Per-character ignore set: characters whose private messages (and
    /// channel lines, at the client) the owner has chosen not to receive.</summary>
    private readonly Dictionary<Serial, HashSet<Serial>> _ignored = [];

    public ChatEngine(params string[] staticChannels)
    {
        foreach (var name in staticChannels.Length > 0 ? staticChannels : ["General"])
            _channels[name] = new ChatChannel { Name = name, IsStatic = true };
    }

    public IReadOnlyCollection<ChatChannel> Channels => _channels.Values;

    public ChatChannel? GetChannel(string name) => _channels.GetValueOrDefault(name);

    public ChatChannel? GetMemberChannel(Serial uid) => _memberChannel.GetValueOrDefault(uid);

    public string GetChatName(Serial uid) => _chatNames.GetValueOrDefault(uid, "");

    /// <summary>Everyone with the chat window open - the audience for a channel-list
    /// change, which Source-X announces globally rather than per channel
    /// (CChat.cpp:48).</summary>
    public IReadOnlyCollection<Serial> Participants => _chatNames.Keys;

    public bool Exists(string channelName) => _channels.ContainsKey(channelName);

    public void SetChatName(Serial uid, string name) => _chatNames[uid] = name;

    /// <summary>Resolve a chat handle back to its character uid (for whispers).</summary>
    public Serial FindByChatName(string name)
    {
        foreach (var kv in _chatNames)
            if (string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase))
                return kv.Key;
        return Serial.Invalid;
    }

    // ---- Ignore list (per character, channel-independent) ----

    /// <summary>Whether <paramref name="owner"/> is ignoring <paramref name="other"/>'s
    /// messages.</summary>
    public bool IsIgnoring(Serial owner, Serial other) =>
        _ignored.TryGetValue(owner, out var set) && set.Contains(other);

    /// <summary>Add/remove a character to the owner's ignore set. Returns true when the
    /// set actually changed. A character cannot ignore itself.</summary>
    public bool SetIgnored(Serial owner, Serial other, bool on)
    {
        if (!other.IsValid || other == owner)
            return false;
        if (on)
        {
            if (!_ignored.TryGetValue(owner, out var set))
                _ignored[owner] = set = [];
            return set.Add(other);
        }
        return _ignored.TryGetValue(owner, out var existing) && existing.Remove(other);
    }

    /// <summary>Flip the ignore state for <paramref name="other"/>. Returns the new
    /// state (true = now ignored).</summary>
    public bool ToggleIgnored(Serial owner, Serial other)
    {
        bool now = !IsIgnoring(owner, other);
        SetIgnored(owner, other, now);
        return now;
    }

    /// <summary>Enter a channel. A member can be in one channel at a time - a
    /// successful switch leaves the previous one, a refused one changes nothing.
    /// <paramref name="create"/> tells the two client commands apart: creating needs
    /// the name to be free, joining needs the channel to exist and the password to
    /// match. Returns the channel, or null when the request is refused.</summary>
    public ChatChannel? Join(Serial uid, string channelName, string? password = null,
        bool create = false)
    {
        channelName = channelName.Trim();
        if (channelName.Length == 0)
            return null;

        // Source-X keeps CreateChannel and JoinChannel apart (CChat.cpp:12/70): a join
        // needs the channel to exist, a create needs the name to be free, and neither
        // silently becomes the other. Sending both commands down one auto-creating Join
        // meant picking a dead channel out of a stale list quietly resurrected it, and
        // creating a channel someone else already owned just walked into their
        // conversation.
        bool exists = _channels.TryGetValue(channelName, out var channel);
        if (create)
        {
            if (exists)
                return null;    // that name is taken
        }
        else
        {
            if (!exists)
                return null;    // there is no such channel
            if (channel!.HasPassword && !string.Equals(channel.Password, password, StringComparison.Ordinal))
                return null;    // wrong/missing password
        }

        // Only once the switch is known to be allowed does the old membership go.
        Leave(uid);
        if (!exists)
        {
            channel = new ChatChannel
            {
                Name = channelName,
                Password = string.IsNullOrEmpty(password) ? null : password,
            };
            _channels[channelName] = channel;
            channel.SeatFounder(uid);
        }
        channel!.Add(uid);
        _memberChannel[uid] = channel;
        return channel;
    }

    /// <summary>Leave the current channel. Empty ad-hoc channels are removed.
    /// Returns the channel that was left, or null.</summary>
    public ChatChannel? Leave(Serial uid)
    {
        if (!_memberChannel.TryGetValue(uid, out var channel))
            return null;
        _memberChannel.Remove(uid);
        channel.Remove(uid);
        if (!channel.IsStatic && channel.Members.Count == 0)
            _channels.Remove(channel.Name);
        return channel;
    }

    // ---- Moderation (the actor must be a moderator of the channel they are in) ----

    private ChatChannel? ModeratedChannel(Serial mod) =>
        GetMemberChannel(mod) is { } c && c.IsModerator(mod) ? c : null;

    /// <summary>Kick a member out of the actor's channel. Returns the kicked
    /// member's channel (so the caller can notify), or null if not permitted.</summary>
    public ChatChannel? Kick(Serial mod, Serial target)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null || target == mod || !channel.Members.Contains(target))
            return null;
        // Leave operates on the target's own membership entry.
        Leave(target);
        return channel;
    }

    public bool SetModerator(Serial mod, Serial target, bool on)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null || !channel.Members.Contains(target)) return false;
        channel.SetModerator(target, on);
        return true;
    }

    public bool SetVoice(Serial mod, Serial target, bool on)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null || !channel.Members.Contains(target)) return false;
        channel.SetVoice(target, on);
        return true;
    }

    public bool SetDefaultVoice(Serial mod, bool on)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null) return false;
        channel.DefaultVoice = on;
        return true;
    }

    public bool SetPassword(Serial mod, string? password)
    {
        var channel = ModeratedChannel(mod);
        if (channel == null) return false;
        channel.Password = string.IsNullOrEmpty(password) ? null : password;
        return true;
    }

    /// <summary>Rename the actor's channel. Returns the channel on success (re-keyed),
    /// null if not permitted or the new name is taken/empty.</summary>
    public ChatChannel? Rename(Serial mod, string newName) => Rename(mod, newName, out _);

    /// <summary>Rename the actor's channel, reporting the name it used to have: every
    /// open chat window is still showing the old one.</summary>
    public ChatChannel? Rename(Serial mod, string newName, out string oldName)
    {
        oldName = "";
        newName = newName.Trim();
        var channel = ModeratedChannel(mod);
        if (channel == null || newName.Length == 0 || channel.IsStatic ||
            _channels.ContainsKey(newName))
            return null;
        oldName = channel.Name;
        _channels.Remove(channel.Name);
        channel.Name = newName;
        _channels[newName] = channel;
        return channel;
    }
}
