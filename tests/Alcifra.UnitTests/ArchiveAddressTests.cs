using Microsoft.Extensions.Configuration;
using Alcifra.Api.Imap;

namespace Alcifra.UnitTests;

// A served message names the installation it came from, never a product's address (#1117).
public class ArchiveAddressTests
{
    [Theory]
    [InlineData("https://archiv.example.com", "https://archiv.example.com", "no-reply@archiv.example.com")]
    [InlineData("https://archiv.example.com/", "https://archiv.example.com", "no-reply@archiv.example.com")]
    [InlineData("https://example.com/archive/", "https://example.com/archive", "no-reply@example.com")]
    public void The_footer_and_the_sender_come_from_the_installations_address(string baseUrl, string url, string noReply)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = baseUrl })
            .Build();

        var archive = ArchiveAddress.From(configuration);

        Assert.Equal(url, archive.Url);
        Assert.Equal(noReply, archive.NoReply);
    }
}
