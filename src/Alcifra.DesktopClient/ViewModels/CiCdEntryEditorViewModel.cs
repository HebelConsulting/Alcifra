using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.ApiClient.CiCd;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>What one key of a CI/CD entry is (ADR 1084).</summary>
public enum CiCdKeyKind
{
    /// <summary>A sealed value typed as text.</summary>
    Secret,

    /// <summary>A sealed file, kept as its raw bytes and sent base64-encoded.</summary>
    File,

    /// <summary>A plain value: not secret, so not sealed.</summary>
    Variable,
}

/// <summary>One key in the CI/CD editor: its name, what it is, and its value or file.</summary>
public sealed partial class CiCdKeyRowViewModel : ObservableObject
{
    private readonly Action _changed;

    public CiCdKeyRowViewModel(Action changed, string? name = null, CiCdKeyKind kind = CiCdKeyKind.Secret, string? text = null, byte[]? file = null, string? fileName = null)
    {
        _changed = changed;
        (_name, _kind, _text, File, _fileName) = (name ?? string.Empty, kind, text ?? string.Empty, file, fileName);
    }

    /// <summary>The kinds as the dropdown names them, in <see cref="CiCdKeyKind"/>'s order.</summary>
    public static IReadOnlyList<string> KindLabels { get; } =
        [Strings.Get("CiCdKindSecret"), Strings.Get("CiCdKindFile"), Strings.Get("CiCdKindVariable")];

    /// <summary>The dropdown's position for <see cref="Kind"/>.</summary>
    public int KindIndex
    {
        get => (int)Kind;
        set => Kind = (CiCdKeyKind)value;
    }

    [ObservableProperty] private string _name;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsFile), nameof(IsSecretText), nameof(IsVariable), nameof(KindIndex))] private CiCdKeyKind _kind;

    [ObservableProperty] private string _text;

    /// <summary>The file chosen or dropped, as its raw bytes.</summary>
    public byte[]? File { get; private set; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(FileLabel))] private string? _fileName;

    public bool IsFile => Kind == CiCdKeyKind.File;

    /// <summary>A secret typed as text: masked while typed.</summary>
    public bool IsSecretText => Kind == CiCdKeyKind.Secret;

    /// <summary>A variable: not secret, so typed in the clear.</summary>
    public bool IsVariable => Kind == CiCdKeyKind.Variable;

    /// <summary>What the row says about its file: its name and size, or that none is chosen yet.</summary>
    public string FileLabel => File is { } bytes
        ? $"{FileName ?? Strings.Get("CiCdStoredFile")} ({HumanFileSize.Format(bytes.Length)})"
        : Strings.Get("CiCdNoFile");

    /// <summary>Takes a file's bytes (chosen or dropped) as this row's value.</summary>
    public void UseFile(string fileName, byte[] bytes)
    {
        File = bytes;
        FileName = fileName;
        Kind = CiCdKeyKind.File;
        _changed();
    }

    partial void OnNameChanged(string value) => _changed();

    partial void OnKindChanged(CiCdKeyKind value) => _changed();

    partial void OnTextChanged(string value) => _changed();
}

/// <summary>
/// The CI/CD secret editor (ADR 1084), the desktop's: its name when new, its targets one per line, and its keys, each a
/// secret typed as text, a file picked or dropped, or a plain variable. Checked as it is typed against the rules the server
/// and apass hold too, and saved as one sealed version.
/// </summary>
public sealed partial class CiCdEntryEditorViewModel : ObservableObject
{
    private readonly Func<string, CiCdEntryContent, Task> _save;

