namespace SimplArchive.Domain.Tenants;

/// <summary>How a phone's offline cache unlocks (#1742, owner decisions 2026-10-13): the modes exclude each other, and the
/// tenant either fixes one or leaves the choice to the user.</summary>
public enum OfflineCacheUnlockMode
{
    /// <summary>The user chooses in the phone's Settings.</summary>
    UserChooses = 0,

    /// <summary>Key and PIN once; the cache stays open until the app closes, the user locks it, or the timer ends.</summary>
    Session = 1,

    /// <summary>Key and PIN for every cached document.</summary>
    Document = 2,
}

/// <summary>How a phone's offline cache follows the server (#1742): exclusive, fixed by the tenant or left to the user.</summary>
public enum OfflineCacheRefreshMode
{
    /// <summary>The user chooses in the phone's Settings.</summary>
    UserChooses = 0,

    /// <summary>Refreshed when the app opens or resumes online, like a subscription.</summary>
    KeepUpdated = 1,

    /// <summary>A copy whose document has a newer version is removed for good.</summary>
    RemoveWhenNewer = 2,
}
