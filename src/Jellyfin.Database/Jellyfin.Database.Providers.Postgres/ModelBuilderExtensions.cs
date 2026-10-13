using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Database.Providers.Postgres;

/// <summary>
/// Model builder extensions.
/// </summary>
internal static class ModelBuilderExtensions
{
    /// <summary>
    /// Converts every <see cref="DateTime"/> and <see cref="DateTimeOffset"/> to UTC before it is written and marks the
    /// values read back as UTC.
    /// </summary>
    /// <param name="modelBuilder">The model builder to extend.</param>
    public static void UseUtcDateTimes(this ModelBuilder modelBuilder)
    {
        var dateTimeConverter = new ValueConverter<DateTime, DateTime>(
            v => v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, DateTimeOffset>(
            v => v.ToUniversalTime(),
            v => v);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (type == typeof(DateTime))
                {
                    property.SetValueConverter(dateTimeConverter);
                }
                else if (type == typeof(DateTimeOffset))
                {
                    property.SetValueConverter(dateTimeOffsetConverter);
                }
            }
        }
    }
}
