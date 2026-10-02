using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class EntryDungeonIntegrationTests
{
    [Theory]
    [InlineData("ragefire-chasm")]
    [InlineData("frostspring-cavern")]
    [InlineData("kobold-mine")]
    [InlineData("windfury-nest")]
    [InlineData("dawn-ruins")]
    [InlineData("spider-canyon")]
    public async Task ProductionEntryRewardsIncludeDeadParticipantsUnlockDeepAndKeepPartialKillsOnFailure(string code)
    {
        var config = new ConfigurationBuilder().AddJsonFile(TestRepository.File("Game.Server", "appsettings.json")).Build();
        IOptions<T> Bind<T>(string section) where T : class, new() => Options.Create(config.GetSection(section).Get<T>()!);
        var weapons = new WeaponCatalog(Bind<WeaponOptions>(WeaponOptions.SectionName));
        var rewards = new RewardCatalog(Bind<RewardOptions>(RewardOptions.SectionName),
            new ConsumableCatalog(Bind<ConsumableOptions>(ConsumableOptions.SectionName)), weapons,
            new MaterialCatalog(Bind<MaterialOptions>(MaterialOptions.SectionName)),
            new SoulImprintCatalog(Bind<SoulImprintOptions>(SoulImprintOptions.SectionName)), new HighRollRandom());
        var combat = new MonsterCombatCatalog(Bind<MonsterCombatOptions>(MonsterCombatOptions.SectionName));
        var depths = new DungeonDepthCatalog(Bind<DungeonDepthOptions>(DungeonDepthOptions.SectionName));
        var parties = new PartyScalingCatalog(Bind<PartyScalingOptions>(PartyScalingOptions.SectionName));
        var planting = new PlantingCatalog(Bind<PlantingOptions>(PlantingOptions.SectionName));
        var encounters = new DungeonEncounterCatalog(Bind<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName),
            combat, rewards, depths);
        var world = WorldCatalog.LoadDefault();
        var dungeon = world.Dungeons.Single(dungeon => dungeon.Code == code);
        var deepCode = config.GetSection(DungeonDepthOptions.SectionName).Get<DungeonDepthOptions>()!.Dungeons
            .Single(pair => pair.Value.PrerequisiteDungeonCode == code).Key;
        var deep = world.Dungeons.Single(dungeon => dungeon.Code == deepCode);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var survivor = new Character { Id = 1, UserId = 1, Name = "Survivor", ProfessionCode = "swordsman", Level = 8, Hp = 1000, MaxHp = 1000 };
        var fallen = new Character { Id = 2, UserId = 1, Name = "Fallen", ProfessionCode = "acolyte", Level = 8, Hp = 1000, MaxHp = 1000 };
        var spectator = new Character { Id = 3, UserId = 2, Name = "Spectator", ProfessionCode = "mage", Level = 8, Hp = 1000, MaxHp = 1000 };
        db.AddRange(new User { Id = 1, UserName = "owner", PasswordHash = "x", ActiveCharacterId = 1 },
            new User { Id = 2, UserName = "spectator", PasswordHash = "x", ActiveCharacterId = 3 },
            survivor, fallen, spectator, dungeon, deep);
        await db.SaveChangesAsync();
        var rules = new DungeonRunRulesService(db, combat, rewards, parties, depths, plants: planting, encounters: encounters);
        var progress = new DungeonDepthProgressService(db, depths, rules);
        var rewardService = new RewardService(db, rewards, ProgressionTestFactory.Create(), depthProgress: progress, runRules: rules);
        var runs = new DungeonRunService(db, rewardService, rareSeeds: new RareSeedService(db, planting, rules), depthProgress: progress, runRules: rules);
        var participants = new[] { new RewardParticipant(1, survivor), new RewardParticipant(1, fallen), new RewardParticipant(2, spectator) };
        Assert.Equal("DungeonDepthLocked", await progress.AdmissionErrorAsync(1, deep, 1));

        var first = await RunAsync(4, dieBeforeBoss: true);
        Assert.Equal((130, 105, 0), (survivor.Gold, fallen.Gold, spectator.Gold));
        Assert.Equal(250, await db.RewardEntries.Where(entry => entry.RoomId == first.Id && entry.CharacterId == 1 && entry.Kind == "Experience")
            .SumAsync(entry => entry.Quantity));
        foreach (var characterId in new[] { 1, 2 })
        {
            Assert.Equal(32, (await db.CharacterItemStacks.SingleAsync(stack => stack.CharacterId == characterId && stack.ItemCode == "weapon-fragment-t1")).Quantity);
            Assert.False(await db.CharacterItemStacks.AnyAsync(stack => stack.CharacterId == characterId && stack.ItemCode == "minor-healing-potion"));
            Assert.True(await db.CharacterBattleMilestones.AnyAsync(milestone => milestone.CharacterId == characterId &&
                milestone.Kind == BattleMilestoneService.DungeonClearKind && milestone.TargetCode == code));
        }
        Assert.False(await db.RewardEntries.AnyAsync(entry => entry.CharacterId == 3));
        Assert.Null(await progress.AdmissionErrorAsync(1, deep, 1));
        Assert.Equal("DungeonDepthLocked", await progress.AdmissionErrorAsync(2, deep, 1));
        Assert.Equal("DungeonDepthLocked", await progress.AdmissionErrorAsync(1, deep, 2));

        var repeat = await RunAsync(4, dieBeforeBoss: false);
        Assert.Equal((220, 195, 0), (survivor.Gold, fallen.Gold, spectator.Gold));
        Assert.False(await db.RewardEntries.AnyAsync(entry => entry.RoomId == repeat.Id && entry.EventKey == "first-clear"));
        foreach (var characterId in new[] { 1, 2 })
        {
            Assert.Equal(44, (await db.CharacterItemStacks.SingleAsync(stack => stack.CharacterId == characterId && stack.ItemCode == "weapon-fragment-t1")).Quantity);
            Assert.False(await db.CharacterItemStacks.AnyAsync(stack => stack.CharacterId == characterId && stack.ItemCode == "minor-healing-potion"));
        }
        Assert.Single(await db.UserDungeonClears.ToListAsync());
        Assert.False(await db.RewardEntries.AnyAsync(entry => entry.Kind == "Consumable"));
        Assert.False(await db.RewardEntries.AnyAsync(entry => entry.Code.StartsWith("seed-")));

        var failed = await RunAsync(3, dieBeforeBoss: false);
        Assert.Equal(45, await db.RewardEntries.Where(entry => entry.RoomId == failed.Id && entry.CharacterId == 1 && entry.Kind == "Gold")
            .SumAsync(entry => entry.Quantity));
        Assert.False(await db.RewardEntries.AnyAsync(entry => entry.RoomId == failed.Id &&
            (entry.EventKey == "clear" || entry.EventKey == "first-clear" || entry.Code == "weapon-fragment-t1" || entry.Code == "minor-healing-potion")));
        Assert.Equal("Defeat", (await db.RewardRuns.SingleAsync(run => run.RoomId == failed.Id)).Status);

        async Task<Room> RunAsync(int kills, bool dieBeforeBoss)
        {
            // A character must leave the completed room before joining another one.
            foreach (var previousSlot in await db.RoomSlots.ToListAsync())
            {
                previousSlot.CharacterId = null;
                previousSlot.UserId = null;
            }
            await db.SaveChangesAsync();
            fallen.Hp = fallen.MaxHp;
            var room = new Room { DungeonId = dungeon.Id, OwnerUserId = 1, SlotCount = 5, Status = RoomStatus.Preparing,
                TotalWaveCount = 4, CurrentWaveNumber = 1, RunSequence = 1 };
            db.Rooms.Add(room);
            await db.SaveChangesAsync();
            var monsters = encounters.CreateMonsters(dungeon);
            foreach (var monster in monsters) monster.RoomId = room.Id;
            db.Monsters.AddRange(monsters);
            await db.SaveChangesAsync();
            room.MonsterId = monsters[0].Id;
            for (var index = 0; index < participants.Length; index++)
                db.RoomSlots.Add(new RoomSlot { RoomId = room.Id, SlotIndex = index + 1,
                    UserId = participants[index].UserId, CharacterId = participants[index].Character.Id });
            var frozen = await rules.EnsureAsync(room);
            Assert.Equal(DungeonRewardEligibility.ActualParticipants, frozen.RewardEligibility);
            await db.SaveChangesAsync();
            for (var index = 0; index < kills; index++)
            {
                var bossDeath = dieBeforeBoss && index == 3;
                if (bossDeath) fallen.Hp = 0;
                monsters[index].Hp = 0;
                var result = await runs.AdvanceAfterDefeatAsync(room, monsters[index], participants, DateTime.UtcNow, [],
                    bossDeath ? [1] : [1, 2], [1, 2]);
                Assert.Null(result.Error);
                Assert.Equal(index == 3, result.IsDungeonComplete);
                if (index < 3) Assert.Equal(index + 2, result.ActiveMonster.WaveNumber);
                await db.SaveChangesAsync();
            }
            if (kills < 4)
            {
                await rewardService.SettleAsync(room, false, DateTime.UtcNow, []);
                await db.SaveChangesAsync();
            }
            return room;
        }
    }

    private sealed class HighRollRandom : Random
    {
        public override double NextDouble() => .99;
    }
}
