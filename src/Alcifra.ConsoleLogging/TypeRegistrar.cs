using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Alcifra.ConsoleLogging;

/// <summary>Spectre's command construction over a service collection, so a command can take an <c>ILogger&lt;T&gt;</c> (#1661).</summary>
/// <remarks>
/// ONE implementation for both command-line programs (#2042). <c>apass</c> built its app without any registrar, and
/// Spectre's own resolver cannot construct a command that asks for a logger: eight of its commands failed with "Could
/// not resolve type" from the first commit on, while <c>alcifra</c>, which had this class, worked.
/// </remarks>
public sealed class TypeRegistrar(IServiceCollection services) : ITypeRegistrar
{
    /// <summary>A registrar that hands every command an <c>ILogger&lt;T&gt;</c> from <paramref name="logging"/>.</summary>
    public static TypeRegistrar WithLogging(ILoggerFactory logging)
    {
        var services = new ServiceCollection();
        services.AddSingleton(logging);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        return new TypeRegistrar(services);
    }

    public ITypeResolver Build() => new TypeResolver(services.BuildServiceProvider());

    public void Register(Type service, Type implementation) => services.AddSingleton(service, implementation);

    public void RegisterInstance(Type service, object implementation) => services.AddSingleton(service, implementation);

    public void RegisterLazy(Type service, Func<object> factory) => services.AddSingleton(service, _ => factory());

    private sealed class TypeResolver(ServiceProvider provider) : ITypeResolver, IDisposable
    {
        public object? Resolve(Type? type) => type is null ? null : provider.GetService(type);

        public void Dispose() => provider.Dispose();
    }
}
