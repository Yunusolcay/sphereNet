using SphereNet.Core.Enums;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Scripting.Variables;

namespace SphereNet.Game.NPCs;

/// <summary>
/// Eating, for players and pets alike.
///
/// Source-X routes both through the same pair: Use_EatQty works out how much of the
/// stack is actually wanted (CCharUse.cpp:870) and EatAnim raises the stats and fires
/// the @Eat event (CCharAct.cpp:3436). SphereNet had two separate hand-written
/// versions - a player double-click that added a flat five and a pet feed that added
/// ten per unit - and the pet one fired no event at all, so a fed pet ran no script.
///
/// Three things follow from having one path:
///
///  * A stack is consumed by the NEEDED amount, not wholesale. The reference refuses
///    outright when there is no room left (:891) and otherwise takes only as many
///    units as the free space calls for (:894), which is why dropping a hundred rations
///    on a full pet no longer destroys them.
///  * @Eat gets the reference's arguments: ARGN1 is a STAT LIMIT starting at zero -
///    not the food restored - with LOCAL.Hits / Mana / Stam / Food carrying the gains
///    and the item as the object argument, all read back afterwards (:3456-3476).
///  * RETURN 1 skips the gains but does NOT save the food: Use_EatQty calls
///    ConsumeAmount after EatAnim returns either way (:913).
/// </summary>
public static class EatEngine
{
    private static readonly Random Rng = new();

    /// <summary>Hunger restored per unit eaten. Source-X reads m_itFood.m_foodval and
    /// falls back to the itemdef's VOLUME (CCharUse.cpp:881); SphereNet has neither
    /// field, so a script may set the FOODVAL tag and everything else keeps the ten
    /// per unit the pet path already used. Never below one, as the reference floors
    /// it (:887).</summary>
    public static int RestorePerUnit(Item food) =>
        food.TryGetTag("FOODVAL", out string? raw) && int.TryParse(raw, out int val) && val > 0
            ? val
            : 10;

    /// <summary>How many units of <paramref name="food"/> this eater actually wants.
    /// Zero when they are full, which the caller must treat as "nothing happened"
    /// rather than as a meal (CCharUse.cpp:891).</summary>
    public static int WantedAmount(Character eater, Item food, int offered)
    {
        int space = eater.MaxFood - eater.Food;
        if (space <= 0)
            return 0;

        int qty = Math.Clamp(offered, 1, Math.Max(1, (int)food.Amount));
        int restore = Math.Max(1, RestorePerUnit(food));
        if (qty > 1 && restore * qty > space)
            qty = Math.Max(1, space / restore);
        return qty;
    }

    /// <summary>Eat up to <paramref name="offered"/> units. Returns how many were
    /// actually eaten - zero when the eater was already full - leaving the caller to
    /// consume that many and hand the rest back.</summary>
    public static int Eat(Character eater, Item food, TriggerDispatcher? triggers, int offered)
    {
        int qty = WantedAmount(eater, food, offered);
        if (qty <= 0)
            return 0;

        ApplyMeal(eater, food, triggers, Math.Max(1, RestorePerUnit(food)) * qty);
        return qty;
    }

    /// <summary>The EatAnim half: fire @Eat with the reference's arguments, read the
    /// script's answer back, and apply what it leaves behind.</summary>
    private static void ApplyMeal(Character eater, Item food, TriggerDispatcher? triggers,
        int restored)
    {
        int hits = 0;
        int mana = 0;
        int stam = Rng.Next(3, 7) + (restored / 5);
        int foodGain = restored;
        int statsLimit = 0;

        if (triggers != null)
        {
            var locals = new VarMap();
            locals.SetInt("Hits", hits);
            locals.SetInt("Mana", mana);
            locals.SetInt("Stam", stam);
            locals.SetInt("Food", foodGain);

            var args = new TriggerArgs
            {
                CharSrc = eater,
                ItemSrc = food,
                O1 = food,
                N1 = statsLimit,
                Locals = locals,
            };

            // RETURN 1 stops the gains and nothing else. The reference returns from
            // EatAnim here and its caller consumes the food regardless (:913), so a
            // vetoing script blocks the benefit, not the meal.
            if (triggers.FireCharTrigger(eater, CharTrigger.Eat, args) == TriggerResult.True)
                return;

            hits = (int)locals.GetInt("Hits");
            mana = (int)locals.GetInt("Mana");
            stam = (int)locals.GetInt("Stam");
            foodGain = (int)locals.GetInt("Food");
            statsLimit = args.N1;
        }

        // Each local is a GAIN added to the stat it names, capped by ARGN1 when the
        // script set one and by the stat's own maximum otherwise.
        //
        // Deliberate divergence, recorded: EatAnim adds the current stat to the local
        // and hands the sum to UpdateStatVal, which adds it to the current value again
        // (CCharAct.cpp:757), so the reference doubles what is already there before
        // clamping. Reproducing that would double a shard's food and healing on every
        // meal for no stated purpose; the plain gain is applied instead.
        if (hits != 0)
            eater.Hits = (short)Cap(eater.Hits + hits, statsLimit, eater.MaxHits);
        if (mana != 0)
            eater.Mana = (short)Cap(eater.Mana + mana, statsLimit, eater.MaxMana);
        if (stam != 0)
            eater.Stam = (short)Cap(eater.Stam + stam, statsLimit, eater.MaxStam);
        if (foodGain != 0)
            eater.Food = (ushort)Cap(eater.Food + foodGain, statsLimit, eater.MaxFood);
    }

    private static int Cap(int value, int scriptLimit, int statMax)
    {
        int limit = scriptLimit > 0 ? scriptLimit : statMax;
        return Math.Clamp(value, 0, Math.Max(0, limit));
    }
}
