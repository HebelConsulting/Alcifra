#if MACOS
using AppKit;

namespace Alcifra.DesktopClient.Services;

/// <summary>What a macOS build does first (#2036), kept out of Program.cs, which is at its line limit.</summary>
internal static class MacStartup
{
    /// <summary>
    /// Initialises AppKit's application object, which the Apple bindings need before any AppKit or AVFoundation type is
    /// touched, then runs a player check when one is asked for. True when a check ran and the program should end.
    /// </summary>
    public static bool Begin(string[] args)
    {
        NSApplication.Init();
        return MemoryPlayerCheck.TryRun(args);
    }
}
#endif
