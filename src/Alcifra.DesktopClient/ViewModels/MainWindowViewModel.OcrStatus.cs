using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

// The OCR verdict line (#999): the persisted detector verdict rendered where the user who wonders why their scan
// is unsearchable is actually looking (ADR 0626's principle in the pane). Its override, Make searchable, lives
// only in the Check-out tab's ribbon (owner decision, ADR 0970).
public partial class MainWindowViewModel
{
    /// <summary>The verdict line's localized text, empty when the version is unjudged.</summary>
    public string SysOcrVerdictText { get; private set; } = string.Empty;

    // The verdict alone: Make searchable lives only in the Check-out tab's ribbon (ADR 0970).
    public bool HasOcrStatusLine => SysOcrVerdictText.Length > 0;

    internal void SetOcrStatus(string? verdict)
    {
        SysOcrVerdictText = Alcifra.Presentation.DocumentStateText.OcrVerdictKey(verdict) is { } key ? Strings.Get(key) : string.Empty;
        OnPropertyChanged(nameof(SysOcrVerdictText));
        OnPropertyChanged(nameof(HasOcrStatusLine));
    }
}
