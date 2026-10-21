using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.EndToEndTests;

// The no-service presign guard (#1843, ADR 0953), rehearsed inside the shared host for the keys a test names.
//
// WHY A REHEARSAL. The fixture configures an encryption service (it hosts a stub), so the composition root installs
// the encrypting decorator and the guard never serves. A second host without the service would re-seed, re-mint its
// signing keys and fight the shared one over process-global settings (CLAUDE.md's E2E gotchas). So the presigned
// READS of exactly the keys in PresignGuardedKeys go through the real EncryptedObjectPresignGuard, probing the real
// store; every other key and every other member reaches the composed client untouched.
public sealed partial class E2EApiFactory
{
    /// <summary>Object keys whose presigned reads this host answers as an installation without the service.</summary>
    public ConcurrentDictionary<string, bool> PresignGuardedKeys { get; } = new(StringComparer.Ordinal);

    private void RehearseThePresignGuard(IServiceCollection services)
    {
        var composed = services.Last(d => d.ServiceType == typeof(IObjectStorageClient));
        services.Remove(composed);
        services.AddSingleton<IObjectStorageClient>(provider =>
        {
            var inner = (IObjectStorageClient)composed.ImplementationFactory!(provider);
            var guard = new EncryptedObjectPresignGuard(inner, provider.GetRequiredService<S3ObjectStorageClient>());
            return PresignRehearsal.Create(inner, guard, PresignGuardedKeys);
        });
    }

    /// <summary>Routes the guarded presigns of named keys to the guard, and everything else to the composed client.</summary>
    public class PresignRehearsal : DispatchProxy
    {
        private static readonly HashSet<string> GuardedPresigns =
        [
            nameof(IObjectStorageClient.GetPresignedDownloadUrlAsync),
            nameof(IObjectStorageClient.GetPresignedPreviewUrlAsync),
        ];

        private IObjectStorageClient _inner = null!;
        private IObjectStorageClient _guard = null!;
        private ConcurrentDictionary<string, bool> _keys = null!;

        public static IObjectStorageClient Create(
            IObjectStorageClient inner, IObjectStorageClient guard, ConcurrentDictionary<string, bool> keys)
        {
            var proxy = Create<IObjectStorageClient, PresignRehearsal>();
            var rehearsal = (PresignRehearsal)(object)proxy;
            rehearsal._inner = inner;
            rehearsal._guard = guard;
            rehearsal._keys = keys;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var target = GuardedPresigns.Contains(targetMethod!.Name) && args?[0] is string key && _keys.ContainsKey(key)
                ? _guard
                : _inner;
            try
            {
                return targetMethod.Invoke(target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }
}
