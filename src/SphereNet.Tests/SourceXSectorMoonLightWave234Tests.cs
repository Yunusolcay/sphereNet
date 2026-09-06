using SphereNet.Core.Types;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;

namespace SphereNet.Tests;

public sealed class SourceXSectorMoonLightWave234Tests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 360)]
    [InlineData(3, 1080)]
    public void GetLocalTime_DistributesDayAcrossSectorColumns(int sectorX, int expectedMinutes)
    {
        var sector = CreateSector(sectorX, columns: 4, worldMinutes: 0);

        Assert.Equal(expectedMinutes, sector.GetLocalTime());
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(14, false, 1)]
    [InlineData(52, false, 3)]
    [InlineData(52, true, 0)]
    [InlineData(105, true, 1)]
    [InlineData(420, true, 4)]
    public void GetMoonPhase_UsesSourceXSynodicPeriods(long minutes, bool felucca, int expected)
    {
        Assert.Equal(expected, Sector.GetMoonPhase(minutes, felucca));
    }

    [Theory]
    [InlineData(0, 720, true)]
    [InlineData(0, 360, false)]
    [InlineData(2, 721, true)]
    [InlineData(4, 60, true)]
    [InlineData(4, 720, false)]
    [InlineData(7, 500, true)]
    public void IsMoonVisible_UsesSourceXRiseAndSetWindows(int phase, int localTime, bool expected)
    {
        Assert.Equal(expected, Sector.IsMoonVisible(phase, localTime));
    }

    [Fact]
    public void GetLightCalc_AppliesBothFullMoonBrightnessTablesAtNight()
    {
        // minute 473: Trammel and Felucca are both phase 4. Sector column 3
        // shifts local time to 01:53, where both full moons are visible.
        var sector = CreateSector(3, columns: 4, worldMinutes: 473);

        Assert.Equal((byte)17, sector.GetLightCalc());
    }

    [Fact]
    public void GetLightCalc_UsesConfiguredDaylightOutsideNightWindow()
    {
        var sector = CreateSector(0, columns: 4, worldMinutes: 473);

        Assert.Equal((byte)0, sector.GetLightCalc());
    }

    [Fact]
    public void GameWorld_ReturnsPositionSpecificSectorLight()
    {
        var world = new GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 256, 64);
        world.SetWorldClockMinutes(473);

        Assert.Equal((byte)0, world.GetLightLevel(new Point3D(1, 1, 0, 0)));
        Assert.Equal((byte)17, world.GetLightLevel(new Point3D(193, 1, 0, 0)));
    }

    private static Sector CreateSector(int sectorX, int columns, long worldMinutes)
    {
        return new Sector(sectorX, 0, 0, columns)
        {
            GetWorldMinutes = () => worldMinutes,
            GetLightSettings = () => (0, 25, 27),
            IsDungeon = () => false,
            // Clear skies. Weather codes are Source-X WEATHER_TYPE now, where 0 is
            // RAIN and 255 is dry - this fixture used to mean "dry" by writing 0.
            Weather = Sector.WeatherDry,
        };
    }
}
