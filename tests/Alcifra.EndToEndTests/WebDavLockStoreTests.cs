using DotNet.Testcontainers.Builders;
using Alcifra.Api.Errors.Exceptions.WebDav;
using Alcifra.Api.WebDav;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Alcifra.EndToEndTests;

// The two lock stores answer ONE contract (#2176, ADR 1052). The Valkey store runs against a real server, and a second store
// over the same server stands in for the other instance: on two instances a client's UNLOCK often reached the one that never
// saw the LOCK, and answered 409.
[Trait("Area", "e2e-2")]
public class WebDavLockStoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task The_in_process_store_answers_the_contract()
    {
        var store = new InMemoryWebDavLockStore();
        await AssertContractAsync(store, store);

        var tenant = Guid.NewGuid();
        Assert.NotNull(await store.TryLockAsync(tenant, "gone", Guid.NewGuid(), TimeSpan.FromSeconds(1), Now));
        Assert.False(await store.IsBlockedAsync(tenant, "gone", Guid.NewGuid(), [], Now.AddSeconds(2)));
    }

    [Fact]
    public async Task Valkey_answers_the_contract_across_two_instances_and_expires_a_lock()
    {
        await using var valkey = new ContainerBuilder()
            .WithImage("valkey/valkey:8.1.1-alpine")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6379))
            .Build();

        await valkey.StartAsync();

        var address = $"{valkey.Hostname}:{valkey.GetMappedPublicPort(6379)}";
        using var first = await ConnectionMultiplexer.ConnectAsync(address);
        using var second = await ConnectionMultiplexer.ConnectAsync(address);
        var store = new ValkeyWebDavLockStore(first, NullLogger<ValkeyWebDavLockStore>.Instance);
        var otherInstance = new ValkeyWebDavLockStore(second, NullLogger<ValkeyWebDavLockStore>.Instance);

        await AssertContractAsync(store, otherInstance);

        var tenant = Guid.NewGuid();
        Assert.NotNull(await store.TryLockAsync(tenant, "gone", Guid.NewGuid(), TimeSpan.FromMilliseconds(200), Now));
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(await otherInstance.IsBlockedAsync(tenant, "gone", Guid.NewGuid(), [], Now));
    }

    [Fact]
    public async Task A_store_that_cannot_reach_valkey_refuses_by_name()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1");
        options.AbortOnConnectFail = false;
        options.ConnectTimeout = 100;
        options.AsyncTimeout = 200;
        options.SyncTimeout = 200;
        using var unreachable = await ConnectionMultiplexer.ConnectAsync(options);
        var store = new ValkeyWebDavLockStore(unreachable, NullLogger<ValkeyWebDavLockStore>.Instance);
        var tenant = Guid.NewGuid();

        await Assert.ThrowsAsync<WebDavLocksUnavailableException>(() => store.TryLockAsync(tenant, "a", Guid.NewGuid(), Timeout, Now));
        await Assert.ThrowsAsync<WebDavLocksUnavailableException>(() => store.UnlockAsync(tenant, "a", "token"));
        await Assert.ThrowsAsync<WebDavLocksUnavailableException>(() => store.IsBlockedAsync(tenant, "a", Guid.NewGuid(), [], Now));
    }

    private static async Task AssertContractAsync(IWebDavLockStore store, IWebDavLockStore otherInstance)
    {
        var tenant = Guid.NewGuid();
        var (holder, stranger) = (Guid.NewGuid(), Guid.NewGuid());

        var taken = await store.TryLockAsync(tenant, "a/b", holder, Timeout, Now);
        Assert.NotNull(taken);
        Assert.Equal(holder, taken.Owner);

        Assert.Null(await otherInstance.TryLockAsync(tenant, "a/b", stranger, Timeout, Now));
        Assert.True(await otherInstance.IsBlockedAsync(tenant, "a/b", stranger, [], Now));
        Assert.False(await otherInstance.IsBlockedAsync(tenant, "a/b", stranger, [taken.Token], Now));
        Assert.False(await otherInstance.IsBlockedAsync(tenant, "a/b", holder, [], Now));

        var refreshed = await otherInstance.TryLockAsync(tenant, "a/b", holder, Timeout, Now);
        Assert.Equal(taken.Token, refreshed?.Token);

        Assert.False(await otherInstance.IsBlockedAsync(Guid.NewGuid(), "a/b", stranger, [], Now));
        Assert.False(await otherInstance.UnlockAsync(tenant, "a/b", "opaquelocktoken:wrong"));
        Assert.True(await otherInstance.IsBlockedAsync(tenant, "a/b", stranger, [], Now));

        Assert.True(await otherInstance.UnlockAsync(tenant, "a/b", taken.Token));
        Assert.False(await store.IsBlockedAsync(tenant, "a/b", stranger, [], Now));
        Assert.False(await store.UnlockAsync(tenant, "a/b", taken.Token));
    }
}
