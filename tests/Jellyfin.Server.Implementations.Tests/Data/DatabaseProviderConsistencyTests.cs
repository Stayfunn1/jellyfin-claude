using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Tests.Item;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

/// <summary>
/// Behaviour the rest of jellyfin was written against on SQLite, and which every database provider has to keep. Runs
/// against whichever database the fixture is configured for.
/// </summary>
public sealed class DatabaseProviderConsistencyTests : SqliteDbTestFixture
{
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";

    [Fact]
    public void OrderBy_OptionalValue_SortsItemsWithoutOneFirst()
    {
        var undated = AddItem("Undated");
        var older = AddItem("Older", premiereDate: new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = AddItem("Newer", premiereDate: new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Guid[] ids = [undated, older, newer];

        using var context = CreateDbContext();
        var ascending = context.BaseItems.Where(e => ids.Contains(e.Id)).OrderBy(e => e.PremiereDate).Select(e => e.Id).ToList();
        var descending = context.BaseItems.Where(e => ids.Contains(e.Id)).OrderByDescending(e => e.PremiereDate).Select(e => e.Id).ToList();

        // Sorting by e.g. the last played date, newest first, must not put everything never played on top.
        Assert.Equal([undated, older, newer], ascending);
        Assert.Equal([newer, older, undated], descending);
    }

    [Fact]
    public void Like_IgnoresTheCaseOfLettersAndHasNoEscapeCharacter()
    {
        var matrix = AddItem("The Matrix");
        var band = AddItem(@"AC\DC");

        using var context = CreateDbContext();
        Assert.Equal(matrix, context.BaseItems.Where(e => EF.Functions.Like(e.Name, "%MATRIX%")).Select(e => e.Id).Single());
        Assert.Equal(band, context.BaseItems.Where(e => EF.Functions.Like(e.Name, @"%c\d%")).Select(e => e.Id).Single());
    }

    [Fact]
    public void Min_OverItemIds_PicksTheSameItemAsSqlite()
    {
        Guid[] ids = [AddItem("A"), AddItem("B"), AddItem("C"), AddItem("D")];

        using var context = CreateDbContext();
        var min = context.BaseItems.Where(e => ids.Contains(e.Id)).GroupBy(e => e.Type).Select(g => g.Min(e => e.Id)).Single();
        var max = context.BaseItems.Where(e => ids.Contains(e.Id)).GroupBy(e => e.Type).Select(g => g.Max(e => e.Id)).Single();

        // SQLite stores ids as upper case text, so that is the order the grouping queries were written for.
        var textOrder = ids.OrderBy(id => id.ToString("D").ToUpperInvariant(), StringComparer.Ordinal).ToArray();
        Assert.Equal(textOrder[0], min);
        Assert.Equal(textOrder[^1], max);
    }

    [Fact]
    public void SaveChanges_TextLongerThanTheDeclaredLength_IsStored()
    {
        // SQLite never enforced the declared lengths, so existing data and clients rely on longer values.
        var name = new string('x', 2000);

        using (var context = CreateDbContext())
        {
            context.ActivityLogs.Add(new ActivityLog(name, "Test", Guid.NewGuid()));
            context.SaveChanges();
        }

        using var after = CreateDbContext();
        Assert.Equal(name, after.ActivityLogs.Single().Name);
    }

    [Fact]
    public void SaveChanges_LocalAndUnspecifiedTimes_AreStoredAsUtc()
    {
        var local = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);
        var localId = AddItem("Local", premiereDate: local);
        var unspecifiedId = AddItem("Unspecified", premiereDate: unspecified);

        using var context = CreateDbContext();
        var storedLocal = context.BaseItems.Single(e => e.Id.Equals(localId)).PremiereDate!.Value;
        var storedUnspecified = context.BaseItems.Single(e => e.Id.Equals(unspecifiedId)).PremiereDate!.Value;

        Assert.Equal(DateTimeKind.Utc, storedLocal.Kind);
        Assert.Equal(local.ToUniversalTime(), storedLocal);
        Assert.Equal(DateTimeKind.Utc, storedUnspecified.Kind);
        Assert.Equal(unspecified.ToUniversalTime(), storedUnspecified);
    }

    private Guid AddItem(string name, DateTime? premiereDate = null)
    {
        var id = Guid.NewGuid();
        using var context = CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity { Id = id, Name = name, Type = MovieType, PremiereDate = premiereDate });
        context.SaveChanges();
        return id;
    }
}
