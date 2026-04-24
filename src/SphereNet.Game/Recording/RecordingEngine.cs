using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Recording;

public sealed class RecordedPacket
{
    public int TickOffset { get; set; }
    public byte[] Data { get; set; } = [];
}

public sealed class RecordingSession
{
    public string Id { get; set; } = "";
    public string RecorderName { get; set; } = "";
    public uint RecorderUid { get; set; }
    public Point3D Center { get; set; }
    public long StartTick { get; set; }
    public int CaptureRange { get; set; } = 18;
    public List<RecordedPacket> Packets { get; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int DurationMs => Packets.Count > 0 ? Packets[^1].TickOffset : 0;
}

public sealed class ReplayState
{
    public RecordingSession Session { get; set; } = null!;
    public int PacketIndex { get; set; }
    public long StartTick { get; set; }
    public Point3D OriginalPosition { get; set; }
    public bool WasInvisible { get; set; }
    public Dictionary<uint, uint> SerialMap { get; } = [];
    public uint NextPhantomSerial { get; set; } = 0x3FFF0001;
    public uint NextPhantomItemSerial { get; set; } = 0x7FFE0001;
    public bool IsPaused { get; set; }
    public int PausedAtOffsetMs { get; set; }
    public float PlaybackSpeed { get; set; } = 1.0f;
    public long LastOverlayTick { get; set; }
}

public sealed class RecordingEngine
{
    private readonly Dictionary<uint, RecordingSession> _activeRecordings = [];
    private readonly Dictionary<uint, ReplayState> _activeReplays = [];
    private readonly string _recordingsDir;

    public RecordingEngine(string recordingsDir)
    {
        _recordingsDir = recordingsDir;
        Directory.CreateDirectory(_recordingsDir);
    }

    public bool IsRecording(uint charUid) => _activeRecordings.ContainsKey(charUid);
    public bool IsReplaying(uint charUid) => _activeReplays.ContainsKey(charUid);
    public bool HasActiveRecordings => _activeRecordings.Count > 0;

    public Func<Point3D, int, List<byte[]>>? SnapshotNearbyCharacters { get; set; }

    public void StartRecording(Character recorder, int captureRange = 18)
    {
        uint uid = recorder.Uid.Value;
        if (_activeRecordings.ContainsKey(uid)) return;

        var session = new RecordingSession
        {
            Id = $"rec_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{uid:X8}",
            RecorderName = recorder.Name ?? "Unknown",
            RecorderUid = uid,
            Center = recorder.Position,
            StartTick = Environment.TickCount64,
            CaptureRange = captureRange
        };

        var snapshots = SnapshotNearbyCharacters?.Invoke(recorder.Position, captureRange);
        if (snapshots != null)
        {
            foreach (var pkt in snapshots)
                session.Packets.Add(new RecordedPacket { TickOffset = 0, Data = pkt });
        }

        _activeRecordings[uid] = session;
    }

    public RecordingSession? StopRecording(uint charUid)
    {
        if (!_activeRecordings.Remove(charUid, out var session))
            return null;

        SaveRecording(session);
        return session;
    }

    public void CapturePacket(uint recorderUid, Point3D packetOrigin, byte[] rawPacket)
    {
        if (!_activeRecordings.TryGetValue(recorderUid, out var session))
            return;

        if (session.Center.GetDistanceTo(packetOrigin) > session.CaptureRange)
            return;

        int offset = (int)(Environment.TickCount64 - session.StartTick);
        session.Packets.Add(new RecordedPacket
        {
            TickOffset = offset,
            Data = rawPacket
        });
    }

    public void CaptureFromBroadcast(Point3D broadcastCenter, int broadcastRange, byte[] rawPacket,
        uint moverUid = 0)
    {
        foreach (var (uid, session) in _activeRecordings)
        {
            bool isRecorder = moverUid != 0 && moverUid == session.RecorderUid;
            if (isRecorder)
            {
                session.Center = broadcastCenter;
            }

            if (isRecorder || session.Center.GetDistanceTo(broadcastCenter) <= session.CaptureRange + broadcastRange)
            {
                int offset = (int)(Environment.TickCount64 - session.StartTick);
                session.Packets.Add(new RecordedPacket
                {
                    TickOffset = offset,
                    Data = rawPacket
                });
            }
        }
    }

