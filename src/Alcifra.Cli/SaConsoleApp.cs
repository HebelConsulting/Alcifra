using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;
using Alcifra.ConsoleLogging;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

// alcifra — administrative CLI for a Alcifra installation (ADR 0822). An API client and nothing else:
// it does not write configuration, seed secrets or start containers, and everything it does is an act some
// principal could also perform through a client.
namespace Alcifra.Cli;

/// <summary>
/// Builds the configured <c>alcifra</c> application.
/// </summary>
/// <remarks>
/// Its own type rather than top-level statements so the wiring can be EXERCISED. The property that needed a
/// test is not which verbs exist but where errors are written: `login` prints shell exports to stdout for
/// `eval "$(alcifra login …)"`, so anything else on stdout is something the shell will execute. An
/// unreachable installation once put `Unexpected SocketException: Connection refused` there.
/// </remarks>
public static class SaConsoleApp
{
    /// <summary>
    /// Runs alcifra: the logger lives exactly as long as the command, and is disposed before the process exits so its
    /// background writer flushes (ADR 0906, #1661). <c>--verbose</c> is taken out of the arguments before parsing: it is
    /// a property of the output, not of any command.
    /// </summary>
    public static int Run(string[] args)
    {
        var verbose = args.Contains(PlainConsoleLogging.VerboseFlag, StringComparer.Ordinal);
        using var logging = PlainConsoleLogging.CreateFactory(verbose);
        return Build(logging).Run([.. args.Where(a => a != PlainConsoleLogging.VerboseFlag)]);
    }

