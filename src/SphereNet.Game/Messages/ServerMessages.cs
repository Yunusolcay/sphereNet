using System.Text.RegularExpressions;

namespace SphereNet.Game.Messages;

/// <summary>
/// Central registry for server messages. Provides default values that can be
/// overridden by DEFMESSAGE entries in sphere_msgs.scp (or any script file).
/// Maps to the defmessages.tbl / DEFMSG_ system in Source-X.
/// </summary>
public static class ServerMessages
{
    private static readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default system message color (SMSG_DEF_COLOR).</summary>
    public static ushort DefaultColor { get; private set; } = 0x0035;

    /// <summary>Default system message font (SMSG_DEF_FONT).</summary>
    public static byte DefaultFont { get; private set; } = 3;

    static ServerMessages()
    {
        RegisterDefaults();
    }

    /// <summary>
    /// Get a message by key. Returns override if present, otherwise default.
    /// If key is unknown, returns the key itself.
    /// </summary>
    public static string Get(string key)
    {
        if (_overrides.TryGetValue(key, out var ov))
            return ov;
        if (_defaults.TryGetValue(key, out var def))
            return def;
        return key;
    }

    /// <summary>
    /// Get a formatted message. Supports C-style %s/%d/%i placeholders
    /// as well as {0}/{1} .NET-style placeholders.
    /// </summary>
    public static string GetFormatted(string key, params object[] args)
    {
        string template = Get(key);
        if (args.Length == 0)
            return template;

        // Convert C-style placeholders to {N} format
        int idx = 0;
        string converted = Regex.Replace(template, @"%[sdi]", _ => $"{{{idx++}}}");
        // Also handle %lld (used in some vendor messages)
        idx = 0;
        converted = Regex.Replace(converted, @"%lld", _ => $"{{{idx++}}}");

        try
        {
            return string.Format(converted, args);
        }
        catch
        {
            return template;
        }
    }

    /// <summary>
    /// Set an override for a message key (from DEFMESSAGE script section).
    /// </summary>
    public static void SetOverride(string key, string value)
    {
        _overrides[key] = value;
    }

