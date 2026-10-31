using Alcifra.HardwareKeys;

namespace Alcifra.UiEndToEndTests;

// #2041: another program that merely loads the card's PKCS#11 module clears the card's PIN state (measured 2026-10-31,
// a slot listing was enough). The held session then shows no private key and every read failed as "not your key",
// forever, because each retry used the same dead session. The rule (owner, 2026-10-31): a held session whose login
// is gone is treated as no session — dropped, the PIN asked again, the operation run once more.
//
// The fake card behaves as the real one does: the login belongs to the CARD, "another program" can clear it, and a
// session on a card that is not logged in refuses the operation rather than answering something.
public class CardLoginRecoveryTests
{
    private sealed class FakeCard
    {
        public bool LoggedIn { get; private set; }
        public int Prompts { get; private set; }
        public int Drops { get; private set; }
        public bool DeclineNextPrompt { get; set; }
        public bool HoldsKey { get; init; } = true;
        private FakeSession? _held;

        // What CardSession.OpenAsync does: the held session if there is one, otherwise a PIN prompt and a new login.
        public Task<FakeSession?> OpenAsync()
        {
            if (_held is not null)
            {
                return Task.FromResult<FakeSession?>(_held);
            }

            Prompts++;
            if (DeclineNextPrompt)
            {
                DeclineNextPrompt = false;
                return Task.FromResult<FakeSession?>(null);
            }

            LoggedIn = true;
            _held = new FakeSession(this);
            return Task.FromResult<FakeSession?>(_held);
        }

        public void Drop()
        {
            Drops++;
            _held = null;
        }

        public void AnotherProgramLoadsTheModule() => LoggedIn = false;

        public bool Holds => _held is not null;
    }

    private sealed class FakeSession(FakeCard card)
    {
        // What Unwrap does on the card: a logged-out card shows no private key, which is a lost login.
        public byte[]? Unwrap() =>
            !card.LoggedIn ? throw new CardLoginLostException("the session shows no private key")
            : card.HoldsKey ? [1, 2, 3]
            : null;
    }

    private static Task<byte[]?> Run(FakeCard card) =>
        CardLoginRecovery.RunAsync(card.Holds, card.OpenAsync, session => session.Unwrap(), card.Drop, "40123757");

    [Fact]
    public async Task A_held_session_whose_login_another_program_reset_asks_for_the_PIN_again_and_opens()
    {
        var card = new FakeCard();
        Assert.NotNull(await Run(card));   // first read: the PIN once, then held
        card.AnotherProgramLoadsTheModule();

        var opened = await Run(card);

        Assert.Equal([1, 2, 3], opened);
        Assert.Equal(2, card.Prompts);   // asked again, exactly once, as on first use
        Assert.Equal(1, card.Drops);     // the dead session was dropped, not reused
    }

    [Fact]
    public async Task Without_a_reset_the_held_session_answers_with_no_prompt()
    {
        var card = new FakeCard();
        await Run(card);

        Assert.NotNull(await Run(card));
        Assert.Equal(1, card.Prompts);
        Assert.Equal(0, card.Drops);
    }

    [Fact]
    public async Task A_lost_login_on_a_session_opened_just_now_is_not_retried()
    {
        // A missing key right after the PIN was accepted is not a reset: asking again could not change the answer.
        var card = new FakeCard();
        var opened = await CardLoginRecovery.RunAsync<FakeSession, byte[]>(
            wasHeld: false,
            card.OpenAsync,
            _ => throw new CardLoginLostException("the session shows no private key"),
            card.Drop,
            "40123757");

        Assert.Null(opened);
        Assert.Equal(1, card.Prompts);
        Assert.Equal(0, card.Drops);
    }

    [Fact]
    public async Task Declining_the_PIN_on_the_retry_answers_sealed()
    {
        var card = new FakeCard();
        await Run(card);
        card.AnotherProgramLoadsTheModule();
        card.DeclineNextPrompt = true;

        Assert.Null(await Run(card));
        Assert.Equal(2, card.Prompts);
        Assert.False(card.Holds);
    }

    [Fact]
    public async Task A_second_loss_after_the_new_login_answers_sealed_without_a_third_prompt()
    {
        var card = new FakeCard();
        await Run(card);
        card.AnotherProgramLoadsTheModule();

        var opened = await CardLoginRecovery.RunAsync(
            card.Holds,
            card.OpenAsync,
            session =>
            {
                var result = session.Unwrap();
                card.AnotherProgramLoadsTheModule();   // reset again, between the new login and the next try
                return result;
            },
            card.Drop,
            "40123757");

        // The first try failed (reset), the new login succeeded and opened before the next reset.
        Assert.Equal([1, 2, 3], opened);

        var again = await CardLoginRecovery.RunAsync<FakeSession, byte[]>(
            card.Holds,
            card.OpenAsync,
            _ => throw new CardLoginLostException("the session shows no private key"),
            card.Drop,
            "40123757");

        Assert.Null(again);
        Assert.Equal(3, card.Prompts);   // one retry per read, never a loop of prompts
    }

    [Fact]
    public async Task A_card_that_is_not_the_key_declines_without_a_retry()
    {
        var card = new FakeCard { HoldsKey = false };
        await Run(card);

        Assert.Null(await Run(card));
        Assert.Equal(1, card.Prompts);
        Assert.Equal(0, card.Drops);
    }
}
