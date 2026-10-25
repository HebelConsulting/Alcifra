using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.PasswordStores;

namespace SimplArchive.Api.PasswordStores;

/// <summary>
/// Why a caller who may create sub-items in a password store still may not create an ENTRY there (ADR 0982): the values
/// of <see cref="PasswordFolderResource.NewEntryUnavailableReason"/>, and the rule that picks one.
/// </summary>
/// <remarks>
/// <para>
/// An entry's content is sealed to the store's readers' attested certificates (ADR 0964), so the offer is honest only
/// when what is saved can be read back: a PERSON must be one of those readers and hold a certificate. A SERVICE ACCOUNT
/// holds no key and never reads (saconsole's seeding, ADR 0966); it seals to the readers, so its offer is honest as long
/// as some reader holds a certificate.
/// </para>
/// <para>
/// Three reasons and not a boolean per case, because a client says something different for each: enrol a key, grant
/// yourself read, or (an automation) tell the readers to enrol.
/// </para>
/// </remarks>
public static class NewEntryUnavailableReasons
{
    /// <summary>A person who reads the store, but holds no attested certificate an entry could be sealed to: enrol a key.</summary>
    public const string EnrolmentRequired = "enrolment-required";

    /// <summary>
    /// A person who may create here without being one of the store's readers (typically a tenant administrator through
    /// the bypass, which makes nobody a reader, ADR 0964): nothing they saved would be sealed to them.
    /// </summary>
    public const string NotAReader = "not-a-reader";

    /// <summary>A service account, in a store none of whose readers holds a certificate: nothing could be sealed at all.</summary>
    public const string NoReaderCertificates = "no-reader-certificates";

    /// <summary>The reason entries may not be created, or null when they may (rights are the caller's own business).</summary>
    /// <param name="userId">The calling person, or null.</param>
    /// <param name="serviceAccountId">The calling service account, or null.</param>
    /// <param name="readers">The store's readers with their certificates (<see cref="PasswordStoreReaders"/>).</param>
    public static string? Of(Guid? userId, Guid? serviceAccountId, IReadOnlyList<PasswordStoreReader> readers) =>
        (userId, serviceAccountId) switch
        {
            (_, not null) => readers.Any(r => r.CertificatePems.Count > 0) ? null : NoReaderCertificates,
            ({ } user, _) => readers.FirstOrDefault(r => r.UserId == user) switch
            {
                null => NotAReader,
                { CertificatePems.Count: 0 } => EnrolmentRequired,
                _ => null,
            },
            _ => NotAReader,
        };
}

/// <summary>
/// Asks, for the caller, whether a new password entry could be sealed so that it is honest to offer one (ADR 0982): the
/// folder resource withholds the <c>password-entries</c> rel by it, and the create refuses by it before writing anything.
/// </summary>
public sealed class PasswordEntryCreation(
    DocumentAccessService access,
    PasswordStoreReaders readers,
    ILogger<PasswordEntryCreation> logger)
{
    /// <summary>Why the caller may not create entries in <paramref name="storeId"/>, or null when they may.</summary>
    public async Task<string?> UnavailableReasonAsync(Guid storeId, CancellationToken cancellationToken)
    {
        var (userId, serviceAccountId) = access.GetCallerIdentity();
        return NewEntryUnavailableReasons.Of(userId, serviceAccountId, await readers.ForStoreAsync(storeId, cancellationToken));
    }

    /// <summary>Refuses, before anything is written, an entry the caller could not have sealed honestly.</summary>
    /// <exception cref="PasswordStoreException">One of the three refusals, by <see cref="NewEntryUnavailableReasons"/>.</exception>
    public async Task RequireAsync(Guid storeId, CancellationToken cancellationToken)
    {
        if (await UnavailableReasonAsync(storeId, cancellationToken) is not { } reason)
        {
            return;
        }

        logger.LogDebug("Refused a new entry in password store {StoreId}: {Reason}", storeId, reason);
        throw reason switch
        {
            NewEntryUnavailableReasons.EnrolmentRequired => new PasswordEntryRequiresEnrolmentException(),
            NewEntryUnavailableReasons.NotAReader => new PasswordEntryRequiresReaderException(),
            _ => new PasswordStoreHasNoReaderCertificatesException(),
        };
    }
}
