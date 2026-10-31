using Alcifra.Presentation;

namespace Alcifra.UnitTests.PasswordStore;

// The eye while EDITING a secret (ADR 1012, owner decisions 2026-10-28): a click shows it for 15 s, each keystroke
// restarts the 15 s, and it hides at once on a second click or on Save, Cancel or close. The clock is a seam.
public sealed class TimedSecretRevealTests
{
    private readonly ManualTime _time = new();

    [Fact]
    public void A_click_shows_the_secret_for_fifteen_seconds()
    {
        using var reveal = new TimedSecretReveal(_time);

        Assert.Equal(SecretFields.Mask, reveal.Display("hunter2"));
        reveal.Toggle();
        Assert.Equal("hunter2", reveal.Display("hunter2"));

        _time.Advance(TimeSpan.FromSeconds(14));
        Assert.True(reveal.IsRevealed);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(reveal.IsRevealed);
        Assert.Equal(SecretFields.Mask, reveal.Display("hunter2"));
    }

    [Fact]
    public void Every_keystroke_starts_the_fifteen_seconds_again()
    {
        using var reveal = new TimedSecretReveal(_time);
        reveal.Toggle();

        _time.Advance(TimeSpan.FromSeconds(10));
        reveal.Typed();
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(reveal.IsRevealed);   // 20 s after the click, 10 s after the keystroke
        reveal.Typed();
        _time.Advance(TimeSpan.FromSeconds(14));
        Assert.True(reveal.IsRevealed);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(reveal.IsRevealed);   // 15 s after the LAST keystroke
    }

    [Fact]
    public void Typing_while_hidden_does_not_show_it()
    {
        using var reveal = new TimedSecretReveal(_time);

        reveal.Typed();

        Assert.False(reveal.IsRevealed);
    }

    [Fact]
    public void A_second_click_hides_it_at_once_and_its_time_running_out_later_changes_nothing()
    {
        using var reveal = new TimedSecretReveal(_time);
        var changes = 0;
        reveal.Changed += () => changes++;

        reveal.Toggle();
        reveal.Toggle();
        Assert.False(reveal.IsRevealed);
        _time.Advance(TimeSpan.FromSeconds(20));

        Assert.Equal(2, changes);   // shown, hidden: no third change when the cancelled time would have run out
    }

    [Fact]
    public void Save_cancel_or_close_hides_it_at_once()
    {
        using var reveal = new TimedSecretReveal(_time);
        reveal.Toggle();

        reveal.Hide();
        Assert.False(reveal.IsRevealed);

        reveal.Toggle();
        Assert.True(reveal.IsRevealed);   // a later click shows it again, for a fresh 15 s
        _time.Advance(TimeSpan.FromSeconds(15));
        Assert.False(reveal.IsRevealed);
    }
}
