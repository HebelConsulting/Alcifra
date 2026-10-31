using Alcifra.Api.Errors.Exceptions.Concurrency;
using Alcifra.Domain.Acl;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Groups;
using Alcifra.Domain.Notifications;
using Alcifra.Domain.ServiceAccounts;
using Alcifra.Domain.Tenants;
using Alcifra.Domain.Users;
using Alcifra.Domain.Workflow;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Concurrency;

// One named verb contract per concurrency-tracked entity (ADR 0795). Each is a single line because everything
// they share lives in EntityVerbContract<T> and the only per-entity difference — which refusal a stale
// precondition answers with — is a constructor lambda.
//
// They exist to be NAMED. A shared helper is invisible when somebody forgets to call it; `DocumentVerbs` in a
// controller's constructor is not, and a mutation that writes a Document without taking one is greppable. That
// is the whole distinction between this and the ConcurrencyHeaders it wraps.
//
// Registered in Program.cs. A new tracked entity adds a line here and a line there.

/// <summary>The verb contract for <see cref="Document"/> — the most-mutated entity in the archive.</summary>
public sealed class DocumentVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<Document>(dbContext, EtagMismatchException.ForDocument);

/// <summary>The verb contract for <see cref="Tenant"/> — nine admin settings PUTs share one row.</summary>
public sealed class TenantVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<Tenant>(dbContext, EtagMismatchException.ForTenant);

/// <summary>The verb contract for <see cref="User"/> — written from the admin side and the user's own side.</summary>
public sealed class UserVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<User>(dbContext, EtagMismatchException.ForUser);

/// <summary>The verb contract for <see cref="ServiceAccount"/> — whose PUT is a full replace.</summary>
public sealed class ServiceAccountVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<ServiceAccount>(dbContext, EtagMismatchException.ForServiceAccount);

/// <summary>The verb contract for <see cref="AclEntry"/> — two admins on one permission dialog.</summary>
public sealed class AclEntryVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<AclEntry>(dbContext, EtagMismatchException.ForAclEntry);

/// <summary>The verb contract for <see cref="WorkflowState"/> — two approvers resolving one workflow.</summary>
public sealed class WorkflowStateVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<WorkflowState>(dbContext, EtagMismatchException.ForWorkflow);

/// <summary>The verb contract for <see cref="Group"/> — renamed and re-granted from the admin form (#1220).</summary>
public sealed class GroupVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<Group>(dbContext, EtagMismatchException.ForGroup);

/// <summary>The verb contract for <see cref="TagDefinition"/> — the catalog's name and colour (#1220).</summary>
public sealed class TagDefinitionVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<TagDefinition>(dbContext, EtagMismatchException.ForTag);

/// <summary>The verb contract for <see cref="PhonePushRegistration"/>: one user, several clients (SimplArchiveMobile #20).</summary>
public sealed class PhonePushRegistrationVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<PhonePushRegistration>(dbContext, EtagMismatchException.ForPushRegistration);

/// <summary>The verb contract for <see cref="Domain.Certificates.TrustedAttestationRoot"/>: renamed and switched (ADR 0963).</summary>
public sealed class TrustedAttestationRootVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<Domain.Certificates.TrustedAttestationRoot>(dbContext, EtagMismatchException.ForAttestationRoot);

public sealed class TenantMailDomainVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<TenantMailDomain>(dbContext, EtagMismatchException.ForMailDomain);

public sealed class UserIdentityAliasVerbs(AlcifraDbContext dbContext)
    : EntityVerbContract<UserIdentityAlias>(dbContext, EtagMismatchException.ForIdentityAlias);
