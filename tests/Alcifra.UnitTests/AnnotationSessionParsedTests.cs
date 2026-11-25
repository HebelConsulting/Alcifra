using Alcifra.Presentation;

namespace Alcifra.UnitTests;

/// <summary>Continuing a stored drawing (mobile #348): the session seeds from the mark's wire form and writes back in it.</summary>
public sealed class AnnotationSessionParsedTests
{
    [Fact]
    public void A_seeded_session_carries_the_stored_strokes_and_writes_them_back_unchanged()
    {
        const string points = "0.1,0.2 0.3,0.4;0.5,0.6 0.7,0.8";

        var session = AnnotationSession.Parsed(7, "#000000", page: 2, points);

        Assert.Equal(2, session.Count);
        Assert.Equal(2, session.Page);
        Assert.Equal(points, session.Canonical());
    }

    [Fact]
    public void Adding_a_stroke_to_a_seeded_session_extends_what_it_writes()
    {
        var session = AnnotationSession.Parsed(7, "#000000", page: 0, "0.1,0.1 0.2,0.2");
        session.Add(0, [(0.3, 0.3), (0.4, 0.4)]);

        Assert.Equal("0.1,0.1 0.2,0.2;0.3,0.3 0.4,0.4", session.Canonical());
    }

    [Fact]
    public void Undo_over_a_seeded_session_steps_back_into_the_stored_strokes()
    {
        var session = AnnotationSession.Parsed(7, "#000000", page: 0, "0.1,0.1 0.2,0.2;0.5,0.5 0.6,0.6");
        session.Undo();

        Assert.Equal("0.1,0.1 0.2,0.2", session.Canonical());
    }

    [Fact]
    public void Empty_or_malformed_points_seed_nothing()
    {
        Assert.Equal(0, AnnotationSession.Parsed(7, "#000000", 0, null).Count);
        Assert.Equal(0, AnnotationSession.Parsed(7, "#000000", 0, "garbage;;x,yy").Count);
    }
}
