using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SimplArchive.BrowserHost;

/// <summary>
/// The executable a browser spawns for a client that keeps its session in its own process: it finds the running
/// client's bridge through the discovery file and relays the native-messaging stream to it unchanged.
/// </summary>
/// <remarks>
/// <b>A relay, not a host.</b> The frames pass through byte for byte; this process knows only where to connect and
/// what token to present. That keeps the secret-bearing half in the one process that already holds the session.
/// </remarks>
public static class BrowserHostShim
{
    /// <summary>Exit codes the launcher's user (and a support conversation) can tell apart.</summary>
    public const int NoRunningClient = 3;
    public const int Refused = 4;

    /// <summary>
    /// Relays until either end closes. Returns an <see cref="ExitCodes"/>-style code; 0 when the browser simply
    /// closed the port.
    /// </summary>
    public static async Task<int> RunAsync(string clientName, Stream stdin, Stream stdout, CancellationToken cancellationToken = default)
    {
        if (await Discovery.ReadAsync(clientName, cancellationToken) is not { } bridge)
        {
            return NoRunningClient;
        }

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, bridge.Port, cancellationToken);
        }
        catch (SocketException)
        {
            // The file outlived the process (a crash). The launcher's message says the client is not running.
            Discovery.Delete(clientName);
            return NoRunningClient;
        }

        await using var socket = client.GetStream();
        await NativeMessaging.WriteAsync(socket, new { token = bridge.Token }, cancellationToken);

        using var greeting = await NativeMessaging.ReadAsync(socket, cancellationToken);
        if (greeting?.RootElement.TryGetProperty("ok", out var ok) != true || !ok.GetBoolean())
        {
            return Refused;
        }

        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var up = PumpAsync(stdin, socket, relay.Token);
        var down = PumpAsync(socket, stdout, relay.Token);
        await Task.WhenAny(up, down);
        await relay.CancelAsync();
        return 0;
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        try
        {
            await from.CopyToAsync(to, cancellationToken);
            await to.FlushAsync(cancellationToken);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Either end closing is how a browser port ends.
        }
    }

    private sealed record Discovery(int Port, string Token)
    {
        public static async Task<Discovery?> ReadAsync(string clientName, CancellationToken cancellationToken)
        {
            var path = AutofillBridge.DiscoveryPath(clientName);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
                var port = document.RootElement.GetProperty("port").GetInt32();
                var token = document.RootElement.GetProperty("token").GetString();
                var pid = document.RootElement.TryGetProperty("pid", out var p) ? p.GetInt32() : 0;

                // A file left by a process that is gone is stale: the bridge deletes it on a clean stop, so this is
                // the crash case, and reporting "not running" beats connecting to whatever took the port since.
                if (token is not { Length: > 0 } || (pid != 0 && !IsAlive(pid)))
                {
                    Delete(clientName);
                    return null;
                }

                return new Discovery(port, token);
            }
            catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return null;
            }
        }

        public static void Delete(string clientName)
        {
            try
            {
                File.Delete(AutofillBridge.DiscoveryPath(clientName));
            }
            catch (IOException)
            {
                // A stale file is ignored on the next read anyway.
            }
        }

        private static bool IsAlive(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }
}
