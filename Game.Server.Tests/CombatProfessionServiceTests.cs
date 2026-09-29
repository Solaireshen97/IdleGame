using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Game.Server.Tests;

public sealed class CombatProfessionServiceTests
{
    [Fact]
    public async Task SwitchingPreservesEachProfessionsProgressAndSkillSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GameDbContext>().UseSqlite(connection).Options;
        await using var db = new GameDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { UserName = "player", PasswordHash = "unused" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.UserLoginSessions.Add(new UserLoginSession
        {
            UserId = user.Id, Token = "token", CreatedAt = DateTime.UtcNow,
            ExpireAt = DateTime.UtcNow.AddHours(1)
        });
        var character = new Character
        {
            UserId = user.Id, Name = "hero", ProfessionCode = "knight", Level = 5,
            Experience = 9, Hp = 100, MaxHp = 100, Attack = 20
        };
        db.Characters.Add(character);
        await db.SaveChangesAsync();
        db.CharacterCombatProfessions.Add(new CharacterCombatProfession
        {
            CharacterId = character.Id, ProfessionCode = "knight", Level = 5, Experience = 9
        });
        db.CharacterSkillSlots.Add(new CharacterSkillSlot
        {
            CharacterId = character.Id, SlotIndex = 1, SkillCode = "knight-hit",
            AutoUseEnabled = true, AutoConditionOverride = "Always", AutoHpThresholdPercent = 42
        });
        await db.SaveChangesAsync();

        var skills = new SkillCatalog(Options.Create(new SkillOptions
        {
            Professions =
            [
                new ProfessionOptions { Code = "knight", Name = "骑士", StartingSkills = ["knight-hit"] },
                new ProfessionOptions { Code = "cleric", Name = "牧师", StartingSkills = ["cleric-heal"] }
            ],
            Abilities =
            [
                new CombatSkillOptions { Code = "knight-hit", ProfessionCode = "knight", Name = "斩击",
                    Description = "伤害", EffectType = "Damage", Power = 10, UnlockLevel = 1 },
                new CombatSkillOptions { Code = "cleric-heal", ProfessionCode = "cleric", Name = "治疗",
                    Description = "治疗", EffectType = "Heal", Power = 10, UnlockLevel = 1 }
            ]
        }));
        var progression = new ProgressionService(Options.Create(new ProgressionOptions
        {
            MaximumLevel = 30, ExperienceToNextLevel = Enumerable.Repeat(10, 29).ToList()
        }));
        var service = new CombatProfessionService(db, new UserService(db, progression, skills), skills, progression);
        var weaponCount = await db.CharacterWeapons.CountAsync();

        var switched = await service.SwitchAsync("token", character.Id,
            new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
        Assert.Null(switched.Error);
        Assert.Equal(("cleric", 1, 0), (character.ProfessionCode, character.Level, character.Experience));
        Assert.Equal("cleric-heal", (await db.CharacterSkillSlots.SingleAsync(slot => slot.SlotIndex == 1)).SkillCode);

        character.Level = 3;
        character.Experience = 4;
        var returned = await service.SwitchAsync("token", character.Id,
            new SwitchCombatProfessionRequest { ProfessionCode = "knight" });
        Assert.Null(returned.Error);
        Assert.Equal(("knight", 5, 9), (character.ProfessionCode, character.Level, character.Experience));
        var knightSlot = await db.CharacterSkillSlots.SingleAsync(slot => slot.SlotIndex == 1);
        Assert.Equal("knight-hit", knightSlot.SkillCode);
        Assert.True(knightSlot.AutoUseEnabled);
        Assert.Equal("Always", knightSlot.AutoConditionOverride);
        Assert.Equal(42, knightSlot.AutoHpThresholdPercent);
        Assert.Equal(3, (await db.CharacterCombatProfessions.FindAsync(character.Id, "cleric"))!.Level);
        Assert.Equal(weaponCount, await db.CharacterWeapons.CountAsync());

        db.CharacterActivities.Add(new CharacterActivity
        {
            CharacterId = character.Id, Kind = "Gathering", SourceId = 1,
            StartedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var blocked = await service.SwitchAsync("token", character.Id,
            new SwitchCombatProfessionRequest { ProfessionCode = "cleric" });
        Assert.Equal("CharacterBusy", blocked.Error);
        Assert.Equal("knight", character.ProfessionCode);
    }
}
