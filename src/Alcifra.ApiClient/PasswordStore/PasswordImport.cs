using System.Text;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>One row of an export, ready to be sealed as a new entry: its name in the clear, the rest its content.</summary>
/// <param name="Line">The row's line in the file, counting from one, for a message naming it.</param>
public sealed record PasswordImportDraft(string Name, PasswordEntryContent Content, int Line);

/// <summary>An entry already in the target folder, opened by the caller so its user name is known.</summary>
public sealed record ExistingPasswordEntry(Guid Id, string Name, string? UserName, string Href);

/// <summary>A draft whose name and user name match an entry already there: the user decides, per entry.</summary>
public sealed record PasswordImportConflict(PasswordImportDraft Draft, ExistingPasswordEntry Existing);

/// <summary>What the user chose for one conflict (owner decision 2026-10-23: ask per entry).</summary>
public enum PasswordImportResolution
{
    /// <summary>Leave the existing entry as it is and import nothing for this row.</summary>
    Skip,

    /// <summary>File the imported content as a new version of the existing entry.</summary>
    AddAsNewVersion,

    /// <summary>Import it as a separate entry beside the existing one, under a name of its own.</summary>
    KeepBoth,
}

/// <summary>One step of an import: create an entry, or file a new version of one.</summary>
public sealed record PasswordImportStep(PasswordImportDraft Draft, ExistingPasswordEntry? NewVersionOf);

/// <summary>The rows parsed, and the ones that could not become an entry with why.</summary>
public sealed record PasswordImportResult(IReadOnlyList<PasswordImportDraft> Drafts, IReadOnlyList<(int Line, string Reason)> Skipped);

/// <summary>
/// Import from another password manager (ADR 0964, owner decision 2026-10-23), CLIENT SIDE ONLY: the export is read on
/// the device and each row becomes an entry sealed there, through the ordinary create, so the plaintext never reaches
/// the server. Google Password Manager's CSV first (<c>name,url,username,password,note</c>); other formats follow as
/// further readers producing the same drafts. A verification code's setup (ADR 1013) is read from a column named as the
/// managers that export one name it (<see cref="CodeColumns"/>), so a CSV of the same shape that carries one keeps it.
/// </summary>
public static class PasswordImport
{
    /// <summary>
    /// The header names a verification code's setup arrives under (ADR 1013), compared without case: <c>totp</c> (a
    /// name/url/username/password export that carries one — the shape several managers share), <c>login_totp</c>,
    /// <c>otpauth</c>, <c>otp</c> and <c>otpsecret</c>. The value is an <c>otpauth://totp/…</c> link or a bare setup key.
    /// </summary>
    public static readonly IReadOnlyList<string> CodeColumns = ["totp", "login_totp", "otpauth", "otp", "otpsecret"];

    /// <summary>The field a code that does not read is kept under: an ordinary secret, so nothing of the export is lost
    /// and the entry does not claim a code it cannot show.</summary>
    public const string UnreadableCodeFieldName = "Verification code (unreadable)";

