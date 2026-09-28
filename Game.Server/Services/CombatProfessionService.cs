using System.Text.Json;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos.Characters;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class CombatProfessionService(GameDbContext db, UserService users, SkillCatalog skills,
    ProgressionService progression)
{
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
        if (await CharacterActivityManager.IsBusyAsync(db, characterId)) return (null, "CharacterBusy");
        if (string.Equals(character!.ProfessionCode, target.Code, StringComparison.OrdinalIgnoreCase))
            return (await BuildAsync(character), null);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var current = await db.CharacterCombatProfessions.FindAsync(characterId, character.ProfessionCode);
        if (current is null)
            db.CharacterCombatProfessions.Add(current = new CharacterCombatProfession
            {
                CharacterId = characterId, ProfessionCode = character.ProfessionCode
            });
        var slots = await db.CharacterSkillSlots.Where(slot => slot.CharacterId == characterId).ToListAsync();
        current.Level = character.Level;
        current.Experience = character.Experience;
        current.SkillLoadoutJson = JsonSerializer.Serialize(slots.Select(slot => new SavedSkillSlot(
            slot.SlotIndex, slot.SkillCode, slot.AutoUseEnabled, slot.AutoConditionOverride,
            slot.AutoHpThresholdPercent)).ToList());

        var next = await db.CharacterCombatProfessions.FindAsync(characterId, target.Code);
        if (next is null)
            db.CharacterCombatProfessions.Add(next = new CharacterCombatProfession
            {
                CharacterId = characterId, ProfessionCode = target.Code
            });
        character.ProfessionCode = target.Code;
        character.AdvancedProfessionCode = null;
        character.Level = next.Level;
        character.Experience = next.Experience;
        character.Version++;
        var saved = next.SkillLoadoutJson is null
            ? skills.SkillsForProfessionAtLevel(target.Code, next.Level).Select((skill, index) => new SavedSkillSlot(index + 1, skill.Code, false,
                null, SkillRules.DefaultAutoHpThresholdPercent)).ToList()
            : JsonSerializer.Deserialize<List<SavedSkillSlot>>(next.SkillLoadoutJson) ?? [];
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

    private async Task<(Character? Character, string? Error)> OwnedCharacterAsync(string? token, int characterId)
    {
        var (user, error) = await users.GetCurrentUserEntityAsync(token);
        if (error is not null) return (null, error);
        var character = await db.Characters.FirstOrDefaultAsync(item => item.Id == characterId);
        if (character is null) return (null, "CharacterNotFound");
        return character.UserId == user!.Id ? (character, null) : (null, "NotOwner");
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

    private sealed record SavedSkillSlot(int SlotIndex, string? SkillCode, bool AutoUseEnabled,
        string? AutoConditionOverride, int AutoHpThresholdPercent);
}
