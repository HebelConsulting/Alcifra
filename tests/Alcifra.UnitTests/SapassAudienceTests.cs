using Alcifra.ApiClient;
using Alcifra.Sapass;
using Alcifra.Sapass.Commands;

namespace Alcifra.UnitTests;

// apass audience (ADR 0983): names resolve against the audience's members and the readers it offers, and the options
// apply in a fixed order — so a script's change is the same change every time.
public class SapassAudienceTests
{
    private static readonly Guid Anna = Guid.NewGuid(), Bob = Guid.NewGuid(), Carol = Guid.NewGuid(), Team = Guid.NewGuid();

    private static ContentAudienceInfo Audience() => new(
        Guid.NewGuid(), IsStore: true, IsPersonal: false, "\"tag\"", CanManage: true, CanAppointManager: false, IsMember: true,
        [new(Anna, null, "Anna", true, true), new(Bob, null, "Bob", false, true)],
        [], [new(Carol, null, "Carol"), new(null, Team, "Team")], LinkMap.Empty);

    [Fact]
    public void Adds_a_reader_removes_a_member_and_promotes_a_person()
    {
        var rows = AudienceCommand.Apply(Audience(), new AudienceSettings { Add = ["carol", "Team"], Remove = ["Bob"], Manager = ["Carol"] });

        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, r => r.UserId == Anna && r.IsManager);
        Assert.Contains(rows, r => r.UserId == Carol && r.IsManager);
        Assert.Contains(rows, r => r.GroupId == Team && !r.IsManager);
        Assert.DoesNotContain(rows, r => r.UserId == Bob);
    }

    [Fact]
    public void Refuses_a_name_that_reads_nothing_here_and_a_group_as_manager()
    {
        Assert.Contains("grant read access first",
            Assert.Throws<SapassException>(() => AudienceCommand.Apply(Audience(), new AudienceSettings { Add = ["Mallory"] })).Message);
        Assert.Contains("a manager is always a person",
            Assert.Throws<SapassException>(() => AudienceCommand.Apply(Audience(), new AudienceSettings { Manager = ["Team"] })).Message);
    }

    [Fact]
    public void Lists_one_member_per_line_with_their_role()
    {
        Assert.Equal(["Anna\tmanager", "Bob\tmember"], AudienceCommand.Lines(Audience()));
    }
}
