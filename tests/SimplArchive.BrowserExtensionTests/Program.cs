using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.BrowserHost;

namespace SimplArchive.BrowserExtensionTests;

/// <summary>
/// The native-messaging host the browser spawns during the tests: the REAL <see cref="AutofillHost"/> loop over a
/// <see cref="FakeAutofillStore"/>. The test runner never calls this; the launcher a test registers does.
/// </summary>
public static class Program
{
    /// <summary>The verb the launcher passes, followed by the client name and the state folder.</summary>
    public const string HostVerb = "--fake-autofill-host";

    public static async Task<int> Main(string[] args)
    {
        if (args is not [HostVerb, var clientName, var stateFolder])
        {
            return 2;
        }

        // Each browser request spawns its own process, so the store's state lives in files, not in memory.
        var host = new AutofillHost(new FakeAutofillStore(stateFolder, clientName), NullLogger.Instance, clientName);
        await host.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
        return 0;
    }
}
