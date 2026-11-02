using System.Net;
using System.Text;
using System.Text.Json;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.UnitTests;

// alcifra certificates enrol from a token's CSR (ADR 1037): the authority gets the CSR, the holder and the attestation
// as given, and the certificate it answers is what the enrolment door then receives; a refusal is named.
public class CaSigningTests
{
    [Fact]
    public async Task The_authority_gets_the_request_holder_and_attestation_and_the_certificate_comes_back()
    {
        string? body = null;
        Uri? called = null;
        using var http = new HttpClient(new Handler((request, ct) =>
        {
            called = request.RequestUri;
            body = request.Content!.ReadAsStringAsync(ct).Result;
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"serial":"AB01","certificatePem":"-----BEGIN CERTIFICATE-----\nMA==\n-----END CERTIFICATE-----\n"}""", Encoding.UTF8, "application/json"),
            };
        }));
        var tenant = Guid.NewGuid();
        var attestation = new AttestationFiles { AttestationCertificatePem = "-----BEGIN CERTIFICATE-----\nA\n-----END CERTIFICATE-----\n", IntermediateCertificatePem = string.Empty, Slot = "9c" };

        var (pem, serial) = await CaSigning.SignAsync(http, new Uri("https://ca.test/"), tenant, "-----BEGIN CERTIFICATE REQUEST-----\nMA==\n-----END CERTIFICATE REQUEST-----\n", "anna@example.test", attestation, CancellationToken.None);

        Assert.Equal($"https://ca.test/tenants/{tenant:D}/sign", called!.ToString());
        var json = JsonDocument.Parse(body!).RootElement;
        Assert.Equal("anna@example.test", json.GetProperty("holderEmail").GetString());
        Assert.Contains("CERTIFICATE REQUEST", json.GetProperty("csrPem").GetString());
        Assert.Equal("9c", json.GetProperty("attestation").GetProperty("slot").GetString());
        Assert.Equal("AB01", serial);
        Assert.Contains("BEGIN CERTIFICATE", pem);
        Assert.True(CaSigning.IsCertificateRequest("-----BEGIN CERTIFICATE REQUEST-----"));
        Assert.False(CaSigning.IsCertificateRequest("-----BEGIN CERTIFICATE-----"));
    }

    [Fact]
    public async Task A_refusal_is_named_by_the_authoritys_code()
    {
        using var http = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("""{"errorCode":"CSR_INVALID","title":"x"}""", Encoding.UTF8, "application/problem+json"),
        }));
        var refusal = await Assert.ThrowsAsync<CliException>(() => CaSigning.SignAsync(http, new Uri("https://ca.test/"), Guid.NewGuid(), "csr", "anna@example.test", null, CancellationToken.None));
        Assert.Contains("422 CSR_INVALID", refusal.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer(request, cancellationToken));
    }
}
