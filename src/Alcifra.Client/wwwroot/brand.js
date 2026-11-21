// The runtime brand's tab icon (ADR 1103, #2408). The asset endpoint is bearer-protected, so the web client fetches
// the pack's favicon with the signed-in client and hands the bytes here as a data URL — a raw <link href> would be
// an anonymous request and answer 401. Leaves the installation's icon alone when no brand pack is in force.
window.simplArchiveBrand = {
    setFavicon: function (url) {
        var link = document.querySelector('link[rel="icon"]');
        if (!link) {
            link = document.createElement('link');
            link.rel = 'icon';
            document.head.appendChild(link);
        }
        link.href = url;
    }
};
