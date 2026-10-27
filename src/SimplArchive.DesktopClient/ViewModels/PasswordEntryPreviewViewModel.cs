using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>What an eye reveals while it is held: pressed shows, released hides (the core's <see cref="SecretReveal"/>).</summary>
public interface IHoldToReveal
{
    void Press();

    void Release();
}

/// <summary>One run of a find-highlighted text: plain, a hit, or the active hit.</summary>
public sealed record FindRun(string Text, bool IsHit, bool IsActive);

/// <summary>
/// What a secret-field row needs from the window around it (ADR 0968): the sensitive clipboard, the plain one, the
/// status line, the screen protection while a secret is shown, and the clock the 30 s clearing runs on.
/// </summary>
public sealed record SecretFieldServices(ISecretClipboard Clipboard, Func<string, Task> CopyPlain, IStatusReporter Status, SecretScreens Screens, TimeProvider? Time = null);

/// <summary>
/// One field of an opened password entry as the preview draws it (ADR 0968), over the core's <see cref="SecretField"/>:
/// the label, the value or the mask, the eye that shows a secret ONLY WHILE HELD (and keeps the window out of capture
/// meanwhile), the copy button, and the find runs of what is visible. A hidden secret's value is never searched.
/// </summary>
public sealed partial class SecretFieldViewModel : ObservableObject, IHoldToReveal
{
    private readonly SecretField _field;
    private readonly SecretFieldServices _services;
    private IDisposable? _protection;
    private IReadOnlyList<int> _labelMatches = [];
    private IReadOnlyList<int> _valueMatches = [];
    private int _queryLength;
    private int _activeLabel;
    private int _activeValue;

    public SecretFieldViewModel(PasswordEntryRow row, SecretFieldServices services)
    {
        (_field, _services) = (new SecretField(row, services.Clipboard, services.Time), services);
        _field.Reveal.Changed += Revealed;
    }

    public string Label => _field.Row.Label;

    public bool IsSecret => _field.Row.IsSecret;

    public bool IsMultiline => _field.Row.IsMultiline;

    /// <summary>The value opens in the browser (the URL's row, core #1949).</summary>
    public bool IsLink => _field.Row.Link is not null;

    [RelayCommand]
    private void OpenLink()
    {
        if (_field.Row.Link is { } link)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link.ToString()) { UseShellExecute = true });
        }
    }

    /// <summary>What is drawn now: the value, or the mask while a secret is not held.</summary>
    public string Display => _field.Display;

    public bool IsRevealed => _field.Reveal.IsRevealed;

    public string EyeIcon => IsRevealed ? "mdi-eye-outline" : "mdi-eye-off-outline";

    public IReadOnlyList<FindRun> LabelRuns => Runs(Label, _labelMatches, _activeLabel);

    public IReadOnlyList<FindRun> ValueRuns => Runs(Display, IsSecret ? [] : _valueMatches, _activeValue);

    /// <summary>The eye pressed: the secret is shown, and the window kept out of screen capture.</summary>
    public void Press() => _field.Reveal.Press();

    /// <summary>Let go, pointer gone, focus lost: hidden again.</summary>
    public void Release() => _field.Reveal.Release();

    /// <summary>Copies the value: a secret marked sensitive and cleared after 30 s if the clipboard still holds it.</summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        if (!IsSecret)
        {
            await _field.CopyAsync(_services.CopyPlain);
            return;
        }

        var clearing = _field.CopyAsync(_services.CopyPlain);
        _services.Status.Report(Strings.Get("PwCopiedSecret"));
        await clearing;
    }

    /// <summary>Copies and hands back the clearing, for a test that moves the clock.</summary>
    internal Task<bool> CopyForTestAsync() => _field.CopyAsync(_services.CopyPlain);

    /// <summary>Counts this row's matches of <paramref name="query"/>: the label always, the value only where it is plain.</summary>
    internal int ApplyFind(string query)
    {
        _queryLength = query.Length;
        _labelMatches = TextFind.Matches(Label, query);
        _valueMatches = IsSecret ? [] : TextFind.Matches(_field.Row.Value, query);
        (_activeLabel, _activeValue) = (0, 0);
        RunsChanged();
        return _labelMatches.Count + _valueMatches.Count;
    }

    /// <summary>Marks the <paramref name="ordinal"/>-th match of this row (0-based; out of range: none) as the active one.</summary>
    internal void Activate(int ordinal)
    {
        (_activeLabel, _activeValue) = ordinal < 0 ? (0, 0)
            : ordinal < _labelMatches.Count ? (ordinal + 1, 0)
            : ordinal < _labelMatches.Count + _valueMatches.Count ? (0, ordinal - _labelMatches.Count + 1)
            : (0, 0);
        RunsChanged();
    }

    /// <summary>The entry closed: nothing stays revealed and the protection it held lifts.</summary>
    internal void Close()
    {
        _field.Hide();
        _protection?.Dispose();
        _protection = null;
    }

    private void Revealed()
    {
        if (IsRevealed && IsSecret)
        {
            _protection ??= _services.Screens.Showing();
        }
        else
        {
            _protection?.Dispose();
            _protection = null;
        }

        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(IsRevealed));
        OnPropertyChanged(nameof(EyeIcon));
        RunsChanged();
    }

    private void RunsChanged()
    {
        OnPropertyChanged(nameof(LabelRuns));
        OnPropertyChanged(nameof(ValueRuns));
    }

    private IReadOnlyList<FindRun> Runs(string text, IReadOnlyList<int> matches, int active) => FindRuns.Of(text, matches, _queryLength, active);
}

