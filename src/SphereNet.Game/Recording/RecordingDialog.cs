using SphereNet.Game.Gumps;

namespace SphereNet.Game.Recording;

public enum RecordActionType { None, StartRecord, StopRecord, Play, StopReplay, Delete, Refresh }

public sealed class RecordDialogAction
{
    public RecordActionType Type { get; set; }
    public int SelectedIndex { get; set; } = -1;
}

public static class RecordingDialog
{
    public const uint GumpId = 0x0EC_0001;
    public const uint ReplayOverlayGumpId = 0x0EC_0002;

    private const int BtnStartRecord = 1;
    private const int BtnStopRecord = 2;
    private const int BtnRefresh = 3;
    private const int BtnPlayBase = 100;
    private const int BtnDeleteBase = 200;

    public const int OverlayBtnStop = 1;
    public const int OverlayBtnPlayPause = 2;
    public const int OverlayBtnRewind = 3;
    public const int OverlayBtnForward = 4;
    public const int OverlayBtnSpeed1x = 5;
    public const int OverlayBtnSpeed2x = 6;
    public const int OverlayBtnSpeed4x = 7;

    private const int BackgroundId = 9200;
    private const int ButtonOk = 4005;
    private const int ButtonOkPressed = 4007;
    private const int ButtonCancel = 4017;
    private const int ButtonCancelPressed = 4019;
    private const int ButtonSmall = 2117;
    private const int ButtonSmallPressed = 2118;

    public static GumpBuilder Build(uint charSerial, bool isRecording,
        List<(string Id, string Recorder, DateTime Date, int DurationMs, int PacketCount)> recordings)
    {
        int listHeight = Math.Max(recordings.Count * 25, 25);
        int totalHeight = 180 + listHeight;
        var gump = new GumpBuilder(charSerial, GumpId, 500, totalHeight);

        gump.AddResizePic(0, 0, BackgroundId, 500, totalHeight);
        gump.AddText(170, 15, 1153, "Recording Manager");
        gump.AddGumpPicTiled(20, 45, 460, 2, 2620);

        if (isRecording)
        {
            gump.AddText(20, 55, 37, "RECORDING...");
            gump.AddButton(180, 55, ButtonCancel, ButtonCancelPressed, BtnStopRecord);
            gump.AddText(215, 57, 0, "Stop Recording");
        }
        else
        {
            gump.AddButton(20, 55, ButtonOk, ButtonOkPressed, BtnStartRecord);
            gump.AddText(55, 57, 0, "Start Recording");
        }

        gump.AddButton(350, 55, ButtonSmall, ButtonSmallPressed, BtnRefresh);
        gump.AddText(375, 55, 0, "Refresh");

        gump.AddGumpPicTiled(20, 85, 460, 2, 2620);

        gump.AddText(20, 95, 946, "#");
        gump.AddText(45, 95, 946, "Recorder");
        gump.AddText(170, 95, 946, "Date");
        gump.AddText(310, 95, 946, "Duration");
        gump.AddText(390, 95, 946, "Actions");

        gump.AddGumpPicTiled(20, 115, 460, 2, 2620);

        if (recordings.Count == 0)
        {
            gump.AddText(20, 125, 0, "No recordings found.");
        }
        else
        {
            for (int i = 0; i < recordings.Count; i++)
            {
                int y = 120 + i * 25;
                var r = recordings[i];
                string duration = r.DurationMs >= 60000
                    ? $"{r.DurationMs / 60000}m {(r.DurationMs % 60000) / 1000}s"
                    : $"{r.DurationMs / 1000.0:F1}s";

                gump.AddText(20, y, 0, $"{i + 1}");
                gump.AddText(45, y, 0, r.Recorder.Length > 14 ? r.Recorder[..14] : r.Recorder);
                gump.AddText(170, y, 0, r.Date.ToLocalTime().ToString("MM/dd HH:mm"));
                gump.AddText(310, y, 0, $"{duration} ({r.PacketCount})");
                gump.AddButton(400, y, ButtonOk, ButtonOkPressed, BtnPlayBase + i);
                gump.AddButton(440, y, ButtonCancel, ButtonCancelPressed, BtnDeleteBase + i);
            }
        }

        return gump;
    }

