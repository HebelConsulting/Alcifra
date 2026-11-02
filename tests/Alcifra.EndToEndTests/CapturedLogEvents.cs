using System.Collections.Concurrent;
using Serilog.Core;
using Serilog.Events;

namespace Alcifra.EndToEndTests;

/// <summary>
/// Every log event the hosted app writes, for a test that asserts a refusal says why (ADR 0626): registered as a Serilog
/// sink in the shared factory, which Serilog reads from the service container (<c>ReadFrom.Services</c>). Shared by the
/// whole collection, so a test looks for a line naming something only it did, never for "the last line".
/// </summary>
public sealed class CapturedLogEvents : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);

    /// <summary>The rendered messages at <paramref name="level"/>, oldest first.</summary>
    public IReadOnlyList<string> At(LogEventLevel level) =>
        [.. _events.Where(e => e.Level == level).Select(e => e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture))];
}
