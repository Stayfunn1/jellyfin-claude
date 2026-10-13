using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Orders restored rows so that every row is written after the rows its foreign keys point to.
/// </summary>
internal static class DatabaseRestoreOrder
{
    /// <summary>
    /// Sorts tables so that each comes after the tables it refers to.
    /// </summary>
    /// <param name="tables">The tables.</param>
    /// <param name="getEntityType">Gets the entity type of a table.</param>
    /// <typeparam name="T">The type describing a table.</typeparam>
    /// <returns>The tables in an order they can be filled in.</returns>
    public static IReadOnlyList<T> SortByDependencies<T>(IReadOnlyList<T> tables, Func<T, IEntityType> getEntityType)
    {
        var known = tables.Select(getEntityType).ToHashSet();
        var placed = new HashSet<IEntityType>();
        var remaining = tables.ToList();
        var result = new List<T>(tables.Count);

        while (remaining.Count > 0)
        {
            var next = remaining.FindIndex(table =>
            {
                var entityType = getEntityType(table);
                return entityType.GetForeignKeys().All(fk =>
                    fk.PrincipalEntityType == entityType
                    || placed.Contains(fk.PrincipalEntityType)
                    || !known.Contains(fk.PrincipalEntityType));
            });

            // Only a cycle between tables leaves nothing ready; take them in the order given then.
            next = Math.Max(next, 0);
            result.Add(remaining[next]);
            placed.Add(getEntityType(remaining[next]));
            remaining.RemoveAt(next);
        }

        return result;
    }

    /// <summary>
    /// Checks whether rows of the table can refer to other rows of the same table.
    /// </summary>
    /// <param name="entityType">The entity type of the table.</param>
    /// <returns><c>true</c> if the table refers to itself.</returns>
    public static bool HasSelfReferences(IEntityType entityType) => GetSelfReferences(entityType).Count > 0;

    /// <summary>
    /// Sorts the rows of a table that refers to itself so that every row comes after the rows it refers to.
    /// </summary>
    /// <param name="entityType">The entity type of the table.</param>
    /// <param name="entities">The rows.</param>
    /// <returns>The rows in an order they can be written in.</returns>
    public static IEnumerable<object> SortSelfReferences(IEntityType entityType, IReadOnlyList<object> entities)
    {
        var references = GetSelfReferences(entityType);
        if (references.Count == 0)
        {
            return entities;
        }

        var principalKey = references[0].PrincipalKey.Properties[0].GetGetter();
        var byKey = new Dictionary<object, object>();
        foreach (var entity in entities)
        {
            if (principalKey.GetClrValue(entity) is { } key)
            {
                byKey[key] = entity;
            }
        }

        var getters = references.Select(fk => fk.Properties[0].GetGetter()).ToArray();
        var written = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var inProgress = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var result = new List<object>(entities.Count);
        var stack = new Stack<(object Entity, bool Expanded)>();

        foreach (var root in entities)
        {
            stack.Push((root, false));
            while (stack.Count > 0)
            {
                var (entity, expanded) = stack.Pop();
                if (expanded)
                {
                    inProgress.Remove(entity);
                    written.Add(entity);
                    result.Add(entity);
                    continue;
                }

                if (written.Contains(entity) || !inProgress.Add(entity))
                {
                    continue;
                }

                // Come back to this row once the rows it refers to are written. A row that is already waiting for the
                // rows it refers to closes a cycle, which is broken there.
                stack.Push((entity, true));
                foreach (var getter in getters)
                {
                    if (getter.GetClrValue(entity) is { } reference
                        && byKey.TryGetValue(reference, out var referenced)
                        && !written.Contains(referenced)
                        && !inProgress.Contains(referenced))
                    {
                        stack.Push((referenced, false));
                    }
                }
            }
        }

        return result;
    }

    private static List<IForeignKey> GetSelfReferences(IEntityType entityType)
    {
        return entityType.GetForeignKeys()
            .Where(fk => fk.PrincipalEntityType == entityType
                && fk.Properties.Count == 1
                && fk.PrincipalKey.Properties.Count == 1)
            .ToList();
    }
}
