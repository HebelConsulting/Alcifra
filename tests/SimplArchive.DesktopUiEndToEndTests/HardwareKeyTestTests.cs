using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CAManagement.Pkcs11;
using CAManagement.Pkcs11.DataStructures;
using SimplArchive.ApiClient.KeyTest;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopUiEndToEndTests;

// Test the hardware key (core #1864, ADR 0955), with the card replaced by SOFTWARE keys behind the same port. The
// sealing is the real Messenger sealer and the opening is the document path's own routing (CardEnvelopeOpener.OpenWith)
// — only the key's one private operation is software — so "any RSA or EC key our rules allow passes" is checked
// against the code a real card goes through. No test here has touched a real card.
public class HardwareKeyTestTests
{
    private const string Pin = "482915";

    [Theory]
    [InlineData("rsa2048", "RSA 2048", "RSA key transport")]
    [InlineData("rsa3072", "RSA 3072", "RSA key transport")]
    [InlineData("p256", "EC P-256", "Elliptic-curve key agreement")]
    [InlineData("p384", "EC P-384", "Elliptic-curve key agreement")]
    public async Task Any_key_our_rules_allow_opens_a_message_sealed_to_its_own_certificate(string kind, string keyType, string scheme)
    {
        using var key = SoftwareKey.Create(kind, "X.509 Certificate for Key Management");
        var port = new SoftwarePort([key]);

        var report = await new HardwareKeyTest(port, _ => Task.FromResult<string?>(Pin), null, "1.2.3").RunAsync();

        Assert.Equal(KeyTestVerdict.Works, report.Verdict);
        Assert.Equal(
            [KeyTestStepKind.FindLibrary, KeyTestStepKind.ListReaders, KeyTestStepKind.ReadKey, KeyTestStepKind.ChooseCertificate,
             KeyTestStepKind.Seal, KeyTestStepKind.Unlock, KeyTestStepKind.Open, KeyTestStepKind.Compare],
            report.Steps.Select(s => s.Kind));
        Assert.All(report.Steps, s => Assert.Equal(KeyTestOutcome.Passed, s.Outcome));
        Assert.Contains(scheme, report.Steps.Single(s => s.Kind == KeyTestStepKind.Seal).Finding, StringComparison.Ordinal);
        var certificate = Assert.Single(Assert.Single(report.Keys).Certificates);
        Assert.Equal(keyType, certificate.KeyType);
        Assert.True(certificate.Tested);
        Assert.Equal(1, port.Opens);
    }

