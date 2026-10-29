using System.Globalization;
using Alcifra.Localization;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>What a notice offers to do about itself.</summary>
public enum PasswordNoticeAction
{
    None,

    /// <summary>Re-seal the store: offered only to a member the server sends the <c>reseal</c> rel to.</summary>
    Reseal,

    /// <summary>Enrol a spare key (the key enrolment page).</summary>
    EnrolSpareKey,

    /// <summary>Enrol a first key, without which no new entry can be sealed to the user (ADR 0982; the key enrolment page).</summary>
    EnrolKey,
}

/// <summary>A sentence shown above a store's contents or an entry, and what it offers.</summary>
/// <param name="Urgent">Drawn as a warning: a revoked key could read it, or a secret should be changed now.</param>
public sealed record PasswordNotice(string Text, bool Urgent, PasswordNoticeAction Action = PasswordNoticeAction.None);

/// <summary>
/// The store's and the entry's notices (Mobile ADR 0037, ADR 0964; both native clients since ADR 0968), once for the phone, the tablet and the desktop: re-seal (urgent after
/// a revocation), the spare key for My Passwords, a store nothing can be sealed to yet, and an entry's "Exposed since".
/// </summary>
public static class PasswordNotices
{
    public static IReadOnlyList<PasswordNotice> For(PasswordFolderInfo folder)
    {
        var notices = new List<PasswordNotice>();
        var reseal = folder.CanReseal ? PasswordNoticeAction.Reseal : PasswordNoticeAction.None;
        if (folder.UrgentReseal)
        {
            notices.Add(new PasswordNotice(Strings.Get("PwUrgentReseal"), Urgent: true, reseal));
        }
        else if (folder.NeedsReseal)
        {
            notices.Add(new PasswordNotice(Strings.Get("PwNeedsReseal"), Urgent: false, reseal));
        }

        if (folder.RecommendsSpareKey)
        {
            notices.Add(new PasswordNotice(Strings.Get("PwSpareKey"), Urgent: false, PasswordNoticeAction.EnrolSpareKey));
        }

        if (WhyNoNewEntry(folder) is { } why)
        {
            notices.Add(new PasswordNotice(why, Urgent: false,
                folder.NewEntryUnavailable == NewEntryUnavailability.EnrolmentRequired ? PasswordNoticeAction.EnrolKey : PasswordNoticeAction.None));
        }

        return notices;
    }

    /// <summary>
    /// Why New entry is unavailable here, in the user's language, or null when it is available or the user may not create
    /// here at all (ADR 0982): the server withholds the rel and says why; every client shows the same sentence.
    /// </summary>
    public static string? WhyNoNewEntry(PasswordFolderInfo folder) => WhyNoNewEntry(folder.NewEntryUnavailable);

    /// <summary>The sentence for one reason (a refusal carries it too); null for <see cref="NewEntryUnavailability.None"/>.</summary>
    public static string? WhyNoNewEntry(NewEntryUnavailability reason) => reason switch
    {
        NewEntryUnavailability.EnrolmentRequired => Strings.Get("PwEnrolmentRequired"),
        NewEntryUnavailability.NotAReader => Strings.Get("PwNotAReader"),
        NewEntryUnavailability.NoReaderCertificates => Strings.Get("PwNoRecipients"),
        NewEntryUnavailability.Unknown => Strings.Get("PwNewEntryUnavailable"),
        _ => null,
    };

    /// <summary>"Exposed since …: change this password", or nothing.</summary>
    public static PasswordNotice? For(PasswordEntryInfo entry) => entry.ExposedSince is { } day
        ? new PasswordNotice(string.Format(CultureInfo.CurrentCulture, Strings.Get("PwExposedSince"), day.ToString("d", CultureInfo.CurrentCulture)), Urgent: true)
        : null;

    /// <summary>What a re-seal did, in one sentence, and the entries this key could not open by name.</summary>
    public static string Describe(PasswordResealOutcome outcome)
    {
        var done = string.Format(CultureInfo.CurrentCulture, Strings.Get("PwResealDone"), outcome.Resealed.Count, outcome.SkippedChanged.Count, outcome.Failed.Count);
        return outcome.Failed.Count == 0
            ? done
            : $"{done}\n{string.Format(CultureInfo.CurrentCulture, Strings.Get("PwResealFailed"), string.Join(", ", outcome.Failed.Select(f => f.Name)))}";
    }

    /// <summary>What an audience change did (ADR 0983): who it is encrypted for now, the re-seal, and who holds no key yet.</summary>
    public static string DescribeAudience(ContentAudienceInfo saved, PasswordResealOutcome? resealed)
    {
        var lines = new List<string> { string.Format(CultureInfo.CurrentCulture, Strings.Get("AudSavedCount"), saved.Recipients.Count) };
        if (resealed is not null)
        {
            lines.Add(Describe(resealed));
        }

        if (saved.WithoutCertificate.Count > 0)
        {
            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.Get("AudWithoutKey"), string.Join(", ", saved.WithoutCertificate.Select(r => r.DisplayName))));
        }

        return string.Join("\n", lines);
    }
}
