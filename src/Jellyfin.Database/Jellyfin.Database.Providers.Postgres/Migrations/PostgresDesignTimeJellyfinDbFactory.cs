using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Database.Providers.Postgres.Migrations
{
    /// <summary>
    /// The design time factory for <see cref="JellyfinDbContext"/>.
    /// This is only used for the creation of migrations and not during runtime.
    /// </summary>
    internal sealed class PostgresDesignTimeJellyfinDbFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext(string[] args)
        {
            var provider = new PostgresDatabaseProvider(NullLogger<PostgresDatabaseProvider>.Instance);
            var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
            provider.Initialise(optionsBuilder, new DatabaseConfigurationOptions { DatabaseType = PostgresDatabaseProvider.ProviderKey });

            return new JellyfinDbContext(
                optionsBuilder.Options,
                NullLogger<JellyfinDbContext>.Instance,
                provider,
                new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        }
    }
}
