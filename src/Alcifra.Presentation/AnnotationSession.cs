namespace Alcifra.Presentation;

/// <summary>
/// A drawing in progress (#2460/#2453, ADR 1107): the strokes taken so far for ONE annotation, undone and redone as
/// a stack, written once on Finished and dropped on Cancel. One session keeps one colour — the palette recolours
/// every stroke of it — and lives on ONE page: the page of its first stroke. Shared by every surface that draws
/// (the phone's bar, the web's and the desktop's ribbons), so the session's grammar is answered once.
/// </summary>
public sealed class AnnotationSession(int kind, string colour)
{
    /// <summary>The colour every stroke of the session is drawn in (one annotation, one colour).</summary>
    public string Colour { get; set; } = colour;

    private readonly List<IReadOnlyList<(double X, double Y)>> _strokes = [];
    private readonly Stack<IReadOnlyList<(double X, double Y)>> _undone = new();

    /// <summary>The stored kind this session will be filed as (the caller's own: freehand 7 or signature 8).</summary>
    public int Kind => kind;

    /// <summary>The page the session lives on, fixed by its first stroke; null until one is drawn.</summary>
    public int? Page { get; private set; }

    /// <summary>The strokes so far, oldest first, each a normalized trail (0..1 of the page).</summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes => _strokes;

    public bool CanUndo => _strokes.Count > 0;

    public bool CanRedo => _undone.Count > 0;

    public int Count => _strokes.Count;

    /// <summary>
    /// Adds a stroke drawn on <paramref name="page"/> — a stroke on another page is refused (one annotation carries
    /// one page), a stroke holding fewer than two points is ignored, and adding after an undo forgets the redone
    /// strokes, as every editor does.
    /// </summary>
    public void Add(int page, IReadOnlyList<(double X, double Y)> stroke)
    {
        if (Page is { } fixedPage && fixedPage != page)
        {
            return;
        }

        if (stroke.Count < 2)
        {
            return;
        }

        Page ??= page;
        _strokes.Add(stroke);
        _undone.Clear();
    }

    /// <summary>Steps back over the last stroke (only before Finished; afterwards the mark is an ordinary one).</summary>
    public void Undo()
    {
        if (_strokes.Count > 0)
        {
            _undone.Push(_strokes[^1]);
            _strokes.RemoveAt(_strokes.Count - 1);
        }
    }

    public void Redo()
    {
        if (_undone.Count > 0)
        {
            _strokes.Add(_undone.Pop());
        }
    }

    /// <summary>A session SEEDED from a stored freehand's wire form — "continue editing" (mobile #348): the mark's own
    /// strokes become the session's, so undo walks them and new strokes join them; <see cref="Canonical"/> writes the
    /// combined set straight back.</summary>
    /// <param name="kind">The stored kind (freehand 7 for a continued drawing).</param>
    /// <param name="colour">The mark's colour — one annotation keeps one colour.</param>
    /// <param name="page">The page the mark lives on.</param>
    /// <param name="points">The stored poly-lines, <see cref="Canonical"/>'s format.</param>
    public static AnnotationSession Parsed(int kind, string colour, int page, string? points)
    {
        var session = new AnnotationSession(kind, colour);
        foreach (var stroke in (points ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trail = new List<(double X, double Y)>();
            foreach (var pair in stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var comma = pair.IndexOf(',');
                if (comma > 0
                    && double.TryParse(pair[..comma], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                    && double.TryParse(pair[(comma + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
                {
                    trail.Add((x, y));
                }
            }

            session.Add(page, trail);
        }

        return session;
    }

    /// <summary>
    /// The wire form (ADR 1107): every stroke as "x,y x,y …" (invariant culture, 4 decimals — the precision the
    /// server canonicalises to), joined by ';'. A stroke with no valid pair is left out.
    /// </summary>
    public string Canonical() =>
        string.Join(';', _strokes.Select(Stroke).Where(stroke => stroke.Length > 0));

    private static string Stroke(IReadOnlyList<(double X, double Y)> points) =>
        string.Join(' ', points.Select(p =>
            $"{p.X.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)},{p.Y.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)}"));
}
