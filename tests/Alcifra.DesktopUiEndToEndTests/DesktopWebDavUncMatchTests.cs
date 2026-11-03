using Alcifra.DesktopClient.Services;

namespace Alcifra.UiEndToEndTests;

// "Is this mapped drive OUR server?" answered from the remote UNC's host (#820) — the same host rule the
// macOS mount matching follows, replacing a volume-label test that a WebDAV mapping (commonly label-less)
// never satisfied. Pure, so every wire shape Windows produces is pinned here without a Windows machine:
// the @SSL marker, explicit ports, default ports by scheme, case, and the DavWWWRoot tail.
public class DesktopWebDavUncMatchTests
{
    [Theory]
    [InlineData($@"\\demo.simplarchive.dev@SSL@443\DavWWWRoot\{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", $"https://demo.simplarchive.dev/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", true)]
    [InlineData($@"\\demo.simplarchive.dev@SSL\DavWWWRoot\{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", $"https://demo.simplarchive.dev/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", true)]  // @SSL without an explicit port implies 443
    [InlineData($@"\\DEMO.SIMPLARCHIVE.DEV@SSL@443\DavWWWRoot\{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", "https://demo.simplarchive.dev/", true)]          // host is case-insensitive
    [InlineData($@"\\localhost@8080\DavWWWRoot\{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", $"http://localhost:8080/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", true)]
    [InlineData($@"\\localhost@80\DavWWWRoot\{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", $"http://localhost/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", true)]
    [InlineData($@"\\other.example.org@SSL@443\DavWWWRoot\{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", "https://demo.simplarchive.dev/", false)]             // another server's mount is NOT ours (the macOS lesson)
    [InlineData($@"\\demo.simplarchive.dev@SSL@8443\DavWWWRoot\{(global::Alcifra.Presentation.WireNames.WebDavSegment)}", "https://demo.simplarchive.dev/", false)]        // same host, different port = a different deployment
    [InlineData(@"\\fileserver\share", "https://demo.simplarchive.dev/", false)]                                              // an ordinary SMB mapping
    public void A_remote_unc_matches_exactly_our_server(string remote, string server, bool expected)
        => Assert.Equal(expected, OsFileManager.UncMatchesServer(remote, new Uri(server)));

    // With an address per account (#2128), two accounts on one server are two mappings, told apart by the path.
    [Theory]
    [InlineData(@"\\demo.alcifra.dev@SSL@443\DavWWWRoot\webdav\11111111-1111-1111-1111-111111111111\22222222-2222-2222-2222-222222222222\Florian (Crypto)",
        "https://demo.alcifra.dev/webdav/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/Florian%20(Crypto)", true)]
    [InlineData(@"\\demo.alcifra.dev@SSL@443\DavWWWRoot\webdav\11111111-1111-1111-1111-111111111111\33333333-3333-3333-3333-333333333333\Anna (Crypto)",
        "https://demo.alcifra.dev/webdav/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/Florian%20(Crypto)", false)]   // another account
    [InlineData(@"\\demo.alcifra.dev@SSL@443\DavWWWRoot\webdav", "https://demo.alcifra.dev/webdav", true)]                                     // the plain address
    public void A_mapping_is_this_address_only_when_its_path_is(string remote, string address, bool expected)
        => Assert.Equal(expected, OsFileManager.UncMatchesAddress(remote, new Uri(address)));

    [Theory]
    [InlineData("dav:host=demo.alcifra.dev,ssl=true,prefix=%2Fwebdav%2Ft%2Fu%2FFlorian%20(Crypto)", "https://demo.alcifra.dev/webdav/t/u/Florian%20(Crypto)", true)]
    [InlineData("dav:host=demo.alcifra.dev,ssl=true,prefix=%2Fwebdav%2Ft%2Fother%2FAnna", "https://demo.alcifra.dev/webdav/t/u/Florian%20(Crypto)", false)]
    [InlineData("dav:host=other.example,ssl=true,prefix=%2Fwebdav", "https://demo.alcifra.dev/webdav", false)]
    [InlineData("smb-share:server=files,share=x", "https://demo.alcifra.dev/webdav", false)]
    public void A_gvfs_mount_is_this_address_only_when_host_and_prefix_are(string name, string address, bool expected)
        => Assert.Equal(expected, OsFileManager.GvfsNameMatches(name, new Uri(address)));

    // Measured on macOS 2026-11-03: the mount table lists a per-account address cut at 89 characters.
    [Theory]
    [InlineData("http://127.0.0.1:8099/webdav/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222",
        "http://127.0.0.1:8099/webdav/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/Florian%20(Crypto)", true)]
    [InlineData("http://127.0.0.1:8099/webdav/11111111-1111-1111-1111-111111111111/33333333-3333-3333-3333",
        "http://127.0.0.1:8099/webdav/11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/Florian%20(Crypto)", false)]   // another account, also cut
    [InlineData("https://demo.alcifra.dev/webdav/", "https://demo.alcifra.dev/webdav", true)]                                                // the plain address, uncut
    [InlineData("https://demo.alcifra.dev/webdav", "https://demo.alcifra.dev/webdav/t/u/Florian%20(Crypto)", false)]                          // a SHORT source is not a cut one
    [InlineData("https://demo.alcifra.dev/webdav/t/u/Florian (Crypto)", "https://demo.alcifra.dev/webdav/t/u/Florian%20(Crypto)", true)]     // listed unescaped
    public void A_listed_mount_source_is_this_address_even_when_the_table_cut_it(string listed, string address, bool expected)
        => Assert.Equal(expected, OsFileManager.MountSourceIs(listed, address));
}
