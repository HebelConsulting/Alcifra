using System.ComponentModel;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

/// <summary>The options every store command shares: which store, and whether to re-read its tree.</summary>
public class StoreSettings : CommandSettings
{
    [CommandOption("-s|--store <NAME>")]
    [Description("The password store, by name, or by its full 'Parent / Name' where names repeat. Defaults to My Passwords.")]
    public string? Store { get; init; }

    [CommandOption("--refresh")]
    [Description("Read the tree from the installation instead of the local cache (five minutes by default).")]
    public bool Refresh { get; init; }
}
