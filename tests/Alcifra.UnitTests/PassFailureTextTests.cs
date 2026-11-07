using Alcifra.ApiClient;
using Alcifra.Pass;
using Microsoft.Extensions.Logging;

namespace Alcifra.UnitTests;

// apass told a refusal only by its type ("Unexpected ApiActionException", owner 2026-11-02): the sentence the shared
// client builds from the server's errorCode was dropped. A refusal is printed as that sentence, like apass's own errors.
public class PassFailureTextTests
{
    [Fact]
    public void A_refusal_the_server_answered_is_printed_as_its_sentence()
    {
        var logging = new CapturingLoggerFactory();

        var exit = PassApp.Fail(logging, new ApiActionException("You don't have permission to do that here."));

        Assert.Equal(1, exit);
        Assert.Equal(["You don't have permission to do that here."], logging.Entries);
    }

    [Fact]
    public void An_unexpected_exception_still_names_its_type()
    {
        var logging = new CapturingLoggerFactory();

        PassApp.Fail(logging, new InvalidOperationException("boom"));

        Assert.Equal(["Unexpected InvalidOperationException"], logging.Entries);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory, ILogger
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }
}
