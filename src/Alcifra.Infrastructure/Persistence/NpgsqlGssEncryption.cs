using Npgsql;

namespace Alcifra.Infrastructure.Persistence;

/// <summary>
/// Switches off Npgsql's attempt at GSS (Kerberos) encryption on every connection this application opens (#1752).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> By default Npgsql tries to negotiate GSS encryption on every NEW physical connection, before
/// authenticating. On the kiosk host that call into the native Kerberos library (<c>InitSecContext</c> in libgssapi)
/// crashed the process with SIGSEGV, several times a day, with no managed trace. The readiness probe opens an
/// unpooled connection every few seconds, so the crashing call ran constantly, even when the demo was idle. Found
/// from the first crash dump (2026-10-14). Nothing in Alcifra uses Kerberos: PostgreSQL is reached with a
/// password (rotated through OpenBao) over the network's TLS when configured.
/// </para>
/// <para>
/// <b>An operator's explicit keyword wins</b>, as with the pool ceiling: a deployment that does use Kerberos says
/// so in its connection string and keeps it. Asked of the untyped builder for the same reason as
/// <see cref="DatabasePoolCeiling"/>: the typed one reports a keyword at its default as present.
/// </para>
/// </remarks>
public static class NpgsqlGssEncryption
{
    private const string Keyword = "GSS Encryption Mode";

    /// <summary><paramref name="connectionString"/> with GSS encryption disabled, unless it already says otherwise.</summary>
    public static string Off(string connectionString)
    {
        var written = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        if (written.ContainsKey(Keyword) || written.ContainsKey("GssEncryptionMode"))
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString;
    }
}
