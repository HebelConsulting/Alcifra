using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Alcifra.EndToEndTests;

// Identity and receiving domains, and identity aliases (#1941, ADR 1015).
//
// A receiving domain still identifies exactly one tenant, which is what keeps mail ingress unambiguous; an identity-only
// domain routes nothing and may sit on several. An alias says who an address names, never who signs in, and an address
// names at most one person. Each rule is asserted from the side a mistake would show: a second tenant receiving for one
// domain, a recipient on an identity-only domain being accepted, an address naming two people, an alias left on a domain
// that stopped vouching for it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class IdentityDomainsAndAliasesTests
{
    private const string Password = "alias-1234";
    private readonly E2EApiFactory _factory;

    public IdentityDomainsAndAliasesTests(E2EApiFactory factory) => _factory = factory;

    // A tenant administrator for mail routing and users, in a tenant of its own.
    private async Task<(HttpClient Client, Guid TenantId, string Email)> AdministratorAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, Password, "Alias Admin", canManageUsers: true, canManageMailRouting: true);
        return (_factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, Password)), tenantId, email);
    }

    private async Task<JsonElement> ClaimVerifiedAsync(HttpClient client, string domain, bool isIdentity = true, bool isReceiving = true)
    {
        var created = await TestJson.Post(client, "/api/tenant/mail-domains", new { domain, isIdentity, isReceiving });
        _factory.Dns.Publish(created.GetProperty("challengeName").GetString()!, created.GetProperty("challengeValue").GetString()!);
        return await TestJson.Post(client, $"/api/tenant/mail-domains/{created.GetProperty("id").GetGuid()}/verify", new { });
    }

    private static async Task<HttpResponseMessage> PutRolesAsync(HttpClient client, Guid id, object roles, string? etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/tenant/mail-domains/{id}/roles") { Content = JsonContent.Create(roles) };
        if (etag is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> ETagOfDomainAsync(HttpClient client, Guid id)
    {
        using var response = await client.GetAsync($"/api/tenant/mail-domains/{id}");
        response.EnsureSuccessStatusCode();
        return response.Headers.ETag!.ToString();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString()!;

    // The identity-aliases rel of the user who signs in as `email`, from the users listing's row.
    private static async Task<string> AliasesHrefAsync(HttpClient client, string email)
    {
        var listing = await TestJson.Get(client, "/api/users?limit=200");
        var row = listing.GetProperty("users").EnumerateArray()
            .Single(u => string.Equals(u.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase));
        return row.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "identity-aliases").GetProperty("href").GetString()!;
    }

    private async Task<bool> ResolvesAsync(string address)
    {
        using var scope = _factory.Services.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<Alcifra.Api.Lmtp.LmtpDelivery>();
        return (await delivery.ResolveAsync(address, CancellationToken.None)).Count > 0;
    }

    [Fact]
    public async Task Two_tenants_may_hold_one_identity_domain_but_only_one_receives_for_it()
    {
        var (first, _, _) = await AdministratorAsync();
        var (second, _, _) = await AdministratorAsync();
        using var _f = first;
        using var _s = second;
        var domain = $"shared{Guid.NewGuid():N}.example";

        await ClaimVerifiedAsync(first, domain);   // identity and receiving, as before the roles existed
        var held = await ClaimVerifiedAsync(second, domain, isReceiving: false);
        Assert.True(held.GetProperty("isIdentity").GetBoolean());
        Assert.False(held.GetProperty("isReceiving").GetBoolean());

        // Receiving is still one tenant's: neither a claim nor a role change makes it a second's.
        using (var claim = await second.PostAsJsonAsync("/api/tenant/mail-domains", new { domain = $"other{Guid.NewGuid():N}.example" }))
        {
            Assert.Equal(HttpStatusCode.Created, claim.StatusCode);
        }

        var id = held.GetProperty("id").GetGuid();
        using (var receive = await PutRolesAsync(second, id, new { isIdentity = true, isReceiving = true }, await ETagOfDomainAsync(second, id)))
        {
            Assert.Equal(HttpStatusCode.Conflict, receive.StatusCode);
            Assert.Equal("MAIL_DOMAIN_ALREADY_CLAIMED", await ErrorCodeAsync(receive));
        }

        // Roles are a tracked edit, and a domain is something.
        using (var bare = await PutRolesAsync(second, id, new { isIdentity = true, isReceiving = false }, etag: null))
        {
            Assert.Equal((HttpStatusCode)428, bare.StatusCode);
        }

        using (var nothing = await PutRolesAsync(second, id, new { isIdentity = false, isReceiving = false }, await ETagOfDomainAsync(second, id)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
            Assert.Equal("MAIL_DOMAIN_NEEDS_A_ROLE", await ErrorCodeAsync(nothing));
        }
    }

    [Fact]
    public async Task An_identity_only_domain_receives_no_mail_until_it_is_made_receiving()
    {
        var (client, tenantId, _) = await AdministratorAsync();
        using var _c = client;
        var domain = $"ident{Guid.NewGuid():N}.example";
        var address = $"reader@{domain}";
        await _factory.SeedUserAsync(tenantId, address, Password, "Reader");

        var held = await ClaimVerifiedAsync(client, domain, isReceiving: false);
        Assert.False(await ResolvesAsync(address));   // a verified domain with a user at it, and still no delivery

        var id = held.GetProperty("id").GetGuid();
        var stale = await ETagOfDomainAsync(client, id);
        using (var receive = await PutRolesAsync(client, id, new { isIdentity = true, isReceiving = true }, stale))
        {
            Assert.Equal(HttpStatusCode.OK, receive.StatusCode);
        }

        Assert.True(await ResolvesAsync(address));

        // The tag that preceded the change no longer describes the domain.
        using var replay = await PutRolesAsync(client, id, new { isIdentity = true, isReceiving = false }, stale);
        Assert.Equal(HttpStatusCode.PreconditionFailed, replay.StatusCode);
    }

    [Fact]
    public async Task An_alias_names_one_person_lives_on_a_verified_identity_domain_and_does_not_sign_in()
    {
        var (client, tenantId, adminEmail) = await AdministratorAsync();
        using var _c = client;
        var domain = $"alias{Guid.NewGuid():N}.example";
        var member = $"member-{Guid.NewGuid():N}@e2e.local";
        var colleague = $"colleague@{domain}";
        await _factory.SeedUserAsync(tenantId, member, Password, "Member");
        await _factory.SeedUserAsync(tenantId, colleague, Password, "Colleague");
        var aliases = await AliasesHrefAsync(client, member);
        var alias = $"old-address@{domain}";

        // Not yet a verified identity domain: the tenant vouches for nothing there.
        var claimed = await TestJson.Post(client, "/api/tenant/mail-domains", new { domain, isReceiving = false });
        using (var early = await client.PostAsJsonAsync(aliases, new { address = alias }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, early.StatusCode);
            Assert.Equal("IDENTITY_ALIAS_DOMAIN_NOT_IDENTITY", await ErrorCodeAsync(early));
        }

        _factory.Dns.Publish(claimed.GetProperty("challengeName").GetString()!, claimed.GetProperty("challengeValue").GetString()!);
        var domainId = claimed.GetProperty("id").GetGuid();
        await TestJson.Post(client, $"/api/tenant/mail-domains/{domainId}/verify", new { });

        var added = await TestJson.Post(client, aliases, new { address = alias });
        Assert.Equal(alias, added.GetProperty("address").GetString());
        Assert.True(added.TryGetProperty("confirmedAt", out var confirmed) && confirmed.ValueKind == JsonValueKind.String);

        // An address names at most one person: not another user's address, not somebody else's alias, and a user's
        // address cannot become somebody's alias either.
        using (var taken = await client.PostAsJsonAsync(aliases, new { address = colleague }))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Equal("IDENTITY_ALIAS_CONFLICT", await ErrorCodeAsync(taken));
        }

        using (var twice = await client.PostAsJsonAsync(await AliasesHrefAsync(client, colleague), new { address = alias.ToUpperInvariant() }))
        {
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        }

        var adminId = (await TestJson.Get(client, "/api/users?limit=200")).GetProperty("users").EnumerateArray()
            .Single(u => u.GetProperty("email").GetString() == adminEmail).GetProperty("id").GetGuid();
        using (var readdress = await client.PutAsJsonAsync($"/api/users/{adminId}/email", new { email = alias }))
        {
            if (readdress.StatusCode != HttpStatusCode.Forbidden)   // an installation may forbid address changes altogether
            {
                Assert.Equal(HttpStatusCode.Conflict, readdress.StatusCode);
                Assert.Equal("IDENTITY_ALIAS_CONFLICT", await ErrorCodeAsync(readdress));
            }
        }

        // The alias names the member, and it does not sign in.
        await Assert.ThrowsAnyAsync<Exception>(() => _factory.GetUserTokenAsync(alias, Password));

        // The domain keeps vouching while an alias lives on it.
        using (var withdraw = await PutRolesAsync(client, domainId, new { isIdentity = false, isReceiving = true }, await ETagOfDomainAsync(client, domainId)))
        {
            Assert.Equal(HttpStatusCode.Conflict, withdraw.StatusCode);
            Assert.Equal("MAIL_DOMAIN_HAS_IDENTITY_ALIASES", await ErrorCodeAsync(withdraw));
        }

        using (var remove = await client.DeleteAsync($"/api/tenant/mail-domains/{domainId}"))
        {
            Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        }

        // Removing the alias is a tracked delete; afterwards the domain may stop being an identity domain.
        var listed = Assert.Single((await TestJson.Get(client, aliases)).GetProperty("aliases").EnumerateArray().ToList());
        var removeHref = listed.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "remove").GetProperty("href").GetString()!;
        using (var bare = await client.DeleteAsync(removeHref))
        {
            Assert.Equal((HttpStatusCode)428, bare.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Delete, removeHref))
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(listed.GetProperty("eTag").GetString()!));
            using var removed = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Empty((await TestJson.Get(client, aliases)).GetProperty("aliases").EnumerateArray());
        using var finally_ = await PutRolesAsync(client, domainId, new { isIdentity = false, isReceiving = true }, await ETagOfDomainAsync(client, domainId));
        Assert.Equal(HttpStatusCode.OK, finally_.StatusCode);
    }
}
