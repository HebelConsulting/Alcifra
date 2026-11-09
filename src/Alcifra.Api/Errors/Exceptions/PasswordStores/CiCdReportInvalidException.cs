using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.PasswordStores;

// A report about a CI/CD secret (ADR 1084) that names no action the audit knows, a key the secret does not have, or a
// target it is not set on: refused, so the audit trail never records an act on something that does not exist.
public sealed class CiCdReportInvalidException : PasswordStoreException
{
    public CiCdReportInvalidException()
        : base("CICD_REPORT_INVALID", StatusCodes.Status400BadRequest,
            "The report names no known action, or a key or target this CI/CD secret does not have.")
    {
    }
}
