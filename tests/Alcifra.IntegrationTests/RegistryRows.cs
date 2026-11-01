using Microsoft.Data.Sqlite;
using Alcifra.Domain.Certificates;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.IntegrationTests;

/// <summary>
/// A reader's certificate in the core registry, for tests that ask WHICH source answers rather than how a certificate is
/// enrolled (#1970 slice 4, where the registry replaced the user's own column as the core's answer).
/// </summary>
/// <remarks>
/// A registry row is a projection of a Certificate document, which needs a folder and a mask version; these tests are
/// about precedence, so the in-memory database's foreign keys are switched off instead of building an archive around the
/// row. Enrolment and its projection are tested end to end in <c>CertificateEnrolmentTests</c>.
/// </remarks>
internal static class RegistryRows
{
    public static async Task AddAsync(SqliteConnection connection, AlcifraDbContext db, Guid tenantId, Guid userId, string pem)
    {
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            await pragma.ExecuteNonQueryAsync();
        }

        db.EnrolledCertificates.Add(new EnrolledCertificate
        {
            DocumentId = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            CertificatePem = pem,
            Label = "Card",
            Thumbprint = Guid.NewGuid().ToString("N"),
            Serial = "01",
            NotBefore = DateTimeOffset.UtcNow.AddDays(-1),
            NotAfter = DateTimeOffset.UtcNow.AddYears(1),
        });
    }
}
