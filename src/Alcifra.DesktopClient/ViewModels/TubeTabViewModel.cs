using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>One channel in the left pane: ticked channels are overlaid in the list, as addressbooks are.</summary>
public sealed partial class TubeChannelItemViewModel(TubeChannelRow row) : ObservableObject
{
    public TubeChannelRow Row { get; } = row;

    [ObservableProperty] private bool _isChecked;

    public Action? Toggled { get; set; }

    partial void OnIsCheckedChanged(bool value) => Toggled?.Invoke();

    public string Name => Row.Name;

    public bool Locked => !Row.CanWrite;
}

/// <summary>
/// Backs the desktop Tube tab (#2148): channels │ videos │ detail, shaped like the Contacts tab (ADR 0511). The list is the
/// module's, by the person's own rights; every change goes through the core's rels. Playing opens the public watch page in
/// the browser until #2036 brings a player, and a draft has none.
/// </summary>
public sealed partial class TubeTabViewModel(IShellContext shell, Func<AlcifraApiClient?> api) : ObservableObject
{
    private AlcifraApiClient? _boundTo;
    private TubeShelfAnswer _shelf = new();
    private TubeVideoForm? _form;

    [ObservableProperty] private bool _isOffered;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private string _emptyMessage = string.Empty;
    [ObservableProperty] private bool _hasRows;
    [ObservableProperty] private TubeListing? _selected;
    [ObservableProperty] private Bitmap? _thumbnail;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editDescription = string.Empty;
    [ObservableProperty] private string _editTags = string.Empty;
    [ObservableProperty] private bool _formReady;
    [ObservableProperty] private TubeChannelRow? _placeTarget;

    public ObservableCollection<TubeChannelItemViewModel> Channels { get; } = [];

    public ObservableCollection<TubeListing> Rows { get; } = [];

    public ObservableCollection<TubeChannelRow> PlaceTargets { get; } = [];

    public bool CanPublish => FormReady && !IsEditing && Selected is { Video.CanEdit: true };

    public string PublishLabel => Strings.Get(Selected?.Video.Published == true ? "TubeUnpublish" : "TubePublish");

    public bool CanEdit => FormReady && !IsEditing && Selected is { Video.CanEdit: true };

    public bool CanWatch => Selected?.Video.WatchUrl is not null;

    public bool CanRemove => !IsEditing && Selected is { Video.CanRemoveFromChannel: true };

    public bool CanPlace => !IsEditing && Selected is not null && PlaceTargets.Count > 0;

    public string? WatchHint => Selected is not null && !CanWatch ? Strings.Get("TubeNoWatch") : null;

    public string SelectedDate => Selected?.Video.PublishedAt is { } at ? at.InZone(SessionTimeZone.Current).ToString("d") : string.Empty;

    public string SelectedTags => Selected is null ? string.Empty : string.Join(", ", Selected.Video.Tags);

    /// <summary>Asks whether the archive offers the module here; the tab shows only if it does. Run at every sign-in.</summary>
    public async Task DiscoverAsync()
    {
        var client = api();
        Reset();
        if (client is null)
        {
            return;
        }

        var offered = await client.Tube.IsOfferedAsync();
        if (ReferenceEquals(client, api()))
        {
            (_boundTo, IsOffered) = (client, offered);
        }
    }

    /// <summary>Forgets the ended session's channels, so the next one never sees them.</summary>
    public void Reset()
    {
        IsOffered = false;
        _boundTo = null;
        _shelf = new TubeShelfAnswer();
        Channels.Clear();
        Rows.Clear();
        Selected = null;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (_boundTo is not { } client)
        {
            return;
        }

        try
        {
            Adopt(await client.Tube.ReadAsync());
        }
        catch (Exception e) when (e is HttpRequestException or ApiActionException)
        {
            shell.Report(string.Format(Strings.Get("TubeLoadFailed"), e.Message));
        }
    }

    /// <summary>Shows an answer: the channels with the person's ticks kept, the videos, and the selection where it still is.</summary>
    public void Adopt(TubeShelfAnswer shelf)
    {
        var keep = Channels.Count == 0 ? null : Channels.Where(c => c.IsChecked).Select(c => c.Row.Id).ToHashSet();
        var selected = Selected;
        _shelf = shelf;
        Channels.Clear();
        foreach (var channel in _shelf.Items)
        {
            // Everything is ticked the first time; afterwards the person's own ticks stand across a reload.
            var item = new TubeChannelItemViewModel(channel) { IsChecked = keep?.Contains(channel.Id) ?? true };
            item.Toggled = Rebuild;
            Channels.Add(item);
        }

        Rebuild();
        Selected = selected is null
            ? null
            : Rows.FirstOrDefault(r => r.Video.Id == selected.Video.Id && r.Channel.Id == selected.Channel.Id);
    }

    partial void OnFilterChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var ticked = Channels.Where(c => c.IsChecked).Select(c => c.Row.Id).ToHashSet();
        var keep = Selected;
        Rows.Clear();
        foreach (var row in TubeShelf.Visible(_shelf.Items, ticked, Filter))
        {
            Rows.Add(row);
        }

