namespace Alcifra.Api.Documents;

/// <summary>
/// What a finalize asks about duplicates (core #2476, owner's decision 2026-11-25): <see cref="Requires"/> is sent by a
/// client that understands the question — the arriving content is then hashed against the archive and refused (409) with
/// the candidates unless <see cref="Allow"/> carries the user's explicit "file anyway". Absent both, the finalize stays
/// lenient: every already-shipped client and every machine path (IMAP/LMTP, modules) files exactly as before, which is
/// the compatibility half of the decision — media-type "v2 is API-wide", so the promise is made per request instead.
/// </summary>
public readonly record struct DuplicatePolicy(bool? Requires, bool? Allow);
