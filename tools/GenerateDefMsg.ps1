# GenerateDefMsg.ps1
#
# One-shot generator that reads Source-X's defmessages.tbl and produces:
#   - src/SphereNet.Game/Messages/ServerMessages.Generated.cs (partial class with 1170 Def() calls)
#   - src/SphereNet.Game/Messages/Msg.cs                      (typed const string keys + helpers)
#   - src/SphereNet.Game/Messages/MessageCategory.Generated.cs (key -> category map)
#
# Re-run any time defmessages.tbl is updated upstream. Output is deterministic.

param(
    [string]$TblPath   = "D:\Projeler\Yunus\sphereNet\oldSphere\Source-X-full\src\tables\defmessages.tbl",
    [string]$OutputDir = "D:\Projeler\Yunus\sphereNet\src\SphereNet.Game\Messages"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $TblPath)) { throw "defmessages.tbl bulunamadi: $TblPath" }
if (-not (Test-Path $OutputDir)) { throw "Output dizini yok: $OutputDir" }

# --- Parse -----------------------------------------------------------------
$lines = Get-Content -LiteralPath $TblPath -Encoding UTF8
$entries = @()  # array of @{ Key=lower; Const=Pascal; Value=str; Category=str }

# MSG(NAME, "value")  - value can contain escaped quotes? In this tbl: no embedded quotes seen.
$rx = [regex]'^\s*MSG\(\s*([A-Z0-9_]+)\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)\s*$'