    /// <summary>
    /// Load all overrides from a dictionary (typically from ResourceHolder).
    /// </summary>
    public static void LoadOverrides(IEnumerable<KeyValuePair<string, string>> overrides)
    {
        foreach (var kv in overrides)
            _overrides[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Set default message color and font from DEFNAME messages_settings.
    /// </summary>
    public static void SetDefaults(ushort color, byte font)
    {
        DefaultColor = color;
        DefaultFont = font;
    }

    /// <summary>
    /// Clear all overrides (for reload/resync).
    /// </summary>
    public static void ClearOverrides()
    {
        _overrides.Clear();
    }

    /// <summary>
    /// Check if a key exists in defaults or overrides.
    /// </summary>
    public static bool HasKey(string key)
    {
        return _overrides.ContainsKey(key) || _defaults.ContainsKey(key);
    }

    private static void Def(string key, string value)
    {
        _defaults[key] = value;
    }

    private static void RegisterDefaults()
    {
        // ===== Combat =====
        Def("combat_nopvp", "You cannot attack players in this area.");
        Def("combat_dead", "You have died. Seek a healer to be resurrected.");
        Def("combat_resurrected", "You have been resurrected.");
        Def("combat_resurrected_with_corpse", "You have been resurrected and your belongings restored.");
        Def("combat_warmode_on", "You are now in war mode.");
        Def("combat_warmode_off", "You are now in peace mode.");
        Def("combat_arch_noammo", "You have no ammunition.");
        Def("combat_arch_tooclose", "You are too close.");
        Def("combat_parry", "Your attack was parried!");

        // ===== Spell / Magic =====
        Def("spell_cant_cast", "You cannot cast that spell.");
        Def("spell_cast_ok", "You cast %s.");
        Def("spell_gen_fizzles", "The spell fizzles");
        Def("spell_try_nomana", "You lack sufficient mana for this spell");
        Def("spell_try_noregs", "You lack %s for this spell");
        Def("spell_try_busyhands", "Your hands must be free to cast spells or meditate.");
        Def("spell_try_frozenhands", "You cannot cast spells while your hands are frozen.");
        Def("spell_try_nobook", "You don't have a spellbook handy.");
        Def("spell_try_dead", "This is beyond your ability.");
        Def("spell_gate_open", "You open a magical gate to another location.");

        // ===== Death / Resurrect =====
        Def("death_cant_while_dead", "You cannot do that while dead.");

        // ===== Mount =====
        Def("mount_already_riding", "You are already riding.");

        // ===== Item Use =====
        Def("itemuse_locked", "This item is locked.");
        Def("itemuse_eat_food", "You eat the food.");
        Def("itemuse_dye_fail", "The dye just drips off this.");

        // ===== Housing =====
        Def("house_placed", "House placed.");
        Def("house_cant_place", "Cannot place house here.");
        Def("house_not_house", "This does not belong to any house.");
        Def("house_select_owner", "Select the new owner.");
        Def("house_transferred", "House transferred to %s.");
        Def("house_demolished", "House demolished. A deed has been placed in your backpack.");
        Def("house_cant_demolish", "You cannot demolish this house.");
        Def("house_door_opened", "Door opened.");
        Def("house_add_coowner", "Target the player to add as co-owner.");
        Def("house_add_friend", "Target the player to add as friend.");
        Def("house_remove_coowner", "Target the co-owner to remove.");
        Def("house_remove_friend", "Target the friend to remove.");
        Def("house_added_coowner", "%s added as co-owner.");
        Def("house_already_coowner", "%s is already a co-owner.");
        Def("house_added_friend", "%s added as friend.");
        Def("house_already_friend", "%s is already a friend.");
        Def("house_removed_coowner", "%s removed as co-owner.");
        Def("house_not_coowner", "That player is not a co-owner.");
        Def("house_removed_friend", "%s removed as friend.");
        Def("house_not_friend", "That player is not a friend.");
        Def("house_lockdown", "Target the item to lock down.");
        Def("house_lockdown_ok", "The item has been locked down.");
        Def("house_lockdown_fail", "Cannot lock down that item (limit reached or no permission).");
        Def("house_lockdown_release", "Target the item to release.");
        Def("house_lockdown_released", "The item has been un-locked from the structure.");
        Def("house_lockdown_not", "That item is not locked down or you lack permission.");
        Def("house_secure", "Target the container to secure.");
        Def("house_secure_ok", "Container secured.");
        Def("house_secure_fail", "Cannot secure that container (limit reached or no permission).");
        Def("house_secure_release", "Target the container to release.");
        Def("house_secure_released", "Container released.");
        Def("house_secure_not", "That container is not secured or you lack permission.");
        Def("house_ban", "Target the player to ban.");
        Def("house_banned", "%s has been banned from the house.");
        Def("house_already_banned", "%s is already banned.");
        Def("house_unban", "Target the player to unban.");
        Def("house_unbanned", "%s has been unbanned.");
        Def("house_not_banned", "That player is not banned.");

        // ===== Crafting =====
        Def("craft_no_recipes", "There are no recipes available for this craft.");
        Def("craft_fail", "You fail to create the item. Some materials are lost.");
        Def("craft_success", "You create %s.");
        Def("cant_make", "You can't make that.");

        // ===== Vendor / Trade (Source-X: npc_vendor_*) =====
        Def("npc_vendor_no_goods", "Sorry I have no goods to sell");
        Def("npc_vendor_nothing_buy", "You have nothing I'm interested in");
        Def("npc_vendor_nothing_sell", "Sorry I have nothing of interest to you.");
        Def("npc_vendor_nomoney1", "Begging thy pardon, but thou canst not afford that.");
        Def("npc_vendor_b1", "That will be %s gold coin%s. ");
        Def("npc_vendor_sell_ty", "Here you are, %s gold coin%s. I thank thee for thy business.");
        Def("npc_vendor_ty", "I thank thee for thy business.");
        Def("npc_vendor_ca", "s");
        Def("npc_vendor_buy_nothing", "Thou hast bought nothing!");
        Def("npc_vendor_cantafford", "I cannot afford any more at the moment");
        Def("npc_vendor_cantfulfill", "Your order cannot be fulfilled, please try again.");
        Def("npc_vendor_cantreach", "You can't reach the Vendor");
        Def("npc_vendor_cantbuy", "You cannot buy that.");
        Def("npc_vendor_cantsell", "You cannot sell that.");
        Def("npc_vendor_buyfast", "You are buying too fast.");
        Def("npc_vendor_sellfast", "You are selling too fast.");
        Def("npc_vendor_nomoney", "Alas I have run out of money.");
        Def("vendor_what_sell", "What would you like to sell?");
        Def("vendor_bank_unavailable", "Your bank box is not available yet.");

        // ===== Trade =====
        Def("trade_invalid_target", "Invalid trade target.");
        Def("trade_cancelled", "Trade cancelled.");
        Def("trade_initiated", "You have initiated a trade with %s.");
        Def("trade_initiated2", "Trade initiated with %s");

        // ===== Rename =====
        Def("rename_no_permission", "You do not have permission to rename.");
        Def("msg_rename_success", "%s renamed: %s");
        Def("rename_item_ok", "Item renamed to '%s'.");

        // ===== Guild =====
        Def("guild_join_request", "You have submitted a request to join the guild.");
        Def("guild_disbanded", "Guild has been disbanded.");
        Def("guild_left", "You have left the guild.");
        Def("guild_target_candidate", "Target the candidate to accept.");
        Def("guild_member_added", "%s is now a guild member.");
        Def("guild_not_candidate", "%s is not a candidate.");
        Def("guild_target_title", "Target the member to set title.");
        Def("guild_not_member", "Not a guild member.");
        Def("guild_no_title", "No title text provided.");
        Def("guild_title_set", "%s's title set to '%s'.");
        Def("guild_target_enemy", "Target the enemy guild stone.");
        Def("guild_not_stone", "That is not a guild stone.");
        Def("guild_war_declared", "War declared on %s!");
        Def("guild_target_peace", "Target the guild stone to make peace with.");
        Def("guild_peace_declared", "Peace declared.");
        Def("guild_created", "Guild '%s' created. You are the guild master.");
        Def("guild_charter_updated", "Guild charter updated.");
        Def("guild_already_member", "You are already a member of a guild. Resign first.");

        // ===== Party (Source-X: party_*) =====
        Def("party_is_full", "You may only have 10 in your party.");
        Def("party_notleader", "You may only add members to the party if you are the leader.");
        Def("party_leave_1", "%s has been removed from your party.");
        Def("party_added", "You have been added to the party.");
        Def("party_decline_2", "You notify %s that you do not wish to join the party.");
        Def("party_invite", "You have invited %s to join the party.");
        Def("party_msg", "[PARTY]: %s");

        // ===== Potion =====
        Def("potion_cured", "You have been cured of poison.");
        Def("potion_heal", "You feel better. (+%s HP)");
        Def("potion_stamina", "You feel invigorated. (+%s Stamina)");
        Def("potion_str", "You feel stronger.");
        Def("potion_dex", "You feel more agile.");
        Def("potion_drink", "You drink the potion.");

        // ===== Skill Use =====
        Def("skill_use_ok", "You use %s.");
        Def("skill_use_fail", "You fail to use %s.");

        // ===== Target =====
        Def("target_cancel_1", "Targeting cancelled.");
        Def("target_invalid", "Invalid target.");
        Def("target_must_object", "You must target an object.");
        Def("target_cant_remove", "Target is invalid or cannot be removed.");

        // ===== Pet Commands =====
        Def("pet_all_cmd", "All pets: %s");
        Def("pet_cmd", "%s: %s");

        // ===== Misc / GM =====
        Def("msg_serial_required", "Serial required.");
        Def("msg_message_posted", "Message posted.");
        Def("msg_name_set", "Name set to: %s");
        Def("msg_invalid_target", "Invalid target.");

        // ===== GM Commands (SpeechEngine) =====
        Def("gm_add_usage", "Usage: .ADD <itemid|charid|DEFNAME>");
        Def("gm_add_select", "Select location/object to add '%s'.");
        Def("gm_teleported", "Teleported to %s.");
        Def("gm_teleported_named", "Teleported to %s (%s).");
        Def("gm_now_invisible", "You are now invisible.");
        Def("gm_now_visible", "You are now visible.");
        Def("gm_allmove_on", "AllMove ON: you can now walk through walls and mobiles.");
        Def("gm_allmove_off", "AllMove OFF: collision checks enabled.");
        Def("gm_go_usage", "Unknown location '%s'. Usage: .GO <x y z> or .GO <name>");
        Def("gm_remove_select", "Select object to remove.");
        Def("gm_remove_usage", "Usage: .REMOVE <serial> or .REMOVE then target.");
        Def("gm_cant_remove_self", "You cannot remove yourself.");
        Def("gm_object_not_found", "Object not found: 0x%s");
        Def("gm_world_info", "World: %s chars, %s items, %s sectors");
        Def("gm_show_select", "Select target for SHOW %s.");
        Def("gm_account_mgmt", "Account management: use the web admin panel or telnet console.");
        Def("gm_page_submitted", "Page submitted: %s");
        Def("gm_skill_set", "Skill %s set to %s%%.");
        Def("gm_frozen", "%s has been frozen.");
        Def("gm_freeze_usage", "Usage: .FREEZE <serial>");
        Def("gm_unfrozen", "%s has been unfrozen.");
        Def("gm_jailed_timed", "%s jailed for %s minutes.");
        Def("gm_jailed_indef", "%s has been jailed (indefinite).");
        Def("gm_released", "%s has been released from jail.");
        Def("gm_safe_teleport", "Teleported to safe location.");
        Def("gm_addnpc_usage", "Usage: .ADDNPC <bodyId>");
        Def("gm_npc_created", "NPC '%s' created at %s.");
        Def("gm_priv_set", "%s privilege set to %s.");
        Def("gm_allshow_off", "AllShow OFF — invisible objects hidden.");
        Def("gm_allshow_on", "AllShow ON — invisible objects shown as grey.");
        Def("gm_tele_select", "Select a character or ground tile to teleport.");
        Def("gm_inspect_select", "Select target to inspect.");
        Def("gm_resync", "Update/Resync requested.");
        Def("gm_pos_fixed", "Character position fixed.");
        Def("gm_mode_on", "GM mode ON (invisible).");
        Def("gm_mode_off", "GM mode OFF.");
        Def("gm_privlevel", "PrivLevel: %s (%s)");
        Def("gm_position", "Position: %s,%s,%s map=%s terrainZ=%s effectiveZ=%s");
        Def("gm_cast_usage", "Usage: .CAST <spell_id or name>");
        Def("gm_unknown_spell", "Unknown spell: %s");
        Def("gm_show_events_usage", "Usage: .SHOW EVENTS [serial]");
        Def("gm_invalid_serial", "Invalid serial: %s");
        Def("gm_no_events", "Target has no EVENTS list: 0x%s");
        Def("gm_unknown_add", "Unknown ADD target: %s");
        Def("gm_removed", "Removed 0x%s.");
        Def("gm_teleported_dest", "Teleported to %s.");
        Def("gm_item_created", "Item created: %s (0x%s).");
        Def("gm_item_no_graphic", "Cannot resolve graphic for '%s' — the itemdef's ID chain does not reach a numeric UO art ID.");
        Def("gm_npc_created2", "NPC created: %s (0x%s) at %s.");
        Def("gm_item_created_hex", "Item created: 0x%s at %s.");
        Def("gm_npc_created_hex", "NPC created: %s (0x%s) at %s.");
        Def("gm_insuf_priv", "Insufficient privileges. Required=%s, YourPLEVEL=%s.");
        Def("cmd_invalid", "Not a valid command or format");
        Def("gm_object_serial", "Object not found: 0x%s");

        // ===== World Save (Source-X: DEFMSG_WORLDSAVE_*) =====
        Def("worldsave_started", "Saving world...");
        Def("worldsave_complete", "World save complete. (#%s, took %s ms)");
        Def("worldsave_failed", "World save FAILED: %s");

        // ===== DB Commands =====
        Def("db_connect_fail", "DB.CONNECT failed: %s");
        Def("db_query_fail", "DB.QUERY failed: %s");
        Def("db_execute_fail", "DB.EXECUTE failed: %s");

        // ===== Script Stubs =====
        Def("msg_stuck_script", "Stuck secimi script tarafinda islenmeli.");
        Def("msg_page_script", "Page gonderim formu script tarafinda islenmeli.");
        Def("msg_pagelist_script", "Onceki page listesi script tarafinda islenmeli.");
    }
}
