using Alcifra.Cli;

namespace Alcifra.UnitTests;

// alcifra's STDOUT carries shell exports, so nothing else may ever appear there.
//
// `alcifra login` is meant to be used as `eval "$(alcifra login --url …)"`, which means stdout is not a
// place for messages — it is a place for statements the shell will EXECUTE. An error written there is an
// error that gets run.
//
// That is not hypothetical. Running the packaged tool against an unreachable installation printed
// `Unexpected SocketException: Connection refused` on STDOUT, where eval would have executed it — and the
// same handler wrote every other failure there too. It was found by running the binary and reading the two
// streams separately; every build and every test until then was green.
[Collection("console")]
public class SaConsoleStreamTests
{
    [Theory]
    // A wrong command line — Spectre's own parse failure.
    [InlineData("no-such-command")]
    // A missing installation — the settings validation path.
    [InlineData("whoami")]
    // An unreachable installation — the one that actually regressed.
    [InlineData("login", "--url", "http://127.0.0.1:1")]
    public void No_failure_path_writes_anything_to_stdout(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (outBefore, errBefore) = (Console.Out, Console.Error);
        var url = Environment.GetEnvironmentVariable("ALCIFRA_URL");
        var token = Environment.GetEnvironmentVariable("ALCIFRA_TOKEN");

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);

            // Cleared so the "missing installation" case cannot be satisfied by the developer's own shell.
            Environment.SetEnvironmentVariable("ALCIFRA_URL", null);
            Environment.SetEnvironmentVariable("ALCIFRA_TOKEN", null);

            var exit = SaConsoleApp.Run(args);   // Run, not Build: it disposes the logger, so its background writer flushes
            Assert.True(exit != 0, "these arguments are all supposed to fail");
        }
        finally
        {
            Console.SetOut(outBefore);
            Console.SetError(errBefore);
            Environment.SetEnvironmentVariable("ALCIFRA_URL", url);
            Environment.SetEnvironmentVariable("ALCIFRA_TOKEN", token);
        }

        Assert.True(string.IsNullOrWhiteSpace(stdout.ToString()),
            "alcifra wrote to STDOUT on a failure path. STDOUT carries shell exports for "
            + "eval \"$(alcifra login …)\", so anything written there is executed by the shell:\n"
            + stdout);

        // And the reader must still be told something — a silent failure would satisfy the assertion above
        // while being worse than the bug it guards against.
        Assert.False(string.IsNullOrWhiteSpace(stderr.ToString()),
            "alcifra failed without writing anything to STDERR, so the reader is told nothing.");
    }
}
