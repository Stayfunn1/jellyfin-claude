using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.FullSystemBackup;
using Jellyfin.Server.Implementations.Tests.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.FullSystemBackup;

/// <summary>
/// Backs a SQLite database up and restores the backup into a database that does not exist yet, on SQLite and, when the
/// tests are pointed at a PostgreSQL server, on PostgreSQL: the way a server moves to another database.
/// </summary>
public sealed class BackupRestoreTests : IDisposable
{
    private const string DataMigration = "202504200500000_DisableTranscodingThrottling";

    private static readonly Version _serverVersion = new(10, 11, 0);

    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "jellyfin-backup-restore-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _folderId = Guid.NewGuid();
    private readonly Guid _movieId = Guid.NewGuid();
    private readonly Guid _extraId = Guid.NewGuid();

    public BackupRestoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task RestoreBackupAsync_IntoANewSqliteDatabase_BringsBackTheData()
    {
        var archive = await CreateBackupOfSeededSqliteDatabaseAsync();

        var target = SqliteTarget("target.db");
        await CreateBackupService(target, "target").RestoreBackupAsync(archive);

        await AssertRestoredAsync(target.CreateDbContext);
    }

    [Fact]
    public async Task RestoreBackupAsync_SqliteBackupIntoANewPostgresDatabase_MovesTheServerOver()
    {
        Assert.SkipUnless(PostgresTestDatabase.IsEnabled, "Needs a PostgreSQL server, set JELLYFIN_TEST_POSTGRES to run.");
        var archive = await CreateBackupOfSeededSqliteDatabaseAsync();

        using var database = PostgresTestDatabase.NotYetCreated();
        var provider = new PostgresDatabaseProvider(NullLogger<PostgresDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, database.Configuration);
        var target = new Target(provider, () => new JellyfinDbContext(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)));

