using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.ApiClient.KeyTest;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

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
/// happen, then a prominent verdict, and below it the report — collapsed by default (ADR 0962), with the archive side
/// behind a switch, ON by default, offered only signed in.
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
        Steps.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSteps));
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

    public bool HasSteps => Steps.Count > 0;

    /// <summary>
    /// The verdict above the report (owner 2026-10-22, ADR 0962): null before a test has finished — the window then
    /// shows only the instructions.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerdict), nameof(Works), nameof(DoesNotWork), nameof(Unfinished),
        nameof(VerdictIcon), nameof(HasVerdictReason), nameof(VerdictSpoken))]
    private KeyTestVerdictLine? _verdict;

    public bool HasVerdict => Verdict is not null;

    public bool Works => Verdict?.Verdict == KeyTestVerdict.Works;

    public bool DoesNotWork => Verdict?.Verdict == KeyTestVerdict.Failed;

    public bool Unfinished => Verdict?.Verdict == KeyTestVerdict.NotCompleted;

    public bool HasVerdictReason => Verdict?.Reason is { Length: > 0 };

    public string VerdictIcon => Verdict?.Verdict switch
    {
        KeyTestVerdict.Works => "mdi-check-circle",
        KeyTestVerdict.Failed => "mdi-close-circle",
        _ => "mdi-alert-circle",
    };

    /// <summary>What a screen reader announces when the verdict appears: the headline and its reason.</summary>
    public string VerdictSpoken => Verdict is { } line ? $"{line.Headline} {line.Reason}".TrimEnd() : string.Empty;

    /// <summary>The report for support, collapsed by default; Copy works either way.</summary>
    [ObservableProperty] private bool _isReportExpanded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private string _reportText = string.Empty;

    public bool HasReport => ReportText.Length > 0;

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

        Verdict = KeyTestVerdictText.Of(report);
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
        Verdict = null;
        _report = null;
        Render();
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
