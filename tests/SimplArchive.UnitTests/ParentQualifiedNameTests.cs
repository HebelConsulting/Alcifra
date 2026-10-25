using SimplArchive.Api.Documents;

namespace SimplArchive.UnitTests;

// ADR 0619's one display rule, shared by the DAV collections and the password stores: "Parent / Name", a root as is.
// The DAV collections wrote it inline before; the expected strings below are exactly what that inline rule produced.
public class ParentQualifiedNameTests
{
    [Theory]
    [InlineData("Anna Berger", "My Calendar", "Anna Berger / My Calendar")]
    [InlineData("Anna Berger", "My Passwords", "Anna Berger / My Passwords")]
    [InlineData("Operations", "Team", "Operations / Team")]
    public void A_child_reads_as_its_parent_slash_its_name(string parent, string name, string expected) =>
        Assert.Equal(expected, ParentQualifiedName.For(parent, name));

    [Fact]
    public void A_root_keeps_its_own_name() => Assert.Equal("Shared", ParentQualifiedName.For(null, "Shared"));

    [Fact]
    public void Two_stores_of_one_name_under_different_parents_read_differently() =>
        Assert.NotEqual(ParentQualifiedName.For("Anna Berger", "My Passwords"), ParentQualifiedName.For("Store Admin", "My Passwords"));
}
