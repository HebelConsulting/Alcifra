using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.ApiClient.KeyTest;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>One step of the test as the window lists it.</summary>
public sealed class KeyTestStepRow(KeyTestStep step)
{
    // The key is named once, on the step that reads it, rather than on every row — the report text names it on
    // every step, where a line is read out of context.
    public string Title => step is { Kind: KeyTestStepKind.ReadKey, Key: { Length: > 0 } key }
        ? $"{Strings.Get($"KtStep{step.Kind}")} · {key}"
        : Strings.Get($"KtStep{step.Kind}");

    public string Detail => step.ErrorCode is { Length: > 0 } code ? $"{step.Finding}\n{code}" : step.Finding;

    public string Icon => step.Outcome switch
    {
        KeyTestOutcome.Passed => "mdi-check-circle",
        KeyTestOutcome.Failed => "mdi-close-circle",
        _ => "mdi-minus-circle-outline",
    };

    public string IconColour => step.Outcome switch
    {
        KeyTestOutcome.Passed => "#2e7d32",
        KeyTestOutcome.Failed => "#c62828",
        _ => "#8a8a8a",
    };
}

/// <summary>
/// <i>Test the hardware key</i> (core #1864, ADR 0955): runs <see cref="HardwareKeyTest"/>, lists its steps as they
/// happen, and shows the report — with the archive side behind a switch, ON by default, offered only signed in.
/// </summary>
public sealed partial class KeyTestViewModel : ObservableObject
{
    private readonly HardwareKeyTest _test;
    private KeyTestReport? _report;

    /// <param name="test">The test, built with what the surroundings know (the archive side when signed in).</param>
    /// <param name="signedIn">Whether there is an archive side to offer — false on the logon window.</param>
    public KeyTestViewModel(HardwareKeyTest test, bool signedIn)
    {
        _test = test;
        CanIncludeArchive = signedIn;
        IncludeArchive = signedIn;
        Verdict = Strings.Get("KtNotRun");
    }

    /// <summary>Puts text on the clipboard — the view's, which does not exist when this is built (ADR 0730).</summary>
    /// <remarks>
    /// Left a settable callback: a forgotten one leaves the Copy button doing nothing visibly, which is loud.
    /// </remarks>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public ObservableCollection<KeyTestStepRow> Steps { get; } = [];

    public bool CanIncludeArchive { get; }

    [ObservableProperty] private bool _includeArchive;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _verdict;

    [ObservableProperty] private string _verdictColour = "#8a8a8a";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string _reportText = string.Empty;

    [ObservableProperty] private string _copyStatus = string.Empty;

    /// <summary>Shows a finished report — the run's, or a sample for the headless render.</summary>
    public void Show(KeyTestReport report)
    {
        _report = report;
        Steps.Clear();
        foreach (var step in report.Steps)
        {
            Steps.Add(new KeyTestStepRow(step));
        }

        (Verdict, VerdictColour) = report.Verdict switch
        {
            KeyTestVerdict.Works => (Strings.Get("KtWorks"), "#2e7d32"),
            KeyTestVerdict.Failed => (string.Format(Strings.Get("KtFailedAt"), Strings.Get($"KtStep{report.FirstFailure!.Kind}")), "#c62828"),
            _ => (Strings.Get("KtNotCompleted"), "#b26a00"),
        };
        Render();
    }

    partial void OnIncludeArchiveChanged(bool value) => Render();

    private void Render() =>
        ReportText = _report is null ? string.Empty : KeyTestReportText.Render(_report, IncludeArchive && CanIncludeArchive);

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        CopyStatus = string.Empty;
        Steps.Clear();
        Verdict = Strings.Get("KtRunning");
        VerdictColour = "#8a8a8a";
        try
        {
            // On the UI thread, as the document path's card reads are: the PIN dialog is awaited here, and the card
            // calls between are short. Each step is listed as it is recorded.
            Show(await _test.RunAsync(step => Steps.Add(new KeyTestStepRow(step))));
        }
        finally
        {
            IsRunning = false;
        }
    }

    private bool CanCopy() => ReportText.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        if (CopyToClipboard is { } copy)
        {
            await copy(ReportText);
            CopyStatus = Strings.Get("KtCopied");
        }
    }
}
