using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.ApiClient;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>One row of the audience being edited: a person or a group, and whether they manage it.</summary>
public sealed partial class AudienceEditorRow(AudienceChange principal, string name, bool inEffect, Action changed) : ObservableObject
{
    public AudienceChange Principal { get; } = principal;

    public string Name { get; } = name;

    public bool IsGroup => Principal.GroupId is not null;

    /// <summary>Only a person can manage (ADR 0983): the box is not offered on a group.</summary>
    public bool CanBeManager => !IsGroup;

    /// <summary>A declared member who lost read access (or was deactivated): kept, but decrypts nothing.</summary>
    public string? Note => inEffect ? null : Strings.Get("AudNotInEffect");

    [ObservableProperty] private bool _isManager = principal.IsManager;

    partial void OnIsManagerChanged(bool value) => changed();

    public AudienceChange Change => Principal with { IsManager = IsManager };
}

/// <summary>
/// The audience as the editor holds it (ADR 0983): the declared rows, the readers that may be added, and the one rule the
/// editor states before the server does — somebody must stay in charge. View-free, so a test drives it directly.
/// </summary>
public sealed partial class AudienceEditor : ObservableObject
{
    public AudienceEditor(ContentAudienceInfo audience)
    {
        Audience = audience;
        foreach (var member in audience.Members)
        {
            Rows.Add(new AudienceEditorRow(AudienceChange.Of(member), member.DisplayName, member.InEffect, Revalidate));
        }

        // A personal space's owner is computed, never a row: show them as the one member they are.
        if (audience.IsPersonal)
        {
            foreach (var owner in audience.Recipients)
            {
                Rows.Add(new AudienceEditorRow(new AudienceChange(owner.UserId, null, true), owner.DisplayName, true, Revalidate));
            }
        }

        foreach (var candidate in audience.Candidates)
        {
            Candidates.Add(candidate);
        }

        Revalidate();
    }

    public ContentAudienceInfo Audience { get; }

    public ObservableCollection<AudienceEditorRow> Rows { get; } = [];

    public ObservableCollection<AudienceCandidateInfo> Candidates { get; } = [];

    /// <summary>Whether this caller may change anything: a manager, or an administrator appointing one.</summary>
    public bool CanEdit => Audience.CanChange && !Audience.IsPersonal;

    /// <summary>What the editor explains above the list: personal, read-only, appointing, or managing.</summary>
    public string Explanation => Audience switch
    {
        { IsPersonal: true } => Strings.Get("AudPersonal"),
        { CanManage: true } => Strings.Get("AudManaging"),
        { CanAppointManager: true } => Strings.Get("AudAppointing"),
        _ => Strings.Get("AudReadOnly"),
    };

    [ObservableProperty] private AudienceCandidateInfo? _selectedCandidate;

    [ObservableProperty] private string? _problem;

    [ObservableProperty] private bool _canSave;

    partial void OnSelectedCandidateChanged(AudienceCandidateInfo? value) => OnPropertyChanged(nameof(CanAdd));

    public bool CanAdd => CanEdit && SelectedCandidate is not null;

    /// <summary>Adds the picked reader as a member; an administrator appointing adds them as a manager, the only row they may add.</summary>
    public void AddSelected()
    {
        if (SelectedCandidate is not { } candidate || !CanEdit)
        {
            return;
        }

        Rows.Add(new AudienceEditorRow(AudienceChange.Of(candidate) with { IsManager = Audience.CanAppointManager }, candidate.DisplayName, true, Revalidate));
        Candidates.Remove(candidate);
        SelectedCandidate = null;
        Revalidate();
    }

    /// <summary>Removes a row (and so its management); a reader is offered again among the candidates.</summary>
    public void Remove(AudienceEditorRow row)
    {
        if (!CanEdit || !Rows.Remove(row))
        {
            return;
        }

        // Still a reader (in effect), so offered again; a member who lost read access cannot be added back from here.
        if (row.Note is null)
        {
            Candidates.Add(new AudienceCandidateInfo(row.Principal.UserId, row.Principal.GroupId, row.Name));
        }

        Revalidate();
    }

    /// <summary>The whole audience to save.</summary>
    public IReadOnlyList<AudienceChange> Result() => [.. Rows.Select(r => r.Change)];

    private void Revalidate()
    {
        Problem = CanEdit && !Rows.Any(r => r.IsManager && !r.IsGroup) ? Strings.Get("AudNeedsManager") : null;
        CanSave = CanEdit && Problem is null;
    }
}
