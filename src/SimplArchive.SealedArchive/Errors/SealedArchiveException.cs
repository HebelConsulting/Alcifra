namespace SimplArchive.SealedArchive.Errors;

/// <summary>
/// The base of every refusal a <c>.simplarchive</c> reader gives (ADR 0961), so a host catches the group and shows
/// the message; each child names one cause, because "tampered", "foreign" and "not for your key" have different fixes.
/// </summary>
public abstract class SealedArchiveException(string message, Exception? inner = null) : Exception(message, inner);
