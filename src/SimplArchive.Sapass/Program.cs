namespace SimplArchive.Sapass;

/// <summary>
/// The entry point. A named class in the tool's OWN namespace rather than top-level statements: top-level code
/// generates a global <c>Program</c>, which collides with the Api's in any project that references both — the E2E
/// suite hosts the Api's <c>Program</c> and drives this tool's types.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) => SapassApp.Run(args);
}
