using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using Alcifra.BrowserHost;

namespace Alcifra.BrowserExtensionTests;

/// <summary>
/// Playwright's own Chromium with the extension loaded unpacked into a throwaway profile, and that profile's
/// native-messaging manifests pointing at this assembly's fake host (ADR 0980).
/// </summary>
/// <remarks>
/// <para>
/// <b>Playwright's Chromium, not the installed browser.</b> The branded browser ignores <c>--load-extension</c> from
/// version 137 on, so the system browser the UI suites drive cannot load an unpacked extension. The <c>chromium</c>
/// channel is the full build in its new headless mode; the separate headless shell runs no extensions at all.
/// </para>
/// <para>
/// <b>The profile's own host folder.</b> A user-level native-messaging manifest is looked up in
/// <c>&lt;user-data-dir&gt;/NativeMessagingHosts</c> — on macOS as on Linux, because the browser derives that folder
/// from its user-data directory, and <c>--user-data-dir</c> moves both. So a throwaway profile carries its own host
/// registrations and nothing is written to the user's real browser folders.
/// </para>
/// <para>
/// <b>The extension id is DERIVED, not discovered.</b> The test copy of the manifest carries a <c>key</c> made per
/// run, and the id is the browser's own function of it (the first 16 bytes of the key's SHA-256, each nibble a
/// letter a–p). The host manifest's <c>allowed_origins</c> is written from that id BEFORE the browser starts, and
/// the launch proves the browser agrees by reading the extension's own manifest under that id. An unpacked
/// extension without a key is identified by a hash of its path instead, which a canonicalised temp folder
/// (<c>/var</c> vs <c>/private/var</c>) makes fragile.
/// </para>
/// </remarks>
public sealed class ExtensionBrowser : IAsyncDisposable
{
    private static readonly Lazy<int> BrowserInstalled = new(() =>
        Microsoft.Playwright.Program.Main(["install", "chromium", "--no-shell"]));

    /// <summary>The extension's name, as its manifest declares it.</summary>
    private const string ExtensionName = "Alcifra autofill";

    private readonly DirectoryInfo _root;
    private readonly IPlaywright _playwright;

    private ExtensionBrowser(DirectoryInfo root, IPlaywright playwright, IBrowserContext context, string extensionId, string stateFolder)
    {
        _root = root;
        _playwright = playwright;
        Context = context;
        ExtensionId = extensionId;
        StateFolder = stateFolder;
    }

    public IBrowserContext Context { get; }

    public string ExtensionId { get; }

    /// <summary>Where the fake store keeps its entries and records its calls.</summary>
    public string StateFolder { get; }

    public IReadOnlyList<FakeCall> Calls => FakeAutofillStore.Calls(StateFolder);

    /// <summary>Starts a browser whose profile registers a fake host under each of <paramref name="clients"/>.</summary>
    public static Task<ExtensionBrowser> LaunchAsync(IEnumerable<FakeEntry> entries, params string[] clients) =>
        LaunchAsync(entries, keepAlive: null, clients);