foreach ($raw in $lines) {
    $m = $rx.Match($raw)
    if (-not $m.Success) { continue }
    $upper = $m.Groups[1].Value
    $value = $m.Groups[2].Value
    $key   = $upper.ToLowerInvariant()

    # PascalCase from snake_case, preserving digits attached to previous segment
    $parts = $upper -split '_'
    $pascal = ''
    foreach ($p in $parts) {
        if ($p.Length -eq 0) { continue }
        $first = $p.Substring(0,1).ToUpperInvariant()
        $rest  = if ($p.Length -gt 1) { $p.Substring(1).ToLowerInvariant() } else { '' }
        $pascal += $first + $rest
    }

    # Category by prefix -- aligned with Source-X cpp dispatch.
    $category = switch -Regex ($key) {
        '^anatomy_'             { 'Skill'; break }
        '^animallore_'          { 'Skill'; break }
        '^armslore_'            { 'Skill'; break }
        '^evalint_'             { 'Skill'; break }
        '^forensics_'           { 'Skill'; break }
        '^itemid_'              { 'Skill'; break }
        '^tasteid_'             { 'Skill'; break }
        '^begging_'             { 'Skill'; break }
        '^cooking_'             { 'Skill'; break }
        '^detecthidden_'        { 'Skill'; break }
        '^fishing_'             { 'Skill'; break }
        '^healing_'             { 'Skill'; break }
        '^herding_'             { 'Skill'; break }
        '^hiding_'              { 'Skill'; break }
        '^inscription_'         { 'Skill'; break }
        '^lockpicking_'         { 'Skill'; break }
        '^lumberjacking_'       { 'Skill'; break }
        '^magery_'              { 'Skill'; break }
        '^makesuccess_'         { 'Skill'; break }
        '^meditation_'          { 'Skill'; break }
        '^mining_'              { 'Skill'; break }
        '^musicanship_'         { 'Skill'; break }
        '^peacemaking_'         { 'Skill'; break }
        '^poisoning_'           { 'Skill'; break }
        '^provocation_'         { 'Skill'; break }
        '^provoke_'             { 'Skill'; break }
        '^enticement_'          { 'Skill'; break }
        '^removetraps_'         { 'Skill'; break }
        '^smithing_'            { 'Skill'; break }
        '^snooping_'            { 'Skill'; break }
        '^spiritspeak_'         { 'Skill'; break }
        '^stealing_'            { 'Skill'; break }
        '^taming_'              { 'Skill'; break }
        '^tracking_'            { 'Skill'; break }
        '^skill_'               { 'Skill'; break }
        '^skillwait_'           { 'Skill'; break }
        '^skillact_'            { 'Skill'; break }
        '^skilltitle_'          { 'Skill'; break }
        '^gainradius_'          { 'Skill'; break }
        '^grandmaster_'         { 'Skill'; break }
        '^carve_'               { 'Skill'; break }
        '^cant_make'            { 'Skill'; break }

        '^itemuse_'             { 'ItemUse'; break }
        '^repair_'              { 'ItemUse'; break }
        '^drink_'               { 'ItemUse'; break }
        '^food_'                { 'ItemUse'; break }
        '^lock_'                { 'ItemUse'; break }
        '^unlock_'              { 'ItemUse'; break }
        '^cantmove_'            { 'ItemUse'; break }
        '^reach_'               { 'ItemUse'; break }
        '^equip_'               { 'ItemUse'; break }

        '^npc_vendor_'          { 'NpcVendor'; break }
        '^npc_pet_'             { 'NpcPet'; break }
        '^npc_trainer_'         { 'NpcTrainer'; break }
        '^npc_stablemaster_'    { 'NpcStable'; break }
        '^npc_healer_'          { 'NpcHealer'; break }
        '^npc_banker_'          { 'NpcBanker'; break }
        '^npc_guard_'           { 'NpcGuard'; break }
        '^npc_generic_'         { 'NpcGeneric'; break }
        '^npc_eat_'             { 'NpcGeneric'; break }
        '^petslots_'            { 'NpcPet'; break }
        '^pet_'                 { 'NpcPet'; break }

        '^party_'               { 'Party'; break }

        '^spell_'               { 'Spell'; break }
        '^resistmagic'          { 'Spell'; break }

        '^combat_'              { 'Combat'; break }
        '^msg_coward_'          { 'Combat'; break }
        '^msg_killed_'          { 'Combat'; break }
        '^msg_younotice_'       { 'Combat'; break }
        '^msg_noto_'            { 'Combat'; break }
        '^msg_corpse_'          { 'Combat'; break }
        '^msg_murderer'         { 'Combat'; break }
        '^msg_guards'           { 'Combat'; break }
        '^msg_forgiven'         { 'Combat'; break }
        '^msg_jailed'           { 'Combat'; break }
        '^msg_stepon_'          { 'Combat'; break }

        '^msg_region_'          { 'Movement'; break }
        '^msg_arrdep_'          { 'Movement'; break }
        '^msg_fatigue'          { 'Movement'; break }
        '^msg_frozen'           { 'Movement'; break }
        '^msg_unconscious'      { 'Movement'; break }
        '^msg_cantsleep'        { 'Movement'; break }
        '^msg_cantpush'         { 'Movement'; break }
        '^msg_push'             { 'Movement'; break }
        '^msg_mount_'           { 'Movement'; break }
        '^msg_summon_'          { 'Movement'; break }
        '^msg_bonded_'          { 'Movement'; break }
        '^msg_alreadyonline'    { 'Movement'; break }
        '^login_'               { 'Movement'; break }

        '^tiller_'              { 'Ship'; break }
        '^ship_'                { 'Ship'; break }

        '^multi_'               { 'Multi'; break }
        '^house_'               { 'Multi'; break }
        '^deed_'                { 'Multi'; break }

        '^cont_'                { 'Container'; break }
        '^bvbox_'               { 'Container'; break }
        '^msg_heavy'            { 'Container'; break }
        '^msg_feet'             { 'Container'; break }
        '^msg_bounce_'          { 'Container'; break }
        '^too_many_items'       { 'Container'; break }
        '^cant_add_'            { 'Container'; break }

        '^charinfo_'            { 'Tooltip'; break }
        '^itemtitle_'           { 'Tooltip'; break }
        '^tooltip_'             { 'Tooltip'; break }
        '^title_'               { 'Tooltip'; break }
        '^tradetitle_'          { 'Tooltip'; break }
        '^possesspronoun_'      { 'Tooltip'; break }
        '^pronoun_'             { 'Tooltip'; break }
        '^noto_'                { 'Tooltip'; break }
        '^map_dir_'             { 'Tooltip'; break }
        '^clock_'               { 'Tooltip'; break }
        '^msg_food_lvl_'        { 'Tooltip'; break }
        '^msg_pet_food_'        { 'Tooltip'; break }
        '^msg_pet_happy_'       { 'Tooltip'; break }
        '^msg_exp_'             { 'Tooltip'; break }
        '^msg_hunger'           { 'Tooltip'; break }
        '^itemstatus_'          { 'Tooltip'; break }
        '^itemid_'              { 'Tooltip'; break }
        '^item_'                { 'Tooltip'; break }
        '^rune_'                { 'Tooltip'; break }
        '^key_to'               { 'Tooltip'; break }
        '^key_name'             { 'Tooltip'; break }
        '^stone_for'            { 'Tooltip'; break }
        '^stone_name'           { 'Tooltip'; break }
        '^rune_name'            { 'Tooltip'; break }
        '^lightsrc_'            { 'Tooltip'; break }
        '^book_author_'         { 'Tooltip'; break }

        '^msg_acc_'             { 'Account'; break }
        '^msg_serv_'            { 'Account'; break }
        '^msg_maxchars'         { 'Account'; break }
        '^msg_guest'            { 'Account'; break }
        '^console_'             { 'Account'; break }
        '^server_'              { 'Account'; break }
        '^cmd_'                 { 'Account'; break }
        '^cmdafk_'              { 'Account'; break }
        '^msg_cmd_'             { 'Account'; break }
        '^axis_'                { 'Account'; break }
        '^hl_'                  { 'Account'; break }
        '^web_'                 { 'Account'; break }
        '^map_is_blank'         { 'Account'; break }

        '^gmpage_'              { 'GmPage'; break }
        '^msg_mailbag_'         { 'GmPage'; break }

        '^target_'              { 'Target'; break }
        '^msg_targ_'            { 'Target'; break }
        '^msg_prompt_'          { 'Target'; break }
        '^select_'              { 'Target'; break }
        '^menu_unexpected'      { 'Target'; break }
        '^where_to_'            { 'Target'; break }
        '^use_spyglass_'        { 'Target'; break }
        '^msg_follow_arrow'     { 'Target'; break }

        '^msg_rename_'          { 'Misc'; break }
        '^msg_seed_'            { 'Misc'; break }
        '^msg_shipname_'        { 'Ship'; break }
        '^msg_invis_'           { 'Misc'; break }
        '^msg_invul_'           { 'Misc'; break }
        '^msg_emote_'           { 'Misc'; break }
        '^msg_err_'             { 'Misc'; break }
        '^msg_eatsome'          { 'Misc'; break }
        '^msg_figurine_'        { 'Misc'; break }
        '^msg_guildresign'      { 'Misc'; break }
        '^msg_steal'            { 'Misc'; break }
        '^msg_stonereg_'        { 'Misc'; break }
        '^msg_trade_'           { 'Trade'; break }
        '^msg_toofar_vendor'    { 'NpcVendor'; break }
        '^msg_it_is_dead'       { 'Misc'; break }
        '^msg_itemplace'        { 'Misc'; break }
        '^msg_key_'             { 'ItemUse'; break }
        '^msg_where'            { 'Misc'; break }
        '^non_alive'            { 'Misc'; break }
        '^msg_serial_required'  { 'Misc'; break }
        '^msg_message_posted'   { 'Misc'; break }
        '^msg_name_set'         { 'Misc'; break }
        '^msg_invalid_target'   { 'Target'; break }
        '^cooking_fire_source'  { 'Skill'; break }
        '^cant_make'            { 'Skill'; break }
        '^trade_'               { 'Trade'; break }
        '^vendor_'              { 'NpcVendor'; break }
        '^guild_'               { 'Guild'; break }
        '^crops_'               { 'Skill'; break }
        '^lock_pick_crime'      { 'Skill'; break }
        '^looting_crime'        { 'Misc'; break }
        '^provoking_crime'      { 'Skill'; break }
        '^just_been_poisoned'   { 'Spell'; break }
        '^loot_'                { 'Misc'; break }
        '^item_in_use'          { 'ItemUse'; break }
        '^location_invalid'     { 'Target'; break }
        '^nuked_'               { 'Account'; break }
        '^tiled_'               { 'Account'; break }
        '^nudged_'              { 'Account'; break }
        '^tile_same_point'      { 'Account'; break }
        '^no_bad_spawns'        { 'Account'; break }
        '^extract_usage'        { 'Account'; break }
        '^unextract_usage'      { 'Account'; break }
        '^select_potion_target' { 'Spell'; break }
        '^select_magic_target'  { 'Spell'; break }
        default                 { 'Misc' }
    }

    $entries += [pscustomobject]@{
        Key      = $key
        Const    = $pascal
        Value    = $value
        Category = $category
    }
}

