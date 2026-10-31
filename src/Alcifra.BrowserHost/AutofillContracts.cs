namespace Alcifra.BrowserHost;

/// <summary>One entry an autofill sheet offers: its name and user name — never the secret, which is only read when
/// the user picks one. <paramref name="HasCode"/> is the index's word that it carries a verification code (ADR 1013),
/// so a one-time-code field is offered only the entries that can fill it.</summary>
public sealed record AutofillMatch(Guid Id, string Name, string Path, string? UserName, bool HasCode = false);

/// <summary>What the browser fills once the user has chosen an entry. <paramref name="Code"/> is the verification code
/// valid NOW (ADR 1013), computed on this machine by the shared rule; the setup behind it never leaves the host, so it
/// is not among the <paramref name="Fields"/>.</summary>
public sealed record AutofillCredentials(string Password, string? UserName, IReadOnlyList<AutofillField> Fields, string? Code = null);

/// <summary>One field of an opened entry, for a form that asks for more than a user name.</summary>
public sealed record AutofillField(string Name, string Value);

/// <summary>A submitted login the browser offers to keep; <see cref="EntryId"/> names the entry it updates.</summary>
public sealed record AutofillSave(Guid? EntryId, string Site, string UserName, string Password);

/// <summary>What was filed: enough for the extension to say so, and nothing secret.</summary>
public sealed record AutofillSaved(Guid Id, string Name, string Path);

/// <summary>
/// What the host needs from a password store. Implemented by apass today and by the desktop when it gains a host
/// mode; the protocol above does not know which client is answering.
/// </summary>
public interface IAutofillStore
{
    /// <summary>The entries whose indexed site (or app) matches, WITHOUT opening any of them (ADR 0973).</summary>
    Task<IReadOnlyList<AutofillMatch>> MatchAsync(string site, string? app, CancellationToken cancellationToken);

    /// <summary>The chosen entry's credentials, opened with the key; null when it is gone.</summary>
    Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken);

    /// <summary>Creates or updates the entry a submitted form describes.</summary>
    Task<AutofillSaved> SaveAsync(AutofillSave save, CancellationToken cancellationToken);

    /// <summary>A fresh password, which is only stored once the browser saves it.</summary>
    string Generate(int length);
}
