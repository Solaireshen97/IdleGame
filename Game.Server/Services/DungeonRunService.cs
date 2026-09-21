using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class DungeonRunService(GameDbContext dbContext, RewardService rewardService)
{
    public async Task<(Monster ActiveMonster, bool IsDungeonComplete, string? Error)> AdvanceAfterDefeatAsync(
        Room room, Monster defeatedMonster, IReadOnlyCollection<RewardParticipant> participants,
        DateTime now, List<string> logs)
    {
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        if (dungeon is null) return (defeatedMonster, false, "DungeonNotFound");

        var eventKey = defeatedMonster.RoomId.HasValue
            ? $"monster:{defeatedMonster.WaveNumber}:{defeatedMonster.Position}"
            : "monster:1";
        await rewardService.RecordAsync(room, dungeon.Code, participants, eventKey, false);
        logs.Add($"{defeatedMonster.Name} is defeated.");

        var nextMonster = defeatedMonster.RoomId.HasValue
            ? await dbContext.Monsters.Where(monster => monster.RoomId == room.Id &&
                    (monster.WaveNumber > defeatedMonster.WaveNumber ||
                     monster.WaveNumber == defeatedMonster.WaveNumber && monster.Position > defeatedMonster.Position))
                .OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).FirstOrDefaultAsync()
            : null;
        if (nextMonster is not null)
        {
            var changedWave = nextMonster.WaveNumber != defeatedMonster.WaveNumber;
            room.MonsterId = nextMonster.Id;
            room.CurrentWaveNumber = nextMonster.WaveNumber;
            room.Status = RoomStatus.WaveTransition;
            room.NextRoundAvailableAtUtc = now.AddSeconds(BattleRules.WaveTransitionSeconds);
            room.RoundCooldownDurationSeconds = BattleRules.WaveTransitionSeconds;
            room.PreparationStartedAtUtc = null;
            room.BattleEndedAtUtc = null;
            logs.Add(changedWave
                ? $"Wave {defeatedMonster.WaveNumber} cleared. Wave {nextMonster.WaveNumber} begins shortly."
                : $"The next enemy in wave {nextMonster.WaveNumber} approaches.");
            return (nextMonster, false, null);
        }

        SetBattleOver(room, now);
        await RecordDungeonClearsAsync(room.DungeonId,
            participants.Select(participant => participant.UserId), now);
        await rewardService.RecordAsync(room, dungeon.Code, participants, "clear", true);
        await rewardService.SettleAsync(room, true, now, logs);
        logs.Add("Dungeon cleared.");
        return (defeatedMonster, true, null);
    }

    public async Task<Monster> ResetEncounterAsync(Room room)
    {
        var monsters = await dbContext.Monsters.Where(monster => monster.RoomId == room.Id)
            .OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).ToListAsync();
        if (monsters.Count == 0)
        {
            var legacyMonster = await dbContext.Monsters.FindAsync(room.MonsterId)
                ?? throw new InvalidOperationException($"Room {room.Id} has no active monster.");
            legacyMonster.Hp = legacyMonster.MaxHp;
            room.CurrentWaveNumber = 1;
            room.TotalWaveCount = 1;
            return legacyMonster;
        }

        foreach (var monster in monsters) monster.Hp = monster.MaxHp;
        var first = monsters[0];
        room.MonsterId = first.Id;
        room.CurrentWaveNumber = first.WaveNumber;
        room.TotalWaveCount = monsters.Max(monster => monster.WaveNumber);
        return first;
    }

    private async Task RecordDungeonClearsAsync(int dungeonId, IEnumerable<int> userIds, DateTime clearedAtUtc)
    {
        var ids = userIds.Distinct().ToList();
        var existingIds = await dbContext.UserDungeonClears
            .Where(clear => clear.DungeonId == dungeonId && ids.Contains(clear.UserId))
            .Select(clear => clear.UserId).ToListAsync();
        dbContext.UserDungeonClears.AddRange(ids.Except(existingIds).Select(userId => new UserDungeonClear
        {
            UserId = userId,
            DungeonId = dungeonId,
            ClearedAtUtc = clearedAtUtc
        }));
    }

    private static void SetBattleOver(Room room, DateTime now)
    {
        room.Status = RoomStatus.BattleOver;
        room.NextRoundAvailableAtUtc = null;
        room.RoundCooldownDurationSeconds = null;
        room.PreparationStartedAtUtc = null;
        room.BattleEndedAtUtc = now;
    }
}
