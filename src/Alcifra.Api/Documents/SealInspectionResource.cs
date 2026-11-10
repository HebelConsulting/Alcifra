using Alcifra.ModuleAbi;

namespace Alcifra.Api.Documents;

/// <summary>
/// What a document's sealed content is and who can open it (#2291, ADR 1029's reader): its current version as stored,
/// never decrypted. Reached by the document's <c>seal-inspection</c> rel. The web shows it because the browser cannot read
/// CMS, and the desktop shows the same answer for a document so one question has one answer.
/// </summary>
/// <remarks>Plain mutable classes, as every resource the core serves: responses negotiate to XML too.</remarks>
public class SealInspectionResource : HypermediaResource
{
    /// <summary><c>Enveloped</c>, <c>AuthEnveloped</c>, <c>Signed</c>, <c>Archive</c> or <c>NotSealed</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The format's name, as a person reads it.</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>The CMS arrived inside an S/MIME message, as the server serves a Sealed document.</summary>
    public bool SmimeWrapped { get; set; }

    public long Size { get; set; }

    public string? ContentType { get; set; }

    public string? ContentEncryption { get; set; }

    /// <summary>Whom the envelope names, each matched to the installation's certificate where the caller may see it.</summary>
    public List<SealRecipientResource> Recipients { get; set; } = [];

    public List<SealSignerResource> Signers { get; set; } = [];

    /// <summary>The certificates a signed file carries, by subject.</summary>
    public List<string> Certificates { get; set; } = [];

    /// <summary>The gap between the envelope and the declared audience; null where the document has no audience.</summary>
    public SealAudienceResource? Audience { get; set; }

    /// <summary>The CMS structure, node by node, with offsets and lengths; empty where there is none.</summary>
    public List<SealAsn1NodeResource> Asn1 { get; set; } = [];
}

/// <summary>One recipient, as the envelope names it, and who that is where the installation knows the certificate.</summary>
public class SealRecipientResource
{
    /// <summary>The holder, as the certificate listing's rights allow (an administrator every holder, a user themself).</summary>
    public string? HolderName { get; set; }

    public string? HolderEmail { get; set; }

    /// <summary>The key's label; null for an unknown recipient.</summary>
    public string? Label { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>"attested", "rootNotTrusted" or "notAttested"; null for an unknown recipient.</summary>
    public string? Attestation { get; set; }

    public DateTimeOffset? NotAfter { get; set; }

    public string? Issuer { get; set; }

    public string? Serial { get; set; }

    public string? SubjectKeyIdentifier { get; set; }

    /// <summary><c>key transport</c> (RSA) or <c>key agreement</c> (ECDH).</summary>
    public string Method { get; set; } = string.Empty;

    public string KeyEncryptionAlgorithm { get; set; } = string.Empty;

    public string? OriginatorKeyAlgorithm { get; set; }

    /// <summary>False: no longer in the audience, yet able to open this version. Null where there is no audience.</summary>
    public bool? InAudience { get; set; }
}

public class SealSignerResource
{
    public string? Subject { get; set; }

    public string? Issuer { get; set; }

    public string? Serial { get; set; }

    public string DigestAlgorithm { get; set; } = string.Empty;

    public DateTimeOffset? SigningTime { get; set; }

    /// <summary>Whether the signature holds; null for a detached signature.</summary>
    public bool? SignatureValid { get; set; }
}

/// <summary>Whether the envelope matches the declared audience, and who is missing from it.</summary>
public class SealAudienceResource
{
    public bool Matches { get; set; }

    /// <summary>In the audience but not in the envelope: this version needs a re-seal for them.</summary>
    public List<SealMissingResource> Missing { get; set; } = [];
}

public class SealMissingResource
{
    public string Person { get; set; } = string.Empty;

    public string Thumbprint { get; set; } = string.Empty;
}

public class SealAsn1NodeResource
{
    public int Offset { get; set; }

    public int Depth { get; set; }

    public int HeaderLength { get; set; }

    public int Length { get; set; }

    public bool Constructed { get; set; }

    public string Tag { get; set; } = string.Empty;

    public string? Value { get; set; }
}
