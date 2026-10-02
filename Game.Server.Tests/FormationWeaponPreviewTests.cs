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
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class FormationWeaponPreviewTests
{
    [Fact]
    public async Task PreviewUsesDraftMainElementAndOnlyDraftWeaponsForStatsAndSkills()
    {
        await using var fixture = await Fixture.CreateAsync();
        var draft = fixture.Draft();
        var preview = await fixture.Loadouts.PreviewAsync(fixture.Character, draft);
        AssertFirePreview(preview);
        AssertMainWeapon(preview, fixture.FireMain);

        // The same three weapons now have a water main: raw stats stay identical,
        // but fire skills deactivate and the water health skill activates.
        (draft.Weapons[0].SlotIndex, draft.Weapons[2].SlotIndex) = (3, 1);
        var water = await fixture.Loadouts.PreviewAsync(fixture.Character, draft);
        Assert.True(water.CanDeploy);
        Assert.Equal(ElementType.Water, water.MainElement);
        AssertMainWeapon(water, fixture.WaterSub);
        Assert.Equal((100, 600, 100, 780), (water.WeaponAttack, water.WeaponMaxHp, water.Attack, water.MaxHp));
        Assert.Equal((0m, 30m, 0m), (water.WeaponAttackBonusPercent, water.WeaponHealthBonusPercent, water.WeaponCriticalChancePercent));
        Assert.Equal("weapon-health", Assert.Single(water.WeaponSkills).SkillCode);
        Assert.Equal(WeaponSkillEffectType.MaxHpPercent, Assert.Single(water.WeaponEffects).EffectType);
    }

    [Fact]
    public async Task PreviewDoesNotMutateTrackedOrPersistedCharacterWeaponsGrowthOrVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var actorBefore = JsonSerializer.Serialize(fixture.Character);
        var persistedActorBefore = JsonSerializer.Serialize(await fixture.Db.Characters.AsNoTracking().SingleAsync());
        var equipmentBefore = await fixture.EquipmentSnapshotAsync();
        var growthBefore = JsonSerializer.Serialize(await fixture.Db.CharacterCombatProfessions.AsNoTracking().ToListAsync());

        var preview = await fixture.Loadouts.PreviewAsync(fixture.Character, fixture.Draft());
        AssertFirePreview(preview);
        AssertMainWeapon(preview, fixture.FireMain);
        Assert.Equal(12, preview.Level); // Draft cleric growth, while the real actor remains a knight.
        Assert.Equal(actorBefore, JsonSerializer.Serialize(fixture.Character));
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(persistedActorBefore, JsonSerializer.Serialize(await fixture.Db.Characters.AsNoTracking().SingleAsync()));
        Assert.Equal(equipmentBefore, await fixture.EquipmentSnapshotAsync());
        Assert.Equal(growthBefore, JsonSerializer.Serialize(await fixture.Db.CharacterCombatProfessions.AsNoTracking().ToListAsync()));
    }

    [Fact]
    public async Task SavedPreviewSurvivesReadbackAndMatchesAppliedWeaponResponse()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Formations.CreateAsync("token", fixture.Character.Id,
            new CreateFormationRequest { Name = "Existing water", GroupElement = ElementType.Water, Position = 1, FromCurrent = true });
        Assert.Null(created.Error);
        AssertMainWeapon(created.Response!.Preview, fixture.OldMain);
        var saved = await fixture.Formations.SaveAsync("token", fixture.Character.Id, created.Response!.Id,
            new SaveFormationRequest { Name = "Draft fire", GroupElement = ElementType.Fire, Position = 1,
                ExpectedVersion = created.Response.Version, Loadout = fixture.Draft() });
        Assert.Null(saved.Error);
        AssertFirePreview(saved.Response!.Preview);
        AssertMainWeapon(saved.Response.Preview, fixture.FireMain);

        fixture.Db.ChangeTracker.Clear();
        var read = await fixture.Formations.GetAsync("token", fixture.Character.Id);
        Assert.Null(read.Error);
        var formation = Assert.Single(read.Response!.Formations, f => f.Id == saved.Response.Id);
        AssertFirePreview(formation.Preview);
        AssertMainWeapon(formation.Preview, fixture.FireMain);
        Assert.Equal(JsonSerializer.Serialize(saved.Response.Preview), JsonSerializer.Serialize(formation.Preview));
        var applied = await fixture.Formations.ApplyAsync("token", fixture.Character.Id, formation.Id,
            new ApplyFormationRequest { ExpectedVersion = formation.Version,
                ExpectedCharacterVersion = read.Response.CharacterVersion, RequestId = "weapon-preview-apply" });
        Assert.Null(applied.Error);
        var actual = await fixture.Weapons.GetAsync("token", fixture.Character.Id);
        Assert.Null(actual.Error);
        var response = actual.Response!;
        var preview = formation.Preview;
        Assert.Equal((preview.WeaponAttack, preview.WeaponMaxHp, preview.Attack, preview.MaxHp),
            (response.TotalAttack, response.TotalMaxHp, response.EffectiveAttack, response.EffectiveMaxHp));
        Assert.Equal((preview.MainElement, preview.WeaponAttackBonusPercent, preview.WeaponHealthBonusPercent, preview.WeaponCriticalChancePercent),
            (response.MainElement, response.AttackBonusPercent, response.HealthBonusPercent, response.CriticalChancePercent));
        Assert.Equal(JsonSerializer.Serialize(preview.WeaponSkills), JsonSerializer.Serialize(response.ActiveSkills));
        Assert.Equal(JsonSerializer.Serialize(preview.WeaponEffects), JsonSerializer.Serialize(response.ActiveEffects));
        Assert.Equal(17, response.Hp); // Applying a healthier loadout must not heal the actor.
        Assert.Equal(fixture.FireMain.Id, Assert.Single(response.Weapons, w => w.EquippedSlotIndex == 1).Id);
        Assert.Null(response.Weapons.Single(w => w.Id == fixture.OldMain.Id).EquippedSlotIndex);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("foreign")]
    public async Task PreviewWithoutOwnedMainNeverFallsBackToCurrentlyEquippedWeapon(string choice)
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = new Character { UserId = fixture.Character.UserId, Name = "other-actor", ProfessionCode = "knight", Level = 1 };
        fixture.Db.Characters.Add(other);
        await fixture.Db.SaveChangesAsync();
        var foreign = new CharacterWeapon { CharacterId = other.Id, WeaponCode = "foreign-main", Name = "Other character weapon", Element = ElementType.Light, MaxHp = 1 };
        fixture.Db.CharacterWeapons.Add(foreign);
        await fixture.Db.SaveChangesAsync();
        var before = await fixture.EquipmentSnapshotAsync();
        var actorBefore = JsonSerializer.Serialize(fixture.Character);
        var draft = fixture.Draft();
        draft.Weapons.Single(w => w.SlotIndex == WeaponRules.MainSlotIndex).WeaponId = choice switch
        {
            "empty" => null,
            "missing" => int.MaxValue,
            _ => foreign.Id
        };

        var preview = await fixture.Loadouts.PreviewAsync(fixture.Character, draft);

        Assert.False(preview.CanDeploy);
        Assert.Null(preview.MainWeapon);
        Assert.Null(preview.MainElement);
        Assert.Contains(preview.Issues, issue => issue.Code == "MainWeaponRequired");
        if (choice != "empty") Assert.Contains(preview.Issues, issue => issue.Code == "WeaponNotOwned");
        Assert.Equal(actorBefore, JsonSerializer.Serialize(fixture.Character));
        Assert.False(fixture.Db.ChangeTracker.HasChanges());
        Assert.Equal(before, await fixture.EquipmentSnapshotAsync());
    }

    private static void AssertMainWeapon(FormationPreviewResponse preview, CharacterWeapon expected)
    {
        var main = Assert.IsType<FormationMainWeaponResponse>(preview.MainWeapon);
        Assert.Equal((expected.Id, expected.WeaponCode, expected.Name, expected.Element, expected.QualityRank),
            (main.WeaponId, main.Code, main.Name, main.Element, main.QualityRank));
    }

    private static void AssertFirePreview(FormationPreviewResponse preview)
    {
        Assert.True(preview.CanDeploy);
        Assert.Equal(ElementType.Fire, preview.MainElement);
        // Production rates: attack Lv10 = 25%; might Lv10 = 15% attack + 20% HP;
        // critical Lv10 = 15%. Off-element weapons contribute raw stats, not skills.
        Assert.Equal((100, 600, 100, 720), (preview.WeaponAttack, preview.WeaponMaxHp, preview.Attack, preview.MaxHp));
        Assert.Equal((40m, 20m, 15m), (preview.WeaponAttackBonusPercent, preview.WeaponHealthBonusPercent, preview.WeaponCriticalChancePercent));
        Assert.Equal(new[] { "weapon-attack", "weapon-critical", "weapon-might" }, preview.WeaponSkills.Select(s => s.SkillCode));
        Assert.All(preview.WeaponSkills, skill => { Assert.Equal(10, skill.Level); Assert.NotEmpty(skill.Name); Assert.NotEmpty(skill.Description); });
        Assert.Equal(3, preview.WeaponEffects.Count);
        Assert.Equal(40m, preview.WeaponEffects.Single(e => e.EffectType == WeaponSkillEffectType.AttackPercent).TotalPercent);
        Assert.Equal(20m, preview.WeaponEffects.Single(e => e.EffectType == WeaponSkillEffectType.MaxHpPercent).TotalPercent);
        Assert.Equal(15m, preview.WeaponEffects.Single(e => e.EffectType == WeaponSkillEffectType.CriticalChancePercent).TotalPercent);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required GameDbContext Db { get; init; }
        public required Character Character { get; init; }
        public required CharacterWeapon OldMain { get; init; }
        public required CharacterWeapon FireMain { get; init; }
        public required CharacterWeapon FireSub { get; init; }
        public required CharacterWeapon WaterSub { get; init; }
        public required CombatLoadoutService Loadouts { get; init; }
        public required FormationService Formations { get; init; }
        public required WeaponService Weapons { get; init; }

        public CombatLoadoutDefinition Draft() => new() { ProfessionCode = "cleric", Weapons =
            [new() { SlotIndex = 1, WeaponId = FireMain.Id }, new() { SlotIndex = 2, WeaponId = FireSub.Id }, new() { SlotIndex = 3, WeaponId = WaterSub.Id }] };

        public async Task<string> EquipmentSnapshotAsync() => JsonSerializer.Serialize(await Db.CharacterWeapons.AsNoTracking()
            .Include(w => w.Skills).OrderBy(w => w.Id).ToListAsync());

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var user = new User { UserName = "preview-player", PasswordHash = "unused" };
            db.Users.Add(user); await db.SaveChangesAsync();
            db.UserLoginSessions.Add(new() { UserId = user.Id, Token = "token", CreatedAt = DateTime.UtcNow, ExpireAt = DateTime.UtcNow.AddHours(1) });
            var actor = new Character { UserId = user.Id, Name = "preview-actor", ProfessionCode = "knight", Level = 8, Experience = 9, Hp = 17, Version = 7 };
            db.Characters.Add(actor); await db.SaveChangesAsync();
            db.CharacterCombatProfessions.Add(new() { CharacterId = actor.Id, ProfessionCode = "cleric", Level = 12, Experience = 4 });
            CharacterWeapon Make(string code, ElementType element, int attack, int hp, int? slot, params string[] skillCodes) => new()
            {
                CharacterId = actor.Id, WeaponCode = code, Name = code, Element = element, Attack = attack, MaxHp = hp,
                EquippedSlotIndex = slot, Version = 3,
                Skills = skillCodes.Select((skill, index) => new CharacterWeaponSkill { SlotIndex = index + 1, SkillCode = skill, Level = 10, BaseLevel = 10 }).ToList()
            };
            var old = Make("preview-old", ElementType.Water, 1000, 2000, 1, "weapon-attack");
            var fire = Make("preview-fire", ElementType.Fire, 20, 100, null, "weapon-attack");
            fire.Name = "预设火焰主武器";
            fire.QualityRank = 2;
            var sub = Make("preview-might", ElementType.Fire, 30, 200, null, "weapon-might", "weapon-critical");
            var water = Make("preview-water", ElementType.Water, 50, 300, null, "weapon-health");
            var unused = Make("preview-unused", ElementType.Fire, 500, 1000, null, "weapon-attack");
            db.CharacterWeapons.AddRange(old, fire, sub, water, unused);
            var catalog = T1WeaponEffectTests.ProductionCatalog();
            catalog.RecalculateEquipmentStats(actor, [old, fire, sub, water, unused]);
            await db.SaveChangesAsync();
            var skills = new SkillCatalog(Options.Create(new SkillOptions
            {
                Professions = [new() { Code = "knight", Name = "骑士", StartingSkills = ["knight-hit"] },
                    new() { Code = "cleric", Name = "牧师", StartingSkills = ["cleric-heal"] }],
                Abilities = [new() { Code = "knight-hit", ProfessionCode = "knight", Name = "斩击", Description = "伤害", EffectType = "Damage", Power = 10, UnlockLevel = 1 },
                    new() { Code = "cleric-heal", ProfessionCode = "cleric", Name = "治疗", Description = "治疗", EffectType = "Heal", Power = 10, UnlockLevel = 1 }]
            }));
            var progression = new ProgressionService(Options.Create(new ProgressionOptions { MaximumLevel = 30, ExperienceToNextLevel = Enumerable.Repeat(10, 29).ToList() }));
            var users = new UserService(db, progression, skills);
            var loadouts = new CombatLoadoutService(db, skills, catalog, ConsumableTestFactory.Create(), SoulImprintTestFactory.Create());
            return new() { Connection = connection, Db = db, Character = actor, OldMain = old, FireMain = fire, FireSub = sub, WaterSub = water,
                Loadouts = loadouts, Formations = new(db, users, loadouts, Options.Create(new FormationOptions())), Weapons = new(db, users, skills, catalog) };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }
}
