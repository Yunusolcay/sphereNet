using SphereNet.Core.Types;
using SphereNet.Game.Chat;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Clients;

public sealed partial class GameClient
{
    /// <summary>
    /// 0xB5 — the player opened the chat window. The character's own name is
    /// accepted as the chat handle (no separate username prompt) and the
    /// channel list is sent; the client auto-joins "General" on acceptance.
    /// </summary>
    public void HandleChatOpen()
    {
        if (_character == null || _chatEngine == null)
            return;
        string chatName = _character.Name ?? "Player";
        _chatEngine.SetChatName(_character.Uid, chatName);
        Send(PacketChatSystem.MakeUsernameAccepted(chatName));
        foreach (var channel in _chatEngine.Channels)
            Send(PacketChatSystem.MakeCreateChannel(channel.Name, channel.HasPassword));
    }

    /// <summary>0xB3 chat actions. Beyond talk/join/create/leave this handles the
    /// conference moderation set: rename, password, kick, moderator/voice grants,
    /// default-voice toggles and emote.</summary>
    public void HandleChatAction(ushort cmd, string text)
    {
        if (_character == null || _chatEngine == null)
            return;

        switch (cmd)
        {
            case 0x62: // join an existing channel: "Name" password
            {
                var (name, password) = ParseJoinCommand(text);
                ChatJoinChannel(name, password, create: false);
                break;
            }
            case 0x63: // create a new channel: Name{password}
            {
                var (name, password) = ParseCreateCommand(text);
                ChatJoinChannel(name, password, create: true);
                break;
            }
            case 0x43: // leave current channel
            case 0x58: // leave chat entirely (OSI alias)
                ChatLeaveChannel();
                break;
            case 0x61: // talk to the current channel
                ChatTalk(text, emote: false);
                break;
            case 0x7A: // emote to the current channel
                ChatTalk(text, emote: true);
                break;
            case 0x65: // private message: "<recipient> <text>"
                ChatPrivateMessage(text);
                break;
            case 0x66: // add to ignore list
                ChatSetIgnore(text, ChatIgnoreAction.Add);
                break;
            case 0x67: // remove from ignore list
                ChatSetIgnore(text, ChatIgnoreAction.Remove);
                break;
            case 0x68: // toggle ignore
                ChatSetIgnore(text, ChatIgnoreAction.Toggle);
                break;
            case 0x64: // rename the current channel (moderator)
                ChatRename(ParseCreateCommand(text).Name);
                break;
            case 0x41: // change channel password (moderator)
                _chatEngine.SetPassword(_character.Uid, ParseCreateCommand(text).Password ?? text.Trim());
                break;
            case 0x76: // kick a member (moderator)
                ChatModerateTarget(text, ChatModAction.Kick);
                break;
            case 0x6C: // add moderator
                ChatModerateTarget(text, ChatModAction.AddModerator);
                break;
            case 0x6D: // remove moderator
                ChatModerateTarget(text, ChatModAction.RemoveModerator);
                break;
            case 0x6E: // toggle moderator
                ChatModerateTarget(text, ChatModAction.ToggleModerator);
                break;
            case 0x69: // grant voice
                ChatModerateTarget(text, ChatModAction.AddVoice);
                break;
            case 0x6A: // revoke voice
                ChatModerateTarget(text, ChatModAction.RemoveVoice);
                break;
            case 0x6B: // toggle voice
                ChatModerateTarget(text, ChatModAction.ToggleVoice);
                break;
            case 0x77: // enable default voice (everyone may talk)
                _chatEngine.SetDefaultVoice(_character.Uid, true);
                break;
            case 0x78: // disable default voice (only moderators/voiced may talk)
                _chatEngine.SetDefaultVoice(_character.Uid, false);
                break;
            case 0x79: // toggle default voice
                ChatToggleDefaultVoice();
                break;
        }
    }

