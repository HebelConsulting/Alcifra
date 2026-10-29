using Alcifra.Api.PasswordStores;

namespace Alcifra.Api.Configuration;

/// <summary>The password store's services (ADR 0964), registered in one call so Program.cs gains a line, not seven.</summary>
public static class PasswordStoreServices
{
    public static IServiceCollection AddPasswordStores(this IServiceCollection services) =>
        services
            .AddSingleton<SealedRecipientCache>()
            .AddScoped<PasswordStoreTree>()
            .AddScoped<Alcifra.Api.Audience.ContentAudience>()
            .AddScoped<Alcifra.Api.Audience.EncryptedForCaller>()
            .AddScoped<Alcifra.Api.Audience.ContentAudienceBackfill>()
            .AddScoped<PasswordStoreReaders>()
            .AddScoped<PasswordStoreSealing>()
            .AddScoped<PasswordStoreResourceBuilder>()
            .AddScoped<PasswordEntryCreation>()
            .AddScoped<PasswordEntryVersions>()
            .AddScoped<MyPasswordsProvisioner>()
            .AddScoped<PasswordEntryHistory>()
            .AddSingleton<PasswordHistorySweepWorker>()
            .AddHostedService(sp => sp.GetRequiredService<PasswordHistorySweepWorker>());
}
