using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Documents;

// A folder mask on what holds content, or an item mask on what holds children (#2169, ADR 1049).
public sealed class MaskKindMismatchException : DocumentException
{
    private MaskKindMismatchException(string message)
        : base("MASK_KIND_MISMATCH", StatusCodes.Status409Conflict, message)
    {
    }

    public static MaskKindMismatchException FolderMaskOnItem(string mask) =>
        new($"'{mask}' is a mask for folders, and this document holds content.");

    public static MaskKindMismatchException ItemMaskOnFolder(string mask) =>
        new($"'{mask}' is a mask for documents, and this folder holds items.");
}