    /// <summary>Drop chat membership on disconnect so channels don't hold
    /// stale entries.</summary>
    public void ChatOnDisconnect()
    {
        if (_character != null)
            ChatLeaveChannel();
    }

    private void ChatJoinChannel(string channelName, string? password, bool create)
    {
        if (_character == null || _chatEngine == null || channelName.Length == 0)
            return;

        // Source-X checks the destination FIRST and only then leaves the old channel
        // (JoinChannel, CChat.cpp:70). Announcing the departure up front meant a wrong
        // password left the speaker in no channel at all - and took a one-member
        // channel, with its password and moderators, down with it.
        var previous = _chatEngine.GetMemberChannel(_character.Uid);
        var channel = _chatEngine.Join(_character.Uid, channelName, password, create);
        if (channel == null)
            return; // no such channel, name taken, or wrong password

        if (previous != null && previous != channel)
        {
            string leavingName = _chatEngine.GetChatName(_character.Uid);
            Send(PacketChatSystem.MakeLeftChannel(previous.Name));
            foreach (var memberUid in previous.Members)
                SendToChar?.Invoke(memberUid, PacketChatSystem.MakeRemoveUser(leavingName));
        }

        // A channel that has just come into being, or one that has just gone, changes
        // the list every open chat window is showing (BroadcastAddChannel,
        // CChat.cpp:48).
        if (create)
            AnnounceToChat(PacketChatSystem.MakeCreateChannel(channel.Name, channel.HasPassword));
        if (previous != null && previous != channel && !_chatEngine.Exists(previous.Name))
            AnnounceToChat(PacketChatSystem.MakeRemoveChannel(previous.Name));

        string myName = _chatEngine.GetChatName(_character.Uid);

        Send(PacketChatSystem.MakeJoinedChannel(channel.Name));
        Send(PacketChatSystem.MakeClearUsers());
        foreach (var memberUid in channel.Members)
        {
            string memberName = _chatEngine.GetChatName(memberUid);
            if (memberName.Length == 0)
                continue;
            Send(PacketChatSystem.MakeAddUser(memberName, channel.UserType(memberUid)));
            if (memberUid != _character.Uid)
                SendToChar?.Invoke(memberUid, PacketChatSystem.MakeAddUser(myName, channel.UserType(_character.Uid)));
        }
    }

    private void ChatLeaveChannel()
    {
        if (_character == null || _chatEngine == null)
            return;
        var left = _chatEngine.Leave(_character.Uid);
        if (left == null)
            return;
        string myName = _chatEngine.GetChatName(_character.Uid);
        Send(PacketChatSystem.MakeLeftChannel(left.Name));
        foreach (var memberUid in left.Members)
            SendToChar?.Invoke(memberUid, PacketChatSystem.MakeRemoveUser(myName));
    }

    private void ChatTalk(string text, bool emote)
    {
        if (_character == null || _chatEngine == null || string.IsNullOrWhiteSpace(text))
            return;
        var channel = _chatEngine.GetMemberChannel(_character.Uid);
        if (channel == null || !channel.CanSpeak(_character.Uid))
            return; // not in a channel, or no voice in a moderated channel
        string myName = _chatEngine.GetChatName(_character.Uid);
        if (text.Length > 256)
            text = text[..256];

        foreach (var memberUid in channel.Members)
        {
            if (memberUid == _character.Uid)
            {
                Send(PacketChatSystem.MakeChannelMessage(myName, text, emote));
            }
            else if (!_chatEngine.IsIgnoring(memberUid, _character.Uid))
            {
                SendToChar?.Invoke(memberUid, PacketChatSystem.MakeChannelMessage(myName, text, emote));
            }
        }
    }

    private enum ChatModAction { Kick, AddModerator, RemoveModerator, ToggleModerator, AddVoice, RemoveVoice, ToggleVoice }