        try
        {
            await CreateBackupService(target, "target").RestoreBackupAsync(archive);

            await AssertRestoredAsync(target.CreateDbContext);

            using var context = target.CreateDbContext();
            var history = (await context.GetService<IHistoryRepository>().GetAppliedMigrationsAsync(TestContext.Current.CancellationToken))
                .Select(e => e.MigrationId)
                .ToHashSet(StringComparer.Ordinal);

            // The PostgreSQL schema history, plus the data migrations the data went through on SQLite.
            Assert.All(context.GetService<IMigrationsAssembly>().Migrations.Keys, id => Assert.Contains(id, history));
            Assert.Contains(DataMigration, history);
            Assert.DoesNotContain(history, id => id.EndsWith("_AddUsers", StringComparison.Ordinal));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    [Fact]
    public void SortSelfReferences_ChildBeforeItsParents_WritesEveryRowAfterTheRowsItRefersTo()
    {
        using var context = SqliteTarget("order.db").CreateDbContext();
        var entityType = context.Model.FindEntityType(typeof(BaseItemEntity))!;

        var root = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Folder" };
        var parent = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Folder", ParentId = root.Id };
        var owner = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Movie", ParentId = parent.Id };
        var extra = new BaseItemEntity { Id = Guid.NewGuid(), Type = "Trailer", ParentId = parent.Id, OwnerId = owner.Id };

        var sorted = DatabaseRestoreOrder.SortSelfReferences(entityType, [extra, owner, parent, root]).Cast<BaseItemEntity>().ToList();

        Assert.Equal(4, sorted.Count);
        foreach (var item in sorted)
        {
            var index = sorted.IndexOf(item);
            Assert.True(item.ParentId is null || sorted.FindIndex(e => e.Id.Equals(item.ParentId.Value)) < index);
            Assert.True(item.OwnerId is null || sorted.FindIndex(e => e.Id.Equals(item.OwnerId.Value)) < index);
        }
    }

    private async Task<string> CreateBackupOfSeededSqliteDatabaseAsync()
    {
        var source = SqliteTarget("source.db");
        using (var context = source.CreateDbContext())
        {
            await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var historyRepository = context.GetService<IHistoryRepository>();
            await context.Database.ExecuteSqlRawAsync(
                historyRepository.GetInsertScript(new HistoryRow(DataMigration, "10.11.0")),
                TestContext.Current.CancellationToken);

            var user = new User("restored", "auth-provider", "pwdreset-provider");
            user.Id = _userId;
            context.Users.Add(user);

            // A child that refers to rows of its own table, which the restore has to write first.
            context.BaseItems.Add(new BaseItemEntity { Id = _folderId, Type = "MediaBrowser.Controller.Entities.Folder", Name = "Movies", IsFolder = true });
            context.BaseItems.Add(new BaseItemEntity { Id = _movieId, Type = "MediaBrowser.Controller.Entities.Movies.Movie", Name = "Film", ParentId = _folderId });
            context.BaseItems.Add(new BaseItemEntity { Id = _extraId, Type = "MediaBrowser.Controller.Entities.Trailer", Name = "Trailer", ParentId = _folderId, OwnerId = _movieId });

            for (var i = 0; i < 3; i++)
            {
                context.ActivityLogs.Add(new ActivityLog("entry " + i, "Test", _userId));
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Directory.CreateDirectory(Path.Combine(_root, "source", "Config"));
        await File.WriteAllTextAsync(Path.Combine(_root, "source", "Config", "database.xml"), "source", TestContext.Current.CancellationToken);

        var manifest = await CreateBackupService(source, "source").CreateBackupAsync(new BackupOptionsDto { Database = true });
        return manifest.Path;
    }

    private async Task AssertRestoredAsync(Func<JellyfinDbContext> createDbContext)
    {
        using (var context = createDbContext())
        {
            Assert.Equal("restored", Assert.Single(context.Users).Username);
            Assert.Equal(_folderId, context.BaseItems.Single(e => e.Id.Equals(_movieId)).ParentId);
            Assert.Equal(_movieId, context.BaseItems.Single(e => e.Id.Equals(_extraId)).OwnerId);
            Assert.Equal([1, 2, 3], context.ActivityLogs.OrderBy(e => e.Id).Select(e => e.Id).ToArray());

            // Keys generated after the restore must not collide with the restored ones.
            context.ActivityLogs.Add(new ActivityLog("after the restore", "Test", _userId));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var context = createDbContext())
        {
            Assert.Equal(4, context.ActivityLogs.Single(e => e.Name == "after the restore").Id);
        }

        // Where the server keeps its database is not something a backup restores.
        Assert.Equal("target", await File.ReadAllTextAsync(Path.Combine(_root, "target", "Config", "database.xml"), TestContext.Current.CancellationToken));
    }

    private Target SqliteTarget(string fileName)
    {
        var provider = new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance);
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(
                $"Data Source={Path.Combine(_root, fileName)};Pooling=false",
                e => e.MigrationsAssembly(typeof(SqliteDatabaseProvider).Assembly))
            .Options;
        return new Target(provider, () => new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)));
    }

    private BackupService CreateBackupService(Target database, string serverDirectory)
    {
        var root = Path.Combine(_root, serverDirectory);
        Directory.CreateDirectory(Path.Combine(root, "Config"));
        var databaseXml = Path.Combine(root, "Config", "database.xml");
        if (!File.Exists(databaseXml))
        {
            File.WriteAllText(databaseXml, serverDirectory);
        }

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(database.CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(database.CreateDbContext);
        database.Provider.DbContextFactory = factory.Object;

        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.Setup(a => a.ApplicationVersion).Returns(_serverVersion);

        var applicationPaths = new ServerApplicationPaths(
            root,
            Path.Combine(root, "log"),
            Path.Combine(root, "Config"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "web"));

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.IsScanRunning).Returns(false);

        return new BackupService(
            NullLogger<BackupService>.Instance,
            factory.Object,
            applicationHost.Object,
            applicationPaths,
            database.Provider,
            new Mock<IHostApplicationLifetime>().Object,
            libraryManager.Object);
    }

    private sealed record Target(IJellyfinDatabaseProvider Provider, Func<JellyfinDbContext> CreateDbContext);
}
