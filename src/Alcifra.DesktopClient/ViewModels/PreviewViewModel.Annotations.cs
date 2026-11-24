using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;

using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

// The sticky-note / positional-annotation half of the preview (ADR "Document annotations"), split out of
// PreviewViewModel.cs so that file has room to grow again (#1450).
//
// A PARTIAL, not a separate class, and that is the whole reason this is safe. [RelayCommand] generates the
// command properties the XAML binds to — NotePlacedCommand, ShapeDrawnCommand and the rest — on the type that
// declares the method. Moving these to a collaborator would leave every one of those bindings resolving to
// nothing, and a broken Avalonia binding is a silent no-op rather than a build error. The per-feature partial
// split is also the shape this codebase already approved for view-glue (ADR 0572: MainWindow's class spans
// ~1,575 lines across three partials, with OverLimitFileCeilingTests holding the FILE-level ceiling).
public sealed partial class PreviewViewModel
{
    // --- Sticky notes / positional annotations (ADR "Document annotations") --------------------------------
    // Only a real document-version preview carries notes (the annotations link + a dialog provider must both be
    // present); the Intray and Recycle-bin previews don't. The dialog itself is supplied by the view (code-behind)
    // so the VM stays view-agnostic, mirroring the injected status reporter.
    public Func<AnnotationDialogRequest, Task<AnnotationDialogResult?>>? AnnotationDialog { get; set; }

    public sealed record AnnotationDialogRequest(string Text, string Color, string? AuthorName, bool CanEdit, bool CanDelete, bool IsShape = false);
    public sealed record AnnotationDialogResult(string Action, string Text, string Color);

    private string? _annotationsUrl;
    private IReadOnlyList<AnnotationsClient.AnnotationInfo> _annotations = [];

    [ObservableProperty] private bool _annotationsAvailable;
    // Whether the caller has CanAnnotate (ADR "CanAnnotate right") — gates the Add-note button; viewing needs only read.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowAnnotationTools))] private bool _canAddNote;

    /// <summary>The drawing tools are shown where the caller may annotate and no drawing session is open (#2453):
    /// a session replaces them with Finished / Cancel / Undo / Redo.</summary>
    public bool ShowAnnotationTools => CanAddNote && !SessionOpen;
    [ObservableProperty] private bool _notesVisible = true;
    [ObservableProperty] private bool _addNoteMode;

    [RelayCommand]
    private void AddNote()
    {
        if (!NotesVisible)
        {
            NotesVisible = true;
            PushNotesToPages();
        }

        AnnotationTool = 0; // notes and shape-drawing are mutually exclusive
        AddNoteMode = true;
        _status.Report("Click a spot on the page to place a note.");
    }

    [RelayCommand]
    private void ToggleNotes()
    {
        NotesVisible = !NotesVisible;
        if (!NotesVisible)
        {
            AddNoteMode = false;
            AnnotationTool = 0;
        }

        PushNotesToPages();
    }

    [RelayCommand]
    private async Task NotePlaced(NotePlacement? placement)
    {
        AddNoteMode = false;
        if (placement is null || _annotationsUrl is null || Api is null || AnnotationDialog is null)
        {
            return;
        }

        var result = await AnnotationDialog(new AnnotationDialogRequest("", Alcifra.Presentation.AnnotationPalette.Default, null, CanEdit: true, CanDelete: false));
        if (result is null || result.Action != "save")
        {
            return;
        }

        try
        {
            // Create the note with a default size (kind 0 + width/height) so it renders as an always-visible box
            // (ADR "Post-it note boxes"); the overlay grows the height to fit the text and the user can resize it.
            await Api.Annotations.CreateAnnotationAsync(_annotationsUrl, placement.PageIndex, 0, placement.X, placement.Y, 0.22, 0.06, result.Text, result.Color);
            await LoadAnnotationsAsync();
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not add the note.");
        }
    }

