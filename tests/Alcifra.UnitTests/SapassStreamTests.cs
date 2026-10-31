using Alcifra.Sapass;

namespace Alcifra.UnitTests;

// apass's stdout carries DATA — a password, a path, a shell export, a completion script — so a failure must never
// write there, and a success must not be the only thing that does. The session is cleared so a developer's own
// shell cannot satisfy a case by accident.
//
// In the "console" collection with alcifra's stream tests: Console.SetOut is process-global, and two classes
// redirecting it at once would read each other's output.
[Collection("console")]
public class SapassStreamTests
{
    [Fact]
    public void No_failure_path_writes_to_stdout()
    {
        var (exit, stdout, stderr) = Run(withoutSession: true, "ls");

        Assert.NotEqual(0, exit);
        Assert.True(string.IsNullOrWhiteSpace(stdout),
            $"apass wrote to STDOUT on a failure path; stdout is where a script captures data:\n{stdout}");
        Assert.False(string.IsNullOrWhiteSpace(stderr), "apass failed without telling the reader anything on stderr.");
    }

    [Fact]
    public void Completion_candidates_are_data_on_stdout_and_need_no_session()
    {
        var (exit, stdout, stderr) = Run(withoutSession: true, SapassApp.CompleteVerb, "");

        Assert.Equal(0, exit);
        Assert.Contains("show", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr), $"completion wrote to STDERR, which lands in the user's prompt:\n{stderr}");
    }

    [Fact]
    public void The_completion_script_is_data_on_stdout()
    {
        var (exit, stdout, stderr) = Run(withoutSession: true, "completion", "bash");

        Assert.Equal(0, exit);
        Assert.Contains("complete -o default", stdout);
        Assert.True(string.IsNullOrWhiteSpace(stderr));
    }

    [Fact]
    public void Login_is_constructed_by_the_app_itself()
    {
        // #2042: the owner's `apass login` failed with "Could not resolve type" before it did anything, because the app
        // had no registrar. Through SapassApp.Run, so this proves the APP wires the registrar, not only that the
        // registrar works. A closed local port: the device request fails at once and nothing leaves the machine.
        var (exit, _, stderr) = Run(withoutSession: true, "login", "--url", "http://127.0.0.1:9");

        Assert.NotEqual(0, exit);
        Assert.DoesNotContain("Could not resolve type", stderr);
    }

    /// <summary>Runs the tool with both streams captured, restoring the console and the environment afterwards.</summary>
    private static (int Exit, string Stdout, string Stderr) Run(bool withoutSession, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (outBefore, errBefore) = (Console.Out, Console.Error);
        var saved = new[] { "ALCIFRA_URL", "ALCIFRA_TOKEN", "SA_URL", "SA_TOKEN", "SACONSOLE_URL", "SACONSOLE_TOKEN" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            if (withoutSession)
            {
                foreach (var name in saved.Keys)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }
            }

            var exit = SapassApp.Run(args);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(outBefore);
            Console.SetError(errBefore);
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
