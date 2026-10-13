using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Configures jellyfin to use a PostgreSQL database.
/// </summary>
/// <remarks>
/// The connection is configured in database.xml: <see cref="CustomDatabaseOptions.ConnectionString"/> takes a regular
/// Npgsql connection string, and every entry in <see cref="CustomDatabaseOptions.Options"/> whose key is a connection
/// string keyword (host, port, database, username, password, ssl mode, ...) is applied on top of it. The keys listed
/// in <see cref="PostgresProviderOptions"/> configure the provider itself.
/// </remarks>
[JellyfinDatabaseProviderKey(ProviderKey)]
public sealed class PostgresDatabaseProvider : IJellyfinDatabaseProvider
{
    /// <summary>
    /// The key that selects this provider in database.xml.
    /// </summary>
    public const string ProviderKey = "Jellyfin-PostgreSQL";

    private const string BackupDatabaseInfix = "_backup_";

    // Stateless, and shared so every context options instance resolves to the same EF Core service provider.
    private static readonly IInterceptor[] _interceptors = [new CaseInsensitiveLikeInterceptor(), new NulCharacterSaveChangesInterceptor(), new WriteTransactionLockInterceptor()];

    private readonly ILogger<PostgresDatabaseProvider> _logger;
    private NpgsqlConnectionStringBuilder? _connectionString;
    private PostgresProviderOptions _providerOptions = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresDatabaseProvider"/> class.
    /// </summary>
    /// <param name="logger">A logger.</param>
    public PostgresDatabaseProvider(ILogger<PostgresDatabaseProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(databaseConfiguration);

        // The context factory is pooled, so this runs once per process and the settings below stay put.
        if (_connectionString is null)
        {
            (_connectionString, _providerOptions) = PostgresConnectionSettings.Build(databaseConfiguration.CustomProviderOptions);
            _logger.LogInformation("PostgreSQL connection string: {ConnectionString}", PostgresConnectionSettings.Redact(_connectionString));
        }

        options
            .UseNpgsql(
                _connectionString.ConnectionString,
                npgsqlOptions => npgsqlOptions.MigrationsAssembly(GetType().Assembly))
            .ReplaceService<IQuerySqlGeneratorFactory, NullsFirstQuerySqlGeneratorFactory>()
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.MultipleCollectionIncludeWarning))
            .AddInterceptors(_interceptors);

        if (_providerOptions.EnableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging();
            _logger.LogInformation("EnableSensitiveDataLogging is enabled on PostgreSQL connection");
        }
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        // timestamp with time zone only accepts UTC values, while parts of jellyfin hand over local or unspecified
        // ones. Converting them the same way the SQLite provider does keeps both databases holding the same values.
        modelBuilder.UseUtcDateTimes();
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // SQLite compares and sorts text byte by byte and folds only ASCII letters in lower(), upper() and LIKE. The
        // C collation does the same, so queries return what they return on SQLite whatever locale the database was
        // created with, and the B-tree indexes on text columns also serve prefix searches.
        configurationBuilder.Properties<string>().UseCollation("C");

        // Npgsql would map these to the xid system type, which is not meant to hold application data.
        configurationBuilder.Properties<uint>().HaveConversion<long>();

        configurationBuilder.Conventions.Add(_ => new UnboundedStringConvention());
        configurationBuilder.Conventions.Add(_ => new NullsFirstIndexConvention());
    }

    /// <inheritdoc/>
    public async Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            // Autovacuum keeps up with day to day changes, this catches up after bulk changes such as migrations.
            // Statistics taken while the library is empty make the planner treat every table as one row, so they
            // are left to autovacuum until there is something in it.
            var analyze = await HasLibraryItemsAsync(context, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Vacuuming{Analyze} the PostgreSQL database", analyze ? " and analyzing" : string.Empty);
            await context.Database.ExecuteSqlRawAsync(analyze ? "VACUUM (ANALYZE)" : "VACUUM", cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("PostgreSQL database optimized successfully!");
        }
    }

    /// <inheritdoc/>
    public async Task RefreshStatistics(CancellationToken cancellationToken)
    {
        if (DbContextFactory is null)
        {
            return;
        }

        var context = await DbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (!await HasLibraryItemsAsync(context, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            _logger.LogInformation("Analyzing the PostgreSQL database");
            await context.Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        // The server holds all data, there is nothing to flush. Closing the idle connections right away saves the
        // server from noticing the process has gone.
        NpgsqlConnection.ClearAllPools();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<string> MigrationBackupFast(CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        if (!_providerOptions.MigrationBackup)
        {
            throw new NotSupportedException(
                $"Backups before migrations are disabled by the {PostgresProviderOptions.MigrationBackupKey} option of the PostgreSQL provider.");
        }

        var database = connectionString.Database!;
        var key = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var backupDatabase = GetBackupDatabaseName(database, key);

        // CREATE DATABASE ... TEMPLATE needs the source database to itself, so give back the idle pooled connections.
        NpgsqlConnection.ClearAllPools();

        var connection = await OpenMaintenanceConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            _logger.LogInformation("Copying database {Database} to {BackupDatabase} before migrating", database, backupDatabase);
            try
            {
                await ExecuteAsync(
                    connection,
                    $"CREATE DATABASE {QuoteIdentifier(backupDatabase)} WITH TEMPLATE {QuoteIdentifier(database)}",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.ObjectInUse)
            {
                throw new NotSupportedException(
                    $"Copying the database failed: {ex.MessageText}. Copying needs the CREATEDB privilege and no other connections to the database; "
                    + $"set the {PostgresProviderOptions.MigrationBackupKey} option to false to stop trying.",
                    ex);
            }
        }

        return key;
    }

    /// <inheritdoc/>
    public async Task RestoreBackupFast(string key, CancellationToken cancellationToken)
    {
        var connectionString = GetConnectionString();
        var database = connectionString.Database!;
        var backupDatabase = GetBackupDatabaseName(database, key);

        NpgsqlConnection.ClearAllPools();

        var connection = await OpenMaintenanceConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (!await DatabaseExistsAsync(connection, backupDatabase, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogCritical("Tried to restore a backup that does not exist: {Key}", key);
                return;
            }

            _logger.LogWarning("Replacing database {Database} with its backup {BackupDatabase}", database, backupDatabase);
            await ExecuteAsync(connection, $"DROP DATABASE IF EXISTS {QuoteIdentifier(database)} WITH (FORCE)", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                $"ALTER DATABASE {QuoteIdentifier(backupDatabase)} RENAME TO {QuoteIdentifier(database)}",
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task DeleteBackup(string key)
    {
        var connectionString = GetConnectionString();
        var backupDatabase = GetBackupDatabaseName(connectionString.Database!, key);

        var connection = await OpenMaintenanceConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            if (!await DatabaseExistsAsync(connection, backupDatabase, CancellationToken.None).ConfigureAwait(false))
            {
                _logger.LogCritical("Tried to delete a backup that does not exist: {Key}", key);
                return;
            }

            await ExecuteAsync(connection, $"DROP DATABASE {QuoteIdentifier(backupDatabase)} WITH (FORCE)", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(tableNames);

        var tables = tableNames.Select(QuoteQualifiedName).ToArray();
        if (tables.Length == 0)
        {
            return;
        }

        // One statement for all tables, so the foreign keys between them do not get in the way. Table names cannot
        // be passed as parameters; they come from the model and are quoted.
        var truncateQuery = $"TRUNCATE TABLE {string.Join(", ", tables)} RESTART IDENTITY CASCADE";
        await dbContext.Database.ExecuteSqlRawAsync(truncateQuery).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task SynchronizeGeneratedKeys(JellyfinDbContext dbContext, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        // Rows written with explicit keys do not move the identity sequences on, so the next generated key would
        // collide with them. Point every sequence past the highest key in its table.
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN
                    SELECT table_schema, table_name, column_name
                    FROM information_schema.columns
                    WHERE is_identity = 'YES' AND table_schema = current_schema()
                LOOP
                    EXECUTE format(
                        'SELECT setval(pg_get_serial_sequence(%L, %L), COALESCE((SELECT MAX(%I) FROM %I.%I), 0) + 1, false)',
                        quote_ident(r.table_schema) || '.' || quote_ident(r.table_name),
                        r.column_name,
                        r.column_name,
                        r.table_schema,
                        r.table_name);
                END LOOP;
            END $$;
            """,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the name of the database that holds the backup with the given key.
    /// </summary>
    /// <param name="database">The name of the jellyfin database.</param>
    /// <param name="key">The backup key.</param>
    /// <returns>The name of the backup database.</returns>
    internal static string GetBackupDatabaseName(string database, string key)
    {
        // PostgreSQL cuts identifiers off after 63 bytes, which could make the backups of two databases collide.
        var suffix = BackupDatabaseInfix + key;
        var maxPrefixLength = 63 - suffix.Length;
        var prefix = database;
        while (System.Text.Encoding.UTF8.GetByteCount(prefix) > maxPrefixLength)
        {
            prefix = prefix[..^1];
        }

        return prefix + suffix;
    }

    private static Task<bool> HasLibraryItemsAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        // Folders and the seeded placeholder exist before any library has been scanned.
        return context.BaseItems.AnyAsync(e => !e.IsFolder && e.Type != "PLACEHOLDER", cancellationToken);
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string QuoteQualifiedName(string name)
    {
        var separator = name.IndexOf('.', StringComparison.Ordinal);
        return separator < 0
            ? QuoteIdentifier(name)
            : QuoteIdentifier(name[..separator]) + "." + QuoteIdentifier(name[(separator + 1)..]);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;

            // Copying a large database takes a while.
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnection connection, string database, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name";
            command.Parameters.AddWithValue("name", database);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
    }

    private NpgsqlConnectionStringBuilder GetConnectionString()
    {
        return _connectionString ?? throw new InvalidOperationException("The database provider has not been initialized.");
    }

    private async Task<NpgsqlConnection> OpenMaintenanceConnectionAsync(CancellationToken cancellationToken)
    {
        // Databases can only be created, dropped and renamed from a connection to another database.
        var maintenance = new NpgsqlConnectionStringBuilder(GetConnectionString().ConnectionString)
        {
            Database = _providerOptions.MaintenanceDatabase,
            Pooling = false
        };

        var connection = new NpgsqlConnection(maintenance.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }
}
