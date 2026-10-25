using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SimplArchive.BrowserHost;

/// <summary>
/// The running client's side of the browser host: a loopback listener that a small shim, spawned by the browser,
/// connects to and relays the native-messaging protocol over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bridge at all.</b> A browser spawns its host as a new process with no arguments and no user present.
/// sapass can sign in from the environment; the DESKTOP's session lives in the desktop process (an in-memory OIDC
/// token, a card session keyed by token serial), so a spawned desktop process would be a second, signless client.
/// The desktop registers a launcher for its shim, and the shim forwards the protocol to this bridge on the running
/// app — which owns the session, the card and the PIN dialog (ADR 0978).
/// </para>
/// <para>
/// <b>Loopback plus a per-run token, in an owner-only file.</b> Any local process of the same user could reach a
/// loopback port, so the first frame on every connection must carry the token; the token is random per run and
/// readable only by that user (0600). A process able to read it already runs as the user and could read the
/// desktop's memory — the file mode is what keeps OTHER users out. The file is written when the bridge starts and
/// deleted when it stops, so a stopped client leaves no address for the shim to find.
/// </para>
/// <para>
/// <b>Opt-in.</b> The desktop starts a bridge only when browser autofill is switched on in its settings (owner,
/// 2026-10-25): the endpoint exists only when the user asked for it.
/// </para>
/// </remarks>
public sealed class AutofillBridge : IAsyncDisposable
{
    private readonly AutofillHost _host;
    private readonly TcpListener _listener;
    private readonly string _token;
    private readonly string _discovery;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();

    private AutofillBridge(AutofillHost host, TcpListener listener, string token, string discovery, ILogger logger)
    {
        _host = host;
        _listener = listener;
        _token = token;
        _discovery = discovery;
        _logger = logger;
    }

    /// <summary>Where a client's shim finds its bridge: one file per client name, under the user's local data.</summary>
    public static string DiscoveryPath(string clientName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "simplarchive", $"autofill-{clientName}.json");

    /// <summary>Starts listening, writes the discovery file, and accepts shims until disposed.</summary>
    public static AutofillBridge Start(AutofillHost host, string clientName, ILogger logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var discovery = DiscoveryPath(clientName);

        Directory.CreateDirectory(Path.GetDirectoryName(discovery)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(new { port, token, pid = Environment.ProcessId });
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(discovery, json);
        }
        else
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            using var stream = new FileStream(discovery, options);
            stream.Write(json);
        }

        var bridge = new AutofillBridge(host, listener, token, discovery, logger);
        _ = bridge.AcceptAsync();
        logger.LogInformation("Browser autofill is listening for the {Client} shim.", clientName);
        return bridge;
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException e)
            {
                _logger.LogWarning("The browser-autofill listener stopped accepting: {Reason}", e.Message);
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();

                // The first frame is the shim's credential. A mismatch closes the connection without an answer, so
                // a scanner learns nothing beyond "something is listening".
                using var greeting = await NativeMessaging.ReadAsync(stream, _stopping.Token);
                var offered = greeting?.RootElement.TryGetProperty("token", out var value) == true ? value.GetString() : null;
                if (offered is null || !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(_token)))
                {
                    _logger.LogWarning("A browser-autofill connection was refused: it did not present this run's token.");
                    return;
                }

                await NativeMessaging.WriteAsync(stream, new { id = 0, ok = true }, _stopping.Token);
                await _host.RunAsync(stream, stream, _stopping.Token);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException)
            {
                // A shim that vanishes mid-request is ordinary (the tab closed); the loop keeps listening.
                _logger.LogDebug("A browser-autofill connection ended: {Reason}", e.Message);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            File.Delete(_discovery);
        }
        catch (IOException e)
        {
            _logger.LogWarning("Could not remove the browser-autofill discovery file {Path}: {Reason}", _discovery, e.Message);
        }

        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }
}