    [RelayCommand]
    private async Task NoteClicked(Guid id)
    {
        var note = _annotations.FirstOrDefault(a => a.Id == id);
        if (note is null || _annotationsUrl is null || Api is null || AnnotationDialog is null)
        {
            return;
        }

        var result = await AnnotationDialog(new AnnotationDialogRequest(note.Text, note.Color, note.AuthorName, note.CanEdit, note.CanDelete, IsShape: note.Kind > 0));
        if (result is null)
        {
            return;
        }

        try
        {
            if (result.Action == "delete")
            {
                await Api.Annotations.DeleteAnnotationAsync(note, note.Etag);
            }
            else if (result.Action == "save")
            {
                await Api.Annotations.UpdateAnnotationAsync(note, note.PageIndex, note.PositionX, note.PositionY, note.Width, note.Height, result.Text, result.Color, note.Etag);
            }
            else
            {
                return;
            }

            await LoadAnnotationsAsync();
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not update the note.");
        }
    }

    // Drag-to-reposition (ADR "Document annotations"): the author dropped a note at a new spot; persist the new
    // position via the same update (keeping text/colour + the current etag), then reload — which snaps it back if
    // it failed (e.g. a 412 etag mismatch) and refreshes the etag.
    [RelayCommand]
    private async Task NoteMoved(NoteMove? move)
    {
        if (move is null || _annotationsUrl is null || Api is null)
        {
            return;
        }

        var note = _annotations.FirstOrDefault(a => a.Id == move.Id);
        if (note is null || !note.CanEdit)
        {
            return;
        }

        try
        {
            await Api.Annotations.UpdateAnnotationAsync(note, note.PageIndex, move.X, move.Y, note.Width, note.Height, note.Text, note.Color, note.Etag);
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not move the note.");
        }

        await LoadAnnotationsAsync();
    }

    // Resize (ADR "Post-it note boxes"): the author dragged the note box's corner grip; persist the new size
    // (keeping position/text/colour + the current etag), then reload — snapping back on a failed update.
    [RelayCommand]
    private async Task NoteResized(NoteResize? resize)
    {
        if (resize is null || _annotationsUrl is null || Api is null)
        {
            return;
        }

        var note = _annotations.FirstOrDefault(a => a.Id == resize.Id);
        if (note is null || !note.CanEdit)
        {
            return;
        }

        try
        {
            await Api.Annotations.UpdateAnnotationAsync(note, note.PageIndex, note.PositionX, note.PositionY, resize.Width, resize.Height, note.Text, note.Color, note.Etag);
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not resize the note.");
        }

        await LoadAnnotationsAsync();
    }

    // --- Markup shapes: highlight / rectangle / arrow (ADR "Annotation markup") ----------------------------
    // 0 none, 1 highlight, 2 rectangle, 3 arrow. Bound to the overlay's DrawKind; drag on the page to draw.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowAnnotationColorPalette))] private int _annotationTool;

    // The active colour for newly-drawn shapes + the toolbar palette (ADR "Highlighting redesign"). Picking a
    // swatch sets this AND recolours the current selection. Shown while a tool is active or something is selected.
    [ObservableProperty] private string _annotationColor = Alcifra.Presentation.AnnotationPalette.Default;
    public IReadOnlyList<AnnotationSwatch> AnnotationPalette { get; } =
        [.. Alcifra.Presentation.AnnotationPalette.Colours.Select(h => new AnnotationSwatch(h))];
    public bool ShowAnnotationColorPalette => AnnotationTool > 0 || HasSelectedAnnotations;

