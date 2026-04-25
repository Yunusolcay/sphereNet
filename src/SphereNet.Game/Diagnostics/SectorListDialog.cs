using System.Text;
using SphereNet.Game.Gumps;
using SphereNet.Game.World.Sectors;

namespace SphereNet.Game.Diagnostics;

public static class SectorListDialog
{
    public const uint GumpId = 0xB07_0002;

    private const int BtnRefresh = 1;

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
        const int width = 560;
        const int height = 500;

        var gump = new GumpBuilder(charSerial, GumpId, width, height);

        gump.AddResizePic(0, 0, BackgroundId, width, height);
        gump.AddText(150, 15, 1153, "Sector List — Active Sectors");

        gump.AddGumpPicTiled(20, 42, width - 40, 2, 2620);

        gump.AddText(20, 48, 946, $"Players: {totalPlayers}");
        gump.AddText(160, 48, 946, $"Sectors: {sectors.Count}");
        gump.AddText(300, 48, 946, $"Total NPCs: {totalNpcs}");

        gump.AddGumpPicTiled(20, 68, width - 40, 2, 2620);

        var sb = new StringBuilder();
        sb.Append("<TABLE border=0 cellpadding=2 cellspacing=0 width=510>");
        sb.Append("<TR>");
        sb.Append("<TD width=70><BASEFONT color=#FFCC00><B>Sector</B></BASEFONT></TD>");
        sb.Append("<TD width=40><BASEFONT color=#FFCC00><B>Map</B></BASEFONT></TD>");
        sb.Append("<TD width=60><BASEFONT color=#FFCC00><B>Players</B></BASEFONT></TD>");
        sb.Append("<TD width=60><BASEFONT color=#FFCC00><B>NPCs</B></BASEFONT></TD>");
        sb.Append("<TD width=60><BASEFONT color=#FFCC00><B>Items</B></BASEFONT></TD>");
        sb.Append("<TD width=50><BASEFONT color=#FFCC00><B>Sleep</B></BASEFONT></TD>");
        sb.Append("<TD width=80><BASEFONT color=#FFCC00><B>Coords</B></BASEFONT></TD>");
        sb.Append("</TR>");

        foreach (var s in sectors)
        {
            string color = s.NpcCount > 500 ? "#FF4444" : s.NpcCount > 100 ? "#FFAA00" : "#FFFFFF";
            string playerColor = s.OnlinePlayerCount > 0 ? "#44FF44" : "#FFFFFF";
            string sleepColor = s.IsSleeping ? "#FF4444" : "#44FF44";

            sb.Append("<TR>");
            sb.Append($"<TD><BASEFONT color={color}>({s.SectorX},{s.SectorY})</BASEFONT></TD>");
            sb.Append($"<TD><BASEFONT color=#FFFFFF>{s.MapIndex}</BASEFONT></TD>");
            sb.Append($"<TD><BASEFONT color={playerColor}>{s.OnlinePlayerCount}</BASEFONT></TD>");
            sb.Append($"<TD><BASEFONT color={color}>{s.NpcCount}</BASEFONT></TD>");
            sb.Append($"<TD><BASEFONT color=#FFFFFF>{s.ItemCount}</BASEFONT></TD>");
            sb.Append($"<TD><BASEFONT color={sleepColor}>{(s.IsSleeping ? "Yes" : "No")}</BASEFONT></TD>");
            sb.Append($"<TD><BASEFONT color=#AAAAAA>{s.SectorX * Sector.SectorSize},{s.SectorY * Sector.SectorSize}</BASEFONT></TD>");
            sb.Append("</TR>");
        }

        sb.Append("</TABLE>");

        gump.AddHtmlGump(15, 72, width - 30, height - 130, sb.ToString(), false, true);

        // Footer
        gump.AddGumpPicTiled(20, height - 50, width - 40, 2, 2620);
        gump.AddButton(width / 2 - 50, height - 42, ButtonOk, ButtonOkPressed, BtnRefresh);
        gump.AddText(width / 2 - 15, height - 40, 0, "Refresh");

        return gump;
    }
}
