using Npgsql;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.UnitTests;

// #1752: Npgsql's default GSS (Kerberos) negotiation crashed the kiosk's app instances in native code. Every
// connection string this application builds switches it off, unless an operator wrote the keyword themselves.
public sealed class NpgsqlGssEncryptionTests
{
    [Fact]
    public void A_connection_string_without_the_keyword_gets_GSS_encryption_disabled()
    {
        var result = new NpgsqlConnectionStringBuilder(NpgsqlGssEncryption.Off("Host=db;Database=simplarchive;Username=app"));

        Assert.Equal(GssEncryptionMode.Disable, result.GssEncryptionMode);
        Assert.Equal(("db", "simplarchive", "app"), (result.Host, result.Database, result.Username));
    }

    [Theory]
    [InlineData("Host=db;GSS Encryption Mode=Prefer")]
    [InlineData("Host=db;GssEncryptionMode=Require")]
    public void An_operators_explicit_keyword_is_kept(string connectionString) =>
        Assert.Equal(connectionString, NpgsqlGssEncryption.Off(connectionString));

    [Fact]
    public void The_central_connection_string_carries_it_with_the_pool_ceiling()
    {
        var (connectionString, _, _) = DatabasePoolCeiling.Apply(NpgsqlGssEncryption.Off("Host=db"), configured: null);

        Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode);
    }
}
