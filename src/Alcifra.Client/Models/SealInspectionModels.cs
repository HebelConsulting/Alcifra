namespace Alcifra.Client.Models;

/// <summary>
/// A document's seal inspection as the server answers its <c>seal-inspection</c> rel (#2291): the browser cannot read CMS,
/// so the server reads the stored envelope and this is its description. The desktop's SealReport is the same answer.
/// </summary>
public sealed class SealInspection
{
    public string Kind { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public bool SmimeWrapped { get; set; }
    public long Size { get; set; }
    public string? ContentType { get; set; }
    public string? ContentEncryption { get; set; }
    public List<SealRecipient> Recipients { get; set; } = [];
    public List<SealSigner> Signers { get; set; } = [];
    public List<string> Certificates { get; set; } = [];
    public SealAudience? Audience { get; set; }
    public List<SealAsn1Node> Asn1 { get; set; } = [];
}

public sealed class SealRecipient
{
    public string? HolderName { get; set; }
    public string? HolderEmail { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? Attestation { get; set; }
    public DateTimeOffset? NotAfter { get; set; }
    public string? Issuer { get; set; }
    public string? Serial { get; set; }
    public string? SubjectKeyIdentifier { get; set; }
    public string Method { get; set; } = string.Empty;
    public string KeyEncryptionAlgorithm { get; set; } = string.Empty;
    public string? OriginatorKeyAlgorithm { get; set; }
    public bool? InAudience { get; set; }

    /// <summary>The installation knows the certificate this recipient names.</summary>
    public bool IsKnown => Label is not null;
}

public sealed class SealSigner
{
    public string? Subject { get; set; }
    public string? Issuer { get; set; }
    public string? Serial { get; set; }
    public string DigestAlgorithm { get; set; } = string.Empty;
    public DateTimeOffset? SigningTime { get; set; }
    public bool? SignatureValid { get; set; }
}

public sealed class SealAudience
{
    public bool Matches { get; set; }
    public List<SealMissing> Missing { get; set; } = [];
}

public sealed class SealMissing
{
    public string Person { get; set; } = string.Empty;
    public string Thumbprint { get; set; } = string.Empty;
}

public sealed class SealAsn1Node
{
    public int Offset { get; set; }
    public int Depth { get; set; }
    public int HeaderLength { get; set; }
    public int Length { get; set; }
    public bool Constructed { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string? Value { get; set; }
}
