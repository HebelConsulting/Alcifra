using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// The chat's reading rules, once for every client (mobile #15 needed them; they lived in the desktop's view model).
public class ChatTextTests
{
    private static readonly Guid Anna = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid Gone = Guid.Parse("99999999-2222-3333-4444-555555555555");

    [Fact]
    public void A_body_splits_into_text_and_mention_parts()
    {
        var parts = ChatText.Split($"Hi @[{Anna}], see page 2");

        Assert.Equal([("Hi ", false, (Guid?)null), ($"@[{Anna}]", true, Anna), (", see page 2", false, null)],
            parts.Select(p => (p.Text, p.IsMention, p.MentionedUser)));
    }

    [Fact]
    public void Flattened_a_mention_reads_as_a_name_and_a_departed_user_as_a_tombstone()
    {
        var line = ChatText.Flatten($"@[{Anna}] and @[{Gone}]", id => id == Anna ? "Anna Muster" : null, "unknown user");

        Assert.Equal("@Anna Muster and @unknown user", line);
    }

    [Fact]
    public void A_body_without_mentions_stays_as_it_is() =>
        Assert.Equal("plain @ text", ChatText.Flatten("plain @ text", _ => "x", "?"));

    [Theory]
    [InlineData(1, 1, "ChatFiledNewDocument")]   // version 1 is the document arriving
    [InlineData(1, 3, "ChatSavedNewVersion")]    // a later version of something already filed
    [InlineData(2, 3, "ChatActivatedVersion")]
    [InlineData(3, null, "ChatAttachmentRefused")]
    public void An_automatic_entry_names_its_sentence(int kind, int? version, string key) =>
        Assert.Equal(key, ChatText.AutomaticEntry(kind, "Demo Admin", version, "scan.exe")!.Value.Key);

    [Fact]
    public void A_refused_attachment_carries_the_file_name_not_a_version() =>
        Assert.Equal(["Demo Admin", "scan.exe"], ChatText.AutomaticEntry(3, "Demo Admin", null, "scan.exe")!.Value.Args);

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public void A_typed_message_or_an_unknown_kind_has_no_sentence(int kind) =>
        Assert.Null(ChatText.AutomaticEntry(kind, "Demo Admin", 1, "body"));

    [Fact]
    public void Replies_hang_under_their_message_one_level_deep_and_an_orphan_is_left_out()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), reply = Guid.NewGuid(), orphan = Guid.NewGuid();
        var messages = new (Guid Id, Guid? Parent)[] { (a, null), (reply, a), (b, null), (orphan, Guid.NewGuid()) };

        var thread = ChatText.Thread(messages, m => m.Id, m => m.Parent);

        Assert.Equal([a, b], thread.Select(t => t.Message.Id));
        Assert.Equal([reply], thread[0].Replies.Select(r => r.Id));
        Assert.Empty(thread[1].Replies);
    }
}
