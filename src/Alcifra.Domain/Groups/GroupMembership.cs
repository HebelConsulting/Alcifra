using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Groups;

public class GroupMembership : ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public Guid GroupId { get; set; }
}
