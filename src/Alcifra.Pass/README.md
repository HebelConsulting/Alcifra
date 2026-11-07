# apass

A pass-like password store in a terminal: entries live in a Alcifra password store — sealed on this machine to
every reader's certificates, with names in the clear and the secrets inside an S/MIME envelope — and are opened only
with your hardware key and its PIN.

```sh
dotnet tool install --global Alcifra.Pass

apass login --url https://archive.example.com             # approve a code in your browser
apass ls                                                  # My Passwords, folders and entries
apass find bank
apass show Mail/Banking                                   # every field in the form's order: URL, user name, password, …
apass password Mail/Banking                               # the password alone, for $(…)
apass otp Mail/Banking                                    # the current code; "valid 17 s" on the next line
apass otp Mail/Banking --code-only                        # the code alone, for $(…)
apass password Mail/Banking -c                            # onto the clipboard instead, cleared after 45 s
apass otp Mail/Banking -c                                 # the code onto the clipboard, cleared when it expires
```

- `--store <name>` reads a shared store (the default is My Passwords). Where two stores share a name, give the full
  name the apps show, `Parent / Name` (e.g. `Anna Berger / My Passwords`); the plain name is refused as ambiguous — except
  `My Passwords`, which always means your own.
- `--refresh` re-reads the tree instead of using the five-minute cache.
- Completion: `apass completion bash|zsh|fish` prints the script; it completes commands, store names and paths. Add `eval "$(apass completion zsh)"` to `~/.zshrc`, or the bash line to `~/.bashrc`. Without it a TAB completes local files.

Nothing is written to the installation's servers in clear: the content is sealed to the store's readers by the shared
client, and the server stores only the envelope. The PIN is never stored — the card holds the session.
