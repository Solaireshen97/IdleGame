using Game.Server.Configuration;
using Game.Server.Controllers;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public partial class RoomServiceTests
{
    [Fact]
    public async Task QuickSkillCastDmlInvalidatesRoomProjectionOnlyOnSuccessfulOwnershipCheck()
    {
        await using var test = await RoomTestContext.CreateAsync();
        await test.AddOtherActiveCharacterAsync();
        var revision = new RoomProjectionRevision();
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create(),
            projectionRevision: revision);
        var before = revision.Value;
        var denied = await users.SetQuickSkillCastAsync(test.Token, 2, true);
        Assert.False(denied.Success);
        Assert.Equal(before, revision.Value);
        var applied = await users.SetQuickSkillCastAsync(test.Token, test.ActiveCharacter.Id, true);
        Assert.True(applied.Success);
        Assert.Equal(before + 1, revision.Value);
        Assert.True(test.ActiveCharacter.IsQuickSkillCastEnabled);
    }

    [Fact]
    public async Task CurrentUserSnapshotNamesTheCharacterOwningItsGold()
    {
        await using var test = await RoomTestContext.CreateAsync();
        test.ActiveCharacter.Gold = 10;
        var other = await test.AddCharacterAsync("Other owned character");
        other.Gold = 20;
        await test.Db.SaveChangesAsync();
        var users = new UserService(test.Db, ProgressionTestFactory.Create(), SkillTestFactory.Create());
        var first = await users.GetCurrentUserAsync(test.Token);
        Assert.Null(first.Error);
        Assert.Equal(test.ActiveCharacter.Id, first.Response!.ActiveCharacterId);
        Assert.Equal(10, first.Response.Gold);
        (await test.Db.Users.FindAsync(1))!.ActiveCharacterId = other.Id;
        await test.Db.SaveChangesAsync();
        var switched = await users.GetCurrentUserAsync(test.Token);
        Assert.Null(switched.Error);
        Assert.Equal(other.Id, switched.Response!.ActiveCharacterId);
        Assert.Equal(20, switched.Response.Gold);
    }

    [Fact]
    public async Task ContentControllerSharesAnonymousDefinitionsAndReturns304ForMatchingETag()
    {
        await using var test = await RoomTestContext.CreateAsync();
        var dungeon = new Dungeon
        {
            Code = "catalog-test", Name = "Test dungeon", RegionCode = "test-region", DungeonKind = "Dungeon",
            MonsterName = "Test monster", MonsterMaxHp = 100, MonsterElement = ElementType.Fire
        };
        var world = new WorldCatalog(Options.Create(new WorldOptions
        {
            Dungeons = [dungeon],
            Regions = [new RegionOptions
            {
                Code = "test-region", Name = "Test region", Description = "Fixed region",
                FeaturedElement = ElementType.Fire, FeaturedDungeonCode = dungeon.Code, FeaturedWeaponCode = "test-weapon"
            }]
        }));
        var weapons = new WeaponCatalog(Options.Create(new WeaponOptions
        {
            Items = [new() { Code = "test-weapon", Name = "Test weapon", Element = ElementType.Fire, MaxHp = 10 }],
            StarterPacks = new() { ["knight"] = ["test-weapon"] }
        }));
        test.Db.Dungeons.Add(dungeon);
        await test.Db.SaveChangesAsync();
        using var provider = new ServiceCollection().AddSingleton(test.Service).BuildServiceProvider();
        var store = new ContentCatalogStore(provider.GetRequiredService<IServiceScopeFactory>(), SkillTestFactory.Create(), world, weapons);
        var controller = new ContentController(store, test.Service)
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = "Bearer token";
        var first = Assert.IsType<ContentResult>(await controller.Get());
        Assert.Equal("public, max-age=60, must-revalidate", controller.Response.Headers.CacheControl.ToString());
        Assert.DoesNotContain("autoUnlocked", first.Content!);
        var etag = controller.Response.Headers.ETag.ToString();
        Assert.False(string.IsNullOrWhiteSpace(etag));
        controller.Request.Headers.Authorization = "Bearer other-token";
        controller.Request.Headers.IfNoneMatch = $"\"unrelated\", W/{etag}";
        var unchanged = Assert.IsType<StatusCodeResult>(await controller.Get());
        Assert.Equal(StatusCodes.Status304NotModified, unchanged.StatusCode);
        Assert.Equal(etag, controller.Response.Headers.ETag.ToString());
        Assert.Same(await store.GetAsync(), await store.GetAsync());
    }
}
