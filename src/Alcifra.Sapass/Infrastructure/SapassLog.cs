using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Alcifra.Sapass.Infrastructure;

/// <summary>The tool's one logger factory: progress and diagnostics to STDERR, so stdout carries only data (ADR 0906).</summary>
/// <remarks>
/// A static rather than constructor injection because the commands get their settings from Spectre with no
/// container behind it; the factory is set once in <see cref="SapassApp.Run"/> and is a no-op until then, which is
/// exactly what a test that runs a command in-process wants.
/// </remarks>
internal static class SapassLog
{
    public static ILoggerFactory Factory { get; set; } = NullLoggerFactory.Instance;

    public static ILogger<T> For<T>() => Factory.CreateLogger<T>();

    /// <summary>The logger a STATIC class uses, which cannot be a generic type argument.</summary>
    public static ILogger For(string category) => Factory.CreateLogger(category);
}
