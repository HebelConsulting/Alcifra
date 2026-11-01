using System.Text.RegularExpressions;

namespace Alcifra.UnitTests;

// The standing principle "output goes through a logger, never the console" (ADR 0906), as a ratchet.
//
// WHAT IT COUNTS. Every line that writes to the console directly: System.Console's Write/WriteLine (on Out and
// Error too), and Spectre's AnsiConsole and IAnsiConsole Write/Markup calls. Comments are skipped. It scans what
// ships or runs as a tool: src/, tools/ and the two published test tools (SelfHosting, ManualCapture).
//
// WHAT IS ALLOWED TO STAY. One kind of site only: a command-line tool's DATA output, the RESULT it prints for a
// script to capture (`eval "$(alcifra login …)"`, `id=$(alcifra repository create …)`). A log line's timestamp
// and level would break the capture, so that write stays plain stdout. Everything else (progress, diagnostics,
// the human half of a CLI, the desktop's headless hooks) goes through ILogger with a console provider.
//
// THE NUMBERS BELOW ARE THE DEBT at the moment the principle was adopted (2026-10-08: 371 lines in 42 files;
// Api, Infrastructure and Worker were already clean). They only go DOWN: convert a file, lower its number in the
// same commit, and delete the entry at zero. A count that went UP is new console output; write it through a
// logger instead. A genuine data-output site keeps its line and says so beside the call.
public class ConsoleOutputRatchetTests
{
    private static readonly Regex ConsoleWrite = new(
        @"\bConsole\s*\.\s*(?:Out\s*\.\s*|Error\s*\.\s*)?Write(?:Line)?\s*\("
        + @"|\bAnsiConsole\s*\.\s*(?:Write(?:Line)?|Markup(?:Line)?(?:Interpolated)?)\s*\("
        + @"|\.\s*Markup(?:Line)?(?:Interpolated)?\s*\(",
        RegexOptions.Compiled);

    private static readonly string[] Roots = ["src", "tools", "tests/Alcifra.SelfHosting", "tests/Alcifra.ManualCapture"];

    private static readonly Dictionary<string, int> Remaining = new()
    {
        ["src/Alcifra.Cli/Commands/DeviceCommands.cs"] = 1,   // data output only (ADR 0906): an enrolled device's password, once (#1970)
        ["src/Alcifra.Cli/Commands/LoginCommand.cs"] = 2,   // data output only (ADR 0906)
        ["src/Alcifra.Cli/Commands/ModuleUploadCommand.cs"] = 1,   // data output only (ADR 0906): the module's filing answer
        ["src/Alcifra.Cli/Commands/PasswordCommands.cs"] = 3,   // data output only (ADR 0906): the store's id, its entry names, the entry's id
        ["src/Alcifra.Cli/Commands/PasswordStoreAudienceCommands.cs"] = 1,   // data output only: the audience, one member per line (ADR 0983)
        ["src/Alcifra.Cli/Commands/RecoveryCertificateCommands.cs"] = 1,   // data output only (ADR 0906): `show --pem`
        ["src/Alcifra.Cli/Commands/RepositoryCreateCommand.cs"] = 1,   // data output only (ADR 0906)
        ["src/Alcifra.Cli/Commands/StandardRepositoryCommands.cs"] = 1,   // data output: the id (#1661; was an uncounted console.WriteLine)
        ["src/Alcifra.Cli/Commands/TenantCreateCommand.cs"] = 1,   // data output only (ADR 0906)
        ["src/Alcifra.Cli/Commands/UserCreateCommand.cs"] = 1,   // data output only (ADR 0906): the user's id
        // The one direct write left in the desktop client: DesktopLog's own fallback when logging cannot start.
        ["src/Alcifra.DesktopClient/Services/DesktopLog.cs"] = 1,
        // apass (ADR 0964, 2026-10-25): a pass-like CLI, so its DATA output is stdout by contract — what a script
        // captures (`apass show x | head -1`, `eval "$(apass login …)"`) and what completion reads.
        ["src/Alcifra.Sapass/Commands/LoginCommand.cs"] = 2,   // data output only: the two shell exports
        ["src/Alcifra.Sapass/Commands/LsCommand.cs"] = 1,   // data output only: the folder's names
        ["src/Alcifra.Sapass/Commands/AudienceCommand.cs"] = 1,   // data output only: the audience, one member per line (ADR 0983)
        ["src/Alcifra.Sapass/Commands/FindCommand.cs"] = 1,   // data output only: the matching paths
        ["src/Alcifra.Sapass/Commands/ShowCommand.cs"] = 2,   // data output only: the password, then the fields
        ["src/Alcifra.Sapass/Commands/OtpCommand.cs"] = 1,   // data output only: the current verification code (ADR 1013)
        ["src/Alcifra.Sapass/Commands/GenerateCommand.cs"] = 1,   // data output only: the secret it generated
        ["src/Alcifra.Sapass/Commands/CompletionCommand.cs"] = 1,   // data output only: the completion script
        ["src/Alcifra.Sapass/Infrastructure/Completion.cs"] = 1,   // data output only: candidates a shell captures
    };

    /// <summary>
    /// The allowances: src/ and the published test tools from the table above, plus tools/ from
    /// <c>tools/console-output-ratchet.tsv</c>. That file lives under tools/ because tools/ is withheld from the public
    /// mirror, and this test file is published (some tool names may not appear in a published file).
    /// </summary>
    private static Dictionary<string, int> Allowances(string root)
    {
        var all = new Dictionary<string, int>(Remaining, StringComparer.Ordinal);
        var tsv = Path.Combine(root, "tools", "console-output-ratchet.tsv");
        if (PrivateRepositoryGate.IsPrivateRepository(root))
        {
            Assert.True(File.Exists(tsv), $"{tsv} is missing: the tools/ half of this ratchet lives there.");
            foreach (var line in File.ReadLines(tsv).Where(l => l.Length > 0 && l[0] != '#'))
            {
                var parts = line.Split('\t');
                all[parts[1]] = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return all;
    }

    [Fact]
    public void No_file_writes_to_the_console_more_than_it_did_and_a_clean_file_stays_clean()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var counts = Count(root);
        var allowed = Allowances(root);
        var grew = counts
            .Where(c => c.Value > allowed.GetValueOrDefault(c.Key))
            .Select(c => $"  {c.Key}: {c.Value} (allowed {allowed.GetValueOrDefault(c.Key)})")
            .ToList();

        Assert.True(grew.Count == 0,
            "Console output grew (ADR 0906). Write it through an ILogger with a console provider instead. "
            + "Only a command-line tool's DATA output (what a script captures) may write stdout directly:\n"
            + string.Join("\n", grew));
    }

    [Fact]
    public void A_file_that_paid_its_debt_lowers_its_number()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var counts = Count(root);
        var stale = Allowances(root)
            .Where(r => counts.GetValueOrDefault(r.Key) < r.Value)
            .Select(r => $"  {r.Key}: allowed {r.Value}, now {counts.GetValueOrDefault(r.Key)}")
            .ToList();

        Assert.True(stale.Count == 0,
            "These files write to the console less than their allowance. Lower the number (or delete the entry "
            + "at zero) in the same change, so the debt cannot quietly grow back:\n" + string.Join("\n", stale));
    }

    private static Dictionary<string, int> Count(string root)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dir in Roots.Select(r => Path.Combine(root, r)).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
            {
                var n = File.ReadLines(file).Count(line =>
                    !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && ConsoleWrite.IsMatch(line));
                if (n > 0)
                {
                    counts[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = n;
                }
            }
        }

        return counts;
    }
}