    /// <summary>Reads a Google Password Manager CSV export: RFC 4180 quoting, multi-line notes, a byte-order mark, CRLF or LF.</summary>
    /// <remarks>
    /// Columns are found by their header, in any order; <c>note</c> may be missing (older exports). A row with neither a
    /// password nor a name is skipped and said. Names repeated inside the file (one site, several accounts) are made
    /// distinct, because two entries in one folder cannot share a name.
    /// </remarks>
    public static PasswordImportResult ReadGoogleCsv(string csv)
    {
        var rows = Csv(csv.TrimStart('﻿'));
        if (rows.Count == 0)
        {
            return new PasswordImportResult([], []);
        }

        var header = rows[0].Fields.Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Column(string name) => header.IndexOf(name);
        var (nameAt, urlAt, userAt, passwordAt, noteAt) = (Column("name"), Column("url"), Column("username"), Column("password"), Column("note"));
        var codeAt = CodeColumns.Select(Column).FirstOrDefault(i => i >= 0, -1);
        if (passwordAt < 0)
        {
            return new PasswordImportResult([], [(1, "the file has no 'password' column; it is not a password export")]);
        }

        var drafts = new List<PasswordImportDraft>();
        var skipped = new List<(int, string)>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fields, line) in rows.Skip(1))
        {
            string At(int index) => index >= 0 && index < fields.Count ? fields[index] : string.Empty;
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var password = At(passwordAt);
            var url = At(urlAt).Trim();
            var name = At(nameAt).Trim();
            if (name.Length == 0)
            {
                name = Uri.TryCreate(url, UriKind.Absolute, out var site) ? site.Host : string.Empty;
            }

            if (password.Length == 0 && name.Length == 0)
            {
                skipped.Add((line, "the row has neither a name nor a password"));
                continue;
            }

            if (name.Length == 0)
            {
                name = "Imported entry";
            }

            var userName = At(userAt).Trim();
            var unique = UniqueName(name, userName, taken);
            taken.Add(unique);
            var content = PasswordEntryContent.Of(password, userName, url, At(noteAt));
            if (CodeField(At(codeAt), userName.Length > 0 ? userName : name) is { } code)
            {
                content = content with { Fields = [.. content.Fields, code] };
            }

            drafts.Add(new PasswordImportDraft(unique, content, line));
        }

        return new PasswordImportResult(drafts, skipped);
    }

    // A setup that reads becomes the Verification code field, as the standard link (a bare key is turned into one,
    // labelled with the account); one that does not is kept as an ordinary secret under a name of its own.
    private static PasswordEntryField? CodeField(string value, string label)
    {
        var text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!VerificationCodeSetup.TryParse(text, out var setup))
        {
            return new PasswordEntryField(UnreadableCodeFieldName, text, IsSecret: true);
        }

        var link = text.StartsWith("otpauth:", StringComparison.OrdinalIgnoreCase) ? text : setup!.ToUri(label);
        return new PasswordEntryField(PasswordEntryFieldNames.VerificationCode, link, IsSecret: true);
    }

    /// <summary>The drafts whose name and user name match an entry already in the folder (names without case, user
    /// names exactly), each to be asked about.</summary>
    public static IReadOnlyList<PasswordImportConflict> Conflicts(IEnumerable<PasswordImportDraft> drafts, IReadOnlyList<ExistingPasswordEntry> existing) =>
        [
            .. drafts.SelectMany(d => existing
                .Where(e => string.Equals(e.Name, d.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.UserName ?? string.Empty, d.Content.UserName ?? string.Empty, StringComparison.Ordinal))
                .Take(1)
                .Select(e => new PasswordImportConflict(d, e))),
        ];

    /// <summary>
    /// The steps that carry out an import: every draft without a conflict is created; each conflict follows the user's
    /// answer. A draft keeping both, or one whose name is taken by an entry it does NOT conflict with, gets a free name.
    /// </summary>
    public static IReadOnlyList<PasswordImportStep> Plan(
        IReadOnlyList<PasswordImportDraft> drafts,
        IReadOnlyList<ExistingPasswordEntry> existing,
        Func<PasswordImportConflict, PasswordImportResolution> resolve)
    {
        var conflicts = Conflicts(drafts, existing).ToDictionary(c => c.Draft);
        var taken = new HashSet<string>(existing.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
        var steps = new List<PasswordImportStep>();
        foreach (var draft in drafts)
        {
            if (conflicts.TryGetValue(draft, out var conflict))
            {
                switch (resolve(conflict))
                {
                    case PasswordImportResolution.Skip:
                        continue;
                    case PasswordImportResolution.AddAsNewVersion:
                        steps.Add(new PasswordImportStep(draft, conflict.Existing));
                        continue;
                }
            }

            var name = taken.Contains(draft.Name) ? UniqueName(draft.Name, draft.Content.UserName, taken) : draft.Name;
            taken.Add(name);
            steps.Add(new PasswordImportStep(draft with { Name = name }, null));
        }

        return steps;
    }

    /// <summary>A name not in <paramref name="taken"/>: the name, then with the user name, then numbered.</summary>
    public static string UniqueName(string name, string? userName, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        if (!string.IsNullOrWhiteSpace(userName) && !taken.Contains($"{name} ({userName})"))
        {
            return $"{name} ({userName})";
        }

        for (var n = 2; ; n++)
        {
            if (!taken.Contains($"{name} ({n})"))
            {
                return $"{name} ({n})";
            }
        }
    }

    // RFC 4180: fields separated by commas, a field quoted when it holds a comma, a quote or a line break, a quote inside
    // doubled. Line breaks inside quotes belong to the field; outside they end the record (CRLF or LF alike).
    private static List<(List<string> Fields, int Line)> Csv(string text)
    {
        var records = new List<(List<string>, int)>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var line = 1;
        var recordLine = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    if (c != '\r' || i + 1 >= text.Length || text[i + 1] != '\n')
                    {
                        field.Append(c);
                    }
                }

                continue;
            }

            switch (c)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    records.Add((fields, recordLine));
                    fields = [];
                    line++;
                    recordLine = line;
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            records.Add((fields, recordLine));
        }

        return records;
    }
}
