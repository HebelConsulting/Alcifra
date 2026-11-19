#if MACOS
using AVFoundation;
using Alcifra.Messenger.Client.Display;
using Foundation;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// Answers the system player's requests for an asset from its bytes (#2036), held in memory or read from storage a range at
/// a time (<see cref="IMediaBytes"/>): what it is (its uniform type, its length, that any range may be asked for) and each
/// range it asks, a seek's and a "to the end" alike, cut into bounded answers by <see cref="MemoryRanges"/>. A request the
/// player gives up (it seeked elsewhere) stops being answered, and so stops being fetched. The phone's loader, line for
/// line (Messenger.Views, Mobile ADR 0030): the arithmetic is the Messenger's and shared; the delegate is this few lines on
/// each platform.
/// </summary>
internal sealed class MemoryAssetLoader(IMediaBytes content, string identifier) : AVAssetResourceLoaderDelegate
{
    /// <summary>Any address of a scheme the system does not know sends every request to the loader; the name says what it is.</summary>
    public static readonly NSUrl Address = new("alcifra-memory://player/content");

    public override bool ShouldWaitForLoadingOfRequestedResource(AVAssetResourceLoader resourceLoader, AVAssetResourceLoadingRequest loadingRequest)
    {
        DesktopLog.Debug("Memory player: asked for {Offset}+{Length} (to the end: {ToEnd}, information: {Information})",
            loadingRequest.DataRequest?.RequestedOffset, loadingRequest.DataRequest?.RequestedLength,
            loadingRequest.DataRequest?.RequestsAllDataToEndOfResource, loadingRequest.ContentInformationRequest is not null);
        if (loadingRequest.ContentInformationRequest is { } information)
        {
            information.ContentType = identifier;
            information.ContentLength = content.Length;
            information.ByteRangeAccessSupported = true;
        }

        if (loadingRequest.DataRequest is not { } data)
        {
            loadingRequest.FinishLoading();
            return true;
        }

        // Answered off the loader's queue, so a long "to the end" does not hold up the next request (a seek) behind it.
        var (offset, length, current, toEnd) = (data.RequestedOffset, (long)data.RequestedLength, data.CurrentOffset, data.RequestsAllDataToEndOfResource);
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var range in MemoryRanges.Answer(content.Length, offset, length, current, toEnd))
                {
                    if (loadingRequest.IsCancelled)
                    {
                        return;
                    }

                    data.Respond(NSData.FromArray(await content.ReadAsync(range.Offset, range.Length, CancellationToken.None)));
                }

                if (!loadingRequest.IsCancelled)
                {
                    loadingRequest.FinishLoading();
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                // A range storage did not answer: the player shows its own failure, and the next seek asks again.
                DesktopLog.Warn("Memory player: a range could not be read: {Reason}", e.Message);
                loadingRequest.FinishLoadingWithError(new NSError(NSError.CocoaErrorDomain, (nint)NSCocoaError.FileReadUnknown));
            }
        });
        return true;
    }
}
#endif
