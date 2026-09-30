using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Shop;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public partial class ShopServiceTests
{
    [Fact]
    public async Task ExchangeRetryAfterLostResponseReturnsReceiptWithoutAnotherCharge()
    {
        await using var test = await ShopTestContext.CreateAsync();
        test.Db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = "kobold-mine-token", Quantity = 6 });
        await test.Db.SaveChangesAsync();
        var request = new ExchangeDungeonWeaponRequest { CharacterId = 1, OfferCode = "kobold-fire", RequestId = Guid.NewGuid().ToString("D") };
        var first = await test.Service.ExchangeAsync(test.Token, request);
        Assert.Null(first.Error);
        await using var fresh = test.CreateDbContext();
        var service = ShopTestContext.CreateService(fresh);
        request.RequestId = Guid.Parse(request.RequestId).ToString("N");
        var retry = await service.ExchangeAsync(test.Token, request);
        Assert.Null(retry.Error);
        Assert.Equal(first.Response!.RewardDisplayName, retry.Response!.RewardDisplayName);
        Assert.Equal(first.Response.WeaponDisplayName, retry.Response.WeaponDisplayName);
        Assert.Single(await fresh.CharacterWeapons.ToListAsync());
        Assert.Equal(0, (await fresh.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.NotNull((await fresh.LogisticsRequests.SingleAsync()).ResultJson);
        request.OfferCode = "kobold-fragments";
        Assert.Equal("RequestIdReused", (await service.ExchangeAsync(test.Token, request)).Error);
        Assert.Equal("InvalidRequestId", (await service.ExchangeAsync(test.Token,
            new() { CharacterId = 1, OfferCode = "kobold-fire" })).Error);
    }

    [Fact]
    public async Task ConcurrentExchangeRetryCreatesOnlyOneReward()
    {
        await using var test = await ShopTestContext.CreateAsync();
        test.Db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = "kobold-mine-token", Quantity = 60 });
        await test.Db.SaveChangesAsync();
        var request = new ExchangeDungeonWeaponRequest { CharacterId = 1, OfferCode = "kobold-fire", RequestId = Guid.NewGuid().ToString("N") };
        await using var first = test.CreateDbContext();
        await using var second = test.CreateDbContext();
        var results = await Task.WhenAll(ShopTestContext.CreateService(first).ExchangeAsync(test.Token, request),
            ShopTestContext.CreateService(second).ExchangeAsync(test.Token, request));
        Assert.Contains(results, item => item.Error is null);
        Assert.All(results, item => Assert.True(item.Error is null or "ConcurrencyConflict"));
        await using var verify = test.CreateDbContext();
        Assert.Null((await ShopTestContext.CreateService(verify).ExchangeAsync(test.Token, request)).Error);
        Assert.Single(await verify.CharacterWeapons.ToListAsync());
        Assert.Single(await verify.LogisticsRequests.ToListAsync());
        Assert.Equal(54, (await verify.CharacterItemStacks.SingleAsync()).Quantity);
    }

    [Fact]
    public async Task ExchangeConflictRollsBackReceiptRewardAndCurrencyTogether()
    {
        await using var test = await ShopTestContext.CreateAsync();
        var stack = new CharacterItemStack { CharacterId = 1, ItemCode = "kobold-mine-token", Quantity = 60 };
        test.Db.CharacterItemStacks.Add(stack);
        await test.Db.SaveChangesAsync();
        await using var stale = test.CreateDbContext();
        await stale.CharacterItemStacks.LoadAsync();
        stack.Quantity = 59;
        stack.Version++;
        await test.Db.SaveChangesAsync();
        var request = new ExchangeDungeonWeaponRequest { CharacterId = 1, OfferCode = "kobold-fire", RequestId = Guid.NewGuid().ToString("N") };
        Assert.Equal("ConcurrencyConflict", (await ShopTestContext.CreateService(stale).ExchangeAsync(test.Token, request)).Error);
        Assert.Empty(stale.ChangeTracker.Entries());
        await using var verify = test.CreateDbContext();
        Assert.Empty(await verify.LogisticsRequests.ToListAsync());
        Assert.Empty(await verify.CharacterWeapons.ToListAsync());
        Assert.Equal(59, (await verify.CharacterItemStacks.SingleAsync()).Quantity);
    }
}

public partial class WeaponServiceTests
{
    [Fact]
    public async Task EnhancementRetryUsesSameReceiptAfterInventoryHasChanged()
    {
        await using var test = await WeaponTestContext.CreateAsync(CreateSkillCatalog());
        var weapon = test.Weapons.Single(item => item.EquippedSlotIndex == 1);
        weapon.ItemLevel = 10;
        weapon.Skills.Single().BaseLevel = weapon.Skills.Single().Level = 1;
        test.Db.CharacterItemStacks.Add(new() { CharacterId = 1, ItemCode = WeaponRules.FragmentCode(1), Quantity = 100 });
        await test.Db.SaveChangesAsync();
        var id = Guid.NewGuid().ToString("D");
        var first = await test.Service.EnhanceSkillAsync(test.Token, 1, weapon.Id, 1, id);
        Assert.Null(first.Error);
        var quantity = (await test.Db.CharacterItemStacks.SingleAsync()).Quantity;
        await using var fresh = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite(test.Db.Database.GetConnectionString()!).Options);
        var skills = SkillTestFactory.Create();
        var service = new WeaponService(fresh, new UserService(fresh, ProgressionTestFactory.Create(), skills), skills, CreateSkillCatalog());
        Assert.Null((await service.EnhanceSkillAsync(test.Token, 1, weapon.Id, 1, Guid.Parse(id).ToString("N"))).Error);
        Assert.Equal(1, (await fresh.CharacterWeaponSkills.SingleAsync(item => item.WeaponId == weapon.Id)).EnhancementLevel);
        Assert.Equal(quantity, (await fresh.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Single(await fresh.LogisticsRequests.ToListAsync());
        Assert.Equal("RequestIdReused", (await service.EnhanceSkillAsync(test.Token, 1, weapon.Id, 2, id)).Error);
        Assert.Equal("InvalidRequestId", (await service.EnhanceSkillAsync(test.Token, 1, weapon.Id, 1)).Error);
        Assert.Null((await service.EnhanceSkillAsync(test.Token, 1, weapon.Id, 1, Guid.NewGuid().ToString("N"))).Error);
        Assert.Equal(2, (await fresh.CharacterWeaponSkills.SingleAsync(item => item.WeaponId == weapon.Id)).EnhancementLevel);
    }
}
