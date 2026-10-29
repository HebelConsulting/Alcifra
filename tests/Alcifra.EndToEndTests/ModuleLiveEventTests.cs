using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Infrastructure.Persistence;
using Alcifra.Presentation;

namespace Alcifra.EndToEndTests;

// A module's ephemeral event over the live hub (ABI 1.25, ADR 0957), over the real wire and the real loader: the test
// module sends to user A; A's open hub connection receives it, stamped with the module's id; B's does not; nothing is
// stored. Refusals wear the core's problem shape: an oversized payload (413), a spent rate (429), a revoked principal
// (403), and an inactive module never reaches the seam at all (the controller gate's 404).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ModuleLiveEventTests(E2EApiFactory factory)
{
    private const string VerifyKey = "SIMPLARCHIVE_TESTMODULE_VERIFY_KEY";
    private const string Send = "/api/test-module/live-events";

    private async Task<(Guid Id, string Token)> UserAsync(Guid tenantId, string name)
    {
        var email = $"live-{name}-{Guid.NewGuid():N}@e2e.local";
        const string password = "live-event-1234";
        var id = await factory.SeedUserAsync(tenantId, email, password, name);
        return (id, await factory.GetUserTokenAsync(email, password));
    }

    private HubConnection Hub(string token) => new HubConnectionBuilder()
        .WithUrl("http://localhost/hubs/notifications", options =>
        {
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync()).GetProperty("errorCode").GetString();

    private async Task WithActiveModuleAsync(Func<TestModuleRig.Rig, Task> test)
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKey, vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            await test(rig);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerifyKey, null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public Task A_module_event_reaches_the_addressed_users_open_connection_only_and_is_not_stored() =>
        WithActiveModuleAsync(async rig =>
        {
            var (aId, aToken) = await UserAsync(rig.TenantId, "anna");
            var (bId, bToken) = await UserAsync(rig.TenantId, "tom");
            var toA = new TaskCompletionSource<ModuleLiveEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var toB = new TaskCompletionSource<ModuleLiveEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hubA = Hub(aToken);
            await using var hubB = Hub(bToken);
            hubA.On<ModuleLiveEvent>(ModuleLiveEvent.HubMethod, e => toA.TrySetResult(e));
            hubB.On<ModuleLiveEvent>(ModuleLiveEvent.HubMethod, e => toB.TrySetResult(e));
            await hubA.StartAsync();
            await hubB.StartAsync();

            // A stranger id (no user here) is dropped and not counted: the module may address only this tenant's users.
            var sent = await TestJson.Post(rig.Admin, Send,
                new { recipientUserIds = new[] { aId, Guid.NewGuid() }, name = "typing", payload = """{"conversation":"c1","typing":true}""" });
            Assert.Equal(1, sent.GetProperty("delivered").GetInt32());

            var completed = await Task.WhenAny(toA.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            Assert.True(completed == toA.Task, "A's live connection should receive the module's event");
            var received = await toA.Task;
            Assert.Equal("test-module", received.ModuleId);   // stamped by the host, not chosen by the module
            Assert.Equal("typing", received.Name);
            Assert.Equal("""{"conversation":"c1","typing":true}""", received.Payload);

            // B was connected the whole time and is not addressed.
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(toB.Task.IsCompleted, "B was not addressed and must not receive the event");

            // Nothing is stored: no notification (so no phone push, no mail, no signal) for either user.
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            Assert.False(await db.Notifications.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(n => n.RecipientUserId == aId || n.RecipientUserId == bId));
        });

    [Fact]
    public Task An_oversized_payload_is_refused_and_nothing_is_sent() =>
        WithActiveModuleAsync(async rig =>
        {
            var (aId, aToken) = await UserAsync(rig.TenantId, "anna");
            var toA = new TaskCompletionSource<ModuleLiveEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hubA = Hub(aToken);
            hubA.On<ModuleLiveEvent>(ModuleLiveEvent.HubMethod, e => toA.TrySetResult(e));
            await hubA.StartAsync();

            using var refused = await rig.Admin.PostAsJsonAsync(Send, new
            {
                recipientUserIds = new[] { aId },
                name = "typing",
                payload = new string('x', SimplArchive.ModuleAbi.ModuleLiveEvent.MaxPayloadBytes + 1),
            });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);
            Assert.Equal("MODULE_LIVE_EVENT_TOO_LARGE", await ErrorCodeAsync(refused));

            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(toA.Task.IsCompleted, "a refused event must not reach anyone");
        });

    [Fact]
    public Task A_sender_past_its_rate_is_refused() =>
        WithActiveModuleAsync(async rig =>
        {
            var (aId, _) = await UserAsync(rig.TenantId, "anna");

            // Fixed windows: 41 sends span at most one window boundary, so one window holds at least 21 > 20.
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < (2 * SimplArchive.ModuleAbi.ModuleLiveEvent.MaxEventsPerSender) + 1; i++)
            {
                using var response = await rig.Admin.PostAsJsonAsync(Send, new { recipientUserIds = new[] { aId }, name = "typing", payload = "{}" });
                statuses.Add(response.StatusCode);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Assert.Equal("MODULE_LIVE_EVENT_RATE_LIMITED", await ErrorCodeAsync(response));
                    break;
                }
            }

            Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
            // Every window first grants its full budget, so at least that many went through before the refusal.
            Assert.True(statuses.Count(s => s == HttpStatusCode.OK) >= SimplArchive.ModuleAbi.ModuleLiveEvent.MaxEventsPerSender);
        });

    [Fact]
    public Task A_module_whose_principal_is_revoked_may_not_send() =>
        WithActiveModuleAsync(async rig =>
        {
            var (aId, _) = await UserAsync(rig.TenantId, "anna");
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
                var principal = await db.ServiceAccounts.IgnoreQueryFilters(["TenantFilter"])
                    .SingleAsync(s => s.TenantId == rig.TenantId && s.OpenIddictApplicationClientId == "module:test-module");
                principal.IsActive = false;   // the consent withdrawn
                await db.SaveChangesAsync();
            }

            using var refused = await rig.Admin.PostAsJsonAsync(Send, new { recipientUserIds = new[] { aId }, name = "typing", payload = "{}" });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("MODULE_LIVE_EVENT_NOT_PERMITTED", await ErrorCodeAsync(refused));
        });

    [Fact]
    public async Task A_module_not_active_in_the_tenant_cannot_send()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        try
        {
            var (aId, _) = await UserAsync(rig.TenantId, "anna");
            using var refused = await rig.Admin.PostAsJsonAsync(Send, new { recipientUserIds = new[] { aId }, name = "typing", payload = "{}" });
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal("MODULE_NOT_ACTIVE", await ErrorCodeAsync(refused));
        }
        finally
        {
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }
}
