using SphereNet.Game.Gumps;

namespace SphereNet.Game.Recording;

public enum StateRecAction
{
    None,
    SelectChar,
    PlayHour,
    PlayLast30,
    PinHour,
    UnpinPeriod,
    ShareHour,
    UnshareView,
    WatchShared,
    PageNext,
    PagePrev,
    BackToList,
    Close
}

public sealed record StateRecResponse(StateRecAction Action, int Index = -1);

public static class StateRecordingDialog
{
    public const uint GumpId = 0x0ED_0001;
    private const int ItemsPerPage = 10;
    private const int BgGump = 9200;
    private const int BtnOk = 0xFA5;
    private const int BtnOkP = 0xFA7;
    private const int BtnSmall = 0xFAB;
    private const int BtnSmallP = 0xFAD;
    private const int BtnCancel = 0xFB1;
    private const int BtnCancelP = 0xFB3;

    private const int BtnPagePrev = 9990;
    private const int BtnPageNext = 9991;
    private const int BtnBackToList = 9992;

    // ----------------------------------------------------------------
    //  Admin character browser
    // ----------------------------------------------------------------

    public static GumpBuilder BuildCharacterList(
        uint charSerial,
        List<(uint Uid, string Name, bool IsPlayer, string LastSeen, int Records)> characters,
        int page, long dbSizeMb)
    {
        int totalPages = Math.Max(1, (characters.Count + ItemsPerPage - 1) / ItemsPerPage);
        page = Math.Clamp(page, 0, totalPages - 1);
        int startIdx = page * ItemsPerPage;
        int endIdx = Math.Min(startIdx + ItemsPerPage, characters.Count);

        var g = new GumpBuilder(charSerial, GumpId, 520, 380)
            { ExplicitX = 80, ExplicitY = 60 };
        g.SetNoDispose();
        g.AddResizePic(0, 0, BgGump, 520, 380);

        g.AddText(15, 10, 67, "State Recording Browser");
        g.AddText(350, 10, 946, $"DB: {dbSizeMb} MB");

        g.AddText(15, 35, 0, "Character");
        g.AddText(200, 35, 0, "Type");
        g.AddText(260, 35, 0, "Last Seen");
        g.AddText(420, 35, 0, "Records");

        int y = 58;
        for (int i = startIdx; i < endIdx; i++)
        {
            var (uid, name, isPlayer, lastSeen, records) = characters[i];
            g.AddButton(15, y + 2, BtnSmall, BtnSmallP, 100 + i);
            g.AddText(35, y, 0, name.Length > 18 ? name[..18] : name);
            g.AddText(200, y, isPlayer ? 67 : 946, isPlayer ? "Player" : "NPC");
            g.AddText(260, y, 0, lastSeen);
            g.AddText(420, y, 0, records.ToString());
            y += 22;
        }

        y = 345;
        if (page > 0)
            g.AddButton(180, y, BtnSmall, BtnSmallP, BtnPagePrev);
        g.AddText(200, y, 0, $"Page {page + 1}/{totalPages}");
        if (page < totalPages - 1)
            g.AddButton(310, y, BtnSmall, BtnSmallP, BtnPageNext);

        g.AddButton(480, y, BtnCancel, BtnCancelP, 0);

        return g;
    }

    // ----------------------------------------------------------------
    //  Admin hour bucket view for a single character
    // ----------------------------------------------------------------

