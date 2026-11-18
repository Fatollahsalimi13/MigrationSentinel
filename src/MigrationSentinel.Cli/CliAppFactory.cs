using MigrationSentinel.Cli.Commands;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MigrationSentinel.Cli;

public static class CliAppFactory
{
    public static CommandApp CreateApp(IAnsiConsole? console = null)
    {
        var resolvedConsole = console ?? AnsiConsole.Console;
        var registrar = new SimpleTypeRegistrar(resolvedConsole);
        var app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.ConfigureConsole(resolvedConsole);
            config.SetApplicationName("migration-sentinel");
            config.SetApplicationVersion("1.0.0");

            config.AddCommand<ScanCommand>("scan")
                .WithDescription("Scan SQL scripts or EF Core C# migrations for breaking changes, locks, and data loss.")
                .WithExample("scan", "./Migrations")
                .WithExample("scan", "./Migrations", "--dialect", "postgresql", "--fail-on", "high")
                .WithExample("scan", "./Migrations", "--format", "sarif", "--output", "sentinel.sarif")
                .WithExample("scan", "./Migrations", "--ai", "openai", "--mitigation-out", "mitigation.sql");

            config.AddCommand<DiffCommand>("diff")
                .WithDescription("Scan only added or modified migration files from a Git diff (ideal for PR gates).")
                .WithExample("diff", "--base", "origin/main")
                .WithExample("diff", "--staged", "--fail-on", "critical");

            config.AddCommand<RulesCommand>("rules")
                .WithDescription("List all built-in AST and heuristic migration safety rules.");

            config.AddCommand<InitCommand>("init")
                .WithDescription("Generate a starter .migrationsentinel.json configuration file.");
        });

        return app;
    }

    private sealed class SimpleTypeRegistrar : ITypeRegistrar
    {
        private readonly Dictionary<Type, List<Func<ITypeResolver, object>>> _registrations = new();

        public SimpleTypeRegistrar(IAnsiConsole console)
        {
            RegisterInstance(typeof(IAnsiConsole), console);
        }

        public void Register(Type service, Type implementation) =>
            AddRegistration(service, resolver => CreateInstance(implementation, resolver));

        public void RegisterInstance(Type service, object implementation) =>
            AddRegistration(service, _ => implementation);

        public void RegisterLazy(Type service, Func<object> factory) =>
            AddRegistration(service, _ => factory());

        public ITypeResolver Build() => new SimpleTypeResolver(_registrations);

        private void AddRegistration(Type service, Func<ITypeResolver, object> factory)
        {
            if (!_registrations.TryGetValue(service, out var list))
            {
                list = [];
                _registrations[service] = list;
            }
            list.Add(factory);
        }

        internal static object CreateInstance(Type targetType, ITypeResolver resolver)
        {
            var ctor = targetType.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor is null)
                return Activator.CreateInstance(targetType)!;

            var parameters = ctor.GetParameters()
                .Select(p => resolver.Resolve(p.ParameterType))
                .ToArray();

            return ctor.Invoke(parameters);
        }
    }

    private sealed class SimpleTypeResolver : ITypeResolver
    {
        private readonly Dictionary<Type, List<Func<ITypeResolver, object>>> _registrations;

        public SimpleTypeResolver(Dictionary<Type, List<Func<ITypeResolver, object>>> registrations)
        {
            _registrations = registrations;
        }

        public object? Resolve(Type? type)
        {
            if (type is null)
                return null;

            if (_registrations.TryGetValue(type, out var factories) && factories.Count > 0)
                return factories[^1](this);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var elemType = type.GetGenericArguments()[0];
                if (_registrations.TryGetValue(elemType, out var elemFactories))
                {
                    var array = Array.CreateInstance(elemType, elemFactories.Count);
                    for (int i = 0; i < elemFactories.Count; i++)
                        array.SetValue(elemFactories[i](this), i);
                    return array;
                }
                return Array.CreateInstance(elemType, 0);
            }

            if (type.IsInterface || type.IsAbstract)
                return null;

            return SimpleTypeRegistrar.CreateInstance(type, this);
        }
    }
}
