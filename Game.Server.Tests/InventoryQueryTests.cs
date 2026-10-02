using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Game.Server.Tests;

public sealed partial class InventoryQueryTests(ITestOutputHelper output)
{
    private static readonly Lazy<Content> Definitions = new(() => new Content());

    [Fact]
    public async Task OverviewCountsHoldingsOnceAndUnionsOverlappingProtection()
    {
        await using var test = await Context.CreateAsync();
        await SeedMixedAsync(test.Db);
        // Same-code instances can have different stored stats, including zero attack.
        var firstWeapon = await test.Db.CharacterWeapons.SingleAsync(item => item.Id == 1);
        firstWeapon.Attack = 0;
        firstWeapon.MaxHp = 137;
        var secondWeapon = await test.Db.CharacterWeapons.SingleAsync(item => item.Id == 2);
        secondWeapon.Attack = 71;
        secondWeapon.MaxHp = 919;
        await test.SaveAsync();
        await using var read = test.NewDb();
        var result = await test.Query(read).GetAsync(Context.Token, 1, new() { PageSize = 100 });
        Assert.Null(result.Error);
        var inventory = result.Response!;
        Assert.Equal((1, "角色甲", 101), (inventory.CharacterId, inventory.CharacterName, inventory.Gold));
        Assert.Equal((4L, 3L, 4L, 2L, 4L), (inventory.WeaponCount, inventory.SoulImprintCount,
            inventory.OwnedStackKinds, inventory.EquippedCount, inventory.ProtectedCount));
        Assert.Equal(11, inventory.FilteredRowCount);
        Assert.Equal(11, inventory.Items.Select(item => item.Key).Distinct().Count());
        var weapon = inventory.Items.Single(item => item.Key == "weapon:1");
        Assert.Equal((0, 137), (weapon.Attack, weapon.MaxHp));
        var secondEntry = inventory.Items.Single(item => item.Key == "weapon:2");
        Assert.Equal((71, 919), (secondEntry.Attack, secondEntry.MaxHp));
        Assert.All(inventory.Items.Where(item => item.AssetKind != InventoryKinds.Weapon), item =>
        {
            Assert.Null(item.Attack);
            Assert.Null(item.MaxHp);
        });
        Assert.True(weapon.IsEquipped);
        Assert.True(weapon.IsLocked);
        Assert.Equal(new[] { "第一套", "第二套" }, weapon.FormationReferences.Select(item => item.Name).Order(StringComparer.Ordinal));
        var sell = weapon.Actions.Single(action => action.Action == "sell");
        Assert.False(sell.Allowed);
        Assert.Contains("WeaponEquipped", sell.ReasonCodes);
        Assert.Contains("WeaponLocked", sell.ReasonCodes);
        Assert.Contains("FormationItemReferenced", sell.ReasonCodes);
        var potion = inventory.Items.Single(item => item.Code == "minor-healing-potion");
        Assert.Equal(12, potion.Quantity);
        Assert.Equal(2, potion.FormationReferences.Count);
        Assert.True(potion.IsEquipped);
        Assert.DoesNotContain(inventory.Items, item => item.Code == "whetstone-oil");
        Assert.Empty(inventory.Items.Single(item => item.Key == "weapon:4").FormationReferences);
        Assert.Empty(inventory.Items.Single(item => item.Key == "soul:2").FormationReferences);
        AssertCategory(inventory, InventoryCategories.Weapons, 4, 4);
        AssertCategory(inventory, InventoryCategories.SoulImprints, 2, 2);
        AssertCategory(inventory, InventoryCategories.Supplies, 1, 12);
        AssertCategory(inventory, InventoryCategories.Planting, 1, 20);
        AssertCategory(inventory, InventoryCategories.Upgrade, 1, 10);
        AssertCategory(inventory, InventoryCategories.Other, 2, 8);
        Assert.False(read.ChangeTracker.HasChanges());
        Assert.Equal(0, test.Counter.WritesAfterReset);
    }

