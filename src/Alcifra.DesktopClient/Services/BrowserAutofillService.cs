using Microsoft.Extensions.Logging;
using Alcifra.BrowserHost;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// Runs the desktop's browser-autofill bridge when — and only while — the user has switched it on (ADR 0978): the
/// loopback endpoint exists only when it was asked for, and only while somebody is signed in.
/// </summary>
/// <remarks>
/// The bridge speaks for the SIGNED-IN session: starting it before sign-in would give the browser a host that can
/// answer nothing, and leaving it after sign-out would keep a decrypting endpoint alive for nobody. Both are why it
/// is applied on sign-in and stopped on sign-out, not merely toggled by the checkbox.
/// </remarks>
public static class BrowserAutofill
{
    private static AutofillBridge? _bridge;
    private static ILogger? _logger;

    /// <summary>The host name the desktop registers; the extension asks for it first (ADR 0978).</summary>
    public const string ClientName = "desktop";

    public static bool IsRunning => _bridge is not null;

    /// <summary>Starts or stops the bridge to match the saved setting and the session that is signed in.</summary>
    public static void Apply(AlcifraApiClient? api, ILoggerFactory? logging = null)
    {
        _logger ??= logging?.CreateLogger("simplarchive.autofill");

        var settings = BrowserAutofillSettingsStore.Load();
        if (!settings.Enabled || api is null)
        {
            Stop();
            return;
        }

        if (_bridge is not null)
        {
            return;   // already serving this client
        }

        _bridge = AutofillBridge.Start(
            new AutofillHost(new DesktopAutofillStore(api), _logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, ClientName),
            ClientName,
            _logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    /// <summary>Stops the bridge — at sign-out, when the setting is switched off, and when the window closes.</summary>
    public static void Stop()
    {
        var bridge = _bridge;
        _bridge = null;
        if (bridge is not null)
        {
            _ = bridge.DisposeAsync();
        }
    }
}