    public static GumpBuilder BuildHourBuckets(
        uint charSerial,
        uint targetUid, string targetName,
        List<(string HourKey, string Display, int Snapshots, int Moves)> hours,
        int page)
    {
        int totalPages = Math.Max(1, (hours.Count + ItemsPerPage - 1) / ItemsPerPage);
        page = Math.Clamp(page, 0, totalPages - 1);
        int startIdx = page * ItemsPerPage;
        int endIdx = Math.Min(startIdx + ItemsPerPage, hours.Count);

        var g = new GumpBuilder(charSerial, GumpId, 520, 420)
            { ExplicitX = 80, ExplicitY = 60 };
        g.SetNoDispose();
        g.AddResizePic(0, 0, BgGump, 520, 420);

        g.AddText(15, 10, 67, $"History: {targetName}");

        g.AddButton(15, 38, BtnSmall, BtnSmallP, BtnBackToList);
        g.AddText(35, 38, 0, "< Back");

        g.AddButton(350, 38, BtnOk, BtnOkP, 50);
        g.AddText(385, 40, 67, "Play Last 30m");

        g.AddText(15, 65, 0, "Hour (UTC)");
        g.AddText(200, 65, 0, "Snapshots");
        g.AddText(300, 65, 0, "Moves");
        g.AddText(380, 65, 0, "Actions");

        int y = 88;
        for (int i = startIdx; i < endIdx; i++)
        {
            var (hourKey, display, snapshots, moves) = hours[i];
            g.AddText(15, y, 0, display);
            g.AddText(210, y, 0, snapshots.ToString());
            g.AddText(310, y, 0, moves.ToString());
            g.AddButton(380, y + 2, BtnSmall, BtnSmallP, 200 + i);
            g.AddText(400, y, 67, "Play");
            g.AddButton(440, y + 2, BtnSmall, BtnSmallP, 300 + i);
            g.AddText(460, y, 946, "Pin");
            g.AddButton(490, y + 2, BtnSmall, BtnSmallP, 400 + i);
            g.AddText(505, y, 37, "Share");
            y += 22;
        }

        y = 385;
        if (page > 0)
            g.AddButton(180, y, BtnSmall, BtnSmallP, BtnPagePrev);
        g.AddText(200, y, 0, $"Page {page + 1}/{totalPages}");
        if (page < totalPages - 1)
            g.AddButton(310, y, BtnSmall, BtnSmallP, BtnPageNext);

        g.AddButton(480, y, BtnCancel, BtnCancelP, 0);

        return g;
    }

    // ----------------------------------------------------------------
    //  Player shared recordings view
    // ----------------------------------------------------------------

    public static GumpBuilder BuildSharedList(
        uint charSerial,
        List<(int Id, string Label, string CharName, string TimeRange, string SharedBy)> items,
        int page)
    {
        int totalPages = Math.Max(1, (items.Count + ItemsPerPage - 1) / ItemsPerPage);
        page = Math.Clamp(page, 0, totalPages - 1);
        int startIdx = page * ItemsPerPage;
        int endIdx = Math.Min(startIdx + ItemsPerPage, items.Count);

        var g = new GumpBuilder(charSerial, GumpId, 480, 360)
            { ExplicitX = 100, ExplicitY = 80 };
        g.SetNoDispose();
        g.AddResizePic(0, 0, BgGump, 480, 360);

        g.AddText(15, 10, 67, "Shared Recordings");

        g.AddText(15, 35, 0, "Recording");
        g.AddText(250, 35, 0, "Character");
        g.AddText(370, 35, 0, "Time");

        int y = 58;
        for (int i = startIdx; i < endIdx; i++)
        {
            var (id, label, charName, timeRange, sharedBy) = items[i];
            g.AddButton(15, y + 2, BtnOk, BtnOkP, 500 + i);
            g.AddText(50, y, 0, label.Length > 22 ? label[..22] : label);
            g.AddText(250, y, 0, charName.Length > 12 ? charName[..12] : charName);
            g.AddText(370, y, 946, timeRange);
            y += 22;
        }

        y = 325;
        if (page > 0)
            g.AddButton(160, y, BtnSmall, BtnSmallP, BtnPagePrev);
        g.AddText(180, y, 0, $"Page {page + 1}/{totalPages}");
        if (page < totalPages - 1)
            g.AddButton(290, y, BtnSmall, BtnSmallP, BtnPageNext);

        g.AddButton(440, y, BtnCancel, BtnCancelP, 0);

        return g;
    }

    // ----------------------------------------------------------------
    //  Response parser
    // ----------------------------------------------------------------

    public static StateRecResponse ParseResponse(uint buttonId)
    {
        if (buttonId == 0) return new(StateRecAction.Close);
        if (buttonId == BtnPagePrev) return new(StateRecAction.PagePrev);
        if (buttonId == BtnPageNext) return new(StateRecAction.PageNext);
        if (buttonId == BtnBackToList) return new(StateRecAction.BackToList);
        if (buttonId == 50) return new(StateRecAction.PlayLast30);

        if (buttonId >= 500) return new(StateRecAction.WatchShared, (int)(buttonId - 500));
        if (buttonId >= 400) return new(StateRecAction.ShareHour, (int)(buttonId - 400));
        if (buttonId >= 300) return new(StateRecAction.PinHour, (int)(buttonId - 300));
        if (buttonId >= 200) return new(StateRecAction.PlayHour, (int)(buttonId - 200));
        if (buttonId >= 100) return new(StateRecAction.SelectChar, (int)(buttonId - 100));

        return new(StateRecAction.None);
    }
}
