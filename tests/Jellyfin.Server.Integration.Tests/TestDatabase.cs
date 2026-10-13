using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Postgres;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Jellyfin.Server.Integration.Tests;

/// <summary>
/// The database of a test server: the default SQLite file, or, when the <c>JELLYFIN_TEST_POSTGRES</c> environment
/// variable holds a connection string to a PostgreSQL server, a PostgreSQL database of its own that the server creates on
/// its first start.
/// </summary>
public static class TestDatabase
{
    private const string EnvironmentVariable = "JELLYFIN_TEST_POSTGRES";

    private static readonly ConcurrentDictionary<string, byte> _createdDatabases = new(StringComparer.Ordinal);

    static TestDatabase()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var database in _createdDatabases.Keys)
            {
                Drop(database);
            }
        };
    }

    /// <summary>
    /// Gets a value indicating whether the test servers run on PostgreSQL.
    /// </summary>
    public static bool IsPostgres => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Points the server in the given directory to its PostgreSQL database, if the tests run on PostgreSQL.
    /// </summary>
    /// <param name="webHostPathRoot">The directory the server keeps its files in.</param>
    public static void Configure(string webHostPathRoot)
    {
        if (!IsPostgres)
        {
            return;
        }

        var databaseXml = Path.Combine(webHostPathRoot, "config", "database.xml");
        if (File.Exists(databaseXml))
        {
            return;
        }

        var database = GetDatabaseName(webHostPathRoot);
        _createdDatabases.TryAdd(database, 0);
        var connectionString = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(EnvironmentVariable))
        {
            Database = database
        };

        new MyXmlSerializer().SerializeToFile(
            new DatabaseConfigurationOptions
            {
                DatabaseType = PostgresDatabaseProvider.ProviderKey,
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = string.Empty,
                    PluginAssembly = string.Empty,
                    ConnectionString = connectionString.ConnectionString
                }
            },
            databaseXml);
    }

    /// <summary>
    /// Deletes the database of the server in the given directory, as if it had been lost.
    /// </summary>
    /// <param name="webHostPathRoot">The directory the server keeps its files in.</param>
    /// <param name="dataPath">The data directory of the server.</param>
    public static void Delete(string webHostPathRoot, string dataPath)
    {
        if (IsPostgres)
        {
            NpgsqlConnection.ClearAllPools();
            Drop(GetDatabaseName(webHostPathRoot));
            return;
        }

        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(dataPath, "jellyfin.db*"))
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// Checks whether the database of the server in the given directory exists.
    /// </summary>
    /// <param name="webHostPathRoot">The directory the server keeps its files in.</param>
    /// <param name="dataPath">The data directory of the server.</param>
    /// <returns><c>true</c> if the database exists; otherwise <c>false</c>.</returns>
    public static bool Exists(string webHostPathRoot, string dataPath)
    {
        if (!IsPostgres)
        {
            return Directory.GetFiles(dataPath, "jellyfin.db*").Length > 0;
        }

        using var connection = OpenServerConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("name", GetDatabaseName(webHostPathRoot));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static string GetDatabaseName(string webHostPathRoot)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(webHostPathRoot)));
        return "jellyfin_it_" + Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }

    private static void Drop(string database)
    {
        using var connection = OpenServerConnection();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Database names cannot be parameters; these are generated from a hash.
        command.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static NpgsqlConnection OpenServerConnection()
    {
        var server = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(EnvironmentVariable))
        {
            Database = "postgres",
            Pooling = false
        };

        var connection = new NpgsqlConnection(server.ConnectionString);
        connection.Open();
        return connection;
    }
}
