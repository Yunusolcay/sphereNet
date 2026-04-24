using System.IO.Pipes;
using System.Text.Json;
using SphereNet.Panel;

namespace SphereNet.Server.Ipc;

/// <summary>
/// Named-pipe IPC server. Accepts one connection from SphereNet.Host,
/// pushes stats every 2 s, and serves queries/mutations/commands.
/// </summary>
public sealed class IpcServer : IDisposable
{
    private readonly string _pipeName;
    private PanelContext? _ctx;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly SemaphoreSlim _wl = new(1, 1);

    private static readonly JsonSerializerOptions _json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IpcServer(string pipeName) => _pipeName = pipeName;

    public void SetContext(PanelContext ctx) => _ctx = ctx;

    public async Task RunAsync(CancellationToken ct)
    {
        _pipe = new NamedPipeServerStream(
            _pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        Console.WriteLine($"[IPC] Waiting for host on pipe '{_pipeName}'…");
        await _pipe.WaitForConnectionAsync(ct);
        Console.WriteLine("[IPC] Host connected.");

        _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(_pipe, leaveOpen: true);

        _ = StatsLoopAsync(ct);
        await ReadLoopAsync(ct);
    }

    // ── Stats push ──────────────────────────────────────────────────────────

    private async Task StatsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_ctx?.GetStats != null)
            {
                try
                {
                    var stats = _ctx.GetStats();
                    await WriteAsync(new { t = "stats", data = stats }, ct);
                }
                catch { return; }
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    // ── Message dispatch ────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader!.ReadLineAsync(ct);
                if (line is null) break;
                _ = HandleAsync(line, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* host disconnected */ }
        Console.WriteLine("[IPC] Host disconnected.");
    }

    private async Task HandleAsync(string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var t = root.GetStr("t");

            if (t == "cmd") { HandleCommand(root); return; }

            // qry / mut — all produce a response
            var id = root.GetStr("id");
            var op = root.TryGetProp("qry") ?? root.TryGetProp("mut") ?? "";

            object? data = null;
            bool ok = true;
            string? err = null;

            try   { data = Dispatch(op, root); }
            catch (Exception ex) { ok = false; err = ex.Message; }

            if (!string.IsNullOrEmpty(id))
                await WriteAsync(new { t = "rsp", id, ok, err, data }, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IPC] Bad message: {ex.Message}");
        }
    }

    private void HandleCommand(JsonElement root)
    {
        switch (root.GetStr("cmd"))
        {
            case "save":      _ctx?.OnSave?.Invoke(); break;
            case "resync":    _ctx?.OnResync?.Invoke(); break;
            case "shutdown":  _ctx?.OnShutdown?.Invoke(); break;
            case "gc":        _ctx?.OnGc?.Invoke(); break;
            case "respawn":   _ctx?.OnRespawn?.Invoke(); break;
            case "restock":   _ctx?.OnRestock?.Invoke(); break;
            case "broadcast":
                _ctx?.OnBroadcast?.Invoke(root.GetStr("msg") ?? ""); break;
        }
    }

    private object? Dispatch(string op, JsonElement root) => op switch
    {
        "stats"   => _ctx?.GetStats?.Invoke(),
        "players" => (object?)(_ctx?.GetOnlinePlayers?.Invoke() ?? []),
        "accounts"=> _ctx?.GetAllAccounts?.Invoke() ?? [],
        "account" => _ctx?.GetAccount?.Invoke(root.GetStr("name") ?? ""),
        "debug"   => _ctx?.GetDebugState?.Invoke() ?? new DebugState(false, false),
        "exec"    => _ctx?.ExecuteCommand?.Invoke(root.GetStr("raw") ?? "") ?? [],

        "account_create" =>
            _ctx?.CreateAccount?.Invoke(root.GetStr("name")!, root.GetStr("pass")!) ?? false,
        "account_delete" =>
            _ctx?.DeleteAccount?.Invoke(root.GetStr("name")!) ?? false,
        "account_ban" =>
            Void(() => _ctx?.SetAccountBanned?.Invoke(root.GetStr("name")!, root.GetBool("banned"))),
        "account_set_pass" =>
            Void(() => _ctx?.SetAccountPassword?.Invoke(root.GetStr("name")!, root.GetStr("pass")!)),
        "account_set_plevel" =>
            Void(() => _ctx?.SetAccountPrivLevel?.Invoke(root.GetStr("name")!, root.GetInt("level"))),
        "set_packet_debug" =>
            Void(() => _ctx?.SetPacketDebug?.Invoke(root.GetBool("on"))),
        "set_script_debug" =>
            Void(() => _ctx?.SetScriptDebug?.Invoke(root.GetBool("on"))),

        _ => null
    };

    private static bool Void(Action a) { a(); return true; }

    // ── Write helper ────────────────────────────────────────────────────────

    private async Task WriteAsync(object msg, CancellationToken ct)
    {
        if (_writer == null) return;
        await _wl.WaitAsync(ct);
        try { await _writer.WriteLineAsync(JsonSerializer.Serialize(msg, _json)); }
        catch { }
        finally { _wl.Release(); }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
    }
}

// ── JsonElement extension helpers ───────────────────────────────────────────

file static class JsonEx
{
    public static string? GetStr(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static bool GetBool(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) && v.GetBoolean();

    public static int GetInt(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) ? v.GetInt32() : 0;

    public static string? TryGetProp(this JsonElement el, string key)
        => el.TryGetProperty(key, out var v) ? v.GetString() : null;
}
