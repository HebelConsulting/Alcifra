using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.KeyTest;

namespace SimplArchive.UnitTests.ApiClient;

// The hardware-key report's ONE shape (core #1864, ADR 0955): what it says, what it never says, and the archive side
// read through advertised rels only.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class KeyTestReportTests : ApiClientSettingsTest
{
    private const string Pin = "482915";
    private const string Thumbprint = "3f0c9d2e7a51b8c4d6e9f0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5";

    [Fact]
    public void The_report_carries_every_section_the_owner_named()
    {
        var text = KeyTestReportText.Render(Report(archive: Archive()));

        foreach (var expected in new[]
                 {
                     "== Environment ==", "SimplArchive Desktop 1.2.3", "Operating system", "Architecture",
                     "/usr/lib/opensc-pkcs11.so", "0.26", "Yubico YubiKey CCID (key present)",
                     "== The key ==", "Yubico", "YubiKey YK5", "5.7", "Serial number", "36771502", "Public key for Key Management",
                     "CN=Anna Example", "CN=Example CA", "EC P-256", Thumbprint, "(tested)",
                     "== The test ==", "Unlock the key with its PIN [YubiKey PIV #36771502]: FAILED", "CKR_PIN_INCORRECT (0x000000A0) in C_Login",
                     "== Archive side ==", "https://archive.example", "0.48.0", "yes, SHA-256 3f0c9d2e7a51b8c4",
                     "Encryption mode", KeyTestReportText.NotAdvertised, "the PIN is asked for every decryption",
                     KeyTestReportText.NeverContains,
                 })
        {
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }

        Assert.StartsWith("SimplArchive hardware key test, ", text, StringComparison.Ordinal);
        Assert.Contains("Result: FAILED at \"Unlock the key with its PIN\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PIN_given_to_the_builder_never_appears_in_the_text_even_when_an_error_echoes_it()
    {
        var builder = new KeyTestReportBuilder().NeverShow(Pin);
        builder.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Failed, $"the module said: login with '{Pin}' refused", "key", "CKR_PIN_INCORRECT");
        builder.Archive(Archive() with { Problems = [$"the PIN policy: echoed {Pin}"] });
        builder.ForgetSecrets();
        builder.Step(KeyTestStepKind.Open, KeyTestOutcome.Skipped, "after the secret was forgotten");

        var text = KeyTestReportText.Render(builder.Build());

        Assert.DoesNotContain(Pin, text, StringComparison.Ordinal);
        Assert.Contains(KeyTestReportBuilder.Withheld, text, StringComparison.Ordinal);
        Assert.Contains("after the secret was forgotten", text, StringComparison.Ordinal);   // not everything is withheld
    }

    [Fact]
    public void A_public_fact_that_happens_to_contain_the_PINs_digits_is_not_withheld()
    {
        // Withholding a serial because it contains the PIN's digits would say where the PIN sits in a number printed
        // on the key; structured fields are public facts and are not scanned (KeyTestReportBuilder's remarks).
        var builder = new KeyTestReportBuilder().NeverShow("7715");
        builder.Key(Key());

        Assert.Contains("36771502", KeyTestReportText.Render(builder.Build()), StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_code_is_structured_and_kept_when_a_PIN_coincides_with_it()
    {
        // A PIN of six zeros sits inside 0x000000A0; withholding the code would hide the one line saying the PIN was wrong.
        var builder = new KeyTestReportBuilder().NeverShow("000000");
        builder.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Failed, "refused", "key", "CKR_PIN_INCORRECT (0x000000A0) in C_Login");

        Assert.Equal("CKR_PIN_INCORRECT (0x000000A0) in C_Login", builder.Build().Steps.Single().ErrorCode);
    }

    [Fact]
    public void The_archive_switch_leaves_the_archive_side_out()
    {
        var text = KeyTestReportText.Render(Report(archive: Archive()), includeArchive: false);

        Assert.DoesNotContain("https://archive.example", text, StringComparison.Ordinal);
        Assert.Contains("Left out by the user.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Signed_out_the_archive_side_says_so()
    {
        Assert.Contains("Not signed in.", KeyTestReportText.Render(Report(archive: null)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(KeyTestOutcome.Passed, KeyTestVerdict.Works)]
    [InlineData(KeyTestOutcome.Failed, KeyTestVerdict.Failed)]
    public void The_verdict_follows_the_comparison(KeyTestOutcome compare, KeyTestVerdict expected)
    {
        var builder = new KeyTestReportBuilder();
        builder.Step(KeyTestStepKind.FindLibrary, KeyTestOutcome.Passed, "found");
        builder.Step(KeyTestStepKind.Compare, compare, "compared");

        Assert.Equal(expected, builder.Build().Verdict);
    }

    [Fact]
    public void A_declined_PIN_is_not_completed_rather_than_failed()
    {
        var builder = new KeyTestReportBuilder();
        builder.Step(KeyTestStepKind.Seal, KeyTestOutcome.Passed, "sealed");
        builder.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Skipped, "no PIN");

        Assert.Equal(KeyTestVerdict.NotCompleted, builder.Build().Verdict);
    }

    [Theory]
    [InlineData("rsa2048", "RSA 2048")]
    [InlineData("rsa3072", "RSA 3072")]
    [InlineData("p256", "EC P-256")]
    [InlineData("p384", "EC P-384")]
    public void The_key_type_names_algorithm_and_size(string kind, string expected)
    {
        using var certificate = Certificate(kind);

        Assert.Equal(expected, KeyTestCertificate.KeyTypeOf(certificate));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), KeyTestCertificate.ThumbprintOf(certificate));
    }

    [Fact]
    public void The_server_address_carries_no_credential()
    {
        Assert.Equal("https://archive.example/sa", KeyTestArchiveProbe.Address("https://user:secret@archive.example/sa/?token=abc"));
    }

    [Fact]
    public async Task The_archive_side_is_read_through_advertised_rels_and_answers_enrolment_per_certificate()
    {
        using var server = new LoopbackServer((method, path, _) => (method, path) switch
        {
            ("GET", "/api") => (200, """{"serverVersion":"0.48.0","links":[{"rel":"me","href":"/api/me"}]}"""),
            ("GET", "/api/me") => (200, """{"email":"anna@example","links":[{"rel":"self","href":"/api/me"},{"rel":"readerCertificates","href":"/api/me/reader-certificates"},{"rel":"offlineCachePolicy","href":"/api/me/offline-cache-policy"},{"rel":"encryption","href":"/api/me/encryption"}]}"""),
            ("GET", "/api/me/encryption") => (200, """{"mode":"Strict","wrapsAtRest":true,"refusesPlaintextDoors":true,"deliversEnvelopes":true,"envelopesMail":true,"links":[{"rel":"self","href":"/api/me/encryption"}]}"""),
            ("GET", "/api/me/reader-certificates") => (200, $$"""{"source":"module","certificates":[{"pem":"","label":"card","notAfter":"2030-01-01T00:00:00Z","thumbprintSha256":"{{Thumbprint.ToUpperInvariant()}}"}]}"""),
            ("GET", "/api/me/offline-cache-policy") => (200, """{"allowed":true,"notifications":true,"openedDocuments":true,"chosenFolders":false,"unlockMode":"Session","refreshMode":"KeepUpdated","maxUnlockedMinutes":30,"pinOncePerSession":true,"links":[{"rel":"self","href":"/api/me/offline-cache-policy"}]}"""),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');
        var core = new ApiCore("secret-access-token");

        var archive = await KeyTestArchiveProbe.GatherAsync(core, new ProfileClient(core), [Thumbprint, new string('0', 64)]);

        Assert.Equal("0.48.0", archive.ServerVersion);
        Assert.Equal("module", archive.CertificateSetSource);
        Assert.Equal([true, false], archive.Enrolments!.Select(e => e.Enrolled));
        Assert.Equal(new KeyTestEncryption("Strict", true, true, true, true), archive.Encryption);
        Assert.True(archive.PinOncePerSession);
        Assert.Equal("Session", archive.OfflineUnlockMode);
        Assert.Empty(archive.Problems);
        var text = KeyTestReportText.Render(Report(archive));
        Assert.Contains("Strict (wraps at rest: yes; refuses plaintext doors: yes; delivers envelopes: yes; envelopes mail: yes)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-access-token", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installation_that_advertises_none_of_the_rels_is_reported_as_not_advertising_them()
    {
        using var server = new LoopbackServer((method, path, _) => (method, path) switch
        {
            ("GET", "/api") => (200, """{"links":[{"rel":"me","href":"/api/me"}]}"""),
            ("GET", "/api/me") => (200, """{"links":[{"rel":"self","href":"/api/me"}]}"""),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');
        var core = new ApiCore("access-1");

        var archive = await KeyTestArchiveProbe.GatherAsync(core, new ProfileClient(core), [Thumbprint]);
        var text = KeyTestReportText.Render(Report(archive));

        Assert.Null(archive.Enrolments);
        Assert.Null(archive.PinOncePerSession);
        Assert.Null(archive.Encryption);   // an installation older than the rel: said, not guessed
        Assert.Contains($"{"Encryption mode",-22}: {KeyTestReportText.NotAdvertised}", text, StringComparison.Ordinal);
        Assert.Contains($"{"Enrolled for you",-22}: {KeyTestReportText.NotAdvertised}", text, StringComparison.Ordinal);
        Assert.Contains($"{"PIN policy",-22}: {KeyTestReportText.NotAdvertised}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_phone_names_its_connection_and_its_PIV_steps_and_no_library()
    {
        // The phone reaches the key over USB-C, NFC or Lightning, with no PKCS#11 library: "none found" there would send
        // support after a library a phone never has (core #1864's phone half).
        var builder = new KeyTestReportBuilder().NeverShow(Pin);
        builder.Environment(new KeyTestEnvironment("SimplArchive Mobile", "0.14.0", "Android 16 (Pixel 9)", "Arm64", null,
            [new KeyTestReader("NFC", true)], Connection: "NFC"));
        builder.Step(KeyTestStepKind.Connect, KeyTestOutcome.Passed, "Connected over NFC.");
        builder.Step(KeyTestStepKind.SelectPiv, KeyTestOutcome.Failed, "The key refused selecting PIV (status 6D00).", errorCode: "SW 6D00");

        var text = KeyTestReportText.Render(builder.Build());

        Assert.Contains($"{"Connection",-22}: NFC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PKCS#11", text, StringComparison.Ordinal);
        Assert.Contains("1. Connect to the key: passed", text, StringComparison.Ordinal);
        Assert.Contains("2. Select the PIV application: FAILED", text, StringComparison.Ordinal);
        Assert.Contains("   code: SW 6D00", text, StringComparison.Ordinal);
        Assert.Contains("Result: FAILED at \"Select the PIV application\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_step_has_a_printed_name_and_a_translated_title()
    {
        // The desktop titles a step with Strings.Get($"KtStep{kind}") and the phone likewise: an interpolated key no
        // key scan sees, so a new step kind is checked here.
        var resx = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "SimplArchive.Localization", "Strings.resx"));
        foreach (var kind in Enum.GetValues<KeyTestStepKind>())
        {
            Assert.NotEqual(kind.ToString(), KeyTestReportText.StepName(kind));
            Assert.Contains($"<data name=\"KtStep{kind}\"", resx, StringComparison.Ordinal);
        }
    }

    private static KeyTestReport Report(KeyTestArchiveSide? archive)
    {
        var builder = new KeyTestReportBuilder().NeverShow(Pin);
        builder.Environment(new KeyTestEnvironment("SimplArchive Desktop", "1.2.3", "Test OS 1.0", "Arm64",
            new KeyTestLibrary("/usr/lib/opensc-pkcs11.so", "0.26", "OpenSC Project", "OpenSC smartcard framework", "2.20"),
            [new KeyTestReader("Yubico YubiKey CCID", true)]));
        builder.Key(Key());
        builder.Step(KeyTestStepKind.FindLibrary, KeyTestOutcome.Passed, "/usr/lib/opensc-pkcs11.so");
        builder.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Failed, "The PIN was not accepted.", "YubiKey PIV #36771502",
            "CKR_PIN_INCORRECT (0x000000A0) in C_Login");
        builder.Archive(archive);
        return builder.Build();
    }

    private static KeyTestArchiveSide Archive() => new(
        "https://archive.example", "0.48.0", "module", null, [new KeyTestEnrolment(Thumbprint, true)], null, false, "Document", []);

    private static KeyTestKey Key() => new("YubiKey PIV #36771502", "Yubico", "YubiKey YK5", "5.7 (hardware 1.0)", "36771502",
        ["Public key for Key Management"],
        [new KeyTestCertificate("X.509 Certificate for Key Management", "CN=Anna Example", "CN=Example CA", "EC P-256",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(80), Thumbprint, Tested: true)]);

    private static X509Certificate2 Certificate(string kind)
    {
        CertificateRequest request = kind switch
        {
            "rsa2048" => new CertificateRequest("CN=t", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "rsa3072" => new CertificateRequest("CN=t", RSA.Create(3072), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            "p256" => new CertificateRequest("CN=t", ECDsa.Create(ECCurve.NamedCurves.nistP256), HashAlgorithmName.SHA256),
            _ => new CertificateRequest("CN=t", ECDsa.Create(ECCurve.NamedCurves.nistP384), HashAlgorithmName.SHA384),
        };
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
