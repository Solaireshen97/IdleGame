using Game.Server.Data;
using Game.Shared.Dtos.Professions;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class ProfessionService(GameDbContext db, UserService users, ProfessionCatalog catalog)
{
    public async Task<(ProfessionProgressResponse? Progress, string? Error)> SpendAsync(
        string? token, string professionCode, string nodeCode)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (!ProfessionCatalog.IsValidProfession(professionCode)) return (null, "ProfessionNotFound");
        var node = catalog.FindNode(nodeCode);
        if (node is null || node.ProfessionCode != professionCode) return (null, "TalentNotFound");
        var level = professionCode == ProfessionCatalog.GatheringCode ? character!.GatheringLevel : character!.AlchemyLevel;
        var points = professionCode == ProfessionCatalog.GatheringCode ? character.GatheringTalentPoints : character.AlchemyTalentPoints;
        if (level < node.MinimumLevel) return (null, "ProfessionLevelTooLow");
        if (points < 1) return (null, "TalentPointsExhausted");
        var talents = await db.CharacterProfessionTalents.Where(item =>
            item.CharacterId == character.Id && item.ProfessionCode == professionCode).ToListAsync();
        var current = talents.FirstOrDefault(item => item.NodeCode == node.Code);
        if (current?.Rank >= node.MaxRank) return (null, "TalentAtMaximum");
        if (node.PrerequisiteCode is not null &&
            (talents.FirstOrDefault(item => item.NodeCode == node.PrerequisiteCode)?.Rank ?? 0) < node.PrerequisiteRank)
            return (null, "TalentPrerequisiteMissing");
        if (current is null)
            db.CharacterProfessionTalents.Add(new CharacterProfessionTalent
            {
                CharacterId = character.Id, ProfessionCode = professionCode, NodeCode = node.Code, Rank = 1
            });
        else current.Rank++;
        if (professionCode == ProfessionCatalog.GatheringCode) character.GatheringTalentPoints--;
        else character.AlchemyTalentPoints--;
        character.Version++;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
        return (await catalog.BuildProgressAsync(db, character, professionCode), null);
    }

    public async Task<(ProfessionProgressResponse? Progress, string? Error)> ResetAsync(
        string? token, string professionCode)
    {
        var (_, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (!ProfessionCatalog.IsValidProfession(professionCode)) return (null, "ProfessionNotFound");
        var talents = await db.CharacterProfessionTalents.Where(item =>
            item.CharacterId == character!.Id && item.ProfessionCode == professionCode).ToListAsync();
        var refunded = talents.Sum(item => item.Rank);
        if (refunded == 0) return (await catalog.BuildProgressAsync(db, character!, professionCode), null);
        db.CharacterProfessionTalents.RemoveRange(talents);
        if (professionCode == ProfessionCatalog.GatheringCode) character!.GatheringTalentPoints += refunded;
        else character!.AlchemyTalentPoints += refunded;
        character.Version++;
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
        return (await catalog.BuildProgressAsync(db, character, professionCode), null);
    }
}
