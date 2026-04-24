using Microsoft.AspNetCore.SignalR;
using Serilog.Core;
using Serilog.Events;
using SphereNet.Panel.Hubs;

namespace SphereNet.Panel.Logging;

/// <summary>
/// Serilog sink that forwards log events to all connected SignalR clients.
/// The HubContext is injected after app.Build() via SetHubContext().
/// </summary>
public sealed class PanelLogSink : ILogEventSink
{
    private IHubContext<ServerHub>? _hub;

    public void SetHubContext(IHubContext<ServerHub> hub) => _hub = hub;

    public void Emit(LogEvent logEvent)
    {
        if (_hub == null) return;

        var entry = new LogEntry(
            logEvent.Timestamp.UtcDateTime,
            logEvent.Level.ToString(),
            logEvent.RenderMessage(),
            logEvent.Properties.TryGetValue("SourceContext", out var src)
                ? src.ToString().Trim('"') : ""
        );

        AddEntry(entry);
    }

    /// <summary>Called by the Host when it captures a log line from the server process stdout.</summary>
    public void AddEntry(LogEntry entry)
    {
        _ = _hub?.Clients.All.SendAsync("ReceiveLog", entry);
    }
}
