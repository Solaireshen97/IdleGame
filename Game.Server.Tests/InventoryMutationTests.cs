using System.Text.Json;
using Game.Server.Controllers;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Inventory;
using Game.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class InventoryQueryTests
{
    [Theory]
    [InlineData(InventoryKinds.Weapon, false)]
    [InlineData(InventoryKinds.Weapon, true)]
    [InlineData(InventoryKinds.SoulImprint, false)]
    [InlineData(InventoryKinds.SoulImprint, true)]
    public async Task DismantleRejectsNoncanonicalReceivingStocksWithoutConsumingInstances(string assetKind, bool canonicalAlsoExists)
    {
        await using var test = await Context.CreateAsync();
        if (assetKind == InventoryKinds.Weapon)
            test.Db.AddRange(Weapon(1), Weapon(2));
        else
        {
            var definition = Definitions.Value.Souls.Items.First();
            test.Db.AddRange(Soul(1, definition.Code), Soul(2, definition.Code));
        }
        await test.SaveAsync();
        var selection = new InventoryActionPreviewRequest
            { AssetKind = assetKind, Action = "dismantle", InstanceIds = [1, 2] };
        string rewardCode;
        await using (var read = test.NewDb())
        {
            var result = await test.Query(read).PreviewAsync(Context.Token, 1, selection);
            Assert.Null(result.Error);
            Assert.True(result.Response!.Allowed);
            rewardCode = Assert.Single(result.Response.Rewards, reward => reward.Kind == "Material").Code;
        }
        var legacyCode = rewardCode.ToUpperInvariant();
        Assert.NotEqual(rewardCode, legacyCode);
        test.Db.Add(Stack(legacyCode, 0));
        if (canonicalAlsoExists) test.Db.Add(Stack(rewardCode, 7));
        await test.SaveAsync();
        InventoryActionPreviewResponse preview;
        await using (var read = test.NewDb())
        {
            var result = await test.Query(read).PreviewAsync(Context.Token, 1, selection);
            Assert.Null(result.Error);
            Assert.True(result.Response!.Allowed);
            preview = result.Response;
            Assert.Equal(rewardCode, Assert.Single(preview.Rewards, reward => reward.Kind == "Material").Code);
        }
        string before;
        await using (var verify = test.NewDb()) before = await MutationSnapshotAsync(verify);
        var requestId = Guid.NewGuid().ToString("N");
        await using (var write = test.NewDb())
        {
            if (assetKind == InventoryKinds.Weapon)
            {
                var result = await WeaponService(write).DismantleAsync(Context.Token, 1, new()
                {
                    WeaponIds = [1, 2], RequestId = requestId,
                    ExpectedVersions = preview.ExpectedVersions, OutcomeFingerprint = preview.OutcomeFingerprint
                });
                Assert.Equal("InventoryCodeConflict", result.Error);
                Assert.Null(result.Response);
            }
            else
            {
                var result = await SoulService(write).DismantleAsync(Context.Token, 1, new()
                {
                    SoulImprintIds = [1, 2], RequestId = requestId,
                    ExpectedVersions = preview.ExpectedVersions, OutcomeFingerprint = preview.OutcomeFingerprint
                });
                Assert.Equal("InventoryCodeConflict", result.Error);
                Assert.Null(result.Response);
            }
        }
        await using var after = test.NewDb();
        Assert.Equal(before, await MutationSnapshotAsync(after));
        Assert.Equal(2, assetKind == InventoryKinds.Weapon
            ? await after.CharacterWeapons.CountAsync() : await after.CharacterSoulImprints.CountAsync());
        Assert.Equal(canonicalAlsoExists ? 2 : 1, await after.CharacterItemStacks.CountAsync());
        Assert.Equal(0, (await after.CharacterItemStacks.SingleAsync(item => item.ItemCode == legacyCode)).Quantity);
        Assert.Equal(canonicalAlsoExists, await after.CharacterItemStacks.AnyAsync(item => item.ItemCode == rewardCode));
        if (canonicalAlsoExists)
            Assert.Equal(7, (await after.CharacterItemStacks.SingleAsync(item => item.ItemCode == rewardCode)).Quantity);
        Assert.Empty(await after.LogisticsRequests.ToListAsync());
    }

    [Fact]
    public async Task RemovedSkillCannotSpendFragmentsWhileAnotherKnownSlotRemainsEnhanceable()
    {
        await using var test = await Context.CreateAsync();
        var unknownOnly = Weapon(1);
        unknownOnly.Skills[0].SkillCode = "removed-weapon-skill";
        var mixed = Weapon(2);
        mixed.Skills[0].SkillCode = "removed-weapon-skill";
        mixed.Skills.Add(new CharacterWeaponSkill
        {
            SlotIndex = 2, SkillCode = "weapon-attack", BaseLevel = 1, Level = 1, SpentFragments = 0
        });
        test.Db.AddRange(unknownOnly, mixed, Stack("weapon-fragment-t1", 10));
        await test.SaveAsync();
        await using (var read = test.NewDb())
        {
            var inventory = await test.Query(read).GetAsync(Context.Token, 1, new());
            Assert.Null(inventory.Error);
            var unavailable = inventory.Response!.Items.Single(item => item.Key == "weapon:1").Actions.Single(action => action.Action == "enhance");
            Assert.False(unavailable.Allowed);
            Assert.Contains("UnknownWeaponSkill", unavailable.ReasonCodes);
            Assert.True(inventory.Response.Items.Single(item => item.Key == "weapon:2").Actions.Single(action => action.Action == "enhance").Allowed);
            var detail = await test.Query(read).DetailAsync(Context.Token, 1, InventoryKinds.Weapon, "2");
            Assert.Null(detail.Error);
            Assert.Null(detail.Response!.Weapon!.Skills.Single(skill => skill.SlotIndex == 1).NextEnhancementCost);
            Assert.Equal(2, detail.Response.Weapon.Skills.Single(skill => skill.SlotIndex == 2).NextEnhancementCost);
        }
        string before;
        await using (var verify = test.NewDb()) before = await MutationSnapshotAsync(verify);
        foreach (var id in new[] { 1, 2 })
        {
            await using var write = test.NewDb();
            var rejected = await WeaponService(write).EnhanceSkillAsync(Context.Token, 1, id, 1, Guid.NewGuid().ToString("N"));
            Assert.Equal("UnknownWeaponSkill", rejected.Error);
            Assert.Null(rejected.Response);
        }
        await using (var verify = test.NewDb()) Assert.Equal(before, await MutationSnapshotAsync(verify));
        await using (var write = test.NewDb())
        {
            var allowed = await WeaponService(write).EnhanceSkillAsync(Context.Token, 1, 2, 2, Guid.NewGuid().ToString("N"));
            Assert.Null(allowed.Error);
            var skills = allowed.Response!.Weapons.Single(item => item.Id == 2).Skills;
            Assert.Equal(1, skills.Single(skill => skill.SlotIndex == 1).Level);
            Assert.Equal(2, skills.Single(skill => skill.SlotIndex == 2).Level);
        }
        await using var after = test.NewDb();
        Assert.Equal(8, (await after.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Single(await after.LogisticsRequests.ToListAsync());
    }

    [Theory]
    [InlineData("input")]
    [InlineData("output")]
    public async Task BreakthroughCraftRejectsCaseConflictsOnEitherSideWithoutChangingStocks(string conflict)
    {
        await using var test = await Context.CreateAsync();
        const string fragments = "weapon-breakthrough-fragment-t1";
        const string stones = "weapon-breakthrough-stone-t1";
        test.Db.AddRange(Stack(fragments, 100), Stack(stones, 0),
            Stack((conflict == "input" ? fragments : stones).ToUpperInvariant(), 0));
        await test.SaveAsync();
        await using (var read = test.NewDb())
        {
            var detail = await test.Query(read).DetailAsync(Context.Token, 1, InventoryKinds.Stack, fragments);
            Assert.Null(detail.Error);
            var craft = detail.Response!.Entry.Actions.Single(action => action.Action == "craft");
            Assert.False(craft.Allowed);
            Assert.Contains("InventoryCodeConflict", craft.ReasonCodes);
        }
        string before;
        await using (var verify = test.NewDb()) before = await MutationSnapshotAsync(verify);
        await using (var write = test.NewDb())
        {
            var result = await WeaponService(write).CraftBreakthroughStoneAsync(Context.Token, 1,
                new() { RequestId = Guid.NewGuid().ToString("N"), Tier = 1, Quantity = 1 });
            Assert.Equal("InventoryCodeConflict", result.Error);
            Assert.Null(result.Response);
        }
        await using var after = test.NewDb();
        Assert.Equal(before, await MutationSnapshotAsync(after));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task BreakthroughCraftDoesNotCreateCanonicalOutputBesideASingleLegacyUppercaseRow(int legacyQuantity)
    {
        await using var test = await Context.CreateAsync();
        const string fragments = "weapon-breakthrough-fragment-t1";
        const string stones = "weapon-breakthrough-stone-t1";
        var legacyCode = stones.ToUpperInvariant();
        test.Db.AddRange(Stack(fragments, 100), Stack(legacyCode, legacyQuantity));
        await test.SaveAsync();
        await using (var read = test.NewDb())
        {
            Assert.False(await read.CharacterItemStacks.AnyAsync(item => item.ItemCode == stones));
            var detail = await test.Query(read).DetailAsync(Context.Token, 1, InventoryKinds.Stack, fragments);
            Assert.Null(detail.Error);
            Assert.False(detail.Response!.Entry.HasCodeConflict); // There is only one row per case-insensitive code.
            var craft = detail.Response.Entry.Actions.Single(action => action.Action == "craft");
            Assert.False(craft.Allowed);
            Assert.Contains("InventoryCodeConflict", craft.ReasonCodes);
        }
        string before;
        await using (var verify = test.NewDb()) before = await MutationSnapshotAsync(verify);
        await using (var write = test.NewDb())
        {
            var rejected = await WeaponService(write).CraftBreakthroughStoneAsync(Context.Token, 1,
                new() { RequestId = Guid.NewGuid().ToString("N"), Tier = 1, Quantity = 1 });
            Assert.Equal("InventoryCodeConflict", rejected.Error);
            Assert.Null(rejected.Response);
        }
        await using var after = test.NewDb();
        Assert.Equal(before, await MutationSnapshotAsync(after));
        Assert.False(await after.CharacterItemStacks.AnyAsync(item => item.ItemCode == stones));
        Assert.Equal(legacyQuantity, (await after.CharacterItemStacks.SingleAsync(item => item.ItemCode == legacyCode)).Quantity);
        Assert.Equal(100, (await after.CharacterItemStacks.SingleAsync(item => item.ItemCode == fragments)).Quantity);
        Assert.Empty(await after.LogisticsRequests.ToListAsync());
    }

    [Fact]
    public async Task SparseFutureFragmentTiersStaySparseInLegacyReadsAndCommittedSaleResponses()
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Weapon(2), Stack("weapon-fragment-t1", 30),
            Stack("weapon-fragment-t99", 4), Stack("weapon-fragment-t2147483647", 9));
        await test.SaveAsync();
        static void AssertSparse(CharacterWeaponsResponse response)
        {
            Assert.Equal(new[] { 1, 99, int.MaxValue }, response.Fragments.Select(item => item.Tier));
            Assert.Equal(new[] { 30, 4, 9 }, response.Fragments.Select(item => item.Quantity));
            Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(response).Length, 1, 32000);
        }
        await using (var read = test.NewDb())
        {
            var legacy = await WeaponService(read).GetAsync(Context.Token, 1);
            Assert.Null(legacy.Error);
            AssertSparse(legacy.Response!);
        }
        var request = await ConfirmWeaponsAsync(test, "sell", [1]);
        await using (var write = test.NewDb())
        {
            var sold = await WeaponService(write).SellAsync(Context.Token, 1, request);
            Assert.Null(sold.Error);
            AssertSparse(sold.Response!);
            Assert.Equal(111, sold.Response!.Gold);
            Assert.Equal(2, Assert.Single(sold.Response.Weapons).Id);
        }
        await using (var replay = test.NewDb())
        {
            var retried = await WeaponService(replay).SellAsync(Context.Token, 1, request);
            Assert.Null(retried.Error);
            AssertSparse(retried.Response!);
            Assert.Equal(111, retried.Response!.Gold);
        }
        await using var verify = test.NewDb();
        Assert.Single(await verify.CharacterWeapons.ToListAsync());
        Assert.Single(await verify.LogisticsRequests.ToListAsync());
        Assert.Equal(3, await verify.CharacterItemStacks.CountAsync());
        Assert.Equal(9, (await verify.CharacterItemStacks.SingleAsync(item => item.ItemCode == "weapon-fragment-t2147483647")).Quantity);
    }

    [Theory]
    [InlineData("sell")]
    [InlineData("dismantle")]
    public async Task InventoryBatchReplayCreditsOnceAndReturnsTheOriginalReceipt(string action)
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Weapon(2), Weapon(3));
        await test.SaveAsync();
        var request = await ConfirmWeaponsAsync(test, action, [1, 2]);
        CharacterWeaponsResponse first;
        await using (var write = test.NewDb())
        {
            var result = await ExecuteWeaponsAsync(write, request, action);
            Assert.Null(result.Error);
            first = result.Response!;
            Assert.Equal(new[] { 1, 2 }, first.OperationResult!.InstanceIds);
        }
        await using (var replayDb = test.NewDb())
        {
            var replay = await ExecuteWeaponsAsync(replayDb, request, action);
            Assert.Null(replay.Error);
            Assert.Equal(JsonSerializer.Serialize(first.OperationResult), JsonSerializer.Serialize(replay.Response!.OperationResult));
        }
        await using (var changedDb = test.NewDb())
        {
            var altered = Clone(request); altered.WeaponIds = [3];
            Assert.Equal("RequestIdReused", (await ExecuteWeaponsAsync(changedDb, altered, action)).Error);
            Assert.Equal("RequestIdReused", (await ExecuteWeaponsAsync(changedDb, request, action == "sell" ? "dismantle" : "sell")).Error);
        }
        await using var verify = test.NewDb();
        Assert.Equal(3, (await verify.CharacterWeapons.SingleAsync()).Id);
        Assert.Single(await verify.LogisticsRequests.ToListAsync());
        var character = await verify.Characters.SingleAsync(character => character.Id == 1);
        Assert.Equal(9, character.Version);
        Assert.Equal(action == "sell" ? 121 : 101, character.Gold);
        if (action == "dismantle") Assert.Equal(10, (await verify.CharacterItemStacks.SingleAsync()).Quantity);
        else Assert.Empty(await verify.CharacterItemStacks.ToListAsync());
    }

    [Fact]
    public async Task ValidSoulDismantleAndReplayKeepUnknownInstancesAndCreditOnlyOnce()
    {
        await using var test = await Context.CreateAsync();
        var definition = Definitions.Value.Souls.Items.First();
        test.Db.AddRange(Soul(1, definition.Code), Soul(2, "retired-soul"));
        await test.SaveAsync();
        var request = await ConfirmSoulsAsync(test, [1]);
        CharacterSoulImprintsResponse response;
        await using (var db = test.NewDb())
        {
            var result = await SoulService(db).DismantleAsync(Context.Token, 1, request);
            Assert.Null(result.Error);
            response = result.Response!;
            Assert.Equal("retired-soul", Assert.Single(response.SoulImprints).Code);
        }
        await using (var db = test.NewDb())
        {
            var replay = await SoulService(db).DismantleAsync(Context.Token, 1, request);
            Assert.Null(replay.Error);
            Assert.Equal(JsonSerializer.Serialize(response.OperationResult), JsonSerializer.Serialize(replay.Response!.OperationResult));
            var altered = CloneSoul(request); altered.SoulImprintIds = [2];
            Assert.Equal("RequestIdReused", (await SoulService(db).DismantleAsync(Context.Token, 1, altered)).Error);
        }
        await using var verify = test.NewDb();
        Assert.Equal(2, (await verify.CharacterSoulImprints.SingleAsync()).Id);
        Assert.Equal(definition.DismantleFragments, (await verify.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Single(await verify.LogisticsRequests.ToListAsync());
    }

    [Theory]
    [InlineData("lock", "WeaponLocked")]
    [InlineData("formation", "FormationItemReferenced")]
    [InlineData("version", "InventoryPreviewChanged")]
    [InlineData("reward", "InventoryPreviewChanged")]
    [InlineData("equipped", "WeaponEquipped")]
    [InlineData("admission", "LoadoutLocked")]
    [InlineData("missing", "WeaponNotOwned")]
    public async Task BatchRechecksPreviewAndRejectsEveryItemWithoutPartialCredit(string change, string expected)
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Weapon(2));
        await test.SaveAsync();
        var request = await ConfirmWeaponsAsync(test, "sell", [1, 2]);
        await using var stale = test.NewDb();
        // Deliberately warm stale tracked entities before a second context changes the database.
        await stale.CharacterWeapons.Include(item => item.Skills).ToListAsync();
        await stale.Characters.ToListAsync();
        await using (var other = test.NewDb())
        {
            var weapon = await other.CharacterWeapons.SingleAsync(item => item.Id == 2);
            switch (change)
            {
                case "lock": weapon.IsLocked = true; weapon.Version++; break;
                case "formation": other.CharacterBattleFormations.Add(Formation(1, "新增保护编队", [2])); break;
                case "version": weapon.Version++; break;
                case "reward": weapon.SellGold++; break;
                case "equipped": weapon.EquippedSlotIndex = 1; weapon.Version++; break;
                case "admission": other.RoomSlots.Add(new() { RoomId = 90, SlotIndex = 1, UserId = 1, CharacterId = 1 }); break;
                case "missing": other.CharacterWeapons.Remove(weapon); break;
            }
            await other.SaveChangesAsync();
        }
        string before;
        await using (var verify = test.NewDb()) before = await MutationSnapshotAsync(verify);
        var failed = await ExecuteWeaponsAsync(stale, request, "sell");
        Assert.Null(failed.Response);
        Assert.StartsWith(expected, failed.Error);
        await using var after = test.NewDb();
        Assert.Equal(before, await MutationSnapshotAsync(after));
        Assert.True(await after.CharacterWeapons.AnyAsync(item => item.Id == 1));
        Assert.Empty(await after.LogisticsRequests.ToListAsync());
    }

    [Theory]
    [InlineData("lock", "SoulImprintLocked")]
    [InlineData("formation", "FormationItemReferenced")]
    [InlineData("version", "InventoryPreviewChanged")]
    public async Task SoulBatchAlsoRechecksProtectionAndVersionsAsOneTransaction(string change, string expected)
    {
        await using var test = await Context.CreateAsync();
        var code = Definitions.Value.Souls.Items.First().Code;
        test.Db.AddRange(Soul(1, code), Soul(2, code));
        await test.SaveAsync();
        var request = await ConfirmSoulsAsync(test, [1, 2]);
        await using (var other = test.NewDb())
        {
            var soul = await other.CharacterSoulImprints.SingleAsync(item => item.Id == 2);
            if (change == "lock") { soul.IsLocked = true; soul.Version++; }
            else if (change == "formation") other.CharacterBattleFormations.Add(Formation(1, "魂印保护", [], 2));
            else soul.Version++;
            await other.SaveChangesAsync();
        }
        string before;
        await using (var verify = test.NewDb()) before = await MutationSnapshotAsync(verify);
        await using (var write = test.NewDb())
            Assert.StartsWith(expected, (await SoulService(write).DismantleAsync(Context.Token, 1, request)).Error);
        await using var after = test.NewDb();
        Assert.Equal(before, await MutationSnapshotAsync(after));
    }

    [Theory]
    [InlineData("sell")]
    [InlineData("dismantle")]
    [InlineData("soul")]
    public async Task CreditOverflowLeavesWholeBatchAndAllReceiptsUnchanged(string action)
    {
        await using var test = await Context.CreateAsync();
        if (action == "soul")
        {
            var code = Definitions.Value.Souls.Items.First().Code;
            test.Db.AddRange(Soul(1, code), Soul(2, code));
        }
        else test.Db.AddRange(Weapon(1), Weapon(2));
        if (action == "sell")
        {
            var character = await test.Db.Characters.SingleAsync(item => item.Id == 1);
            character.Gold = int.MaxValue - 5;
        }
        else test.Db.Add(Stack("weapon-fragment-t1", int.MaxValue));
        await test.SaveAsync();
        var weaponRequest = action == "soul" ? null : await ConfirmWeaponsAsync(test, action, [1, 2]);
        var soulRequest = action == "soul" ? await ConfirmSoulsAsync(test, [1, 2]) : null;
        string before;
        await using (var verify = test.NewDb()) before = await MutationSnapshotAsync(verify);
        await using (var write = test.NewDb())
        {
            var error = action == "soul" ? (await SoulService(write).DismantleAsync(Context.Token, 1, soulRequest!)).Error
                : (await ExecuteWeaponsAsync(write, weaponRequest!, action)).Error;
            Assert.Equal("InventoryFull", error);
        }
        await using var after = test.NewDb();
        Assert.Equal(before, await MutationSnapshotAsync(after));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("tooMany")]
    public async Task InvalidSelectionIsRejectedByPreviewAndBothAssetServices(string selection)
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Soul(1, Definitions.Value.Souls.Items.First().Code));
        await test.SaveAsync();
        List<int> ids = selection switch
        {
            "empty" => [], "duplicate" => [1, 1], "zero" => [0], "negative" => [-1],
            _ => Enumerable.Range(1, 101).ToList()
        };
        await using var db = test.NewDb();
        Assert.Equal("InvalidInventorySelection", (await test.Query(db).PreviewAsync(Context.Token, 1,
            new() { AssetKind = InventoryKinds.Weapon, Action = "sell", InstanceIds = ids })).Error);
        Assert.Equal("InvalidWeaponSelection", (await WeaponService(db).SellAsync(Context.Token, 1, new() { WeaponIds = ids })).Error);
        Assert.Equal("InvalidSoulImprintSelection", (await SoulService(db).DismantleAsync(Context.Token, 1, new() { SoulImprintIds = ids })).Error);
        Assert.Equal(0, test.Counter.WritesAfterReset);
        Assert.Equal(1, await db.CharacterWeapons.CountAsync());
        Assert.Equal(1, await db.CharacterSoulImprints.CountAsync());
    }

    [Fact]
    public async Task AConfirmedHundredItemBatchIsAcceptedAtTheSelectionLimit()
    {
        await using var test = await Context.CreateAsync();
        test.Db.CharacterWeapons.AddRange(Enumerable.Range(1, 100).Select(id => Weapon(id)));
        await test.SaveAsync();
        var request = await ConfirmWeaponsAsync(test, "sell", Enumerable.Range(1, 100).ToArray());
        await using var write = test.NewDb();
        var result = await WeaponService(write).SellAsync(Context.Token, 1, request);
        Assert.Null(result.Error);
        Assert.Equal(1101, result.Response!.Gold);
        Assert.Empty(result.Response.Weapons);
        Assert.Equal(100, result.Response.OperationResult!.InstanceIds.Count);
    }

    [Theory]
    [InlineData("noRequest", "InvalidRequestId")]
    [InlineData("emptyGuid", "InvalidRequestId")]
    [InlineData("malformed", "InvalidRequestId")]
    [InlineData("noVersions", "InventoryPreviewRequired")]
    [InlineData("duplicateVersions", "InventoryPreviewRequired")]
    [InlineData("noOutcome", "InventoryPreviewRequired")]
    [InlineData("changedOutcome", "InventoryPreviewChanged")]
    public async Task BatchRequiresTheConfirmedVersionsOutcomeAndRequestId(string invalid, string expected)
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Weapon(2));
        await test.SaveAsync();
        var request = await ConfirmWeaponsAsync(test, "sell", [1, 2]);
        switch (invalid)
        {
            case "noRequest": request.RequestId = null; break;
            case "emptyGuid": request.RequestId = Guid.Empty.ToString(); break;
            case "malformed": request.RequestId = "bad"; break;
            case "noVersions": request.ExpectedVersions = []; break;
            case "duplicateVersions": request.ExpectedVersions = [request.ExpectedVersions[0], request.ExpectedVersions[0]]; break;
            case "noOutcome": request.OutcomeFingerprint = null; break;
            case "changedOutcome": request.OutcomeFingerprint = "changed"; break;
        }
        await using var write = test.NewDb();
        Assert.Equal(expected, (await WeaponService(write).SellAsync(Context.Token, 1, request)).Error);
        Assert.Equal(2, await write.CharacterWeapons.CountAsync());
        Assert.Empty(await write.LogisticsRequests.ToListAsync());
        Assert.Equal(101, (await write.Characters.SingleAsync(item => item.Id == 1)).Gold);
    }

    [Fact]
    public async Task MutationOwnershipNeverCreditsOrDeletesAnotherCharactersAssets()
    {
        await using var test = await Context.CreateAsync();
        var code = Definitions.Value.Souls.Items.First().Code;
        test.Db.AddRange(Weapon(1), Weapon(2, 2), Soul(1, code), Soul(2, code, 2));
        await test.SaveAsync();
        var request = await ConfirmWeaponsAsync(test, "sell", [1]);
        var soulRequest = await ConfirmSoulsAsync(test, [1]);
        await using var write = test.NewDb();
        Assert.Equal("Unauthorized", (await WeaponService(write).SellAsync("bad-token", 1, request)).Error);
        Assert.Equal("NotOwner", (await WeaponService(write).SellAsync(Context.Token, 3, request)).Error);
        Assert.Equal("CharacterNotFound", (await WeaponService(write).SellAsync(Context.Token, 999, request)).Error);
        Assert.Equal("WeaponNotOwned", (await WeaponService(write).SellAsync(Context.Token, 2, request)).Error);
        Assert.Equal("SoulImprintNotOwned", (await SoulService(write).DismantleAsync(Context.Token, 2, soulRequest)).Error);
        var mixed = Clone(request); mixed.WeaponIds = [1, 2];
        Assert.Equal("WeaponNotOwned", (await WeaponService(write).SellAsync(Context.Token, 1, mixed)).Error);
        Assert.Equal(2, await write.CharacterWeapons.CountAsync());
        Assert.Equal(2, await write.CharacterSoulImprints.CountAsync());
        Assert.Empty(await write.LogisticsRequests.ToListAsync());
        Assert.Equal(new[] { 101, 222, 333 }, await write.Characters.OrderBy(item => item.Id).Select(item => item.Gold).ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualityUpgradeReplayConsumesItsMaterialExactlyOnce(bool stone)
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Weapon(2), Stack("weapon-breakthrough-stone-t1", 3));
        await test.SaveAsync();
        var request = new UpgradeWeaponQualityRequest { RequestId = Guid.NewGuid().ToString("N"), MaterialWeaponId = stone ? 0 : 2, UseUniversalStone = stone };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var write = test.NewDb();
            var result = await WeaponService(write).UpgradeQualityAsync(Context.Token, 1, 1, request);
            Assert.Null(result.Error);
            Assert.Equal(1, result.Response!.Weapons.Single(item => item.Id == 1).QualityRank);
        }
        await using var verify = test.NewDb();
        Assert.Equal(stone ? 2 : 1, await verify.CharacterWeapons.CountAsync());
        Assert.Equal(stone ? 2 : 3, (await verify.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Single(await verify.LogisticsRequests.ToListAsync());
        request.MaterialWeaponId = stone ? 2 : 1;
        Assert.Equal("RequestIdReused", (await WeaponService(verify).UpgradeQualityAsync(Context.Token, 1, 1, request)).Error);
    }

    [Fact]
    public async Task BreakthroughCraftReplayConsumesFragmentsAndAddsStonesOnlyOnce()
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Stack("weapon-breakthrough-fragment-t1", 300), Stack("weapon-breakthrough-stone-t1", 1));
        await test.SaveAsync();
        var request = new CraftWeaponBreakthroughStoneRequest { RequestId = Guid.NewGuid().ToString("N"), Tier = 1, Quantity = 2 };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var write = test.NewDb();
            Assert.Null((await WeaponService(write).CraftBreakthroughStoneAsync(Context.Token, 1, request)).Error);
        }
        await using var verify = test.NewDb();
        Assert.Equal(100, (await verify.CharacterItemStacks.SingleAsync(item => item.ItemCode == "weapon-breakthrough-fragment-t1")).Quantity);
        Assert.Equal(3, (await verify.CharacterItemStacks.SingleAsync(item => item.ItemCode == "weapon-breakthrough-stone-t1")).Quantity);
        Assert.Single(await verify.LogisticsRequests.ToListAsync());
        request.Quantity = 1;
        Assert.Equal("RequestIdReused", (await WeaponService(verify).CraftBreakthroughStoneAsync(Context.Token, 1, request)).Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task HttpGrowthEndpointsRejectMissingOrInvalidGuidBeforeAnyMutation(string? requestId)
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Weapon(2), Stack("weapon-breakthrough-fragment-t1", 100));
        await test.SaveAsync();
        await using var db = test.NewDb();
        var controller = new WeaponController(WeaponService(db))
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers.Authorization = "Bearer " + Context.Token;
        var quality = await controller.UpgradeQuality(1, 1, new() { RequestId = requestId, MaterialWeaponId = 2 });
        var craft = await controller.CraftBreakthroughStone(1, new() { RequestId = requestId, Tier = 1, Quantity = 1 });
        Assert.Equal("InvalidRequestId", Assert.IsType<BadRequestObjectResult>(quality.Result).Value);
        Assert.Equal("InvalidRequestId", Assert.IsType<BadRequestObjectResult>(craft.Result).Value);
        Assert.Equal(0, test.Counter.Selects);
        Assert.Equal(0, test.Counter.WritesAfterReset);
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestsFromTwoContextsReturnOneCommittedReceipt()
    {
        await using var test = await Context.CreateAsync();
        test.Db.AddRange(Weapon(1), Weapon(2));
        await test.SaveAsync();
        var request = await ConfirmWeaponsAsync(test, "dismantle", [1, 2]);
        await using var first = test.NewDb();
        await using var second = test.NewDb();
        using var start = new Barrier(2);
        async Task<(CharacterWeaponsResponse? Response, string? Error)> Run(GameDbContext db)
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            return await WeaponService(db).DismantleAsync(Context.Token, 1, Clone(request));
        }
        var results = await Task.WhenAll(Task.Run(() => Run(first)), Task.Run(() => Run(second)));
        Assert.All(results, result => Assert.Null(result.Error));
        Assert.Equal(JsonSerializer.Serialize(results[0].Response!.OperationResult), JsonSerializer.Serialize(results[1].Response!.OperationResult));
        await using var verify = test.NewDb();
        Assert.Empty(await verify.CharacterWeapons.ToListAsync());
        Assert.Equal(10, (await verify.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.Single(await verify.LogisticsRequests.ToListAsync());
        Assert.Equal(9, (await verify.Characters.SingleAsync(item => item.Id == 1)).Version);
    }

    [Fact]
    public async Task ConcurrentLockAndSellCannotBothCommitAgainstTheSameInstance()
    {
        await using var test = await Context.CreateAsync();
        test.Db.Add(Weapon(1));
        await test.SaveAsync();
        var request = await ConfirmWeaponsAsync(test, "sell", [1]);
        await using var selling = test.NewDb();
        await using var locking = test.NewDb();
        using var start = new Barrier(2);
        var saleTask = Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            return await WeaponService(selling).SellAsync(Context.Token, 1, request);
        });
        var lockTask = Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
            return await WeaponService(locking).SetLockAsync(Context.Token, 1, 1, new() { IsLocked = true });
        });
        await Task.WhenAll(saleTask, lockTask);
        var sale = await saleTask; var locked = await lockTask;
        Assert.False(sale.Error is null && locked.Error is null, "Lock and sale must not both commit the same instance.");
        await using var verify = test.NewDb();
        var retained = await verify.CharacterWeapons.SingleOrDefaultAsync();
        if (sale.Error is null)
        {
            Assert.Null(retained);
            Assert.Contains(locked.Error, new[] { "WeaponNotOwned", "ConcurrencyConflict" });
            Assert.Equal(111, (await verify.Characters.SingleAsync(item => item.Id == 1)).Gold);
            Assert.Single(await verify.LogisticsRequests.ToListAsync());
        }
        else
        {
            Assert.NotNull(retained);
            Assert.True(retained.IsLocked);
            Assert.Null(locked.Error);
            Assert.Contains(sale.Error, new[] { "WeaponLocked", "InventoryPreviewChanged", "ConcurrencyConflict" });
            Assert.Equal(101, (await verify.Characters.SingleAsync(item => item.Id == 1)).Gold);
            Assert.Empty(await verify.LogisticsRequests.ToListAsync());
        }
    }

    private static WeaponService WeaponService(GameDbContext db)
    {
        var skills = SkillTestFactory.Create();
        return new(db, new UserService(db, ProgressionTestFactory.Create(), skills), skills,
            Definitions.Value.Weapons, Definitions.Value.Breakthroughs);
    }

    private static SoulImprintService SoulService(GameDbContext db) => new(db,
        new UserService(db, ProgressionTestFactory.Create(), SkillTestFactory.Create()), Definitions.Value.Souls);

    private static async Task<WeaponBatchRequest> ConfirmWeaponsAsync(Context test, string action, int[] ids)
    {
        await using var read = test.NewDb();
        var result = await test.Query(read).PreviewAsync(Context.Token, 1,
            new() { AssetKind = InventoryKinds.Weapon, Action = action, InstanceIds = ids.ToList() });
        Assert.Null(result.Error);
        Assert.True(result.Response!.Allowed);
        return new() { WeaponIds = ids.ToList(), RequestId = Guid.NewGuid().ToString("N"),
            ExpectedVersions = result.Response.ExpectedVersions, OutcomeFingerprint = result.Response.OutcomeFingerprint };
    }

    private static async Task<SoulImprintBatchRequest> ConfirmSoulsAsync(Context test, int[] ids)
    {
        await using var read = test.NewDb();
        var result = await test.Query(read).PreviewAsync(Context.Token, 1,
            new() { AssetKind = InventoryKinds.SoulImprint, Action = "dismantle", InstanceIds = ids.ToList() });
        Assert.Null(result.Error);
        Assert.True(result.Response!.Allowed);
        return new() { SoulImprintIds = ids.ToList(), RequestId = Guid.NewGuid().ToString("N"),
            ExpectedVersions = result.Response.ExpectedVersions, OutcomeFingerprint = result.Response.OutcomeFingerprint };
    }

    private static Task<(CharacterWeaponsResponse? Response, string? Error)> ExecuteWeaponsAsync(GameDbContext db,
        WeaponBatchRequest request, string action) => action == "sell" ? WeaponService(db).SellAsync(Context.Token, 1, request)
            : WeaponService(db).DismantleAsync(Context.Token, 1, request);

    private static WeaponBatchRequest Clone(WeaponBatchRequest request) => new()
    {
        RequestId = request.RequestId, WeaponIds = request.WeaponIds.ToList(), OutcomeFingerprint = request.OutcomeFingerprint,
        ExpectedVersions = request.ExpectedVersions.Select(item => new InventoryInstanceVersion { Id = item.Id, Version = item.Version }).ToList()
    };

    private static SoulImprintBatchRequest CloneSoul(SoulImprintBatchRequest request) => new()
    {
        RequestId = request.RequestId, SoulImprintIds = request.SoulImprintIds.ToList(), OutcomeFingerprint = request.OutcomeFingerprint,
        ExpectedVersions = request.ExpectedVersions.Select(item => new InventoryInstanceVersion { Id = item.Id, Version = item.Version }).ToList()
    };

    private static async Task<string> MutationSnapshotAsync(GameDbContext db) => JsonSerializer.Serialize(new
    {
        Characters = await db.Characters.AsNoTracking().OrderBy(item => item.Id).Select(item => new { item.Id, item.Gold, item.Version, item.Hp, item.MaxHp, item.Attack }).ToListAsync(),
        Weapons = await db.CharacterWeapons.AsNoTracking().Include(item => item.Skills).OrderBy(item => item.Id).ToListAsync(),
        Souls = await db.CharacterSoulImprints.AsNoTracking().OrderBy(item => item.Id).ToListAsync(),
        Stacks = await db.CharacterItemStacks.AsNoTracking().OrderBy(item => item.Id).ToListAsync(),
        Receipts = await db.LogisticsRequests.AsNoTracking().OrderBy(item => item.CharacterId).ThenBy(item => item.RequestId).ToListAsync()
    });
}