    // Picks a colour: sets the draw colour + recolours every selected annotation the caller may edit, then reload.
    [RelayCommand]
    private async Task SetAnnotationColor(string? color)
    {
        if (string.IsNullOrEmpty(color))
        {
            return;
        }

        AnnotationColor = color;
        if (_session is { } open)
        {
            // #2453: one annotation keeps one colour — the palette recolours the whole open drawing live.
            open.Colour = color;
            RefreshSessionState();
            return;
        }

        if (_annotationsUrl is null || Api is null || _selectedAnnotationIds.Count == 0)
        {
            return;
        }

        var targets = _annotations.Where(a => a.CanEdit && _selectedAnnotationIds.Contains(a.Id)).ToList();
        try
        {
            foreach (var a in targets)
            {
                await Api.Annotations.UpdateAnnotationAsync(a, a.PageIndex, a.PositionX, a.PositionY, a.Width, a.Height, a.Text, color, a.Etag);
            }
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not recolour the selection.");
        }

        await LoadAnnotationsAsync();
    }

    [RelayCommand]
    private void SelectShapeTool(object? kindParam)
    {
        // The XAML CommandParameter is a string ("1".."8"); accept an int too. 1-3 are shapes, 7 the pen and 8 the
        // sign tool, which open a drawing SESSION instead (#2453, ADR 1107).
        var kind = kindParam is int i ? i : int.TryParse(kindParam?.ToString(), out var p) ? p : 0;
        if (kind is 7 or 8)
        {
            StartDrawingSession(kind);
            return;
        }

        if (kind is < 1 or > 3)
        {
            return;
        }

        if (!NotesVisible)
        {
            NotesVisible = true;
            PushNotesToPages();
        }

        AddNoteMode = false;
        AnnotationTool = AnnotationTool == kind ? 0 : kind; // clicking the active tool turns it off
        if (AnnotationTool > 0)
        {
            _status.Report("Drag on the page to draw.");
        }
    }

    // A shape was drawn by dragging — create it immediately with the tool's default colour (editable by clicking).
    [RelayCommand]
    private async Task ShapeDrawn(ShapeDraw? draw)
    {
        if (draw is null || draw.Kind <= 0 || _annotationsUrl is null || Api is null)
        {
            return;
        }

        AnnotationTool = 0; // one shape per tool activation (ADR "Draw-tool behaviour")

        try
        {
            await Api.Annotations.CreateAnnotationAsync(_annotationsUrl, draw.PageIndex, draw.Kind, draw.X, draw.Y, draw.W, draw.H, "", AnnotationColor);
            await LoadAnnotationsAsync();
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not add the markup.");
        }
    }

