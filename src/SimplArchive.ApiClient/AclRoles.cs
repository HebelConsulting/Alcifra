namespace SimplArchive.ApiClient;

/// <summary>
/// The three role presets of Manage access (Viewer, Editor, Manager) and the cap on what a caller may grant (the client
/// half of the server's <c>EffectiveRights.Covers</c>, ADR 0169). Shared by the desktop's dialog and the phone's form
/// (mobile slice 5), so a preset means the same rights on both.
/// </summary>
public static class AclRoles
{
    public static AclRights Viewer { get; } = new(true, true, false, false, false, false, false, false, false);

    public static AclRights Editor { get; } = new(true, true, true, true, true, false, true, true, false);

    public static AclRights Manager { get; } = new(true, true, true, true, true, true, true, true, true);

    /// <summary>The presets in the order a picker lists them, with their label keys.</summary>
    public static IReadOnlyList<(string LabelKey, AclRights Rights)> All { get; } =
        [("MaRoleViewer", Viewer), ("MaRoleEditor", Editor), ("MaRoleManager", Manager)];

    /// <summary>The label of a set of rights: its preset's, or "Custom".</summary>
    public static string LabelKeyOf(AclRights rights) => All.FirstOrDefault(p => p.Rights == rights).LabelKey ?? "MaRoleCustom";

    /// <summary>Whether <paramref name="grantable"/> (what this caller may hand out here) covers every right in the bundle.</summary>
    public static bool Covers(AclRights bundle, AclRights grantable) =>
        (!bundle.CanSee || grantable.CanSee)
        && (!bundle.CanReadContent || grantable.CanReadContent)
        && (!bundle.CanEditContent || grantable.CanEditContent)
        && (!bundle.CanEditIndexData || grantable.CanEditIndexData)
        && (!bundle.CanCreateSubItems || grantable.CanCreateSubItems)
        && (!bundle.CanDelete || grantable.CanDelete)
        && (!bundle.CanMove || grantable.CanMove)
        && (!bundle.CanAnnotate || grantable.CanAnnotate)
        && (!bundle.CanManagePermissions || grantable.CanManagePermissions);
}
