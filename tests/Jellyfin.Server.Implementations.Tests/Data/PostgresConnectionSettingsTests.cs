using System;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Postgres;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

public sealed class PostgresConnectionSettingsTests
{
    [Fact]
    public void Build_OptionsAndConnectionString_OptionsWin()
    {
        var options = Options("Host=from-string;Database=from-string;Username=u", ("Database", "from-option"), ("SSL Mode", "Require"));

        var (connectionString, _) = PostgresConnectionSettings.Build(options);

        Assert.Equal("from-string", connectionString.Host);
        Assert.Equal("from-option", connectionString.Database);
        Assert.Equal(Npgsql.SslMode.Require, connectionString.SslMode);
        Assert.Equal("Jellyfin", connectionString.ApplicationName);
    }

    [Fact]
    public void Build_ProviderOptions_AreNotPartOfTheConnectionString()
    {
        var options = Options("Host=h", ("migration-backup", "false"), ("maintenance-database", "template1"), ("EnableSensitiveDataLogging", "True"));

        var (connectionString, providerOptions) = PostgresConnectionSettings.Build(options);

        Assert.False(providerOptions.MigrationBackup);
        Assert.Equal("template1", providerOptions.MaintenanceDatabase);
        Assert.True(providerOptions.EnableSensitiveDataLogging);
        Assert.DoesNotContain("migration", connectionString.ConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_UnknownOption_ExplainsWhichOne()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PostgresConnectionSettings.Build(Options(string.Empty, ("Hots", "typo"))));

        Assert.Contains("'Hots'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NothingConfigured_UsesTheDefaults()
    {
        Assert.SkipWhen(
            Environment.GetEnvironmentVariable("PGHOST") is not null
                || Environment.GetEnvironmentVariable("PGDATABASE") is not null
                || Environment.GetEnvironmentVariable("PGUSER") is not null,
            "The libpq environment variables are set for this process.");

        var (connectionString, providerOptions) = PostgresConnectionSettings.Build(null);

        Assert.Equal("localhost", connectionString.Host);
        Assert.Equal("jellyfin", connectionString.Database);
        Assert.Equal("jellyfin", connectionString.Username);
        Assert.True(providerOptions.MigrationBackup);
    }

    [Fact]
    public void Redact_HidesThePassword()
    {
        var (connectionString, _) = PostgresConnectionSettings.Build(Options("Host=h;Password=s3cret"));

        var redacted = PostgresConnectionSettings.Redact(connectionString);

        Assert.DoesNotContain("s3cret", redacted, StringComparison.Ordinal);
        Assert.Equal("s3cret", connectionString.Password);
    }

    private static CustomDatabaseOptions Options(string connectionString, params (string Key, string Value)[] options)
    {
        var result = new CustomDatabaseOptions
        {
            PluginName = string.Empty,
            PluginAssembly = string.Empty,
            ConnectionString = connectionString
        };

        foreach (var (key, value) in options)
        {
            result.Options.Add(new CustomDatabaseOption { Key = key, Value = value });
        }

        return result;
    }
}