    public static GumpBuilder BuildReplayOverlay(uint charSerial, string recorderName,
        int totalMs, int currentMs, bool isPaused, float speed)
    {
        var gump = new GumpBuilder(charSerial, ReplayOverlayGumpId, 360, 95);
        gump.SetNoDispose();

        gump.AddResizePic(0, 0, BackgroundId, 360, 95);

        string status = isPaused ? "PAUSED" : "PLAYING";
        gump.AddText(10, 5, isPaused ? 37 : 67, status);
        string nameDisplay = recorderName.Length > 15 ? recorderName[..15] : recorderName;
        gump.AddText(85, 5, 0, nameDisplay);
        gump.AddText(280, 5, 946, $"x{speed:G}");

        gump.AddButton(330, 5, ButtonCancel, ButtonCancelPressed, OverlayBtnStop);

        gump.AddText(10, 28, 0, $"{FormatTime(currentMs)} / {FormatTime(totalMs)}");

        int barX = 120;
        int barWidth = 220;
        int filledBars = totalMs > 0 ? (int)((long)currentMs * barWidth / totalMs) : 0;
        filledBars = Math.Clamp(filledBars, 0, barWidth);
        gump.AddGumpPicTiled(barX, 34, barWidth, 2, 2620);
        if (filledBars > 0)
            gump.AddGumpPicTiled(barX, 33, filledBars, 4, 2621);

        int btnY = 58;

        gump.AddButton(10, btnY, ButtonSmall, ButtonSmallPressed, OverlayBtnRewind);
        gump.AddText(28, btnY, 0, "-10s");

        if (isPaused)
        {
            gump.AddButton(80, btnY, ButtonOk, ButtonOkPressed, OverlayBtnPlayPause);
            gump.AddText(115, btnY + 2, 0, "Play");
        }
        else
        {
            gump.AddButton(80, btnY, ButtonCancel, ButtonCancelPressed, OverlayBtnPlayPause);
            gump.AddText(115, btnY + 2, 0, "Pause");
        }

        gump.AddButton(165, btnY, ButtonSmall, ButtonSmallPressed, OverlayBtnForward);
        gump.AddText(183, btnY, 0, "+10s");

        gump.AddButton(235, btnY, ButtonSmall, ButtonSmallPressed, OverlayBtnSpeed1x);
        gump.AddText(253, btnY, speed == 1f ? 67 : 0, "1x");
        gump.AddButton(278, btnY, ButtonSmall, ButtonSmallPressed, OverlayBtnSpeed2x);
        gump.AddText(296, btnY, speed == 2f ? 67 : 0, "2x");
        gump.AddButton(318, btnY, ButtonSmall, ButtonSmallPressed, OverlayBtnSpeed4x);
        gump.AddText(336, btnY, speed == 4f ? 67 : 0, "4x");

        return gump;
    }

    private static string FormatTime(int ms)
    {
        int totalSeconds = ms / 1000;
        int minutes = totalSeconds / 60;
        int seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:D2}";
    }

    public static RecordDialogAction ParseResponse(uint buttonId)
    {
        if (buttonId == 0)
            return new RecordDialogAction { Type = RecordActionType.None };
        if (buttonId == BtnStartRecord)
            return new RecordDialogAction { Type = RecordActionType.StartRecord };
        if (buttonId == BtnStopRecord)
            return new RecordDialogAction { Type = RecordActionType.StopRecord };
        if (buttonId == BtnRefresh)
            return new RecordDialogAction { Type = RecordActionType.Refresh };
        if (buttonId >= BtnDeleteBase)
            return new RecordDialogAction { Type = RecordActionType.Delete, SelectedIndex = (int)(buttonId - BtnDeleteBase) };
        if (buttonId >= BtnPlayBase)
            return new RecordDialogAction { Type = RecordActionType.Play, SelectedIndex = (int)(buttonId - BtnPlayBase) };

        return new RecordDialogAction { Type = RecordActionType.None };
    }
}
