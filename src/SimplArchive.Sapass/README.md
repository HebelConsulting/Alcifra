# sapass

A pass-like password store in a terminal: entries live in a SimplArchive password store — sealed on this machine to
every reader's certificates, with names in the clear and the secrets inside an S/MIME envelope — and are opened only
with your hardware key and its PIN.

```sh
dotnet tool install --global SimplArchive.Sapass

eval "$(sapass login --url https://archive.example.com)"   # approve a code in your browser
sapass ls                                                  # My Passwords, folders and entries
sapass find bank
sapass show Mail/Banking                                   # password on the first line, fields after it
```

- `--store <name>` reads a shared store (the default is My Passwords). Where two stores share a name, give the full
  name the apps show, `Parent / Name` (e.g. `Anna Berger / My Passwords`); the plain name is refused as ambiguous — except
  `My Passwords`, which always means your own.
- `--refresh` re-reads the tree instead of using the five-minute cache.
- Completion: `sapass completion bash|zsh|fish` prints the script; it completes commands, store names and paths.

Nothing is written to the installation's servers in clear: the content is sealed to the store's readers by the shared
client, and the server stores only the envelope. The PIN is never stored — the card holds the session.
