using System.Net.Sockets;
using System.Text;
using Alcifra.Api.Imap;
using Serilog.Events;

namespace Alcifra.EndToEndTests;

// #2069 (ADR 0626): every sign-in IMAP refuses says why in the log, as a failed password already did. A phone's mail
// account that kept retrying was refused for minutes with nothing logged (the kiosk, 2026-11-02). Spoken over a raw
// socket, since a real client never sends the malformed forms.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ImapRefusalLoggingTests
{
    private readonly E2EApiFactory _factory;

    public ImapRefusalLoggingTests(E2EApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("AUTHENTICATE XOAUTH2 dGVzdA==", "NO only AUTH=PLAIN", "a mechanism other than PLAIN")]
    [InlineData("AUTHENTICATE PLAIN ***not-base64***", "BAD malformed SASL", "a SASL response that is not base64")]
    [InlineData("AUTHENTICATE PLAIN dGVzdA==", "BAD malformed SASL", "a SASL response that is not three NUL-separated parts")]
    public async Task A_refused_sign_in_says_what_was_refused(string command, string answer, string reason)
    {
        var before = _factory.Logs.At(LogEventLevel.Warning).Count(l => l.Contains(reason, StringComparison.Ordinal));

        var replies = await SendAsync($"a1 {command}");

        Assert.Contains(answer, replies, StringComparison.Ordinal);
        Assert.True(_factory.Logs.At(LogEventLevel.Warning).Count(l => l.Contains(reason, StringComparison.Ordinal)) > before,
            $"No warning says the server refused {reason}.");
    }

    [Fact]
    public async Task A_throttled_sign_in_says_it_was_throttled_each_time()
    {
        var email = $"retrying-{Guid.NewGuid():N}@e2e.local";
        string replies = string.Empty;
        for (var attempt = 0; attempt < 8 && !replies.Contains("[UNAVAILABLE]", StringComparison.Ordinal); attempt++)
        {
            replies = await SendAsync($"a1 LOGIN {email} wrong-password");
        }

        Assert.Contains("[UNAVAILABLE]", replies, StringComparison.Ordinal);
        Assert.Contains(_factory.Logs.At(LogEventLevel.Warning), l => l.Contains(email, StringComparison.Ordinal) && l.Contains("throttled", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Logs.At(LogEventLevel.Warning), l => l.Contains("wrong-password", StringComparison.Ordinal));   // never the credentials
    }

    private async Task<string> SendAsync(string line)
    {
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await reader.ReadLineAsync();   // the greeting
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"{line}\r\n"));
        var reply = new StringBuilder();
        while (await reader.ReadLineAsync() is { } received)
        {
            reply.AppendLine(received);
            if (received.StartsWith("a1 ", StringComparison.Ordinal))
            {
                break;
            }
        }

        return reply.ToString();
    }
}
