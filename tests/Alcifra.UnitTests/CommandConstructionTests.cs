using Alcifra.Cli;
using Alcifra.ConsoleLogging;
using Alcifra.Sapass;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Alcifra.UnitTests;

// #2042: apass built its command app without a type registrar, so every command whose constructor takes a logger
// failed with "Could not resolve type" — eight of thirteen, from the tool's first commit on — and no test noticed,
// because the tests only ran the commands that happened to construct. This constructs EVERY command of both
// command-line programs, found by reflection, so a new one is covered the moment it exists.
public class CommandConstructionTests
{
    public static TheoryData<string> Commands()
    {
        var data = new TheoryData<string>();
        foreach (var command in CommandTypes())
        {
            data.Add(command.FullName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Every_command_of_both_tools_can_be_constructed(string commandName)
    {
        var command = CommandTypes().Single(t => t.FullName == commandName);
        using var logging = LoggerFactory.Create(_ => { });
        var registrar = TypeRegistrar.WithLogging(logging);
        registrar.Register(command, command);

        var resolver = registrar.Build();
        try
        {
            Assert.NotNull(resolver.Resolve(command));
        }
        finally
        {
            (resolver as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Both_tools_have_commands_to_check()
    {
        // A guard over an empty set passes for the wrong reason.
        Assert.Contains(CommandTypes(), t => t.Assembly == typeof(SapassApp).Assembly);
        Assert.Contains(CommandTypes(), t => t.Assembly == typeof(SaConsoleApp).Assembly);
    }

    private static IEnumerable<Type> CommandTypes() =>
        new[] { typeof(SapassApp).Assembly, typeof(SaConsoleApp).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(t => t is { IsAbstract: false, IsGenericTypeDefinition: false } && typeof(ICommand).IsAssignableFrom(t));
}
