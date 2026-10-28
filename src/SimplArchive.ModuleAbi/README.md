# Alcifra.ModuleAbi — the contract an Alcifra industry module builds against

## What Alcifra is

**Alcifra** (its repository's working name: SimplArchive) is an open-source (Apache-2.0), multi-tenant **sovereign
workspace**. It is a document management system (DMS) with encryption to each person's own key and open protocols
(IMAP, WebDAV, CalDAV, CardDAV) for bring-your-own-device. It provides:
- repositories with typed index data, versions, full-text search, permissions, workflow and a hash-chained audit trail;
- a web client, a desktop client and a mobile app;
- a Helm chart for production.

Project and documentation: <https://github.com/HebelConsulting/SimplArchive>

## What this package is

An **industry module** extends Alcifra for one line of business. An installation administrator activates it per
tenant, usually with a vendor-signed licence. This package is the **only** assembly from the core a module may
reference. Everything a module can do is exported here deliberately, one capability at a time, so the rest of the
core stays free to change. It contains:

- **Registration**: `IIndustryModule`, which declares the module's id, its ABI version, its services and its HTTP
  controllers. The core mounts the controllers behind an activation gate.
- **Masks and settings**: the masks the module seeds, with content segments and exemptions (search, renditions,
  retention, legal hold), and the settings the core renders for an administrator.
- **The archive facade**: `IModuleArchiveFacade`, through which the module reads and files documents. It acts as
  the module's own consent-gated principal, and every write is audited as the module's act. Multi-write acts are
  atomic through `InTransactionAsync`.
- **Read models**: the module's own projections, in its own database context.
- **Content**: content goes in by presigned upload and out by redirect, never as a byte array or a proxy.
- **Signals and live events**: a signal tells a user "something new here". A live event is ephemeral (for example
  "is typing") and travels over the core's notifications hub.
- **Licences**: signed tenant and mobile-app licence records, verified offline.

## Versions

`Major.Minor.Patch`. The host checks the **major** version strictly: a module built for another major is refused
with a clear message. The **minor** floats: a module built against an older minor keeps loading on a newer host,
and one built against a newer minor than the host offers is refused. A patch never changes the contract.

```xml
<PackageReference Include="Alcifra.ModuleAbi" Version="1.25.1" />
```

Until the rename to Alcifra is complete, the same contract is also published as `SimplArchive.ModuleAbi`, at the
same version.
