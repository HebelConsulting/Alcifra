using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Keeps a window out of screen capture while it shows a secret (ADR 0968, the phone's secure window in the desktop's
/// terms): screenshots, recordings and screen sharing draw it black or leave it out. Counted by the core's
/// <c>SecretScreens</c>, so it lifts only when the last secret on show went.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows</b>: <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c>. <b>macOS</b>: the window's
/// <c>sharingType</c> set to none, which the system's capture honours; a capture API that ignores it is outside what an
/// application can refuse. <b>Linux</b>: no compositor-independent way exists for a window to refuse capture, so nothing
/// is done there, and ADR 0968 says so as a parity gap.
/// </para>
/// <para>
/// Best effort by design: a refusal is logged at Debug and the secret is still shown, because the user asked for it and
/// the clipboard and hold-to-show rules still apply.
/// </para>
/// </remarks>
internal static class CaptureProtection
{
    private const uint WdaNone = 0x0;
    private const uint WdaExcludeFromCapture = 0x11;
    private const string ObjC = "/usr/lib/libobjc.dylib";

    /// <summary>Whether this platform can keep a window out of capture at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Protects <paramref name="window"/>, or lifts the protection.</summary>
    public static void Apply(TopLevel? window, bool on)
    {
        if (window?.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!SetWindowDisplayAffinity(handle.Handle, on ? WdaExcludeFromCapture : WdaNone))
                {
                    DesktopLog.Debug("The window could not be kept out of screen capture (error {Error})", Marshal.GetLastWin32Error());
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                // The handle is the content view; its window is what is shared. NSWindowSharingNone = 0, ReadOnly = 1.
                var nsWindow = handle.HandleDescriptor == "NSWindow" ? handle.Handle : Send(handle.Handle, Sel("window"));
                if (nsWindow != IntPtr.Zero)
                {
                    Send(nsWindow, Sel("setSharingType:"), on ? IntPtr.Zero : new IntPtr(1));
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            DesktopLog.Debug("Screen-capture protection is not available here: {Reason}", e.Message);
        }
    }

    // DllImport rather than LibraryImport, for SecretStore's reason: no AllowUnsafeBlocks for three P/Invokes.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport(ObjC, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern IntPtr Sel(string name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
}
