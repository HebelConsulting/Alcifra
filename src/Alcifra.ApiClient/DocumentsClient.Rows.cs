using System.Text.Json;

namespace Alcifra.ApiClient;

/// <summary>
/// The row parser BOTH the repositories and the children listings go through (#858), split from
/// <see cref="DocumentsClient"/> by responsibility when that file reached the 1000-line limit: a row, its capability
/// answers, its encryption sign and what it admits (#1710's name vocabulary included).
/// </summary>
public sealed partial class DocumentsClient
{
    internal static Node ParseNode(JsonElement item) => new(
        item.GetProperty("id").GetGuid(),
        item.GetProperty("name").GetString() ?? "",
        item.TryGetProperty("hasChildren", out var hc) && hc.GetBoolean(),
        item.TryGetProperty("hasVersions", out var hv) && hv.GetBoolean(),
        item.TryGetProperty("hasSubfolders", out var hs) && hs.GetBoolean(),
        item.TryGetProperty("hasReferences", out var hr) && hr.GetBoolean(),
        item.TryGetProperty("onLegalHold", out var lh) && lh.ValueKind == JsonValueKind.True,
        item.TryGetProperty("checkedOut", out var co) && co.ValueKind == JsonValueKind.True,
        item.TryGetProperty("checkedOutByMe", out var com) && com.ValueKind == JsonValueKind.True,
        item.TryGetProperty("checkedOutByName", out var con) ? con.GetString() ?? "" : "",
        // List-row columns (ADR "List-row columns and sorting").
        item.TryGetProperty("documentType", out var dt) ? dt.GetString() ?? "" : "",
        item.TryGetProperty("documentDate", out var dd) && dd.ValueKind == JsonValueKind.String && DateOnly.TryParse(dd.GetString(), out var date) ? date : null,
        item.TryGetProperty("documentTime", out var dtm) && dtm.ValueKind == JsonValueKind.String && TimeOnly.TryParse(dtm.GetString(), out var dtime) ? dtime : null,
        item.TryGetProperty("sizeBytes", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : null,
        item.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array ? tg.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList() : [],
        item.TryGetProperty("sensitivityLabelName", out var sln) ? sln.GetString() ?? "" : "",
        item.TryGetProperty("sensitivityLabelColor", out var slc) && slc.ValueKind == JsonValueKind.String ? slc.GetString() : null,
        item.TryGetProperty("versionCount", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt32() : 0,
        item.TryGetProperty("createdBy", out var cb) ? cb.GetString() ?? "" : "",
        item.TryGetProperty("versionCreatedAt", out var vca) && vca.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(vca.GetString(), out var vcaDt) ? vcaDt : null,
        // The row's advertised addresses. WITHOUT this every Node.Links is null and Href() throws — which
        // is exactly what shipped in 2aeaae0, because the edit that added it silently did not apply.
        ApiCore.ParseLinks(item),
        ParseAdmits(item),
        item.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null,
        // The row's capability answers (#858). Read here, in the parser BOTH the repositories and children
        // listings go through, so there is no per-listing site to forget — which is how the web side nearly
        // shipped with the admin tree missing them.
        //
        // ParseReference below deliberately does NOT set them: a shortcut is renamed, moved and deleted through
        // its target, not through the row, and both clients' menus already exclude a reference from those three.
        item.TryGetProperty("canDelete", out var cd) && cd.ValueKind == JsonValueKind.True,
        item.TryGetProperty("canEditIndexData", out var ce) && ce.ValueKind == JsonValueKind.True,
        item.TryGetProperty("canMove", out var cm) && cm.ValueKind == JsonValueKind.True,
        item.TryGetProperty("canManagePermissions", out var cmp) && cmp.ValueKind == JsonValueKind.True,
        item.TryGetProperty("canCreateChildren", out var ccc) && ccc.ValueKind == JsonValueKind.True,
        RowFlags.IsFolderOf(item),
        EncryptedForOf(item),
        ApiWire.TrueOrFalse(item, "hasCode"),
        ApiWire.TrueOrFalse(item, "hasPasskey"));

    // The row's sign for encrypted content (ADR 0983); an absent or unknown value marks nothing.
    internal static EncryptedFor EncryptedForOf(JsonElement item) =>
        (item.TryGetProperty("encryptedForCaller", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null) switch
        {
            "you" => EncryptedFor.You,
            "not-you" => EncryptedFor.NotYou,
            "not-yet-you" => EncryptedFor.NotYetYou,
            _ => EncryptedFor.NotEncryptedContent,
        };

    // What this folder will accept, with the address for each (#673). An absent or empty array means the
    // client offers no creates here — the same reading as a missing rel: not available to you, here, now.
    private static IReadOnlyList<CreatableChild> ParseAdmits(JsonElement item) =>
        item.TryGetProperty("admits", out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(e => new CreatableChild(
                e.GetProperty("maskId").GetGuid(),
                e.GetProperty("name").GetString() ?? "",
                e.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.True,
                e.GetProperty("href").GetString() ?? "",
                e.TryGetProperty("folderMask", out var fm) && fm.ValueKind == JsonValueKind.String ? fm.GetString() : null,
                e.TryGetProperty("prompt", out var pr) ? pr.GetString() ?? "name" : "name",
                e.TryGetProperty("icon", out var ei) && ei.ValueKind == JsonValueKind.String ? ei.GetString() : null,
                // Where the name completes from, and whether it holds several values (#1710): advertised by the server
                // for a module's name vocabulary, and never read here until now, so the clients never offered it.
                e.TryGetProperty("nameValuesHref", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString() : null,
                ApiWire.TrueOrFalse(e, "nameValuesAreMultiple"),
                e.TryGetProperty("maskHref", out var mh) && mh.ValueKind == JsonValueKind.String ? mh.GetString() : null,
                e.TryGetProperty("requiredFields", out var rf) && rf.ValueKind == JsonValueKind.Array
                    ? [.. rf.EnumerateArray().Select(f => f.GetString() ?? string.Empty)]
                    : null))]
            : [];
}
