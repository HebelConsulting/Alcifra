using SimplArchive.Localization;

namespace SimplArchive.UnitTests;

// Names already written into installations and users' machines (#1117 slice G, phase 5). A new value orphans what was
// stored under the old one: a data-protection application name unprotects nothing written before, a keychain service
// finds no secrets, an app-data folder starts empty. Pinned here, so the phase-5 sweep (which rewrites "SimplArchive"
// everywhere but PersistedNames.cs) turns this test red if it ever reaches that file.
public sealed class PersistedNamesTests
{
    [Fact]
    public void The_persisted_names_keep_the_spelling_they_were_written_with()
    {
        Assert.Equal("SimplArchive", PersistedNames.DataProtectionApplication);
        Assert.Equal("SimplArchive", PersistedNames.DesktopAppData);
        Assert.Equal("SimplArchive", PersistedNames.DesktopKeychainService);
        Assert.Equal("SimplArchive", PersistedNames.DesktopTempFolder);
        Assert.Equal("SimplArchive-DesktopClient", PersistedNames.DesktopUserAgent);
        Assert.Equal("SimplArchive", PersistedNames.EarlierProduct);
    }
}
