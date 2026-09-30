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

public sealed class FirstHuntBalanceTests
{
    private static readonly (string Code, ElementType Element, int Hp, int Attack, string Skill, string Weapon)[] Hunts =
    [
        ("durotar-valley-boar", ElementType.Fire, 4450, 100, "燧谷冲撞", "t1-candle-staff"),
        ("dun-morogh-snow-hare", ElementType.Water, 4400, 100, "霜足蹬击", "t1-ice-tusk-mallet"),
        ("northshire-wolves", ElementType.Earth, 4500, 110, "岩牙扑咬", "t1-stone-edge-hatchet"),
        ("mulgore-plainstrider-chick", ElementType.Wind, 4475, 95, "疾羽啄击", "t1-feather-short-staff"),
        ("eversong-golden-lynx", ElementType.Light, 4550, 105, "琉光裂爪", "t1-sentry-old-sword"),
        ("tirisfal-dusk-bat", ElementType.Dark, 4050, 100, "暗翼俯冲", "t1-dim-apprentice-staff")
    ];

    [Fact]
    public async Task SixFirstHuntsHaveFixedSecondRoundIntentAndMatchingRewardPreview()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Game.Server", "appsettings.json"))).Build();
        T OptionsFor<T>(string section) where T : class, new() =>
            configuration.GetSection(section).Get<T>()!;
        var encounters = OptionsFor<DungeonEncounterOptions>(DungeonEncounterOptions.SectionName);
        var combatOptions = OptionsFor<MonsterCombatOptions>(MonsterCombatOptions.SectionName);
        var rewardOptions = OptionsFor<RewardOptions>(RewardOptions.SectionName);
        var combat = new MonsterCombatCatalog(Options.Create(combatOptions));
        var consumables = new ConsumableCatalog(Options.Create(OptionsFor<ConsumableOptions>(ConsumableOptions.SectionName)));
        var weapons = new WeaponCatalog(Options.Create(OptionsFor<WeaponOptions>(WeaponOptions.SectionName)));
        var materials = new MaterialCatalog(Options.Create(OptionsFor<MaterialOptions>(MaterialOptions.SectionName)));
        var soulImprints = new SoulImprintCatalog(Options.Create(OptionsFor<SoulImprintOptions>(SoulImprintOptions.SectionName)));
        var rewards = new RewardCatalog(Options.Create(rewardOptions), consumables, weapons, materials, soulImprints);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { UserName = "hunter" };
        var dungeon = new Dungeon { Code = Hunts[0].Code, Name = "首怪讨伐", MonsterMaxHp = 1 };
        db.AddRange(user, dungeon);
        await db.SaveChangesAsync();
        var character = new Character { UserId = user.Id, Name = "前排", Hp = 10000, MaxHp = 10000, Attack = 10 };
        var room = new Room { DungeonId = dungeon.Id, OwnerUserId = user.Id, SlotCount = 1 };
        db.AddRange(character, room);
        await db.SaveChangesAsync();
        var slot = new RoomSlot { RoomId = room.Id, CharacterId = character.Id, UserId = user.Id, SlotIndex = 1 };
        var monster = new Monster { RoomId = room.Id, Name = "首怪", Hp = 5000, MaxHp = 5000, Attack = 160 };
        db.AddRange(slot, monster);
        await db.SaveChangesAsync();
        var service = new MonsterCombatService(db, combat);

        foreach (var (code, element, hp, attack, skillName, weaponCode) in Hunts)
        {
            var configured = Assert.Single(Assert.Single(encounters.Dungeons[code]).Monsters);
            Assert.Equal((element, hp, attack, 0, code),
                (configured.Element, configured.MaxHp, configured.Attack, configured.Defense, configured.CombatProfileCode));
            Assert.Equal(12, rewardOptions.MonsterKills[code].Experience + rewardOptions.DungeonClears[code].Experience);
            Assert.Equal(10, rewardOptions.MonsterKills[code].Gold + rewardOptions.DungeonClears[code].Gold);
            var killDrop = Assert.Single(rewards.GetDropPreview(code, false));
            Assert.Equal(("Weapon", weaponCode, 1, 5m),
                (killDrop.Kind, killDrop.Code, killDrop.Quantity, killDrop.ChancePercent));
            Assert.Contains(rewards.GetDropPreview(code, true), drop =>
                drop.Kind == "Consumable" && drop.Code == "minor-healing-potion" && drop.ChancePercent == 35);
            Assert.Contains(rewards.GetDropPreview(code, true), drop =>
                drop.Kind == "Material" && drop.Code == "weapon-fragment-t1" &&
                drop.Quantity == 1 && drop.ChancePercent == 30);
            var skillCode = Assert.Single(combat.FindProfile(code)!.Skills).Code;
            var skill = combat.FindSkill(skillCode)!;
            Assert.Equal((skillName, "Front", 125, 1, 20, 50),
                (skill.Name, skill.TargetType, skill.DamagePowerPercent, skill.RoomRoundAtLeast,
                    skill.CooldownRounds, skill.ForcedPriority));
            Assert.Empty(skill.Statuses);

            room.RunSequence++;
            room.RoundNumber = 0;
            monster.CombatProfileCode = code;
            monster.Attack = attack;
            Assert.Equal("BasicAttack", (await service.EnsureIntentAsync(room, monster)).ActionType);
            room.RoundNumber = 1;
            var second = await service.EnsureIntentAsync(room, monster);
            Assert.Equal("Skill", second.ActionType);
            Assert.Equal(skillCode, second.SkillCode);
            Assert.Equal(character.Id, second.TargetCharacterId);
            await service.ExecuteIntentAsync(room, monster, [new(slot, character)],
                new Dictionary<int, ElementType>(), []);
            room.RoundNumber = 2;
            Assert.Equal("BasicAttack", (await service.EnsureIntentAsync(room, monster)).ActionType);
        }
    }
}
