using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// One document a reference field's picker offers (#2214): its name, and its picture once read. The picture is read after
/// the row is shown, so a list of names never waits for images, and a document whose preview is not a picture keeps its
/// name alone.
/// </summary>
public sealed partial class ReferenceChoice(MasksClient.ReferenceTarget target) : ObservableObject
{
    public MasksClient.ReferenceTarget Target { get; } = target;

    public string Name => Target.Name;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasPicture))] private Bitmap? _picture;

    public bool HasPicture => Picture is not null;

    /// <summary>What the type-ahead's text box shows once this is picked.</summary>
    public override string ToString() => Name;

    /// <summary>Reads the picture with <paramref name="read"/>; one that does not decode as an image leaves none.</summary>
    public async Task LoadPictureAsync(Func<MasksClient.ReferenceTarget, CancellationToken, Task<byte[]?>> read, CancellationToken cancellationToken = default)
    {
        if (await read(Target, cancellationToken) is not { Length: > 0 } bytes)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            Picture = Bitmap.DecodeToWidth(stream, 96);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            DesktopLog.Debug("Reference picker: the preview of {Target} is not a picture ({Reason})", Target.Id, e.Message);
        }
    }
}
