using MigrationSentinel.Cli;

var app = CliAppFactory.CreateApp();
return await app.RunAsync(args);
