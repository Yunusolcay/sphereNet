using SphereNet.Game.Gumps;
using SphereNet.Game.World.Sectors;

namespace SphereNet.Game.Diagnostics;

public static class SectorListDialog
{
    public const uint GumpId = 0xB07_0002;

    public const int BtnRefresh = 1;
    public const int BtnGoBase = 100;

    private const int BackgroundId = 9200;
    private const int ButtonOk = 4005;
    private const int ButtonOkPressed = 4007;

    public readonly record struct SectorEntry(
        int SectorX,
        int SectorY,
        byte MapIndex,
        int OnlinePlayerCount,
        int NpcCount,
        int ItemCount,
        bool IsSleeping);

    public static GumpBuilder Build(uint charSerial, List<SectorEntry> sectors, int totalNpcs, int totalPlayers)
    {
        const int width = 620;
        int rowHeight = 20;
        int headerY = 72;
        int listY = headerY + rowHeight + 4;
        int listH = Math.Min(sectors.Count * rowHeight, 360);
        int height = listY + listH + 65;
        height = Math.Max(height, 200);

        var gump = new GumpBuilder(charSerial, GumpId, width, height);

        gump.AddResizePic(0, 0, BackgroundId, width, height);
        gump.AddText(170, 15, 1153, "Sector List — Active Sectors");

        gump.AddGumpPicTiled(20, 42, width - 40, 2, 2620);

        gump.AddText(20, 48, 946, $"Players: {totalPlayers}");
        gump.AddText(160, 48, 946, $"Sectors: {sectors.Count}");
        gump.AddText(300, 48, 946, $"Total NPCs: {totalNpcs}");

        gump.AddGumpPicTiled(20, 68, width - 40, 2, 2620);

        // Column headers
        int col0 = 20;   // GO button
        int col1 = 60;   // Sector
        int col2 = 140;  // Map
        int col3 = 190;  // Players
        int col4 = 260;  // NPCs
        int col5 = 330;  // Items
        int col6 = 400;  // Sleep
        int col7 = 470;  // Coords

        gump.AddText(col0, headerY, 1153, "Go");
        gump.AddText(col1, headerY, 1153, "Sector");
        gump.AddText(col2, headerY, 1153, "Map");
        gump.AddText(col3, headerY, 1153, "Players");
        gump.AddText(col4, headerY, 1153, "NPCs");
        gump.AddText(col5, headerY, 1153, "Items");
        gump.AddText(col6, headerY, 1153, "Sleep");
        gump.AddText(col7, headerY, 1153, "Coords");

        int y = listY;
        for (int i = 0; i < sectors.Count; i++)
        {
            var s = sectors[i];

            int npcHue = s.NpcCount > 500 ? 37 : s.NpcCount > 100 ? 53 : 0;
            int playerHue = s.OnlinePlayerCount > 0 ? 68 : 0;
            int sleepHue = s.IsSleeping ? 37 : 68;

            gump.AddButton(col0, y + 1, ButtonOk, ButtonOkPressed, BtnGoBase + i);
            gump.AddText(col1, y, npcHue, $"({s.SectorX},{s.SectorY})");
            gump.AddText(col2, y, 0, $"{s.MapIndex}");
            gump.AddText(col3, y, playerHue, $"{s.OnlinePlayerCount}");
            gump.AddText(col4, y, npcHue, $"{s.NpcCount}");
            gump.AddText(col5, y, 0, $"{s.ItemCount}");
            gump.AddText(col6, y, sleepHue, s.IsSleeping ? "Yes" : "No");
            gump.AddText(col7, y, 946, $"{s.SectorX * Sector.SectorSize},{s.SectorY * Sector.SectorSize}");

            y += rowHeight;
        }

        // Footer
        gump.AddGumpPicTiled(20, height - 50, width - 40, 2, 2620);
        gump.AddButton(width / 2 - 50, height - 42, ButtonOk, ButtonOkPressed, BtnRefresh);
        gump.AddText(width / 2 - 15, height - 40, 0, "Refresh");

        return gump;
    }
}
