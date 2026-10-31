using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace Alcifra.EndToEndTests;

// Certificate enrolment owned by the core (ADR 0936), end to end: offered only while an active module licenses it,
// configured by the tenant's own settings, filed into the "Reader certificates" folder, registered in the same
// transaction (ADR 0935), and answered as the reader's certificates. The policy is replaced only under If-Match.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public partial class CertificateEnrolmentTests
{
    private const string LicensesVariable = "SIMPLARCHIVE_TESTMODULE_LICENSES_ENROLMENT";
    private const string VerifyKeyVariable = "SIMPLARCHIVE_TESTMODULE_VERIFY_KEY";

    private readonly E2EApiFactory _factory;

    public CertificateEnrolmentTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Owner, HttpClient Admin, HttpClient Member, Guid TenantId, Guid RepoId)
    {
        public string MemberEmail { get; init; } = string.Empty;

        public string AdminEmail { get; init; } = string.Empty;
    }

    // `certificates list --holder` promised "only this holder's" while the server ignored the value and listed the
    // whole tenant. The filter is real now, and what an OMITTED holder means stays the caller's scope: their own, or
    // the tenant's for an administrator.
    // #1943: "my enrolled keys" were read from that listing, so in an ADMINISTRATOR's session every colleague's key
    // counted as the administrator's own and opened what it was addressed to — ADR 0969's own case (crypt@'s card opened
    // the Demo logins in florian@'s session). "me" carries the caller's own, whoever the caller is.
    [Fact]
    public async Task An_administrators_own_keys_are_theirs_alone_while_the_listing_still_shows_the_tenant()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            var root = Links(await TestJson.Get(rig.Admin, "/api"));
            var adminPem = SelfSigned("admin", rig.AdminEmail);
            await TestJson.Post(rig.Admin, root["certificates"], new { certificatePem = adminPem, label = "Admin card" });
            await TestJson.Post(rig.Admin, root["certificates"],
                new { certificatePem = SelfSigned("member", rig.MemberEmail), label = "Member card", holder = rig.MemberEmail });

            // The administration view is unchanged: the tenant.
            Assert.Equal(2, (await TestJson.Get(rig.Admin, root["certificates"])).EnumerateArray().Count());

            // "me" names the administrator's own key only, as an envelope names a recipient.
            using var admin = X509Certificate2.CreateFromPem(adminPem);
            var own = Assert.Single((await TestJson.Get(rig.Admin, root["me"])).GetProperty("enrolledKeys").EnumerateArray().ToList());
            Assert.Equal(admin.Issuer, own.GetProperty("issuer").GetString());
            Assert.Equal(admin.SerialNumber, own.GetProperty("serial").GetString());
            Assert.Equal("Admin card", own.GetProperty("label").GetString());

            // And the clients' enrolled set — what every opener admits a key by — is that, not the tenant.
            var mine = await Alcifra.ApiClient.EnrolledKeys.MineAsync(Alcifra.ApiClient.ApiCore.Over(rig.Admin));
            Assert.Equal(["Admin card"], mine!.Select(k => k.Label).Distinct().ToList());
            Assert.Equal(["Member card"], (await TestJson.Get(rig.Member, root["me"])).GetProperty("enrolledKeys")
                .EnumerateArray().Select(k => k.GetProperty("label").GetString()).ToList());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task The_listing_filters_by_holder_and_shows_a_member_only_their_own()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            var root = Links(await TestJson.Get(rig.Admin, "/api"));

            await TestJson.Post(rig.Admin, root["certificates"],
                new { certificatePem = SelfSigned("admin", rig.AdminEmail), label = "Admin card" });
            await TestJson.Post(rig.Admin, root["certificates"],
                new { certificatePem = SelfSigned("member", rig.MemberEmail), label = "Member card", holder = rig.MemberEmail });

            // The holder filter is real: only that holder's row comes back.
            var forMember = (await TestJson.Get(rig.Admin,
                    $"{root["certificates"]}?holder={Uri.EscapeDataString(rig.MemberEmail)}"))
                .EnumerateArray().ToList();
            Assert.Equal("Member card", Assert.Single(forMember).GetProperty("label").GetString());

            // An administrator who names nobody sees the tenant.
            Assert.Equal(2, (await TestJson.Get(rig.Admin, root["certificates"])).EnumerateArray().Count());

            // A member asking for somebody else's gets nothing, and their own listing stays their own.
            Assert.Empty((await TestJson.Get(rig.Member,
                $"{root["certificates"]}?holder={Uri.EscapeDataString(rig.AdminEmail)}")).EnumerateArray());
            var own = Assert.Single((await TestJson.Get(rig.Member, root["certificates"])).EnumerateArray().ToList());
            Assert.Equal("Member card", own.GetProperty("label").GetString());

            // A holder who is not a user here answers nothing rather than everything.
            Assert.Empty((await TestJson.Get(rig.Admin,
                $"{root["certificates"]}?holder={Uri.EscapeDataString($"nobody-{Guid.NewGuid():N}@e2e.local")}")).EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task A_member_enrols_their_own_certificate_and_is_addressed_by_it()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            var root = Links(await TestJson.Get(rig.Member, "/api"));
            Assert.Equal("/api/certificates", root["certificates"]);
            Assert.Equal("/api/certificate-policy", root["certificatePolicy"]);
            Assert.Equal("/api/certificates", root["encryption:certificates"]);   // the alias, for one release

            // Self-service is off until the tenant switches it on, and the self-enrolment list says so.
            var pem = SelfSigned("member", rig.MemberEmail);
            using (var refused = await rig.Member.PostAsJsonAsync(root["certificates"], new { certificatePem = pem, label = "Card" }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Contains("CERTIFICATE_SELF_SERVICE_DISABLED", await refused.Content.ReadAsStringAsync());
            }

            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);

            var enrolled = await TestJson.Post(rig.Member, root["certificates"], new { certificatePem = pem, label = "Card" });
            Assert.Equal("Card", enrolled.GetProperty("label").GetString());

            var listed = Assert.Single((await TestJson.Get(rig.Member, root["certificates"])).EnumerateArray().ToList());
            Assert.True(listed.GetProperty("usable").GetBoolean());

            // Issuer and serial: how a sealed envelope names a recipient, so a client tells "sealed to your key that is
            // not here" from "sealed for other keys", and uses only the user's own keys (ADR 0969).
            using (var certificate = X509Certificate2.CreateFromPem(pem))
            {
                Assert.Equal(certificate.Issuer, listed.GetProperty("issuer").GetString());
                Assert.Equal(certificate.SerialNumber, listed.GetProperty("serial").GetString(), StringComparer.OrdinalIgnoreCase);
            }

            // The core's registry answers who the member is addressed to.
            var me = Links(await TestJson.Get(rig.Member, "/api/me"));
            var readers = await TestJson.Get(rig.Member, me["readerCertificates"]);
            var entry = Assert.Single(readers.GetProperty("certificates").EnumerateArray().ToList());
            Assert.Equal(pem.Trim(), entry.GetProperty("pem").GetString()!.Trim());

            using (var again = await rig.Member.PostAsJsonAsync(root["certificates"], new { certificatePem = pem, label = "Card" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task The_policy_is_replaced_only_by_the_tag_the_administrator_saw()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: false, parent: rig.RepoId);
            var href = Links(await TestJson.Get(rig.Admin, "/api"))["certificatePolicy"];

            using var read = await rig.Admin.GetAsync(href);
            read.EnsureSuccessStatusCode();
            var seen = read.Headers.ETag!.Tag;
            var policy = new { minimumKeySizeBits = 3072 };

            using (var missing = await rig.Admin.PutAsJsonAsync(href, policy))
            {
                Assert.Equal((HttpStatusCode)428, missing.StatusCode);
            }

            using (var first = await PutWithTagAsync(rig.Admin, href, policy, seen))
            {
                first.EnsureSuccessStatusCode();
                Assert.NotEqual(seen, first.Headers.ETag!.Tag);
            }

            using (var stale = await PutWithTagAsync(rig.Admin, href, new { minimumKeySizeBits = 4096 }, seen))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            }

            Assert.Equal(3072, (await TestJson.Get(rig.Member, href)).GetProperty("minimumKeySizeBits").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task An_unattended_SETUP_ACCOUNT_sets_the_enrolment_settings_and_a_member_may_not()
    {
        // The kiosk's nightly seed signs in as a service account granted CanManageModules (ADR 0870) and sets where the
        // reader-certificate folder goes. These were module settings, which that right sets; the tenant settings refuse
        // every service account, which is why they are a resource of their own (ADR 0936).
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            var created = await TestJson.Post(rig.Admin, "/api/service-accounts", new { name = $"setup-{Guid.NewGuid():N}", canManageModules = true });
            using var setup = _factory.CreateAuthedClient(await _factory.GetTokenAsync(
                created.GetProperty("clientId").GetString()!, created.GetProperty("clientSecret").GetString()!));

            await SetCertificateSettingsAsync(setup, selfService: true, parent: rig.RepoId);

            var read = await TestJson.Get(rig.Admin, "/api/certificate-settings");
            Assert.True(read.GetProperty("selfServiceEnabled").GetBoolean());
            Assert.Equal(rig.RepoId, read.GetProperty("parentFolderId").GetGuid());
            Assert.StartsWith("Certificates ", read.GetProperty("parentFolderName").GetString());   // what a form shows

            using var refused = await rig.Member.GetAsync("/api/certificate-settings");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task An_unattended_SETUP_ACCOUNT_enrols_a_certificate_for_a_named_member()
    {
        // The first enrolment creates the "Reader certificates" folder, and the setup account is its creator: a service
        // account, not a user. Found live (Messenger demo, 2026-10-16): the folder was written with NO creator, the
        // CK_Documents_ExactlyOneCreator check refused it, and every unattended enrolment answered 500.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            var created = await TestJson.Post(rig.Admin, "/api/service-accounts", new { name = $"setup-{Guid.NewGuid():N}", canManageModules = true });
            using var setup = _factory.CreateAuthedClient(await _factory.GetTokenAsync(
                created.GetProperty("clientId").GetString()!, created.GetProperty("clientSecret").GetString()!));
            await SetCertificateSettingsAsync(setup, selfService: false, parent: rig.RepoId);
            await SetSanMustMatchAccountAsync(setup, true);   // what the Crypto seed states explicitly (ADR 0971)

            var enrolled = await TestJson.Post(setup, "/api/certificates", new { certificatePem = SelfSigned("member", rig.MemberEmail), label = "Card", holder = rig.MemberEmail });
            Assert.Equal("Card", enrolled.GetProperty("label").GetString());

            // The member is addressed by it, and the listing for the holder shows it.
            Assert.Single((await TestJson.Get(rig.Member, "/api/certificates")).EnumerateArray().ToList());
            Assert.Single((await TestJson.Get(setup, $"/api/certificates?holder={Uri.EscapeDataString(rig.MemberEmail)}")).EnumerateArray().ToList());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    // ADR 0971: a certificate is bound to its holder by e-mail unless the tenant opted out. Found live: a certificate
    // saying florian@… was enrolled for crypt@…, and crypt's documents were sealed to florian's key.
    [Fact]
    public async Task A_certificate_carrying_ANOTHER_address_is_refused_by_default_for_its_holder_and_for_a_named_holder()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            var someoneElse = $"someone-else-{Guid.NewGuid():N}@e2e.local";
            var policy = await TestJson.Get(rig.Member, "/api/certificate-policy");
            Assert.True(policy.GetProperty("sanMustMatchAccount").GetBoolean());   // what a tenant that never set one is told

            using (var own = await rig.Member.PostAsJsonAsync("/api/certificates", new { certificatePem = SelfSigned("member", someoneElse), label = "Card" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
                var problem = await own.Content.ReadAsStringAsync();
                Assert.Contains("CERTIFICATE_POLICY_REFUSED", problem, StringComparison.Ordinal);
                Assert.Contains(someoneElse, problem, StringComparison.Ordinal);       // what it says
                Assert.Contains(rig.MemberEmail, problem, StringComparison.Ordinal);   // what it wanted
            }

            // alcifra certificates enrol --holder: the same refusal, as the line the administrator reads.
            var cli = await Assert.ThrowsAsync<Alcifra.Cli.Infrastructure.CliException>(() =>
                new Alcifra.Cli.Infrastructure.AlcifraApi(rig.Admin).PostAsync("/api/certificates",
                    new { certificatePem = SelfSigned("member", someoneElse), label = "Card", holder = rig.MemberEmail }, CancellationToken.None));
            Assert.Contains("[CERTIFICATE_POLICY_REFUSED]", cli.Message, StringComparison.Ordinal);
            Assert.Contains(someoneElse, cli.Message, StringComparison.Ordinal);
            Assert.Contains(rig.MemberEmail, cli.Message, StringComparison.Ordinal);

            Assert.Empty((await TestJson.Get(rig.Member, "/api/certificates")).EnumerateArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task A_tenant_that_OPTED_OUT_enrols_it_and_switching_back_on_leaves_that_enrolment_alone()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            await SetSanMustMatchAccountAsync(rig.Admin, false);

            var pem = SelfSigned("member", $"pki-without-mail-{Guid.NewGuid():N}@e2e.local");
            await TestJson.Post(rig.Admin, "/api/certificates", new { certificatePem = pem, label = "Card", holder = rig.MemberEmail });

            // Back on: the rule is applied at the door, never backwards — the enrolment is neither refused nor flagged,
            // and it still addresses its holder.
            await SetSanMustMatchAccountAsync(rig.Admin, true);
            var listed = Assert.Single((await TestJson.Get(rig.Member, "/api/certificates")).EnumerateArray().ToList());
            Assert.True(listed.GetProperty("usable").GetBoolean());
            Assert.True(!listed.TryGetProperty("failsPolicy", out var fails) || fails.ValueKind == JsonValueKind.Null, $"flagged: {fails}");
            var me = Links(await TestJson.Get(rig.Member, "/api/me"));
            Assert.Single((await TestJson.Get(rig.Member, me["readerCertificates"])).GetProperty("certificates").EnumerateArray().ToList());

            // And the next one with a foreign address is refused again.
            using var refused = await rig.Admin.PostAsJsonAsync("/api/certificates",
                new { certificatePem = SelfSigned("member", $"other-{Guid.NewGuid():N}@e2e.local"), label = "Second", holder = rig.MemberEmail });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    // ADR 1015 (#1941): an account changes its address while the certificate on its key keeps the old one. The old address
    // becomes an identity alias on one of the tenant's identity domains, and Strict binding accepts a certificate naming
    // it, from the holder enrolling it and from an administrator naming the holder by it. An address the account was
    // never given is still refused.
    [Fact]
    public async Task Strict_binding_accepts_a_certificate_naming_an_identity_alias_of_its_holder()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);
            Assert.True((await TestJson.Get(rig.Member, "/api/certificate-policy")).GetProperty("sanMustMatchAccount").GetBoolean());

            // Domains and aliases are a user administrator's with the mail-routing right, granted explicitly.
            var routerEmail = $"router-{Guid.NewGuid():N}@e2e.local";
            await _factory.SeedUserAsync(rig.TenantId, routerEmail, "router-1234", "Router", canManageUsers: true, canManageMailRouting: true);
            using var router = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(routerEmail, "router-1234"));

            var domain = $"earlier{Guid.NewGuid():N}.example";
            var claimed = await TestJson.Post(router, "/api/tenant/mail-domains", new { domain, isReceiving = false });
            _factory.Dns.Publish(claimed.GetProperty("challengeName").GetString()!, claimed.GetProperty("challengeValue").GetString()!);
            await TestJson.Post(router, $"/api/tenant/mail-domains/{claimed.GetProperty("id").GetGuid()}/verify", new { });

            var row = (await TestJson.Get(router, "/api/users?limit=200")).GetProperty("users").EnumerateArray()
                .Single(u => string.Equals(u.GetProperty("email").GetString(), rig.MemberEmail, StringComparison.OrdinalIgnoreCase));
            var alias = $"crypt@{domain}";
            await TestJson.Post(router, Links(row)["identity-aliases"], new { address = alias });

            // The holder enrols a certificate naming the alias, though they sign in as another address.
            await TestJson.Post(rig.Member, "/api/certificates", new { certificatePem = SelfSigned("member", alias), label = "Card" });

            // An administrator names the holder BY the alias, and the listing for that address finds the same person.
            await TestJson.Post(rig.Admin, "/api/certificates", new { certificatePem = SelfSigned("member", alias), label = "Second card", holder = alias });
            Assert.Equal(2, (await TestJson.Get(rig.Admin, $"/api/certificates?holder={Uri.EscapeDataString(alias)}")).EnumerateArray().Count());
            Assert.Equal(2, (await TestJson.Get(rig.Member, "/api/certificates")).EnumerateArray().Count());

            // An address on the same domain that was never given to the account is still somebody else's.
            using var foreign = await rig.Member.PostAsJsonAsync("/api/certificates",
                new { certificatePem = SelfSigned("member", $"someone@{domain}"), label = "Not mine" });
            Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
            Assert.Contains("CERTIFICATE_POLICY_REFUSED", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    // As `alcifra certificates policy set --san-must-match-account on|off` does it.
    private static async Task SetSanMustMatchAccountAsync(HttpClient admin, bool on)
    {
        var written = await Alcifra.Cli.Commands.CertificatePolicySurface.SetSanMustMatchAccountAsync(
            new Alcifra.Cli.Infrastructure.AlcifraApi(admin), on, CancellationToken.None);
        Assert.Equal(on, written.GetProperty("sanMustMatchAccount").GetBoolean());
    }

    [Fact]
    public async Task Saconsole_switches_the_holders_address_rule_and_keeps_every_other_rule()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: false, parent: rig.RepoId);   // the policy is filed beneath it
            var href = Links(await TestJson.Get(rig.Admin, "/api"))["certificatePolicy"];
            using (var read = await rig.Admin.GetAsync(href))
            using (var write = await PutWithTagAsync(rig.Admin, href,
                       new { minimumKeySizeBits = 3072, issuerDnPattern = "^CN=Acme" }, read.Headers.ETag!.Tag))
            {
                Assert.True((await TestJson.Read(write)).GetProperty("sanMustMatchAccount").GetBoolean());   // left out: stays on
            }

            await SetSanMustMatchAccountAsync(rig.Admin, false);

            var policy = await TestJson.Get(rig.Member, href);
            Assert.False(policy.GetProperty("sanMustMatchAccount").GetBoolean());
            Assert.Equal(3072, policy.GetProperty("minimumKeySizeBits").GetInt32());
            Assert.Equal("^CN=Acme", policy.GetProperty("issuerDnPattern").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task Without_a_licensing_module_enrolment_does_not_exist()
    {
        var email = $"noenrol-core-{Guid.NewGuid():N}@e2e.local";
        const string password = "noenrol12345";
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        await _factory.SeedUserAsync(tenantId, email, password, "No Enrolment");
        using var member = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        Assert.DoesNotContain("certificates", Links(await TestJson.Get(member, "/api")).Keys);
        using var response = await member.GetAsync("/api/certificates");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PutWithTagAsync(HttpClient client, string href, object body, string tag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return await client.SendAsync(request);
    }

    // The enrolment mechanics are tested with hardware without attestation ALLOWED (ADR 0963), so a plain self-signed
    // certificate is enrolled; the attestation rules are CertificateEnrolmentTests.Attestation.cs's subject.
    private static Task SetCertificateSettingsAsync(Rig rig, bool selfService, Guid parent, bool unattested = true) =>
        SetCertificateSettingsAsync(rig.Admin, selfService, parent, unattested);

    private static async Task SetCertificateSettingsAsync(HttpClient client, bool selfService, Guid parent, bool unattested = true)
    {
        var href = Links(await TestJson.Get(client, "/api"))["certificateSettings"];
        using var read = await client.GetAsync(href);
        await TestJson.Read(read);
        using var write = await PutWithTagAsync(client, href,
            new { selfServiceEnabled = selfService, parentFolderId = parent, unattestedHardwareAllowed = unattested }, read.Headers.ETag!.Tag);
        await TestJson.Read(write);
    }

    // #1839: a user's OWN certificate (the self-service column) is carried into the registry once, so a module that
    // licenses enrolment does not silently stop addressing them; a signing-only one is not, and nothing is carried twice.
    [Fact]
    public async Task A_users_own_certificate_is_carried_into_the_registry_once_and_a_signing_one_is_not()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: false, parent: rig.RepoId);
            var own = SelfSigned("member's own", rig.MemberEmail);
            var signerEmail = $"signer-{Guid.NewGuid():N}@e2e.local";
            await _factory.SeedUserAsync(rig.TenantId, signerEmail, "Passw0rd!Passw0rd!", "Signer");
            await SetOwnCertificateAsync(rig.TenantId, rig.MemberEmail, own);
            await SetOwnCertificateAsync(rig.TenantId, signerEmail, SigningOnly("signer"));

            Assert.Equal(1, await AdoptAsync(rig.TenantId));   // the member's; the signing-only certificate is refused

            var me = Links(await TestJson.Get(rig.Member, "/api/me"));
            var readers = await TestJson.Get(rig.Member, me["readerCertificates"]);
            Assert.Equal(own.Trim(), Assert.Single(readers.GetProperty("certificates").EnumerateArray().ToList()).GetProperty("pem").GetString()!.Trim());

            Assert.Equal(0, await AdoptAsync(rig.TenantId));   // once: the next start carries nothing again

            // Deleted by an administrator: the next start does not bring it back from the column.
            var enrolled = Assert.Single((await TestJson.Get(rig.Member, "/api/certificates")).EnumerateArray().ToList());
            var address = $"/api/documents/{enrolled.GetProperty("documentId").GetGuid()}";
            using (var read = await rig.Admin.GetAsync(address))
            using (var delete = new HttpRequestMessage(HttpMethod.Delete, address))
            {
                delete.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
                using var deleted = await rig.Admin.SendAsync(delete);
                Assert.True(deleted.IsSuccessStatusCode, $"delete answered {(int)deleted.StatusCode}");
            }

            Assert.Equal(0, await AdoptAsync(rig.TenantId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    private async Task<int> AdoptAsync(Guid tenantId)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Alcifra.Infrastructure.Persistence.CurrentTenantAccessor>().TenantId = tenantId;
        await Alcifra.Infrastructure.Certificates.CertificateRegistryRebuild.RebuildTenantAsync(
            _factory.Services, tenantId, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var adopted = await scope.ServiceProvider.GetRequiredService<Alcifra.Api.Certificates.CertificateEnrolment>().AdoptOwnCertificatesAsync(CancellationToken.None);
        await Alcifra.Infrastructure.Certificates.CertificateRegistryRebuild.RebuildTenantAsync(
            _factory.Services, tenantId, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return adopted;
    }

    private async Task SetOwnCertificateAsync(Guid tenantId, string email, string pem)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Alcifra.Infrastructure.Persistence.AlcifraDbContext>();
        var normalized = email.ToUpperInvariant();
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.TenantId == tenantId && u.NormalizedEmail == normalized);
        user.SmimeCertificatePem = pem;
        await db.SaveChangesAsync();
    }

    private static string SigningOnly(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }

    // The holder's address rides as the e-mail SAN: enrolment asks for it by default since ADR 0971.
    private static string SelfSigned(string subject, string email)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress(email);
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }

    private static IReadOnlyDictionary<string, string> Links(JsonElement resource) =>
        resource.TryGetProperty("links", out var links)
            ? links.EnumerateArray()
                .GroupBy(l => l.GetProperty("rel").GetString()!)
                .ToDictionary(g => g.Key, g => g.First().GetProperty("href").GetString()!)
            : new Dictionary<string, string>();

    private async Task<Rig> RigAsync(ECDsa vendorKey)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Certificates {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var adminEmail = $"enrol-admin-{Guid.NewGuid():N}@e2e.local";
        var memberEmail = $"enrol-member-{Guid.NewGuid():N}@e2e.local";
        const string password = "enrolment1234";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Enrolment Admin", isTenantAdmin: true, canManageServiceAccounts: true);
        await _factory.SeedUserAsync(tenantId, memberEmail, password, "Enrolment Member");
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));
        var member = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(memberEmail, password));

        var license = new TenantLicense(["test-module"], tenantId,
                DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), ModuleAbiVersion.Major, string.Empty)
            .Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
        return new Rig(owner, admin, member, tenantId, repoId) { MemberEmail = memberEmail, AdminEmail = adminEmail };
    }
}
