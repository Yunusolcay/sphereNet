using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using SphereNet.Panel;

namespace SphereNet.Host;

/// <summary>
/// Named-pipe client that talks to SphereNet.Server's IpcServer.
/// Receives stats pushes; sends commands and queries.
/// </summary>
public sealed class IpcBridge : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wl = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ServerStats? LastStats { get; private set; }
    public DebugState? LastDebugState { get; private set; }
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(string pipeName)
    {
        _cts = new CancellationTokenSource();
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await _pipe.ConnectAsync(15_000, _cts.Token);
        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(_pipe, leaveOpen: true);
        IsConnected = true;
        _ = ReadLoopAsync(_cts.Token);
    }

    public void Disconnect()
    {
        IsConnected = false;
        _cts.Cancel();
        foreach (var p in _pending.Values) p.TrySetCanceled();
        _pending.Clear();
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    // ── Fire-and-forget commands ────────────────────────────────────────────

    public void SendCommand(string cmd)
        => _ = WriteAsync(new { t = "cmd", cmd });

    public void SendBroadcast(string msg)
        => _ = WriteAsync(new { t = "cmd", cmd = "broadcast", msg });

    // ── Request/response ────────────────────────────────────────────────────

    public Task<T?> QueryAsync<T>(string qry, object? args = null) where T : class
        => RequestAsync<T>("qry", qry, args);

    public Task<bool> MutateAsync(string mut, object? args = null)
        => RequestAsync<bool>("mut", mut, args).ContinueWith(t => t.Result, TaskScheduler.Default)
            .ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default);

    private async Task<T?> RequestAsync<T>(string kind, string op, object? args)
    {
        if (!IsConnected) return default;
        var id = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var msg = BuildMsg(kind, id, op, args);
        await WriteAsync(msg);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var el = await tcs.Task.WaitAsync(cts.Token);
            if (typeof(T) == typeof(bool)) return (T)(object)(el.ValueKind != JsonValueKind.False);
            return el.Deserialize<T>(_json);
        }
        catch { return default; }
        finally { _pending.TryRemove(id, out _); }
    }

    private static Dictionary<string, object?> BuildMsg(string kind, string id, string op, object? args)
    {
        var d = new Dictionary<string, object?>
        {
            ["t"]    = kind,
            ["id"]   = id,
            [kind]   = op,
        };
        if (args != null)
            foreach (var p in JsonSerializer.SerializeToElement(args, _json).EnumerateObject())
                d[p.Name] = p.Value;
        return d;
    }

    // ── Read loop ───────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line is null) break;
                ProcessMessage(line);
            }
        }
        catch { }
        IsConnected = false;
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.GetProperty("t").GetString())
            {
                case "stats":
                    LastStats = root.GetProperty("data").Deserialize<ServerStats>(_json);
                    break;
                case "rsp":
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (id != null && _pending.TryRemove(id, out var tcs))
                    {
                        var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
                        tcs.TrySetResult(data);
                    }
                    break;
            }
        }
        catch { }
    }

    // ── Write ───────────────────────────────────────────────────────────────

    private async Task WriteAsync(object msg)
    {
        if (_writer == null) return;
        await _wl.WaitAsync();
        try { await _writer.WriteLineAsync(JsonSerializer.Serialize(msg, _json)); }
        catch { }
        finally { _wl.Release(); }
    }

    public void Dispose() => Disconnect();
}
