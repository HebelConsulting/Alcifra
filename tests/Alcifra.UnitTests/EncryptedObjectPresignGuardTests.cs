using System.Net;
using System.Reflection;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure;
using Alcifra.Infrastructure.Storage;

namespace Alcifra.UnitTests;

// Without an encryption service, a presigned READ of an object encrypted at rest is refused the way the
// server-side reads refuse it (#1843, ADR 0953). Before this, the reads said 503 ENCRYPTION_SERVICE_NOT_CONFIGURED
// while the preview's presigned URL handed the browser the AES-GCM ciphertext, which it rendered as text.
public class EncryptedObjectPresignGuardTests
{
    private const string Key = "tenants/8a1f6d3e-0000-4000-8000-000000000001/2026/b2c3d4e5-0000-4000-8000-000000000002/content.pdf";
    private const string WrappedDekValue = "c2VjcmV0LXdyYXBwZWQta2V5LXZhbHVl";

    // The presigned reads the guard must refuse, and the presigns it deliberately leaves alone. Every
    // GetPresigned* member of the interface must be in exactly one of the two, so a new presign cannot arrive
    // unclassified (and therefore unguarded) — see Every_presign_on_the_seam_is_classified.
    private static readonly string[] GuardedPresigns =
    [
        nameof(IObjectStorageClient.GetPresignedDownloadUrlAsync),
        nameof(IObjectStorageClient.GetPresignedPreviewUrlAsync),
    ];

    private static readonly Dictionary<string, string> UnguardedPresigns = new()
    {
        // A presigned PUT hands nothing out.
        [nameof(IObjectStorageClient.GetPresignedUploadUrlAsync)] = "an upload hands nothing out",
        // Ciphertext by name, signed for the encryption service's internal fetch, never a client's; its only
        // caller cannot run without the service.
        [nameof(IObjectStorageClient.GetPresignedCiphertextUrlAsync)] = "the encryption service's own fetch",
    };

