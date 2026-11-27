using System.Security.Cryptography;
using System.Text;

namespace Alcifra.Application.Identifiers;

/// <summary>
/// Name-based (RFC 4122 §4.3, version 5 style) GUIDs: the same namespace and name always produce the same id, so a
/// seeded document keeps its identity across a wipe and reseed (#781), and a caller-chosen slug keeps its identity
/// across resets (#2501).
/// </summary>
/// <remarks>
/// <para>
/// <b>The algorithm is frozen.</b> Changing it changes every derived id — every client cache and every bookmarked
/// address — so a change here is a deliberate migration, never a cleanup. Each NAMESPACE is frozen on its own terms:
/// the demo seeder's (<c>DemoId</c>) and the caller slugs' (<c>CallerDocumentIds</c>) are distinct constants, so the
/// two can never collide in one tenant although they share this one implementation.
/// </para>
/// </remarks>
public static class DeterministicIds
{
    /// <summary>The version-5 GUID of <paramref name="name"/> in <paramref name="namespaceId"/>.</summary>
    public static Guid V5(Guid namespaceId, string name)
    {
        // SHA-1 over the namespace GUID in network byte order + the name's UTF-8 bytes, then stamp version 5 and the
        // RFC variant into the first 16 hash bytes.
        var namespaceBytes = namespaceId.ToByteArray();
        SwapGuidByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);

        var hash = SHA1.HashData(input);
        var id = new byte[16];
        Array.Copy(hash, id, 16);
        id[6] = (byte)((id[6] & 0x0F) | 0x50); // version 5
        id[8] = (byte)((id[8] & 0x3F) | 0x80); // RFC variant

        SwapGuidByteOrder(id);
        return new Guid(id);
    }

    // Guid.ToByteArray emits the first three fields little-endian; RFC 4122 hashes network (big-endian) order.
    private static void SwapGuidByteOrder(byte[] guidBytes)
    {
        (guidBytes[0], guidBytes[3]) = (guidBytes[3], guidBytes[0]);
        (guidBytes[1], guidBytes[2]) = (guidBytes[2], guidBytes[1]);
        (guidBytes[4], guidBytes[5]) = (guidBytes[5], guidBytes[4]);
        (guidBytes[6], guidBytes[7]) = (guidBytes[7], guidBytes[6]);
    }
}
