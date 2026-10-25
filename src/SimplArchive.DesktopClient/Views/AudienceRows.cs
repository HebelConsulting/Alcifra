namespace SimplArchive.DesktopClient.Views;

/// <summary>One person in the audience picker (ADR 0976): who, and which of their keys a document would be sealed to.</summary>
public sealed class ReaderRow
{
    public ReaderRow(SimplArchive.ApiClient.ReaderDirectoryClient.Reader reader)
    {
        Name = reader.DisplayName;
        Certificates = reader.Certificates;
        Detail = string.Join(" · ", reader.Certificates.Select(c => c.Label));
    }

    public string Name { get; }

    /// <summary>One label per usable certificate — what the person will be sealed to, not a list of ids.</summary>
    public string Detail { get; }

    public IReadOnlyList<SimplArchive.ApiClient.ReaderDirectoryClient.Certificate> Certificates { get; }
}

/// <summary>A certificate added from a file (with its attestation chain, validated by the core), shown beside the people.</summary>
public sealed class FileCertificateRow
{
    public FileCertificateRow(string subject, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate)
    {
        Subject = subject;
        Certificate = certificate;
    }

    public string Subject { get; }

    public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate { get; }
}
