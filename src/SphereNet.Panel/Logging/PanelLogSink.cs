using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Serilog.Core;
using Serilog.Events;
using SphereNet.Panel.Hubs;

namespace SphereNet.Panel.Logging;

public sealed class PanelLogSink : ILogEventSink, IDisposable
{
    private IHubContext<ServerHub>? _hub;
    private readonly ConcurrentQueue<LogEntry> _buffer = new();
    private Timer? _flushTimer;

    public void SetHubContext(IHubContext<ServerHub> hub)
    {
        _hub = hub;
        _flushTimer = new Timer(FlushBuffer, null, 150, 150);
    }

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry(
            logEvent.Timestamp.UtcDateTime,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            logEvent.Properties.TryGetValue("SourceContext", out var src)
                ? src.ToString().Trim('"') : ""
        );

        AddEntry(entry);
    }

    public void AddEntry(LogEntry entry) => _buffer.Enqueue(entry);

    private void FlushBuffer(object? state)
    {
        if (_hub == null || _buffer.IsEmpty) return;

        var batch = new List<LogEntry>();
        while (_buffer.TryDequeue(out var e)) batch.Add(e);

        if (batch.Count > 0)
            _ = _hub.Clients.All.SendAsync("ReceiveLogBatch", batch);
    }

    public void Dispose() => _flushTimer?.Dispose();
}
