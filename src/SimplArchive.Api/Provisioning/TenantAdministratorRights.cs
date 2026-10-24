using SimplArchive.Domain.Users;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// What makes a user a tenant administrator: the ACL bypass and every system-level right, once, for the founding
/// administrator a provisioning creates and for any further administrator a seed makes (the Crypto demo's
/// florian, thomas and alex). One list, because a right missing from it is permanently un-grantable (#1241).
/// </summary>
public static class TenantAdministratorRights
{
    public static void GrantAll(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        user.IsTenantAdmin = true;
        // The tenant administrator gets EVERY system-level right at provisioning. No right is implied by
        // IsTenantAdmin, and a caller can only grant a right it already holds (SystemRightsPolicy) — so if
        // the founding admin lacked a right, there'd be nobody in the tenant able to delegate it (it would
        // be permanently un-grantable). Granting all of them makes every right delegable from day one.
        user.CanImpersonate = true;
        user.CanOverrideCheckout = true;
        user.CanLegalHold = true;
        user.CanManageClassification = true;
        user.CanResetMfa = true;
        user.CanManageRepositories = true;
        user.CanManageMasks = true;
        user.CanManageServiceAccounts = true;
        user.CanManageUsers = true;
        user.CanViewAuditLog = true;
        user.CanExport = true;
        user.CanImport = true;
        user.CanManageIntrays = true;
        user.CanCreateExternalLink = true;
        // The tenant's first administrator gets the x-ray into personal spaces (ADR 0670) — implied at
        // GRANT time, so it is an ordinary revocable column from here on. Without it the founding admin
        // could not see the Administration → Users branch at all, the bypass no longer reaching there.
        user.CanAccessWithoutGrant = true;
        // Mail routing is part of founding a tenant too: the first administrator must be able to give a
        // department a mailbox without a second principal existing yet to grant it from (#703).
        user.CanManageMailRouting = true;
        // The booking rights, added with the inventory work and then missing from this list for four
        // releases (#1241). The comment above is not decoration: because nothing is implied by
        // IsTenantAdmin and SystemRightsPolicy caps a grant to what the caller holds, a right absent HERE is
        // not merely un-held — it is permanently UN-GRANTABLE, by anyone, in every tenant this provisions.
        // Measured on the v0.26.0 demo: both tenant admins had them false, so a grounded aircraft could not
        // be released by anybody through the app (ADR 0778 makes suspension derived from an active block,
        // clearable only with CanReleaseResources).
        //
        // The service-account asymmetry is NOT contradicted by this. ServiceAccount carries
        // CanBlockResources and deliberately no CanReleaseResources (owner decision 2026-09-09): a machine
        // may ground on suspicion, releasing is a human judgement. This is a human, and the founding one.
        user.CanBlockResources = true;
        user.CanReleaseResources = true;
    }
}
