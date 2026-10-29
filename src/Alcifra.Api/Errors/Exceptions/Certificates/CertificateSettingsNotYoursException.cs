using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Certificates;

/// <summary>A caller without the module-administration right read or changed certificate enrolment's settings.</summary>
public sealed class CertificateSettingsNotYoursException : CertificateException
{
    public CertificateSettingsNotYoursException()
        : base("CERTIFICATE_SETTINGS_NOT_YOURS", StatusCodes.Status403Forbidden,
            "Only an administrator of modules can see or change certificate enrolment's settings.")
    {
    }
}