    public ReplayState? StartReplay(Character viewer, RecordingSession session)
    {
        uint uid = viewer.Uid.Value;
        if (_activeReplays.ContainsKey(uid)) return null;

        var state = new ReplayState
        {
            Session = session,
            PacketIndex = 0,
            StartTick = Environment.TickCount64,
            OriginalPosition = viewer.Position,
            WasInvisible = viewer.IsInvisible
        };
        _activeReplays[uid] = state;
        return state;
    }

    public ReplayState? GetReplayState(uint charUid)
    {
        _activeReplays.TryGetValue(charUid, out var state);
        return state;
    }

    public void StopReplay(uint charUid)
    {
        _activeReplays.Remove(charUid);
    }

    public bool HasActiveReplays => _activeReplays.Count > 0;
    public IEnumerable<uint> GetActiveReplayUids() => _activeReplays.Keys;

    public int GetElapsedMs(uint charUid)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state))
            return 0;
        return GetElapsedMs(state);
    }

    private static int GetElapsedMs(ReplayState state)
    {
        if (state.IsPaused)
            return state.PausedAtOffsetMs;
        int elapsed = (int)((Environment.TickCount64 - state.StartTick) * state.PlaybackSpeed);
        return Math.Clamp(elapsed, 0, state.Session.DurationMs);
    }

    public void PauseReplay(uint charUid)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state) || state.IsPaused)
            return;
        state.PausedAtOffsetMs = GetElapsedMs(state);
        state.IsPaused = true;
        state.LastOverlayTick = 0;
    }

    public void ResumeReplay(uint charUid)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state) || !state.IsPaused)
            return;
        long now = Environment.TickCount64;
        state.StartTick = now - (long)(state.PausedAtOffsetMs / (double)state.PlaybackSpeed);
        state.IsPaused = false;
        state.LastOverlayTick = 0;
    }

    public void SetPlaybackSpeed(uint charUid, float speed)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state))
            return;
        if (!state.IsPaused)
        {
            int currentMs = GetElapsedMs(state);
            state.PlaybackSpeed = speed;
            long now = Environment.TickCount64;
            state.StartTick = now - (long)(currentMs / (double)speed);
        }
        else
        {
            state.PlaybackSpeed = speed;
        }
        state.LastOverlayTick = 0;
    }

    public void SeekReplay(uint charUid, int targetMs,
        Action<uint, byte[]> sendRawPacket,
        Action<uint, short, short, sbyte, byte>? onCameraUpdate = null)
    {
        if (!_activeReplays.TryGetValue(charUid, out var state))
            return;

        targetMs = Math.Clamp(targetMs, 0, state.Session.DurationMs);

        foreach (uint phantom in state.SerialMap.Values)
        {
            var del = new byte[5];
            del[0] = 0x1D;
            WriteUInt32(del, 1, phantom);
            sendRawPacket(charUid, del);
        }

        state.SerialMap.Clear();
        state.NextPhantomSerial = 0x3FFF0001;
        state.NextPhantomItemSerial = 0x7FFE0001;

        var packets = state.Session.Packets;
        short lastX = 0, lastY = 0;
        sbyte lastZ = 0;
        byte lastDir = 0;
        bool hasCam = false;
        state.PacketIndex = packets.Count;

        for (int i = 0; i < packets.Count; i++)
        {
            if (packets[i].TickOffset > targetMs)
            {
                state.PacketIndex = i;
                break;
            }

            var data = RemapSerials(packets[i].Data, charUid, state);
            if (data != null)
            {
                sendRawPacket(charUid, data);

                if (packets[i].Data.Length >= 12 && packets[i].Data[0] == 0x77)
                {
                    uint origSerial = ReadUInt32(packets[i].Data, 1);
                    if (origSerial == state.Session.RecorderUid)
                    {
                        lastX = (short)(data[7] << 8 | data[8]);
                        lastY = (short)(data[9] << 8 | data[10]);
                        lastZ = (sbyte)data[11];
                        lastDir = (byte)(data[12] & 0x87);
                        hasCam = true;
                    }
                }
            }
        }

        long now = Environment.TickCount64;
        if (state.IsPaused)
            state.PausedAtOffsetMs = targetMs;
        else
            state.StartTick = now - (long)(targetMs / (double)state.PlaybackSpeed);

        state.LastOverlayTick = 0;

        if (hasCam)
            onCameraUpdate?.Invoke(charUid, lastX, lastY, lastZ, lastDir);
    }

    public void TickReplays(Action<uint, byte[]> sendRawPacket, Action<uint>? onReplayFinished,
        Action<uint, short, short, sbyte, byte>? onCameraUpdate = null)
    {
        long now = Environment.TickCount64;
        List<uint>? finished = null;

        foreach (var (uid, state) in _activeReplays)
        {
            if (state.IsPaused)
                continue;

            int elapsed = (int)((now - state.StartTick) * state.PlaybackSpeed);
            var packets = state.Session.Packets;

            while (state.PacketIndex < packets.Count)
            {
                var pkt = packets[state.PacketIndex];
                if (pkt.TickOffset > elapsed)
                    break;

                var data = RemapSerials(pkt.Data, uid, state);
                if (data != null)
                {
                    sendRawPacket(uid, data);

                    if (onCameraUpdate != null && pkt.Data.Length >= 12 && pkt.Data[0] == 0x77)
                    {
                        uint origSerial = ReadUInt32(pkt.Data, 1);
                        if (origSerial == state.Session.RecorderUid)
                        {
                            short x = (short)(data[7] << 8 | data[8]);
                            short y = (short)(data[9] << 8 | data[10]);
                            sbyte z = (sbyte)data[11];
                            byte dir = (byte)(data[12] & 0x87);
                            onCameraUpdate(uid, x, y, z, dir);
                        }
                    }
                }
                state.PacketIndex++;
            }

            if (state.PacketIndex >= packets.Count)
            {
                finished ??= [];
                finished.Add(uid);
            }
        }

        if (finished != null)
        {
            foreach (uint uid in finished)
            {
                onReplayFinished?.Invoke(uid);
                if (_activeReplays.ContainsKey(uid))
                    _activeReplays.Remove(uid);
            }
        }
    }

    public List<uint> GetPhantomSerials(uint viewerUid)
    {
        if (!_activeReplays.TryGetValue(viewerUid, out var state))
            return [];
        return [.. state.SerialMap.Values];
    }

    private static byte[]? RemapSerials(byte[] data, uint viewerUid, ReplayState state)
    {
        if (data.Length < 2) return null;
        byte opcode = data[0];

        if (opcode == 0x20 || opcode == 0x22 || opcode == 0x21)
            return null;

        if (opcode == 0x78)
            return RemapDrawObject(data, state);

        int[] serialOffsets = GetSerialOffsets(opcode, data.Length);
        if (serialOffsets.Length == 0)
            return data;

        byte[] copy = (byte[])data.Clone();
        foreach (int offset in serialOffsets)
        {
            RemapSerialAt(copy, offset, state);
        }
        return copy;
    }

    private static byte[]? RemapDrawObject(byte[] data, ReplayState state)
    {
        if (data.Length < 19) return null;
        byte[] copy = (byte[])data.Clone();

        RemapSerialAt(copy, 3, state);

        int pos = 19;
        while (pos + 4 <= copy.Length)
        {
            uint itemSerial = ReadUInt32(copy, pos);
            if (itemSerial == 0) break;

            RemapItemSerialAt(copy, pos, state);
            pos += 4;

            if (pos + 2 > copy.Length) break;
            ushort itemId = (ushort)(copy[pos] << 8 | copy[pos + 1]);
            if ((itemId & 0x8000) != 0)
            {
                pos += 2 + 1 + 2; // itemId + layer + hue
            }
            else
            {
                pos += 2 + 1; // itemId + layer
            }
        }

        return copy;
    }

    private static void RemapSerialAt(byte[] data, int offset, ReplayState state)
    {
        if (offset + 4 > data.Length) return;
        uint serial = ReadUInt32(data, offset);
        if (serial == 0) return;

        if (!state.SerialMap.TryGetValue(serial, out uint phantom))
        {
            bool isItem = (serial & 0x40000000) != 0;
            phantom = isItem ? state.NextPhantomItemSerial++ : state.NextPhantomSerial++;
            state.SerialMap[serial] = phantom;
        }
        WriteUInt32(data, offset, phantom);
    }

    private static void RemapItemSerialAt(byte[] data, int offset, ReplayState state)
    {
        if (offset + 4 > data.Length) return;
        uint serial = ReadUInt32(data, offset);
        if (serial == 0) return;

        if (!state.SerialMap.TryGetValue(serial, out uint phantom))
        {
            phantom = state.NextPhantomItemSerial++;
            state.SerialMap[serial] = phantom;
        }
        WriteUInt32(data, offset, phantom);
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static int[] GetSerialOffsets(byte opcode, int length)
    {
        return opcode switch
        {
            0x77 => [1],          // MobileMoving: serial at byte 1
            0x1D => [1],          // DeleteObject: serial at byte 1
            0x6E => [1],          // CharacterAnimation: serial at byte 1
            0x0B => [1],          // Damage: serial at byte 1
            0x0C => length >= 12 ? [1, 7] : [1],
            0x70 => [1],          // GraphicalEffect
            0xC0 => [1],          // Effect
            0x4F => [1],          // LightSource
            0x54 => [1],          // PlaySound
            0x1A => [3],          // ObjectInfo
            0x25 => length >= 9 ? [7] : [],
            0x2E => [1, 9],       // EquipItem: item serial + container serial
            0x1C => [3],          // AsciiMessage
            0xAE => [3],          // UnicodeMessage
            0xCC => [3],          // LocMessage
            0xC1 => [3],          // LocMessageAffix
            _ => []
        };
    }

    public List<(string Id, string Recorder, DateTime Date, int DurationMs, int PacketCount)> ListRecordings()
    {
        var result = new List<(string, string, DateTime, int, int)>();
        if (!Directory.Exists(_recordingsDir)) return result;

        foreach (var file in Directory.GetFiles(_recordingsDir, "*.rec"))
        {
            try
            {
                using var fs = File.OpenRead(file);
                using var br = new BinaryReader(fs);
                string id = br.ReadString();
                string recorder = br.ReadString();
                long ticks = br.ReadInt64();
                var date = new DateTime(ticks, DateTimeKind.Utc);
                int durationMs = br.ReadInt32();
                int packetCount = br.ReadInt32();
                br.ReadUInt32(); // RecorderUid
                result.Add((id, recorder, date, durationMs, packetCount));
            }
            catch { }
        }

        result.Sort((a, b) => b.Item3.CompareTo(a.Item3));
        return result;
    }

    public RecordingSession? LoadRecording(int index)
    {
        var list = ListRecordingFiles();
        if (index < 0 || index >= list.Count) return null;
        return LoadRecordingFromFile(list[index]);
    }

    public bool DeleteRecording(int index)
    {
        var list = ListRecordingFiles();
        if (index < 0 || index >= list.Count) return false;
        try { File.Delete(list[index]); return true; } catch { return false; }
    }

    public RecordingSession? LoadRecordingById(string id)
    {
        string path = Path.Combine(_recordingsDir, id + ".rec");
        if (!File.Exists(path)) return null;
        return LoadRecordingFromFile(path);
    }

    private List<string> ListRecordingFiles()
    {
        if (!Directory.Exists(_recordingsDir))
            return [];

        var files = Directory.GetFiles(_recordingsDir, "*.rec");
        Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
        return [.. files];
    }

    private void SaveRecording(RecordingSession session)
    {
        string path = Path.Combine(_recordingsDir, session.Id + ".rec");
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);

        bw.Write(session.Id);
        bw.Write(session.RecorderName);
        bw.Write(session.CreatedAt.Ticks);
        bw.Write(session.DurationMs);
        bw.Write(session.Packets.Count);
        bw.Write(session.RecorderUid);

        bw.Write(session.Center.X);
        bw.Write(session.Center.Y);
        bw.Write(session.Center.Z);
        bw.Write(session.Center.Map);

        foreach (var pkt in session.Packets)
        {
            bw.Write(pkt.TickOffset);
            bw.Write((ushort)pkt.Data.Length);
            bw.Write(pkt.Data);
        }
    }

    private static RecordingSession? LoadRecordingFromFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            var session = new RecordingSession
            {
                Id = br.ReadString(),
                RecorderName = br.ReadString(),
                CreatedAt = new DateTime(br.ReadInt64(), DateTimeKind.Utc)
            };
            int durationMs = br.ReadInt32();
            int packetCount = br.ReadInt32();
            session.RecorderUid = br.ReadUInt32();

            short cx = br.ReadInt16();
            short cy = br.ReadInt16();
            sbyte cz = br.ReadSByte();
            byte cmap = br.ReadByte();
            session.Center = new Point3D(cx, cy, cz, cmap);

            for (int i = 0; i < packetCount; i++)
            {
                int tickOffset = br.ReadInt32();
                ushort len = br.ReadUInt16();
                byte[] data = br.ReadBytes(len);
                session.Packets.Add(new RecordedPacket { TickOffset = tickOffset, Data = data });
            }

            return session;
        }
        catch
        {
            return null;
        }
    }
}
