using System.Runtime.CompilerServices;
using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// The tools load the session file when they run, and this suite runs them in process: without this, a test would read and
// renew the session of whoever is running it.
internal static class NoRealSessionFile
{
    [ModuleInitializer]
    internal static void Point() =>
        Environment.SetEnvironmentVariable(WireNames.SessionFileVariable, Path.Combine(Path.GetTempPath(), $"no-session-{Guid.NewGuid():N}.json"));
}
