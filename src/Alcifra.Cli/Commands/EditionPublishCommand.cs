using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class EditionPublishSettings : ApiSettings
{
    [CommandArgument(0, "<VERSION>")]
    [Description("The release to publish, e.g. 0.57.6.")]
    public required string Version { get; init; }

    [CommandOption("--layouts <DIR>")]
    [Description("A folder of OCI layouts, one per repository, named after it with __ for a slash.")]
    public required string Layouts { get; init; }

    [CommandOption("--downloads <DIR>")]
    [Description("A folder holding the release's desktop packages and manual. Other files in it are left out.")]
    public string? Downloads { get; init; }

    [CommandOption("--key-file <FILE>")]
    [Description($"A file holding the edition's push key (keep it mode 600). Defaults to ${ModuleUploadSettings.KeyVariable}.")]
    public string? KeyFile { get; init; }

    /// <summary>The key: from the file if one is named, else the environment. Never an option value (ADR 0886).</summary>
    public string ResolvedKey => string.IsNullOrWhiteSpace(KeyFile)
        ? Environment.GetEnvironmentVariable(ModuleUploadSettings.KeyVariable)?.Trim() ?? string.Empty
        : File.ReadAllText(KeyFile).Trim();

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when !Directory.Exists(Layouts) => ValidationResult.Error($"No such folder: {Layouts}"),
        _ when Directory.GetDirectories(Layouts).Length == 0 => ValidationResult.Error($"{Layouts} holds no image layouts: a release needs at least one."),
        _ when Downloads is not null && !Directory.Exists(Downloads) => ValidationResult.Error($"No such folder: {Downloads}"),
        _ when !string.IsNullOrWhiteSpace(KeyFile) && !File.Exists(KeyFile) => ValidationResult.Error($"No such key file: {KeyFile}"),
        _ when string.IsNullOrEmpty(ResolvedKey) => ValidationResult.Error($"No push key. Pass --key-file, or set ${ModuleUploadSettings.KeyVariable}."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>A manifest of a layout, as it is offered: its tag where one names it.</summary>
public sealed record LayoutManifest(string Repository, string? Tag, string Digest, string MediaType, byte[] Content);

/// <summary>Everything one OCI layout holds, in the order a release needs it: blobs first, then manifests bottom-up,
/// so whatever a manifest names is there when it arrives.</summary>
public sealed record OciLayout(IReadOnlyList<(string Digest, string Path, string? MediaType)> Blobs, IReadOnlyList<LayoutManifest> Manifests)
{
    private const string TagAnnotation = "org.opencontainers.image.ref.name";
    private const string DefaultManifestType = "application/vnd.oci.image.manifest.v1+json";

    /// <summary>Reads the layout in <paramref name="directory"/>. The folder's name is the repository, with __ for a slash.</summary>
    public static OciLayout Read(string directory, string version)
    {
        var repository = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)).Replace("__", "/", StringComparison.Ordinal);
        var blobs = new Dictionary<string, (string Digest, string Path, string? MediaType)>(StringComparer.Ordinal);
        var manifests = new List<LayoutManifest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "index.json")));
        foreach (var entry in index.RootElement.GetProperty("manifests").EnumerateArray())
        {
            var digest = entry.GetProperty("digest").GetString()!;
            Walk(digest, entry.TryGetProperty("mediaType", out var type) ? type.GetString() : null);
            if (entry.TryGetProperty("annotations", out var annotations) && annotations.TryGetProperty(TagAnnotation, out var tagged))
            {
                var tag = tagged.GetString()!;
                var bytes = BlobBytes(digest);
                var mediaType = TypeOf(bytes, entry.TryGetProperty("mediaType", out var t) ? t.GetString() : null);
                manifests.Add(new LayoutManifest(repository, tag, digest, mediaType, bytes));
                if (tag == version)
                {
                    manifests.Add(new LayoutManifest(repository, "latest", digest, mediaType, bytes));   // the newest the caller may see (ADR 0021)
                }
            }
        }

        return new OciLayout([.. blobs.Values], manifests);

        byte[] BlobBytes(string digest) => File.ReadAllBytes(BlobPath(digest));

        string BlobPath(string digest) => Path.Combine(directory, "blobs", "sha256", digest["sha256:".Length..]);

        // Children first; a digest is offered once untagged, and again for each tag that names it.
        void Walk(string digest, string? declaredType)
        {
            if (!seen.Add(digest))
            {
                return;
            }

            var bytes = BlobBytes(digest);
            using var manifest = JsonDocument.Parse(bytes);
            if (manifest.RootElement.TryGetProperty("config", out var config))
            {
                Add(config);
            }

            foreach (var name in new[] { "layers" })
            {
                if (manifest.RootElement.TryGetProperty(name, out var list))
                {
                    foreach (var layer in list.EnumerateArray())
                    {
                        Add(layer);
                    }
                }
            }

            if (manifest.RootElement.TryGetProperty("manifests", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    Walk(child.GetProperty("digest").GetString()!, child.TryGetProperty("mediaType", out var type) ? type.GetString() : null);
                }
            }

            manifests.Add(new LayoutManifest(repository, null, digest, TypeOf(bytes, declaredType), bytes));
        }

        void Add(JsonElement descriptor)
        {
            var digest = descriptor.GetProperty("digest").GetString()!;
            var path = BlobPath(digest);
            if (!File.Exists(path))
            {
                throw new CliException($"The layout of {repository} names {digest}, which it does not hold.");
            }

            blobs[digest] = (digest, path, descriptor.TryGetProperty("mediaType", out var type) ? type.GetString() : null);
        }
    }

    private static string TypeOf(byte[] manifest, string? declared)
    {
        using var doc = JsonDocument.Parse(manifest);
        return doc.RootElement.TryGetProperty("mediaType", out var own) && own.GetString() is { Length: > 0 } type ? type : declared ?? DefaultManifestType;
    }
}

