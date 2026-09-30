using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class TransitionHuntBalanceTests
{
    private static readonly (string Code, int Level, int Hp, int Attack, int Power, string? Status)[] Hunts =
    [
        ("durotar-dust-raptor", 3, 10000, 110, 125, null),
        ("dun-morogh-rockjaw-digger", 3, 9700, 110, 115, null),
        ("stone-tusk-boars", 3, 9300, 125, 130, null),
        ("mulgore-bristleback-forager", 3, 9800, 110, 120, null),
        ("eversong-feral-treant", 3, 9600, 120, 115, null),
        ("tirisfal-rotting-ghoul", 3, 10200, 110, 125, null),
        ("durotar-burning-cultist", 5, 19400, 120, 110, "hunt-scorch"),
        ("dun-morogh-ice-shell-boar", 5, 18400, 125, 110, "hunt-chill"),
        ("kobold-miners", 5, 17600, 145, 110, "hunt-armor-break"),
        ("mulgore-venture-lumberjack", 5, 16200, 125, 120, null),
        ("eversong-runestone-sentinel", 5, 18400, 135, 115, null),
        ("tirisfal-forsaken-acolyte", 5, 16400, 125, 110, "hunt-poison")
    ];

    [Fact]
    public async Task TransitionHuntsUseMatchingStatsAndTelegraphedSkillSchedule()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server"));
        var settings = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "appsettings.json")).Build();
        var worldSettings = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "world.json")).Build();
        var encounters = settings.GetSection(DungeonEncounterOptions.SectionName).Get<DungeonEncounterOptions>()!;
        var combat = new MonsterCombatCatalog(Options.Create(
            settings.GetSection(MonsterCombatOptions.SectionName).Get<MonsterCombatOptions>()!));
        var rewards = new RewardCatalog(
            Options.Create(settings.GetSection(RewardOptions.SectionName).Get<RewardOptions>()!),
            new ConsumableCatalog(Options.Create(settings.GetSection(ConsumableOptions.SectionName).Get<ConsumableOptions>()!)),
            new WeaponCatalog(Options.Create(settings.GetSection(WeaponOptions.SectionName).Get<WeaponOptions>()!)),
            new MaterialCatalog(Options.Create(settings.GetSection(MaterialOptions.SectionName).Get<MaterialOptions>()!)),
            new SoulImprintCatalog(Options.Create(settings.GetSection(SoulImprintOptions.SectionName).Get<SoulImprintOptions>()!)));
        var world = new WorldCatalog(Options.Create(worldSettings.GetSection(WorldOptions.SectionName).Get<WorldOptions>()!),
            encounters: new DungeonEncounterCatalog(Options.Create(encounters), combat, rewards));
        var huntTiers = world.Dungeons.Where(entry => entry.IsVisible && entry.DungeonKind == "Hunt" &&
            entry.RecommendedLevel is 1 or 3 or 5).GroupBy(entry => entry.MonsterElement).ToList();
        Assert.Equal(6, huntTiers.Count);
        foreach (var tier in huntTiers)
        {
            var attacks = tier.OrderBy(entry => entry.RecommendedLevel).Select(entry => entry.MonsterAttack).ToList();
            Assert.Equal(3, attacks.Count);
            Assert.True(attacks[0] < attacks[1] && attacks[1] < attacks[2],
                $"{tier.Key} hunt attack should rise from Lv1 to Lv5.");
        }

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { UserName = "hunter" };
        var dungeon = new Dungeon { Code = Hunts[0].Code, Name = "过渡讨伐", MonsterMaxHp = 1 };
        db.AddRange(user, dungeon);
        await db.SaveChangesAsync();
        var character = new Character { UserId = user.Id, Name = "前排", Hp = 50000, MaxHp = 50000, Attack = 10 };
        var room = new Room { DungeonId = dungeon.Id, OwnerUserId = user.Id, SlotCount = 1 };
        db.AddRange(character, room);
        await db.SaveChangesAsync();
        var slot = new RoomSlot { RoomId = room.Id, CharacterId = character.Id, UserId = user.Id, SlotIndex = 1 };
        var monster = new Monster { RoomId = room.Id, Name = "过渡怪", Hp = 50000, MaxHp = 50000 };
        db.AddRange(slot, monster);
        await db.SaveChangesAsync();
        var service = new MonsterCombatService(db, combat);

        foreach (var (code, level, hp, attack, power, status) in Hunts)
        {
            var configured = Assert.Single(Assert.Single(encounters.Dungeons[code]).Monsters);
            var visible = Assert.Single(world.Dungeons, entry => entry.Code == code);
            Assert.Equal((hp, attack, 0, code),
                (configured.MaxHp, configured.Attack, configured.Defense, configured.CombatProfileCode));
            Assert.Equal((hp, attack, 0, level),
                (visible.MonsterMaxHp, visible.MonsterAttack, visible.MonsterDefense, visible.RecommendedLevel));
            var fragments = Assert.Single(rewards.GetDropPreview(code, true), drop =>
                drop.Kind == "Material" && drop.Code == "weapon-fragment-t1");
            Assert.Equal((1, level == 3 ? 50m : 70m), (fragments.Quantity, fragments.ChancePercent));

            var profile = combat.FindProfile(code)!;
            Assert.Equal(0, profile.SkillUseChancePercent);
            var skill = combat.FindSkill(Assert.Single(profile.Skills).Code)!;
            Assert.Equal((power, level == 3 ? 2 : 3, level == 3 ? 20 : 3, 50, true),
                (skill.DamagePowerPercent, skill.RoomRoundAtLeast, skill.CooldownRounds,
                    skill.ForcedPriority, skill.IsInterruptible));
            if (status is null) Assert.Empty(skill.Statuses);
            else Assert.Equal(status, Assert.Single(skill.Statuses).StatusCode);

            room.RunSequence++;
            monster.CombatProfileCode = code;
            monster.Attack = attack;
            for (var round = 0; round < 10; round++)
            {
                room.RoundNumber = round;
                var intent = await service.EnsureIntentAsync(room, monster);
                var shouldCast = level == 3 ? round == 2 : round is 3 or 7;
                Assert.Equal(shouldCast ? "Skill" : "BasicAttack", intent.ActionType);
                if (shouldCast) Assert.Equal(skill.Code, intent.SkillCode);
                await service.ExecuteIntentAsync(room, monster, [new(slot, character)],
                    new Dictionary<int, Game.Shared.Enums.ElementType>(), []);
                if (status is not null && round == 3)
                    Assert.True(await service.HasStatusAsync(room, "Character", character.Id, status));
            }
        }
    }
}
