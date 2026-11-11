using Game.Infrastructure;
using Microsoft.Extensions.Logging;

if (args.Length != 1 || args[0] is not ("migrate" or "status"))
{
    Console.Error.WriteLine("Usage: Game.Database migrate|status. Set ConnectionStrings__GameDatabase externally.");
    return 2;
}
string? connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__GameDatabase");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings__GameDatabase is required.");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var logging = LoggerFactory.Create(builder => builder.AddSimpleConsole());
try
{
    await using var source = PostgresServices.CreateDataSource(connectionString);
    var migrator = new SchemaMigrator(source, logging.CreateLogger<SchemaMigrator>());
    if (args[0] == "migrate") await migrator.ApplyAsync(cancellation.Token);
    var versions = await migrator.GetVersionsAsync(cancellation.Token);
    Console.WriteLine(versions.Count == 0 ? "Schema version: 0 (empty)" : $"Schema version: {versions.Max(v => v.Version)}");
    foreach (var version in versions)
        Console.WriteLine($"{version.Version}: {version.Name} SHA256={version.Checksum} applied={version.AppliedAt:O}");
    return 0;
}
catch (PersistenceException exception)
{
    Console.Error.WriteLine($"{exception.Message} SQLSTATE={exception.SqlState ?? "unavailable"}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Database operation cancelled.");
    return 1;
}
