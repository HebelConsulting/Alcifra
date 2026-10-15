namespace SimplArchive.ModuleAbi;

/// <summary>
/// What a module says about offline exports on a tenant that refuses plaintext doors (ABI 1.15, core ADR 0928): the
/// phone's offline copy there is made of envelopes, and whether it may exist at all is the module's per-tenant setting.
/// </summary>
/// <param name="Allowed">Offline exports may run on this tenant (content then travels as envelopes to the caller).</param>
/// <param name="SealIndexDataAndChat">Index data and chat travel sealed to the caller's certificates too, so nothing in
/// the copy is readable without the caller's key.</param>
public sealed record OfflineExportPolicy(bool Allowed, bool SealIndexDataAndChat)
{
    /// <summary>
    /// A phone may hold the PIN of the user's hardware key for a session (core ADR 0931, which changed the meaning this
    /// member had under ADR 0929): every sealed object is still decrypted by the key, in memory, and nothing is ever
    /// re-sealed on the device. False: the PIN is asked for every decryption. Asked on every tenant, online and offline,
    /// not only for offline exports. An init property (ABI 1.16), so the two-value constructor a 1.15 module calls is
    /// unchanged.
    /// </summary>
    public bool ContentOncePerSession { get; init; }
}

/// <summary>The question's context: the tenant asking is the module's own (hooks run as the module).</summary>
/// <param name="Services">The module's scoped services, as for <see cref="ReaderCertificateContext"/>.</param>
public sealed record OfflineExportPolicyContext(IServiceProvider Services);