/// <summary>
/// Publishes one release of a white-label edition to the vendor (Licensing ADR 0022), with the edition's push key: opens
/// it, uploads each layer and download straight to storage, adds the manifests after what they name, and seals it.
/// </summary>
/// <remarks>Safe to run again: what is already there is reported and skipped, and until it is sealed a release is absent
/// to customers. Found by hypermedia without a login, like <c>module upload</c> (ADR 0918). The key goes only to the
/// module's requests, never to the root or to storage.</remarks>
public sealed class EditionPublishCommand(ILogger<EditionPublishCommand> logger) : AsyncCommand<EditionPublishSettings>
{
    /// <summary>The rel the Licensing Module advertises on every root.</summary>
    public const string PublishRel = "licensing:edition-publish";

    // What the opened release links to. The Licensing Module advertises them, in another repository, so the core's rel
    // guard cannot see them, as for module upload's commit rel.
    private const string BlobsRel = "blobs";
    private const string DownloadsRel = "downloads";
    private const string ManifestsRel = "manifests";
    private const string SealRel = "seal";

    protected override async Task<int> ExecuteAsync(CommandContext context, EditionPublishSettings settings, CancellationToken cancellationToken)
    {
        // Hashing a large layer on the server takes minutes, so no request here may time out by default.
        using var http = new HttpClient { BaseAddress = new Uri($"{settings.ResolvedUrl.TrimEnd('/')}/"), Timeout = Timeout.InfiniteTimeSpan };
        var key = settings.ResolvedKey;
        var open = await new Hypermedia(new AlcifraApi(http)).RootHrefAsync(PublishRel, cancellationToken);
        var release = JsonDocument.Parse(await PostAsync(http, open, key, new { version = settings.Version }, cancellationToken)).RootElement;
        var links = Hypermedia.LinksOf(release);
        var stored = release.GetProperty("blobs").EnumerateArray().Select(b => b.GetString()).ToHashSet(StringComparer.Ordinal);
        logger.LogInformation("Opened release {Version}; {Count} layers are stored already", settings.Version, stored.Count);

        foreach (var directory in Directory.GetDirectories(settings.Layouts).Order(StringComparer.Ordinal))
        {
            var layout = OciLayout.Read(directory, settings.Version);
            foreach (var blob in layout.Blobs)
            {
                if (stored.Add(blob.Digest))
                {
                    await UploadAsync(http, Hypermedia.Href(links, BlobsRel, "The release"), key, new { digest = blob.Digest, mediaType = blob.MediaType }, blob.Path, cancellationToken);
                }
            }

            foreach (var manifest in layout.Manifests)
            {
                await PostAsync(http, Hypermedia.Href(links, ManifestsRel, "The release"), key, new
                {
                    repository = manifest.Repository,
                    tag = manifest.Tag,
                    digest = manifest.Digest,
                    mediaType = manifest.MediaType,
                    content = Convert.ToBase64String(manifest.Content),
                }, cancellationToken);
            }

            logger.LogInformation("Published {Directory}: {Blobs} layers, {Manifests} manifests", Path.GetFileName(directory), layout.Blobs.Count, layout.Manifests.Count);
        }

        if (settings.Downloads is { } downloads)
        {
            foreach (var path in Directory.GetFiles(downloads).Order(StringComparer.Ordinal))
            {
                if (EditionDownloads.Classify(Path.GetFileName(path)) is var (platform, kind))
                {
                    await using var bytes = File.OpenRead(path);
                    var sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(bytes, cancellationToken));
                    await UploadAsync(http, Hypermedia.Href(links, DownloadsRel, "The release"), key, new { name = Path.GetFileName(path), platform, kind, sha256 }, path, cancellationToken);
                }
            }
        }

