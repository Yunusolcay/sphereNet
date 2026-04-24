using SphereNet.Game.Clients;
using SphereNet.Game.Macro;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Server.Macro;

public sealed class MacroEngine
{
    private readonly int _maxSteps;
    private readonly int _maxLoopMinutes;

    private readonly Dictionary<uint, RecordingState> _recording = [];
    private readonly Dictionary<uint, PlaybackState> _playing = [];
    private readonly Dictionary<uint, MacroSession> _lastRecorded = [];

    public MacroEngine(int maxSteps, int maxLoopMinutes)
    {
        _maxSteps = maxSteps;
        _maxLoopMinutes = maxLoopMinutes;
    }

    // ----------------------------------------------------------------
    //  Recording
    // ----------------------------------------------------------------

    public bool IsRecording(uint charUid) => _recording.ContainsKey(charUid);

    public void StartRecording(uint charUid)
    {
        StopPlayback(charUid);
        _recording[charUid] = new RecordingState();
    }

    public MacroSession? StopRecording(uint charUid)
    {
        if (!_recording.Remove(charUid, out var state)) return null;
        if (state.Session.Steps.Count == 0) return null;
        _lastRecorded[charUid] = state.Session;
        return state.Session;
    }

    public void CaptureUseObject(uint charUid, ushort dispId)
    {
        if (!_recording.TryGetValue(charUid, out var state)) return;
        if (state.Session.Steps.Count >= _maxSteps) return;
        AddStep(state, new MacroStep { Type = MacroStepType.UseObject, ItemDispId = dispId });
    }

    public void CaptureUseSkill(uint charUid, int skillId)
    {
        if (!_recording.TryGetValue(charUid, out var state)) return;
        if (state.Session.Steps.Count >= _maxSteps) return;
        AddStep(state, new MacroStep { Type = MacroStepType.UseSkill, SkillId = skillId });
    }

    public void CaptureTarget(uint charUid, uint serial, short x, short y, sbyte z, ushort graphic,
        uint selfUid)
    {
        if (!_recording.TryGetValue(charUid, out var state)) return;
        if (state.Session.Steps.Count >= _maxSteps) return;

        MacroStep step;
        if (serial != 0 && serial == selfUid)
            step = new MacroStep { Type = MacroStepType.TargetSelf };
        else if (serial != 0)
            step = new MacroStep
            {
                Type = MacroStepType.TargetObject, Serial = serial,
                X = x, Y = y, Z = z
            };
        else
            step = new MacroStep
            {
                Type = MacroStepType.TargetLocation,
                X = x, Y = y, Z = z, Graphic = graphic
            };

        AddStep(state, step);
    }

    private static void AddStep(RecordingState state, MacroStep step)
    {
        long now = Environment.TickCount64;
        int delay = state.Session.Steps.Count == 0
            ? 0
            : Math.Clamp((int)(now - state.LastCaptureTick), 100, 30_000);

        var finalStep = new MacroStep
        {
            Type = step.Type,
            DelayMs = delay,
            ItemDispId = step.ItemDispId,
            SkillId = step.SkillId,
            X = step.X,
            Y = step.Y,
            Z = step.Z,
            Graphic = step.Graphic,
            Serial = step.Serial,
        };
        state.Session.Steps.Add(finalStep);
        state.LastCaptureTick = now;
    }

    // ----------------------------------------------------------------
    //  Playback
    // ----------------------------------------------------------------

    public bool IsPlaying(uint charUid) => _playing.ContainsKey(charUid);

    public bool HasRecording(uint charUid) => _lastRecorded.ContainsKey(charUid);

    public MacroSession? GetRecording(uint charUid) =>
        _lastRecorded.GetValueOrDefault(charUid);

    public bool StartPlayback(uint charUid, bool loop)
    {
        if (!_lastRecorded.TryGetValue(charUid, out var session)) return false;
        if (session.Steps.Count == 0) return false;
        StopRecording(charUid);

        long now = Environment.TickCount64;
        _playing[charUid] = new PlaybackState
        {
            CharUid = charUid,
            Session = session,
            IsLooping = loop,
            StartMs = now,
            MaxDurationMs = (long)_maxLoopMinutes * 60_000,
            NextStepMs = now,
        };
        return true;
    }

    public void StopPlayback(uint charUid)
    {
        _playing.Remove(charUid);
    }

    public void OnCharDisconnect(uint charUid)
    {
        _recording.Remove(charUid);
        _playing.Remove(charUid);
    }

    // ----------------------------------------------------------------
    //  Tick — called every server tick
    // ----------------------------------------------------------------

