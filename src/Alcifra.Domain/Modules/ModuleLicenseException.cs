namespace Alcifra.Domain.Modules;

/// <summary>
/// A presented module license failed verification (ADRs 0740/0743). Derives from
/// <see cref="InvalidOperationException"/> so the Api boundary translates it the way it translates the
/// other domain invariants (the <c>BookingInvariantException</c> precedent) — each factory names the
/// precise refusal, so a rejection never reads as a generic "bad license".
/// </summary>
public sealed class ModuleLicenseException : InvalidOperationException
{
    private ModuleLicenseException(string message)
        : base(message)
    {
    }

    /// <summary>The document's content is not a parseable license artefact.</summary>
    public static ModuleLicenseException Malformed(string detail) =>
        new($"The document is not a readable module license: {detail}");

    /// <summary>The signature does not verify against the module's embedded key.</summary>
    public static ModuleLicenseException BadSignature(string moduleId) =>
        new($"The license's signature does not verify against module '{moduleId}''s key.");

    /// <summary>The license names a different module than the one being activated.</summary>
    public static ModuleLicenseException WrongModule(string licensed, string requested) =>
        new($"The license is issued for module '{licensed}', not '{requested}'.");

    /// <summary>The license binds to a different tenant (per-tenant binding, ADR 0743).</summary>
    public static ModuleLicenseException WrongTenant() =>
        new("The license is issued for a different tenant.");

    /// <summary>Activation without a licence was asked of a module that requires one (ABI 1.23, ADR 0944).</summary>
    public static ModuleLicenseException LicenceRequired(string moduleId) =>
        new($"Module '{moduleId}' requires a licence; file one to activate it.");

    /// <summary>The license was issued for a different ABI major than this host runs (ADR 0741).</summary>
    public static ModuleLicenseException AbiMismatch(int licensed, int hosted) =>
        new($"The license is issued for ABI major {licensed}; this host runs ABI major {hosted}.");
}