    /// <summary>
    /// As above, with the page's keep-alive interval shortened to <paramref name="keepAlive"/> in the test copy, so a
    /// test can watch several ticks in seconds rather than minutes. Only that one constant differs from what ships.
    /// </summary>
    public static async Task<ExtensionBrowser> LaunchAsync(IEnumerable<FakeEntry> entries, TimeSpan? keepAlive, params string[] clients)
    {
        if (BrowserInstalled.Value != 0)
        {
            throw new InvalidOperationException("Installing Playwright's Chromium failed; see the test output.");
        }

        var root = Directory.CreateTempSubdirectory("sa-extension-e2e-");
        var extension = Path.Combine(root.FullName, "extension");
        var profile = Path.Combine(root.FullName, "profile");
        var state = Path.Combine(root.FullName, "state");
        var extensionId = CopyExtension(extension, keepAlive);
        FakeAutofillStore.Seed(state, entries);

        var hosts = Path.Combine(profile, "NativeMessagingHosts");
        Directory.CreateDirectory(hosts);
        foreach (var client in clients)
        {
            var launcher = HostRegistration.WriteLauncher(
                Path.Combine(root.FullName, "launchers", $"{client}.sh"),
                DotnetExecutable(),
                $"'{typeof(Program).Assembly.Location}' {Program.HostVerb} {client} '{state}'");
            File.WriteAllText(
                Path.Combine(hosts, $"{HostRegistration.HostNameFor(client)}.json"),
                HostRegistration.Manifest(client, extensionId, launcher));
        }

        var playwright = await Playwright.CreateAsync();
        var context = await playwright.Chromium.LaunchPersistentContextAsync(profile, new BrowserTypeLaunchPersistentContextOptions
        {
            Channel = "chromium",
            Headless = true,
            Args = [$"--disable-extensions-except={extension}", $"--load-extension={extension}"],
        });

        // The extension's own files answer only under the id the browser assigned, so this proves the derivation.
        var probe = await context.NewPageAsync();
        var answer = await probe.GotoAsync($"chrome-extension://{extensionId}/manifest.json");
        if (answer is not { Ok: true } || !(await answer.TextAsync()).Contains(ExtensionName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The extension is not loaded under the derived id {extensionId}.");
        }

        await probe.CloseAsync();

        return new ExtensionBrowser(root, playwright, context, extensionId, state);
    }

    /// <summary>Opens <paramref name="url"/> in the browser's tab.</summary>
    public async Task<IPage> OpenAsync(string url)
    {
        var page = Context.Pages.FirstOrDefault() ?? await Context.NewPageAsync();
        await page.GotoAsync(url);
        return page;
    }

    /// <summary>Watches the extension's service worker in <paramref name="page"/>'s browser; see
    /// <see cref="ServiceWorkerProbe"/>.</summary>
    public Task<ServiceWorkerProbe> ProbeServiceWorkerAsync(IPage page) => ServiceWorkerProbe.AttachAsync(Context, page, ExtensionId);

    public async ValueTask DisposeAsync()
    {
        await Context.CloseAsync();
        _playwright.Dispose();
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A host process can still hold a file for a moment; the temp folder is the OS's to reclaim.
        }
    }

    /// <summary>Copies the extension as shipped, adding only a per-run <c>key</c>; returns the id it yields.</summary>
    private static string CopyExtension(string target, TimeSpan? keepAlive)
    {
        var source = Path.Combine(RepositoryRoot(), "browser-extension", "chrome");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        if (keepAlive is { } interval)
        {
            const string Shipped = "const KEEP_ALIVE_MS = 20 * 1000;";
            var content = Path.Combine(target, "content.js");
            var script = File.ReadAllText(content);
            if (!script.Contains(Shipped, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"content.js no longer declares '{Shipped}'; update the test's patch.");
            }

            File.WriteAllText(content, script.Replace(Shipped, $"const KEEP_ALIVE_MS = {(int)interval.TotalMilliseconds};", StringComparison.Ordinal));
        }

        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var manifestPath = Path.Combine(target, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["key"] = Convert.ToBase64String(publicKey);
        File.WriteAllText(manifestPath, manifest.ToJsonString());

        return string.Concat(SHA256.HashData(publicKey).Take(16)
            .SelectMany(b => new[] { b >> 4, b & 0xF })
            .Select(nibble => (char)('a' + nibble)));
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "browser-extension", "chrome", "manifest.json")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("browser-extension/chrome was not found above the test output.");
    }

    /// <summary>The muxer that runs this assembly as the host; the launcher's PATH lookup is the fallback.</summary>
    private static string DotnetExecutable()
    {
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(hostPath) && File.Exists(hostPath))
        {
            return hostPath;
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var underRoot = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "dotnet");
        return underRoot is not null && File.Exists(underRoot) ? underRoot : "dotnet";
    }
}
