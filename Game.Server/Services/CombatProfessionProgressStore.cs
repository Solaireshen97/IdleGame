using Game.Server.Data;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

// Character remains the active profession's authority; archived professions are restored on switching.
// Callers own SaveChanges and the transaction so progress commits with rewards or the loadout.
public sealed class CombatProfessionProgressStore(GameDbContext db)
{
    public async Task<CharacterCombatProfession> CaptureActiveAsync(Character character)
    {
        var record = await GetOrCreateAsync(character.Id, character.ProfessionCode);
        record.Level = character.Level;
        record.Experience = character.Experience;
        return record;
    }

    public async Task<CharacterCombatProfession> RestoreAsync(Character character, string professionCode)
    {
        var record = await GetOrCreateAsync(character.Id, professionCode);
        character.ProfessionCode = professionCode;
        character.AdvancedProfessionCode = null;
        character.Level = record.Level;
        character.Experience = record.Experience;
        return record;
    }

    public async Task<ProgressionGain> AwardExperienceAsync(Character character, int experience, ProgressionService progression)
    {
        var gain = progression.AwardExperience(character, experience);
        if (gain.ExperienceGained > 0) character.Version++;
        await CaptureActiveAsync(character);
        return gain;
    }

    public async Task<Dictionary<string, int>> ReadLevelsAsync(int characterId) =>
        (await db.CharacterCombatProfessions.AsNoTracking().Where(item => item.CharacterId == characterId).ToListAsync())
        .ToDictionary(item => item.ProfessionCode, item => item.Level, StringComparer.OrdinalIgnoreCase);

    private async Task<CharacterCombatProfession> GetOrCreateAsync(int characterId, string professionCode)
    {
        var record = await db.CharacterCombatProfessions.FindAsync(characterId, professionCode);
        if (record is not null) return record;
        record = new CharacterCombatProfession { CharacterId = characterId, ProfessionCode = professionCode };
        db.CharacterCombatProfessions.Add(record);
        return record;
    }
}