    [Fact]
    public async Task A_refused_PIN_fails_the_unlock_step_with_its_exact_code_and_the_PIN_never_reaches_the_report()
    {
        using var key = SoftwareKey.Create("p256", "X.509 Certificate for Key Management");

        var report = await new HardwareKeyTest(new SoftwarePort([key]), _ => Task.FromResult<string?>("777777"), null, "1.2.3").RunAsync();

        var failed = report.FirstFailure!;
        Assert.Equal(KeyTestStepKind.Unlock, failed.Kind);
        Assert.Equal("CKR_PIN_INCORRECT (0x000000A0) in C_Login", failed.ErrorCode);
        Assert.Contains("retry counter", failed.Finding, StringComparison.Ordinal);
        Assert.DoesNotContain("777777", KeyTestReportText.Render(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_whose_message_echoes_the_PIN_is_withheld_from_the_report()
    {
        using var key = SoftwareKey.Create("rsa2048", "X.509 Certificate for Key Management");
        var port = new SoftwarePort([key]) { FailOpenWith = pin => new InvalidOperationException($"driver trace: pin={pin}") };

        var report = await new HardwareKeyTest(port, _ => Task.FromResult<string?>(Pin), null, "1.2.3").RunAsync();
        var text = KeyTestReportText.Render(report);

        Assert.Equal(KeyTestStepKind.Open, report.FirstFailure!.Kind);
        Assert.Equal(KeyTestOutcome.Passed, report.Steps.Single(s => s.Kind == KeyTestStepKind.Unlock).Outcome);
        Assert.DoesNotContain(Pin, text, StringComparison.Ordinal);
        Assert.Contains(KeyTestReportBuilder.Withheld, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_library_the_first_step_says_what_to_install()
    {
        var report = await new HardwareKeyTest(new SoftwarePort([]) { NoLibrary = true }, _ => Task.FromResult<string?>(Pin), null, "1.2.3").RunAsync();

        var only = Assert.Single(report.Steps);
        Assert.Equal((KeyTestStepKind.FindLibrary, KeyTestOutcome.Failed, "NO_LIBRARY"), (only.Kind, only.Outcome, only.ErrorCode));
        Assert.Contains("PKCS#11 library  ".TrimEnd(), KeyTestReportText.Render(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_library_that_will_not_load_reports_the_PKCS11_return_value()
    {
        var port = new SoftwarePort([])
        {
            LibraryFailure = new KeyLibraryNotLoadedException(new Pkcs11Exception(CK_RV.CKR_GENERAL_ERROR, "C_Initialize")),
        };

        var report = await new HardwareKeyTest(port, _ => Task.FromResult<string?>(Pin), null, "1.2.3").RunAsync();

        Assert.Equal("CKR_GENERAL_ERROR (0x00000005) in C_Initialize", report.FirstFailure!.ErrorCode);
    }

    [Fact]
    public async Task A_key_with_no_certificate_is_told_so_and_never_asked_for_a_PIN()
    {
        using var key = SoftwareKey.Create("p256", "X.509 Certificate for Key Management");
        var port = new SoftwarePort([key]) { HideCertificates = true };
        var asked = false;

        var report = await new HardwareKeyTest(port, _ => { asked = true; return Task.FromResult<string?>(Pin); }, null, "1.2.3").RunAsync();

        Assert.Equal((KeyTestStepKind.ChooseCertificate, "NO_CERTIFICATE"), (report.FirstFailure!.Kind, report.FirstFailure.ErrorCode));
        Assert.False(asked);
    }

    [Fact]
    public async Task A_declined_PIN_leaves_the_test_not_completed_and_the_key_unused()
    {
        using var key = SoftwareKey.Create("p256", "X.509 Certificate for Key Management");
        var port = new SoftwarePort([key]);

        var report = await new HardwareKeyTest(port, _ => Task.FromResult<string?>(null), null, "1.2.3").RunAsync();

        Assert.Equal(KeyTestVerdict.NotCompleted, report.Verdict);
        Assert.Equal(0, port.Opens);
    }

    [Fact]
    public async Task Signed_in_the_archive_side_is_asked_about_the_certificate_that_was_tested()
    {
        using var key = SoftwareKey.Create("p256", "X.509 Certificate for Key Management");
        IReadOnlyList<string>? asked = null;

        var report = await new HardwareKeyTest(
            new SoftwarePort([key]),
            _ => Task.FromResult<string?>(Pin),
            thumbprints =>
            {
                asked = thumbprints;
                return Task.FromResult<KeyTestArchiveSide?>(new KeyTestArchiveSide("https://a", "1", "module", null, [], null, null, null, []));
            },
            "1.2.3").RunAsync();

        Assert.Equal([KeyTestCertificate.ThumbprintOf(key.Found.Certificate)], asked);
        Assert.NotNull(report.Archive);
    }

    [Fact]
    public void The_key_management_certificate_is_chosen_over_one_listed_before_it()
    {
        using var authentication = SoftwareKey.Create("p256", "X.509 Certificate for PIV Authentication");
        using var management = SoftwareKey.Create("p256", "Certificate for Key Management");

        Assert.Same(management.Found, HardwareKeyTest.ChooseKeyManagement([authentication.Found, management.Found]));
        Assert.Null(HardwareKeyTest.ChooseKeyManagement([]));
    }

    [Fact]
    public async Task The_window_lists_every_step_and_its_switch_takes_the_archive_side_out_of_the_copied_text()
    {
        using var key = SoftwareKey.Create("p256", "X.509 Certificate for Key Management");
        var test = new HardwareKeyTest(new SoftwarePort([key]), _ => Task.FromResult<string?>(Pin),
            _ => Task.FromResult<KeyTestArchiveSide?>(new KeyTestArchiveSide("https://archive.example", "1", "module", null, [], null, null, null, [])),
            "1.2.3");
        var model = new SimplArchive.DesktopClient.ViewModels.KeyTestViewModel(test, signedIn: true);
        string? copied = null;
        model.CopyToClipboard = text => { copied = text; return Task.CompletedTask; };

        await model.RunCommand.ExecuteAsync(null);
        Assert.Equal(8, model.Steps.Count);
        Assert.True(model.IncludeArchive);   // on by default
        Assert.Contains("https://archive.example", model.ReportText, StringComparison.Ordinal);

        model.IncludeArchive = false;
        await model.CopyCommand.ExecuteAsync(null);

        Assert.DoesNotContain("https://archive.example", copied, StringComparison.Ordinal);
        Assert.DoesNotContain(Pin, copied, StringComparison.Ordinal);
    }

    [Fact]
    public void Signed_out_the_window_offers_no_archive_switch()
    {
        var model = new SimplArchive.DesktopClient.ViewModels.KeyTestViewModel(
            new HardwareKeyTest(new SoftwarePort([]), _ => Task.FromResult<string?>(null), null, "1.2.3"), signedIn: false);

        Assert.False(model.CanIncludeArchive);
        Assert.False(model.IncludeArchive);
    }

    /// <summary>A key in software: its certificate as the token shows it (public only) and its private half.</summary>
    private sealed class SoftwareKey : IDisposable
    {
        private SoftwareKey(CardCertificates.Found found, AsymmetricAlgorithm privateKey)
        {
            Found = found;
            PrivateKey = privateKey;
        }

        public CardCertificates.Found Found { get; }

        public AsymmetricAlgorithm PrivateKey { get; }

        public static SoftwareKey Create(string kind, string label)
        {
            AsymmetricAlgorithm key = kind switch
            {
                "rsa2048" => RSA.Create(2048),
                "rsa3072" => RSA.Create(3072),
                "p256" => ECDsa.Create(ECCurve.NamedCurves.nistP256),
                _ => ECDsa.Create(ECCurve.NamedCurves.nistP384),
            };
            var request = key is RSA rsa
                ? new CertificateRequest("CN=Anna Example", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : new CertificateRequest("CN=Anna Example", (ECDsa)key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                key is RSA ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.KeyAgreement | X509KeyUsageFlags.DigitalSignature, critical: true));
            using var withKey = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
            return new SoftwareKey(new CardCertificates.Found("Software key", "36771502", label, publicOnly, [0x03]), key);
        }

        public byte[] Open(byte[] sealedMessage) => CardEnvelopeOpener.OpenWith(
            sealedMessage,
            Found.Certificate,
            wrapped => ((RSA)PrivateKey).Decrypt(wrapped, RSAEncryptionPadding.Pkcs1),
            point => SharedSecret(point, ((ECDsa)PrivateKey).ExportParameters(includePrivateParameters: true)));

        public void Dispose()
        {
            PrivateKey.Dispose();
            Found.Certificate.Dispose();
        }

        private static byte[] SharedSecret(byte[] originatorPoint, ECParameters privateKey)
        {
            var coordinateLength = (originatorPoint.Length - 1) / 2;
            using var originator = ECDiffieHellman.Create(new ECParameters
            {
                Curve = privateKey.Curve,
                Q = new ECPoint { X = originatorPoint[1..(1 + coordinateLength)], Y = originatorPoint[(1 + coordinateLength)..] },
            });
            using var mine = ECDiffieHellman.Create(privateKey);
            return mine.DeriveRawSecretAgreement(originator.PublicKey);
        }
    }

    /// <summary>The port with software keys, answering as a PKCS#11 library would — including its refusals.</summary>
    private sealed class SoftwarePort(IReadOnlyList<SoftwareKey> keys) : IHardwareKeyPort
    {
        public bool NoLibrary { get; init; }

        public Exception? LibraryFailure { get; init; }

        public bool HideCertificates { get; init; }

        public Func<string, Exception>? FailOpenWith { get; init; }

        public int Opens { get; private set; }

        public Task<KeyTestLibrary?> FindLibraryAsync() =>
            LibraryFailure is { } failure ? Task.FromException<KeyTestLibrary?>(failure)
            : Task.FromResult(NoLibrary ? null : new KeyTestLibrary("/usr/lib/software-pkcs11.so", "1.0", "Tests", "Software keys", "2.40"));

        public Task<KeyPortSurvey> SurveyAsync() => Task.FromResult(new KeyPortSurvey(
            [new KeyTestReader("Software reader", keys.Count > 0)],
            keys.Select(k => new KeyPortKey("Software key", "Tests", "Software", "1.0", k.Found.TokenSerial, ["Public key for Key Management"],
                HideCertificates ? [] : [k.Found])).ToList(),
            []));

        public Task<byte[]> OpenAsync(KeyPortKey key, CardCertificates.Found certificate, byte[] sealedMessage, string pin)
        {
            if (pin != Pin)
            {
                throw new KeyUnlockFailedException(new Pkcs11Exception(CK_RV.CKR_PIN_INCORRECT, "C_Login"));
            }

            Opens++;
            if (FailOpenWith is { } fail)
            {
                throw fail(pin);
            }

            return Task.FromResult(keys.Single(k => ReferenceEquals(k.Found, certificate)).Open(sealedMessage));
        }
    }
}
