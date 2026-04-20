namespace SphereNet.Game.Messages;

/// <summary>
/// Loose grouping of DEFMSG_* keys by feature area, derived from Source-X
/// dispatch sites (CCharSkill, CCharUse, CCharNPCAct_*, CCharFight, CClientMsg, ...).
/// Used purely for diagnostics, log filtering and editor tooling -- the engine
/// itself never branches on category.
/// </summary>
public enum MessageCategory
{
    Misc = 0,
    Skill,
    ItemUse,
    NpcVendor,
    NpcPet,
    NpcTrainer,
    NpcStable,
    NpcHealer,
    NpcBanker,
    NpcGuard,
    NpcGeneric,
    Spell,
    Combat,
    Movement,
    Ship,
    Multi,
    Container,
    Tooltip,
    Party,
    Trade,
    Account,
    GmPage,
    Target,
    Guild,
}

/// <summary>
/// Maps every Source-X DEFMSG_* key to its <see cref="MessageCategory"/>.
/// The mapping itself is generated (see MessageCategory.Generated.cs) by
/// <c>tools/GenerateDefMsg.ps1</c> from defmessages.tbl.
/// </summary>
public static partial class MessageCategoryMap
{
    private static readonly Dictionary<string, MessageCategory> s_map = BuildMap();

    /// <summary>
    /// Get the category of a message key. Unknown keys (custom SphereNet keys
    /// not present upstream) fall back to <see cref="MessageCategory.Misc"/>.
    /// </summary>
    public static MessageCategory GetCategory(string key) =>
        s_map.TryGetValue(key, out var cat) ? cat : MessageCategory.Misc;

    /// <summary>True if the key is part of the Source-X DEFMSG_* set.</summary>
    public static bool IsSourceXKey(string key) => s_map.ContainsKey(key);

    /// <summary>Total number of Source-X DEFMSG_* keys mapped (matches Msg.Count).</summary>
    public static int Count => s_map.Count;
}
