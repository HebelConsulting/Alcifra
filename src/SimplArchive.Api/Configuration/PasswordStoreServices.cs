using SimplArchive.Api.PasswordStores;

namespace SimplArchive.Api.Configuration;

/// <summary>The password store's services (ADR 0964), registered in one call so Program.cs gains a line, not seven.</summary>
public static class PasswordStoreServices
{
    public static IServiceCollection AddPasswordStores(this IServiceCollection services) =>
        services
            .AddSingleton<SealedRecipientCache>()
            .AddScoped<PasswordStoreTree>()
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
