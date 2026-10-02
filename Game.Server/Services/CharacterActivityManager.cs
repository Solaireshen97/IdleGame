using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public static class CharacterActivityManager
{
    public const string BattleKind = "Battle";
    public const string GatheringKind = "Gathering";
    public const string ProductionKind = "Production";

    public static async Task<bool> IsBusyAsync(GameDbContext db, int characterId) =>
        await db.CharacterActivities.AnyAsync(activity => activity.CharacterId == characterId) ||
        await db.RoomSlots.AnyAsync(slot => slot.CharacterId == characterId);

    public static void StartBattle(GameDbContext db, int characterId, Room room, DateTime now) =>
        db.CharacterActivities.Add(new CharacterActivity
        {
            CharacterId = characterId,
            Kind = BattleKind,
            SourceId = room.Id,
            StartedAtUtc = now,
            EndsAtUtc = room.ExpiresAtUtc
        });

    public static void StartGathering(GameDbContext db, GatheringTask task) =>
        db.CharacterActivities.Add(new CharacterActivity
        {
            CharacterId = task.CharacterId,
            Kind = GatheringKind,
            SourceId = task.Id,
            StartedAtUtc = task.StartedAtUtc,
            EndsAtUtc = task.EndsAtUtc
        });

    public static void StartProduction(GameDbContext db, ProductionTask task) =>
        db.CharacterActivities.Add(new CharacterActivity
        {
            CharacterId = task.CharacterId,
            Kind = ProductionKind,
            SourceId = task.Id,
            StartedAtUtc = task.StartedAtUtc,
            EndsAtUtc = task.EndsAtUtc
        });

    public static async Task ReleaseBattleAsync(GameDbContext db, int characterId, int roomId)
    {
        var activity = await db.CharacterActivities.FindAsync(characterId);
        if (activity is { Kind: BattleKind } && activity.SourceId == roomId)
            db.CharacterActivities.Remove(activity);
    }

    public static async Task CloseBattleRoomAsync(GameDbContext db, Room room, DateTime now)
    {
        if (room.ClosedAtUtc.HasValue) return;
        room.ClosedAtUtc = now;
        room.IsOwnerAutoEnabled = false;
        room.Status = RoomStatus.BattleOver;
        room.NextRoundAvailableAtUtc = null;
        room.RoundCooldownDurationSeconds = null;
        room.PreparationStartedAtUtc = null;
        var slots = await db.RoomSlots.Where(slot => slot.RoomId == room.Id).ToListAsync();
        foreach (var slot in slots)
        {
            if (slot.CharacterId is { } id && db.Characters.Local.FirstOrDefault(c => c.Id == id) is { } character)
                character.BattleMaxHpLimit = null;
            slot.CharacterId = null;
            slot.IsConfirmed = false;
            slot.IsAutoEnabled = false;
            slot.IsTemporaryAuto = false;
            slot.PendingConsumableSlotMask = 0;
            SkillQueueRules.Clear(slot);
            slot.IsSoulImprintQueued = false;
            slot.HasParticipatedInRun = false;
            slot.LastParticipatedMonsterId = null;
        }
        db.CharacterActivities.RemoveRange(await db.CharacterActivities
            .Where(activity => activity.Kind == BattleKind && activity.SourceId == room.Id).ToListAsync());
        db.BattleStatusEffects.RemoveRange(await db.BattleStatusEffects.Where(effect => effect.RoomId == room.Id).ToListAsync());
    }
}
