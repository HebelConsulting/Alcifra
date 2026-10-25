namespace SimplArchive.Cli.Infrastructure;

/// <summary>
/// Who this tool is to an installation. The id is the client <see cref="SimplArchive.Api.Provisioning.SaConsoleClientSeeder"/>
/// registers, and the one the device grant names — kept here rather than on the shared flow since 2026-10-25, when
/// the flow moved into ApiClient so sapass could use it under its own id.
/// </summary>
public static class CliIdentity
{
    public const string ClientId = "saconsole";
}