    private void ChatModerateTarget(string targetName, ChatModAction action)
    {
        if (_character == null || _chatEngine == null)
            return;
        var target = _chatEngine.FindByChatName(ParseCreateCommand(targetName).Name);
        if (!target.IsValid)
            return;

        // The actor's own channel decides the current role state for the toggles.
        var actorChannel = _chatEngine.GetMemberChannel(_character.Uid);

        switch (action)
        {
            case ChatModAction.Kick:
            {
                var channel = _chatEngine.Kick(_character.Uid, target);
                if (channel == null) return;
                string kickedName = _chatEngine.GetChatName(target);
                // Tell the kicked client it left, and every remaining member it's gone.
                SendToChar?.Invoke(target, PacketChatSystem.MakeLeftChannel(channel.Name));
                foreach (var m in channel.Members)
                    SendToChar?.Invoke(m, PacketChatSystem.MakeRemoveUser(kickedName));
                break;
            }
            case ChatModAction.AddModerator:
            case ChatModAction.RemoveModerator:
            case ChatModAction.ToggleModerator:
            {
                bool on = action == ChatModAction.ToggleModerator
                    ? actorChannel == null || !actorChannel.IsModerator(target)
                    : action == ChatModAction.AddModerator;
                if (_chatEngine.SetModerator(_character.Uid, target, on))
                    ChatRefreshUserType(target);
                break;
            }
            case ChatModAction.AddVoice:
            case ChatModAction.RemoveVoice:
            case ChatModAction.ToggleVoice:
            {
                bool on = action == ChatModAction.ToggleVoice
                    ? actorChannel == null || !actorChannel.IsVoiced(target)
                    : action == ChatModAction.AddVoice;
                if (_chatEngine.SetVoice(_character.Uid, target, on))
                    ChatRefreshUserType(target);
                break;
            }
        }
    }

    private void ChatToggleDefaultVoice()
    {
        if (_character == null || _chatEngine == null)
            return;
        var channel = _chatEngine.GetMemberChannel(_character.Uid);
        if (channel != null)
            _chatEngine.SetDefaultVoice(_character.Uid, !channel.DefaultVoice);
    }

    private enum ChatIgnoreAction { Add, Remove, Toggle }

    /// <summary>0x66/0x67/0x68 — manage the player's ignore list by chat handle.</summary>
    private void ChatSetIgnore(string targetName, ChatIgnoreAction action)
    {
        if (_character == null || _chatEngine == null)
            return;
        var target = _chatEngine.FindByChatName(ParseCreateCommand(targetName).Name);
        if (!target.IsValid || target == _character.Uid)
            return;
        switch (action)
        {
            case ChatIgnoreAction.Add: _chatEngine.SetIgnored(_character.Uid, target, true); break;
            case ChatIgnoreAction.Remove: _chatEngine.SetIgnored(_character.Uid, target, false); break;
            case ChatIgnoreAction.Toggle: _chatEngine.ToggleIgnored(_character.Uid, target); break;
        }
    }

    /// <summary>0x65 — a private message. The payload is the recipient's chat handle,
    /// a space, then the text. Delivery is suppressed when the recipient is ignoring
    /// the sender; the sender sees their own copy regardless.</summary>
    private void ChatPrivateMessage(string raw)
    {
        if (_character == null || _chatEngine == null)
            return;
        string s = raw.Trim();
        int space = s.IndexOf(' ');
        if (space <= 0)
            return; // need both a recipient and a message
        string targetName = s[..space].Trim();
        string text = s[(space + 1)..].Trim();
        if (text.Length == 0)
            return;
        if (text.Length > 256)
            text = text[..256];

        var target = _chatEngine.FindByChatName(targetName);
        if (!target.IsValid || target == _character.Uid)
            return;

        string myName = _chatEngine.GetChatName(_character.Uid);
        // The sender always sees what they sent.
        Send(PacketChatSystem.MakeChannelMessage(myName, text));
        // The recipient receives it unless they have ignored the sender.
        if (!_chatEngine.IsIgnoring(target, _character.Uid))
            SendToChar?.Invoke(target, PacketChatSystem.MakeChannelMessage(myName, text));
    }

