using Game.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Game.Postgres.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresFixture.Enabled) Skip = "Set GAME_POSTGRES_TESTS=1 to run real PostgreSQL integration tests (Docker required).";
    }
}
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PostgresFixture.Enabled) Skip = "Set GAME_POSTGRES_TESTS=1 to run real PostgreSQL integration tests (Docker required).";
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    public static bool Enabled => Environment.GetEnvironmentVariable("GAME_POSTGRES_TESTS") == "1";
    private PostgreSqlContainer? container;
    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        container = new PostgreSqlBuilder("postgres:17.11-bookworm")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();
        // Failure to start Docker is a test failure when explicitly enabled, never a false pass.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await container.StartAsync(timeout.Token);
    }
    public async Task DisposeAsync()
    {
        if (container is not null) await container.DisposeAsync();
    }
    public async Task<TestDatabase> CreateDatabaseAsync(bool migrate = true)
    {
        if (container is null) throw new InvalidOperationException("Integration fixture was not started.");
        string database = "phase2a_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        // Identifier is generated exclusively from a GUID, never from external input.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await create.ExecuteNonQueryAsync();
        var settings = new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = database };
        var result = new TestDatabase(settings.ConnectionString);
        if (migrate) await result.Migrator.ApplyAsync();
        return result;
    }
}

public sealed class TestDatabase(string connectionString) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;
    public NpgsqlDataSource Source { get; } = PostgresServices.CreateDataSource(connectionString);
    public SchemaMigrator Migrator => new(Source, NullLogger<SchemaMigrator>.Instance);
    public PostgresMatchStore Store => new(Source, NullLogger<PostgresMatchStore>.Instance);
    public async Task<int> ExecuteAsync(string sql, params object[] values)
    {
        await using var command = Source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return await command.ExecuteNonQueryAsync();
    }
    public async Task<T> ScalarAsync<T>(string sql, params object[] values)
    {
        await using var command = Source.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
    public ValueTask DisposeAsync() => Source.DisposeAsync();
}
