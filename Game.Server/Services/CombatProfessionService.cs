using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class CombatProfessionService(GameDbContext db, UserService users, SkillCatalog skills,
    ProgressionService progression)
{
    private readonly CombatProfessionProgressStore _progress = new(db);
    public async Task<(CharacterCombatProfessionsResponse? Response, string? Error)> GetAsync(string? token, int characterId)
    {
        var (character, error) = await OwnedCharacterAsync(token, characterId);
        return error is null ? (await BuildAsync(character!), null) : (null, error);
    }

    public async Task<(CharacterCombatProfessionsResponse? Response, string? Error)> SwitchAsync(
        string? token, int characterId, SwitchCombatProfessionRequest request)
    {
        var (character, error) = await OwnedCharacterAsync(token, characterId);
        if (error is not null) return (null, error);
        var target = skills.BaseProfessions.FirstOrDefault(item =>
            string.Equals(item.Code, request.ProfessionCode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (target is null) return (null, "InvalidProfession");
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await CharacterActivityManager.IsBusyAsync(db, characterId)) return (null, "CharacterBusy");
        if (string.Equals(character!.ProfessionCode, target.Code, StringComparison.OrdinalIgnoreCase))
            return (await BuildAsync(character), null);

        try
        {
            var current = await _progress.CaptureActiveAsync(character);
            CombatSkillLoadoutCodec.EnsureSupportedVersion(current.SkillLoadoutJson);
            var slots = await db.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync();
            current.SkillLoadoutJson = CombatSkillLoadoutCodec.Capture(slots);
            var next = await _progress.RestoreAsync(character, target.Code);
            character.Version++;
            var levels = await _progress.ReadLevelsAsync(characterId);
            levels[current.ProfessionCode] = current.Level;
            levels[target.Code] = character.Level;
            var saved = CombatSkillLoadoutCodec.Restore(next.SkillLoadoutJson, character, skills, levels);
            foreach (var index in Enumerable.Range(1, SkillRules.SlotCount))
            {
                var source = saved.FirstOrDefault(slot => slot.SlotIndex == index);
                var slot = slots.FirstOrDefault(slot => slot.SlotIndex == index);
                if (slot is null)
                {
                    slot = new CharacterSkillSlot { CharacterId = characterId, SlotIndex = index };
                    db.CharacterSkillSlots.Add(slot);
                }
                else slot.Version++;
                slot.SkillCode = source?.SkillCode;
                slot.AutoUseEnabled = source?.AutoUseEnabled ?? false;
                slot.AutoConditionOverride = source?.AutoConditionOverride;
                slot.AutoHpThresholdPercent = source?.AutoHpThresholdPercent ?? SkillRules.DefaultAutoHpThresholdPercent;
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return (await BuildAsync(character), null);
        }
        catch (UnsupportedCombatSkillLoadoutVersionException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return (null, "UnsupportedSkillLoadoutVersion");
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
    }

    private async Task<(Character? Character, string? Error)> OwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await users.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        return await new CharacterAccessResolver(db).OwnedAsync(user!, characterId);
    }

    private async Task<CharacterCombatProfessionsResponse> BuildAsync(Character character)
    {
        var records = await db.CharacterCombatProfessions.AsNoTracking()
            .Where(item => item.CharacterId == character.Id).ToDictionaryAsync(item => item.ProfessionCode);
        return new CharacterCombatProfessionsResponse
        {
            CharacterId = character.Id,
            ActiveProfessionCode = character.ProfessionCode,
            CanSwitch = !await CharacterActivityManager.IsBusyAsync(db, character.Id),
            Professions = skills.BaseProfessions.Select(profession =>
            {
                var active = string.Equals(character.ProfessionCode, profession.Code, StringComparison.OrdinalIgnoreCase);
                records.TryGetValue(profession.Code, out var record);
                var level = active ? character.Level : record?.Level ?? 1;
                return new CharacterCombatProfessionResponse
                {
                    Code = profession.Code, Name = profession.Name, Description = profession.Description,
                    Level = level, Experience = active ? character.Experience : record?.Experience ?? 0,
                    ExperienceToNextLevel = progression.GetExperienceToNextLevel(level),
                    IsActive = active, IsSharedSkillUnlocked = level >= 30
                };
            }).ToList()
        };
    }
}
