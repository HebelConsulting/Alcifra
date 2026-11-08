using System.Net;

namespace Alcifra.EndToEndTests;

// The shared server login page pre-fills the email from the OIDC login_hint carried in the authorize request
// (ADR "Browser-only desktop login + login_hint") — so a returning desktop user, whose logon window passes the
// remembered username as login_hint, doesn't retype it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class LoginHintTests
{
    private readonly E2EApiFactory _factory;

    public LoginHintTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_page_prefills_the_email_from_login_hint()
    {
        using var client = _factory.CreateClient();

        var returnUrl = "/connect/authorize?client_id=alcifra-desktop&login_hint=you%40example.com";
        var response = await client.GetAsync($"/Account/Login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("value=\"you@example.com\"", html);
    }

    [Fact]
    public async Task Login_page_has_no_prefill_without_a_hint()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("value=\"you@example.com\"", html);
    }

    // ADR 1075: where a demo login is configured, the sign-in step names it and carries it for the fill button, HTML-encoded
    // like everything else on the page. Where none is, nothing (DemoSignInHintTests).
    [Fact]
    public async Task Login_page_shows_the_configured_demo_login_with_its_fill_button()
    {
        using var client = _factory.CreateClient();

        var html = await (await client.GetAsync("/Account/Login")).Content.ReadAsStringAsync();
        Assert.Contains("id=\"demoLogin\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-email=\"{E2EApiFactory.DemoSignInEmail}\"", html, StringComparison.Ordinal);
        Assert.Contains("data-password=\"Visit&lt;&amp;&gt;2026!\"", html, StringComparison.Ordinal);
        Assert.Contains("Public demo: sign in as visitor@demo.e2e.local with the password Visit&lt;&amp;&gt;2026!.", html, StringComparison.Ordinal);
        Assert.Contains("id=\"demoLoginFill\"", html, StringComparison.Ordinal);
    }
}
