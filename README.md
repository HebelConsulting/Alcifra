# Alcifra

**A showcase of how a senior, AI-driven software developer can produce a complex, enterprise-grade system in a relatively short period of time.**

**Alcifra** is a multi-tenant **sovereign workspace**: a document management system (DMS) with encryption to each person's own key and open protocols (IMAP, WebDAV, CalDAV, CardDAV) for bring-your-own-device. It is a working system, built end-to-end as a demonstration of AI-assisted software engineering: Clean Architecture, a hypermedia REST API, a web app, a desktop app and phone apps, OAuth/OIDC sign-in with MFA and passkeys, full-text + OCR search, document previews, versioning, ACLs, workflow, audit trails, legal hold & retention, a **fully localized UI (English, German, Italian, Spanish)**, and a production Helm chart — with an extensive automated test suite (unit, integration, container-backed E2E, and browser/desktop UI E2E) and a hardened CI pipeline.

**Website:** [www.alcifra.com](https://www.alcifra.com) — features, manuals and downloads · **Demo:** [demo.alcifra.dev](https://demo.alcifra.dev) · **Downloads:** this repository's [Releases](https://github.com/HebelConsulting/Alcifra/releases)

## What's in it

- **The workbench, on the web and on the desktop** — tree, list, index data, preview and chat side by side; drag and drop to file; versions you can compare; check-out and check-in; approval workflows with a task inbox; tags, reminders, references and external links; calendars and address books; an intray that straightens and splits scans.
- **Phone and tablet apps** for Android, iPhone and iPad — coming to the app stores. The archive's tree, previews and search on the go; sealed documents opened with a hardware key over NFC or USB-C; the Password Store with autofill in other apps and the browser.
- **Your own devices, with a password each** — mail, files, calendars and contacts over the open protocols, each device enrolled under *Devices…* with its own password, a QR code for the password and a one-time setup profile a phone's camera can install. Revoking a lost phone cuts off that phone only.
- **A Password Store** — logins kept in the archive, sealed on your machine to the hardware keys of the people allowed to read them: folders, shared stores with their own audience, verification codes (TOTP), import from a password manager's export, autofill in the browser through an extension, and `apass`, a command-line tool in the spirit of `pass`.
- **Sealed documents and offline exports** — a document sealed to its audience's keys before it leaves your machine, and a folder exported as one archive sealed to its recipients and to the organisation's recovery certificate.
- **Modules** — features added per tenant: the **Messenger** (chat sealed to its members' keys; open source, in its own repository, and free), **encryption** (paid, below) and **industry modules** for a trade's own workflows, such as a flight school's bookings, aircraft and training records.

## ▶ Try the live demo

A live instance is running at **<https://demo.alcifra.dev>** — log in and explore the archive of a fictional company, Kristall Holding AG; nothing to install. Three people are advertised, so you can also see the app from a colleague's side:

| Sign in as | Email | Password | What it's for |
|---|---|---|---|
| **Demo Admin** | `admin@demo.alcifra.dev` | `SimplDemo2026!` | The tenant administrator — start here. Sees everything: administration, audit, retention, legal holds, every user's inbox. |
| **Anna Meyer** | `anna@demo.alcifra.dev` | `SimplDemo2026!` | Works in Accounting: edits the whole archive and shares the *Accounting* group inbox, but is a **plain user without tenant-admin rights**. Signing in as a second person is how the collaboration side shows up: chat and mentions from someone else, a workflow review arriving in another inbox. |
| **Tom Fischer** | `tom@demo.alcifra.dev` | `SimplDemo2026!` | The assistant: may **read the archive but not change it** — the account to use if you want to watch a permission actually deny something. |

These are deliberately trivial, publicly posted credentials on a throwaway demo that wipes itself nightly — they are demo furniture, not an example of how the product handles secrets.

It resets to a clean, known state every night, so feel free to create, upload, workflow, and delete anything. The user manual is one click away at [demo.alcifra.dev/download/manual/](https://demo.alcifra.dev/download/manual/).

> **This demo is not a deployment.** The kiosk above runs in Development with development certificates, a published password and a nightly wipe — it is furniture for exploring, and nothing about it describes how the product is deployed.
>
> **The product itself is built to run for real:** a Helm chart for Kubernetes, a fully automated AWS installation, secrets and certificates from OpenBao, backup/DR scripts, and a readiness gate that **refuses to start outside Development** when any dev-grade setting is present — with no bypass flag. What remains ahead is operational maturity (live-cluster HA/DR validation, scheduled offsite backups, zero-downtime schema changes), not basic readiness.

## Run it locally (Docker Compose)

The whole stack — API + web/desktop-serving host, Postgres, S3-compatible object storage, OpenSearch + Tika, Gotenberg, OCR, mail catcher, secret store, and a pgAdmin UI — runs from one file:

```bash
docker compose up --build
```

Then open **http://localhost:8080** and log straight into the UI with the seeded demo account:

- **Email:** `admin@alcifra.local`
- **Password:** `SimplDemo2026!`

Two further seeded users share that password — **`anna@alcifra.local`** (Anna Meyer, in Accounting: she edits the archive and shares the *Accounting* group inbox) and **`tom@alcifra.local`** (Tom Fischer, the assistant, who may only read). Signing in as one of them is how you see the collaboration side of the app — chat and mentions from another person, a workflow review arriving in someone else's inbox — and, as Tom, a permission actually being refused. Neither is a tenant admin.

It comes pre-seeded with a demo tenant holding the archive of a fictional Kristall Holding AG — contracts, invoices under review, versions to compare, customers' contacts and calendars — so there's something to explore immediately. (The same file runs under Podman: `podman compose up --build`.) Handy dev UIs: pgAdmin at http://localhost:5050 (auto-connected to the database) and the Mailpit inbox at http://localhost:8025.

The entire UI — both clients plus the shared sign-in page — is available in **English, German, Italian, and Spanish**. Switch language from the flag menu in the web app bar (next to the notifications bell); the desktop app picks it on the logon window.

To try it from **another device on your network** (a phone, a second laptop), set `PROXY_HOST` to this machine's LAN IP/hostname and `S3_PUBLIC_URL` to the matching object-storage proxy URL — an optional Caddy reverse proxy then serves both the app (port 9443) and object storage (port 9444) over HTTPS:

```bash
PROXY_HOST=192.168.1.50 S3_PUBLIC_URL=https://192.168.1.50:9444 docker compose up --build
```

On the device, first visit **https://192.168.1.50:9444** once and accept the self-signed-certificate warning (needed so document previews/uploads work), then browse **https://192.168.1.50:9443** and accept its warning too. (Dev/test only.)

## Desktop app

Alongside the web client, Alcifra ships a cross-platform **desktop client** for macOS, Windows and Linux: opening a document in its native application, a mounted file drive, card-held keys, and everything the web workbench does. Every release attaches self-contained packages to this repository's [Releases](https://github.com/HebelConsulting/Alcifra/releases) — no .NET needed on the target machine. The **macOS `.dmg` is signed and notarized**; the Windows and Linux packages are unsigned, so SmartScreen warns on first run ("More info" → "Run anyway") and the Linux launcher may need `chmod +x`.

To build them yourself:

```bash
scripts/package-macos-dmg.sh          # macOS → dist/Alcifra-<version>-{arm64,x64}.dmg        (run on macOS)
scripts/package-windows-linux.sh      # Win/Linux x64 → dist/Alcifra-<version>-win-x64.zip + -linux-x64.tar.gz
```

A local macOS build without a signing identity is signed ad hoc; the Windows/Linux script cross-builds from any OS with the .NET SDK.

## Connect your own apps — calendars, contacts, notes, files

Your archive also speaks the protocols your existing apps already know, so calendars, contacts, notes and files show up alongside everything else on your devices rather than only in the workbench:

| What | Where to point the app | Username | Password to use |
|---|---|---|---|
| **Calendars** (CalDAV) | `https://demo.alcifra.dev/caldav` | your email | a device password |
| **Contacts** (CardDAV) | `https://demo.alcifra.dev/carddav` | your email | a device password |
| **Notes** (IMAP) | `demo.alcifra.dev`, port **993**, TLS | your email | a device password |
| **Files** (WebDAV) | `https://demo.alcifra.dev/webdav` | your email | a device password |

**Use a device password, never your login password.** Open *Devices…* in the account menu (top right) and *Enrol a device…*: each device gets its own password, for whichever of mail, files, calendars and contacts you switch on, shown once with a QR code and every server address. A phone can scan a second code to set up its accounts itself. Revoking a device cuts off that device only, without touching your login or your other devices.

Discovery is supported, so on most apps the host alone is enough and the paths above are the fallback when it asks for a full URL.

**Where your documents show up over IMAP.** Every folder is a mailbox, and each document in it is a message with the file attached. **`INBOX` is your personal space's root**, which normally holds only folders — so an empty inbox is the expected first impression, not a broken sync. The archive's contents are in the *sibling* mailboxes (`Demo Repository/Invoices`, `Templates`, …). By default a mail app shows only emails; turn on **Devices ▸ Mail shows all documents** in the account menu to have every file appear as well.

- **Apple devices** — *System Settings ▸ Internet Accounts ▸ Add Account ▸ Other*, then add a CalDAV or CardDAV account. For notes, add a Mail account and enable **Notes** on it. Worth knowing: the accounts framework quietly refuses plaintext IMAP *and* untrusted certificates — no error is shown, the account simply never syncs — so the TLS port and a trusted certificate are requirements here, not preferences.
- **Android** — the built-in accounts do not speak CalDAV/CardDAV; install a sync app that does and give it the URL, username and app password above. Notes need no extra app: any mail client that supports IMAP will show them.
- **Desktop mail/calendar clients** — usually the smoothest route on any operating system, since one client covers all three protocols. Add a calendar/address book by URL, and a mail account for notes.

**Files (WebDAV) is not universal.** A desktop file manager mounts it directly; **iPhone/iPad have no built-in WebDAV support**, so a third-party file client is needed there.

Running your own instance instead of the demo? Substitute your host; the paths and the account-menu steps are identical.

## Install in production

Production deploys via the **Helm chart** in [`charts/alcifra`](charts/alcifra) — an API Deployment/Service/Ingress/HPA/PDB with health probes, non-root containers, and secret wiring; dependencies (Postgres, object storage, OpenSearch, …) are external/managed. The chart's [`values.yaml`](charts/alcifra/values.yaml) documents the full configuration surface, and pre-install/pre-upgrade migration hooks apply schema changes off the app's startup path.

Every release publishes the chart, versioned with the release so it installs exactly that release's images: as an
OCI artifact and as a `.tgz` on the release.

```sh
helm upgrade --install alcifra oci://ghcr.io/hebelconsulting/charts/alcifra \
  --version X.Y.Z -n alcifra --create-namespace -f values-production.yaml
```

## Encryption — two paid extras

Encryption for Alcifra comes as **two separate products**, and the line between them is not
packaging: it is **whether the component holds a secret.**

**The Encryption Module — encrypted delivery.** Content leaves the archive as an envelope addressed to the
reader's own certificates, of which a person may hold several — one per device, so a smartcard in a computer and
an identity on a phone can both be addressed at once. It holds **nothing**: an envelope is built from *public*
certificates and the private keys never leave the card or device they were created on. Because there is no key
to guard, it needs no hardware security module and no second process — it installs into the archive itself, per
tenant, with a licence.

**The Encryption Service — encryption at rest.** Document content is encrypted where it is stored, with
per-object keys wrapped by an installation key held in a hardware security module the operating organisation
controls; rotating that key re-wraps the keys rather than rewriting the documents. This one *does* hold a
secret — the key that decrypts the archive — which is exactly why it is a separate process with its own
security posture.

They are bought independently, and the level is set **per tenant**, so an archive that needs either can run
beside one that does not:

| | encrypted at rest | encrypted on delivery |
|---|---|---|
| neither | — | — |
| Service | ✓ | — |
| Module | — | ✓ |
| both | ✓ | ✓ |

On top of that, a **strict** level for organisations such as government and defence, where the
archive serves no readable content at all — every read is encrypted to the certificate on the reader's
smartcard, and full-text search across document contents is given up in exchange, while names, index fields and
dates stay searchable.

It is **custody, not mathematics**: with the Service, content is never stored in readable form, the keys live in
hardware the organisation controls, and every decryption is recorded — but it *can* decrypt, because that is
what lets a lost or reissued smartcard be replaced without losing access to the archive. It is not a claim that
nobody could ever read your documents, and a system able to re-key on card reissue could not honestly make that
claim. The Module makes a narrower and simpler claim: what leaves the archive is readable only by the person it
was addressed to — and, where it is used without the Service, **what is stored is not encrypted**.

## Architecture at a glance

- **Clean Architecture** — `Domain` → `Application` → `Infrastructure`/`Auth` → `Api` → `Client`/`Worker`, with the dependency direction enforced as tests.
- **Multi-tenant by construction** — global tenant query filters, per-tenant object-storage buckets, and per-tenant tamper-evident (hash-chained, WORM-sealed) audit trails.
- **Hypermedia REST API** — RFC 7807 problem details, ETag/`If-Match` optimistic concurrency, media-type API versioning, and independent JSON/XML content negotiation.
- **Documents** — a unified tree where a repository, a folder, and a leaf are all one `Document` type; immutable versioned metadata "masks"; EAV index fields; document-scoped ACLs with inheritance + override.
- **Search & preview** — OpenSearch full-text over content (Apache Tika; scans become searchable PDFs via an **OCRmyPDF/Tesseract** sidecar), faceted navigation, search hit-overlays, and on-demand previews/renditions (images, Office → PDF via Gotenberg, email, Markdown, …).
- **Enterprise features** — approval workflow, notifications (in-app + email + push + real-time), legal hold & retention with WORM immutability, check-out/check-in, WebDAV/CalDAV/CardDAV/IMAP gateways with a password per device, MFA (TOTP + passkeys), and OpenBao-backed secrets.
- **Encryption to people, not just disks** — CMS envelopes addressed to every certificate a reader holds, hardware keys with attestation, sealed content the server stores but never opens, and a per-tenant posture from plaintext to strict.
- **Modules** — industry modules load per tenant behind a licence gate and a versioned ABI: their own controllers, read models, settings and manuals, acting through a consent-gated principal of their own.
- **Clients** — a Blazor WebAssembly web workbench and a native Avalonia desktop client in this repository, and phone apps (.NET MAUI) beside it, all driving the same hypermedia API through one shared client library.

## Tech stack

.NET 10 · ASP.NET Core · Blazor WebAssembly · Avalonia · .NET MAUI (phone apps) · EF Core (PostgreSQL) · OpenIddict · OpenSearch + Apache Tika · OCRmyPDF/Tesseract · Gotenberg · S3-compatible object storage · OpenBao · Serilog · Docker/Kubernetes.

## Support the project

The core — everything in this repository — is free and open source under Apache 2.0: the archive, the web and
desktop clients, the open protocols and the Password Store, complete, with no telemetry; so is the Messenger module. Beside it there are **paid
extras**: the encryption products, the industry modules, and the phone apps' Standard and Pro subscriptions. They
build on the core; nothing in the core is held back for them.

The public demo, the container images and the desktop builds all cost something to run and publish. If the project
is useful to you, or you enjoyed poking around the architecture, you can support it here:

**[☕ Buy me a coffee](https://buymeacoffee.com/hebel.it)** · **[💸 Donate via PayPal](https://www.paypal.com/donate/?business=paypal@alcifra.dev)**

(PayPal donations go to `paypal@alcifra.dev`, if you would rather send one directly from your own account.)

Entirely optional, and it buys no influence over the roadmap. Starring the repository or opening a thoughtful
issue is just as welcome.

## License

Licensed under the [Apache License 2.0](LICENSE).
