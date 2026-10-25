using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.BrowserHost;

namespace SimplArchive.UnitTests;

// Chrome's native-messaging framing and the autofill commands, over in-memory streams. What is asserted here is
// the SHAPE of the channel: a message survives the wire, a bad frame ends it, a bad request answers instead, and a
// secret never reaches the log.
public class BrowserHostTests
{
    // ---- Framing (Chrome: 32-bit native-order length, then UTF-8 JSON) ----

    [Fact]
    public async Task A_message_survives_the_wire()
    {
        using var stream = new MemoryStream();
        await NativeMessaging.WriteAsync(stream, new { command = "ping", id = 7 });

        stream.Position = 0;
        using var read = await NativeMessaging.ReadAsync(stream);

        Assert.NotNull(read);
        Assert.Equal("ping", read.RootElement.GetProperty("command").GetString());
        Assert.Equal(7, read.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task End_of_input_is_a_null_message_not_an_error() =>
        Assert.Null(await NativeMessaging.ReadAsync(new MemoryStream()));

    [Fact]
    public async Task A_header_that_stops_mid_length_is_a_framing_error()
    {
        using var stream = new MemoryStream([1, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(stream));
    }

    [Fact]
    public async Task A_length_the_host_will_not_allocate_is_refused()
    {
        // 2 GB, little-endian: a corrupted or hostile channel, not a message.
        using var stream = new MemoryStream([0, 0, 0, 0x80]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(stream));
    }

    [Fact]
    public async Task A_body_shorter_than_its_declared_length_is_a_framing_error()
    {
        using var stream = new MemoryStream([5, 0, 0, 0, (byte)'{', (byte)'}']);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(stream));
    }

    // ---- The commands, over the same channel ----

    [Fact]
    public async Task Ping_names_the_host()
    {
        var responses = await ExchangeAsync(new { command = "ping", id = 1 });

        Assert.True(responses[0].GetProperty("ok").GetBoolean());
        Assert.Equal("sapass", responses[0].GetProperty("host").GetString());
    }

    [Fact]
    public async Task Match_returns_names_and_user_names_rather_than_secrets()
    {
        var responses = await ExchangeAsync(new { command = "match", id = 2, site = "example.com" });

        var entry = responses[0].GetProperty("entries").EnumerateArray().Single();
        Assert.Equal("example.com", entry.GetProperty("name").GetString());
        Assert.Equal("anna", entry.GetProperty("userName").GetString());
        Assert.False(entry.TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Open_returns_the_credentials_for_the_chosen_entry()
    {
        var responses = await ExchangeAsync(new { command = "open", id = 3, entryId = FakeStore.Entry.ToString() });

        Assert.Equal("hunter2", responses[0].GetProperty("password").GetString());
    }

    [Fact]
    public async Task Save_echoes_what_was_filed_without_the_secret()
    {
        var responses = await ExchangeAsync(new { command = "save", id = 4, site = "example.com", userName = "anna", password = "hunter2" });

        Assert.True(responses[0].GetProperty("ok").GetBoolean());
        Assert.Equal("example.com", responses[0].GetProperty("name").GetString());
        Assert.False(responses[0].TryGetProperty("password", out _));
    }

    [Fact]
    public async Task Generate_answers_a_password_of_the_requested_length()
    {
        var responses = await ExchangeAsync(new { command = "generate", id = 5, length = 32 });

        Assert.Equal(32, responses[0].GetProperty("password").GetString()!.Length);
    }

    [Fact]
    public async Task An_unknown_command_is_answered_and_the_channel_survives()
    {
        var responses = await ExchangeAsync(new { command = "shred", id = 6 }, new { command = "ping", id = 7 });

        Assert.False(responses[0].GetProperty("ok").GetBoolean());
        Assert.True(responses[1].GetProperty("ok").GetBoolean());   // the next request still gets through
    }

    [Fact]
    public async Task A_missing_site_is_answered_with_the_reason()
    {
        var responses = await ExchangeAsync(new { command = "match", id = 8 });

        Assert.False(responses[0].GetProperty("ok").GetBoolean());
        Assert.Contains("site", responses[0].GetProperty("error").GetString());
    }

    [Fact]
    public async Task No_secret_reaches_the_log()
    {
        var logger = new RecordingLogger();
        await ExchangeAsync(logger, new { command = "save", id = 9, site = "example.com", userName = "anna", password = "hunter2" });

        Assert.DoesNotContain(logger.Lines, line => line.Contains("hunter2", StringComparison.Ordinal));
    }

    // ---- The harness ----

    private static Task<List<JsonElement>> ExchangeAsync(params object[] requests) => ExchangeAsync(new RecordingLogger(), requests);

    private static async Task<List<JsonElement>> ExchangeAsync(RecordingLogger logger, params object[] requests)
    {
        var input = new MemoryStream();
        foreach (var request in requests)
        {
            await NativeMessaging.WriteAsync(input, request);
        }

        input.Position = 0;
        var output = new MemoryStream();
        await new AutofillHost(new FakeStore(), logger, "sapass").RunAsync(input, output);

        output.Position = 0;
        var responses = new List<JsonElement>();
        while (await NativeMessaging.ReadAsync(output) is { } document)
        {
            using (document)
            {
                responses.Add(document.RootElement.Clone());
            }
        }

        return responses;
    }

    private sealed class FakeStore : IAutofillStore
    {
        public static readonly Guid Entry = Guid.NewGuid();

        public Task<IReadOnlyList<AutofillMatch>> MatchAsync(string site, string? app, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutofillMatch>>([new AutofillMatch(Entry, site, site, "anna")]);

        public Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken) =>
            Task.FromResult<AutofillCredentials?>(entryId == Entry
                ? new AutofillCredentials("hunter2", "anna", [new AutofillField("User name", "anna")])
                : null);

        public Task<AutofillSaved> SaveAsync(AutofillSave save, CancellationToken cancellationToken) =>
            Task.FromResult(new AutofillSaved(Entry, save.Site, save.Site));

        public string Generate(int length) => SimplArchive.Sapass.Infrastructure.Passwords.Generate(length);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}

// The desktop's shape (ADR 0978): a browser spawns a SHIM, the shim finds the RUNNING client's bridge through a
// discovery file, presents the per-run token, and relays the native-messaging frames unchanged.
public class BrowserBridgeTests
{
    [Fact]
    public async Task The_shim_relays_the_protocol_to_a_running_client()
    {
        var logger = new RecordingLogger();
        var host = new AutofillHost(new FakeStore(), logger, "desktop");
        await using var bridge = AutofillBridge.Start(host, "unittests", logger);

        var toShim = new System.IO.Pipelines.Pipe();
        var fromShim = new System.IO.Pipelines.Pipe();
        var relay = BrowserHostShim.RunAsync("unittests", toShim.Reader.AsStream(), fromShim.Writer.AsStream());

        await NativeMessaging.WriteAsync(toShim.Writer.AsStream(), new { command = "ping", id = 1 });
        using var answer = await NativeMessaging.ReadAsync(fromShim.Reader.AsStream());
        Assert.NotNull(answer);

        Assert.True(answer.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("desktop", answer.RootElement.GetProperty("host").GetString());

        await toShim.Writer.CompleteAsync();
        Assert.Equal(0, await relay);
    }

    [Fact]
    public async Task A_discovery_file_whose_process_is_gone_reads_as_no_client_and_is_removed()
    {
        var path = AutofillBridge.DiscoveryPath("stale");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // pid 1 is not a process a test can have started; on Unix it exists (init) — use an id no process has.
        await File.WriteAllTextAsync(path, """{"port":1,"token":"x","pid":2147483000}""");

        var exit = await BrowserHostShim.RunAsync("stale", new MemoryStream(), new MemoryStream());

        Assert.Equal(BrowserHostShim.NoRunningClient, exit);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_connection_without_the_run_s_token_is_closed_without_an_answer()
    {
        var logger = new RecordingLogger();
        var host = new AutofillHost(new FakeStore(), logger, "desktop");
        await using var bridge = AutofillBridge.Start(host, "unittests-refused", logger);

        using var client = new System.Net.Sockets.TcpClient();
        var discovery = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(AutofillBridge.DiscoveryPath("unittests-refused")));
        await client.ConnectAsync(System.Net.IPAddress.Loopback, discovery.RootElement.GetProperty("port").GetInt32());
        await using var stream = client.GetStream();

        await NativeMessaging.WriteAsync(stream, new { token = "not-this-run's-token" });

        // Closed, not answered: a scanner learns nothing beyond "something is listening".
        Assert.Null(await NativeMessaging.ReadAsync(stream));
    }

    private sealed class FakeStore : IAutofillStore
    {
        public Task<IReadOnlyList<AutofillMatch>> MatchAsync(string site, string? app, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutofillMatch>>([]);

        public Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken) =>
            Task.FromResult<AutofillCredentials?>(null);

        public Task<AutofillSaved> SaveAsync(AutofillSave save, CancellationToken cancellationToken) =>
            Task.FromResult(new AutofillSaved(Guid.NewGuid(), save.Site, save.Site));

        public string Generate(int length) => new('a', length);
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
