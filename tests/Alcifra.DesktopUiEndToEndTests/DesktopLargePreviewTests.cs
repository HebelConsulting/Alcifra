using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;

namespace Alcifra.UiEndToEndTests;

// A version over 10 MB waits for Open on the desktop too (ADR 1048), and the Toggle field reads as its slider (ADR 1047).
public class DesktopLargePreviewTests
{
    private const long Large = 11L * 1024 * 1024;

    private static Preview Version(long? size) => new(null, false, null, null, null, ".pdf", SizeBytes: size);

    private static PreviewViewModel Pane() => new(new TestShell()) { Api = new AlcifraApiClient("token") };

    [Fact]
    public async Task A_large_version_shows_the_message_and_the_button_and_a_small_one_does_not()
    {
        var vm = Pane();

        await vm.RenderAsync(Version(Large));
        Assert.True(vm.CanOpenLarge);
        Assert.Equal(Strings.Get("PvLargeMessage"), vm.PreviewPlaceholder);

        await vm.RenderAsync(Version(1024));
        Assert.False(vm.CanOpenLarge);
        Assert.NotEqual(Strings.Get("PvLargeMessage"), vm.PreviewPlaceholder);
    }

    [Fact]
    public async Task Open_lets_that_preview_through_and_the_same_version_selected_again_asks_again()
    {
        var vm = Pane();

        await vm.RenderAsync(Version(Large));
        await vm.OpenLargeCommand.ExecuteAsync(null);
        Assert.False(vm.CanOpenLarge);
        Assert.NotEqual(Strings.Get("PvLargeMessage"), vm.PreviewPlaceholder);

        await vm.RenderAsync(Version(Large));
        Assert.True(vm.CanOpenLarge);
    }

    [Theory]
    [InlineData(".mp4", 1024L)]
    [InlineData(".mov", 11L * 1024 * 1024)]
    [InlineData(".avi", null)]
    public async Task A_video_fetches_nothing_and_says_to_open_it_elsewhere(string extension, long? size)
    {
        var vm = Pane();

        // No preview address would be reachable here: a fetch would land in the failure message instead.
        await vm.RenderAsync(new Preview("http://nothing.test/clip", false, null, null, null, extension, SizeBytes: size));

        Assert.Equal(Strings.Get("PvMediaUseApp"), vm.PreviewPlaceholder);
        Assert.False(vm.CanOpenLarge);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void A_toggle_field_edits_as_the_slider_and_stores_true_or_false(string stored, bool on)
    {
        var field = MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Published", "Toggle", IsRequired: false, IsList: false), [stored]);

        Assert.True(field.IsToggle);
        Assert.False(field.IsBoolean);
        Assert.False(field.IsSingleLine);
        Assert.Equal(on, field.BoolValue);
        Assert.Equal([stored], field.ToValues());
    }

    [Fact]
    public void An_unset_toggle_reads_as_disabled()
    {
        var field = MaskFieldEditViewModel.Create(new MasksClient.MaskFieldInfo(Guid.NewGuid(), "Published", "Toggle", IsRequired: false, IsList: false), []);

        Assert.False(field.BoolValue);
        Assert.Equal(["false"], field.ToValues());
    }
}
