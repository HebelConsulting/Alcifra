using Microsoft.Extensions.Configuration;
using Alcifra.Localization;

namespace Alcifra.Infrastructure.Branding;

/// <summary>
/// The installation's product name (core #1117): one setting, <c>Branding:ProductName</c>, else the build's name
/// (the ProductName build property). Every text that names the product reads <see cref="Strings.ProductName"/>, and the
/// API root hands it to the web and desktop clients. Identifiers (the WebDAV path, iCalendar product ids) follow the
/// build's name, not this setting.
/// </summary>
public static class ProductBranding
{
    public const string ProductNameKey = "Branding:ProductName";

    /// <summary>Sets the process's product name from <paramref name="configuration"/>.</summary>
    public static void Apply(IConfiguration configuration) =>
        Strings.ProductName = configuration[ProductNameKey] is { Length: > 0 } configured && configured.Trim() is { Length: > 0 } name
            ? name
            : Strings.DefaultProductName;
}
