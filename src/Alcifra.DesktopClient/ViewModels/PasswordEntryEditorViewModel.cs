using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// A secret text box's eye in the form (ADR 1012, the core's <see cref="TimedSecretReveal"/>): nobody can hold the eye and
/// type, so a click shows the value for 15 s, each keystroke in the box restarts that time, and a second click, Save,
/// Cancel or the form closing hides it at once. The window is kept out of screen capture while it shows. The read view
/// keeps hold-to-reveal (ADR 0968).
/// </summary>
public sealed partial class SecretBoxReveal : ObservableObject
{
    private readonly SecretScreens _screens;
    private readonly TimedSecretReveal _reveal = new();
    private IDisposable? _protection;

    public SecretBoxReveal(SecretScreens screens)
    {
        _screens = screens;
        // The time running out raises this on a timer thread; the box and the screen protection change on the UI thread.
        _reveal.Changed += () => OnUi(Redraw);
    }

    private void Redraw()
    {
        if (_reveal.IsRevealed)
        {
            _protection ??= _screens.Showing();
        }
        else
        {
            _protection?.Dispose();
            _protection = null;
        }

        OnPropertyChanged(nameof(IsRevealed));
        OnPropertyChanged(nameof(PasswordChar));
        OnPropertyChanged(nameof(EyeIcon));
    }

    internal static void OnUi(Action action)
    {
        if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(action);
        }
    }

    public bool IsRevealed => _reveal.IsRevealed;

    /// <summary>The box's mask character: none while held.</summary>
    public char PasswordChar => IsRevealed ? '\0' : '•';

    public string EyeIcon => IsRevealed ? "mdi-eye-outline" : "mdi-eye-off-outline";

    /// <summary>The eye's click: shows for 15 s, or hides at once when shown.</summary>
    [RelayCommand]
    private void Toggle() => _reveal.Toggle();

    /// <summary>A keystroke in the box: while shown, the 15 s start again.</summary>
    public void Typed() => _reveal.Typed();

    /// <summary>Save, Cancel, the form closing or the window losing focus.</summary>
    public void Hide() => _reveal.Hide();
}

/// <summary>A field of the form beyond the well-known ones, as a row binds it.</summary>
public sealed partial class PasswordFormFieldViewModel : ObservableObject
{
    public PasswordFormFieldViewModel(PasswordFormField field, SecretScreens screens, Action<PasswordFormFieldViewModel> remove)
    {
        (Field, Reveal, _remove) = (field, new SecretBoxReveal(screens), remove);
    }

    private readonly Action<PasswordFormFieldViewModel> _remove;

    public PasswordFormField Field { get; }

    public SecretBoxReveal Reveal { get; }

    public string Name
    {
        get => Field.Name;
        set => SetProperty(Field.Name, value, Field, (f, v) => f.Name = v);
    }

    public string Value
    {
        get => Field.Value;
        set
        {
            if (SetProperty(Field.Value, value, Field, (f, v) => f.Value = v))
            {
                Reveal.Typed();   // shown while typing: the 15 s start again (ADR 1012)
            }
        }
    }

