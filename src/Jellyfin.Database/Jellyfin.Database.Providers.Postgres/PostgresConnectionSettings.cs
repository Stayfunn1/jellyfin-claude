using System;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.DbConfiguration;
using Npgsql;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Turns the database.xml settings into a connection string and provider options.
/// </summary>
public static class PostgresConnectionSettings
{
    /// <summary>
    /// The host used when neither database.xml nor the PGHOST environment variable names one.
    /// </summary>
    public const string DefaultHost = "localhost";

    /// <summary>
    /// The database and user name used when neither database.xml nor the libpq environment variables name one.
    /// </summary>
    public const string DefaultName = "jellyfin";

    /// <summary>
    /// Builds the connection string and the provider options from the custom options of database.xml.
    /// </summary>
    /// <param name="customOptions">The custom provider options, if any.</param>
    /// <returns>The connection string and the provider options.</returns>
    /// <exception cref="InvalidOperationException">An option is neither a provider option nor a connection string keyword.</exception>
    public static (NpgsqlConnectionStringBuilder ConnectionString, PostgresProviderOptions Options) Build(CustomDatabaseOptions? customOptions)
    {
        var builder = new NpgsqlConnectionStringBuilder(customOptions?.ConnectionString ?? string.Empty);
        var providerOptions = new PostgresProviderOptions();

        foreach (var option in customOptions?.Options ?? [])
        {
            if (option.Key.Equals(PostgresProviderOptions.MigrationBackupKey, StringComparison.OrdinalIgnoreCase))
            {
                providerOptions.MigrationBackup = ParseBool(option.Key, option.Value);
            }
            else if (option.Key.Equals(PostgresProviderOptions.MaintenanceDatabaseKey, StringComparison.OrdinalIgnoreCase))
            {
                providerOptions.MaintenanceDatabase = option.Value;
            }
            else if (option.Key.Equals(PostgresProviderOptions.EnableSensitiveDataLoggingKey, StringComparison.OrdinalIgnoreCase))
            {
                providerOptions.EnableSensitiveDataLogging = ParseBool(option.Key, option.Value);
            }
            else
            {
                try
                {
                    builder[option.Key] = option.Value;
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException(
                        $"The PostgreSQL option '{option.Key}' in database.xml is neither a provider option nor an Npgsql connection string keyword.",
                        ex);
                }
            }
        }

        // Settings database.xml leaves out come from the libpq environment variables, e.g. to keep the password out of
        // the file. Npgsql only reads some of them itself.
        if (string.IsNullOrEmpty(builder.Host))
        {
            builder.Host = GetEnvironmentVariable("PGHOST") ?? DefaultHost;
        }

        if (!HasOption(customOptions, "Port") && GetEnvironmentVariable("PGPORT") is { } port)
        {
            builder.Port = int.Parse(port, CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrEmpty(builder.Database))
        {
            builder.Database = GetEnvironmentVariable("PGDATABASE") ?? DefaultName;
        }

        if (string.IsNullOrEmpty(builder.Username))
        {
            builder.Username = GetEnvironmentVariable("PGUSER") ?? DefaultName;
        }

        if (string.IsNullOrEmpty(builder.Password) && GetEnvironmentVariable("PGPASSWORD") is { } password)
        {
            builder.Password = password;
        }

        if (string.IsNullOrEmpty(builder.ApplicationName))
        {
            builder.ApplicationName = "Jellyfin";
        }

        return (builder, providerOptions);
    }

    /// <summary>
    /// Gets the connection string with its password hidden, for logging.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <returns>The connection string without the password.</returns>
    public static string Redact(NpgsqlConnectionStringBuilder connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        var redacted = new NpgsqlConnectionStringBuilder(connectionString.ConnectionString);
        if (!string.IsNullOrEmpty(redacted.Password))
        {
            redacted.Password = "********";
        }

        return redacted.ConnectionString;
    }

    private static string? GetEnvironmentVariable(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static bool HasOption(CustomDatabaseOptions? customOptions, string keyword)
    {
        if (customOptions is null)
        {
            return false;
        }

        // NpgsqlConnectionStringBuilder answers ContainsKey for every keyword it knows, set or not.
        var fromConnectionString = new DbConnectionStringBuilder { ConnectionString = customOptions.ConnectionString ?? string.Empty };
        return fromConnectionString.ContainsKey(keyword)
            || customOptions.Options.Any(e => e.Key.Equals(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ParseBool(string key, string value)
    {
        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"The PostgreSQL option '{key}' in database.xml must be true or false, not '{value}'.");
    }
}
