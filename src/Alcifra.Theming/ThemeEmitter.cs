using System.Text;

namespace Alcifra.Theming;

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
    public static string ToCss(ThemeTokens tokens, BrandFonts fonts)
    {
        var css = new StringBuilder();
        css.Append("/* Built from brand/brand.json and any installation override (ADR 0578). */\n\n");
        AppendFontFaces(css, fonts.Ui);

        // The screens' typeface, with the stack the web used before the brand named one (#844) as its fallback.
        css.Append(":root {\n  --sa-font-ui: \"").Append(fonts.Ui)
           .Append("\", \"Helvetica Neue\", Helvetica, Arial, sans-serif;\n}\n\n");

        AppendCssBlock(css, ":root", tokens.Light);
        css.Append('\n');
        css.Append("@media (prefers-color-scheme: dark) {\n");
        AppendCssBlock(css, "  :root", tokens.Dark, indent: "  ");
        css.Append("}\n\n");
        AppendCssBlock(css, ":root[data-theme=\"dark\"]", tokens.Dark);
        css.Append('\n');
        AppendCssBlock(css, ":root[data-theme=\"light\"]", tokens.Light);
        css.Append('\n');
        AppendWordmarkRules(css);
        return css.ToString();
    }

    // The web's files are the family's two variable fonts (upright and italic), served by the API under /brand, so
    // every weight the pages ask for is one file and no visitor's address goes to a font host (#844).
    private static void AppendFontFaces(StringBuilder css, string family)
    {
        var stem = BrandFonts.FileStem(family);
        foreach (var (suffix, style) in new[] { (string.Empty, "normal"), ("-Italic", "italic") })
        {
            css.Append("@font-face {\n")
               .Append("  font-family: \"").Append(family).Append("\";\n")
               .Append("  src: url(\"").Append(BrandWordmark.UrlRoot).Append("fonts/").Append(stem).Append("Variable").Append(suffix).Append(".woff2\") format(\"woff2\");\n")
               .Append("  font-weight: 100 900;\n")
               .Append("  font-style: ").Append(style).Append(";\n")
               .Append("  font-display: swap;\n")
               .Append("}\n\n");
        }
    }

    // The wordmark comes in two files (ADR 0992), and a page carries both: these rules show the one that matches
    // the theme in the SAME four states as the colours above, so the mark can never disagree with the page.
    // The explicit choice wins in both directions because :root[data-theme] outranks the media query's bare class.
    private static void AppendWordmarkRules(StringBuilder css)
    {
        css.Append(".sa-wordmark-dark { display: none; }\n");
        css.Append("@media (prefers-color-scheme: dark) {\n");
        css.Append("  .sa-wordmark-light { display: none; }\n");
        css.Append("  .sa-wordmark-dark { display: inline-block; }\n");
        css.Append("}\n");
        css.Append(":root[data-theme=\"dark\"] .sa-wordmark-light { display: none; }\n");
        css.Append(":root[data-theme=\"dark\"] .sa-wordmark-dark { display: inline-block; }\n");
        css.Append(":root[data-theme=\"light\"] .sa-wordmark-light { display: inline-block; }\n");
        css.Append(":root[data-theme=\"light\"] .sa-wordmark-dark { display: none; }\n");
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
