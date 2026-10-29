using Microsoft.Extensions.Options;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Http;

namespace Alcifra.Api.Notifications;

/// <summary>
/// New notifications to the recipients' phones (SimplArchiveMobile #20; APNs, core #1805): best effort, after the save,
/// off the request. Moved out of Program.cs when APNs joined, which would have taken it past the 1000-line ceiling.
/// </summary>
public static class PhonePushServices
{
    public static IServiceCollection AddPhonePush(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPhonePushNotifier, PhonePushSender>();
        services.Configure<Controllers.PhonePushOptions>(configuration.GetSection(Controllers.PhonePushOptions.SectionName));

        // The phone's push address is whatever the device registered, so it goes through the same outbound guard as the
        // calendar push: without it, a registration naming an internal host would make the server post to it (SSRF).
        services.AddHttpClient(nameof(PhonePushSender))
            .ConfigurePrimaryHttpMessageHandler(provider => GuardedOutboundHandler.Create(provider.GetRequiredService<IOutboundAddressPolicy>()));

        // APNs: Apple's hosts only, over HTTP/2 as APNs requires — through the outbound guard all the same, a REGISTERED
        // named client (an unregistered one skips it).
        services.Configure<ApnsOptions>(configuration.GetSection(ApnsOptions.SectionName));
        services.AddSingleton(provider => new ApnsClient(provider.GetRequiredService<IOptions<ApnsOptions>>().Value, TimeProvider.System));
        services.AddHttpClient(ApnsClient.HttpClientName, http =>
            {
                http.DefaultRequestVersion = System.Net.HttpVersion.Version20;
                http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
            })
            .ConfigurePrimaryHttpMessageHandler(provider => GuardedOutboundHandler.Create(provider.GetRequiredService<IOutboundAddressPolicy>()));
        return services;
    }
}