    public static CommandApp Build(ILoggerFactory logging)
    {
        var app = new CommandApp(TypeRegistrar.WithLogging(logging));
        app.Configure(config =>
        {
            config.SetApplicationName("alcifra");

            // EVERY error goes to STDERR, and that is load-bearing rather than tidy. `alcifra login` prints shell
            // exports to stdout so it can be used as `eval "$(alcifra login --url …)"` — so an error written to
            // stdout is an error the shell EXECUTES. Measured before this was fixed: an unreachable installation put
            // `Unexpected SocketException: Connection refused` on stdout, where eval would have run it.
            var errors = logging.CreateLogger("alcifra");

            // Four kinds of failure, told apart because calling them all the same thing is how a tool starts lying
            // about what went wrong.
            //
            //   CliException            — something the installation said; the message is written for the reader.
            //   CommandRuntimeException — Spectre's own parse/validation failure, i.e. the command line was wrong.
            //                             Found by running the PACKAGED tool: a forgotten --url printed
            //                             "Unexpected CommandRuntimeException", which tells someone they hit a bug
            //                             when they merely mistyped.
            //   HttpRequestException    — the installation could not be REACHED. A wrong URL, a stopped server or a
            //                             firewall is the most ordinary thing that can happen to a tool whose whole
            //                             job is to call one, and reporting it as an unexpected SocketException sends
            //                             the reader looking for a bug in the binary.
            //   anything else           — a real bug, and it keeps its type rather than hiding behind a friendly
            //                             sentence.
            config.SetExceptionHandler((exception, _) =>
            {
                switch (exception.GetBaseException())
                {
                    case CliException expected:
                        errors.LogError("{Message}", expected.Message);
                        return 1;

                    case CommandRuntimeException usage:
                        errors.LogError("{Message} Run with --help to see the options.", usage.Message);
                        return 1;

                    // The base exception of a refused connection is SocketException, so catching HttpRequestException
                    // alone would miss it — which is exactly how it reached the "real bug" branch.
                    case HttpRequestException or System.Net.Sockets.SocketException:
                        errors.LogError("Could not reach the installation. Check the URL, that it is running, and that this host can reach it.");
                        return 1;

                    case var bug:
                        errors.LogError("Unexpected {ExceptionType}: {Message}", bug.GetType().Name, bug.Message);
                        return 2;
                }
            });

            // Sign-in and "where am I?" sit at the top level rather than in a branch: they are what somebody reaches
            // for first, and burying them under a noun would make the tool's entry point the least discoverable part
            // of it.
            config.AddCommand<LoginCommand>("login")
                .WithDescription("Sign in through the device grant and print the session as shell exports.");
            config.AddCommand<WhoAmICommand>("whoami")
                .WithDescription("Name the installation and identity the current session acts as.");

            // The caller's OWN certificate (#1353, ADR 0833) — the self-service resource, under `me` rather
            // than a bare `certificate`, because the noun has to say WHOSE. An administrator registering on
            // somebody else's behalf is a different resource and a different right, and naming this one `me`
            // leaves room for that instead of having to rename this when it arrives.
            config.AddBranch("me", me =>
            {
                me.SetDescription("The signed-in user's own settings.");
                me.AddBranch("certificate", certificate =>
                {
                    certificate.SetDescription("The certificate this user's content is enveloped to.");
                    certificate.AddCommand<CertificateShowCommand>("show")
                        .WithDescription("Report the registered certificate, or that there is none.");
                    certificate.AddCommand<CertificateRegisterCommand>("register")
                        .WithDescription("Register a certificate from a file (the public certificate only).");
                    certificate.AddCommand<CertificateDeleteCommand>("delete")
                        .WithDescription("Remove the registered certificate.");
                });
                me.AddBranch("device", device =>
                {
                    device.SetDescription("The devices that reach your mail, files, calendars and contacts, each with its own password.");
                    device.AddCommand<DeviceListCommand>("list").WithDescription("List your devices, their protocols and when each was last used.");
                    device.AddCommand<DeviceEnrolCommand>("enrol")
                        .WithDescription("Enrol a device; its password is printed once, on stdout.");
                    device.AddCommand<DeviceRevokeCommand>("revoke").WithDescription("Revoke a device; its password stops working at once.");
                });
            });

            // CERTIFICATES, the ADMINISTRATOR's surface (owner-decided): enrolling for OTHER people, in bulk,
            // from a certification authority's manifest. Deliberately not under `me` — that branch is the
            // signed-in user's own settings and writes the self-service MAIL certificate, which is a
            // different store from the reader certificates documents are addressed to (#1501).
            config.AddBranch("certificates", certificates =>
            {
                certificates.SetDescription("Reader certificates, for a tenant administrator.");
                certificates.AddCommand<CertificateListCommand>("list")
                    .WithDescription("Report what is enrolled, including what is no longer usable and why.");
                certificates.AddCommand<CertificateEnrolCommand>("enrol")
                    .WithDescription("Enrol one certificate, for a named holder or the caller.");
                certificates.AddCommand<CertificateRevokeCommand>("revoke")
                    .WithDescription("Revoke one, by serial or thumbprint.");
                certificates.AddBranch("settings", certificateSettings =>
                {
                    certificateSettings.SetDescription("Whether users enrol their own, and where the \"Reader certificates\" folder is created (ADR 0936).");
                    certificateSettings.AddCommand<CertificateSettingsShowCommand>("show")
                        .WithDescription("Show the enrolment settings.");
                    certificateSettings.AddCommand<CertificateSettingsSetCommand>("set")
                        .WithDescription("Change them; what is not named keeps its value.");
                });
                certificates.AddBranch("policy", policy =>
                {
                    policy.SetDescription("What a certificate must meet before it is enrolled; the holder's e-mail address by default (ADR 0971).");
                    policy.AddCommand<CertificatePolicyShowCommand>("show")
                        .WithDescription("Show the policy, defaults included.");
                    policy.AddCommand<CertificatePolicySetCommand>("set")
                        .WithDescription("Change it; what is not named keeps its value. Applies to the next enrolment, never to what is enrolled.");
                });
                certificates.AddCommand<CertificateImportCommand>("import")
                    .WithDescription("Enrol and revoke in bulk from a CA manifest. Safe to re-run.");
                certificates.AddBranch("attestation-roots", roots =>
                {
                    roots.SetDescription("Which of the installation's attestation roots this tenant trusts; a tenant may only switch some off (ADR 0963).");
                    roots.AddCommand<CertificateAttestationRootListCommand>("list")
                        .WithDescription("List the installation's roots and whether each is trusted here.");
                    roots.AddCommand<CertificateAttestationRootDisableCommand>("disable")
                        .WithDescription("Switch a root off for this tenant.");
                    roots.AddCommand<CertificateAttestationRootEnableCommand>("enable")
                        .WithDescription("Switch a root back on for this tenant.");
                });
            });

            // The installation's trusted attestation roots (ADR 0963): a PLATFORM ADMINISTRATOR's surface, no client UI.
            config.AddBranch("attestation-roots", roots =>
            {
                roots.SetDescription("The attestation roots this installation trusts, for a platform administrator (ADR 0963).");
                roots.AddCommand<AttestationRootListCommand>("list").WithDescription("List the roots, shipped and uploaded.");
                roots.AddCommand<AttestationRootAddCommand>("add").WithDescription("Upload a root's public certificate (switched off unless --enable).");
                roots.AddCommand<AttestationRootEnableCommand>("enable").WithDescription("Trust a root.");
                roots.AddCommand<AttestationRootDisableCommand>("disable").WithDescription("Stop trusting a root; keys attested to it stop addressing anyone.");
                roots.AddCommand<AttestationRootRenameCommand>("rename").WithDescription("Rename a root.");
            });

            // The intray's recovery path (#799). The bytes an overwrite set aside have been preserved since
            // #794 and were reachable only by going into object storage by hand; this is the way back, and it
            // is here rather than in the two user interfaces because alcifra is a client too — an endpoint
            // it reaches by following a rel is complete, not a gap waiting for a button.
            config.AddBranch("intray", intray =>
            {
                intray.SetDescription("The signed-in user's intray.");
                intray.AddBranch("previous", previous =>
                {
                    previous.SetDescription("Bytes an intray overwrite set aside, and the way back to them.");
                    previous.AddCommand<IntrayPreviousListCommand>("list")
                        .WithDescription("What is recoverable, and how long is left to recover it.");
                    previous.AddCommand<IntrayPreviousRestoreCommand>("restore")
                        .WithDescription("Put one back into the intray. Reversible: what is there now is kept.");
                });
            });

            // Modules, and for now the one act a script needs that no client covers: filing a signed licence
            // and pointing the modules it names at it. Both clients already let a HUMAN do this; nothing let
            // a script, which is why the flight-school demo seeder grew its own inline copy (#1474).
            config.AddBranch("module", module =>
            {
                module.SetDescription("Modules: activation, settings and actions (tenant administrator), and uploads with a module credential.");
                module.AddCommand<ModuleListCommand>("list")
                    .WithDescription("Name the installed modules, and whether each is active for this tenant.");
                module.AddBranch("settings", moduleSettings =>
                {
                    moduleSettings.SetDescription("What a module declared it needs configuring (ADR 0772).");
                    moduleSettings.AddCommand<ModuleSettingsShowCommand>("show")
                        .WithDescription("Report the declared settings and what is configured. A secret's value is never shown.");
                    moduleSettings.AddCommand<ModuleSettingsSetCommand>("set")
                        .WithDescription("Write one setting. The value comes from --from-env, --from-file or --stdin, never an option.");
                });
                module.AddCommand<ModuleRebuildCommand>("rebuild")
                    .WithDescription("Re-derive a module's read model from documents. One projection, or all.");
                module.AddCommand<ModuleActivateCommand>("activate")
                    .WithDescription("File a vendor-signed licence and activate every module it names; or, with --module alone, activate a module that needs no licence.");
                module.AddCommand<ModuleDeactivateCommand>("deactivate")
                    .WithDescription("Turn off a module that needs no licence (ADR 0944).");
                module.AddCommand<ModuleActionCommand>("action")
                    .WithDescription("Invoke a module's action on a document. A value it reveals once is written to a file.");
                module.AddCommand<ModuleUploadCommand>("upload")
                    .WithDescription("Upload a file to a module's upload rel with a module credential (e.g. an app to the app repository).");
            });

            config.AddBranch("messenger", messenger =>
            {
                messenger.SetDescription("The Messenger: back up your conversations, restore them here (core #1813).");
                messenger.AddCommand<MessengerBackupCommand>("backup")
                    .WithDescription("Write your conversations to one sealed file; nothing is decrypted.");
                messenger.AddCommand<MessengerRestoreCommand>("restore")
                    .WithDescription("Restore your backup here as read-only history; restoring twice changes nothing.");
            });
            config.AddBranch("user", user =>
            {
                user.SetDescription("Users of the signed-in tenant (needs the right to manage users).");
                user.AddCommand<UserCreateCommand>("create")
                    .WithDescription("Create a user, or find the one with that e-mail; prints the id. The password comes from stdin only.");
                user.AddBranch("alias", alias =>
                {
                    alias.SetDescription("A user's identity aliases: further addresses they are known by. An alias does not sign in; Strict certificate binding accepts a certificate naming one.");
                    alias.AddCommand<UserAliasListCommand>("list").WithDescription("List a user's identity aliases.");
                    alias.AddCommand<UserAliasAddCommand>("add").WithDescription("Add an identity alias, on a verified identity domain; adding one twice changes nothing.");
                    alias.AddCommand<UserAliasRemoveCommand>("remove").WithDescription("Remove an identity alias.");
                });
            });
            config.AddBranch("mail-domain", domain =>
            {
                domain.SetDescription("The tenant's registered domains: identity (account addresses live there), receiving (mail is filed), or both.");
                domain.AddCommand<MailDomainListCommand>("list").WithDescription("List the domains with their roles.");
                domain.AddCommand<MailDomainAddCommand>("add").WithDescription("Claim a domain; prints the DNS record that proves it.");
                domain.AddCommand<MailDomainRolesCommand>("roles").WithDescription("Set a domain's roles.");
            });
            config.AddBranch("repository", repository =>
            {
                repository.SetDescription("Repositories: the root documents everything is filed under (ADR 0200).");
                repository.AddCommand<RepositoryCreateCommand>("create")
                    .WithDescription("Create a repository, or find the one of that name. Prints its id on stdout.");
            });

            // ACL: `set` states the complete set of rights (a replace, because the endpoint is a PUT), while
            // `grant` and `revoke` adjust what is there. Three verbs for one concept, owner-decided
            // 2026-10-01, because one verb makes one of the two intentions awkward or dishonest.
            config.AddBranch("acl", acl =>
            {
                acl.SetDescription("Who may do what to a document.");
                acl.AddCommand<AclSetCommand>("set")
                    .WithDescription("State the COMPLETE set of rights a principal holds. Unnamed rights are withheld.");
                acl.AddCommand<AclGrantCommand>("grant")
                    .WithDescription("Add rights to what a principal already holds, leaving the rest alone.");
                acl.AddCommand<AclRevokeCommand>("revoke")
                    .WithDescription("Take rights away, leaving the rest alone.");
                acl.AddCommand<AclBreakInheritanceCommand>("break-inheritance")
                    .WithDescription("Make the document's own grants the ones that apply: the inherited ones are copied onto it first.");
            });

            // The password store (ADRs 0964/0966): a shared store and its entries, sealed on THIS machine to the store's
            // audience (ADR 0983). Who may read it is an ordinary grant (acl); who it is encrypted for is its audience.
            config.AddBranch("passwords", passwords =>
            {
                passwords.SetDescription("Password stores: shared stores and their entries, sealed to the readers' certificates.");
                passwords.AddBranch("store", store =>
                {
                    store.SetDescription("A shared password store.");
                    store.AddCommand<PasswordStoreCreateCommand>("create")
                        .WithDescription("Create a store in a folder, or find the one of that name there. Prints its id on stdout.");
                    store.AddCommand<PasswordStoreShowCommand>("show")
                        .WithDescription("Report a store's sealing state and contents by name; entry names go to stdout. Nothing is opened.");
                    store.AddBranch("audience", audience =>
                    {
                        audience.SetDescription("Who a store is encrypted for (ADR 0983); once people manage it, they change it in apass or the clients.");
                        audience.AddCommand<PasswordStoreAudienceShowCommand>("show")
                            .WithDescription("List the store's audience, one member per line on stdout, with their role.");
                        audience.AddCommand<PasswordStoreAudienceSetCommand>("set")
                            .WithDescription("Replace the audience of a store nobody manages yet (a seed's), by name; run before filing entries.");
                        audience.AddCommand<PasswordStoreAudienceAppointCommand>("appoint")
                            .WithDescription("Appoint a manager to a store left without one: a tenant administrator's act, signed in as a person.");
                    });
                });
                passwords.AddBranch("entry", entry =>
                {
                    entry.SetDescription("An entry of a password store.");
                    entry.AddCommand<PasswordEntryCreateCommand>("create")
                        .WithDescription("Create an entry sealed to the store's readers, or leave the one of that name alone. The password comes from stdin only.");
                });
            });

            config.AddBranch("storage", storage =>
            {
                storage.SetDescription("The installation's object storage (platform administrator).");
                storage.AddCommand<StorageMigrateCommand>("migrate")
                    .WithDescription("Move every tenant's objects from the legacy bucket prefix to the current one (ADR 1000); safe to stop and re-run.");
            });
            config.AddBranch("tenant", tenant =>
            {
                tenant.SetDescription("Tenant administration: provisioning (platform administrator), the tenant's standard repository, its recovery certificate and its offline-cache policy (tenant administrator).");
                tenant.AddCommand<TenantCreateCommand>("create")
                    .WithDescription("Provision a tenant with its first administrator and repository.");
                tenant.AddBranch("standard-repository", standard =>
                {
                    standard.SetDescription($"Where {Alcifra.Localization.Strings.ProductName} files what it brings, its manuals first (ADR 0892).");
                    standard.AddCommand<StandardRepositoryShowCommand>("show")
                        .WithDescription("Show the tenant's standard repository.");
                    standard.AddCommand<StandardRepositorySetCommand>("set")
                        .WithDescription("Make another repository the standard one; the manuals folder moves with it.");
                });
                tenant.AddBranch("recovery-certificate", recovery =>
                {
                    recovery.SetDescription("The certificate every offline export is also sealed to, so it stays readable when cards expire (ADR 0960). Public half only.");
                    recovery.AddCommand<RecoveryCertificateShowCommand>("show")
                        .WithDescription("Show the recovery certificate, or that none is set.");
                    recovery.AddCommand<RecoveryCertificateSetCommand>("set")
                        .WithDescription("Set it where none is set yet; offline exports are refused until then.");
                    recovery.AddCommand<RecoveryCertificateReplaceCommand>("replace")
                        .WithDescription("Replace it; archives made so far stay sealed to the previous one.");
                });
                tenant.AddBranch("offline-cache", cache =>
                {
                    cache.SetDescription("What the tenant allows a phone's offline cache (#1742); it binds the apps, which honour it.");
                    cache.AddCommand<OfflineCacheShowCommand>("show")
                        .WithDescription("Show the offline-cache policy.");
                    cache.AddCommand<OfflineCacheSetCommand>("set")
                        .WithDescription("Change it (tenant administrator); options left out keep their value.");
                });
            });
        });

        return app;
    }
}
