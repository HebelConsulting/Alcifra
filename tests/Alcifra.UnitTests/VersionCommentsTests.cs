using Alcifra.Presentation;

namespace Alcifra.UnitTests;

/// <summary>The system-comment tokens (owner decisions 2026-11-25, #2471): a closed list, one resolver, and the
/// doubled-brace escape for a user who means the literal.</summary>
public sealed class VersionCommentsTests
{
    private static string Translate(string key) => key switch
    {
        "VersionCommentAnnotationsEmbedded" => "Annotations embedded into the document",
        "VersionCommentSignaturesCompleted" => "Signatures completed in the document",
        _ => key,
    };

    [Fact]
    public void A_listed_token_resolves_to_the_readers_sentence()
    {
        Assert.Equal("Annotations embedded into the document", VersionComments.Resolve(VersionComments.AnnotationsEmbeddedToken, Translate));
        Assert.Equal("Signatures completed in the document", VersionComments.Resolve(" " + VersionComments.SignaturesCompletedToken + " ", Translate));
    }

    [Fact]
    public void A_doubled_opening_brace_is_the_escape_and_stays_literal()
    {
        Assert.Equal("{documentversion.annotations-embedded}", VersionComments.Resolve("{{documentversion.annotations-embedded}}", Translate));
    }

    [Fact]
    public void User_text_and_unlisted_look_alikes_render_literally()
    {
        Assert.Equal("Renewed per the framework agreement", VersionComments.Resolve("Renewed per the framework agreement", Translate));
        Assert.Equal("{documentversion.something-else}", VersionComments.Resolve("{documentversion.something-else}", Translate));
        Assert.Equal("{a} mixed with words", VersionComments.Resolve("{a} mixed with words", Translate));
    }

    [Fact]
    public void Null_and_blank_pass_through()
    {
        Assert.Null(VersionComments.Resolve(null, Translate));
        Assert.Equal("", VersionComments.Resolve("", Translate));
    }

    [Fact]
    public void Only_the_listed_tokens_are_system_comments()
    {
        Assert.True(VersionComments.IsSystemComment(VersionComments.AnnotationsEmbeddedToken));
        Assert.False(VersionComments.IsSystemComment("{{documentversion.annotations-embedded}}"));
        Assert.False(VersionComments.IsSystemComment("ordinary text"));
    }

    [Fact]
    public void A_machine_kind_reads_as_its_localized_sentence_and_everything_else_resolves_the_text()
    {
        Assert.Equal("Searchable PDF generated for the document", VersionComments.Display(null, 1, TranslateKinds));
        Assert.Equal("Resealed after an access change", VersionComments.Display(null, 2, TranslateKinds));
        Assert.Equal("Annotations embedded into the document", VersionComments.Display(VersionComments.AnnotationsEmbeddedToken, 0, TranslateKinds));
        Assert.Equal("A typed note", VersionComments.Display("A typed note", null, TranslateKinds));
        Assert.Null(VersionComments.Display("  ", 0, TranslateKinds));
    }

    private static string TranslateKinds(string key) => key switch
    {
        "VersionCommentSearchablePdf" => "Searchable PDF generated for the document",
        "VersionCommentResealed" => "Resealed after an access change",
        _ => Translate(key),
    };
}
