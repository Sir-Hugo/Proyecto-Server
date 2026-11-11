using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Game.Infrastructure;

public sealed record SchemaVersion(int Version, string Name, string Checksum, DateTimeOffset AppliedAt);

public sealed class SchemaMigrator(NpgsqlDataSource dataSource, ILogger<SchemaMigrator> logger)
{
    private const long MigrationLock = 706_202_602_001;
    private sealed record Migration(int Version, string Name, string Sql, string Checksum);

    private static IReadOnlyList<Migration> LoadMigrations()
    {
        var assembly = typeof(SchemaMigrator).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(resource =>
            {
                string name = resource.Split(".Migrations.", StringSplitOptions.None)[1];
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!, Encoding.UTF8);
                // Stable checksums across Windows CRLF and Linux LF checkouts.
                string sql = reader.ReadToEnd().ReplaceLineEndings("\n");
                return new Migration(int.Parse(name.Split('_')[0], CultureInfo.InvariantCulture), name, sql,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
            }).OrderBy(migration => migration.Version).ToArray();
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
            {
                gate.Parameters.AddWithValue(MigrationLock);
                await gate.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var history = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS public.schema_migrations (
                    version integer PRIMARY KEY CHECK (version > 0),
                    name text NOT NULL UNIQUE,
                    checksum text NOT NULL CHECK (length(checksum) = 64),
                    applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
                )
                """, connection, transaction))
                await history.ExecuteNonQueryAsync(cancellationToken);

            var installed = await ReadVersionsAsync(connection, transaction, cancellationToken);
            var migrations = LoadMigrations();
            List<int> applied = [];
            foreach (var version in installed)
            {
                var expected = migrations.SingleOrDefault(m => m.Version == version.Version);
                if (expected is null || expected.Name != version.Name || expected.Checksum != version.Checksum)
                    throw new PersistenceException("migration history mismatch");
            }
            foreach (var migration in migrations.Where(m => installed.All(v => v.Version != m.Version)))
            {
                // Only reviewed embedded SQL is executed; no external SQL or user input is concatenated.
                await using var command = new NpgsqlCommand(migration.Sql, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await using var record = new NpgsqlCommand(
                    "INSERT INTO public.schema_migrations(version, name, checksum) VALUES ($1, $2, $3)", connection, transaction);
                record.Parameters.AddWithValue(migration.Version);
                record.Parameters.AddWithValue(migration.Name);
                record.Parameters.AddWithValue(migration.Checksum);
                await record.ExecuteNonQueryAsync(cancellationToken);
                applied.Add(migration.Version);
            }
            await transaction.CommitAsync(cancellationToken);
            foreach (int version in applied)
                logger.LogInformation("Committed schema migration {Version}", version);
        }
        catch (NpgsqlException exception)
        {
            logger.LogError("Schema migration failed; SQLSTATE {SqlState}", exception.SqlState ?? "connection");
            throw new PersistenceException("migrate schema", exception.SqlState);
        }
        catch (PersistenceException)
        {
            logger.LogError("Schema migration refused: incompatible migration history");
            throw;
        }
    }

    public async Task<IReadOnlyList<SchemaVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var exists = new NpgsqlCommand("SELECT to_regclass('public.schema_migrations') IS NOT NULL", connection);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!) return [];
            return await ReadVersionsAsync(connection, null, cancellationToken);
        }
        catch (NpgsqlException exception)
        {
            logger.LogError("Schema status failed; SQLSTATE {SqlState}", exception.SqlState ?? "connection");
            throw new PersistenceException("read schema version", exception.SqlState);
        }
    }

    private static async Task<IReadOnlyList<SchemaVersion>> ReadVersionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version, name, checksum, applied_at FROM public.schema_migrations ORDER BY version", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        List<SchemaVersion> result = [];
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }
}