if ($entries.Count -lt 1000) {
    throw "Beklenenden az MSG entry: $($entries.Count). Parser veya tbl hatali."
}

Write-Host "Parse edildi: $($entries.Count) anahtar."

# --- Util ------------------------------------------------------------------
function EscapeForCsharpVerbatim([string]$s) {
    return $s -replace '"', '""'
}

# --- Build ServerMessages.Generated.cs ------------------------------------
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('// <auto-generated>')
[void]$sb.AppendLine('//   Source-X DEFMSG_* parity defaults.')
[void]$sb.AppendLine('//   Generated by tools/GenerateDefMsg.ps1 from')
[void]$sb.AppendLine("//   $TblPath")
[void]$sb.AppendLine('//   Do NOT edit this file by hand. Re-run the generator after upstream changes.')
[void]$sb.AppendLine('// </auto-generated>')
[void]$sb.AppendLine()
[void]$sb.AppendLine('namespace SphereNet.Game.Messages;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('public static partial class ServerMessages')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine("    /// <summary>Total number of DEFMSG_* keys exported by Source-X (defmessages.tbl).</summary>")
[void]$sb.AppendLine("    public const int SphereDefaultCount = $($entries.Count);")
[void]$sb.AppendLine()
[void]$sb.AppendLine('    /// <summary>')
[void]$sb.AppendLine('    /// Loads every DEFMSG_* default present in Source-X. Called once from the static')
[void]$sb.AppendLine('    /// constructor before user-facing custom defaults are applied, so any custom Def(...)')
[void]$sb.AppendLine('    /// call (or messages.scp override) takes precedence.')
[void]$sb.AppendLine('    /// </summary>')
[void]$sb.AppendLine('    private static void RegisterSphereDefaults()')
[void]$sb.AppendLine('    {')
foreach ($e in $entries) {
    $val = EscapeForCsharpVerbatim($e.Value)
    [void]$sb.AppendLine("        Def(""$($e.Key)"", @""$val"");")
}
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

$genPath = Join-Path $OutputDir 'ServerMessages.Generated.cs'
Set-Content -LiteralPath $genPath -Value $sb.ToString() -Encoding UTF8
Write-Host "Yazildi: $genPath"

# --- Build Msg.cs ---------------------------------------------------------
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('// <auto-generated>')
[void]$sb.AppendLine('//   Type-safe DEFMSG_* key constants for Source-X parity.')
[void]$sb.AppendLine('//   Generated by tools/GenerateDefMsg.ps1.')
[void]$sb.AppendLine('//   Use Msg.<KeyName> instead of magic strings; Msg.Get / Msg.Format wrap ServerMessages.')
[void]$sb.AppendLine('// </auto-generated>')
[void]$sb.AppendLine()
[void]$sb.AppendLine('namespace SphereNet.Game.Messages;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('/// <summary>')
[void]$sb.AppendLine('/// Tip-guvenli DEFMSG_* anahtarlari. Her sabit, Source-X defmessages.tbl icindeki')
[void]$sb.AppendLine('/// MSG(NAME, ...) tanimina karsilik gelir. Anahtar adi snake_case lowercase olarak')
[void]$sb.AppendLine('/// ServerMessages dictionary lookup icin kullanilir.')
[void]$sb.AppendLine('/// </summary>')
[void]$sb.AppendLine('public static class Msg')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine("    /// <summary>Total exported keys count (matches ServerMessages.SphereDefaultCount).</summary>")
[void]$sb.AppendLine("    public const int Count = $($entries.Count);")
[void]$sb.AppendLine()
[void]$sb.AppendLine('    /// <summary>Get raw text (override or default) for a DEFMSG_* key.</summary>')
[void]$sb.AppendLine('    public static string Get(string key) => ServerMessages.Get(key);')
[void]$sb.AppendLine()
[void]$sb.AppendLine('    /// <summary>Get formatted text supporting %s/%d/%i/%lld/%lu/%u/%c placeholders.</summary>')
[void]$sb.AppendLine('    public static string Format(string key, params object[] args) => ServerMessages.GetFormatted(key, args);')
[void]$sb.AppendLine()
foreach ($e in $entries) {
    [void]$sb.AppendLine("    /// <summary>$($e.Const) -- defmessages.tbl key '$($e.Key)'.</summary>")
    [void]$sb.AppendLine("    public const string $($e.Const) = ""$($e.Key)"";")
}
[void]$sb.AppendLine('}')

$msgPath = Join-Path $OutputDir 'Msg.cs'
Set-Content -LiteralPath $msgPath -Value $sb.ToString() -Encoding UTF8
Write-Host "Yazildi: $msgPath"

# --- Build MessageCategory.Generated.cs ----------------------------------
$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('// <auto-generated>')
[void]$sb.AppendLine('//   Generated by tools/GenerateDefMsg.ps1.')
[void]$sb.AppendLine('// </auto-generated>')
[void]$sb.AppendLine()
[void]$sb.AppendLine('using System.Collections.Generic;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('namespace SphereNet.Game.Messages;')
[void]$sb.AppendLine()
[void]$sb.AppendLine('public static partial class MessageCategoryMap')
[void]$sb.AppendLine('{')
[void]$sb.AppendLine('    private static Dictionary<string, MessageCategory> BuildMap()')
[void]$sb.AppendLine('    {')
[void]$sb.AppendLine("        var map = new Dictionary<string, MessageCategory>($($entries.Count), System.StringComparer.OrdinalIgnoreCase);")
foreach ($e in $entries) {
    [void]$sb.AppendLine("        map[""$($e.Key)""] = MessageCategory.$($e.Category);")
}
[void]$sb.AppendLine('        return map;')
[void]$sb.AppendLine('    }')
[void]$sb.AppendLine('}')

$catPath = Join-Path $OutputDir 'MessageCategory.Generated.cs'
Set-Content -LiteralPath $catPath -Value $sb.ToString() -Encoding UTF8
Write-Host "Yazildi: $catPath"

Write-Host ""
Write-Host "Tamamlandi. $($entries.Count) anahtar uretildi."
Write-Host "Kategori dagilimi:"
$entries | Group-Object Category | Sort-Object Count -Descending | Format-Table Count, Name -AutoSize
