using Game.Server.Data;
using Game.Shared;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class DungeonRunService(GameDbContext dbContext, RewardService rewardService,
    MonsterCombatService? monsterCombatService = null, BattleMilestoneService? battleMilestones = null,
    GatheringOpportunityService? gatheringOpportunities = null, RareSeedService? rareSeeds = null,
    DungeonDepthProgressService? depthProgress = null, DungeonRunRulesService? runRules = null)
{
    private readonly DungeonDepthProgressService _depthProgress = depthProgress ?? new(dbContext, runRules: runRules);
    public async Task<(Monster ActiveMonster, bool IsDungeonComplete, string? Error)> AdvanceAfterDefeatAsync(
        Room room, Monster defeatedMonster, IReadOnlyCollection<RewardParticipant> participants,
        DateTime now, List<string> logs, IReadOnlyCollection<int>? actualMonsterCharacterIds = null,
        IReadOnlyCollection<int>? actualRunCharacterIds = null)
    {
        var dungeon = await dbContext.Dungeons.FindAsync(room.DungeonId);
        if (dungeon is null) return (defeatedMonster, false, "DungeonNotFound");
        var definition = runRules is not null ? await runRules.EnsureAsync(room) : null;
        var dungeonKind = definition?.DungeonKind ?? dungeon.DungeonKind;
        var eligibility = definition is not null ? definition.RewardEligibility :
            await _depthProgress.DefinitionAsync(room) is not null ? DungeonRewardEligibility.ActualParticipants : DungeonRewardEligibility.CurrentSlots;
        if (definition is not null && eligibility == DungeonRewardEligibility.ActualParticipants &&
            (actualMonsterCharacterIds is null || actualRunCharacterIds is null))
            return (defeatedMonster, false, "MissingParticipationEvidence");
        var killParticipants = eligibility == DungeonRewardEligibility.ActualParticipants && actualMonsterCharacterIds is not null
            ? participants.Where(item => actualMonsterCharacterIds.Contains(item.Character.Id)).ToList()
            : participants;

        var eventKey = defeatedMonster.RoomId.HasValue
            ? $"monster:{defeatedMonster.WaveNumber}:{defeatedMonster.Position}"
            : "monster:1";
        var rewardProfileCode = string.IsNullOrWhiteSpace(defeatedMonster.RewardProfileCode)
            ? dungeon.Code
            : defeatedMonster.RewardProfileCode;
        if (await rewardService.RecordAsync(room, rewardProfileCode, killParticipants, eventKey, false, actualMonsterCharacterIds))
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
        var clearParticipants = eligibility == DungeonRewardEligibility.ActualParticipants && actualRunCharacterIds is not null
            ? participants.Where(item => actualRunCharacterIds.Contains(item.Character.Id)).ToList()
            : participants;
        var firstClearUserIds = await RecordDungeonClearsAsync(room.DungeonId,
            clearParticipants.Select(participant => participant.UserId), now, room.DepthLevel);
        if (await rewardService.RecordAsync(room, dungeon.Code, clearParticipants, "clear", true, actualRunCharacterIds))
        {
            var eligibleIds = clearParticipants.Select(participant => participant.Character.Id).ToHashSet();
            var actualCharacterIds = actualRunCharacterIds is null ? eligibleIds.ToList()
                : actualRunCharacterIds.Where(eligibleIds.Contains).Distinct().ToList();
            if (actualRunCharacterIds is not null)
                foreach (var characterId in actualCharacterIds)
                    await StoryProgressService.RecordAsync(dbContext, characterId, "DungeonClear", dungeon.Code,
                        $"battle:{room.Id}:{room.RunSequence}:clear", 1, now);
            await rewardService.RecordChallengeFirstClearsAsync(room,
                clearParticipants.Where(item => actualCharacterIds.Contains(item.Character.Id)), now, logs);
            await _depthProgress.RecordCharacterClearsAsync(room, actualCharacterIds, logs);
            await (battleMilestones ?? new BattleMilestoneService(dbContext)).RecordAsync(
                actualCharacterIds,
                BattleMilestoneService.DungeonClearKind, dungeon.Code, now);
            if ((dungeonKind is "Elite" or "Dungeon") && rareSeeds is not null)
                await rareSeeds.RecordDropsAsync(room, dungeon.Code, participants, actualCharacterIds);
            if ((dungeonKind is "Elite" or "Dungeon") && gatheringOpportunities is not null)
            {
                var names = participants.DistinctBy(participant => participant.Character.Id)
                    .ToDictionary(participant => participant.Character.Id, participant => participant.Character.Name);
                foreach (var grant in await gatheringOpportunities.GrantForEliteAsync(dungeon.Code, actualCharacterIds))
                    logs.Add($"{names[grant.CharacterId]} 获得 {grant.PointName} 的稀有采集机会 × 1。");
            }
        }
        var firstClearRewardCode = $"{dungeon.Code}-first-clear";
        if (firstClearUserIds.Count > 0 && await rewardService.HasRewardProfileAsync(room, firstClearRewardCode, true))
            await rewardService.RecordAsync(room, firstClearRewardCode,
                clearParticipants.Where(participant => firstClearUserIds.Contains(participant.UserId)),
                "first-clear", true);
        await rewardService.SettleAsync(room, true, now, logs);
        logs.Add("副本挑战成功。");
        return (defeatedMonster, true, null);
    }

    public async Task<Monster> ResetEncounterAsync(Room room)
    {
        room.ScalingPartySize = 1;
        if (monsterCombatService is not null) await monsterCombatService.ResetRoomStateAsync(room.Id);
        var monsters = await dbContext.Monsters.Where(monster => monster.RoomId == room.Id)
            .OrderBy(monster => monster.WaveNumber).ThenBy(monster => monster.Position).ToListAsync();
        if (monsters.Count == 0)
        {
            var legacyMonster = await dbContext.Monsters.FindAsync(room.MonsterId)
                ?? throw new InvalidOperationException($"Room {room.Id} has no active monster.");
            RestoreBaseHealth(legacyMonster);
            room.CurrentWaveNumber = 1;
            room.TotalWaveCount = 1;
            return legacyMonster;
        }

        foreach (var monster in monsters) RestoreBaseHealth(monster);
        var first = monsters[0];
        room.MonsterId = first.Id;
        room.CurrentWaveNumber = first.WaveNumber;
        room.TotalWaveCount = monsters.Max(monster => monster.WaveNumber);
        return first;
    }

    private static void RestoreBaseHealth(Monster monster)
    {
        if (monster.BaseMaxHp <= 0) monster.BaseMaxHp = monster.MaxHp;
        monster.Hp = monster.MaxHp = monster.BaseMaxHp;
    }

    private async Task<HashSet<int>> RecordDungeonClearsAsync(int dungeonId, IEnumerable<int> userIds, DateTime clearedAtUtc, int depthLevel)
    {
        var ids = userIds.Distinct().ToList();
        var existing = await dbContext.UserDungeonClears
            .Where(clear => clear.DungeonId == dungeonId && ids.Contains(clear.UserId))
            .ToListAsync();
        existing = existing.Concat(dbContext.UserDungeonClears.Local.Where(clear => clear.DungeonId == dungeonId && ids.Contains(clear.UserId))).Distinct().ToList();
        var firstClearIds = ids.Except(existing.Select(clear => clear.UserId)).ToHashSet();
        foreach (var clear in existing.Where(clear => clear.HighestDepth < depthLevel))
        {
            clear.HighestDepth = depthLevel;
            clear.Version++;
        }
        dbContext.UserDungeonClears.AddRange(firstClearIds.Select(userId => new UserDungeonClear
        {
            UserId = userId,
            DungeonId = dungeonId,
            HighestDepth = depthLevel,
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
