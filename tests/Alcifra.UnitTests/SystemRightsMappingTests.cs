using Alcifra.Api.Controllers;
using Alcifra.Api.Users;
using Alcifra.Application.Abstractions;

namespace Alcifra.UnitTests;

// The escalation cap compares the caller's effective rights with the rights being granted (#2277). The users endpoint's
// own mapping of the caller's rights never learned four rights added later, so nobody "held" them and no tenant
// administrator could grant them: copying any user who had them was refused. One mapping now serves both endpoints, and
// this keeps every right in it, whichever right is added next.
public class SystemRightsMappingTests
{
    [Fact]
    public void Every_right_a_caller_holds_reaches_the_bundle_the_cap_compares()
    {
        var parameters = typeof(SystemRightsSet).GetConstructors().Single().GetParameters();
        var everything = (SystemRightsSet)Activator.CreateInstance(typeof(SystemRightsSet), parameters.Select(_ => (object)true).ToArray())!;

        var mapped = SystemRightsMapping.FromEffective(everything);

        var missing = typeof(SystemRights).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && !(bool)p.GetValue(mapped)!)
            .Select(p => p.Name)
            .ToList();
        Assert.True(missing.Count == 0, $"held by the caller but not mapped for the cap: {string.Join(", ", missing)}");
    }

    // The cap names each right by hand. A right missing from SystemRightsPolicy.CanApply is the dangerous drift: it would
    // be granted WITHOUT the check, by anyone who may manage users. So each right, granted alone by a caller who holds
    // nothing, must be refused.
    public static TheoryData<string> Rights()
    {
        var data = new TheoryData<string>();
        foreach (var property in typeof(SystemRights).GetProperties().Where(p => p.PropertyType == typeof(bool)))
        {
            data.Add(property.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Rights))]
    public void A_caller_who_lacks_a_right_cannot_grant_it(string right)
    {
        var proposed = new SystemRights();
        typeof(SystemRights).GetProperty(right)!.SetValue(proposed, true);

        Assert.False(SystemRightsPolicy.CanApply(caller: new SystemRights(), current: new SystemRights(), proposed),
            $"{right} can be granted by a caller who does not hold it: SystemRightsPolicy.CanApply does not check it.");
    }

    [Fact]
    public void Every_right_the_bundle_carries_is_one_a_caller_can_hold()
    {
        var held = typeof(SystemRightsSet).GetProperties().Select(p => p.Name).ToHashSet();

        var unknown = typeof(SystemRights).GetProperties()
            .Where(p => p.PropertyType == typeof(bool) && !held.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();
        Assert.True(unknown.Count == 0, $"rights a caller can never hold, so never grant: {string.Join(", ", unknown)}");
    }
}