    public bool IsSecret
    {
        get => Field.IsSecret;
        set => SetProperty(Field.IsSecret, value, Field, (f, v) => f.IsSecret = v);
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>
/// The generator in the form (ADR 0964 over the core's <see cref="PasswordGeneration"/>): a TYPED length (ADR 0874), the
/// classes as switches, avoid look-alikes; the result is hidden like any password, shown while the eye is held, and
/// "Use" puts it in the form's password box.
/// </summary>
public sealed partial class PasswordGeneratorViewModel : ObservableObject
{
    private readonly Action<string> _use;

    public PasswordGeneratorViewModel(SecretScreens screens, Action<string> use) => (Reveal, _use) = (new SecretBoxReveal(screens), use);

    public SecretBoxReveal Reveal { get; }

    [ObservableProperty] private string _length = new PasswordGeneratorOptions().Length.ToString(CultureInfo.CurrentCulture);
    [ObservableProperty] private bool _lowercase = true;
    [ObservableProperty] private bool _uppercase = true;
    [ObservableProperty] private bool _digits = true;
    [ObservableProperty] private bool _symbols = true;
    [ObservableProperty] private bool _avoidAmbiguous;

    /// <summary>The generated password (shown masked); empty until generated.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasGenerated))] private string _generated = string.Empty;

    /// <summary>Why nothing was generated, in the user's language.</summary>
    [ObservableProperty] private string? _problem;

    public bool HasGenerated => Generated.Length > 0;

    [RelayCommand]
    public void Generate()
    {
        var result = PasswordGeneration.Generate(Length, new PasswordGeneratorOptions(0, Lowercase, Uppercase, Digits, Symbols, AvoidAmbiguous));
        (Generated, Problem) = (result.Password ?? string.Empty, result.Problem);
    }

    [RelayCommand]
    private void Use()
    {
        if (HasGenerated)
        {
            _use(Generated);
            Generated = string.Empty;
        }
    }
}

/// <summary>
/// The create and edit form of an entry (ADR 0968, the core's <see cref="PasswordEntryForm"/>): the name on create, then
/// URL, user name and password — always offered — the PIN once the entry has one or "Add field" chose it, every other
/// field in the entry's own order, and the notes. Saving seals on this machine to the store's readers: a create through
/// the folder's rel, an edit as a new version under the ETag the entry was OPENED with, so a change made meanwhile is a
/// 412 said as such (ADR 0794).
/// </summary>
public sealed partial class PasswordEntryEditorViewModel : ObservableObject
{
    private readonly PasswordEntryForm _form;
    private readonly Func<string, PasswordEntryContent, Task<PasswordEntryInfo>> _save;
    private readonly OpenedPasswordEntry? _opened;
    private readonly SecretScreens _screens;
    private readonly Func<PasswordEntryInfo?, Exception?, Task> _done;

    private PasswordEntryEditorViewModel(Func<string, PasswordEntryContent, Task<PasswordEntryInfo>> save, PasswordEntryForm form, OpenedPasswordEntry? opened,
        SecretScreens screens, Func<PasswordEntryInfo?, Exception?, Task> done)
    {
        (_save, _form, _opened, _screens, _done) = (save, form, opened, screens, done);
        PasswordReveal = new SecretBoxReveal(screens);
        PinReveal = new SecretBoxReveal(screens);
        Generator = new PasswordGeneratorViewModel(screens, generated => Password = generated);
        foreach (var field in form.Fields)
        {
            Fields.Add(new PasswordFormFieldViewModel(field, screens, RemoveField));
        }
    }

    /// <summary>A new entry in <paramref name="folder"/>.</summary>
    /// <param name="done">Called with the saved entry, or with the failure (a 412 among them), or with neither when cancelled.</param>
    public static PasswordEntryEditorViewModel Create(PasswordStoreClient client, PasswordFolderInfo folder, SecretScreens screens, Func<PasswordEntryInfo?, Exception?, Task> done) =>
        new((name, content) => client.CreateEntryAsync(folder, name, content), new PasswordEntryForm(), null, screens, done);

    /// <summary>An edit of an entry opened with the key: the form filled from it, its ETag what the save sends.</summary>
    public static PasswordEntryEditorViewModel Edit(PasswordStoreClient client, OpenedPasswordEntry opened, SecretScreens screens, Func<PasswordEntryInfo?, Exception?, Task> done) =>
        Edit(opened, (_, content) => client.SaveEntryAsync(opened, content), screens, done);

    /// <summary>An edit whose save is given as a lambda (the headless screenshot's, which files nothing).</summary>
    public static PasswordEntryEditorViewModel Edit(OpenedPasswordEntry opened, Func<string, PasswordEntryContent, Task<PasswordEntryInfo>> save, SecretScreens screens,
        Func<PasswordEntryInfo?, Exception?, Task> done) =>
        new(save, PasswordEntryForm.From(opened.Content), opened, screens, done);

    public bool IsCreating => _opened is null;

    /// <summary>The entry this edits; null for a new one.</summary>
    public Guid? EntryId => _opened?.Entry.Id;

    public string Title => IsCreating ? Strings.Get("PwNewEntry") : _opened!.Entry.Name;

    public string Name
    {
        get => _form.Name;
        set => SetProperty(_form.Name, value, _form, (f, v) => f.Name = v);
    }

    public string Url
    {
        get => _form.Url;
        set => SetProperty(_form.Url, value, _form, (f, v) => f.Url = v);
    }

    public string UserName
    {
        get => _form.UserName;
        set => SetProperty(_form.UserName, value, _form, (f, v) => f.UserName = v);
    }

    public string Password
    {
        get => _form.Password;
        set
        {
            if (SetProperty(_form.Password, value, _form, (f, v) => f.Password = v))
            {
                PasswordReveal.Typed();   // shown while typing: the 15 s start again (ADR 1012)
            }
        }
    }

    public string Pin
    {
        get => _form.Pin;
        set
        {
            if (SetProperty(_form.Pin, value, _form, (f, v) => f.Pin = v))
            {
                PinReveal.Typed();
            }
        }
    }

    /// <summary>
    /// The verification code's setup (ADR 1013): the stored link, or a setup key or link the user pastes or a QR code
    /// read from an image gives. The box always masks it — its secret is never shown, only replaced or removed — and a
    /// value that does not read is flagged beside it (<see cref="VerificationCodeProblem"/>).
    /// </summary>
    public string VerificationCode
    {
        get => _form.VerificationCode;
        set
        {
            if (SetProperty(_form.VerificationCode, value, _form, (f, v) => f.VerificationCode = v))
            {
                _scanProblem = null;   // the box changed: a failed read's message no longer describes it
                OnPropertyChanged(nameof(VerificationCodeProblem));
                OnPropertyChanged(nameof(HasVerificationCodeProblem));
            }
        }
    }

    public bool HasVerificationCode => _form.HasVerificationCode;

    /// <summary>"Add field" offers the verification code after the PIN, while the form has none (ADR 1013).</summary>
    public bool SuggestsVerificationCode => _form.SuggestsVerificationCode;

    /// <summary>Why the box's value is not a setup, flagged inline; null when it reads or is empty.</summary>
    public string? VerificationCodeProblem => _scanProblem ?? (_form.VerificationCodeInvalid ? Strings.Get("PwVerificationCodeInvalid") : null);

    public bool HasVerificationCodeProblem => VerificationCodeProblem is not null;

    private string? _scanProblem;

    public string Notes
    {
        get => _form.Notes;
        set => SetProperty(_form.Notes, value, _form, (f, v) => f.Notes = v);
    }

    public bool HasPin => _form.HasPin;

    /// <summary>"Add field" offers the PIN first, while the form has none (owner, 2026-10-23).</summary>
    public bool SuggestsPin => _form.SuggestsPin;

    public SecretBoxReveal PasswordReveal { get; }

    public SecretBoxReveal PinReveal { get; }

    public PasswordGeneratorViewModel Generator { get; }

    [ObservableProperty] private bool _generatorOpen;

    public ObservableCollection<PasswordFormFieldViewModel> Fields { get; } = [];

    /// <summary>Why the form cannot be saved, or why the save failed; null when there is nothing to say.</summary>
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasProblem))] private string? _problem;

    public bool HasProblem => Problem is not null;

    [ObservableProperty][NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isSaving;

    [RelayCommand]
    private void AddPin()
    {
        _form.AddPin();
        OnPropertyChanged(nameof(HasPin));
        OnPropertyChanged(nameof(SuggestsPin));
    }

    [RelayCommand]
    private void AddVerificationCode()
    {
        _form.AddVerificationCode();
        OnPropertyChanged(nameof(HasVerificationCode));
        OnPropertyChanged(nameof(SuggestsVerificationCode));
    }

    /// <summary>
    /// What a QR code read from an image held (the clipboard's screenshot or a file, read by the view): a setup link
    /// fills the box; no code found, or one that is not a setup, is said beside it and leaves the box as it was.
    /// </summary>
    public void AcceptScannedCode(string? text)
    {
        if (text is null)
        {
            SetScanProblem(Strings.Get("PwVerificationCodeNoQr"));
            return;
        }

        if (!VerificationCodeSetup.TryParse(text, out _))
        {
            SetScanProblem(Strings.Get("PwVerificationCodeInvalid"));
            return;
        }

        if (!HasVerificationCode)
        {
            AddVerificationCode();
        }

        SetScanProblem(null);
        VerificationCode = text.Trim();
    }

    private void SetScanProblem(string? problem)
    {
        _scanProblem = problem;
        OnPropertyChanged(nameof(VerificationCodeProblem));
        OnPropertyChanged(nameof(HasVerificationCodeProblem));
    }

    [RelayCommand]
    private void AddField()
    {
        var field = new PasswordFormField();
        _form.Fields.Add(field);
        Fields.Add(new PasswordFormFieldViewModel(field, _screens, RemoveField));
    }

    [RelayCommand]
    private void ToggleGenerator() => GeneratorOpen = !GeneratorOpen;

    private void RemoveField(PasswordFormFieldViewModel field)
    {
        _form.Fields.Remove(field.Field);
        Fields.Remove(field);
    }

    private bool CanSave() => !IsSaving;

    /// <summary>Save (Cmd/Ctrl+S): sealed here and filed in one act; a refusal leaves the entry as it was.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync()
    {
        if (_form.Problem(IsCreating) is { } key)
        {
            Problem = Strings.Get(key);
            return;
        }

        (IsSaving, Problem) = (true, null);
        try
        {
            var content = _form.ToContent();
            var saved = await _save(_form.Name.Trim(), content);
            HideSecrets();
            await _done(saved, null);
        }
        catch (Exception e) when (e is not OperationCanceledException && PasswordWork.Describe(e) is { } said)
        {
            Problem = said;
            if (e is PasswordEntryChangedException or HttpRequestException { StatusCode: System.Net.HttpStatusCode.PreconditionFailed })
            {
                HideSecrets();
                await _done(null, e);   // the tab reads the entry again; this form is of a version that is gone
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Esc: the form goes, nothing filed.</summary>
    [RelayCommand]
    public async Task CancelAsync()
    {
        HideSecrets();
        await _done(null, null);
    }

    /// <summary>The window lost focus or the form left the screen: every held eye lets go.</summary>
    public void HideSecrets()
    {
        PasswordReveal.Hide();
        PinReveal.Hide();
        Generator.Reveal.Hide();
        foreach (var field in Fields)
        {
            field.Reveal.Hide();
        }
    }
}
