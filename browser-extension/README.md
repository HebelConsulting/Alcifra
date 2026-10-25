# SimplArchive autofill — browser extension (Chrome/Edge)

Fills logins from a SimplArchive password store and offers to keep a submitted one. The password never leaves the
machine except into the page you chose: names are in the clear (ADR 0964), the secret is read from the store with your
hardware key and its PIN.

## Install

1. **Load the extension** (it is not in the Web Store yet): open `chrome://extensions` (or `edge://extensions`),
   switch on **Developer mode**, choose **Load unpacked**, and pick this folder (`browser-extension/chrome`).
   The extension's **id** is shown on its card.
2. **Register a host**: install one of the clients and let it register itself against that id:

   ```sh
   sapass browser install chrome --extension-id <the id from step 1>
   ```

   The desktop client registers its own host name from its settings. Both may be installed; the extension asks the
   desktop's host first and falls back to sapass's (ADR 0978).
3. **Restart the browser** (native-messaging manifests are read at start), then sign in where a login form is.

## What it does

- Focusing a password field offers the entries whose indexed site matches the page (ADR 0973) — name and user name,
  never a secret. Picking one opens it with the key (a PIN prompt from the host) and fills both fields.
- **Generate a strong password** is offered in the same sheet; it is stored only when you save the login.
- A submitted login is offered for saving on the page that follows. The typed secret waits in
  `chrome.storage.session` — the extension's own store, unreadable by page scripts and cleared when the browser
  closes — for at most five minutes, and is dropped whether you save or decline.

## Limits of this first slice

- Chrome and Edge. Safari needs its own containing app and a Swift handler, which is a later slice.
- Saving files into **My Passwords** under the site's name (an update of a chosen entry is supported by the host
  protocol; the page flow always creates).
- Redirects to another origin after login lose the save prompt (it is keyed by origin).
