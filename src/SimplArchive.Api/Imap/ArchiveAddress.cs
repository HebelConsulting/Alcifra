namespace SimplArchive.Api.Imap;

/// <summary>
/// The installation a served message comes from, read from <c>App:BaseUrl</c>: the address its footer names and the
/// domain of its synthetic sender (#1117). Never a product's own address, which would put one edition's name on every
/// edition's mail.
/// </summary>
internal sealed record ArchiveAddress(Uri BaseUrl)
{
    /// <summary>The sender of a synthetic message: the archive's own domain, never a reply target.</summary>
    public string NoReply => $"no-reply@{BaseUrl.Host}";

    /// <summary>The address a reader opens to reach the archive.</summary>
    public string Url => BaseUrl.ToString().TrimEnd('/');

    /// <summary>From configuration. <c>App:BaseUrl</c> is required outside tests; startup refuses without it.</summary>
    public static ArchiveAddress From(IConfiguration configuration) =>
        new(new Uri(configuration["App:BaseUrl"] ?? "http://localhost"));
}
