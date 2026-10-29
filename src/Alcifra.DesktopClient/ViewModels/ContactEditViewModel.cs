using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>One typed multi-value row (an e-mail or a phone number).</summary>
public sealed partial class ContactFieldRowViewModel : ObservableObject
{
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private string _type = "work";

    /// <summary>The three the form offers. vCard allows many more; these are the ones a person picks.</summary>
    public static IReadOnlyList<string> Types { get; } = ["home", "work", "mobile"];
}

/// <summary>One postal address. Every part optional — a card may carry only a city.</summary>
public sealed partial class ContactAddressRowViewModel : ObservableObject
{
    [ObservableProperty] private string _type = "home";
    [ObservableProperty] private string _street = string.Empty;
    [ObservableProperty] private string _city = string.Empty;
    [ObservableProperty] private string _region = string.Empty;
    [ObservableProperty] private string _postalCode = string.Empty;
    [ObservableProperty] private string _country = string.Empty;

    public static IReadOnlyList<string> Types { get; } = ["home", "work"];
}

/// <summary>
/// The contact edit form's state (#564, ADR 0631). Holds only the fields the form models — everything else on
/// the stored card is preserved by the server's merge and never travels through here.
/// </summary>
public sealed partial class ContactEditViewModel : StructuredEditFormViewModel
{
    [ObservableProperty] private string _givenName = string.Empty;
    [ObservableProperty] private string _familyName = string.Empty;
    [ObservableProperty] private string _organization = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _birthday = string.Empty;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _note = string.Empty;

    public ObservableCollection<ContactFieldRowViewModel> Emails { get; } = [];

    public ObservableCollection<ContactFieldRowViewModel> Phones { get; } = [];

    public ObservableCollection<ContactAddressRowViewModel> Addresses { get; } = [];

    /// <summary>
    /// The card's FN as stored. Kept rather than recomposed: a contact whose display name is deliberately not
    /// "given family" — a company, a person with one name, a name in an order we do not model — must not have
    /// it rewritten because somebody edited their phone number.
    /// </summary>
    public string? StoredFormattedName { get; set; }

    [RelayCommand]
    public void AddEmail() => Emails.Add(new ContactFieldRowViewModel { Type = "work" });

    [RelayCommand]
    public void RemoveEmail(ContactFieldRowViewModel row) => Emails.Remove(row);

    [RelayCommand]
    public void AddPhone() => Phones.Add(new ContactFieldRowViewModel { Type = "mobile" });

    [RelayCommand]
    public void RemovePhone(ContactFieldRowViewModel row) => Phones.Remove(row);

    [RelayCommand]
    public void AddAddress() => Addresses.Add(new ContactAddressRowViewModel());

    [RelayCommand]
    public void RemoveAddress(ContactAddressRowViewModel row) => Addresses.Remove(row);

    /// <summary>Reads the API's contact-card resource into the form (the mapping is the shared <see cref="ContactCard"/>).</summary>
    public static ContactEditViewModel From(JsonElement body) => From(ContactCard.From(body));

    public static ContactEditViewModel From(ContactCard card)
    {
        var model = new ContactEditViewModel
        {
            GivenName = card.GivenName,
            FamilyName = card.FamilyName,
            Organization = card.Organization,
            Title = card.Title,
            Birthday = card.Birthday,
            Url = card.Url,
            Note = card.Note,
            StoredFormattedName = card.StoredFormattedName,
        };
        foreach (var e in card.Emails)
        {
            model.Emails.Add(new ContactFieldRowViewModel { Value = e.Value, Type = e.Type });
        }

        foreach (var p in card.Phones)
        {
            model.Phones.Add(new ContactFieldRowViewModel { Value = p.Value, Type = p.Type });
        }

        foreach (var a in card.Addresses)
        {
            model.Addresses.Add(new ContactAddressRowViewModel
            {
                Type = a.Type,
                Street = a.Street,
                City = a.City,
                Region = a.Region,
                PostalCode = a.PostalCode,
                Country = a.Country,
            });
        }

        return model;
    }

    /// <summary>The form as the shared card.</summary>
    public ContactCard ToCard() => new(GivenName, FamilyName, Organization, Title, Birthday, Url, Note, StoredFormattedName,
        [.. Emails.Select(e => new ContactCard.Field(e.Value, e.Type))],
        [.. Phones.Select(p => new ContactCard.Field(p.Value, p.Type))],
        [.. Addresses.Select(a => new ContactCard.Address(a.Type, a.Street, a.City, a.Region, a.PostalCode, a.Country))]);

    /// <summary>The body a PUT sends back. Empty rows are dropped rather than saved as blank properties.</summary>
    public object ToPayload() => ToCard().ToPayload();
}

