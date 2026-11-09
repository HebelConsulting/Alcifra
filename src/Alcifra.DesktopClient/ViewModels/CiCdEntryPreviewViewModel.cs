using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.ApiClient.CiCd;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>One file a CI/CD secret holds (ADR 1084): its key and size, saved as the raw bytes it was.</summary>
public sealed partial class CiCdFileViewModel(CiCdSecretValue value, Func<CiCdSecretValue, Task> save) : ObservableObject
{
    public string Name => value.Name;

    public string Size => string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Get("CiCdFileSize"), HumanFileSize.Format(value.Value.Length));

    [RelayCommand]
    private Task SaveAsync() => save(value);
}

/// <summary>
/// A CI/CD secret in the workbench preview (ADR 1084): its targets, key names and variables at once, from plain index data
/// with no key; its secret values after one open with the key. A text value then shows only while its eye is held and
/// copies like a password; a file is saved as the bytes it was. Every value shown, copied or saved is reported by key.
/// </summary>
public sealed partial class CiCdEntryPreviewViewModel : ObservableObject
{
    private readonly PasswordEntryInfo _entry;
    private readonly CiCdStoreClient _client;
    private readonly SecretFieldServices _services;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    public CiCdEntryPreviewViewModel(PasswordEntryInfo entry, CiCdStoreClient client, SecretFieldServices services)
    {
        (_entry, _client, _services) = (entry, client, services);
        var index = entry.CiCd ?? new CiCdIndexInfo([], [], [], []);
        Targets = index.Targets;
        SecretKeys = index.SecretKeys;
        Variables = [.. index.Variables.Select(CiCdEntryRules.SplitVariable).OfType<(string Name, string Value)>()
            .Select(v => new SecretFieldViewModel(new PasswordEntryRow(v.Name, v.Value, SecretFields.For(isPassword: false, markedSecret: false)), services))];
    }

    public string Name => _entry.Name;

    public PasswordEntryInfo Entry => _entry;

    public IReadOnlyList<string> Targets { get; }

    /// <summary>The secret keys by name, shown before the values are opened.</summary>
    public IReadOnlyList<string> SecretKeys { get; }

    public IReadOnlyList<SecretFieldViewModel> Variables { get; }

    /// <summary>The text secrets, after <see cref="OpenCommand"/>.</summary>
    public ObservableCollection<SecretFieldViewModel> Secrets { get; } = [];

    /// <summary>The file secrets, after <see cref="OpenCommand"/>.</summary>
    public ObservableCollection<CiCdFileViewModel> Files { get; } = [];

    /// <summary>What the last open said went wrong, or null.</summary>
    [ObservableProperty] private string? _openError;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsClosed))] private bool _isOpened;

    public bool IsClosed => !IsOpened;

    public bool CanEdit => _entry.CanEdit;

    /// <summary>The opened content, for the editor: null until opened.</summary>
    public OpenedCiCdEntry? Opened { get; private set; }

    /// <summary>Set by the host: asks where to save a file, and writes it there; false when the person cancelled.</summary>
    public Func<string, byte[], Task<bool>>? SaveFile { get; set; }

    /// <summary>Set by the host: opens the editor over the opened content.</summary>
    public Func<CiCdEntryPreviewViewModel, Task>? Edit { get; set; }

    /// <summary>Opens the values with the key: one PIN, held by the card session.</summary>
    [RelayCommand]
    private async Task OpenAsync()
    {
        OpenError = null;
        try
        {
            Opened = await _client.OpenAsync(_entry);
        }
        catch (Exception e) when (PasswordsTabViewModel.IsReported(e) || e is CiCdEntryUnreadableException)
        {
            OpenError = PasswordWork.Describe(e) ?? Strings.Get(e is CiCdEntryUnreadableException ? "CiCdUnreadable" : "PwOpenToRead");
            return;
        }

        Secrets.Clear();
        Files.Clear();
        foreach (var secret in Opened.Content.Secrets)
        {
            if (secret.IsFile)
            {
                Files.Add(new CiCdFileViewModel(secret, SaveAsync));
            }
            else
            {
                var name = secret.Name;
                Secrets.Add(new SecretFieldViewModel(
                    new PasswordEntryRow(name, System.Text.Encoding.UTF8.GetString(secret.Value), SecretFields.For(isPassword: true, markedSecret: true)),
                    _services, () => Report(name)));
            }
        }

        IsOpened = true;
    }

    [RelayCommand]
    private Task EditAsync() => Edit?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>Every revealed secret hidden again: the window lost focus, or the preview moved on.</summary>
    public void HideAll()
    {
        foreach (var secret in Secrets)
        {
            secret.Release();
        }
    }

    internal void Close()
    {
        foreach (var secret in Secrets)
        {
            secret.Close();
        }
    }

    private async Task SaveAsync(CiCdSecretValue value)
    {
        if (SaveFile is { } save && await save(value.Name, value.Value))
        {
            Report(value.Name);
        }
    }

    // Once per key and preview: the server records who revealed which key, never the value.
    private void Report(string key)
    {
        if (_reported.Add(key))
        {
            Services.Safe.Fire(() => _client.ReportAsync(_entry, "revealed", key, target: null));
        }
    }
}
