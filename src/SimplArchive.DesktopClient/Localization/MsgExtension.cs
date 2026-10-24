using System;
using Avalonia.Markup.Xaml;
using SimplArchive.Messenger.Client.Display;

namespace SimplArchive.DesktopClient.Localization;

// XAML markup extension: {loc:Msg Key} resolves to the Messenger's own text for Key (ViewText, in the same four
// languages), so a label the phone's Messenger shows reads the same on the desktop and is translated once, in the
// Messenger client, rather than copied into Strings.resx (ADR 0967). A key it does not know throws at load.
public sealed class MsgExtension : MarkupExtension
{
    public MsgExtension()
    {
    }

    public MsgExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => ViewText.Get(Key);
}