/// <summary>Find runs from the shared arithmetic (<see cref="TextFind"/>), for the rows and the entry's name alike.</summary>
internal static class FindRuns
{
    public static IReadOnlyList<FindRun> Of(string text, IReadOnlyList<int> matches, int queryLength, int active) =>
        [.. TextFind.Segments(text, matches, queryLength, active).Select(s => new FindRun(s.Segment, s.IsHit, s.IsActive))];
}

/// <summary>
/// An opened password entry in the PREVIEW pane (ADR 0968): its name, the "Exposed since" notice, and its fields in the
/// core's order (<see cref="PasswordEntryFieldOrder"/>), held in memory only. The preview's find searches what is
/// visible — the name, every label, the plain values — and never a secret's value, hidden or held.
/// </summary>
public sealed partial class PasswordEntryPreviewViewModel : ObservableObject
{
    private IReadOnlyList<int> _nameMatches = [];
    private int _nameLength;
    private int _activeName;

    public PasswordEntryPreviewViewModel(string name, PasswordEntryContent content, PasswordEntryInfo? entry, SecretFieldServices services, bool isReadOnly)
    {
        Name = name;
        IsReadOnly = isReadOnly;
        Notice = entry is not null ? PasswordNotices.For(entry)?.Text : null;
        foreach (var row in PasswordEntryRows.Of(content))
        {
            Rows.Add(new SecretFieldViewModel(row, services));
        }
    }

    public string Name { get; }

    /// <summary>The name as drawn, with its find hits.</summary>
    public IReadOnlyList<FindRun> NameRuns => FindRuns.Of(Name, _nameMatches, _nameLength, _activeName);

    /// <summary>Read-only: an entry met as a document in the Repositories tree (changing it is the Passwords tab's).</summary>
    public bool IsReadOnly { get; }

    /// <summary>"Exposed since …: change this password", or null.</summary>
    public string? Notice { get; }

    public bool HasNotice => Notice is not null;

    public ObservableCollection<SecretFieldViewModel> Rows { get; } = [];

    private List<(SecretFieldViewModel Row, int Count)> _counts = [];

    /// <summary>Applies the find query to every row; answers the number of matches.</summary>
    public int ApplyFind(string query)
    {
        (_nameMatches, _nameLength, _activeName) = (TextFind.Matches(Name, query), query.Length, 0);
        OnPropertyChanged(nameof(NameRuns));
        _counts = [.. Rows.Select(r => (r, r.ApplyFind(query)))];
        return _nameMatches.Count + _counts.Sum(c => c.Count);
    }

    /// <summary>Marks the <paramref name="index"/>-th match overall (0-based) as the active one.</summary>
    public void Activate(int index)
    {
        _activeName = index >= 0 && index < _nameMatches.Count ? index + 1 : 0;
        OnPropertyChanged(nameof(NameRuns));
        var before = _nameMatches.Count;
        foreach (var (row, count) in _counts)
        {
            row.Activate(index >= before && index < before + count ? index - before : -1);
            before += count;
        }
    }

    /// <summary>The window lost focus or the pane changed: every held secret is hidden again.</summary>
    public void HideAll()
    {
        foreach (var row in Rows)
        {
            row.Release();
        }
    }

    /// <summary>The entry leaves the screen: hidden, protection lifted, its rows let go of.</summary>
    public void Close()
    {
        foreach (var row in Rows)
        {
            row.Close();
        }

        Rows.Clear();
        _counts = [];
    }
}
