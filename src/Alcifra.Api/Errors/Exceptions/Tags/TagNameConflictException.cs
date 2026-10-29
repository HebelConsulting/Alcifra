using Microsoft.AspNetCore.Http;

namespace Alcifra.Api.Errors.Exceptions.Tags;

public sealed class TagNameConflictException : TagException
{
    public TagNameConflictException()
        : base("TAG_NAME_CONFLICT", StatusCodes.Status409Conflict, "A catalog tag with that name already exists.")
    {
    }
}
