using System.Security.Cryptography;
using System.Text;
using Game.Domain;
using Game.Infrastructure;

namespace Game.Postgres.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class LiveMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact] public async Task UpgradesInstalledVersionOneWithoutChangingItsHistoryOrData()
    {
        await using var db = await fixture.CreateDatabaseAsync(false); string checksum = await InstallVersionOneAsync(db);
        var match = Sample(); await db.Store.CreateAsync(match);
        await db.Migrator.ApplyAsync(); await db.Migrator.ApplyAsync();
        var versions = await db.Migrator.GetVersionsAsync(); Assert.Equal(new[] { 1, 2, 3, 4 }, versions.Select(v => v.Version));
        Assert.Equal(checksum, versions[0].Checksum);
        Assert.Equal(match.State, (await db.Store.GetAsync(match.State.MatchId))!.State);
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM active_match_players"));
    }
    [PostgresFact] public async Task ConflictingLegacyActiveMatchesRefuseUpgradeWithoutDestroyingData()
    {
        await using var db = await fixture.CreateDatabaseAsync(false); await InstallVersionOneAsync(db);
        await db.Store.CreateAsync(Sample()); await db.Store.CreateAsync(Sample());
        Assert.Equal("23505", (await Assert.ThrowsAsync<PersistenceException>(() => db.Migrator.ApplyAsync())).SqlState);
        Assert.Single(await db.Migrator.GetVersionsAsync());
        Assert.Equal(2L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('active_match_players') IS NULL"));
    }
    [PostgresFact] public async Task UpgradesInstalledVersionTwoAndPreservesLiveMatch()
    {
        await using var db=await fixture.CreateDatabaseAsync(false);await InstallVersionOneAsync(db);var match=Sample();await db.Store.CreateAsync(match);
        var assembly=typeof(SchemaMigrator).Assembly;using var reader=new StreamReader(assembly.GetManifestResourceStream("Game.Infrastructure.Migrations.002_live_matches.sql")!,Encoding.UTF8);
        string sql=(await reader.ReadToEndAsync()).ReplaceLineEndings("\n");string checksum=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        await db.ExecuteAsync(sql);await db.ExecuteAsync("INSERT INTO schema_migrations(version,name,checksum) VALUES(2,'002_live_matches.sql',$1)",checksum);
        await db.Migrator.ApplyAsync();Assert.Equal(new[]{1,2,3,4},(await db.Migrator.GetVersionsAsync()).Select(v=>v.Version));
        Assert.Equal(match.State,(await db.Store.GetAsync(match.State.MatchId))!.State);Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('rooms') IS NOT NULL AND to_regclass('match_outbox') IS NOT NULL"));
        Assert.True(await db.ScalarAsync<bool>("SELECT to_regclass('user_suspensions') IS NOT NULL AND to_regclass('admin_audit_log') IS NOT NULL"));
    }
    private static StoredMatch Sample()
    {
        var now = DateTimeOffset.Parse("2026-11-10T12:00:00Z"); var state = MatchState.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now);
        return new(state, [new(state.FirstPlayer.PlayerId, "auth0|legacy-alice", null, 1), new(state.SecondPlayer.PlayerId, "auth0|legacy-bob", null, 2)], now, null);
    }
    private static async Task<string> InstallVersionOneAsync(TestDatabase db)
    {
        // Deploy the unchanged, published 2A resource to reproduce an already-installed database.
        var assembly = typeof(SchemaMigrator).Assembly;
        using var reader = new StreamReader(assembly.GetManifestResourceStream("Game.Infrastructure.Migrations.001_initial.sql")!, Encoding.UTF8);
        string sql = (await reader.ReadToEndAsync()).ReplaceLineEndings("\n");
        string checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        Assert.Equal("233C9068D710D26959D0A27BBE8E3F311C25A5258A904911A4901506E12E53F2", checksum);
        await db.ExecuteAsync(sql);
        await db.ExecuteAsync("CREATE TABLE schema_migrations(version integer PRIMARY KEY, name text NOT NULL UNIQUE, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP)");
        await db.ExecuteAsync("INSERT INTO schema_migrations(version,name,checksum) VALUES (1,'001_initial.sql',$1)", checksum);
        return checksum;
    }
}
