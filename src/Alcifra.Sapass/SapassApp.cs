using Alcifra.ApiClient.PasswordStore;
using Microsoft.Extensions.Logging;
using Alcifra.ApiClient;
using Alcifra.BrowserHost;
using Alcifra.ApiClient.Login;
using Alcifra.ConsoleLogging;
using Alcifra.HardwareKeys;
using Alcifra.Sapass.Commands;
using Alcifra.Sapass.Infrastructure;
using Spectre.Console.Cli;

namespace Alcifra.Sapass;

/// <summary>
/// Builds and runs <c>apass</c> — a pass-like password store in a terminal, sealed on the machine and opened only
/// with the hardware key (ADR 0964).
/// </summary>
/// <remarks>
/// Its own type rather than top-level statements so the wiring can be EXERCISED: what needs a test is not which
/// verbs exist but where each kind of output goes. <c>show</c> prints the password to stdout for a pipeline, and
/// <c>login</c> prints shell exports for <c>eval</c> — so anything else on stdout is a defect, and errors belong
/// on stderr through the logger (ADR 0906).
/// </remarks>
public static class SapassApp
{
    /// <summary>The hidden verb the shell completion scripts call.</summary>
    public const string CompleteVerb = "__complete";

    /// <summary>The hidden verb the browser's native-messaging manifest launches: the autofill host.</summary>
    public const string BrowserHostVerb = "__browser-host";

    public static int Run(string[] args)
    {
        var verbose = args.Contains(PlainConsoleLogging.VerboseFlag, StringComparer.Ordinal);
        using var logging = PlainConsoleLogging.CreateFactory(verbose);
        SapassLog.Factory = logging;
        ApiClientSettings.Logger = logging.CreateLogger("apass");

        // The PIN prompt is a property the host assigns (ADR 0730): the card code cannot own a terminal dialog,
        // and a tool that is not at a terminal declines rather than writing a prompt into a pipe.
        CardSession.PinPrompt = PinPrompt.AskAsync;

        if (args.Length > 0 && args[0] == CompleteVerb)
        {
            return Completion.RunAsync([.. args[1..]], CancellationToken.None).GetAwaiter().GetResult();
        }

        if (args.Length > 0 && args[0] == BrowserHostVerb)
        {
            // The browser speaks on stdin/stdout for as long as its port is open. Nothing but the protocol may be
            // written to stdout, which is why the logger (stderr) is the only other output here.
            var host = new AutofillHost(new SapassAutofillStore(), logging.CreateLogger("sapass.browser"), "sapass");
            host.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None).GetAwaiter().GetResult();
            return 0;
        }

        var app = new CommandApp();
        app.Configure(config =>
        {
            config.SetApplicationName("apass");
            config.SetExceptionHandler((exception, _) => Fail(logging, exception));

            config.AddCommand<LoginCommand>("login")
                .WithDescription("Sign in to an installation; prints shell exports for eval.");
            config.AddCommand<LsCommand>("ls")
                .WithDescription("List a folder's folders and entries.");
            config.AddCommand<FindCommand>("find")
                .WithDescription("Print every path containing the text.");
            config.AddCommand<ShowCommand>("show")
                .WithDescription("Print an entry's password and fields, opened with your card.");
            config.AddCommand<OtpCommand>("otp")
                .WithDescription("Print an entry's current verification code, opened with your card.");
            config.AddCommand<InsertCommand>("insert")
                .WithDescription("File a new secret, read from stdin, sealed to the store's readers.");
            config.AddCommand<GenerateCommand>("generate")
                .WithDescription("Generate a password, file it, and print it.");
            config.AddCommand<EditCommand>("edit")
                .WithDescription("Open an entry in your editor and file what you save as a new version.");
            config.AddCommand<RmCommand>("rm")
                .WithDescription("Delete an entry (the archive's own delete rules apply).");
            config.AddCommand<ResealCommand>("reseal")
                .WithDescription("Seal a store's entries again to its readers' current certificates.");
            config.AddCommand<AudienceCommand>("audience")
                .WithDescription("Show or change who a store is encrypted for, and re-seal it.");
            config.AddCommand<CompletionCommand>("completion")
                .WithDescription("Print the shell completion script (bash, zsh, fish).");
            config.AddBranch("browser", browser =>
            {
                browser.SetDescription("Browser autofill: register this tool as the browser's host.");
                browser.AddCommand<BrowserInstallCommand>("install")
                    .WithDescription("Register apass as the autofill host for Chrome or Edge.");
            });
        });

        return app.Run([.. args.Where(a => a != PlainConsoleLogging.VerboseFlag)]);
    }

    /// <summary>Four kinds of failure, told apart — calling them all "an error" is how a tool starts lying.</summary>
    private static int Fail(ILoggerFactory logging, Exception exception)
    {
        var log = logging.CreateLogger("apass");
        switch (exception)
        {
            case SapassException e:
                log.LogError("{Message}", e.Message);
                return 1;
            case DeviceFlowException e:
                log.LogError("{Message}", e.Message);
                return 1;
            case Exception e when e is PasswordEntryCreationWithheldException or PasswordStoreHasNoRecipientsException:
                // The shared client's refusals carry their sentence (ADR 0982): a state of the store, not a bug.
                log.LogError("{Message}", e.Message);
                return 1;
            case CommandRuntimeException e:
                // Spectre's parse/validation failure: the command line was wrong, not the installation.
                log.LogError("{Message}", e.Message);
                return 2;
            case HttpRequestException e:
                log.LogError("The installation could not be reached: {Message}", e.Message);
                return 3;
            default:
                log.LogError(exception, "Unexpected {Type}", exception.GetType().Name);
                return 1;
        }
    }
}
