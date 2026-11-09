using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Alcifra.Presentation;
using Microsoft.Extensions.Logging;

namespace Alcifra.Pass.Infrastructure;

/// <summary>The platform a target names, as <c>apass cicd</c> reads and sets it (ADR 1084).</summary>
public interface ICiCdPlatform
{
    Task<CiCdTargetState> ReadAsync(string target, CancellationToken cancellationToken);

    Task SetSecretAsync(string target, string name, byte[] value, CancellationToken cancellationToken);

    Task SetVariableAsync(string target, string name, string value, CancellationToken cancellationToken);
}

/// <summary>
/// GitHub through its own command-line tool, already signed in on this machine (ADR 1084). A VALUE goes on <c>gh</c>'s
/// standard input and never on its command line, where any user of the machine could read it in the process list; the
/// command line carries names only, and only names are logged.
/// </summary>
internal sealed class GitHubCli(ILogger logger) : ICiCdPlatform
{
    public async Task<CiCdTargetState> ReadAsync(string target, CancellationToken cancellationToken)
    {
        var (repository, environment) = Parse(target);
        using var secrets = JsonDocument.Parse(await RunAsync(["secret", "list", .. Scope(repository, environment), "--json", "name,updatedAt"], null, cancellationToken));
        using var variables = JsonDocument.Parse(await RunAsync(["variable", "list", .. Scope(repository, environment), "--json", "name,value"], null, cancellationToken));
        return new CiCdTargetState(
            secrets.RootElement.EnumerateArray().ToDictionary(s => s.GetProperty("name").GetString()!, s => s.GetProperty("updatedAt").GetDateTimeOffset()),
            variables.RootElement.EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString() ?? string.Empty));
    }

    public async Task SetSecretAsync(string target, string name, byte[] value, CancellationToken cancellationToken)
    {
        var (repository, environment) = Parse(target);
        await RunAsync(["secret", "set", name, .. Scope(repository, environment)], value, cancellationToken);
    }

    public async Task SetVariableAsync(string target, string name, string value, CancellationToken cancellationToken)
    {
        var (repository, environment) = Parse(target);
        await RunAsync(["variable", "set", name, .. Scope(repository, environment)], Encoding.UTF8.GetBytes(value), cancellationToken);
    }

    private static (string Repository, string? Environment) Parse(string target) =>
        CiCdEntryRules.ParseTarget(target) ?? throw new PassException($"'{target}' is not a target (github:owner/name or github:owner/name@environment).");

    private static string[] Scope(string repository, string? environment) =>
        environment is null ? ["--repo", repository] : ["--repo", repository, "--env", environment];

    // Runs gh with the arguments as a list (no shell), the value on stdin when there is one. Its output is the answer.
    private async Task<string> RunAsync(IReadOnlyList<string> arguments, byte[]? stdin, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("gh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        logger.LogDebug("Running gh {Arguments}", string.Join(' ', arguments));
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new PassException("gh could not be started.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new PassException("apass cicd needs the GitHub command-line tool (gh), signed in. It was not found on this machine.");
        }

        using (process)
        {
            if (stdin is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(stdin, cancellationToken);
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0
                ? await output
                : throw new PassException($"gh {arguments[0]} {arguments[1]} failed: {(await error).Trim()}");
        }
    }
}
