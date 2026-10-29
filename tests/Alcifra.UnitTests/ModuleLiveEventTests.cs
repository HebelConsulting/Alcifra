using Alcifra.Api.Realtime;
using SimplArchive.ModuleAbi;
using Alcifra.Presentation;
using AbiEvent = SimplArchive.ModuleAbi.ModuleLiveEvent;
using WireEvent = Alcifra.Presentation.ModuleLiveEvent;

namespace Alcifra.UnitTests;

// ABI 1.25 (ADR 0957): the shape a module's live event must have, and the router that hands a received event to its own
// module's subscribers only. The wire path is EndToEndTests' ModuleLiveEventTests.
public class ModuleLiveEventTests
{
    private static AbiEvent Event(string name = "typing", string payload = "{}", params Guid[] recipients) =>
        new(recipients.Length == 0 ? [Guid.NewGuid()] : recipients, name, payload);

    [Fact]
    public void A_payload_at_the_cap_passes_and_one_byte_over_is_too_large()
    {
        Assert.Single(ModuleLiveEventRules.Validate(Event(payload: new string('x', AbiEvent.MaxPayloadBytes))));
        var refused = Assert.Throws<ModuleLiveEventTooLargeException>(
            () => ModuleLiveEventRules.Validate(Event(payload: new string('x', AbiEvent.MaxPayloadBytes + 1))));
        Assert.Equal("MODULE_LIVE_EVENT_TOO_LARGE", refused.ErrorCode);
        Assert.Equal(413, refused.StatusCode);
    }

    [Fact]
    public void The_cap_counts_utf8_bytes_not_characters()
    {
        // "ä" is two bytes: half the cap in characters is the whole cap in bytes, one more is over it.
        Assert.Single(ModuleLiveEventRules.Validate(Event(payload: new string('ä', AbiEvent.MaxPayloadBytes / 2))));
        Assert.Throws<ModuleLiveEventTooLargeException>(
            () => ModuleLiveEventRules.Validate(Event(payload: new string('ä', (AbiEvent.MaxPayloadBytes / 2) + 1))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Typing")]
    [InlineData("-typing")]
    [InlineData("typing now")]
    [InlineData("tippé")]
    public void A_bad_name_is_invalid(string name) =>
        Assert.Equal("MODULE_LIVE_EVENT_INVALID",
            Assert.Throws<ModuleLiveEventInvalidException>(() => ModuleLiveEventRules.Validate(Event(name: name))).ErrorCode);

    [Fact]
    public void A_name_of_the_longest_length_passes_and_one_longer_does_not()
    {
        Assert.Single(ModuleLiveEventRules.Validate(Event(name: new string('a', AbiEvent.MaxNameLength))));
        Assert.Throws<ModuleLiveEventInvalidException>(() => ModuleLiveEventRules.Validate(Event(name: new string('a', AbiEvent.MaxNameLength + 1))));
        Assert.Single(ModuleLiveEventRules.Validate(Event(name: "messenger.typing-stop_2")));
    }

    [Fact]
    public void Recipients_are_distinct_at_least_one_and_at_most_the_cap()
    {
        var anna = Guid.NewGuid();
        Assert.Single(ModuleLiveEventRules.Validate(new AbiEvent([anna, anna, Guid.Empty], "typing", "{}")));
        Assert.Throws<ModuleLiveEventInvalidException>(() => ModuleLiveEventRules.Validate(new AbiEvent([Guid.Empty], "typing", "{}")));

        var many = Enumerable.Range(0, AbiEvent.MaxRecipients + 1).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(AbiEvent.MaxRecipients, ModuleLiveEventRules.Validate(new AbiEvent(many[..AbiEvent.MaxRecipients], "typing", "{}")).Count);
        Assert.Throws<ModuleLiveEventTooLargeException>(() => ModuleLiveEventRules.Validate(new AbiEvent(many, "typing", "{}")));
    }

    [Fact]
    public void Every_refusal_is_one_catchable_family_mapped_to_its_status()
    {
        Assert.Equal(429, new ModuleLiveEventRateLimitedException().StatusCode);
        Assert.Equal(403, new ModuleLiveEventNotPermittedException("no").StatusCode);
        Assert.IsAssignableFrom<ModuleLiveEventRefusedException>(new ModuleLiveEventRateLimitedException());
        Assert.IsAssignableFrom<ModuleApiException>(new ModuleLiveEventTooLargeException("big"));
    }

    [Fact]
    public void The_router_hands_an_event_only_to_its_own_modules_subscribers()
    {
        var router = new ModuleLiveEventRouter();
        var messenger = new List<WireEvent>();
        var other = new List<WireEvent>();
        using var a = router.Subscribe("messenger", messenger.Add);
        using var b = router.Subscribe("flight-school", other.Add);

        Assert.Equal(1, router.Dispatch(new WireEvent("messenger", "typing", "{}")));
        Assert.Single(messenger);
        Assert.Empty(other);
    }

    [Fact]
    public void A_disposed_subscription_hears_nothing_and_a_throwing_one_does_not_starve_the_rest()
    {
        var router = new ModuleLiveEventRouter();
        var heard = 0;
        var gone = router.Subscribe("messenger", _ => heard += 100);
        using var thrower = router.Subscribe("messenger", _ => throw new InvalidOperationException("view gone"));
        using var listener = router.Subscribe("messenger", _ => heard++);
        gone.Dispose();
        gone.Dispose();   // idempotent

        Assert.Equal(2, router.Dispatch(new WireEvent("messenger", "typing", "{}")));
        Assert.Equal(1, heard);
    }
}
