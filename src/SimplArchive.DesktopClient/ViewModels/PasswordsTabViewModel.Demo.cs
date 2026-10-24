using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>What the headless screenshot of the Passwords tab shows (ADR 0968).</summary>
public enum PasswordsDemo
{
    /// <summary>An entry opened in the preview, a find query lighting its visible text.</summary>
    Entry,

    /// <summary>The edit form with the generator open.</summary>
    Edit,
}

// The headless screenshot's demo (Program's --screenshot --passwords): a store, a folder, an opened entry, no server and no
// key — the fields are set here as the key would have opened them.
public sealed partial class PasswordsTabViewModel
{
    public void PopulateDemoForScreenshot(PasswordsDemo demo)
    {
        IsOffered = true;
        static LinkMap Links(string self, bool create = true) => LinkMap.FromHrefs(create
            ? new Dictionary<string, string> { ["self"] = self, ["password-entries"] = $"{self}/entries", ["password-folders"] = $"{self}/folders" }
            : new Dictionary<string, string> { ["self"] = self });
        PasswordItemInfo Row(string name, bool folder) =>
            new(Guid.NewGuid(), name, folder, "\"1\"", true, true, true, true, Links($"demo/{name}", create: false));

        var storeId = Guid.NewGuid();
        var banking = Row("Banking", folder: true);
        var codeHosting = Row("Code hosting", folder: false);
        var items = new List<PasswordItemInfo> { banking, Row("Work", folder: true), codeHosting, Row("Mail server", folder: false), Row("Wi-Fi office", folder: false) };
        var mine = new PasswordNodeViewModel("My Passwords", isStore: true, isPersonal: true, "demo/mine", null, null);
        mine.Read(new PasswordFolderInfo(storeId, "My Passwords", true, storeId, true, false, false, 1, "\"1\"", items, Links("demo/mine")));
        var team = new PasswordNodeViewModel("Operations", isStore: true, isPersonal: false, "demo/ops", null, null);
        Nodes.Clear();
        mine.IsExpanded = true;
        Nodes.Add(mine);
        Nodes.Add(team);
        SelectedNode = mine;
        Items.Clear();
        foreach (var item in items.OrderBy(i => !i.IsFolder).ThenBy(i => i.Name))
        {
            Items.Add(new PasswordItemRowViewModel(item));
        }

        Notices.Clear();
        foreach (var notice in PasswordNotices.For(mine.Folder!))
        {
            Notices.Add(new PasswordNoticeViewModel(notice));
        }

        var content = new PasswordEntryContent("c0rrect-H0rse-battery!",
        [
            new PasswordEntryField(PasswordEntryFieldNames.Url, "https://code.example/login"),
            new PasswordEntryField(PasswordEntryFieldNames.UserName, "anna.berger"),
            new PasswordEntryField("PIN", "482913", IsSecret: true),
            new PasswordEntryField("Recovery codes", "a1b2-c3d4 e5f6-g7h8", IsSecret: true),
            new PasswordEntryField(PasswordEntryFieldNames.Notes, "Two-factor with the hardware key.\nRecovery codes in the safe."),
        ]);
        var entry = new PasswordEntryInfo(codeHosting.Id, "Code hosting", storeId, storeId, 3, "\"1\"", false, null, true, true, true, true, LinkMap.Empty);
        SelectedItem = Items.First(i => i.Item.Id == codeHosting.Id);
        _opened = new OpenedPasswordEntry(entry, content);
        Preview.ShowPasswordEntry(new PasswordEntryPreviewViewModel("Code hosting", content, entry, _services, isReadOnly: false));
        EntryOpen = true;
        if (demo == PasswordsDemo.Entry)
        {
            Preview.FindQuery = "code";
            return;
        }

        var editor = PasswordEntryEditorViewModel.Edit(_opened, (_, _) => Task.FromResult(entry), Screens, (_, _) => Task.CompletedTask);
        editor.GeneratorOpen = true;
        editor.Generator.Length = "24";
        editor.Generator.AvoidAmbiguous = true;
        editor.Generator.Generate();
        Editor = editor;
    }
}
