using System;
using System.Threading;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// A throw-away PostgreSQL database for one test, copied from a template that the provider's migrations built once per
/// test run.
/// </summary>
public sealed class PostgresTestDatabase : IDisposable
{
    private const string EnvironmentVariable = "JELLYFIN_TEST_POSTGRES";

    private static readonly Lazy<string> _template = new(CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lock _createLock = new();

    private readonly string _name;

    public PostgresTestDatabase()
        : this(create: true)
    {
    }

    private PostgresTestDatabase(bool create)
    {
        _name = "jellyfin_test_" + Guid.NewGuid().ToString("N");

        if (create)
        {
            // CREATE DATABASE ... TEMPLATE fails while another session copies the same template.
            lock (_createLock)
            {
                ExecuteOnServer($"CREATE DATABASE \"{_name}\" TEMPLATE \"{_template.Value}\"");
            }
        }

        Configuration = CreateConfiguration(_name);
    }

    /// <summary>
    /// Gets a value indicating whether the tests are configured to run against PostgreSQL.
    /// </summary>
    public static bool IsEnabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Gets the database.xml configuration that points to this database.
    /// </summary>
    public DatabaseConfigurationOptions Configuration { get; }

    /// <summary>
    /// Gets a database that does not exist yet, for tests of what creates it. It is dropped like any other.
    /// </summary>
    /// <returns>The database.</returns>
    public static PostgresTestDatabase NotYetCreated() => new(create: false);

    public void Dispose()
    {
        NpgsqlConnection.ClearAllPools();
        ExecuteOnServer($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }

    private static DatabaseConfigurationOptions CreateConfiguration(string database)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(EnvironmentVariable))
        {
            Database = database
        };

        return new DatabaseConfigurationOptions
        {
            DatabaseType = PostgresDatabaseProvider.ProviderKey,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = connectionString.ConnectionString
            }
        };
    }

    private static string CreateTemplate()
    {
        var name = "jellyfin_test_template_" + Guid.NewGuid().ToString("N");
        ExecuteOnServer($"CREATE DATABASE \"{name}\"");

        var provider = new PostgresDatabaseProvider(NullLogger<PostgresDatabaseProvider>.Instance);
        var builder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(builder, CreateConfiguration(name));
        using (var context = new JellyfinDbContext(
            builder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)))
        {
            context.Database.Migrate();
        }

        // Copying needs the template to itself.
        NpgsqlConnection.ClearAllPools();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ExecuteOnServer($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        return name;
    }

    private static void ExecuteOnServer(string sql)
    {
        var server = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(EnvironmentVariable))
        {
            Database = "postgres",
            Pooling = false
        };

        using var connection = new NpgsqlConnection(server.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Database names cannot be parameters; the test generates them.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }
}
