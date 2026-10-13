using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Stores all text as unbounded text.
/// </summary>
/// <remarks>
/// SQLite never enforced the maximum lengths declared on the entities, so jellyfin never had to respect them and
/// existing databases hold longer values. PostgreSQL would reject those, e.g. a device name a client made up, and with
/// it the login of that client.
/// </remarks>
internal sealed class UnboundedStringConvention : IModelFinalizingConvention
{
    /// <inheritdoc/>
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.ClrType == typeof(string) && property.GetMaxLength() is not null)
                {
                    property.Builder.HasMaxLength(null, fromDataAnnotation: true);
                }
            }
        }
    }
}
