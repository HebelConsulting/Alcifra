using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Alcifra.BrowserExtensionTests;

/// <summary>
/// A two-page site on loopback: a login form, and the page its submit lands on (no password field, which is where
/// the extension offers to keep a submitted login). Reachable as <c>localhost</c> and <c>127.0.0.1</c>, which the
/// extension treats as two different sites.
/// </summary>
public sealed class LoginSite : IDisposable
{
    private const string LoginPage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Sign in</title></head>
        <body>
          <form method="post" action="/welcome">
            <input id="user" name="user" type="text" autocomplete="username">
            <input id="pass" name="pass" type="password" autocomplete="current-password">
            <button id="go" type="submit">Sign in</button>
          </form>
        </body></html>
        """;

    // The second step of a sign-in (ADR 1013): one field by the standard token, one only by its name.
    private const string VerifyPage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Verify</title></head>
        <body>
          <form method="post" action="/welcome">
            <input id="otp" name="code" type="text" inputmode="numeric" autocomplete="one-time-code">
            <input id="totp" name="totp" type="text">
            <input id="note" name="note" type="text">
            <button id="go" type="submit">Verify</button>
          </form>
        </body></html>
        """;

    private const string WelcomePage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Welcome</title></head>
        <body><h1>Welcome</h1></body></html>
        """;

    private readonly HttpListener _listener = new();
    private readonly Task _serving;

    public LoginSite()
    {
        Port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public int Port { get; }

    /// <summary>The login page, on the host the extension will call the site.</summary>
    public string Login(string host) => $"http://{host}:{Port}/login";

    /// <summary>The page that asks for a verification code (ADR 1013).</summary>
    public string Verify(string host) => $"http://{host}:{Port}/verify";

    /// <summary>The page a submit lands on.</summary>
    public string Welcome(string host) => $"http://{host}:{Port}/welcome";

    public void Dispose()
    {
        _listener.Close();
        try
        {
            _serving.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The listener closing ends the loop with an exception; nothing to report.
        }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var page = context.Request.Url?.AbsolutePath switch
            {
                "/login" => LoginPage,
                "/welcome" => WelcomePage,
                "/verify" => VerifyPage,
                _ => null,
            };
            var body = Encoding.UTF8.GetBytes(page ?? "not found");
            context.Response.StatusCode = page is null ? 404 : 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
