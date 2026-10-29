using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.ApiClient;

/// <summary>Who the session is and its system-wide rights, from the root's <c>whoami</c> rel. Moved from the desktop's
/// facade for the phone, whose ⋮ menu gates Legal hold, Override check-out and Export on these (ADR 0917).</summary>
public sealed record WhoAmIInfo(Guid? UserId, Guid? TenantId, string? TenantName, string? UserName, bool IsTenantAdmin, bool CanManageUsers, bool HasPhoto, bool CanViewAuditLog, bool MfaEnabled, bool CanResetMfa, bool CanLegalHold, bool CanManageClassification, bool CanOverrideCheckout = false, bool CanImpersonate = false, string? ImpersonatedBy = null, bool CanExport = false, bool CanImport = false, bool CanManageIntrays = false, bool CanManageServiceAccounts = false, bool CanManageMailRouting = false);

public sealed class WhoAmIClient(ApiCore core)
{
    public async Task<WhoAmIInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        var json = await core.Http.GetFromJsonAsync<JsonElement>(await core.RootHrefAsync("whoami", cancellationToken), cancellationToken);
        return new WhoAmIInfo(
            json.TryGetProperty("userId", out var u) && u.ValueKind == JsonValueKind.String ? u.GetGuid() : null,
            json.TryGetProperty("tenantId", out var t) && t.ValueKind == JsonValueKind.String ? t.GetGuid() : null,
            json.TryGetProperty("tenantName", out var tn) ? tn.GetString() : null,
            json.TryGetProperty("userName", out var un) ? un.GetString() : null,
            json.TryGetProperty("isTenantAdmin", out var a) && a.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageUsers", out var m) && m.ValueKind == JsonValueKind.True,
            json.TryGetProperty("hasPhoto", out var hp) && hp.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canViewAuditLog", out var av) && av.ValueKind == JsonValueKind.True,
            json.TryGetProperty("mfaEnabled", out var mfa) && mfa.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canResetMfa", out var crm) && crm.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canLegalHold", out var clh) && clh.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageClassification", out var cmc) && cmc.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canOverrideCheckout", out var coc) && coc.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canImpersonate", out var ci) && ci.ValueKind == JsonValueKind.True,
            json.TryGetProperty("impersonatedBy", out var ib) && ib.ValueKind == JsonValueKind.String ? ib.GetString() : null,
            json.TryGetProperty("canExport", out var ce) && ce.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canImport", out var cim) && cim.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageIntrays", out var cmi) && cmi.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageServiceAccounts", out var cmsa) && cmsa.ValueKind == JsonValueKind.True,
            json.TryGetProperty("canManageMailRouting", out var cmr) && cmr.ValueKind == JsonValueKind.True);
    }
}
