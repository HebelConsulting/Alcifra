using OpenIddict.Abstractions;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// Registers every public client an installation offers: the desktop, the mobile app and its autofill
/// extension, and the two CLIs.
/// </summary>
/// <remarks>
/// The sequence in one place rather than four calls in <c>Program.cs</c>, which the 1000-line guard had reached
/// exactly when sapass's client joined it. Each seeder owns its own descriptor and its own healing; this only says
/// WHICH clients exist and in what order they are ensured.
/// </remarks>
public static class PublicClientsSeed
{
    public static async Task SeedAsync(IOpenIddictApplicationManager applications, CancellationToken cancellationToken = default)
    {
        await DesktopClientSeeder.SeedAsync(applications, cancellationToken);
        await MobileClientSeeder.SeedAsync(applications, cancellationToken);
        await AutofillClientSeeder.SeedAsync(applications, cancellationToken);
        await SaConsoleClientSeeder.SeedAsync(applications, cancellationToken);
        await SapassClientSeeder.SeedAsync(applications, cancellationToken);
    }
}
