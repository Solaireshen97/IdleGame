using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class DungeonRunService(GameDbContext dbContext, RewardService rewardService,
    MonsterCombatService? monsterCombatService = null, BattleMilestoneService? battleMilestones = null)
{
    public async Task<(Monster ActiveMonster, bool IsDungeonComplete, string? Error)> AdvanceAfterDefeatAsync(
        Room room, Monster defeatedMonster, IReadOnlyCollection<RewardParticipant> participants,
        DateTime now, List<string> logs, IReadOnlyCollection<int>? actualMonsterCharacterIds = null,
        IReadOnlyCollection<int>? actualRunCharacterIds = null)
    {
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        if (dungeon is null) return (defeatedMonster, false, "DungeonNotFound");

        var eventKey = defeatedMonster.RoomId.HasValue
            ? $"monster:{defeatedMonster.WaveNumber}:{defeatedMonster.Position}"
            : "monster:1";
        var rewardProfileCode = string.IsNullOrWhiteSpace(defeatedMonster.RewardProfileCode)
            ? dungeon.Code
            : defeatedMonster.RewardProfileCode;
        if (await rewardService.RecordAsync(room, rewardProfileCode, participants, eventKey, false))
            await (battleMilestones ?? new BattleMilestoneService(dbContext)).RecordAsync(
                actualMonsterCharacterIds ?? participants.Select(participant => participant.Character.Id),
                BattleMilestoneService.MonsterKillKind, rewardProfileCode, now);
        logs.Add($"{defeatedMonster.Name} 已被击败。");
        if (monsterCombatService is not null)
            await monsterCombatService.RemoveMonsterStateAsync(room.Id, defeatedMonster.Id);

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
                ? $"第 {defeatedMonster.WaveNumber} 波已清空，第 {nextMonster.WaveNumber} 波即将开始。"
                : $"第 {nextMonster.WaveNumber} 波的下一名敌人正在接近。");
            return (nextMonster, false, null);
        }

        SetBattleOver(room, now);
        var firstClearUserIds = await RecordDungeonClearsAsync(room.DungeonId,
            participants.Select(participant => participant.UserId), now);
        if (await rewardService.RecordAsync(room, dungeon.Code, participants, "clear", true))
            await (battleMilestones ?? new BattleMilestoneService(dbContext)).RecordAsync(
                actualRunCharacterIds ?? participants.Select(participant => participant.Character.Id),
                BattleMilestoneService.DungeonClearKind, dungeon.Code, now);
        var firstClearRewardCode = $"{dungeon.Code}-first-clear";
        if (firstClearUserIds.Count > 0 && rewardService.HasRewardProfile(firstClearRewardCode, true))
            await rewardService.RecordAsync(room, firstClearRewardCode,
                participants.Where(participant => firstClearUserIds.Contains(participant.UserId)),
                "first-clear", true);
        await rewardService.SettleAsync(room, true, now, logs);
        logs.Add("副本挑战成功。");
        return (defeatedMonster, true, null);
    }

    public async Task<Monster> ResetEncounterAsync(Room room)
    {
        if (monsterCombatService is not null) await monsterCombatService.ResetRoomStateAsync(room.Id);
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

    private async Task<HashSet<int>> RecordDungeonClearsAsync(int dungeonId, IEnumerable<int> userIds, DateTime clearedAtUtc)
    {
        var ids = userIds.Distinct().ToList();
        var existingIds = await dbContext.UserDungeonClears
            .Where(clear => clear.DungeonId == dungeonId && ids.Contains(clear.UserId))
            .Select(clear => clear.UserId).ToListAsync();
        var firstClearIds = ids.Except(existingIds).ToHashSet();
        dbContext.UserDungeonClears.AddRange(firstClearIds.Select(userId => new UserDungeonClear
        {
            UserId = userId,
            DungeonId = dungeonId,
            ClearedAtUtc = clearedAtUtc
        }));
        return firstClearIds;
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
