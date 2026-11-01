using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Errors.Exceptions.ProtocolDevices;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Encryption;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>The rules of a user's protocol devices (#1970): naming, enrolment, and when mail encryption may be on.</summary>
public sealed class ProtocolDeviceEnrolment(
    AlcifraDbContext dbContext,
    EncryptionModes modes,
    ModuleReaderCertificates readerCertificates,
    TimeProvider clock)
{
    public const int MaxNameLength = 100;

    // Constructed, not injected, as every protocol door does: the hasher is stateless and not registered.
    private readonly PasswordHasher<User> _passwordHasher = new();

    /// <summary>Whether the organisation seals mail whatever a device says (owner, 2026-10-31: the <c>EnvelopesMail</c>
    /// modes). The setting is then shown on and locked.</summary>
    public async Task<bool> OrganisationSealsMailAsync(Guid tenantId, CancellationToken cancellationToken) =>
        modes.EnvelopesMail(await dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(cancellationToken));

    /// <summary>A trimmed name of 1 to <see cref="MaxNameLength"/> characters that none of the user's other devices has.</summary>
    public async Task<string> NameAsync(Guid userId, string? requested, Guid? exceptDeviceId, CancellationToken cancellationToken)
    {
        var name = requested?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            throw new ProtocolDeviceNameInvalidException();
        }

        if (await dbContext.ProtocolDevices.AnyAsync(d => d.UserId == userId && d.Name == name && d.Id != exceptDeviceId, cancellationToken))
        {
            throw new ProtocolDeviceNameTakenException(name);
        }

        return name;
    }

    /// <summary>
    /// Refuses mail encryption, with the reason, while the user has no hardware key to seal to (ADR 0963): the registry's
    /// answer, which leaves out a key that is not attested. Where the organisation seals mail anyway, the flag is
    /// disregarded and nothing is refused.
    /// </summary>
    public async Task RequireSealableAsync(Guid userId, bool encryptMail, bool organisationSealsMail, CancellationToken cancellationToken)
    {
        if (!encryptMail || organisationSealsMail)
        {
            return;
        }

        if ((await readerCertificates.ForAsync(userId, cancellationToken)).Certificates.Count == 0)
        {
            throw new DeviceEncryptionNeedsHardwareKeyException();
        }
    }

    /// <summary>A new enrolled device and its password, shown once. Tracked only; the caller saves.</summary>
    public async Task<(ProtocolDevice Device, string Password)> EnrolAsync(
        User user, string name, DeviceSwitches switches, CancellationToken cancellationToken)
    {
        // A 40-bit key: a collision is improbable but is checked rather than left to the unique index.
        var (key, secret, password) = ProtocolDevicePassword.Generate();
        while (await dbContext.ProtocolDevices.IgnoreQueryFilters(["TenantFilter"]).AnyAsync(d => d.CredentialKey == key, cancellationToken))
        {
            (key, secret, password) = ProtocolDevicePassword.Generate();
        }

        var device = new ProtocolDevice
        {
            Id = Guid.NewGuid(),
            TenantId = user.TenantId,
            UserId = user.Id,
            Kind = ProtocolDeviceKind.Enrolled,
            Name = name,
            CredentialKey = key,
            SecretHash = _passwordHasher.HashPassword(user, secret),
            CreatedAt = clock.GetUtcNow(),
        };
        switches.ApplyTo(device);
        dbContext.ProtocolDevices.Add(device);
        return (device, password);
    }
}

/// <summary>The four protocol switches and mail encryption, as a request states them in full (no PATCH, ADR 0872).</summary>
public sealed record DeviceSwitches(bool Mail, bool Files, bool Calendars, bool Contacts, bool EncryptMail)
{
    public void ApplyTo(ProtocolDevice device)
    {
        device.MailEnabled = Mail;
        device.FilesEnabled = Files;
        device.CalendarsEnabled = Calendars;
        device.ContactsEnabled = Contacts;
        device.EncryptMail = EncryptMail;
    }
}
