using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXCombatSpeedWave226Tests
{
    [Fact]
    public void SamuraiEmpireFormula_UsesScaleAndQuarterSecondTicks()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.Dex = 100;
        // Eras 1-4 price the swing off CURRENT STAMINA (Source-X
        // Stat_GetVal(STAT_DEX)), not the base stat, so the pool has to be real.
        attacker.Stam = 100;
        Character.CombatSpeedEra = 3;
        Character.CombatSpeedScaleFactor = 80_000;

        Assert.Equal(1_500, CombatEngine.GetSwingDelayMs(attacker, null));
    }

    [Fact]
    public void SwingSpeedIncrease_AggregatesCharacterAndAllEquippedItems()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.Dex = 100;
        // Eras 1-4 price the swing off CURRENT STAMINA (Source-X
        // Stat_GetVal(STAT_DEX)), not the base stat, so the pool has to be real.
        attacker.Stam = 100;
        Assert.True(attacker.TrySetProperty(CombatSpeedProperties.IncreaseSwingSpeed, "10"));
        var ring = world.CreateItem();
        Assert.True(ring.TrySetProperty(CombatSpeedProperties.IncreaseSwingSpeed, "10"));
        attacker.Equip(ring, Layer.Ring);
        Character.CombatSpeedEra = 3;
        Character.CombatSpeedScaleFactor = 80_000;

        Assert.Equal(20, CombatEngine.GetEquipmentPropertyValue(
            attacker, CombatSpeedProperties.IncreaseSwingSpeed));
        Assert.Equal(1_200, CombatEngine.GetSwingDelayMs(attacker, null));
        Assert.True(attacker.TryGetProperty(CombatSpeedProperties.IncreaseSwingSpeed, out string value));
        Assert.Equal("20", value);
    }

    [Fact]
    public void MondainsLegacyFormula_UsesMlWeaponSpeedFormat()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.Dex = 90;
        attacker.Stam = 90;
        var weapon = world.CreateItem();
        weapon.SetTag("OVERRIDE.SPEED", "3");
        Character.CombatSpeedEra = 4;

        Assert.Equal(3, weapon.Speed);
        Assert.Equal(2_200, CombatEngine.GetSwingDelayMs(attacker, weapon));
    }

    [Fact]
    public void MondainsLegacyFormula_PreservesSourceXIntegerSsiOrder()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.Dex = 90;
        attacker.Stam = 90;
        attacker.SetTag(CombatSpeedProperties.IncreaseSwingSpeed, "10");
        var weapon = world.CreateItem();
        weapon.SetTag("OVERRIDE.SPEED", "3");
        Character.CombatSpeedEra = 4;

        // Source-X evaluates 100 / (100 + SSI) using integer arithmetic.
        Assert.Equal(1_200, CombatEngine.GetSwingDelayMs(attacker, weapon));
    }
}
