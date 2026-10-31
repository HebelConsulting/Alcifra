using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.UnitTests.PasswordStore;

// Import from a password manager's export (ADR 0964): Google's CSV read on the device into entry drafts, and the
// duplicates against the target folder asked per entry.
public sealed class PasswordImportTests
{
    private const string Header = "name,url,username,password,note";

    [Fact]
    public void A_plain_export_becomes_one_draft_per_row_with_the_password_apart()
    {
        var result = PasswordImport.ReadGoogleCsv($"{Header}\nexample.org,https://example.org/,anna,pa55,\n");

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("example.org", draft.Name);
        Assert.Equal("pa55", draft.Content.Password);
        Assert.Equal("anna", draft.Content.UserName);
        Assert.Equal("https://example.org/", draft.Content.Url);
        Assert.Null(draft.Content.Notes);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Quotes_commas_line_breaks_a_byte_order_mark_CRLF_and_unicode_survive()
    {
        var csv = "﻿" + Header + "\r\n"
            + "\"Bank, private\",https://bank.example/,\"zoë\",\"p\"\"w,\nd\",\"first line\r\nsecond \"\"quoted\"\" line\"\r\n"
            + "Ünïcødé ✓,,user,ünï,\r\n";

        var result = PasswordImport.ReadGoogleCsv(csv);

        Assert.Equal(2, result.Drafts.Count);
        var bank = result.Drafts[0];
        Assert.Equal("Bank, private", bank.Name);
        Assert.Equal("zoë", bank.Content.UserName);
        Assert.Equal("p\"w,\nd", bank.Content.Password);
        Assert.Equal("first line\nsecond \"quoted\" line", bank.Content.Notes);
        Assert.Equal(2, bank.Line);
        Assert.Equal("Ünïcødé ✓", result.Drafts[1].Name);
        Assert.Equal(5, result.Drafts[1].Line);
    }

    [Fact]
    public void Columns_are_found_by_header_and_empty_fields_are_left_out()
    {
        var result = PasswordImport.ReadGoogleCsv("password,username,name,url\nsecret,,,https://site.example/path\n");

        var draft = Assert.Single(result.Drafts);
        Assert.Equal("site.example", draft.Name);   // no name: the site's host
        Assert.Null(draft.Content.UserName);
        Assert.Single(draft.Content.Fields);
    }

    [Fact]
    public void A_row_with_neither_name_nor_password_is_skipped_and_said_and_a_file_without_passwords_is_refused()
    {
        var result = PasswordImport.ReadGoogleCsv($"{Header}\n,,,,just a note\n");
        Assert.Empty(result.Drafts);
        Assert.Equal(2, Assert.Single(result.Skipped).Line);

        Assert.Single(PasswordImport.ReadGoogleCsv("a,b\n1,2\n").Skipped);
    }

    [Fact]
    public void One_site_with_two_accounts_gets_two_names_because_a_folder_cannot_hold_one_twice()
    {
        var result = PasswordImport.ReadGoogleCsv($"{Header}\nsite,,anna,1,\nsite,,bob,2,\nsite,,,3,\n");

        Assert.Equal(["site", "site (bob)", "site (2)"], result.Drafts.Select(d => d.Name));
    }

    [Fact]
    public void A_duplicate_by_name_and_user_name_is_asked_about_and_each_answer_is_carried_out()
    {
        var drafts = PasswordImport.ReadGoogleCsv($"{Header}\nmail,,anna,new,\nbank,,anna,b,\nshop,,anna,s,\nother,,carl,o,\n").Drafts;
        ExistingPasswordEntry Existing(string name, string? user) => new(Guid.NewGuid(), name, user, $"/entries/{name}");
        var existing = new[] { Existing("MAIL", "anna"), Existing("bank", "anna"), Existing("shop", "anna"), Existing("other", "dora") };

        var conflicts = PasswordImport.Conflicts(drafts, existing);
        Assert.Equal(["mail", "bank", "shop"], conflicts.Select(c => c.Draft.Name));

        var answers = new Dictionary<string, PasswordImportResolution>
        {
            ["mail"] = PasswordImportResolution.AddAsNewVersion,
            ["bank"] = PasswordImportResolution.Skip,
            ["shop"] = PasswordImportResolution.KeepBoth,
        };
        var steps = PasswordImport.Plan(drafts, existing, c => answers[c.Draft.Name]);

        Assert.Equal(3, steps.Count);
        Assert.Equal(existing[0], steps[0].NewVersionOf);
        Assert.Equal(("shop (anna)", (ExistingPasswordEntry?)null), (steps[1].Draft.Name, steps[1].NewVersionOf));
        // Same name, another user: no conflict to ask about, but the name is taken, so it gets its own.
        Assert.Equal(("other (carl)", (ExistingPasswordEntry?)null), (steps[2].Draft.Name, steps[2].NewVersionOf));
    }

    // ADR 1013: a verification code in the export becomes the entry's Verification code field — a link as it came, a
    // bare key as the standard link — and says the entry has a code. Fixture rows in the shapes managers export.
    [Fact]
    public void A_totp_column_becomes_the_verification_code()
    {
        const string Link = "otpauth://totp/Bank:anna?secret=GEZDGNBVGY3TQOJQ&issuer=Bank&digits=8";
        var csv = "name,url,username,password,note,totp\n"
            + $"Bank,https://bank.example,anna,pa55,,{Link}\n"
            + "Mail,https://mail.example,bob,pw,,gezd gnbv gy3t qojq\n"
            + "Shop,https://shop.example,carl,pw,,\n";

        var drafts = PasswordImport.ReadGoogleCsv(csv).Drafts;

        Assert.Equal(Link, drafts[0].Content.VerificationCode);
        Assert.True(drafts[0].Content.HasCode);
        var code = Assert.Single(drafts[0].Content.Fields, f => f.Name == PasswordEntryFieldNames.VerificationCode);
        Assert.True(code.IsSecret);
        Assert.Equal("otpauth://totp/bob?secret=GEZDGNBVGY3TQOJQ", drafts[1].Content.VerificationCode);
        Assert.False(drafts[2].Content.HasCode);
    }

    [Theory]
    [InlineData("login_totp")]
    [InlineData("OTPAuth")]
    [InlineData("otpSecret")]
    public void Each_managers_column_name_is_read(string column)
    {
        var csv = $"name,url,username,password,{column}\nBank,https://bank.example,anna,pa55,GEZDGNBVGY3TQOJQ\n";

        Assert.True(Assert.Single(PasswordImport.ReadGoogleCsv(csv).Drafts).Content.HasCode);
    }

    [Fact]
    public void A_code_that_does_not_read_is_kept_as_an_ordinary_secret_and_claims_no_code()
    {
        var csv = "name,url,username,password,totp\nPlay,https://play.example,anna,pa55,steam://ABC1\n";

        var content = Assert.Single(PasswordImport.ReadGoogleCsv(csv).Drafts).Content;

        Assert.False(content.HasCode);
        var kept = Assert.Single(content.Fields, f => f.Name == PasswordImport.UnreadableCodeFieldName);
        Assert.Equal(("steam://ABC1", true), (kept.Value, kept.IsSecret));
    }
}
