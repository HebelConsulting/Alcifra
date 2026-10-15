using System.Text.Json;

namespace SimplArchive.ApiClient;

/// <summary>
/// A contact as the API's <c>contact-card</c> resource carries it, without any UI: what the desktop's contact form and
/// the phone's Address book (SimplArchiveMobile #46) both read and save (core ADR 0917: moved, not copied). Empty rows
/// are dropped on the way back rather than saved as blank properties.
/// </summary>
public sealed record ContactCard(
    string GivenName, string FamilyName, string Organization, string Title, string Birthday, string Url, string Note,
    string? StoredFormattedName,
    IReadOnlyList<ContactCard.Field> Emails, IReadOnlyList<ContactCard.Field> Phones, IReadOnlyList<ContactCard.Address> Addresses)
{
    /// <summary>An e-mail address or a phone number, with its type (work, home, mobile, …).</summary>
    public sealed record Field(string Value, string Type);

    public sealed record Address(string Type, string Street, string City, string Region, string PostalCode, string Country)
    {
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Street) && string.IsNullOrWhiteSpace(City) && string.IsNullOrWhiteSpace(Region)
            && string.IsNullOrWhiteSpace(PostalCode) && string.IsNullOrWhiteSpace(Country);
    }

    /// <summary>An empty card, for New.</summary>
    public static ContactCard Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, null, [], [], []);

    /// <summary>Reads the API's contact-card resource.</summary>
    public static ContactCard From(JsonElement body) => new(
        Text(body, "givenName"), Text(body, "familyName"), Text(body, "organization"), Text(body, "title"),
        Text(body, "birthday"), Text(body, "url"), Text(body, "note"),
        Text(body, "formattedName") is { Length: > 0 } fn ? fn : null,
        [.. Array(body, "emails").Select(r => new Field(Text(r, "value"), Text(r, "type") is { Length: > 0 } t ? t : "work"))],
        [.. Array(body, "phones").Select(r => new Field(Text(r, "value"), Text(r, "type") is { Length: > 0 } t ? t : "mobile"))],
        [.. Array(body, "addresses").Select(r => new Address(Text(r, "type") is { Length: > 0 } t ? t : "home",
            Text(r, "street"), Text(r, "city"), Text(r, "region"), Text(r, "postalCode"), Text(r, "country")))]);

    /// <summary>The body a PUT (or a create) sends.</summary>
    public object ToPayload() => new
    {
        formattedName = StoredFormattedName,
        givenName = Null(GivenName),
        familyName = Null(FamilyName),
        organization = Null(Organization),
        title = Null(Title),
        emails = Emails.Where(e => !string.IsNullOrWhiteSpace(e.Value)).Select(e => new { value = e.Value.Trim(), type = e.Type }).ToArray(),
        phones = Phones.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new { value = p.Value.Trim(), type = p.Type }).ToArray(),
        addresses = Addresses.Where(a => !a.IsEmpty).Select(a => new
        {
            type = a.Type,
            street = Null(a.Street),
            city = Null(a.City),
            region = Null(a.Region),
            postalCode = Null(a.PostalCode),
            country = Null(a.Country),
        }).ToArray(),
        birthday = Null(Birthday),
        url = Null(Url),
        note = Null(Note),
    };

    private static string? Null(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
}
