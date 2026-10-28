using System.Text;

namespace SimplArchive.Theming;

/// <summary>
/// Turns a palette into the web's idea of a theme: a CSS custom property sheet (ADR 0578), served by the API at
/// <c>/api/theme.css</c>.
/// </summary>
/// <remarks>
/// The desktop builds its brushes from the same <see cref="ThemeVariables"/> in <c>ThemeApplier</c>, and the
/// manual reads its one colour from brand/brand.json directly: nothing generated is committed (ADR 0990). What
/// must not be duplicated is the <em>decision</em> about which variables exist and what they are worth, and
/// that lives in <see cref="ThemeVariables"/>.
/// </remarks>
public static class ThemeEmitter
{
    /// <summary>The web client's custom properties, in all four states the viewer's theme can be in.</summary>
    /// <remarks>
    /// The media query carries the operating system's preference and the <c>data-theme</c> rules carry an
    /// explicit choice, so the explicit one has to be able to win in <b>both</b> directions — which is why the
    /// light block is repeated under <c>[data-theme="light"]</c> rather than left to the default.
    /// </remarks>
    public static string ToCss(ThemeTokens tokens)
    {
        var css = new StringBuilder();
        css.Append("/* Built from brand/brand.json and any installation override (ADR 0578). */\n\n");

        AppendCssBlock(css, ":root", tokens.Light);
        css.Append('\n');
        css.Append("@media (prefers-color-scheme: dark) {\n");
        AppendCssBlock(css, "  :root", tokens.Dark, indent: "  ");
        css.Append("}\n\n");
        AppendCssBlock(css, ":root[data-theme=\"dark\"]", tokens.Dark);
        css.Append('\n');
        AppendCssBlock(css, ":root[data-theme=\"light\"]", tokens.Light);
        return css.ToString();
    }

    private static void AppendCssBlock(StringBuilder css, string selector, ThemePalette palette, string indent = "")
    {
        css.Append(selector).Append(" {\n");
        foreach (var variable in ThemeVariables.For(palette))
        {
            css.Append(indent).Append("  ").Append(variable.CssName).Append(": ")
               .Append(ToCssColour(variable.Value)).Append(";\n");
        }

        css.Append(indent).Append("}\n");
    }

    // Avalonia writes alpha first (#AARRGGBB), CSS writes it last (#RRGGBBAA). The one translucent value we
    // emit is the empty-folder icon; everything else passes through untouched.
    private static string ToCssColour(string value) =>
        value.Length == 9 ? $"#{value[3..]}{value[1..3]}" : value;
}
