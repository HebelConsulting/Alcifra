using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Controllers;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Certificates;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Notifications;
using Alcifra.Infrastructure.Certificates;
using Alcifra.Infrastructure.Certificates.Attestation;
using Alcifra.Infrastructure.Certificates.Policy;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Certificates;

/// <summary>
/// Certificate enrolment, owned by the core (ADR 0936; moved from the Encryption Module): validate a certificate
/// against enrolment's own checks and the tenant's policy, then file it as a Certificate document in the tenant's
/// "Reader certificates" folder, where the registry projection (ADR 0935) picks it up in the same transaction.
/// </summary>
/// <remarks>
/// <b>Enrolment validates BEFORE it files.</b> A document filed first and validated later leaves the archive holding a
/// certificate a reader can see and which silently addresses nothing, so every refusal happens before anything is
/// written, and names what is wrong in terms the person who chose the file can act on.
/// </remarks>
public sealed class CertificateEnrolment(
    AlcifraDbContext dbContext,
    IObjectStorageClient storage,
    DocumentFinalizer finalizer,
    IAuditRecorder audit,
    DocumentAccessService access,
    ICurrentTenantAccessor tenant,
    IReadOnlyList<ModuleLoader.LoadedModule> modules,
    EnrolmentAttestation attestation,
    ILogger<CertificateEnrolment> logger)
{
    /// <summary>An enrolled certificate as the listing shows it.</summary>
    /// <param name="Usable">Whether it addresses its holder now: valid, not revoked, and accepted by the tenant's attestation rules.</param>
    /// <param name="Attestation">"attested", "rootNotTrusted" or "notAttested" (ADR 0963).</param>
    /// <param name="AttestationRoot">The name of the root the key is attested to, where it is.</param>
    /// <param name="DeviceSerial">The token's serial number, as its attestation states it.</param>
    public sealed record Listed(
        Guid DocumentId, string Label, string Thumbprint, string Serial, DateTimeOffset NotAfter, DateTimeOffset? RevokedAt,
        bool Usable, string? FailsPolicy, string Attestation = "notAttested", string? AttestationRoot = null, string? DeviceSerial = null,
        string? Issuer = null);

    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("Certificate enrolment needs a current tenant.");

    /// <summary>Whether the caller administers enrolment: enrols for others and sets the policy.</summary>
    public Task<bool> CallerAdministersAsync(CancellationToken cancellationToken) => access.CanAdministerModulesAsync(cancellationToken);

    /// <summary>
    /// The certificates a listing shows: the caller's own, a named holder's (an administrator's question), or the whole
    /// tenant's when an administrator names nobody.
    /// </summary>
    /// <param name="holder">
    /// A holder's e-mail, or null/empty for "mine" (or, for an administrator, "the tenant's"). A holder that is not a
    /// user here answers nothing rather than refusing: the CLI's <c>--holder</c> promised a filter that the server did
    /// not implement, and an empty answer is the honest one for somebody who is not enrolled.
    /// </param>
    public async Task<IReadOnlyList<Listed>> ListAsync(string? holder, CancellationToken cancellationToken)
    {
        var query = dbContext.EnrolledCertificates.AsNoTracking();
        if (holder is { Length: > 0 })
        {
            // Asking for somebody else's certificates is an administrator's question; anyone else gets an empty
            // answer rather than a refusal, as the module always answered.
            if (!await CallerAdministersAsync(cancellationToken))
            {
                return [];
            }

            if (await dbContext.UserNamedByAsync(holder, cancellationToken) is not { } holderUserId)
            {
                return [];
            }

            query = query.Where(c => c.UserId == holderUserId);
        }
        else if (!await CallerAdministersAsync(cancellationToken))
        {
            var userId = access.GetCallerIdentity().UserId;
            query = query.Where(c => c.UserId == userId);
        }

        var rows = await query.ToListAsync(cancellationToken);
        var policy = await CurrentPolicyAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var trust = await AttestationTrust.ForTenantAsync(dbContext, TenantId, cancellationToken);
        var ids = rows.Select(r => r.DocumentId).ToList();
        var serials = await dbContext.CertificateAttestations.AsNoTracking().Where(a => ids.Contains(a.DocumentId))
            .ToDictionaryAsync(a => a.DocumentId, a => a.DeviceSerial, cancellationToken);
        return [.. rows
            .OrderByDescending(r => r.NotAfter)
            .ThenBy(r => r.Thumbprint, StringComparer.Ordinal)
            .Select(r => new Listed(
                r.DocumentId, r.Label, r.Thumbprint, r.Serial, r.NotAfter, r.RevokedAt,
                r.RevokedAt is null && r.NotBefore <= now && r.NotAfter > now && trust.Addresses(r.AttestationRootThumbprint),
                PolicyEnforcement.RefusalForEnrolled(policy, r.CertificatePem),
                StateName(trust.StateOf(r.AttestationRootThumbprint)),
                r.AttestationRootThumbprint is { } root ? trust.RootNames.GetValueOrDefault(root) : null,
                serials.GetValueOrDefault(r.DocumentId) is { Length: > 0 } serial ? serial : null,
                IssuerOf(r.CertificatePem)))];
    }

    /// <summary>An enrolled certificate as an envelope names its recipient, with the label its holder knows it by.</summary>
    public sealed record OwnKey(string Issuer, string Serial, string Label);

    /// <summary>
    /// The certificates enrolled for THIS user, usable or not — whoever asks (ADR 0969's enrolled set; #1943).
    /// </summary>
    /// <remarks>
    /// Not <see cref="ListAsync"/>: an administrator who names no holder is answered with the whole tenant there, and the
    /// clients took that answer for "my keys" — so in an administrator's session every colleague's key was admitted, the
    /// case ADR 0969 exists to prevent (crypt@'s card opened the Demo logins in florian@'s session, 2026-10-27). A row
    /// whose certificate does not parse cannot be matched and is left out.
    /// </remarks>
    public async Task<IReadOnlyList<OwnKey>> OwnAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.EnrolledCertificates.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Label)
            .Select(c => new { c.CertificatePem, c.Serial, c.Label })
            .ToListAsync(cancellationToken);
        return [.. rows
            .Select(r => (Issuer: IssuerOf(r.CertificatePem), r.Serial, r.Label))
            .Where(r => r.Issuer is not null)
            .Select(r => new OwnKey(r.Issuer!, r.Serial, r.Label))];
    }

    /// <summary>
    /// The certificate's issuer as .NET formats it — with the serial, how an envelope names its recipient — so a client can
    /// tell "sealed to a key of yours that is not here" from "sealed for other keys" (ADR 0969). Read from the stored
    /// certificate at listing time, so no column is needed; null for one that does not parse.
    /// </summary>
    private static string? IssuerOf(string certificatePem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            return certificate.Issuer;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The wire name of an attestation state, as the listing carries it.</summary>
    public static string StateName(AttestationState state) => state switch
    {
        AttestationState.Attested => "attested",
        AttestationState.RootNotTrusted => "rootNotTrusted",
        _ => "notAttested",
    };

    /// <summary>
    /// Validates and files one certificate, with its key's attestation where one is sent (ADR 0963); returns what the
    /// listing would show for it, and whether a NEW enrolment was filed (false: evidence was added to an existing one).
    /// Every refusal is audited, by code and holder, never with the certificate.
    /// </summary>
    public async Task<(Listed Enrolled, bool Created)> EnrolAsync(
        string certificatePem, string? label, string? holder, AttestationEvidence? evidence, CancellationToken cancellationToken)
    {
        try
        {
            return await EnrolCheckedAsync(certificatePem, label, holder, evidence, cancellationToken);
        }
        catch (CertificateException refusal) when (refusal is not CertificateAlreadyEnrolledException)
        {
            await audit.RecordAsync(AuditActions.CertificateEnrolmentRefused, "Certificate", null, holder is { Length: > 0 } ? holder : "self",
                refusal.ErrorCode, cancellationToken: cancellationToken);
            throw;
        }
    }

    private async Task<(Listed Enrolled, bool Created)> EnrolCheckedAsync(
        string certificatePem, string? label, string? holder, AttestationEvidence? evidence, CancellationToken cancellationToken)
    {
        var admin = await CallerAdministersAsync(cancellationToken);
        var forSomebodyElse = holder is { Length: > 0 };
        if (forSomebodyElse && !admin)
        {
            throw new CertificateNotYoursToEnrolException();
        }

        if (!forSomebodyElse && !admin && !await dbContext.Tenants.Where(t => t.Id == TenantId).Select(t => t.CertificateSelfServiceEnabled).SingleAsync(cancellationToken))
        {
            throw new CertificateSelfServiceDisabledException();
        }

        // Possession (ADR 1017): without a proof that the caller holds the key, only an administrator says whose it is.
        var policy = await CurrentPolicyAsync(cancellationToken);
        if (policy.EffectiveBinding == CertificateBinding.Possession && !admin)
        {
            throw new CertificatePossessionNeedsAdministratorException();
        }

        var checkedPem = EnrolmentPolicy.Check(certificatePem);
        if (!checkedPem.Ok)
        {
            throw new CertificateRefusedException(checkedPem.Refusal!);
        }

        using var leaf = checkedPem.Leaf!;
        var userId = access.GetCallerIdentity().UserId;
        var holderAddress = forSomebodyElse
            ? holder!
            : await dbContext.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken);
        if (holderAddress is not { Length: > 0 })
        {
            throw new CertificateHolderMissingException();
        }

        var holderId = await HolderIdAsync(holderAddress, forSomebodyElse, userId, cancellationToken);
        if (PolicyEnforcement.Refusal(policy, leaf, holderAddress, certificatePem,
                await IdentityAliasesAsync(holderId, cancellationToken)) is { } policyRefusal)
        {
            throw new CertificatePolicyRefusedException(policyRefusal);
        }

        var thumbprint = Convert.ToHexString(SHA256.HashData(leaf.RawData));
        var existing = await dbContext.EnrolledCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Thumbprint == thumbprint, cancellationToken);

        // THE WAY BACK for a key enrolled before attestation was required (ADR 0963): the same key, enrolled again with its
        // attestation, by its holder (where self-service is on) or an administrator, gains the evidence. No new key, no new
        // document, and nothing an administrator has to do for a holder who can do it themselves.
        if (existing is not null && evidence is not null
            && !await dbContext.CertificateAttestations.AnyAsync(a => a.DocumentId == existing.DocumentId, cancellationToken)
            && holderId == existing.UserId)
        {
            return (await AttestExistingAsync(existing, evidence, leaf, cancellationToken), false);
        }

        if (existing is not null)
        {
            throw new CertificateAlreadyEnrolledException(existing.UserId == userId
                ? $"This certificate is already enrolled as '{existing.Label}'"
                    + $"{(existing.RevokedAt is { } when ? $", revoked on {when:yyyy-MM-dd}" : string.Empty)}"
                    + ". Enrolling it again would change nothing; revoke the existing entry first if it should no longer "
                    + "address documents."
                : "This certificate is already enrolled for somebody else in this tenant. One key cannot be held by two "
                    + "readers; if that is genuinely the intent, the existing enrolment has to be revoked first.");
        }

        // One key, one holder, whatever the binding (ADR 1017): the same key under ANOTHER certificate is as much the same
        // key as the same certificate is, and two readers holding it would each read what is sealed to the other.
        if (await KeyHeldByAnotherAsync(leaf, holderId, cancellationToken))
        {
            throw new CertificateAlreadyEnrolledException(
                "This key is already enrolled for somebody else in this tenant, under another certificate. One key cannot "
                + "be held by two readers; if that is genuinely the intent, the existing enrolment has to be revoked first.");
        }

        await EnsureRoomAsync(holderId, cancellationToken);

        var evidenceRow = await attestation.DecideAsync(evidence, leaf, cancellationToken);
        return (await FileEnrolmentAsync(certificatePem, leaf, thumbprint, label, holderAddress, access.GetCallerIdentity(),
            $"Holder {holderAddress}, thumbprint {thumbprint}, {(evidenceRow is null ? "no attestation" : $"attestation of token {evidenceRow.DeviceSerial}")}",
            cancellationToken, evidenceRow), true);
    }

    // The core's own tier (#1970 slice 4): without a licensing module, a holder keeps at most two unrevoked certificates.
    // A holder who is not a user yet holds none.
    private async Task EnsureRoomAsync(Guid? holderId, CancellationToken cancellationToken)
    {
        if (holderId is { } id
            && await CertificateEnrolmentLicence.PerUserLimitAsync(dbContext, modules, DateTimeOffset.UtcNow, cancellationToken) is { } limit
            && await dbContext.EnrolledCertificates.CountAsync(c => c.UserId == id && c.RevokedAt == null, cancellationToken) >= limit)
        {
            throw new CertificateLimitReachedException(limit);
        }
    }

    // Whether another user in the tenant holds an unrevoked enrolment of this public key. Read from the stored
    // certificates rather than a column: an enrolment table is small, and nothing has to be migrated for it.
    private async Task<bool> KeyHeldByAnotherAsync(X509Certificate2 leaf, Guid? holderId, CancellationToken cancellationToken)
    {
        var key = leaf.PublicKey.ExportSubjectPublicKeyInfo();
        var others = await dbContext.EnrolledCertificates.AsNoTracking()
            .Where(c => c.UserId != holderId && c.RevokedAt == null)
            .Select(c => c.CertificatePem)
            .ToListAsync(cancellationToken);
        return others.Any(pem => SameKey(pem, key));
    }

    private static bool SameKey(string pem, byte[] key)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return certificate.PublicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;   // an unreadable stored certificate addresses nobody, so it holds no key either
        }
    }

    private async Task<Guid?> HolderIdAsync(string holderAddress, bool forSomebodyElse, Guid? callerId, CancellationToken cancellationToken) =>
        forSomebodyElse ? await dbContext.UserNamedByAsync(holderAddress, cancellationToken) : callerId;

    // The holder's confirmed identity aliases, which Strict binding accepts beside their own address (ADR 1015).
    private async Task<IReadOnlyCollection<string>> IdentityAliasesAsync(Guid? userId, CancellationToken cancellationToken) =>
        userId is { } id
            ? await dbContext.UserIdentityAliases.Where(a => a.UserId == id && a.ConfirmedAt != null).Select(a => a.Address).ToListAsync(cancellationToken)
            : [];

    // Adds the evidence to an enrolment that had none; the projection re-derives its root in the same save.
    private async Task<Listed> AttestExistingAsync(
        Domain.Certificates.EnrolledCertificate existing, AttestationEvidence evidence, X509Certificate2 leaf, CancellationToken cancellationToken)
    {
        var row = await attestation.DecideAsync(evidence, leaf, cancellationToken)
            ?? throw new InvalidOperationException("Evidence was sent, so the decision returns its row or refuses.");
        row.DocumentId = existing.DocumentId;
        dbContext.CertificateAttestations.Add(row);
        await dbContext.SaveChangesAsync(cancellationToken);

        await audit.RecordAsync(AuditActions.CertificateAttestationAdded, "Document", existing.DocumentId, existing.Label,
            $"Thumbprint {existing.Thumbprint}, attestation of token {row.DeviceSerial}", cancellationToken: cancellationToken);
        logger.LogInformation("Added the attestation of token {DeviceSerial} to enrolled certificate {Thumbprint}", row.DeviceSerial, existing.Thumbprint);
        return (await ListAsync(holder: null, cancellationToken)).Single(l => l.DocumentId == existing.DocumentId);
    }

    /// <summary>
    /// Files a checked certificate as a Certificate document in the "Reader certificates" folder, as
    /// <paramref name="creator"/>, and records it: what an enrolment and the carry-over of a user's own certificate
    /// (#1839) both do, so the filing exists once.
    /// </summary>
    private async Task<Listed> FileEnrolmentAsync(
        string certificatePem, X509Certificate2 leaf, string thumbprint, string? label, string holderAddress,
        (Guid? UserId, Guid? ServiceAccountId) creator, string auditDetail, CancellationToken cancellationToken,
        Domain.Certificates.CertificateAttestation? evidence = null)
    {
        var notBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Holder"] = holderAddress,
            ["Label"] = label is { Length: > 0 } ? label : leaf.GetNameInfo(X509NameType.SimpleName, false),
            ["Subject"] = leaf.Subject,
            ["Issuer"] = leaf.Issuer,
            ["Thumbprint"] = thumbprint,
            ["Serial"] = leaf.SerialNumber.ToUpperInvariant(),
            ["Valid from"] = Instant(notBefore),
            ["Valid until"] = Instant(notAfter),
        };

        Document document;
        var owned = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        await using (owned)
        {
            var folder = await ReaderCertificateFolder.FindOrCreateAsync(dbContext, TenantId, creator, cancellationToken)
                ?? throw new CertificateFolderNotConfiguredException();
            document = await FileAsync(folder.Id, WellKnownMaskIds.Certificate, $"{fields["Label"]} ({thumbprint[..16]})",
                Encoding.UTF8.GetBytes(certificatePem), ".pem", "application/x-pem-file", creator, cancellationToken);
            // The evidence in the same save as the fields, so the projection that save triggers derives the root from it.
            if (evidence is not null)
            {
                evidence.DocumentId = document.Id;
                dbContext.CertificateAttestations.Add(evidence);
            }

            await SetFieldsAsync(document, fields, cancellationToken);
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }

        // After the commit: an event announcing an enrolment that then rolled back would be a lie.
        await audit.RecordAsync(AuditActions.CertificateEnrolled, "Document", document.Id, document.Name,
            auditDetail, cancellationToken: cancellationToken);
        logger.LogInformation("Enrolled certificate {Thumbprint} as document {DocumentId}", thumbprint, document.Id);
        return (await ListAsync(holder: null, cancellationToken)).FirstOrDefault(l => l.DocumentId == document.Id)
            ?? new Listed(document.Id, fields["Label"], thumbprint, fields["Serial"], notAfter, null, false, null);
    }

    /// <summary>
    /// Carries each active user's OWN certificate (the retired self-service column, <c>User.SmimeCertificatePem</c>) into
    /// the registry, once (#1839; on every tenant since #1970 slice 4, where the registry is the core's answer too).
    /// Applies enrolment's own checks and the core's limit of two; never carries a certificate that was filed before —
    /// revoked or deleted included, so an administrator's act is not undone at the next start. A user whose certificate
    /// then does not address them (not carried, or carried but not accepted by the tenant's attestation rules, which a
    /// bare certificate cannot pass) is told so once, by notification. The column is dropped in a later release.
    /// </summary>
    /// <returns>How many certificates were carried, and how many users were told theirs no longer addresses them.</returns>
    public async Task<(int Carried, int Notified)> AdoptOwnCertificatesAsync(INotificationService notifications, CancellationToken cancellationToken)
    {
        var waiting = await dbContext.Users
            .Where(u => u.IsActive && u.SmimeCertificatePem != null && u.SmimeCertificatePem != string.Empty)
            .Where(u => !dbContext.Notifications.Any(n => n.RecipientUserId == u.Id && n.Type == NotificationType.OwnCertificateRetired))
            .Select(u => new { u.Id, u.Email, Pem = u.SmimeCertificatePem! })
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return (0, 0);
        }

        var policy = await CurrentPolicyAsync(cancellationToken);
        var limit = await CertificateEnrolmentLicence.PerUserLimitAsync(dbContext, modules, DateTimeOffset.UtcNow, cancellationToken);
        var (carried, notified) = (0, 0);
        foreach (var user in waiting)
        {
            var outcome = await CarryAsync(user.Id, user.Email, user.Pem, policy, limit, cancellationToken);
            if (outcome is CarryOutcome.FolderMissing)
            {
                logger.LogWarning("{Count} users' own certificates wait to be carried into the certificate registry: this tenant has "
                    + "no folder for reader certificates chosen yet (#1839). Choose one before the column is dropped; the next start "
                    + "carries them.", waiting.Count - carried);
                break;
            }

            carried += outcome is CarryOutcome.Carried ? 1 : 0;
            if (!await AddressesAsync(user.Id, user.Pem, cancellationToken))
            {
                await notifications.NotifyAsync(user.Id, NotificationType.OwnCertificateRetired,
                    "Your encrypted-mail certificate was retired",
                    "Mail and documents are no longer sealed to the certificate you registered for encrypted mail. To have them "
                    + "sealed again, enrol a hardware key (a smartcard or security key) as your reader certificate.",
                    cancellationToken: cancellationToken);
                notified++;
            }
        }

        return (carried, notified);
    }

    private enum CarryOutcome
    {
        Carried,
        NotCarried,
        FolderMissing,
    }

    // One user's own certificate into the registry, if enrolment's checks, the policy and the limit allow it.
    private async Task<CarryOutcome> CarryAsync(
        Guid userId, string email, string pem, CertificatePolicy policy, int? limit,
        CancellationToken cancellationToken)
    {
        var checkedPem = EnrolmentPolicy.Check(pem);
        if (!checkedPem.Ok)
        {
            logger.LogWarning("The own certificate of user {Email} is not carried into the certificate registry: {Reason} (#1839)",
                email, checkedPem.Refusal);
            return CarryOutcome.NotCarried;
        }

        using var leaf = checkedPem.Leaf!;
        var thumbprint = Convert.ToHexString(SHA256.HashData(leaf.RawData));
        if (await EverFiledAsync(thumbprint, cancellationToken))
        {
            return CarryOutcome.NotCarried;
        }

        // Possession (ADR 1017): a user's own certificate is the user's own claim, and without a proof of possession only
        // an administrator's enrolment says whose key it is.
        if (policy.EffectiveBinding == CertificateBinding.Possession)
        {
            logger.LogWarning("The own certificate of user {Email} is not carried into the certificate registry: the tenant "
                + "binds keys by possession, so an administrator enrols it (ADR 1017).", email);
            return CarryOutcome.NotCarried;
        }

        if (PolicyEnforcement.Refusal(policy, leaf, email, pem, await IdentityAliasesAsync(userId, cancellationToken)) is { } refusal)
        {
            logger.LogWarning("The own certificate of user {Email} is not carried into the certificate registry: the tenant's "
                + "certificate policy refuses it ({Reason}) (#1839).", email, refusal);
            return CarryOutcome.NotCarried;
        }

        if (limit is { } most
            && await dbContext.EnrolledCertificates.CountAsync(c => c.UserId == userId && c.RevokedAt == null, cancellationToken) >= most)
        {
            logger.LogWarning("The own certificate of user {Email} is not carried into the certificate registry: they already hold "
                + "{Limit} enrolled certificates (#1970).", email, most);
            return CarryOutcome.NotCarried;
        }

        try
        {
            await FileEnrolmentAsync(pem, leaf, thumbprint, label: null, email, (userId, null),
                $"Holder {email}, thumbprint {thumbprint}; carried over from the user's own certificate (#1839)", cancellationToken);
            return CarryOutcome.Carried;
        }
        catch (CertificateFolderNotConfiguredException)
        {
            return CarryOutcome.FolderMissing;
        }
    }

    // Whether this certificate now addresses its holder through the registry: enrolled to them, current, and accepted by
    // the tenant's attestation rules.
    private async Task<bool> AddressesAsync(Guid userId, string pem, CancellationToken cancellationToken)
    {
        string? thumbprint;
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            thumbprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }

        var addressing = await CertificateRegistry.ForReaderAsync(dbContext, userId, DateTimeOffset.UtcNow, cancellationToken);
        return addressing.Any(c => SameThumbprint(c.CertificatePem, thumbprint));
    }

    private static bool SameThumbprint(string pem, string thumbprint)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            return Convert.ToHexString(SHA256.HashData(certificate.RawData)) == thumbprint;
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    // Filed before, by thumbprint: in the registry, or as any Certificate document's Thumbprint field — a deleted one too,
    // whose field values stay with it.
    private async Task<bool> EverFiledAsync(string thumbprint, CancellationToken cancellationToken) =>
        await dbContext.EnrolledCertificates.AnyAsync(c => c.Thumbprint == thumbprint, cancellationToken)
        || await dbContext.FieldValues.AnyAsync(v => v.Value == thumbprint
            && dbContext.FieldDefinitions.Any(f => f.Id == v.FieldDefinitionId && f.Name == "Thumbprint"), cancellationToken);

    /// <summary>The tenant's certificate policy; the unset policy when none was saved.</summary>
    public async Task<CertificatePolicy> CurrentPolicyAsync(CancellationToken cancellationToken) =>
        (await CurrentPolicyWithTagAsync(cancellationToken)).Policy;

    /// <summary>
    /// The policy and its tag: the id of the policy document's current version, or <see cref="Guid.Empty"/> before one
    /// was saved. Each save is a new version, so the tag moves with every change, which is what <c>If-Match</c> compares.
    /// </summary>
    public async Task<(CertificatePolicy Policy, Guid Tag)> CurrentPolicyWithTagAsync(CancellationToken cancellationToken)
    {
        if (await PolicyDocumentAsync(cancellationToken) is not { } document
            || await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken) is not { } version)
        {
            return (CertificatePolicy.Unset, Guid.Empty);
        }

        // Read here and parsed; the bytes never leave the process (PlaintextDoorRatchetTests' internal readers).
        await using var stream = await storage.GetObjectAsync(version.ObjectKey, cancellationToken);
        using var reader = new StreamReader(stream);
        return (CertificatePolicy.FromJson(await reader.ReadToEndAsync(cancellationToken)), version.Id);
    }

    /// <summary>
    /// Saves the policy as a new version of the tenant's policy document, creating it on first use, provided it is still
    /// the one the caller read (<paramref name="ifMatch"/>, the tag of <see cref="CurrentPolicyWithTagAsync"/>). Returns
    /// the new tag.
    /// </summary>
    public async Task<Guid> SavePolicyAsync(CertificatePolicy policy, Guid ifMatch, CancellationToken cancellationToken)
    {
        if (!await CallerAdministersAsync(cancellationToken))
        {
            throw new CertificatePolicyNotYoursToSetException();
        }

        if (policy.Problems() is { Count: > 0 } problems)
        {
            throw new CertificatePolicyRefusedException(
                $"This policy would not work as written, so it was not saved: {string.Join(" ", problems)}");
        }

        var bytes = Encoding.UTF8.GetBytes(policy.ToJson());
        Document document;
        var owned = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        await using (owned)
        {
            // THE LOCK, before the comparison: a second save running beside this one waits here, then reads the tag this
            // one committed and is refused. TOKEN DELIBERATELY NOT MOVED: nothing changes, the statement exists only for
            // the row lock it takes (ManualFiler's idiom).
            await dbContext.Tenants
                .Where(t => t.Id == TenantId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.StandardRepositoryId, t => t.StandardRepositoryId), cancellationToken);
            if ((await CurrentPolicyWithTagAsync(cancellationToken)).Tag != ifMatch)
            {
                throw Errors.Exceptions.Concurrency.EtagMismatchException.ForCertificatePolicy();
            }

            if (await PolicyDocumentAsync(cancellationToken) is { } existing)
            {
                document = existing;
                await AddVersionAsync(document, bytes, ".json", "application/json", access.GetCallerIdentity(), cancellationToken);
            }
            else
            {
                var folder = await ReaderCertificateFolder.FindOrCreateAsync(dbContext, TenantId, access.GetCallerIdentity(), cancellationToken)
                    ?? throw new CertificateFolderNotConfiguredException();
                document = await FileAsync(folder.Id, WellKnownMaskIds.CertificatePolicy, "Certificate policy", bytes, ".json", "application/json",
                    access.GetCallerIdentity(), cancellationToken);
            }

            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }

        await audit.RecordAsync(AuditActions.CertificatePolicyChanged, "Document", document.Id, document.Name, cancellationToken: cancellationToken);
        return (await CurrentPolicyWithTagAsync(cancellationToken)).Tag;
    }

    private Task<Document?> PolicyDocumentAsync(CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.CertificatePolicy))
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Document> FileAsync(
        Guid folderId, Guid maskId, string name, byte[] bytes, string extension, string contentType,
        (Guid? UserId, Guid? ServiceAccountId) creator, CancellationToken cancellationToken)
    {
        var (userId, serviceAccountId) = creator;
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            ParentId = folderId,
            Name = name,
            MaskVersionId = await dbContext.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).SingleAsync(cancellationToken),
            CreatedByUserId = userId,
            CreatedByServiceAccountId = userId is null ? serviceAccountId : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AddVersionAsync(document, bytes, extension, contentType, creator, cancellationToken);
        return document;
    }

    private async Task AddVersionAsync(
        Document document, byte[] bytes, string extension, string contentType, (Guid? UserId, Guid? ServiceAccountId) creator,
        CancellationToken cancellationToken)
    {
        var (userId, serviceAccountId) = creator;
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, document.CreatedAt, document.StorageFolderId, versionId, extension);

        // Through the seam, so at-rest encryption applies exactly as it does to an upload.
        using (var content = new MemoryStream(bytes, writable: false))
        {
            await storage.PutObjectAsync(objectKey, content, contentType, cancellationToken);
        }

        // PENDING and confirmed by the finalizer, never a hand-written Confirmed row: it hashes the stored bytes, numbers
        // the version and joins our transaction.
        await finalizer.FileAsync(new DocumentVersion
        {
            Id = versionId,
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = userId is null ? serviceAccountId : null,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        if (document.CurrentVersionId is not null)
        {
            document.CurrentVersionId = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SetFieldsAsync(Document document, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var definitions = await dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == document.MaskVersionId && values.Keys.Contains(f.Name))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);
        foreach (var definition in definitions)
        {
            dbContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = definition.Id,
                Value = values[definition.Name],
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // A certificate's validity is a pair of UTC instants, stored with their offset (the Valid from/until DateTime fields).
    private static string Instant(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}
