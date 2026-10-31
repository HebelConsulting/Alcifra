# alcifra — the Alcifra administrative CLI

## What Alcifra is

**Alcifra** is an open-source (Apache-2.0) **enterprise document management system**: an archive in which an
organisation files, finds, controls and keeps its documents. It covers the essentials of the category:

- **Repositories and folders**, with **index data** described by masks (typed fields per kind of document) and full
  **version history**.
- **Full-text search** across content, names and index data.
- **Permissions** per document, inherited down the tree, with groups, service accounts and clearance levels.
- An **approval workflow** with a task inbox, escalation and reminders.
- An append-only, **hash-chained audit trail**, with retention rules, **legal holds** and WORM storage.
- **Multi-tenancy**, **MFA and passkeys**, and optional **encryption at rest** with smartcard-based reading.
- **Standard protocols**, so an archive can be mounted as a drive (WebDAV) or read from a mail, calendar or address
  book client (IMAP, CalDAV, CardDAV).
- A **web client** and a cross-platform **desktop client**, and **industry modules** that extend the core per tenant.

It runs as a container image beside PostgreSQL, S3-compatible object storage and a search engine, with a Helm chart
for production. The project is also a showcase of how far an AI-assisted, senior-led development process can take an
enterprise-grade system: <https://github.com/HebelConsulting/Alcifra>

## What alcifra does

`alcifra` administers a Alcifra installation over its HTTP API, **following the links the API advertises**
rather than composing addresses, so it keeps working when routes move. It is how an installation is set up and
configured from a terminal or a script:

| Command | For |
|---|---|
| `login`, `whoami` | Sign in (interactively, or as a service account) and show who you are acting as |
| `tenant create`, `tenant standard-repository` | Provision a tenant with its first administrator; choose where Alcifra files what it brings |
| `tenant recovery-certificate show`, `set`, `replace` | The tenant's recovery certificate (ADR 0960): every offline export is also sealed to it, so an archive stays readable after its cards expire. Only the PUBLIC certificate is uploaded; `set` refuses where one is set, `replace` where none is. Without one, every offline export is refused (`RECOVERY_CERTIFICATE_REQUIRED`). `show --pem` prints the certificate for a script |
| `tenant offline-cache show`, `set` | What the tenant allows the phone app's offline cache: the cache at all, what it may keep, how it unlocks and refreshes, how long it may stay unlocked. It binds the apps, which honour it; a phone cannot be forced |
| `module list`, `module activate`, `module settings`, `module rebuild` | Activate industry modules with a vendor-signed licence, configure them, rebuild their read models |
| `module action` | Invoke a module's action on a document; a value it reveals once (a credential) is written to a mode-600 file |
| `module upload` | Upload a file through a module's upload link, such as a package or an app for its feed, found on the installation's root |
| `repository create` | Create a repository, or find the one of that name; prints its id |
| `user create` | Create a user, or find the one with that e-mail; prints the id. The password is read from stdin only |
| `user alias list`, `add`, `remove` | A user's identity aliases (ADR 1015): further addresses they are known by, on a verified identity domain. An alias does not sign in; Strict certificate binding accepts a certificate naming one |
| `mail-domain list`, `add`, `roles` | The tenant's domains and their roles: identity (account addresses and aliases live there), receiving (mail is filed), or both. `add` prints the DNS record that proves ownership |
| `certificates list`, `enrol`, `revoke`, `import` | Manage the reader certificates encrypted content is addressed to, including bulk import from a CA |
| `certificates policy show`, `set` | What a certificate must meet before it is enrolled; `set --binding strict\|possession` chooses how a certificate is tied to its holder: its address (strict, the default, ADR 0971) or an administrator's enrolment (possession, ADR 1017); `--san-must-match-account on\|off` is the earlier spelling; `--trust-anchors <file>\|none` names the authorities a certificate must chain to, one or several in a PEM file |
| `acl set`, `grant`, `revoke`, `break-inheritance` | Decide who may do what to a document; `break-inheritance` makes a document's own grants the ones that apply |
| `passwords store create`, `show`, `passwords entry create` | Set up a shared password store: create it, then file entries sealed on your machine to every certificate its readers hold. The password is read from stdin only (`printf '%s' "$pw" \| alcifra passwords entry create --store <id> --name … --password-stdin`); a re-run changes nothing |
| `me certificate`, `intray previous` | A user's own certificate, and recovering intray content |

Every command has `--help`. For example, to fix how phones unlock their offline cache and keep the rest:

```sh
alcifra tenant offline-cache set --unlock-mode Session --max-unlocked-minutes 10
alcifra tenant offline-cache show
```

## Install and sign in

```sh
dotnet tool install --global Alcifra.Cli

# the earlier ids SimplArchive.Cli and HebelConsulting.SimplArchive.Cli install `saconsole`; replace them once:
#   dotnet tool uninstall --global SimplArchive.Cli

# interactive: approve the sign-in in your browser, then the session is set for this shell
eval "$(alcifra login --url https://archive.example.com)"
alcifra whoami
```

For unattended use, sign in as a tenant **service account** with `alcifra login --client-id <id>`, with the secret
in `ALCIFRA_CLIENT_SECRET` (from the environment, never as an argument; `SACONSOLE_CLIENT_SECRET` is still read until v0.56.0).

Requires the .NET 10 runtime. Source, documentation and issues: <https://github.com/HebelConsulting/Alcifra>

## Output

What is meant for a person goes to **stderr**, as plain lines (`warning:` and `error:` mark those levels); add
`--verbose` for diagnostic detail. **stdout carries only a command's result**: the exports of `login`, the id of
`repository create`, `passwords store create` and `entry create` and `tenant standard-repository show`, the entry names of `passwords store show`, the one-time password of `tenant create`. So
`id=$(alcifra repository create --name Customers)` captures exactly the id, and `2>/dev/null` silences the rest.
