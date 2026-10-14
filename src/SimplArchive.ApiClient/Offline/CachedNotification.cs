namespace SimplArchive.ApiClient.Offline;

/// <summary>A notification as the server listed it, to be read offline (the device copy keeps the list beside the
/// exports, <see cref="ArchiveMerger.PutNotifications"/>).</summary>
public sealed record CachedNotification(Guid Id, string Type, string Title, string Body, Guid? DocumentId, DateTimeOffset CreatedAt, bool IsRead);
