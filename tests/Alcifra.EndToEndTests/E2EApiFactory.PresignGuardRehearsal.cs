using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Storage;

namespace Alcifra.EndToEndTests;

// The no-service storage guard (#1843, ADR 0953; reads since #1859), rehearsed inside the shared host for the keys a
// test names.
//
// WHY A REHEARSAL. The fixture configures an encryption service (it hosts a stub), so the composition root installs
// the encrypting decorator and the guard never serves. A second host without the service would re-seed, re-mint its
// signing keys and fight the shared one over process-global settings (CLAUDE.md's E2E gotchas). So, for exactly the
// keys in GuardedKeys:
//   - the PRESIGNED reads go through the real EncryptedObjectPresignGuard, probing the real store;
//   - the SERVER-SIDE reads (the bytes of a rendition's source, a check-out stash) go to a real S3 client with the
//     composition root's flag flipped, refusing exactly as an installation without the service refuses them.
// Every other key and every other member reaches the composed client untouched.
public sealed partial class E2EApiFactory
{
    /// <summary>Object keys this host treats as belonging to an installation without an encryption service.</summary>
    public ConcurrentDictionary<string, bool> PresignGuardedKeys { get; } = new(StringComparer.Ordinal);

    private static readonly HashSet<string> GuardedPresigns =
    [
        nameof(IObjectStorageClient.GetPresignedDownloadUrlAsync),
        nameof(IObjectStorageClient.GetPresignedPreviewUrlAsync),
    ];

    private static readonly HashSet<string> RefusedReads =
    [
        nameof(IObjectStorageClient.GetObjectAsync),
        nameof(IObjectStorageClient.GetObjectWithMetadataAsync),
        nameof(IObjectStorageClient.GetObjectInfoAsync),
    ];

    private void RehearseThePresignGuard(IServiceCollection services)
    {
        var composed = services.Last(d => d.ServiceType == typeof(IObjectStorageClient));
        services.Remove(composed);
        services.AddSingleton<IObjectStorageClient>(provider =>
        {
            var inner = (IObjectStorageClient)composed.ImplementationFactory!(provider);
            var guard = new EncryptedObjectPresignGuard(inner, provider.GetRequiredService<S3ObjectStorageClient>());
            // The read half: the store's own client with the composition root's flag, so a guarded key's bytes refuse
            // with the real message and the real exception — never a fake that only pretends to be encrypted.
            var refusing = new S3ObjectStorageClient(
                provider.GetRequiredService<IOptions<ObjectStorageOptions>>(),
                provider.GetRequiredService<ILogger<S3ObjectStorageClient>>())
            {
                RefuseEncryptedObjects = true,
            };
            return PresignRehearsal.Create(inner, guard, refusing, PresignGuardedKeys);
        });
    }

    /// <summary>Routes the guarded presigns and reads of named keys to their guards, everything else to the client.</summary>
    public class PresignRehearsal : DispatchProxy
    {
        private IObjectStorageClient _inner = null!;
        private IObjectStorageClient _guard = null!;
        private IObjectStorageClient _refusing = null!;
        private ConcurrentDictionary<string, bool> _keys = null!;

        public static IObjectStorageClient Create(
            IObjectStorageClient inner, IObjectStorageClient guard, IObjectStorageClient refusing, ConcurrentDictionary<string, bool> keys)
        {
            var proxy = Create<IObjectStorageClient, PresignRehearsal>();
            var rehearsal = (PresignRehearsal)(object)proxy;
            rehearsal._inner = inner;
            rehearsal._guard = guard;
            rehearsal._refusing = refusing;
            rehearsal._keys = keys;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var target = _inner;
            if (args is { Length: > 0 } && args[0] is string key && _keys.ContainsKey(key))
            {
                target = GuardedPresigns.Contains(targetMethod!.Name) ? _guard
                    : RefusedReads.Contains(targetMethod.Name) ? _refusing
                    : _inner;
            }

            try
            {
                return targetMethod!.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
