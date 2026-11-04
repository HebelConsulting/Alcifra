using System.Text.Json;
using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// What the Tube tab shows and sends (#2148): the rules every client answers alike.
public class TubeShelfTests
{
    private static readonly Guid PublishedId = Guid.NewGuid();
    private static readonly Guid AtId = Guid.NewGuid();
    private static readonly Guid DescriptionId = Guid.NewGuid();
    private static readonly Guid TagsId = Guid.NewGuid();
    private static readonly Guid OtherId = Guid.NewGuid();

    private static readonly Dictionary<string, Guid> Fields = new()
    {
        [TubeShelf.PublishedField] = PublishedId,
        [TubeShelf.PublishedAtField] = AtId,
        [TubeShelf.DescriptionField] = DescriptionId,
        [TubeShelf.TagsField] = TagsId,
    };

    private static readonly TubeVideoRow Shared = new() { Id = Guid.NewGuid(), Name = "Shared", Tags = ["keynote"], Published = true };
    private static readonly TubeVideoRow Draft = new() { Id = Guid.NewGuid(), Name = "Draft", Description = "An unfinished cut" };
    private static readonly TubeChannelRow One = new() { Id = Guid.NewGuid(), Name = "One", CanWrite = true, Videos = [Shared, Draft] };
    private static readonly TubeChannelRow Two = new() { Id = Guid.NewGuid(), Name = "Two", CanWrite = true, Videos = [Shared] };
    private static readonly TubeChannelRow Three = new() { Id = Guid.NewGuid(), Name = "Three", CanWrite = true };
    private static readonly TubeChannelRow Locked = new() { Id = Guid.NewGuid(), Name = "Locked" };

    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);

    private static JsonElement Baseline(params (Guid Id, string[] Values)[] fields) => JsonSerializer.SerializeToElement(new
    {
        name = "Clip",
        documentDate = "2026-11-01",
        documentTime = (string?)null,
        ocrLanguages = Array.Empty<string>(),
        sensitivityLabelId = (Guid?)null,
        sensitivityLabelName = (string?)null,
        tags = new[] { "kept" },
        fields = fields.Select(f => new { fieldDefinitionId = f.Id, values = f.Values }),
        maskId = Guid.NewGuid(),
        maskName = "Video",
        isFolder = false,
        etag = "abc",
        links = Array.Empty<object>(),
    });

    private static string[] ValuesOf(System.Text.Json.Nodes.JsonObject body, Guid field) =>
        body["fields"]!.AsArray().FirstOrDefault(f => f!["fieldDefinitionId"]!.GetValue<string>() == field.ToString())?["values"]!.AsArray()
            .Select(v => v!.GetValue<string>()).ToArray() ?? [];

    [Fact]
    public void A_video_in_two_ticked_channels_is_two_rows_and_an_unticked_channel_shows_nothing()
    {
        Assert.Equal(["One", "One", "Two"], TubeShelf.Visible([One, Two], new HashSet<Guid> { One.Id, Two.Id }, null).Select(r => r.Channel.Name));
        Assert.Equal(["Two"], TubeShelf.Visible([One, Two], new HashSet<Guid> { Two.Id }, null).Select(r => r.Channel.Name));
    }

    [Theory]
    [InlineData("shared", "Shared")]
    [InlineData("UNFINISHED", "Draft")]
    [InlineData("keyn", "Shared")]
    public void The_filter_reads_the_name_the_description_and_the_tags(string filter, string found) =>
        Assert.Equal(found, Assert.Single(TubeShelf.Visible([One], new HashSet<Guid> { One.Id }, filter)).Video.Name);

    [Fact]
    public void A_video_can_be_placed_only_where_the_person_may_write_and_it_is_not_already()
    {
        Assert.Equal([Three], TubeShelf.PlaceableIn([One, Two, Three, Locked], Shared.Id));
    }

    [Fact]
    public void A_row_that_is_not_published_is_a_draft()
    {
        Assert.True(Draft.IsDraft);
        Assert.False(Shared.IsDraft);
    }

    [Fact]
    public void Publishing_changes_the_switch_stamps_an_empty_date_and_echoes_everything_else()
    {
        var body = TubeShelf.DetailBody(Baseline((OtherId, ["other"])), Fields, new TubeVideoChange(Published: true), Now);

        Assert.Equal(["true"], ValuesOf(body, PublishedId));
        Assert.Equal(["2026-11-04T10:00:00+00:00"], ValuesOf(body, AtId));
        Assert.Equal(["other"], ValuesOf(body, OtherId));
        Assert.Equal("Clip", body["name"]!.GetValue<string>());
        Assert.Equal("2026-11-01", body["documentDate"]!.GetValue<string>());
        Assert.Equal("kept", body["tags"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Publishing_again_keeps_the_date_it_first_had_and_unpublishing_keeps_it_too()
    {
        var stamped = Baseline((PublishedId, ["false"]), (AtId, ["2026-01-01T09:00:00+00:00"]));
        Assert.Equal(["2026-01-01T09:00:00+00:00"], ValuesOf(TubeShelf.DetailBody(stamped, Fields, new TubeVideoChange(Published: true), Now), AtId));

        var off = TubeShelf.DetailBody(Baseline((PublishedId, ["true"]), (AtId, ["2026-01-01T09:00:00+00:00"])), Fields, new TubeVideoChange(Published: false), Now);
        Assert.Equal(["false"], ValuesOf(off, PublishedId));
        Assert.Equal(["2026-01-01T09:00:00+00:00"], ValuesOf(off, AtId));
    }

    [Fact]
    public void An_edit_changes_what_it_names_and_sends_nothing_only_the_server_writes()
    {
        var body = TubeShelf.DetailBody(Baseline((PublishedId, ["true"])), Fields, new TubeVideoChange(Name: "New", Description: "Text", Tags: ["a", "b"]), Now);

        Assert.Equal("New", body["name"]!.GetValue<string>());
        Assert.Equal(["Text"], ValuesOf(body, DescriptionId));
        Assert.Equal(["a", "b"], ValuesOf(body, TagsId));
        Assert.Equal(["true"], ValuesOf(body, PublishedId));
        foreach (var serverOnly in new[] { "links", "etag", "maskName", "sensitivityLabelName", "isFolder" })
        {
            Assert.False(body.ContainsKey(serverOnly), serverOnly);
        }
    }
}
