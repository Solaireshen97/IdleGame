using System.Data.Common;
using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class FormationSkillLibraryTests
{
    [Fact]
    public async Task DraftLibraryIncludesAllNativeAndOtherProfessionsSharedSkillsWithoutUnlockingThem()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Actor, fixture.Draft("mage"));
        Assert.True(preview.CanDeploy);
        Assert.Equal(9, preview.Level);
        var native = preview.SkillLibrary.Where(skill => !skill.IsShared).ToList();
        Assert.Equal(5, native.Count);
        Assert.All(native, skill =>
        {
            Assert.Equal("mage", skill.SourceProfessionCode);
            Assert.Equal("法师", skill.SourceProfessionName);
            Assert.Equal(9, skill.SourceProfessionLevel);
        });
        var locked = Assert.Single(native, skill => skill.Code == "mage-5");
        Assert.False(locked.IsUnlocked);
        Assert.False(locked.CanEquip);
        Assert.Equal((10, 10, 1), (locked.RequiredProfessionLevel, locked.RequiredCurrentProfessionLevel, locked.Level));
        Assert.DoesNotContain(preview.AvailableSkills, skill => skill.Code == locked.Code);
        Assert.Equal(4, preview.AvailableSkills.Count);
        var shared = preview.SkillLibrary.Where(skill => skill.IsShared).ToList();
        Assert.Equal(4, shared.Count);
        Assert.DoesNotContain(shared, skill => skill.SourceProfessionCode == "mage");
        Assert.All(shared, skill => Assert.Equal((30, 10), (skill.RequiredProfessionLevel, skill.RequiredCurrentProfessionLevel)));
        Assert.Equal(1, Assert.Single(shared, skill => skill.SourceProfessionCode == "rogue").SourceProfessionLevel);
        Assert.False(Assert.Single(shared, skill => skill.SourceProfessionCode == "acolyte").IsUnlocked);
    }

    [Theory]
    [InlineData(9, 1)]
    [InlineData(15, 2)]
    [InlineData(25, 3)]
    public async Task NativeLevelPreviewsUseCompiledEffectsAndCooldowns(int draftLevel, int expectedRank)
    {
        await using var fixture = await Fixture.CreateAsync();
        var growth = await fixture.Db.CharacterCombatProfessions.SingleAsync(item => item.ProfessionCode == "mage");
        growth.Level = draftLevel;
        await fixture.Db.SaveChangesAsync();
        var preview = await fixture.Service.PreviewAsync(fixture.Actor, fixture.Draft("mage"));
        var skill = Assert.Single(preview.SkillLibrary, item => item.Code == "mage-5");
        Assert.Equal(expectedRank, skill.Level);
        Assert.Collection(skill.LevelPreviews,
            level => AssertLevel(level, 1, 10, 100m, 8, 2, false),
            level => AssertLevel(level, 2, 20, 125m, 6, 1, false),
            level => AssertLevel(level, 3, 30, 150m, 4, 0, false));
        Assert.Equal(skill.LevelPreviews[expectedRank - 1].CooldownRounds, skill.CooldownRounds);
        Assert.Equal(JsonSerializer.Serialize(skill.LevelPreviews[expectedRank - 1].Effects), JsonSerializer.Serialize(skill.Effects));
        var shared = Assert.Single(preview.SkillLibrary, item => item.Code == "swordsman-5");
        AssertLevel(Assert.Single(shared.LevelPreviews), 3, 7, 60m, 11, 5, true);
        AssertLevel(shared, 3, 7, 60m, 11, 5, true);
    }

    [Theory]
    [InlineData(29, 10, false, false)]
    [InlineData(30, 9, true, false)]
    [InlineData(30, 10, true, true)]
    public async Task SharedEligibilityUsesSourceLevelAndDraftLevelWhileAvailableSkillsKeepTheirMeaning(
        int sourceLevel, int draftLevel, bool unlocked, bool equip)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Actor.Level = sourceLevel;
        (await fixture.Db.CharacterCombatProfessions.SingleAsync(item => item.ProfessionCode == "mage")).Level = draftLevel;
        await fixture.Db.SaveChangesAsync();
        var draft = fixture.Draft("mage");
        draft.Skills.Add(new() { SlotIndex = 1, SkillCode = "swordsman-5" });
        var preview = await fixture.Service.PreviewAsync(fixture.Actor, draft);
        var shared = Assert.Single(preview.SkillLibrary, item => item.Code == "swordsman-5");
        Assert.Equal(sourceLevel, shared.SourceProfessionLevel); // Active level overrides the old stored snapshot.
        Assert.Equal(unlocked, shared.IsUnlocked);
        Assert.Equal(equip, shared.CanEquip);
        Assert.Equal(equip, preview.AvailableSkills.Any(item => item.Code == shared.Code));
        Assert.Equal(equip, preview.CanDeploy);
        Assert.Equal(!equip, preview.Issues.Any(issue => issue.Code == "SkillNotLearned"));
    }

    [Fact]
    public async Task ChangingDraftProfessionChangesLibraryAndLockedNativeSkillStillCannotEquip()
    {
        await using var fixture = await Fixture.CreateAsync();
        var knight = await fixture.Service.PreviewAsync(fixture.Actor, fixture.Draft("swordsman"));
        Assert.All(knight.SkillLibrary.Where(item => !item.IsShared), item =>
        {
            Assert.Equal("swordsman", item.SourceProfessionCode);
            Assert.True(item.CanEquip);
            Assert.Equal(3, item.Level);
        });
        Assert.DoesNotContain(knight.SkillLibrary, item => item.IsShared && item.SourceProfessionCode == "swordsman");
        Assert.Contains(knight.SkillLibrary, item => item.IsShared && item.SourceProfessionCode == "mage" && !item.IsUnlocked);
        var mage = fixture.Draft("mage");
        mage.Skills.Add(new() { SlotIndex = 1, SkillCode = "mage-5" });
        var preview = await fixture.Service.PreviewAsync(fixture.Actor, mage);
        Assert.False(preview.CanDeploy);
        Assert.Contains(preview.Issues, issue => issue.Code == "SkillNotLearned" && issue.SlotIndex == 1);
        Assert.DoesNotContain(preview.AvailableSkills, item => item.Code == "mage-5");
        Assert.Equal("swordsman", fixture.Actor.ProfessionCode);
    }

    [Fact]
    public async Task LibraryReadsHaveNoTrackedOrPersistedSideEffects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var before = await fixture.SnapshotAsync();
        var actorBefore = JsonSerializer.Serialize(fixture.Actor);
        fixture.Writes.Count = 0;
        var previews = await fixture.Service.PreviewManyAsync(fixture.Actor,
            new[] { (fixture.Draft("mage"), (ElementType?)null), (fixture.Draft("rogue"), (ElementType?)null) });
        Assert.All(previews, preview => Assert.Equal(9, preview.SkillLibrary.Count));
        Assert.Equal(actorBefore, JsonSerializer.Serialize(fixture.Actor));
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Equal(before, await fixture.SnapshotAsync());
        Assert.Equal(0, fixture.Writes.Count);
    }

    private static void AssertLevel(Game.Shared.Dtos.Characters.LearnedSkillResponse skill, int rank, int power,
        decimal percent, int cooldown, int initialCooldown, bool shared)
    {
        Assert.Equal((rank, power, cooldown, initialCooldown, shared),
            (skill.Level, skill.Power, skill.CooldownRounds, skill.InitialCooldownRounds, skill.IsShared));
        var effect = Assert.Single(skill.Effects);
        Assert.Equal(("Damage", power, percent), (effect.Type, effect.Power, effect.AttackPowerPercent));
        Assert.NotEmpty(effect.Summary);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required GameDbContext Db { get; init; }
        public required Character Actor { get; init; }
        public required CombatLoadoutService Service { get; init; }
        public required WriteCounter Writes { get; init; }
        public required int WeaponId { get; init; }
        public CombatLoadoutDefinition Draft(string profession) => new()
        {
            ProfessionCode = profession,
            Weapons = [new() { SlotIndex = WeaponRules.MainSlotIndex, WeaponId = WeaponId }]
        };

        public async Task<string> SnapshotAsync() => JsonSerializer.Serialize(new
        {
            Actors = await Db.Characters.AsNoTracking().ToListAsync(),
            Growth = await Db.CharacterCombatProfessions.AsNoTracking().OrderBy(item => item.ProfessionCode).ToListAsync(),
            Weapons = await Db.CharacterWeapons.AsNoTracking().ToListAsync(),
            Skills = await Db.CharacterSkillSlots.AsNoTracking().ToListAsync()
        });

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var writes = new WriteCounter();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).AddInterceptors(writes).Options);
            await db.Database.EnsureCreatedAsync();
            var user = new User { UserName = "library-player", PasswordHash = "unused" };
            db.Users.Add(user); await db.SaveChangesAsync();
            var actor = new Character { UserId = user.Id, Name = "library-actor", ProfessionCode = "swordsman", Level = 30, Hp = 20 };
            db.Characters.Add(actor); await db.SaveChangesAsync();
            foreach (var (profession, level) in new[] { ("swordsman", 2), ("mage", 9), ("acolyte", 29), ("hunter", 30) })
                db.CharacterCombatProfessions.Add(new() { CharacterId = actor.Id, ProfessionCode = profession, Level = level });
            var weapon = new CharacterWeapon { CharacterId = actor.Id, WeaponCode = "library-weapon", Name = "Test", MaxHp = 50, Attack = 10 };
            db.CharacterWeapons.Add(weapon);
            await db.SaveChangesAsync();
            var professions = new[] { ("swordsman", "骑士"), ("mage", "法师"), ("acolyte", "祭司"), ("hunter", "猎人"), ("rogue", "盗贼") };
            var catalog = new SkillCatalog(Options.Create(new SkillOptions
            {
                Professions = professions.Select(item => new ProfessionOptions
                {
                    Code = item.Item1, Name = item.Item2, StartingSkills = [$"{item.Item1}-1"], SharedSkillCode = $"{item.Item1}-5"
                }).ToList(),
                Abilities = professions.SelectMany(item => Enumerable.Range(1, 5).Select(index => new CombatSkillOptions
                {
                    Code = $"{item.Item1}-{index}", ProfessionCode = item.Item1, Name = $"{item.Item2}技能{index}", Description = "真实配置效果",
                    EffectType = "Damage", Power = 10, AttackPowerPercent = 100, CooldownRounds = 8, InitialCooldownRounds = 2,
                    UnlockLevel = index == 5 ? 10 : index * 2 - 1, Level2UnlockLevel = 15, Level3UnlockLevel = 25,
                    Level2 = new() { Power = 20, AttackPowerPercent = 125, CooldownRounds = 6, InitialCooldownRounds = 1 },
                    Level3 = new() { Power = 30, AttackPowerPercent = 150, CooldownRounds = 4, InitialCooldownRounds = 0 },
                    SharedVersion = new() { Power = 7, AttackPowerPercent = 60, CooldownRounds = 11, InitialCooldownRounds = 5 }
                })).ToList()
            }));
            return new() { Connection = connection, Db = db, Actor = actor, WeaponId = weapon.Id, Writes = writes,
                Service = new(db, catalog, T1WeaponEffectTests.ProductionCatalog(), ConsumableTestFactory.Create(), SoulImprintTestFactory.Create()) };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private sealed class WriteCounter : DbCommandInterceptor
    {
        public int Count { get; set; }
        private void Observe(DbCommand command)
        {
            var sql = command.CommandText.TrimStart();
            if (new[] { "INSERT", "UPDATE", "DELETE" }.Any(prefix => sql.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) Count++;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Observe(command); return ValueTask.FromResult(result); }
    }
}
