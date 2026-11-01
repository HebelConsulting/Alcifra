// PORTED from the sister project SimplCalCon (Apache-2.0, same licence) — see ADR 0621. ADAPTED: the
// credential is a protocol DEVICE's password (#1970; the earlier shared DAV password is now a legacy device), checked
// against the device's Calendars or Contacts switch, and the authenticated principal additionally carries the tenant,
// because every read here is scoped by the tenant query filter.
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.CalDav.Authentication;

public sealed class DavBasicAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string BasicPrefix = "Basic ";

    /// <summary>The claim carrying the authenticated user's tenant — read back by the DAV controllers.</summary>
    public const string TenantClaim = "alcifra:tenant";

    private readonly Alcifra.Api.ProtocolDevices.ProtocolDeviceAuthenticator _devices;

    public DavBasicAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        Alcifra.Api.ProtocolDevices.ProtocolDeviceAuthenticator devices)
        : base(options, logger, encoder)
    {
        _devices = devices;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header))
        {
            return AuthenticateResult.NoResult();
        }

        var value = header.ToString();
        if (!value.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value[BasicPrefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        // The one check every protocol shares (#1970): the device the password names, and the switch of the protocol
        // this request is for. One handler serves both, so the path says which: contacts under /carddav, else
        // calendars.
        var protocol = Request.Path.StartsWithSegments(Alcifra.Api.CalDav.DavProtocol.CardDav.BasePath, StringComparison.OrdinalIgnoreCase)
            ? Alcifra.Api.ProtocolDevices.DeviceProtocol.Contacts
            : Alcifra.Api.ProtocolDevices.DeviceProtocol.Calendars;
        if (await _devices.SignInAsync(decoded[..separator], decoded[(separator + 1)..], protocol, Context.RequestAborted)
            is not { User: var user })
        {
            return AuthenticateResult.Fail("Invalid DAV credentials.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(TenantClaim, user.TenantId.ToString()),
        ], DavAuthenticationDefaults.Scheme);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), DavAuthenticationDefaults.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{DavAuthenticationDefaults.Realm}\"";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