        await PostAsync(http, Hypermedia.Href(links, SealRel, "The release"), key, new { }, cancellationToken);
        logger.LogInformation("Sealed release {Version}: customers entitled to it can pull it now", settings.Version);
        return 0;
    }

    // Offer, then PUT and commit unless it is there already.
    private async Task UploadAsync(HttpClient http, string collection, string key, object offer, string path, CancellationToken cancellationToken)
    {
        var answer = JsonDocument.Parse(await PostAsync(http, collection, key, offer, cancellationToken)).RootElement;
        if (answer.TryGetProperty("exists", out var exists) && exists.GetBoolean())
        {
            return;
        }

        logger.LogInformation("Uploading {File} ({Bytes} bytes)", Path.GetFileName(path), new FileInfo(path).Length);
        await AlcifraApi.UploadFileAsync(new Uri(answer.GetProperty(ModuleUploadCommand.UrlField).GetString()!), path, cancellationToken);
        await PostAsync(http, Hypermedia.Href(Hypermedia.LinksOf(answer), ModuleUploadCommand.CommitRel, "The upload offer"), key, new { }, cancellationToken);
    }

    private static async Task<string> PostAsync(HttpClient http, string href, string key, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, href)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? text
            : throw new CliException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "The vendor refused the push key (401). Check the key file or the environment variable."
                : AlcifraApi.Describe(response.StatusCode, text, href));
    }
}

/// <summary>Which files of a release are delivered to a person, and what the page calls them.</summary>
public static class EditionDownloads
{
    /// <summary>The platform and kind of a release's file, or null for what is not delivered (the chart is an image
    /// artifact, a site bundle is the website's).</summary>
    public static (string Platform, string Kind)? Classify(string fileName) => fileName switch
    {
        _ when fileName.EndsWith("-arm64.dmg", StringComparison.Ordinal) => ("macos-arm64", "client"),
        _ when fileName.EndsWith("-x64.dmg", StringComparison.Ordinal) => ("macos-x64", "client"),
        _ when fileName.EndsWith("-win-x64.zip", StringComparison.Ordinal) => ("windows-x64", "client"),
        _ when fileName.EndsWith("-linux-x64.tar.gz", StringComparison.Ordinal) => ("linux-x64", "client"),
        _ when fileName.EndsWith("-Manual.pdf", StringComparison.Ordinal) => ("any", "manual"),
        _ => null,
    };
}
