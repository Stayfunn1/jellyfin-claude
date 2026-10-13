using System;
using System.Collections.Generic;
using System.Threading;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Base fixture for the item tests that run against a real database: one database per test class instance, plus the
/// wiring the repositories under test need. Derived classes seed in their own constructor.
/// </summary>
/// <remarks>
/// The tests run against an in-memory SQLite database. When the <c>JELLYFIN_TEST_POSTGRES</c> environment variable holds
/// a connection string to a PostgreSQL server, they run against a fresh PostgreSQL database instead, which is created
/// from the provider's migrations and dropped afterwards.
/// </remarks>
public abstract class SqliteDbTestFixture : IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly PostgresTestDatabase? _postgres;
    private readonly IJellyfinDatabaseProvider _provider;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    protected SqliteDbTestFixture(params IInterceptor[] interceptors)
    {
        ApplicationPaths = new Mock<IApplicationPaths>().Object;

        if (PostgresTestDatabase.IsEnabled)
        {
            _postgres = new PostgresTestDatabase();
            var postgresProvider = new PostgresDatabaseProvider(NullLogger<PostgresDatabaseProvider>.Instance);
            var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
            postgresProvider.Initialise(builder, _postgres.Configuration);
            _provider = postgresProvider;
            _dbOptions = builder.AddInterceptors(interceptors).Options;
            return;
        }

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options;
        _provider = new SqliteDatabaseProvider(ApplicationPaths, NullLogger<SqliteDatabaseProvider>.Instance);

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    protected IApplicationPaths ApplicationPaths { get; }

    /// <summary>
    /// Gets a value indicating whether the tests run against PostgreSQL rather than SQLite.
    /// </summary>
    protected bool IsPostgres => _postgres is not null;

    /// <summary>
    /// Gets the database provider the contexts are created with.
    /// </summary>
    protected IJellyfinDatabaseProvider DatabaseProvider => _provider;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        _provider,
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    protected IDbContextFactory<JellyfinDbContext> CreateDbContextFactory()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDbContext);

        return factory.Object;
    }

    /// <summary>
    /// Gets the plan the database picks for a query, one line per step.
    /// </summary>
    /// <param name="sql">The query.</param>
    /// <param name="parameters">The values of the query parameters.</param>
    /// <returns>The lines of the plan.</returns>
    /// <remarks>
    /// On PostgreSQL the tables are analyzed and sequential scans are switched off for the query: the test tables hold a
    /// handful of rows, which the planner reads faster without an index, but the point is whether an index serves it.
    /// </remarks>
    protected string[] ExplainQueryPlan(string sql, IEnumerable<(string Name, object? Value)> parameters)
    {
        using var context = CreateDbContext();
        context.Database.OpenConnection();
        var connection = context.Database.GetDbConnection();
        if (IsPostgres)
        {
            // Without statistics every index that starts with a matching column looks equally good.
            context.Database.ExecuteSqlRaw("ANALYZE");
            context.Database.ExecuteSqlRaw("SET enable_seqscan = off");
        }

        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The query is generated by EF Core; its values remain bound parameters.
        command.CommandText = (IsPostgres ? "EXPLAIN " : "EXPLAIN QUERY PLAN ") + sql;
#pragma warning restore CA2100
        foreach (var value in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Name;
            parameter.Value = value.Value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var plan = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                // SQLite describes each step in the fourth column, PostgreSQL in its only one.
                plan.Add(reader.GetString(IsPostgres ? 0 : 3));
            }
        }

        if (IsPostgres)
        {
            context.Database.ExecuteSqlRaw("RESET enable_seqscan");
        }

        return plan.ToArray();
    }

    protected BaseItemRepository CreateBaseItemRepository(ItemTypeLookup itemTypeLookup)
    {
        var serverConfigurationManager = new Mock<IServerConfigurationManager>();
        serverConfigurationManager.Setup(c => c.Configuration).Returns(new ServerConfiguration());

        return new BaseItemRepository(
            CreateDbContextFactory(),
            new Mock<IServerApplicationHost>().Object,
            itemTypeLookup,
            serverConfigurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection?.Dispose();
            _postgres?.Dispose();
        }
    }
}
