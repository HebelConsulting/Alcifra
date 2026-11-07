using System.Net;
using Alcifra.Sapass;
using Alcifra.Sapass.Infrastructure;

namespace Alcifra.UnitTests;

// An expired session answers 401 with no body, and the shared client's fallback then blamed the store. The tool says what
// it is and how to get a new one, for every command, because they all read through this client.
public class SapassSessionExpiryTests
{
    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private static HttpClient Client(HttpStatusCode status) =>
        new(new SessionExpiry("https://archive.example") { InnerHandler = new Answering(status) }) { BaseAddress = new Uri("https://archive.example/") };

    [Fact]
    public async Task A_401_says_the_session_expired_and_how_to_sign_in_again()
    {
        using var session = Session.Over(Client(HttpStatusCode.Unauthorized));

        var refusal = await Assert.ThrowsAsync<SapassException>(() => new StoreCatalog(session).StoreAsync(null, CancellationToken.None));

        Assert.Contains("session has expired", refusal.Message);
        Assert.Contains("apass login --url https://archive.example", refusal.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Any_other_answer_passes_through(HttpStatusCode status)
    {
        using var http = Client(status);

        using var response = await http.GetAsync("api");

        Assert.Equal(status, response.StatusCode);
    }
}
