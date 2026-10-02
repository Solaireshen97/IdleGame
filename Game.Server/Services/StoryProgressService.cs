using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

// These operations join the caller's unit of work. Never save or publish before its commit.
public static class StoryProgressService
{
    public static StoryQuestDefinition Definition(StoryQuestProgress quest) =>
        JsonSerializer.Deserialize<StoryQuestDefinition>(quest.DefinitionJson)
        ?? throw new InvalidOperationException("Missing story definition snapshot.");

    public static async Task<(UserStoryState? State, StoryQuestProgress? Quest)> CurrentAsync(GameDbContext db, int userId)
    {
        var state = db.UserStoryStates.Local.FirstOrDefault(x => x.UserId == userId)
            ?? await db.UserStoryStates.FindAsync(userId);
        if (state?.CurrentQuestCode is not { } code) return (state, null);
        var quest = db.StoryQuestProgress.Local.FirstOrDefault(x => x.UserId == userId && x.QuestCode == code)
            ?? await db.StoryQuestProgress.FindAsync(userId, code);
        return (state, quest);
    }

    public static async Task RecordAsync(GameDbContext db, int characterId, string kind, string targetCode,
        string sourceEventId, int quantity, DateTime now)
    {
        if (quantity <= 0 || string.IsNullOrWhiteSpace(sourceEventId)) return;
        var character = db.Characters.Local.FirstOrDefault(x => x.Id == characterId) ?? await db.Characters.FindAsync(characterId);
        if (character is null) return;
        var (state, quest) = await CurrentAsync(db, character.UserId);
        if (state is null || state.IsLegacy || quest is null || quest.Status != "Active" || now < quest.ActivatedAtUtc) return;
        var definition = Definition(quest);
        if (definition.ActorPolicy == "TutorialCharacter" && quest.ActorCharacterId != characterId) return;
        var expectedKind = definition.ObjectiveType == "ProduceAndEquipPotion" ? "Produce" : definition.ObjectiveType;
        if (kind != expectedKind || (definition.TargetCode.Length > 0 && definition.TargetCode != targetCode)) return;
        if (db.StoryEventReceipts.Local.Any(x => x.UserId == character.UserId && x.QuestCode == quest.QuestCode && x.SourceEventId == sourceEventId)
            || await db.StoryEventReceipts.AnyAsync(x => x.UserId == character.UserId && x.QuestCode == quest.QuestCode && x.SourceEventId == sourceEventId)) return;
        db.StoryEventReceipts.Add(new StoryEventReceipt { UserId = character.UserId, QuestCode = quest.QuestCode,
            SourceEventId = sourceEventId, OccurredAtUtc = now });
        quest.ActorCharacterId ??= characterId;
        quest.Progress = (int)Math.Min(definition.RequiredCount, (long)quest.Progress + quantity);
        quest.Version++; state.Version++;
        if (definition.ObjectiveType != "ProduceAndEquipPotion" && quest.Progress >= definition.RequiredCount)
            Complete(state, quest, now);
        await RefreshAsync(db, characterId, now);
    }

    public static async Task RefreshAsync(GameDbContext db, int characterId, DateTime now)
    {
        var character = db.Characters.Local.FirstOrDefault(x => x.Id == characterId) ?? await db.Characters.FindAsync(characterId);
        if (character is null || db.Entry(character).State == EntityState.Deleted) return;
        var (state, quest) = await CurrentAsync(db, character.UserId);
        if (state is null || quest is null || quest.Status != "Active") return;
        var definition = Definition(quest);
        if (definition.ActorPolicy == "TutorialCharacter" && quest.ActorCharacterId != characterId) return;
        if (state.IsLegacy || definition.ObjectiveType == "Dialogue")
        {
            quest.ActorCharacterId ??= characterId;
            quest.Progress = definition.RequiredCount;
            Complete(state, quest, now);
            return;
        }
        var satisfied = false;
        if (definition.ObjectiveType is "EquipLootWeapon" or "WeaponSkillLevel")
        {
            await db.CharacterWeapons.Include(x => x.Skills).Where(x => x.CharacterId == characterId).LoadAsync();
            var weapons = db.CharacterWeapons.Local.Where(x => x.CharacterId == characterId && db.Entry(x).State != EntityState.Deleted);
            satisfied = definition.ObjectiveType == "EquipLootWeapon"
                ? weapons.Count(x => x.EquippedSlotIndex.HasValue && x.Origin is WeaponOrigin.Drop or WeaponOrigin.Tutorial) >= definition.RequiredCount
                : weapons.Any(x => x.Skills.Any(s => db.Entry(s).State != EntityState.Deleted && s.Level >= definition.RequiredCount));
        }
        else if (definition.ObjectiveType == "ProduceAndEquipPotion" && quest.Progress >= definition.RequiredCount)
        {
            await db.CharacterConsumableSlots.Where(x => x.CharacterId == characterId).LoadAsync();
            await db.CharacterItemStacks.Where(x => x.CharacterId == characterId && x.ItemCode == definition.TargetCode).LoadAsync();
            satisfied = db.CharacterConsumableSlots.Local.Any(x => x.CharacterId == characterId &&
                x.SlotIndex == ConsumableRules.HealingPotionSlotIndex && x.ItemCode == definition.TargetCode && db.Entry(x).State != EntityState.Deleted)
                && db.CharacterItemStacks.Local.Any(x => x.CharacterId == characterId && x.ItemCode == definition.TargetCode &&
                    x.Quantity > 0 && db.Entry(x).State != EntityState.Deleted);
        }
        if (!satisfied) return;
        quest.ActorCharacterId ??= characterId;
        quest.Progress = definition.RequiredCount;
        Complete(state, quest, now);
    }

    public static void Complete(UserStoryState state, StoryQuestProgress quest, DateTime now)
    {
        if (quest.Status != "Active") return;
        quest.Status = "ReadyToTurnIn"; quest.CompletedAtUtc = now;
        quest.Version++; state.Version++;
    }
}