    [Fact]
    public async Task ExplicitCharacterQueriesAreIsolatedAndDoNotSelectTheBrowsedCharacter()
    {
        await using var test = await Context.CreateAsync();
        await SeedMixedAsync(test.Db);
        await using var read = test.NewDb();
        var query = test.Query(read);
        var second = await query.GetAsync(Context.Token, 2, new());
        Assert.Null(second.Error);
        Assert.Equal((2, "角色乙", 222, 1L, 1L, 1L), (second.Response!.CharacterId,
            second.Response.CharacterName, second.Response.Gold, second.Response.WeaponCount,
            second.Response.SoulImprintCount, second.Response.OwnedStackKinds));
        Assert.All(second.Response.Items, item => Assert.Contains(item.Key, new[] { "weapon:5", "soul:4", "stack:peacebloom" }));
        Assert.Equal("NotOwner", (await query.GetAsync(Context.Token, 3, new())).Error);
        Assert.Equal("CharacterNotFound", (await query.GetAsync(Context.Token, 999, new())).Error);
        Assert.Equal("Unauthorized", (await query.GetAsync("invalid-token", 1, new())).Error);
        Assert.Equal("Unauthorized", (await query.GetAsync(null, 1, new())).Error);
        Assert.Equal("InventoryItemNotFound", (await query.DetailAsync(Context.Token, 1, InventoryKinds.Weapon, "5")).Error);
        Assert.Equal("NotOwner", (await query.DetailAsync(Context.Token, 3, InventoryKinds.Stack, "peacebloom")).Error);
        Assert.Equal("WeaponNotOwned", (await query.PreviewAsync(Context.Token, 1,
            new() { AssetKind = InventoryKinds.Weapon, Action = "sell", InstanceIds = [5] })).Error);
        Assert.Equal(0, test.Counter.WritesAfterReset);
        await using var verification = test.NewDb();
        Assert.Equal(1, (await verification.Users.AsNoTracking().SingleAsync(user => user.Id == 1)).ActiveCharacterId);
    }

    [Fact]
    public async Task UnknownDefinitionsAndCaseConflictsStayVisibleWithoutCombiningStocks()
    {
        await using var test = await Context.CreateAsync();
        var soul = Definitions.Value.Souls.Items.First();
        test.Db.AddRange(Stack("minor-healing-potion", 7), Stack("MINOR-HEALING-POTION", 11),
            Stack("peacebloom", 0), Stack("PEACEBLOOM", 2), Stack("retired-stack", 4),
            Soul(1, "retired-soul"), Soul(2, soul.Code));
        await test.SaveAsync();
        await using var read = test.NewDb();
        var result = await test.Query(read).GetAsync(Context.Token, 1, new() { PageSize = 100 });
        Assert.Null(result.Error);
        Assert.Equal((4L, 2L, 6), (result.Response!.OwnedStackKinds, result.Response.SoulImprintCount, result.Response.FilteredRowCount));
        var conflictRows = result.Response.Items.Where(item => item.HasCodeConflict).ToList();
        Assert.Equal(3, conflictRows.Count);
        Assert.Equal(7, conflictRows.Single(item => item.Code == "minor-healing-potion").Quantity);
        Assert.Equal(11, conflictRows.Single(item => item.Code == "MINOR-HEALING-POTION").Quantity);
        Assert.Equal(2, conflictRows.Single(item => item.Code == "PEACEBLOOM").Quantity);
        Assert.All(conflictRows, item => Assert.Empty(item.Usages));
        Assert.DoesNotContain(result.Response.Items, item => item.Key == "stack:peacebloom");
        var unknown = result.Response.Items.Single(item => item.Code == "retired-stack");
        Assert.False(unknown.IsDefinitionKnown);
        Assert.Equal("retired-stack", unknown.Name);
        Assert.Equal(InventoryCategories.Other, unknown.Category);
        var unknownSoul = result.Response.Items.Single(item => item.Key == "soul:1");
        Assert.False(unknownSoul.IsDefinitionKnown);
        Assert.Equal("retired-soul", unknownSoul.Name);
        Assert.False(unknownSoul.Actions.Single(action => action.Action == "dismantle").Allowed);
        Assert.Contains("UnknownSoulImprint", unknownSoul.Actions.Single(action => action.Action == "dismantle").ReasonCodes);
        var detail = await test.Query(read).DetailAsync(Context.Token, 1, InventoryKinds.SoulImprint, "1");
        Assert.Null(detail.Error);
        Assert.Equal("retired-soul", detail.Response!.SoulImprint!.Code);
        // Missing definitions elsewhere in the same inventory do not block another valid instance.
        var preview = await test.Query(read).PreviewAsync(Context.Token, 1,
            new() { AssetKind = InventoryKinds.SoulImprint, Action = "dismantle", InstanceIds = [2] });
        Assert.Null(preview.Error);
        Assert.True(preview.Response!.Allowed);
        Assert.Equal(0, test.Counter.WritesAfterReset);
    }

