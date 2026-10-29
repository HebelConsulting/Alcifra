using Alcifra.Domain.Modules;

namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// The one place the escalate → grace → self-deactivate arithmetic lives (ADR 0740). Active is DERIVED at
/// every ask from the stored contract end date — no flag, no sweep, no event to miss: the module "turns
/// off" the instant the math says so, and a renewal filed during grace turns it back on the same way.
/// </summary>
public static class ModuleActivationPolicy
{
    /// <summary>
    /// How long the behaviour keeps running past the support contract's end (ADR 0740: a flight school
    /// mid-season does not lose its state machines overnight over a late invoice). A core constant, the
    /// same for every module — deliberately not configuration, which would be a lever that guts the
    /// enforcement story.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);

    /// <summary>The first instant the contract no longer covers — the end DAY is inclusive
    /// (<see cref="ModuleActivation.SupportContractEndDate"/> stores its midnight UTC).</summary>
    public static DateTimeOffset ExpiresAt(ModuleActivation activation) =>
        activation.SupportContractEndDate is { } end ? end.AddDays(1) : DateTimeOffset.MaxValue;

    /// <summary>The instant the grace runs out and the module deactivates itself.</summary>
    /// <remarks>A licence-free activation (no end date, ADR 0944) never deactivates: <see cref="DateTimeOffset.MaxValue"/>.</remarks>
    public static DateTimeOffset DeactivatesAt(ModuleActivation activation) =>
        activation.SupportContractEndDate is null ? DateTimeOffset.MaxValue : ExpiresAt(activation) + GracePeriod;

    /// <summary>A licence-free activation (ABI 1.23, ADR 0944): no contract, no ladder, never lapses.</summary>
    public static bool IsLicenceFree(ModuleActivation activation) => activation.SupportContractEndDate is null;

    /// <summary>Whether the module's behaviour is on for this tenant right now.</summary>
    public static bool IsActive(ModuleActivation activation, DateTimeOffset now) =>
        now < DeactivatesAt(activation);

    /// <summary>Whether the contract has ended but the grace period still carries the behaviour —
    /// the window in which escalation says "renew now" rather than "renew soon".</summary>
    public static bool IsInGrace(ModuleActivation activation, DateTimeOffset now) =>
        now >= ExpiresAt(activation) && now < DeactivatesAt(activation);

    /// <summary>How far ahead of the contract's end the first warning fires — symmetric with the grace:
    /// one month's warning, then one month's grace, before the behaviour stops (owner-decided).</summary>
    public static readonly TimeSpan WarnWindow = TimeSpan.FromDays(30);

    /// <summary>The escalation step the activation is at RIGHT NOW (0 calm · 1 expiring soon · 2 in grace ·
    /// 3 deactivated) — derived like everything else here; the stored level only remembers what was
    /// already announced.</summary>
    public static int EscalationLevelFor(ModuleActivation activation, DateTimeOffset now) =>
        IsLicenceFree(activation) ? 0
        : !IsActive(activation, now) ? 3
        : IsInGrace(activation, now) ? 2
        : now >= ExpiresAt(activation) - WarnWindow ? 1
        : 0;
}
