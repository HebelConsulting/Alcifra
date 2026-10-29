namespace Alcifra.Localization;

/// <summary>
/// Names already written into installations and users' machines, which keep the spelling they were written with
/// whatever the product is called (#1117 slice G, phase 5): renaming one orphans what was stored under it.
/// </summary>
/// <remarks>
/// <para>
/// One file so the code sweep that renames <c>SimplArchive</c> to <c>Alcifra</c> (phase 5) can exclude exactly this and
/// nothing else. A new persisted name that must survive a rename belongs here, never inline.
/// </para>
/// <list type="bullet">
/// <item><see cref="DataProtectionApplication"/>: ASP.NET Core data protection isolates its keys by this name; a new one
/// unprotects nothing written before (sign-in cookies, antiforgery, protected settings).</item>
/// <item><see cref="DesktopAppData"/>: the desktop's settings folder (layout, tree, servers, autofill, logs, the browser
/// host's launcher) under the OS's application-data folders, and on Windows the local folders' base.</item>
/// <item><see cref="DesktopKeychainService"/>: the OS keychain service the desktop's secrets are stored under.</item>
/// <item><see cref="DesktopTempFolder"/>: where opened documents are staged; old copies are cleaned from here.</item>
/// <item><see cref="DesktopUserAgent"/>: what the update check sends; a log filter may match it.</item>
/// <item><see cref="EarlierProduct"/>: the product's name before the rename, which healers and the home-folder move
/// recognise in data written then.</item>
/// </list>
/// </remarks>
public static class PersistedNames
{
    public const string DataProtectionApplication = "SimplArchive";

    public const string DesktopAppData = "SimplArchive";

    public const string DesktopKeychainService = "SimplArchive";

    public const string DesktopTempFolder = "SimplArchive";

    public const string DesktopUserAgent = "SimplArchive-DesktopClient";

    public const string EarlierProduct = "SimplArchive";
}
