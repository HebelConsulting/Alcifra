namespace SimplArchive.ApiClient.KeyTest;

/// <summary>
/// Collects a <see cref="KeyTestReport"/> as the test runs, and keeps every secret it was told about out of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The report never receives the PIN — and if a failure's own text echoes it, that text is withheld.</b> The PIN
/// is not passed to anything that writes the report; but an error message is free text from a library we do not
/// own, and "never" cannot rest on every library's discretion. So a caller registers each secret it handles with
/// <see cref="NeverShow"/>, and every FREE-TEXT field (a step's detail, an archive-side problem) that
/// contains one is replaced as a whole by <see cref="Withheld"/> — the whole field, not the matching characters,
/// because a masked span would show where the PIN sat inside a string the reader may know.
/// </para>
/// <para>
/// <b>Structured fields are not scanned, deliberately.</b> A serial number, a subject, a thumbprint or an error CODE
/// (built from a return value, an operation name and a type name, never from a message) is a public fact; a short
/// PIN can coincide with part of one by chance, and withholding a serial because it happens to contain the digits of
/// the PIN would itself say something about the PIN. Whitelist what is safe
/// (CLAUDE.md's redaction rule): those fields come from public data only, never from free text.
/// </para>
/// </remarks>
public sealed class KeyTestReportBuilder(TimeProvider? time = null)
{
    /// <summary>What a free-text field carrying a secret is replaced with.</summary>
    public const string Withheld = "(withheld: this text contained a secret)";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<string> _secrets = [];
    private readonly List<KeyTestKey> _keys = [];
    private readonly List<KeyTestStep> _steps = [];
    private KeyTestEnvironment? _environment;
    private KeyTestArchiveSide? _archive;

    /// <summary>The steps so far — a client shows them live while the test runs.</summary>
    public IReadOnlyList<KeyTestStep> Steps => _steps;

    /// <summary>Registers a secret (the PIN) that no free-text field of the report may contain. Empty is ignored.</summary>
    public KeyTestReportBuilder NeverShow(string? secret)
    {
        if (secret is { Length: > 0 })
        {
            _secrets.Add(secret);
        }

        return this;
    }

    /// <summary>Forgets the registered secrets — once the step that handled them is over.</summary>
    /// <remarks>
    /// Fields already recorded were checked when they were added, so forgetting is safe; it keeps the PIN from
    /// living in this object for the window's lifetime.
    /// </remarks>
    public void ForgetSecrets() => _secrets.Clear();

    public KeyTestReportBuilder Environment(KeyTestEnvironment environment)
    {
        _environment = environment;
        return this;
    }

    public KeyTestReportBuilder Key(KeyTestKey key)
    {
        _keys.Add(key);
        return this;
    }

    /// <summary>Records a step, withholding its detail where it contains a registered secret.</summary>
    /// <remarks>
    /// The code is kept as given: it must be STRUCTURED (<c>CKR_PIN_INCORRECT (0x000000A0) in C_Login</c>), and a
    /// six-digit PIN of zeros would otherwise hide exactly the code that says the PIN was wrong.
    /// </remarks>
    public KeyTestStep Step(
        KeyTestStepKind kind, KeyTestOutcome outcome, string detail, string? key = null, string? errorCode = null)
    {
        var step = new KeyTestStep(kind, outcome, Clean(detail), key, errorCode);
        _steps.Add(step);
        return step;
    }

    public KeyTestReportBuilder Archive(KeyTestArchiveSide? archive)
    {
        _archive = archive is null ? null : archive with { Problems = archive.Problems.Select(Clean).ToList() };
        return this;
    }

    public KeyTestReport Build() => new(
        _time.GetUtcNow(),
        _environment ?? KeyTestEnvironment.ThisMachine(SimplArchive.Localization.Strings.ProductName, "unknown", null, []),
        _keys.ToList(),
        _steps.ToList(),
        _archive);

    private string Clean(string text) =>
        _secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)) ? Withheld : text;
}