    public void Tick(long nowMs,
        Func<uint, GameClient?> getClient,
        Func<Character, ushort, Item?> findItemInBackpack,
        Action<uint, string> sendMessage)
    {
        if (_playing.Count == 0) return;

        foreach (var uid in _playing.Keys.ToArray())
        {
            if (!_playing.TryGetValue(uid, out var state)) continue;

            var client = getClient(uid);
            if (client?.Character == null || client.Character.IsDeleted || client.Character.IsDead)
            {
                _playing.Remove(uid);
                sendMessage(uid, "Macro stopped.");
                continue;
            }

            if (nowMs < state.NextStepMs) continue;

            if (!state.IsLooping && state.CurrentStep >= state.Session.Steps.Count)
            {
                _playing.Remove(uid);
                sendMessage(uid, "Macro completed.");
                continue;
            }

            if (state.IsLooping && nowMs - state.StartMs > state.MaxDurationMs)
            {
                _playing.Remove(uid);
                sendMessage(uid, $"Macro stopped: {_maxLoopMinutes} min limit reached.");
                continue;
            }

            var step = state.Session.Steps[state.CurrentStep];

            if (IsTargetStep(step.Type) && !client.HasPendingTarget)
            {
                if (!state.WaitingForTarget)
                {
                    state.WaitingForTarget = true;
                    state.WaitTargetDeadline = nowMs + 5_000;
                    continue;
                }
                if (nowMs < state.WaitTargetDeadline) continue;
                state.WaitingForTarget = false;
                AdvanceStep(state, nowMs);
                continue;
            }
            state.WaitingForTarget = false;

            ExecuteStep(state, step, client, findItemInBackpack, sendMessage, nowMs);
        }
    }

    private void ExecuteStep(PlaybackState state, MacroStep step, GameClient client,
        Func<Character, ushort, Item?> findItemInBackpack,
        Action<uint, string> sendMessage, long nowMs)
    {
        var ch = client.Character!;

        switch (step.Type)
        {
            case MacroStepType.UseObject:
            {
                var item = findItemInBackpack(ch, step.ItemDispId);
                if (item == null)
                {
                    item = FindEquippedItem(ch, step.ItemDispId);
                }
                if (item == null)
                {
                    _playing.Remove(state.CharUid);
                    sendMessage(state.CharUid, "Macro stopped: item not found.");
                    return;
                }
                client.HandleDoubleClick(item.Uid.Value);
                break;
            }

            case MacroStepType.UseSkill:
                client.HandleUseSkill(step.SkillId);
                break;

            case MacroStepType.TargetLocation:
                client.HandleTargetResponse(1, 0, 0, step.X, step.Y, step.Z, step.Graphic);
                break;

            case MacroStepType.TargetObject:
                client.HandleTargetResponse(0, 0, step.Serial, step.X, step.Y, step.Z, 0);
                break;

            case MacroStepType.TargetSelf:
                client.HandleTargetResponse(0, 0, ch.Uid.Value, ch.X, ch.Y, ch.Z, 0);
                break;
        }

        AdvanceStep(state, nowMs);
    }

    private void AdvanceStep(PlaybackState state, long nowMs)
    {
        state.CurrentStep++;
        if (state.CurrentStep >= state.Session.Steps.Count)
        {
            if (state.IsLooping)
                state.CurrentStep = 0;
            else
                return;
        }

        int nextDelay = state.Session.Steps[state.CurrentStep].DelayMs;
        state.NextStepMs = nowMs + Math.Max(nextDelay, 200);
    }

    private static Item? FindEquippedItem(Character ch, ushort dispId)
    {
        for (int i = 1; i <= 25; i++)
        {
            var item = ch.GetEquippedItem((SphereNet.Core.Enums.Layer)i);
            if (item != null && item.DispIdFull == dispId) return item;
        }
        return null;
    }

    private static bool IsTargetStep(MacroStepType type) =>
        type is MacroStepType.TargetLocation or MacroStepType.TargetObject or MacroStepType.TargetSelf;

    // ----------------------------------------------------------------
    //  Internal state types
    // ----------------------------------------------------------------

    private sealed class RecordingState
    {
        public MacroSession Session { get; } = new();
        public long LastCaptureTick { get; set; } = Environment.TickCount64;
    }

    private sealed class PlaybackState
    {
        public uint CharUid { get; init; }
        public MacroSession Session { get; init; } = null!;
        public int CurrentStep { get; set; }
        public bool IsLooping { get; init; }
        public long NextStepMs { get; set; }
        public long StartMs { get; init; }
        public long MaxDurationMs { get; init; }
        public bool WaitingForTarget { get; set; }
        public long WaitTargetDeadline { get; set; }
    }
}
