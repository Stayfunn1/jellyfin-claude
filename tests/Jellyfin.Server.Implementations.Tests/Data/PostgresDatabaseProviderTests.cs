using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Server.Implementations.Tests.Item;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

/// <summary>
/// The parts of the PostgreSQL provider that have no SQLite counterpart. Only runs when the tests are pointed at a
/// PostgreSQL server, see <see cref="PostgresTestDatabase"/>.
/// </summary>
public sealed class PostgresDatabaseProviderTests : SqliteDbTestFixture
{
    private const string SkipReason = "Needs a PostgreSQL server, set JELLYFIN_TEST_POSTGRES to run.";

    public PostgresDatabaseProviderTests()
    {
        DatabaseProvider.DbContextFactory = CreateDbContextFactory();
    }

    [Fact]
    public async Task RefreshStatistics_EmptyLibrary_LeavesTheTablesUnanalyzed()
    {
        Assert.SkipUnless(IsPostgres, SkipReason);
        SeedItems(3, isFolder: true);

        await DatabaseProvider.RefreshStatistics(CancellationToken.None);

        // Statistics taken while the library is empty make the planner treat every table as one row.
        Assert.Equal(0, ReadAnalyzeCount("BaseItems"));
    }

    [Fact]
    public async Task RefreshStatistics_LibraryWithItems_Analyzes()
    {
        Assert.SkipUnless(IsPostgres, SkipReason);
        SeedItems(7, isFolder: false);

        await DatabaseProvider.RefreshStatistics(CancellationToken.None);

        Assert.Equal(1, ReadAnalyzeCount("BaseItems"));
        Assert.Equal(CountItems(), ReadEstimatedRowCount("BaseItems"));
    }

    [Fact]
    public async Task RunScheduledOptimisation_LibraryWithItems_VacuumsAndAnalyzes()
    {
        Assert.SkipUnless(IsPostgres, SkipReason);
        SeedItems(4, isFolder: false);

        await DatabaseProvider.RunScheduledOptimisation(CancellationToken.None);

        Assert.Equal(CountItems(), ReadEstimatedRowCount("BaseItems"));
    }

    [Fact]
    public async Task RestoreBackupFast_AfterChanges_BringsBackTheDatabaseAsItWas()
    {
        Assert.SkipUnless(IsPostgres, SkipReason);
        AddActivity("before the backup");

        var key = await DatabaseProvider.MigrationBackupFast(CancellationToken.None);
        AddActivity("after the backup");
        await DatabaseProvider.RestoreBackupFast(key, CancellationToken.None);

        using var context = CreateDbContext();
        Assert.Equal("before the backup", Assert.Single(context.ActivityLogs).Name);
    }

    [Fact]
    public async Task DeleteBackup_RemovesTheBackupDatabase()
    {
        Assert.SkipUnless(IsPostgres, SkipReason);

        var key = await DatabaseProvider.MigrationBackupFast(CancellationToken.None);
        using (var context = CreateDbContext())
        {
            Assert.True(BackupExists(context, key));
        }

        await DatabaseProvider.DeleteBackup(key);

        using (var context = CreateDbContext())
        {
            Assert.False(BackupExists(context, key));
        }
    }

    [Fact]
    public async Task SynchronizeGeneratedKeys_AfterRowsWereWrittenWithTheirKeys_NewRowsDoNotCollide()
    {
        Assert.SkipUnless(IsPostgres, SkipReason);
        AddActivity("existing");

        using (var context = CreateDbContext())
        {
            // What a restore does: empty the tables, then write the saved rows with their original keys.
            await DatabaseProvider.PurgeDatabase(context, ["ActivityLogs"]);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "ActivityLogs" ("Id", "Name", "Type", "UserId", "DateCreated", "LogSeverity", "RowVersion")
                VALUES (41, 'restored', 'Test', '00000000-0000-0000-0000-000000000000', now(), 2, 0)
                """,
                TestContext.Current.CancellationToken);
            await DatabaseProvider.SynchronizeGeneratedKeys(context, CancellationToken.None);
        }

        AddActivity("added afterwards");

        using var after = CreateDbContext();
        Assert.Equal(42, after.ActivityLogs.Single(e => e.Name == "added afterwards").Id);
    }

    [Fact]
    public void SaveChanges_TextWithNulCharacters_IsStoredWithoutThem()
    {
        Assert.SkipUnless(IsPostgres, SkipReason);

        // Broken tags carry NUL characters, which PostgreSQL text cannot hold.
        AddActivity("Broken\0Tag");

        using var context = CreateDbContext();
        Assert.Equal("BrokenTag", context.ActivityLogs.Single().Name);
    }

    [Fact]
    public void GetBackupDatabaseName_LongDatabaseName_StaysWithinTheIdentifierLimit()
    {
        var name = PostgresDatabaseProvider.GetBackupDatabaseName(new string('j', 80), "20261013000000");

        Assert.Equal(63, name.Length);
        Assert.EndsWith("_backup_20261013000000", name, StringComparison.Ordinal);
    }

    private static bool BackupExists(JellyfinDbContext context, string key)
    {
        var database = context.Database.GetDbConnection().Database;
        var backup = PostgresDatabaseProvider.GetBackupDatabaseName(database, key);
        return context.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_database WHERE datname = {backup}")
            .Single() == 1;
    }

    private void AddActivity(string name)
    {
        using var context = CreateDbContext();
        context.ActivityLogs.Add(new ActivityLog(name, "Test", Guid.Empty));
        context.SaveChanges();
    }

    private void SeedItems(int count, bool isFolder)
    {
        using var context = CreateDbContext();
        context.BaseItems.AddRange(Enumerable.Range(0, count).Select(_ => new BaseItemEntity
        {
            Id = Guid.NewGuid(),
            Type = isFolder ? "MediaBrowser.Controller.Entities.Folder" : "MediaBrowser.Controller.Entities.TV.Episode",
            IsFolder = isFolder
        }));
        context.SaveChanges();
    }

    private long CountItems()
    {
        using var context = CreateDbContext();
        return context.BaseItems.LongCount();
    }

    private long ReadAnalyzeCount(string table)
    {
        using var context = CreateDbContext();
        return context.Database
            .SqlQuery<long>($"SELECT analyze_count AS \"Value\" FROM pg_stat_user_tables WHERE relname = {table}")
            .Single();
    }

    private long ReadEstimatedRowCount(string table)
    {
        using var context = CreateDbContext();
        return context.Database
            .SqlQuery<long>($"SELECT reltuples::bigint AS \"Value\" FROM pg_class WHERE relname = {table}")
            .Single();
    }
}