        HasRows = Rows.Count > 0;
        EmptyMessage = Strings.Get((_shelf.EncryptsAtRest, Channels.Count, ticked.Count) switch
        {
            (true, _, _) => "TubeEncrypts",
            (_, 0, _) => "TubeNoChannels",
            (_, _, 0) => "TubeNoneSelected",
            _ => "TubeEmpty",
        });
        Selected = keep is null ? null : Rows.FirstOrDefault(r => r == keep);
    }

    // The pane shows nothing of the previous subject, and its actions wait for this subject's own read (ADRs 0559, 0661).
    partial void OnSelectedChanged(TubeListing? value)
    {
        _form = null;
        FormReady = false;
        IsEditing = false;
        Thumbnail = null;
        PlaceTargets.Clear();
        PlaceTarget = null;
        if (value is not null)
        {
            foreach (var channel in TubeShelf.PlaceableIn(_shelf.Items, value.Video.Id))
            {
                PlaceTargets.Add(channel);
            }

            PlaceTarget = PlaceTargets.FirstOrDefault();
            Safe.Fire(() => FillPaneAsync(value));
        }

        NotifyActions();
    }

    partial void OnIsEditingChanged(bool value) => NotifyActions();

    partial void OnFormReadyChanged(bool value) => NotifyActions();

    private void NotifyActions()
    {
        foreach (var name in new[] { nameof(CanPublish), nameof(PublishLabel), nameof(CanEdit), nameof(CanWatch), nameof(CanRemove), nameof(CanPlace), nameof(WatchHint), nameof(SelectedDate), nameof(SelectedTags) })
        {
            OnPropertyChanged(name);
        }
    }

    private async Task FillPaneAsync(TubeListing row)
    {
        if (_boundTo is not { } client)
        {
            return;
        }

        try
        {
            if (row.Video.CanEdit)
            {
                var form = await client.Tube.OpenAsync(row.Video);
                if (Selected == row)
                {
                    _form = form;
                    FormReady = true;
                }
            }

            if (await client.Tube.ThumbnailAsync(row.Video) is { } bytes && Selected == row)
            {
                using var stream = new MemoryStream(bytes);
                Thumbnail = new Bitmap(stream);
            }
        }
        catch (Exception e) when (e is ApiActionException or HttpRequestException)
        {
            // It may have gone since the list was read; the actions stay disabled, and it says so.
            shell.Report(string.Format(Strings.Get("TubeLoadFailed"), e.Message));
        }
    }

    [RelayCommand]
    private void Watch()
    {
        if (Selected?.Video.WatchUrl is { } address)
        {
            NativeFileOpener.OpenWebAddress(address);
        }
    }

    [RelayCommand]
    private Task TogglePublishAsync() =>
        Selected is { } row ? SaveAsync(new TubeVideoChange(Published: !row.Video.Published)) : Task.CompletedTask;

    [RelayCommand]
    private void BeginEdit()
    {
        if (Selected is { } row)
        {
            (EditName, EditDescription, EditTags) = (row.Video.Name, row.Video.Description, string.Join(", ", row.Video.Tags));
            IsEditing = true;
        }
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private Task SaveEditAsync() => SaveAsync(new TubeVideoChange(
        Name: EditName.Trim(),
        Description: EditDescription,
        Tags: [.. EditTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]));

    private async Task SaveAsync(TubeVideoChange change)
    {
        if (_boundTo is not { } client || _form is not { } form)
        {
            return;
        }

        try
        {
            await client.Tube.SaveAsync(form, change);
        }
        catch (Exception e) when (e is ApiActionException or DetailChangedElsewhereException or DuplicateAddressClaimException or HttpRequestException)
        {
            shell.Report(string.Format(Strings.Get("TubeSaveFailed"), e.Message));
            return;
        }

        IsEditing = false;
        shell.Report(Strings.Get("TubeSaved"));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (_boundTo is not { } client || Selected is not { Video.CanRemoveFromChannel: true } row)
        {
            return;
        }

        await ActAsync(() => client.Tube.RemoveFromChannelAsync(row.Channel, row.Video.Id), string.Format(Strings.Get("TubeRemoved"), row.Channel.Name));
    }

    [RelayCommand]
    private async Task PlaceAsync()
    {
        if (_boundTo is not { } client || Selected is not { } row || PlaceTarget is not { } channel)
        {
            return;
        }

        await ActAsync(() => client.Tube.PlaceAsync(channel, row.Video.Id), string.Format(Strings.Get("TubePlaced"), channel.Name));
    }

    private async Task ActAsync(Func<Task> act, string done)
    {
        try
        {
            await act();
        }
        catch (Exception e) when (e is ApiActionException or HttpRequestException)
        {
            shell.Report(string.Format(Strings.Get("TubeSaveFailed"), e.Message));
            return;
        }

        shell.Report(done);
        await LoadAsync();
    }
}
