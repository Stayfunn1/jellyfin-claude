using System;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Migrations.Routines;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

/// <summary>
/// Runs <see cref="MigrateRatingLevels"/> against an in-memory SQLite database, or against a new PostgreSQL database when
/// <c>JELLYFIN_TEST_POSTGRES</c> holds a connection string to a PostgreSQL server.
/// </summary>
public sealed class MigrateRatingLevelsTests : IDisposable
{
    private readonly SqliteConnection? _sqlite;
    private readonly string? _postgresDatabase;
    private readonly IJellyfinDatabaseProvider _provider;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    public MigrateRatingLevelsTests()
    {
        var postgres = Environment.GetEnvironmentVariable("JELLYFIN_TEST_POSTGRES");
        if (string.IsNullOrEmpty(postgres))
        {
            _sqlite = new SqliteConnection("Data Source=:memory:");
            _sqlite.Open();
            _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_sqlite).Options;
            _provider = new SqliteDatabaseProvider(new Mock<IApplicationPaths>().Object, NullLogger<SqliteDatabaseProvider>.Instance);
            using var context = CreateDbContext();
            context.Database.EnsureCreated();
            return;
        }

        _postgresDatabase = "jellyfin_test_" + Guid.NewGuid().ToString("N");
        var provider = new PostgresDatabaseProvider(NullLogger<PostgresDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, new DatabaseConfigurationOptions
        {
            DatabaseType = PostgresDatabaseProvider.ProviderKey,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = new NpgsqlConnectionStringBuilder(postgres) { Database = _postgresDatabase }.ConnectionString
            }
        });
        _provider = provider;
        _dbOptions = builder.Options;
        using (var context = CreateDbContext())
        {
            // Creates the database as well.
            context.Database.Migrate();
        }
    }

    public void Dispose()
    {
        _sqlite?.Dispose();
        if (_postgresDatabase is not null)
        {
            NpgsqlConnection.ClearAllPools();
            var server = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("JELLYFIN_TEST_POSTGRES")) { Database = "postgres", Pooling = false };
            using var connection = new NpgsqlConnection(server.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Database names cannot be parameters; the test generates this one.
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_postgresDatabase}\" WITH (FORCE)";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Perform_ItemsWithAndWithoutRatings_GetTheLevelsOfTheirRatings()
    {
        var rated = Guid.NewGuid();
        var unrated = Guid.NewGuid();
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = rated, Type = "Movie", OfficialRating = "PG-13", InheritedParentalRatingValue = 1 });
            context.BaseItems.Add(new BaseItemEntity { Id = unrated, Type = "Movie", InheritedParentalRatingValue = 5 });
            context.SaveChanges();
        }

        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetRatingScore("PG-13", null)).Returns(new ParentalRatingScore(13, 2));
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);

        // Updates the items while it goes through the ratings, which PostgreSQL only allows once they have been read.
        new MigrateRatingLevels(
            factory.Object,
            new StartupLogger<MigrateRatingLevels>(NullLogger<MigrateRatingLevels>.Instance),
            localization.Object).Perform();

        using var after = CreateDbContext();
        var ratedItem = after.BaseItems.Single(e => e.Id.Equals(rated));
        Assert.Equal(13, ratedItem.InheritedParentalRatingValue);
        Assert.Equal(2, ratedItem.InheritedParentalRatingSubValue);
        Assert.Null(after.BaseItems.Single(e => e.Id.Equals(unrated)).InheritedParentalRatingValue);
    }

    private JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        _provider,
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
