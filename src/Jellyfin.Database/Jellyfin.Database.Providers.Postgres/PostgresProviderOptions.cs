namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// The settings of the PostgreSQL provider that are not part of the connection string.
/// </summary>
public sealed class PostgresProviderOptions
{
    /// <summary>
    /// The database.xml option key for <see cref="MigrationBackup"/>.
    /// </summary>
    public const string MigrationBackupKey = "migration-backup";

    /// <summary>
    /// The database.xml option key for <see cref="MaintenanceDatabase"/>.
    /// </summary>
    public const string MaintenanceDatabaseKey = "maintenance-database";

    /// <summary>
    /// The database.xml option key for <see cref="EnableSensitiveDataLogging"/>.
    /// </summary>
    public const string EnableSensitiveDataLoggingKey = "EnableSensitiveDataLogging";

    /// <summary>
    /// Gets or sets a value indicating whether the database is copied before migrations change it, so a failed
    /// migration can be rolled back. Copying needs the CREATEDB privilege and no other connections to the database.
    /// </summary>
    public bool MigrationBackup { get; set; } = true;

    /// <summary>
    /// Gets or sets the database to connect to when creating, renaming or dropping the jellyfin database and its backups.
    /// </summary>
    public string MaintenanceDatabase { get; set; } = "postgres";

    /// <summary>
    /// Gets or sets a value indicating whether query parameter values are written to the log.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; set; }
}
