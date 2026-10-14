namespace SimplArchive.ModuleAbi;

/// <summary>
/// What a module says about offline exports on a tenant that refuses plaintext doors (ABI 1.15, core ADR 0928): the
/// phone's offline copy there is made of envelopes, and whether it may exist at all is the module's per-tenant setting.
/// </summary>
/// <param name="Allowed">Offline exports may run on this tenant (content then travels as envelopes to the caller).</param>
/// <param name="SealIndexDataAndChat">Index data and chat travel sealed to the caller's certificates too, so nothing in
/// the copy is readable without the caller's key.</param>
public sealed record OfflineExportPolicy(bool Allowed, bool SealIndexDataAndChat);

/// <summary>The question's context: the tenant asking is the module's own (hooks run as the module).</summary>
/// <param name="Services">The module's scoped services, as for <see cref="ReaderCertificateContext"/>.</param>
public sealed record OfflineExportPolicyContext(IServiceProvider Services);
