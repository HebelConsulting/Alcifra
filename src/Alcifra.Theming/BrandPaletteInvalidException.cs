namespace Alcifra.Theming;

/// <summary>
/// A runtime brand pack's palette cannot be painted (ADR 1103, #2408): a token is missing, or a value is not a
/// <c>#RRGGBB</c> colour. Thrown by <see cref="ThemeTokensReader.FromGroups"/> and caught where a pack is loaded —
/// the pack is then left out with a Warning, because rendering half a brand is worse than the installation's own.
/// </summary>
public sealed class BrandPaletteInvalidException(string message) : FormatException(message);