    /// <param name="opened">The entry being edited, opened; null for a new one.</param>
    /// <param name="save">Seals and files the content under the name; throws with what the server refused.</param>
    public CiCdEntryEditorViewModel(OpenedCiCdEntry? opened, Func<string, CiCdEntryContent, Task> save)
    {
        _save = save;
        IsNew = opened is null;
        _name = opened?.Entry.Name ?? string.Empty;
        _targetsText = string.Join(Environment.NewLine, opened?.Content.Targets ?? []);
        foreach (var secret in opened?.Content.Secrets ?? [])
        {
            Keys.Add(secret.IsFile
                ? new CiCdKeyRowViewModel(Validate, secret.Name, CiCdKeyKind.File, file: secret.Value)
                : new CiCdKeyRowViewModel(Validate, secret.Name, CiCdKeyKind.Secret, Encoding.UTF8.GetString(secret.Value)));
        }

        foreach (var variable in opened?.Content.Variables ?? [])
        {
            Keys.Add(new CiCdKeyRowViewModel(Validate, variable.Name, CiCdKeyKind.Variable, variable.Value));
        }

        if (Keys.Count == 0)
        {
            // One key and value by default (owner, 2026-11-08); more are added.
            Keys.Add(new CiCdKeyRowViewModel(Validate));
        }

        Validate();
    }

    public bool IsNew { get; }

    public string Title => Strings.Get(IsNew ? "CiCdNewTitle" : "CiCdEditTitle");

    [ObservableProperty] private string _name;

    [ObservableProperty] private string _targetsText;

    public ObservableCollection<CiCdKeyRowViewModel> Keys { get; } = [];

    /// <summary>What stops a save, one sentence per problem; empty when it may be saved.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasProblems))] private string _problems = string.Empty;

    public bool HasProblems => Problems.Length > 0;

    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _canSave;

    [ObservableProperty] private bool _busy;

    /// <summary>Set by the dialog: closes it once the entry is saved.</summary>
    public Action? Saved { get; set; }

    [RelayCommand]
    private void AddKey() => Keys.Add(new CiCdKeyRowViewModel(Validate));

    [RelayCommand]
    private void RemoveKey(CiCdKeyRowViewModel row)
    {
        Keys.Remove(row);
        Validate();
    }

    /// <summary>A file dropped on the editor, not on a row: a new file key named after it, which the person may rename.</summary>
    public void AddFile(string fileName, byte[] bytes)
    {
        var row = new CiCdKeyRowViewModel(Validate, KeyNameFrom(fileName), CiCdKeyKind.File);
        Keys.Add(row);
        row.UseFile(fileName, bytes);
    }

    /// <summary>The content the form states.</summary>
    public CiCdEntryContent Content() => new(
        [.. TargetsText.Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0)],
        [.. Keys.Where(k => k.Kind != CiCdKeyKind.Variable).Select(k => new CiCdSecretValue(
            k.Name.Trim(), k.IsFile ? k.File ?? [] : Encoding.UTF8.GetBytes(k.Text), k.IsFile))],
        [.. Keys.Where(k => k.Kind == CiCdKeyKind.Variable).Select(k => new CiCdVariable(k.Name.Trim(), k.Text))]);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Busy = true;
        try
        {
            await _save(Name.Trim(), Content());
            Saved?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or CiCdEntryInvalidException
            or ApiClient.ApiActionException or ApiClient.PasswordStore.PasswordEntryChangedException or ApiClient.DocumentNameTakenException)
        {
            Problems = e is ApiClient.PasswordStore.PasswordEntryChangedException ? Strings.Get("CiCdChangedMeanwhile") : e.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    partial void OnNameChanged(string value) => Validate();

    partial void OnTargetsTextChanged(string value) => Validate();

    private void Validate()
    {
        var content = Content();
        var sentences = content.Problems.Select(p => Strings.Get($"CiCdProblem{p}")).ToList();
        if (Name.Trim().Length == 0)
        {
            sentences.Insert(0, Strings.Get("CiCdProblemNoName"));
        }

        if (Keys.Any(k => k.IsFile && k.File is null))
        {
            sentences.Add(Strings.Get("CiCdProblemNoFile"));
        }

        if (content.Targets.Count == 0)
        {
            sentences.Add(Strings.Get("CiCdProblemNoTarget"));
        }

        Problems = string.Join(Environment.NewLine, sentences);
        CanSave = sentences.Count == 0 && Keys.Count > 0;
    }

    // "Developer ID.p12" → "DEVELOPER_ID_P12": a name the rules accept, which the person may change.
    internal static string KeyNameFrom(string fileName)
    {
        var name = new string([.. fileName.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]).Trim('_');
        return name.Length == 0 || char.IsAsciiDigit(name[0]) ? $"FILE_{name}" : name;
    }
}
