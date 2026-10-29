namespace Alcifra.Sapass;

/// <summary>Something the user can act on, written for them to read — the message IS the output.</summary>
/// <remarks>
/// The CLI's counterpart of alcifra's <c>CliException</c>: a specific type, so the top-level handler can tell "the
/// installation said no" from "the command line was wrong" from "the installation could not be reached".
/// </remarks>
public sealed class SapassException(string message) : Exception(message);
