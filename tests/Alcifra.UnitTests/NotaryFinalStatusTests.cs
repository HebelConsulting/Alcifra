using System.Diagnostics;

namespace Alcifra.UnitTests;

// The release's notarization decides by the submission's FINAL status (scripts/package-macos-dmg.sh). Its progress lines
// read "Current status: In Progress..." even when the output ends "status: Accepted", and searching the whole output for
// "In Progress" took two accepted submissions for waits that ran out, so v0.60.0's first packaging run failed. Nothing runs
// this path before a tag, so the function is run here against that run's own output.
public class NotaryFinalStatusTests
{
    private const string AcceptedAfterProgress = """
        Conducting pre-submission checks for Alcifra-0.60.0-arm64.dmg and initiating connection to the Apple notary service...
        Submission ID received
          id: fa5a765d-d746-4f33-a055-5664ec1ff1af
        Successfully uploaded file
          id: fa5a765d-d746-4f33-a055-5664ec1ff1af
          path: /Users/runner/work/Alcifra/Alcifra/dist/Alcifra-0.60.0-arm64.dmg
        Waiting for processing to complete. Wait timeout is set to 1200.0 second(s).

        Current status: In Progress...
        Current status: In Progress....
        Current status: Accepted.........Processing complete
          id: fa5a765d-d746-4f33-a055-5664ec1ff1af
          status: Accepted
        """;

    private const string StillInProgress = """
        Waiting for processing to complete. Wait timeout is set to 1200.0 second(s).
        Current status: In Progress...
        Current status: In Progress....
          id: fa5a765d-d746-4f33-a055-5664ec1ff1af
          status: In Progress
        """;

    private const string Invalid = """
        Current status: Invalid.....Processing complete
          id: fa5a765d-d746-4f33-a055-5664ec1ff1af
          status: Invalid
        """;

    private static string FinalStatusOf(string output)
    {
        var script = Path.Combine(RepoPaths.Root(), "scripts", "package-macos-dmg.sh");
        // Only the function, cut out of the script: the rest of it signs and packages.
        var start = new ProcessStartInfo("bash", ["-c", $"eval \"$(sed -n '/^notary_final_status() {{/,/^}}/p' '{script}')\"; notary_final_status"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        using var bash = Process.Start(start)!;
        bash.StandardInput.Write(output);
        bash.StandardInput.Close();
        var answer = bash.StandardOutput.ReadToEnd().Trim();
        bash.WaitForExit();
        Assert.Equal(0, bash.ExitCode);
        return answer;
    }

    [Fact]
    public void An_accepted_submission_is_accepted_whatever_its_progress_lines_said() =>
        Assert.Equal("Accepted", FinalStatusOf(AcceptedAfterProgress));

    [Fact]
    public void A_wait_that_ran_out_is_still_in_progress() =>
        Assert.Equal("In Progress", FinalStatusOf(StillInProgress));

    [Fact]
    public void A_refusal_is_its_own_status() =>
        Assert.Equal("Invalid", FinalStatusOf(Invalid));
}