    /// <summary>Re-send a member's AddUser to the channel so clients update the
    /// moderator/voice marker beside the name.</summary>
    private void ChatRefreshUserType(Serial target)
    {
        if (_chatEngine == null) return;
        var channel = _chatEngine.GetMemberChannel(target);
        if (channel == null) return;
        string name = _chatEngine.GetChatName(target);
        if (name.Length == 0) return;
        var pkt = PacketChatSystem.MakeAddUser(name, channel.UserType(target));
        foreach (var m in channel.Members)
            SendToChar?.Invoke(m, pkt);
    }

    private void ChatRename(string newName)
    {
        if (_character == null || _chatEngine == null || newName.Length == 0)
            return;
        var channel = _chatEngine.Rename(_character.Uid, newName, out string oldName);
        if (channel == null)
            return;
        // The client tracks channels by name: re-advertise as remove-then-create.
        foreach (var m in channel.Members)
        {
            SendToChar?.Invoke(m, PacketChatSystem.MakeJoinedChannel(channel.Name));
        }
        // And everyone ELSE has the old name in their channel list (RenameChannel does
        // a global remove/add, CChatChannel.cpp:138).
        AnnounceToChat(PacketChatSystem.MakeRemoveChannel(oldName));
        AnnounceToChat(PacketChatSystem.MakeCreateChannel(channel.Name, channel.HasPassword));
    }

    /// <summary>Tell every chat participant about a channel-list change. Source-X
    /// announces a create, a removal and a rename to all of them, not only to the
    /// members of the channel in question (CChat.cpp:48; RenameChannel,
    /// CChatChannel.cpp:138).</summary>
    private void AnnounceToChat(SphereNet.Network.Packets.Outgoing.PacketChatSystem packet)
    {
        if (_chatEngine == null || SendToChar == null)
            return;
        foreach (var participant in _chatEngine.Participants.ToArray())
            SendToChar(participant, packet);
    }

    /// <summary>The JOIN command's shape: <c>"Name" password</c> - the name inside the
    /// quotes, the password after them (Source-X CChat.cpp:169). The old parser threw
    /// the tail away and then looked for braces in what was left, so the standard client
    /// could never join a password-protected channel with the right password.</summary>
    private static (string Name, string? Password) ParseJoinCommand(string raw)
    {
        string s = raw.Trim();
        int firstQuote = s.IndexOf('"');
        if (firstQuote < 0)
            return ParseCreateCommand(s);   // an unquoted name, brace form and all

        int secondQuote = s.IndexOf('"', firstQuote + 1);
        if (secondQuote < 0)
            return (s[(firstQuote + 1)..].Trim(), null);

        string name = s[(firstQuote + 1)..secondQuote].Trim();
        string tail = s[(secondQuote + 1)..].Trim();
        return (name, string.IsNullOrEmpty(tail) ? null : tail);
    }

    /// <summary>The CREATE command's shape: <c>Name{password}</c>.</summary>
    private static (string Name, string? Password) ParseCreateCommand(string raw)
    {
        string s = raw.Trim();
        int firstQuote = s.IndexOf('"');
        if (firstQuote >= 0)
        {
            int secondQuote = s.IndexOf('"', firstQuote + 1);
            s = secondQuote > firstQuote
                ? s[(firstQuote + 1)..secondQuote]
                : s[(firstQuote + 1)..];
        }
        string? password = null;
        int brace = s.IndexOf('{');
        if (brace >= 0)
        {
            int closeBrace = s.IndexOf('}', brace + 1);
            password = closeBrace > brace ? s[(brace + 1)..closeBrace] : s[(brace + 1)..];
            s = s[..brace];
        }
        return (s.Trim(), string.IsNullOrEmpty(password) ? null : password);
    }
}