    [Fact]
    public async Task CategoryTotalsUseLongEvenWhenMultipleStacksExceedIntRange()
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Stack("peacebloom", int.MaxValue), Stack("silverleaf", int.MaxValue));
        await test.SaveAsync();
        await using var read = test.NewDb();
        var result = await test.Query(read).GetAsync(Context.Token, 1, new());
        Assert.Null(result.Error);
        AssertCategory(result.Response!, InventoryCategories.Planting, 2, 2L * int.MaxValue);
        Assert.All(result.Response!.Items, item => Assert.Equal(int.MaxValue, item.Quantity));
    }

    [Fact]
    public async Task FiltersAndStablePaginationDoNotChangeWholeCharacterSummary()
    {
        await using var test = await Context.CreateAsync();
        await SeedMixedAsync(test.Db);
        await using var read = test.NewDb();
        var query = test.Query(read);
        var available = await query.GetAsync(Context.Token, 1, new()
        {
            Category = InventoryCategories.Weapons, State = "available", Element = ElementType.Earth,
            Tier = 1, QualityRank = 0, Search = "GOLDTOOTH", PageSize = 1
        });
        Assert.Null(available.Error);
        Assert.Equal("weapon:4", Assert.Single(available.Response!.Items).Key);
        Assert.Equal((4L, 3L, 4L), (available.Response.WeaponCount, available.Response.SoulImprintCount, available.Response.OwnedStackKinds));
        var first = await query.GetAsync(Context.Token, 1, new() { Category = InventoryCategories.Weapons, PageSize = 2 });
        var second = await query.GetAsync(Context.Token, 1, new() { Category = InventoryCategories.Weapons, PageSize = 2, Page = 2 });
        Assert.Equal(new[] { "weapon:1", "weapon:2" }, first.Response!.Items.Select(item => item.Key));
        Assert.Equal(new[] { "weapon:3", "weapon:4" }, second.Response!.Items.Select(item => item.Key));
        Assert.Equal(4, second.Response.FilteredRowCount);
        var outOfRange = await query.GetAsync(Context.Token, 1, new() { Category = InventoryCategories.Weapons, PageSize = 2, Page = 999 });
        Assert.Equal(2, outOfRange.Response!.Page);
        Assert.Equal(second.Response.Items.Select(item => item.Key), outOfRange.Response.Items.Select(item => item.Key));
        var referenced = await query.GetAsync(Context.Token, 1, new() { State = "referenced" });
        Assert.Equal(new[] { "soul:1", "stack:minor-healing-potion", "weapon:1", "weapon:3" },
            referenced.Response!.Items.Select(item => item.Key).Order());
        var nameSearch = await query.GetAsync(Context.Token, 1, new() { Search = "  宁神花  " });
        Assert.Equal("peacebloom", Assert.Single(nameSearch.Response!.Items).Code);
        var quantities = await query.GetAsync(Context.Token, 1, new() { Sort = "quantity", PageSize = 2 });
        Assert.Equal(new[] { "peacebloom", "minor-healing-potion" }, quantities.Response!.Items.Select(item => item.Code));
        AssertCategory(first.Response, InventoryCategories.Planting, 1, 20);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("state")]
    [InlineData("sort")]
    [InlineData("page")]
    [InlineData("pageSize")]
    [InlineData("search")]
    [InlineData("tier")]
    [InlineData("quality")]
    [InlineData("element")]
    public async Task InvalidQueryIsRejectedBeforeReadingHoldings(string field)
    {
        await using var test = await Context.CreateAsync();
        var request = new InventoryQueryRequest();
        switch (field)
        {
            case "category": request.Category = "missing"; break;
            case "state": request.State = "missing"; break;
            case "sort": request.Sort = "missing"; break;
            case "page": request.Page = 0; break;
            case "pageSize": request.PageSize = 101; break;
            case "search": request.Search = new string('x', 101); break;
            case "tier": request.Tier = 0; break;
            case "quality": request.QualityRank = 4; break;
            case "element": request.Element = (ElementType)999; break;
        }
        await using var read = test.NewDb();
        var result = await test.Query(read).GetAsync(Context.Token, 1, request);
        Assert.Equal("InvalidInventoryQuery", result.Error);
        Assert.Null(result.Response);
        Assert.Equal(0, test.Counter.Selects);
        Assert.Equal(0, test.Counter.WritesAfterReset);
    }

    [Fact]
    public async Task ReadQueriesDoNotSettleDueProductionReserveIngredientsOrCreateGardenPlots()
    {
        await using var test = await Context.CreateAsync();
        var due = DateTime.UtcNow.AddHours(-1);
        test.Db.AddRange(Weapon(1), Stack("minor-healing-potion", 15), Stack("peacebloom", 100));
        test.Db.ProductionTasks.Add(new ProductionTask
        {
            Id = 1, UserId = 1, CharacterId = 1, RecipeCode = "minor-healing-potion", OutputCode = "minor-healing-potion",
            OutputQuantity = 1, IngredientsJson = "[{\"Code\":\"peacebloom\",\"Quantity\":2}]", CycleSeconds = 10,
            StartedAtUtc = due.AddMinutes(-10), NextCycleAtUtc = due, EndsAtUtc = due.AddHours(2),
            CompletedCycles = 20, TotalQuantity = 20, TargetCycles = 40, Version = 9
        });
        await test.SaveAsync();
        await using var read = test.NewDb();
        var query = test.Query(read);
        var inventory = await query.GetAsync(Context.Token, 1, new());
        Assert.Null(inventory.Error);
        Assert.Equal(15, inventory.Response!.Items.Single(item => item.Code == "minor-healing-potion").Quantity);
        Assert.Equal(100, inventory.Response.Items.Single(item => item.Code == "peacebloom").Quantity);
        Assert.Null((await query.DetailAsync(Context.Token, 1, InventoryKinds.Weapon, "1")).Error);
        Assert.True((await query.PreviewAsync(Context.Token, 1,
            new() { AssetKind = InventoryKinds.Weapon, Action = "sell", InstanceIds = [1] })).Response!.Allowed);
        Assert.Equal(0, test.Counter.WritesAfterReset);
        Assert.DoesNotContain(read.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        await using var verify = test.NewDb();
        var task = await verify.ProductionTasks.AsNoTracking().SingleAsync();
        Assert.Equal(("Running", 20, 20, 9, due), (task.Status, task.CompletedCycles, task.TotalQuantity, task.Version, task.NextCycleAtUtc));
        Assert.Empty(await verify.CharacterGardenPlots.ToListAsync());
        Assert.Empty(await verify.LogisticsRequests.ToListAsync());
        Assert.Equal(101, (await verify.Characters.AsNoTracking().SingleAsync(character => character.Id == 1)).Gold);
        Assert.Equal(15, (await verify.CharacterItemStacks.AsNoTracking().SingleAsync(stack => stack.ItemCode == "minor-healing-potion")).Quantity);
        Assert.Equal(100, (await verify.CharacterItemStacks.AsNoTracking().SingleAsync(stack => stack.ItemCode == "peacebloom")).Quantity);
    }

    [Fact]
    public async Task MaturePlotsAndPendingBattleRewardsRemainSeparateFromCurrentHoldings()
    {
        await using var test = await Context.CreateAsync();
        var now = DateTime.UtcNow;
        test.Db.AddRange(Stack("minor-healing-potion", 15), Stack("peacebloom", 9),
            new CharacterGardenPlot { CharacterId = 1, PlotIndex = 0, PlantCode = "peacebloom", MaterialCode = "peacebloom", HarvestQuantity = 40, MaturesAtUtc = now.AddMinutes(-1) },
            new CharacterGardenPlot { CharacterId = 1, PlotIndex = 1, PlantCode = "peacebloom", MaterialCode = "peacebloom", HarvestQuantity = 40, MaturesAtUtc = now.AddHours(1) },
            new CharacterGardenPlot { CharacterId = 2, PlotIndex = 0, PlantCode = "peacebloom", MaterialCode = "peacebloom", HarvestQuantity = 40, MaturesAtUtc = now.AddMinutes(-1) });
        test.Db.RewardRuns.AddRange(new RewardRun { RoomId = 10, Sequence = 1 }, new RewardRun { RoomId = 10, Sequence = 2 },
            new RewardRun { RoomId = 10, Sequence = 3, Status = "Settled", SettledAtUtc = now }, new RewardRun { RoomId = 10, Sequence = 4 });
        var soulCode = Definitions.Value.Souls.Items.First().Code;
        test.Db.RewardEntries.AddRange(Reward(1, "Gold", "", 30), Reward(1, "Consumable", "minor-healing-potion", 3),
            Reward(1, "Material", "peacebloom", 4), Reward(1, "Weapon", "goldtooth-pickaxe", 2), Reward(1, "SoulImprint", soulCode, 1),
            Reward(1, "Experience", "", 100), Reward(2, "Gold", "", 5), Reward(2, "Consumable", "minor-healing-potion", 2),
            Reward(3, "Gold", "", 999), Reward(3, "Consumable", "minor-healing-potion", 999),
            Reward(4, "Gold", "", 88, 2), Reward(4, "Consumable", "minor-healing-potion", 55, 2),
            Reward(5, "Consumable", "minor-healing-potion", 7));
        await test.SaveAsync();
        await using var read = test.NewDb();
        var result = await test.Query(read).GetAsync(Context.Token, 1, new());
        Assert.Null(result.Error);
        var inventory = result.Response!;
        Assert.Equal((101, 0L, 0L, 2L, 2), (inventory.Gold, inventory.WeaponCount, inventory.SoulImprintCount, inventory.OwnedStackKinds, inventory.FilteredRowCount));
        Assert.Equal((1, 2, 35L), (inventory.Pending.MaturePlotCount, inventory.Pending.PendingBattleCount, inventory.Pending.PendingGold));
        Assert.Equal(4, inventory.Pending.BattleRewards.Count);
        Assert.Equal(5, inventory.Pending.BattleRewards.Single(item => item.Code == "minor-healing-potion").Quantity);
        Assert.Equal(4, inventory.Pending.BattleRewards.Single(item => item.Code == "peacebloom").Quantity);
        Assert.Equal(2, inventory.Pending.BattleRewards.Single(item => item.Kind == "Weapon").Quantity);
        Assert.DoesNotContain(inventory.Pending.BattleRewards, item => item.Kind is "Gold" or "Experience");
        Assert.Equal(15, inventory.Items.Single(item => item.Code == "minor-healing-potion").Quantity);
        Assert.Equal(9, inventory.Items.Single(item => item.Code == "peacebloom").Quantity);
        Assert.Equal(0, test.Counter.WritesAfterReset);
        Assert.Equal(3, await read.CharacterGardenPlots.CountAsync());
        Assert.Equal(13, await read.RewardEntries.CountAsync());
    }

    [Fact]
    public async Task WeaponDetailsPreserveSavedStatsAndExcludeProtectedGrowthMaterials()
    {
        await using var test = await Context.CreateAsync();
        var target = Weapon(1); target.Attack = 987; target.MaxHp = 432; target.Name = "历史属性武器"; target.EquippedSlotIndex = 1; target.IsLocked = true;
        var locked = Weapon(3); locked.IsLocked = true;
        var equipped = Weapon(4); equipped.EquippedSlotIndex = 2;
        var starter = Weapon(6); starter.Origin = WeaponOrigin.Starter;
        var different = Weapon(7); different.WeaponCode = "different-legacy-weapon";
        test.Db.AddRange(target, Weapon(2), locked, equipped, Weapon(5), starter, different, Weapon(8, 2),
            Stack("weapon-fragment-t1", 30), Stack("weapon-breakthrough-fragment-t1", 250), Stack("weapon-breakthrough-stone-t1", 2));
        test.Db.CharacterBattleFormations.Add(Formation(1, "保护素材", [5]));
        await test.SaveAsync();
        await using var read = test.NewDb();
        var detail = await test.Query(read).DetailAsync(Context.Token, 1, InventoryKinds.Weapon, "weapon:1");
        Assert.Null(detail.Error);
        Assert.Equal((987, 432, "历史属性武器"), (detail.Response!.Weapon!.Attack, detail.Response.Weapon.MaxHp, detail.Response.Weapon.Name));
        Assert.Equal(2, Assert.Single(detail.Response.QualityMaterials).Id);
        Assert.Equal(30, Assert.Single(detail.Response.Fragments).Quantity);
        var breakthrough = Assert.Single(detail.Response.BreakthroughMaterials);
        Assert.Equal((250, 2, 2), (breakthrough.FragmentQuantity, breakthrough.StoneQuantity, breakthrough.CanCraftQuantity));
        Assert.True(detail.Response.Entry.IsEquipped);
        Assert.True(detail.Response.Entry.IsLocked);
        Assert.NotEmpty(detail.Response.Weapon.Skills);
        Assert.Equal(0, test.Counter.WritesAfterReset);
        // Admission changes the policy for every candidate without changing ownership.
        test.Db.RoomSlots.Add(new RoomSlot { RoomId = 90, SlotIndex = 1, UserId = 1, CharacterId = 1 });
        await test.SaveAsync();
        await using var admitted = test.NewDb();
        var inRoomDetail = await test.Query(admitted).DetailAsync(Context.Token, 1, InventoryKinds.Weapon, "1");
        Assert.Null(inRoomDetail.Error);
        Assert.Empty(inRoomDetail.Response!.QualityMaterials);
        var overview = await test.Query(admitted).GetAsync(Context.Token, 1, new());
        Assert.True(overview.Response!.IsLoadoutLocked);
        Assert.Contains("LoadoutLocked", overview.Response.Items.Single(item => item.Key == "weapon:2").Actions.Single(action => action.Action == "sell").ReasonCodes);
    }

    [Fact]
    public async Task QueryCountAndPagedPayloadStayBoundedForTenToTenThousandInstances()
    {
        var measurements = new List<(int Size, int Selects, int Bytes, double Milliseconds)>();
        foreach (var size in new[] { 10, 1000, 10000 })
        {
            await using var test = await Context.CreateAsync();
            test.Db.CharacterWeapons.AddRange(Enumerable.Range(1, size).Select(id => Weapon(id)));
            await test.SaveAsync();
            await using var read = test.NewDb();
            var query = test.Query(read);
            var timer = Stopwatch.StartNew();
            var result = await query.GetAsync(Context.Token, 1, new() { Category = InventoryCategories.Weapons, PageSize = 10 });
            timer.Stop();
            Assert.Null(result.Error);
            Assert.Equal(size, result.Response!.WeaponCount);
            Assert.Equal(size, result.Response.FilteredRowCount);
            Assert.Equal(10, result.Response.Items.Count);
            Assert.Equal(0, test.Counter.WritesAfterReset);
            Assert.InRange(test.Counter.Selects, 1, 20);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Response);
            Assert.InRange(bytes.Length, 1, 64000);
            using var json = JsonDocument.Parse(bytes);
            Assert.All(json.RootElement.GetProperty("Items").EnumerateArray(), item =>
            {
                Assert.False(item.TryGetProperty("Skills", out _));
                Assert.False(item.TryGetProperty("LockedSkills", out _));
            });
            measurements.Add((size, test.Counter.Selects, bytes.Length, timer.Elapsed.TotalMilliseconds));
            output.WriteLine($"Inventory {size:N0} instances: {test.Counter.Selects} SELECTs, 0 writes, {bytes.Length:N0} JSON bytes, {timer.Elapsed.TotalMilliseconds:0.0} ms (fresh SQLite context).");
        }
        Assert.Single(measurements.Select(item => item.Selects).Distinct());
        Assert.InRange(Math.Abs(measurements[2].Bytes - measurements[1].Bytes), 0, 512);
    }

    private static void AssertCategory(InventoryOverviewResponse response, string category, long rows, long quantity)
    {
        var summary = response.Categories.Single(item => item.Category == category);
        Assert.Equal((rows, quantity), (summary.RowCount, summary.Quantity));
    }

    private static CharacterItemStack Stack(string code, int quantity, int characterId = 1) => new()
        { CharacterId = characterId, ItemCode = code, Quantity = quantity, Version = 7 };

    private static CharacterWeapon Weapon(int id, int characterId = 1) => new()
    {
        Id = id, CharacterId = characterId, WeaponCode = "goldtooth-pickaxe", Name = "统一测试武器", Element = ElementType.Earth,
        Attack = 12, MaxHp = 80, ItemLevel = 5, SellGold = 10, DismantleFragments = 5, Origin = WeaponOrigin.Drop, Version = 3,
        Skills = [new CharacterWeaponSkill { SlotIndex = 1, SkillCode = "weapon-might", BaseLevel = 1, Level = 1, SpentFragments = 0 }]
    };

    private static CharacterSoulImprint Soul(int id, string code, int characterId = 1) => new()
        { Id = id, CharacterId = characterId, SoulImprintCode = code, Version = 4 };

    private static CharacterBattleFormation Formation(int id, string name, int[] weapons, int? soul = null, bool deleted = false) => new()
    {
        Id = id, CharacterId = 1, GroupElement = ElementType.Earth, Position = id, Name = name, SoulImprintId = soul, IsDeleted = deleted,
        Weapons = weapons.Select((weapon, index) => new FormationWeaponSlot { SlotIndex = index + 1, WeaponId = weapon }).ToList(),
        Consumables = [new FormationConsumableSlot { SlotIndex = 1, ItemCode = "minor-healing-potion" }]
    };

    private static RewardEntry Reward(int sequence, string kind, string code, int quantity, int characterId = 1) => new()
        { RoomId = 10, Sequence = sequence, EventKey = "query-test", UserId = 1, CharacterId = characterId, Kind = kind, Code = code, Quantity = quantity };

    private static async Task SeedMixedAsync(GameDbContext db)
    {
        var equipped = Weapon(1); equipped.EquippedSlotIndex = 1; equipped.IsLocked = true;
        var locked = Weapon(2); locked.IsLocked = true;
        var knownCode = Definitions.Value.Souls.Items.First().Code;
        var soul = Soul(1, knownCode); soul.EquippedSlotIndex = 1; soul.IsLocked = true;
        db.AddRange(equipped, locked, Weapon(3), Weapon(4), Weapon(5, 2), soul, Soul(2, knownCode),
            Soul(3, "retired-soul"), Soul(4, knownCode, 2), Stack("weapon-fragment-t1", 10),
            Stack("minor-healing-potion", 12), Stack("peacebloom", 20), Stack("whetstone-oil", 0), Stack("retired-stack", 7),
            Stack("peacebloom", 8, 2), new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 1, ItemCode = "minor-healing-potion" },
            new CharacterConsumableSlot { CharacterId = 1, SlotIndex = 2, ItemCode = "whetstone-oil" },
            Formation(1, "第一套", [1, 3], 1), Formation(2, "第二套", [1, 3], 1), Formation(3, "已删除", [4], 2, true));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed class Context : IAsyncDisposable
    {
        public const string Token = "inventory-query-token";
        private readonly string _path = Path.Combine(Path.GetTempPath(), "idle-inventory-query-" + Guid.NewGuid().ToString("N") + ".db");
        private DbContextOptions<GameDbContext> Options => new DbContextOptionsBuilder<GameDbContext>()
            .UseSqlite($"Data Source={_path};Pooling=False").AddInterceptors(Counter).Options;
        public CommandCounter Counter { get; } = new();
        public GameDbContext Db { get; private set; } = null!;
        public GameDbContext NewDb() { Counter.Reset(); return new(Options); }

        public static async Task<Context> CreateAsync()
        {
            var test = new Context();
            test.Db = new(test.Options);
            await test.Db.Database.EnsureCreatedAsync();
            test.Db.AddRange(new User { Id = 1, UserName = "owner", ActiveCharacterId = 1 }, new User { Id = 2, UserName = "stranger", ActiveCharacterId = 3 },
                new UserLoginSession { UserId = 1, Token = Token, CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddDays(1) },
                new Character { Id = 1, UserId = 1, Name = "角色甲", Gold = 101, Attack = 10, Hp = 50, MaxHp = 100, Version = 8 },
                new Character { Id = 2, UserId = 1, Name = "角色乙", Gold = 222, Attack = 11, Hp = 60, MaxHp = 100, Version = 9 },
                new Character { Id = 3, UserId = 2, Name = "外部角色", Gold = 333, Attack = 12, Hp = 70, MaxHp = 100 });
            await test.SaveAsync();
            return test;
        }

        public InventoryQuery Query(GameDbContext db)
        {
            var content = Definitions.Value;
            return new(db, new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()),
                content.Catalog, content.Weapons, content.Souls, content.Breakthroughs);
        }

        public async Task SaveAsync()
        {
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            Counter.Reset();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
        }
    }

    private sealed class Content
    {
        private readonly IConfiguration _configuration = new ConfigurationBuilder()
            .AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        public WeaponCatalog Weapons { get; }
        public SoulImprintCatalog Souls { get; }
        public WeaponBreakthroughCatalog Breakthroughs { get; }
        public ItemDefinitionCatalog Catalog { get; }

        public Content()
        {
            var materials = new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName));
            Weapons = new(Bind<WeaponOptions>(WeaponOptions.SectionName));
            Souls = new(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName));
            Breakthroughs = new(Bind<WeaponBreakthroughOptions>(WeaponBreakthroughOptions.SectionName));
            var consumables = new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName), Weapons);
            var world = WorldCatalog.LoadDefault();
            Catalog = new(materials, consumables, new(Bind<PlantingOptions>(PlantingOptions.SectionName)), Weapons, Breakthroughs, Souls,
                new(Bind<DungeonExchangeOptions>(DungeonExchangeOptions.SectionName), materials, Weapons, Souls),
                new(Bind<ProductionOptions>(ProductionOptions.SectionName), world, materials, consumables),
                new(Bind<GatheringOptions>(GatheringOptions.SectionName), world, materials));
        }

        private IOptions<T> Bind<T>(string section) where T : class, new() =>
            Microsoft.Extensions.Options.Options.Create(_configuration.GetSection(section).Get<T>()!);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Selects { get; private set; }
        public int WritesAfterReset { get; private set; }
        public void Reset() { Selects = 0; WritesAfterReset = 0; }
        private void Record(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Selects++;
            else if (sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase)) WritesAfterReset++;
        }
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result) { Record(command); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken ct = default) { Record(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData data,
            InterceptionResult<int> result) { Record(command); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken ct = default) { Record(command); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData data,
            InterceptionResult<object> result) { Record(command); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<object> result, CancellationToken ct = default) { Record(command); return ValueTask.FromResult(result); }
    }
}
