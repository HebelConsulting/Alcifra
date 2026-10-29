using System.Net;
using System.Reflection;
using System.Text;
using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.UnitTests;

/// <summary>
/// <c>alcifra passwords</c> (ADR 0966): the secret arrives on stdin only, a re-run writes nothing, and nothing is
/// created that nobody could read. The fake server answers as the real one does for a service account.
/// </summary>
public sealed class SaConsolePasswordCommandsTests
{
    private static readonly Guid StoreId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EntryId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RepoId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private sealed class FakeServer(Func<string, string, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        public readonly List<string> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var asked = $"{request.Method} {request.RequestUri!.AbsolutePath}";
            Asked.Add(asked);
            return Task.FromResult(answer(request.Method.Method, request.RequestUri.AbsolutePath) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Store(int certificates, string items) => $$"""
        {"id":"{{StoreId}}","name":"Demo logins","kind":"store","storeId":"{{StoreId}}","isPersonal":false,"needsReseal":false,
         "urgentReseal":false,"readerCertificateCount":{{certificates}},"eTag":"\"s\"","items":[{{items}}],
         "links":[{"rel":"self","href":"/api/password-folders/{{StoreId}}","method":"GET"},
                  {"rel":"password-entries","href":"/api/password-folders/{{StoreId}}/entries","method":"POST"},
                  {"rel":"password-store-recipients","href":"/api/password-stores/{{StoreId}}/recipients","method":"GET"}]}
        """;

    private static readonly string EntryRow = $$"""
        {"id":"{{EntryId}}","name":"Demo login: admin@","kind":"entry","eTag":"\"e\"","canEdit":true,"canRename":true,"canDelete":true,"canMove":true,
         "links":[{"rel":"self","href":"/api/password-entries/{{EntryId}}","method":"GET"}]}
        """;

    private static readonly string Entry = $$"""
        {"id":"{{EntryId}}","name":"Demo login: admin@","storeId":"{{StoreId}}","parentId":"{{StoreId}}","versionNumber":1,"eTag":"\"e\"",
         "needsReseal":false,"exposedSince":null,"canEdit":true,"canRename":true,"canDelete":true,"canMove":true,
         "links":[{"rel":"self","href":"/api/password-entries/{{EntryId}}","method":"GET"},
                  {"rel":"content","href":"https://storage.example/x","method":"GET"}]}
        """;

    private static HttpResponseMessage? StoreServer(string method, string path, string store) => (method, path) switch
    {
        ("GET", "/api") => Json("""{"links":[{"rel":"passwordStores","href":"/api/password-stores","method":"GET"}]}"""),
        ("GET", "/api/password-stores") => Json($$"""{"stores":[{"id":"{{StoreId}}","name":"Demo logins","isPersonal":false,"links":[{"rel":"self","href":"/api/password-folders/{{StoreId}}","method":"GET"}]}]}"""),
        ("GET", var p) when p == $"/api/password-folders/{StoreId}" => Json(store),
        ("GET", var p) when p == $"/api/password-entries/{EntryId}" => Json(Entry),
        ("GET", var p) when p == $"/api/password-stores/{StoreId}/recipients" => Json(
            """{"certificates":[],"readersWithoutCertificate":[{"userId":"44444444-4444-4444-4444-444444444444","displayName":"Thomas"}]}"""),
        _ => null,
    };

    private static HttpClient Over(FakeServer server) => new(server) { BaseAddress = new Uri("https://archive.example.test/") };

    [Fact]
    public async Task An_entry_already_there_with_content_is_left_alone_and_nothing_is_written()
    {
        var server = new FakeServer((m, p) => StoreServer(m, p, Store(certificates: 3, EntryRow)));
        using var http = Over(server);

        var outcome = await PasswordStoreSurface.FindOrCreateEntryAsync(
            new PasswordStoreClient(ApiCore.Over(http)), StoreId, "Demo login: admin@", PasswordEntryContent.Of("x"), CancellationToken.None);

        Assert.Equal((EntryId, PasswordStoreSurface.EntryOutcome.Unchanged), outcome);
        Assert.DoesNotContain(server.Asked, a => !a.StartsWith("GET ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_no_reader_certificate_nothing_is_created_and_the_refusal_names_who_lacks_one()
    {
        var server = new FakeServer((m, p) => StoreServer(m, p, Store(certificates: 0, string.Empty)));
        using var http = Over(server);

        var refusal = await Assert.ThrowsAsync<CliException>(() => PasswordStoreSurface.FindOrCreateEntryAsync(
            new PasswordStoreClient(ApiCore.Over(http)), StoreId, "New", PasswordEntryContent.Of("x"), CancellationToken.None));

        Assert.Contains("Thomas", refusal.Message);
        Assert.DoesNotContain(server.Asked, a => a.StartsWith("POST ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_store_the_caller_cannot_see_is_refused_by_id()
    {
        var server = new FakeServer((m, p) => StoreServer(m, p, Store(3, string.Empty)));
        using var http = Over(server);

        var refusal = await Assert.ThrowsAsync<CliException>(() => PasswordStoreSurface.StoreAsync(
            new PasswordStoreClient(ApiCore.Over(http)), Guid.NewGuid(), CancellationToken.None));

        Assert.Contains("visible", refusal.Message);
    }

    // The parent's children listing holds a child of that name; whether it is a store decides re-use or refusal.
    private static FakeServer ParentWithChild(bool isStore) => new((method, path) => (method, path) switch
    {
        ("GET", "/api") => Json("""
            {"links":[{"rel":"document","href":"/api/documents/{id}","method":"GET"},{"rel":"passwordStores","href":"/api/password-stores","method":"GET"},
                      {"rel":"repositories","href":"/api/repositories","method":"GET"}]}
            """),
        ("GET", var p) when p == $"/api/documents/{RepoId}" => Json($$"""{"id":"{{RepoId}}","links":[{"rel":"children","href":"/api/documents/{{RepoId}}/children","method":"GET"}]}"""),
        ("GET", var p) when p == $"/api/documents/{RepoId}/children" => Json($$"""{"children":[{"id":"{{StoreId}}","name":"Demo logins"}],"links":[]}"""),
        ("GET", "/api/password-stores") => Json(isStore ? $$"""{"stores":[{"id":"{{StoreId}}","name":"Demo logins","isPersonal":false,"links":[]}]}""" : """{"stores":[]}"""),
        _ => null,
    });

    [Fact]
    public async Task A_store_of_that_name_is_found_rather_than_created_twice()
    {
        var server = ParentWithChild(isStore: true);
        using var http = Over(server);

        var found = await PasswordStoreSurface.FindOrCreateStoreAsync(new AlcifraApi(http), RepoId, "Demo logins", CancellationToken.None);

        Assert.Equal((StoreId, false), found);
        Assert.DoesNotContain(server.Asked, a => a.StartsWith("POST ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_child_of_that_name_that_is_not_a_store_is_refused_never_taken_for_one()
    {
        using var http = Over(ParentWithChild(isStore: false));

        var refusal = await Assert.ThrowsAsync<CliException>(
            () => PasswordStoreSurface.FindOrCreateStoreAsync(new AlcifraApi(http), RepoId, "Demo logins", CancellationToken.None));

        Assert.Contains("not a password store", refusal.Message);
    }

    [Theory]
    [InlineData("SimplDemo2026!\n")]
    [InlineData("SimplDemo2026!\r\n")]
    [InlineData("SimplDemo2026!")]
    public async Task The_password_is_what_arrived_on_stdin_without_its_line_break(string piped)
    {
        Assert.Equal("SimplDemo2026!", await PasswordInput.ReadAsync(new StringReader(piped), CancellationToken.None));
    }

    [Fact]
    public async Task Nothing_on_stdin_is_refused()
    {
        await Assert.ThrowsAsync<CliException>(() => PasswordInput.ReadAsync(new StringReader("\n"), CancellationToken.None));
    }

    [Fact]
    public void An_entry_without_password_stdin_is_refused_before_anything_is_read()
    {
        var settings = new PasswordEntryCreateSettings { Url = "https://archive.example.test", Store = StoreId.ToString(), Name = "x" };

        Assert.False(settings.Validate().Successful);
    }

    // ADR 0886: no option can carry the secret, so it can never reach argv, shell history or a logged command line.
    [Fact]
    public void No_option_of_the_entry_command_takes_a_password_value()
    {
        var options = typeof(PasswordEntryCreateSettings).GetProperties()
            .Select(p => (Property: p, Option: p.GetCustomAttribute<CommandOptionAttribute>()))
            .Where(o => o.Option is not null)
            .ToList();

        var passwordOptions = options.Where(o => o.Option!.LongNames.Any(n => n.Contains("password", StringComparison.OrdinalIgnoreCase))).ToList();
        var only = Assert.Single(passwordOptions);
        Assert.Equal(typeof(bool), only.Property.PropertyType);   // a flag, `--password-stdin`, never a value
    }
}
