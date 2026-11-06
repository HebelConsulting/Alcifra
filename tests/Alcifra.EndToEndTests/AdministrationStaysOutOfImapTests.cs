using MailKit.Net.Imap;
using MailKit.Security;

namespace Alcifra.EndToEndTests;

// ADR 1066's named exception to ADR 0809: an administrator's mail account mirrors the Repositories tree minus the
// Administration folder — a folder of references to other people's spaces is nothing a mail client can do anything
// useful with, and a mailbox called Administration beside the repositories would invite exactly the wrong click.
// Guarded here, beside ImapEndpointTests' parity, so the exception cannot widen unnoticed; WebDavTreeParityTests
// holds the mount to the same rule.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AdministrationStaysOutOfImapTests
{
    private readonly E2EApiFactory _factory;

    public AdministrationStaysOutOfImapTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_administrators_mailboxes_omit_the_Administration_folder()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Imap{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();
        await _factory.EnsureAdministrationFoldersAsync(tenantId, repoId);

        var email = $"imapadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "imap-1234";
        await _factory.SeedUserAsync(tenantId, email, password, "Imap Admin");
        await _factory.GrantTenantAdminAsync(email);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        // The tree has it — asserted first, so the exception below is really exercised...
        Assert.Contains((await TestJson.Get(api, "/api/repositories")).GetProperty("repositories").EnumerateArray(),
            r => r.GetProperty("name").GetString() == "Administration");

        // ...and the mail account does not, while the ordinary repository is there as ADR 0809 promises.
        var imapPassword = (await TestJson.Post(api, "/api/me/devices", E2EDevices.Any())).GetProperty("password").GetString()!;
        var port = ((Alcifra.Api.Imap.ImapServer)_factory.Services.GetService(typeof(Alcifra.Api.Imap.ImapServer))!).BoundPort!.Value;
        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, imapPassword);
        var names = (await client.GetFoldersAsync(client.PersonalNamespaces[0])).Select(f => f.FullName).ToList();
        await client.DisconnectAsync(true);

        Assert.Contains(repoName, names);
        Assert.DoesNotContain("Administration", names);
    }
}
