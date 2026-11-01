using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Alcifra.Api.ProtocolDevices;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Audit;

namespace Alcifra.Api.Controllers;

/// <summary>
/// A new device's configuration profile, through the one-time link its QR code carries (#1970 slice 2; owner,
/// 2026-10-29/31): anyone holding the link, once, within about ten minutes.
/// </summary>
/// <remarks>
/// <b>Anonymous on purpose.</b> A new phone's camera opens the link in a browser that is not signed in, so the link's
/// token is the authorisation. It is 256 random bits, works once, and expires; the answer is never cached. An unknown,
/// used or expired link answers 404, the same for every cause.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/device-profiles/{token}")]
[AllowAnonymous]
public class DeviceProfilesController(DeviceHandOvers handOvers, IAuditRecorder audit, ILogger<DeviceProfilesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Take(string token, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (await handOvers.TakeAsync(token, cancellationToken) is not { } taken)
        {
            return NotFound();
        }

        // The profile carries the device's password; who took it is the record an incident response needs.
        logger.LogInformation("The profile of protocol device {DeviceId} was taken through its one-time link", taken.Device.Id);
        await audit.RecordForActorAsync(AuditActorType.User, taken.Device.UserId, taken.Device.Name, taken.Device.TenantId,
            AuditActions.ProtocolDeviceProfileTaken, "User", taken.Device.UserId, taken.Device.Name,
            $"The profile of the device '{taken.Device.Name}' was taken through its one-time link.", cancellationToken);

        return File(System.Text.Encoding.UTF8.GetBytes(taken.Profile), DeviceProfile.ContentType, $"{Alcifra.Localization.Strings.ProductName}.mobileconfig");
    }

    [HttpHead]
    public async Task<IActionResult> Head(string token, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        return await handOvers.ExistsAsync(token, cancellationToken) ? Ok() : NotFound();
    }
}