    // --- Drawing sessions: pen + sign (#2453, ADR 1107) ----------------------------------------------------
    // The ribbon's drawing group becomes Finished / Cancel / Undo / Redo while a session is open (owner decision
    // 2026-11-24); a signature asks for its name at Finished, and its completion gate files a new version below.
    private AnnotationSession? _session;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowAnnotationTools))] private bool _sessionOpen;
    [ObservableProperty] private bool _canUndoStroke;
    [ObservableProperty] private bool _canRedoStroke;
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<(double X, double Y)>> _sessionStrokes = [];
    [ObservableProperty] private int _sessionPage = -1;
    [ObservableProperty] private string? _sessionColor;

    // Thinned exactly as the phone's pen thins (AnnotationStroke.MinimumDistance): a resting hand draws nothing.
    private const double StrokeStep = 0.002;

    /// <summary>The name prompt a signature asks for at Finished (#2453): null means the view has no prompt.</summary>
    public Func<string?, Task<string?>>? SignatureNamePrompt { get; set; }

    /// <summary>A yes/no confirmation for the completion gate; the view supplies it.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>The completion gate is offered (#2453): a signature exists and this caller may file a version.</summary>
    [ObservableProperty] private bool _canCompleteSignatures;

    private int SessionKind => _session?.Kind ?? 0;

    private void StartDrawingSession(int kind)
    {
        if (!NotesVisible)
        {
            NotesVisible = true;
            PushNotesToPages();
        }

        AddNoteMode = false;
        AnnotationTool = 0;   // shape tools and sessions never overlap
        _session = new AnnotationSession(kind, AnnotationColor);
        AnnotationTool = kind;
        SessionOpen = true;
        RefreshSessionState();
        _status.Report(kind == 8
            ? "Draw your signature on the page; Finished asks for your name."
            : "Draw on the page; Finished files the drawing as one annotation.");
    }

    [RelayCommand]
    private void StrokeDrawn(StrokeDraw? draw)
    {
        if (draw is null || _session is null)
        {
            return;
        }

        if (draw.Points.Count >= 2 && (draw.Points[^1].X != draw.Points[^2].X || draw.Points[^1].Y != draw.Points[^2].Y))
        {
            _session.Add(draw.PageIndex, Thinned(draw.Points));
        }

        RefreshSessionState();
    }

    private static IReadOnlyList<(double X, double Y)> Thinned(IReadOnlyList<(double X, double Y)> points)
    {
        var kept = new List<(double X, double Y)>(points.Count);
        foreach (var (x, y) in points)
        {
            if (kept.Count == 0 || Math.Abs(x - kept[^1].X) >= StrokeStep || Math.Abs(y - kept[^1].Y) >= StrokeStep)
            {
                kept.Add((x, y));
            }
        }

        return kept;
    }

    private void RefreshSessionState()
    {
        SessionStrokes = _session?.Strokes ?? [];
        SessionPage = _session?.Page ?? -1;
        SessionColor = _session?.Colour;
        CanUndoStroke = _session?.CanUndo == true;
        CanRedoStroke = _session?.CanRedo == true;
        UndoStrokeCommand.NotifyCanExecuteChanged();
        RedoStrokeCommand.NotifyCanExecuteChanged();
        FinishSessionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUndoStroke))]
    private void UndoStroke()
    {
        _session?.Undo();
        RefreshSessionState();
    }

    [RelayCommand(CanExecute = nameof(CanRedoStroke))]
    private void RedoStroke()
    {
        _session?.Redo();
        RefreshSessionState();
    }

    [RelayCommand]
    private void CancelSession() => EndSession();

    private void EndSession()
    {
        _session = null;
        AnnotationTool = 0;
        SessionOpen = false;
        RefreshSessionState();
    }

    [RelayCommand(CanExecute = nameof(CanUndoStroke))]
    private async Task FinishSession()
    {
        if (_session is not { Page: { } page } session || _annotationsUrl is null || Api is null)
        {
            return;
        }

        var text = string.Empty;
        if (session.Kind == 8)
        {
            if (SignatureNamePrompt is null || await SignatureNamePrompt(null) is not { } name || string.IsNullOrWhiteSpace(name))
            {
                return;   // a cancelled name leaves the session open rather than filing an unnamed signature
            }

            text = name.Trim();
        }

        var (x, y) = session.Strokes[0][0];
        try
        {
            await Api.Annotations.CreateAnnotationAsync(_annotationsUrl, page, session.Kind, x, y, null, null, text, session.Colour, session.Canonical());
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not file the drawing.");
            return;
        }

        EndSession();
        await LoadAnnotationsAsync();
    }

    // The completion gate (#2453): every mark is flattened into the pages and filed as the next version — the same
    // bake the phone performs, through the one shared writer; the previous version keeps its rows untouched.
    [RelayCommand]
    private async Task CompleteSignatures()
    {
        if (Api is null || Confirm is null || LastRendered is not { VersionsHref: { } versionsHref, DownloadUrl: { } downloadUrl }
            || !string.Equals(LastRendered.FileExtension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return;   // only a PDF can carry its marks flattened; the phone draws the same line
        }

        if (!await Confirm(Strings.Get("PhoneCompleteSignaturesConfirm"), Strings.Get("PhoneCompleteSignatures")))
        {
            return;
        }

        try
        {
            var (bytes, _) = await AlcifraApiClient.DownloadAsync(downloadUrl);
            var baked = AnnotationBaking.Flatten(bytes, _annotations);
            await Api.Documents.UploadNewVersionAsync(versionsHref, baked, ".pdf");
            _status.Report(Strings.Get("PhoneCompleteSignaturesDone"));
            await LoadAnnotationsAsync();
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not complete the signatures.");
        }
    }

    private async Task LoadAnnotationsAsync()
    {
        _annotations = [];
        CanAddNote = false;
        if (Api is not null && _annotationsUrl is not null)
        {
            try
            {
                var list = await Api.Annotations.GetAnnotationsAsync(_annotationsUrl);
                _annotations = list.Items;
                CanAddNote = list.CanCreate; // CanAnnotate (ADR "CanAnnotate right")
                // The completion gate (#2453): offered where the caller may file a new version and a signature exists.
                CanCompleteSignatures = list.CanEditContent && AnnotationBaking.HasSignature(list.Items);
            }
            catch (Exception) { /* no notes / not readable */ }
        }

        UpdateSelectionState();
    }

    private void PushNotesToPages()
    {
        foreach (var page in PreviewPages)
        {
            page.Notes = NotesVisible
                ? _annotations.Where(a => a.PageIndex == page.PageIndex).Select(a => new NoteBox(a.Id, a.Kind, a.PositionX, a.PositionY, a.Width ?? 0, a.Height ?? 0, a.Color, a.CanEdit, a.Text, _selectedAnnotationIds.Contains(a.Id), a.Points)).ToList()
                : [];
        }
    }

    // --- Multi-select: delete / group-move / copy-paste (ADR "Annotation multi-select") --------------------
    // The selected annotation ids (notes + shapes) on the current version, and an in-app clipboard of copied
    // annotations (pasted as offset duplicates on the same page). Selection is client-only; a selected id that
    // no longer exists after a reload is dropped by PushNotesToPages/UpdateSelectionState.
    private readonly HashSet<Guid> _selectedAnnotationIds = [];
    private readonly List<AnnotationsClient.AnnotationInfo> _annotationClipboard = [];

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowAnnotationColorPalette))] private bool _hasSelectedAnnotations;
    [ObservableProperty] private bool _hasClipboardAnnotations;

    internal IReadOnlyCollection<Guid> SelectedAnnotationIdsForTest => _selectedAnnotationIds;

    private void UpdateSelectionState()
    {
        // Drop ids that no longer resolve to a loaded annotation (deleted elsewhere).
        _selectedAnnotationIds.RemoveWhere(id => _annotations.All(a => a.Id != id));
        HasSelectedAnnotations = _selectedAnnotationIds.Count > 0;
        PushNotesToPages();
    }

    // A click / Ctrl-click on an annotation: a plain click selects just it; a Ctrl-click toggles it in the set.
    [RelayCommand]
    private void SelectAnnotation(AnnotationSelect? select)
    {
        if (select is null)
        {
            return;
        }

        if (select.Toggle)
        {
            if (!_selectedAnnotationIds.Remove(select.Id))
            {
                _selectedAnnotationIds.Add(select.Id);
            }
        }
        else
        {
            _selectedAnnotationIds.Clear();
            _selectedAnnotationIds.Add(select.Id);
        }

        UpdateSelectionState();
    }

    // A marquee drag over empty page area selected the enclosed annotations (Ctrl adds to the current selection).
    [RelayCommand]
    private void MarqueeSelectAnnotations(MarqueeSelect? marquee)
    {
        if (marquee is null)
        {
            return;
        }

        if (!marquee.Additive)
        {
            _selectedAnnotationIds.Clear();
        }

        foreach (var id in marquee.Ids)
        {
            _selectedAnnotationIds.Add(id);
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private void ClearAnnotationSelection()
    {
        if (_selectedAnnotationIds.Count == 0)
        {
            return;
        }

        _selectedAnnotationIds.Clear();
        UpdateSelectionState();
    }

    // A group drag moved the whole selection by a normalized delta — shift every selected annotation on the page
    // (only the ones the caller may edit), persist each, then reload.
    [RelayCommand]
    private async Task GroupMoveAnnotations(AnnotationGroupMove? move)
    {
        if (move is null || _annotationsUrl is null || Api is null)
        {
            return;
        }

        var targets = _annotations
            .Where(a => a.PageIndex == move.PageIndex && a.CanEdit && _selectedAnnotationIds.Contains(a.Id))
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var a in targets)
            {
                var nx = Math.Clamp(a.PositionX + move.Dx, 0, 1);
                var ny = Math.Clamp(a.PositionY + move.Dy, 0, 1);
                await Api.Annotations.UpdateAnnotationAsync(a, a.PageIndex, nx, ny, a.Width, a.Height, a.Text, a.Color, a.Etag);
            }
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not move the selection.");
        }

        await LoadAnnotationsAsync();
    }

    // Delete every selected annotation the caller may delete; reload + clear the selection.
    [RelayCommand]
    private async Task DeleteSelectedAnnotations()
    {
        if (_annotationsUrl is null || Api is null || _selectedAnnotationIds.Count == 0)
        {
            return;
        }

        var targets = _annotations.Where(a => a.CanDelete && _selectedAnnotationIds.Contains(a.Id)).ToList();
        try
        {
            foreach (var a in targets)
            {
                await Api.Annotations.DeleteAnnotationAsync(a, a.Etag);
            }
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not delete the selection.");
        }

        _selectedAnnotationIds.Clear();
        await LoadAnnotationsAsync();
    }

    // Copy the selected annotations into the in-app clipboard (a snapshot; paste re-creates them offset).
    [RelayCommand]
    private void CopySelectedAnnotations()
    {
        _annotationClipboard.Clear();
        _annotationClipboard.AddRange(_annotations.Where(a => _selectedAnnotationIds.Contains(a.Id)));
        HasClipboardAnnotations = _annotationClipboard.Count > 0;
        if (HasClipboardAnnotations)
        {
            _status.Report($"Copied {_annotationClipboard.Count} annotation(s).");
        }
    }

    // Paste the clipboard as offset duplicates on their original page/version; reload + select the new copies is
    // not attempted (fresh ids are server-assigned) — the pastes just appear, nudged by a small offset.
    [RelayCommand]
    private async Task PasteAnnotations()
    {
        if (_annotationsUrl is null || Api is null || _annotationClipboard.Count == 0 || !CanAddNote)
        {
            return;
        }

        const double offset = 0.03;
        try
        {
            foreach (var a in _annotationClipboard)
            {
                var nx = Math.Clamp(a.PositionX + offset, 0, 1);
                var ny = Math.Clamp(a.PositionY + offset, 0, 1);
                // Freehand (kind 7) has no box extent — carry its poly-line points instead of a width/height.
                var w = a.Kind is 7 or 8 ? (double?)null : a.Width ?? (a.Kind == 0 ? 0.22 : 0.1);
                var h = a.Kind is 7 or 8 ? (double?)null : a.Height ?? (a.Kind == 0 ? 0.06 : 0.05);
                await Api.Annotations.CreateAnnotationAsync(_annotationsUrl, a.PageIndex, a.Kind, nx, ny, w, h, a.Text, a.Color, a.Points);
            }
        }
        catch (Exception e)
        {
            _status.Report(e is ApiActionException ae ? ae.Message : "Could not paste the annotations.");
        }

        await LoadAnnotationsAsync();
    }

    // Test seam: point the view model at a real annotations URL and load, so a VM-level test can drive the
    // multi-select commands without the full detail-load path.
    internal async Task LoadAnnotationsForTestAsync(string annotationsUrl)
    {
        _annotationsUrl = annotationsUrl;
        await LoadAnnotationsAsync();
    }
}