    [Fact]
    public void Every_presign_on_the_seam_is_classified()
    {
        var presigns = typeof(IObjectStorageClient).GetMethods()
            .Where(m => m.Name.StartsWith("GetPresigned", StringComparison.Ordinal))
            .Select(m => m.Name)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        var classified = GuardedPresigns.Concat(UnguardedPresigns.Keys).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            presigns.SequenceEqual(classified),
            $"IObjectStorageClient presigns [{string.Join(", ", presigns)}] but this test classifies "
            + $"[{string.Join(", ", classified)}]. A new presigned READ must be refused by EncryptedObjectPresignGuard "
            + "for an object carrying sa-wrapped-dek (#1843) and added to GuardedPresigns; a presign that hands "
            + "nothing to a client goes to UnguardedPresigns with its reason.");
    }

    [Fact]
    public void Every_member_of_the_seam_is_forwarded_rather_than_left_to_its_default()
    {
        // A default interface body reached through the guard runs against the GUARD, replacing the S3 client's
        // real override (the range read, the metadata write) with the fake-friendly fallback — silently.
        var map = typeof(EncryptedObjectPresignGuard).GetInterfaceMap(typeof(IObjectStorageClient));
        var defaulted = map.TargetMethods
            .Where(target => target.DeclaringType != typeof(EncryptedObjectPresignGuard))
            .Select(target => target.Name)
            .ToList();

        Assert.True(defaulted.Count == 0,
            $"EncryptedObjectPresignGuard leaves [{string.Join(", ", defaulted)}] to the interface's default body; forward them to the inner client.");
    }

    [Fact]
    public async Task Every_member_reaches_the_same_member_of_the_inner_client()
    {
        // The behavioural half of the test above: implementing a member is not forwarding it.
        foreach (var method in typeof(IObjectStorageClient).GetMethods())
        {
            var (guard, inner, _) = Build(new Dictionary<string, string>());

            await InvokeAsync(guard, method);

            Assert.Contains(Signature(method), inner.Calls);
        }
    }

    [Fact]
    public async Task Every_guarded_presign_refuses_an_object_carrying_a_wrapped_DEK()
    {
        foreach (var name in GuardedPresigns)
        {
            var (guard, inner, probe) = Build(Wrapped());

            var refusal = await Assert.ThrowsAsync<EncryptedObjectWithoutEncryptionServiceException>(
                () => InvokeAsync(guard, PresignNamed(name)));

            // The reads' own refusal, so following the link answers the same 503 ENCRYPTION_SERVICE_NOT_CONFIGURED.
            Assert.Equal("ENCRYPTION_SERVICE_NOT_CONFIGURED", refusal.ErrorCode);
            Assert.Equal(503, refusal.ApiStatusCode);
            Assert.Equal(Key, refusal.ObjectKey);

            // Refused BEFORE signing: no URL to the ciphertext was ever made. One HEAD, through the probe.
            Assert.DoesNotContain(inner.Calls, call => call.StartsWith(name, StringComparison.Ordinal));
            Assert.Equal(1, probe.Calls);
        }
    }

    [Fact]
    public async Task The_guard_never_asks_through_the_inner_clients_refusing_read()
    {
        // One log line per refusal: the plain client's GetObjectInfoAsync refuses a wrapped object AND logs an
        // Error, so a guard that asked through it would add a second line to every refusal. It asks the probe.
        foreach (var name in GuardedPresigns)
        {
            var (guard, inner, _) = Build(Wrapped());

            await Assert.ThrowsAsync<EncryptedObjectWithoutEncryptionServiceException>(
                () => InvokeAsync(guard, PresignNamed(name)));

            Assert.DoesNotContain(inner.Calls, call => call.StartsWith(nameof(IObjectStorageClient.GetObjectInfoAsync), StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_plaintext_object_is_presigned_as_before()
    {
        // The anti-vacuous half: a guard that refused everything would pass the tests above and take down every
        // installation without an encryption service — which is most of them.
        foreach (var name in GuardedPresigns)
        {
            var (guard, inner, probe) = Build(new Dictionary<string, string> { ["content-owner"] = "someone" });

            var url = await InvokeAsync(guard, PresignNamed(name));

            Assert.Equal(FakeStore.PresignedUrl, url);
            Assert.Contains(inner.Calls, call => call.StartsWith(name, StringComparison.Ordinal));
            Assert.Equal(1, probe.Calls); // the owner kept the cost: one HEAD per presigned read, always
        }
    }

    [Fact]
    public async Task A_missing_object_is_presigned_as_before()
    {
        // Presigning never needed the object to exist; a HEAD's 404 must not change that.
        foreach (var name in GuardedPresigns)
        {
            var (guard, _, _) = Build(metadata: null);

            Assert.Equal(FakeStore.PresignedUrl, await InvokeAsync(guard, PresignNamed(name)));
        }
    }

    [Fact]
    public async Task The_unguarded_presigns_never_ask_the_store()
    {
        foreach (var name in UnguardedPresigns.Keys)
        {
            var (guard, _, probe) = Build(Wrapped());

            Assert.Equal(FakeStore.PresignedUrl, await InvokeAsync(guard, PresignNamed(name)));
            Assert.Equal(0, probe.Calls);
        }
    }

    // ---- OmittableLinks: drop the link, keep the resource -----------------------------------------------------

    [Fact]
    public async Task An_embedded_link_to_a_refused_object_is_omitted_with_one_Warning()
    {
        var (guard, _, _) = Build(Wrapped());
        var log = new List<(LogLevel Level, string Message)>();
        var omittable = new OmittableLinks(new CapturingLogger<OmittableLinks>(log));

        var link = await omittable.OrOmittedAsync(guard.GetPresignedPreviewUrlAsync(Key, TimeSpan.FromMinutes(5)), "preview");

        Assert.Null(link);
        // ONE line for the refusal, at Warning: the caller sees only an absent rel (ADR 0626).
        var line = Assert.Single(log);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains("preview", line.Message, StringComparison.Ordinal);
        Assert.Contains(Key, line.Message, StringComparison.Ordinal);
        Assert.Contains("Trace", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(WrappedDekValue, line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_embedded_link_to_a_plaintext_object_passes_through_without_a_line()
    {
        var (guard, _, _) = Build(new Dictionary<string, string>());
        var log = new List<(LogLevel Level, string Message)>();
        var omittable = new OmittableLinks(new CapturingLogger<OmittableLinks>(log));

        Assert.Equal(FakeStore.PresignedUrl,
            await omittable.OrOmittedAsync(guard.GetPresignedDownloadUrlAsync(Key, TimeSpan.FromMinutes(5)), "download"));
        Assert.Empty(log);
    }

    [Fact]
    public async Task Only_the_encryption_refusal_is_omitted_and_every_other_failure_still_fails()
    {
        // Swallowing everything would turn a storage outage into silently missing links.
        var omittable = new OmittableLinks(new CapturingLogger<OmittableLinks>([]));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => omittable.OrOmittedAsync(Task.FromException<Uri?>(new InvalidOperationException("store down")), "download"));
    }

    [Theory]
    [InlineData(null, typeof(EncryptedObjectPresignGuard))]
    [InlineData("http://encryption.invalid:8080", typeof(EncryptingObjectStorageClient))]
    public void The_composition_root_installs_the_guard_only_when_no_service_is_configured(string? serviceUrl, Type expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=db.invalid;Database=x;Username=x;Password=x",
                ["ObjectStorage:ServiceUrl"] = "http://storage.invalid:8333",
                ["ObjectStorage:Region"] = "us-east-1",
                ["ObjectStorage:BucketName"] = "simplarchive",
                ["ObjectStorage:AccessKey"] = "access",
                ["ObjectStorage:SecretKey"] = "secret",
                ["Encryption:ServiceUrl"] = serviceUrl,
            })
            .Build();
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.IsType(expected, provider.GetRequiredService<IObjectStorageClient>());
    }

    // ---- harness -------------------------------------------------------------------------------------------

    private static Dictionary<string, string> Wrapped() => new()
    {
        [AtRestMetadata.WrappedDekKey] = WrappedDekValue,
        [AtRestMetadata.KekGenerationKey] = "kek-v1",
    };

    private static MethodInfo PresignNamed(string name) =>
        typeof(IObjectStorageClient).GetMethods().Single(m => m.Name == name);

    private static string Signature(MethodInfo method) =>
        $"{method.Name}({string.Join(",", method.GetParameters().Select(p => p.ParameterType.Name))})";

    /// <summary>Calls an interface member with placeholder arguments and awaits it, returning its result.</summary>
    private static async Task<object?> InvokeAsync(IObjectStorageClient target, MethodInfo method)
    {
        var arguments = method.GetParameters().Select(Placeholder).ToArray();
        var task = (Task)method.Invoke(target, arguments)!;
        await task;
        return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
    }

    private static object? Placeholder(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        return type switch
        {
            _ when type == typeof(string) => parameter.Name is "prefix" ? "tenants/8a1f6d3e-0000-4000-8000-000000000001/" : Key,
            _ when type == typeof(Stream) => new MemoryStream([1, 2, 3]),
            _ when type == typeof(TimeSpan) => TimeSpan.FromMinutes(5),
            _ when type == typeof(CancellationToken) => CancellationToken.None,
            _ when type == typeof(IReadOnlyDictionary<string, string>) => new Dictionary<string, string>(),
            _ when type.IsValueType => Activator.CreateInstance(type),
            _ => null,
        };
    }

    private static (EncryptedObjectPresignGuard Guard, FakeStore Inner, FakeProbe Probe) Build(Dictionary<string, string>? metadata)
    {
        // The inner store refuses a wrapped object on its own HEAD, as the real plain client does.
        var inner = FakeStore.Create(metadata, refusesOnHead: true);
        var probe = new FakeProbe(metadata);
        return (new EncryptedObjectPresignGuard((IObjectStorageClient)(object)inner, probe), inner, probe);
    }

    /// <summary>The metadata HEAD, counted — null metadata means nothing is stored under the key.</summary>
    private sealed class FakeProbe(Dictionary<string, string>? metadata) : IStoredMetadataProbe
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyDictionary<string, string>?> MetadataAsync(string objectKey, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(metadata);
        }
    }

    /// <summary>
    /// A store that records every member called on it and answers with a placeholder of the member's return
    /// type — a proxy, so a member added to the interface is covered without this fake learning about it.
    /// </summary>
    public class FakeStore : DispatchProxy
    {
        public static readonly Uri PresignedUrl = new("https://storage.invalid/presigned");

        public List<string> Calls { get; } = [];

        private Dictionary<string, string>? _metadata;
        private bool _refusesOnHead;

        public static FakeStore Create(Dictionary<string, string>? metadata, bool refusesOnHead)
        {
            var proxy = Create<IObjectStorageClient, FakeStore>();
            var fake = (FakeStore)(object)proxy;
            fake._metadata = metadata;
            fake._refusesOnHead = refusesOnHead;
            return fake;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod!;
            Calls.Add(Signature(method));

            if (method.Name == nameof(IObjectStorageClient.GetObjectInfoAsync))
            {
                return _metadata switch
                {
                    null => Task.FromException<StoredObjectInfo>(new AmazonS3Exception("Not Found") { StatusCode = HttpStatusCode.NotFound }),
                    _ when _refusesOnHead && _metadata.ContainsKey(AtRestMetadata.WrappedDekKey) =>
                        Task.FromException<StoredObjectInfo>(new EncryptedObjectWithoutEncryptionServiceException((string)args![0]!)),
                    _ => Task.FromResult(new StoredObjectInfo(3, "application/pdf", _metadata)),
                };
            }

            var returnType = method.ReturnType;
            if (!returnType.IsGenericType)
            {
                return Task.CompletedTask;
            }

            var resultType = returnType.GetGenericArguments()[0];
            object? result = resultType switch
            {
                _ when resultType == typeof(Uri) => PresignedUrl,
                _ when resultType == typeof(Stream) => new MemoryStream(),
                _ when resultType == typeof(StoredObject) => new StoredObject(new MemoryStream(), 0, new Dictionary<string, string>()),
                _ when resultType == typeof(ObjectLockStatus) => new ObjectLockStatus(null, false),
                _ when resultType == typeof(IReadOnlyList<StorageObject>) => Array.Empty<StorageObject>(),
                _ when resultType == typeof(IReadOnlyList<StoredObjectVersion>) => Array.Empty<StoredObjectVersion>(),
                _ when resultType.IsValueType => Activator.CreateInstance(resultType),
                _ => null,
            };

            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result]);
        }
    }

    private sealed class CapturingLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Add((logLevel, formatter(state, exception)));
    }
}
