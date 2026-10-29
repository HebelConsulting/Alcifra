using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Alcifra.BrowserExtensionTests;

/// <summary>
/// The extension's service worker as the DevTools protocol reports it: whether it is running, and a way to stop it as
/// the browser does after about 30 idle seconds.
/// </summary>
/// <remarks>
/// A headless test never sees that idle stop on its own: the automation's attachment to the worker keeps it alive.
/// Stopping it here is how a test puts the extension in the state a real user's browser reaches by waiting.
/// </remarks>
public sealed class ServiceWorkerProbe : IAsyncDisposable
{
    private readonly ICDPSession _cdp;
    private readonly ConcurrentDictionary<string, string> _status = new();
    private readonly string _extensionId;
    private Action? _changed;

    private ServiceWorkerProbe(ICDPSession cdp, string extensionId)
    {
        _cdp = cdp;
        _extensionId = extensionId;
    }

    /// <summary>True while any version of the extension's worker reports <c>running</c>.</summary>
    public bool IsRunning => _status.Values.Any(s => s == "running");

    public static async Task<ServiceWorkerProbe> AttachAsync(IBrowserContext context, IPage page, string extensionId)
    {
        var probe = new ServiceWorkerProbe(await context.NewCDPSessionAsync(page), extensionId);
        probe._cdp.Event("ServiceWorker.workerVersionUpdated").OnEvent += (_, updated) =>
        {
            foreach (var version in updated!.Value.GetProperty("versions").EnumerateArray())
            {
                var url = version.GetProperty("scriptURL").GetString() ?? string.Empty;
                if (url.StartsWith($"chrome-extension://{extensionId}/", StringComparison.Ordinal))
                {
                    probe._status[version.GetProperty("versionId").GetString()!] = version.GetProperty("runningStatus").GetString()!;
                    probe._changed?.Invoke();
                }
            }
        };
        await probe._cdp.SendAsync("ServiceWorker.enable");
        await probe.WaitUntilAsync(() => !probe._status.IsEmpty, TimeSpan.FromSeconds(10), "the extension's worker to be reported");
        return probe;
    }

    /// <summary>Stops the worker and waits until it reports stopped.</summary>
    public async Task StopAsync()
    {
        foreach (var versionId in _status.Keys)
        {
            await _cdp.SendAsync("ServiceWorker.stopWorker", new Dictionary<string, object> { ["versionId"] = versionId });
        }

        await WaitUntilAsync(() => _status.Values.All(s => s == "stopped"), TimeSpan.FromSeconds(10), "the worker to stop");
    }

    /// <summary>Waits until something starts the worker again; throws if nothing does within <paramref name="within"/>.</summary>
    public Task WaitUntilRunningAsync(TimeSpan within) => WaitUntilAsync(() => IsRunning, within, "the worker to run again");

    public async ValueTask DisposeAsync() => await _cdp.DetachAsync();

    private async Task WaitUntilAsync(Func<bool> condition, TimeSpan within, string what)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _changed = () =>
        {
            if (condition())
            {
                reached.TrySetResult();
            }
        };
        try
        {
            if (!condition())
            {
                await reached.Task.WaitAsync(within);
            }
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out after {within} waiting for {what} (extension {_extensionId}).");
        }
        finally
        {
            _changed = null;
        }
    }
}
