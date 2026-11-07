namespace Alcifra.Pass.Infrastructure;

/// <summary>
/// Who this tool is to an installation: the public client <c>PassClientSeeder</c> registers and the id the device
/// grant names. Its own id, not alcifra's — the approval page says which tool is asking, and revoking one tool
/// must not revoke the other (owner, 2026-10-25).
/// </summary>
internal static class PassIdentity
{
    public const string ClientId = Alcifra.Presentation.WireNames.PassClientId;
}
