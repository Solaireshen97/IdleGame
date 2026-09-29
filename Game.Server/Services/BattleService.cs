using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public partial class BattleService(GameDbContext dbContext, UserService userService, ConsumableCatalog consumableCatalog, SkillCatalog skillCatalog, RewardService rewardService, DungeonRunService? dungeonRunService = null, MonsterCombatService? monsterCombatService = null, BattleLogStore? battleLogStore = null, Random? random = null, BattleMilestoneService? battleMilestones = null, WeaponCatalog? weaponCatalog = null, SoulImprintCatalog? soulImprintCatalog = null, PartyScalingService? partyScalingService = null, RoomService? roomService = null)
{
    private readonly PartyScalingService _partyScaling = partyScalingService ?? new(dbContext, PartyScalingCatalog.Default);
    private static readonly TimeSpan RoundCooldown = TimeSpan.FromSeconds(BattleRules.RoundCooldownSeconds);
    private static readonly TimeSpan AutoRoundCooldown = TimeSpan.FromSeconds(BattleRules.AutoRoundCooldownSeconds);
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(BattleRules.PreparationTimeoutSeconds);

    public async Task<(BattleResult? Result, string? Error)> StartPreparationAsync(int roomId, string? token,
        int? expectedRoundNumber = null, int? expectedRunSequence = null)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.IsRepeatBattle && room.ExpiresAtUtc is DateTime deadline && now >= deadline &&
            (room.Status is RoomStatus.NotStarted or RoomStatus.Cooldown) && room.RoundNumber == 0)
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, now);
            await SaveResultAsync(room, slots!, monster!, now, ["任务已达到时限，无法开始新一轮战斗。"]);
            return (null, "RoomClosed");
        }
        if (expectedRoundNumber.HasValue && room!.RoundNumber != expectedRoundNumber.Value ||
            expectedRunSequence.HasValue && room!.RunSequence != expectedRunSequence.Value)
            return (BuildResult(room, slots!, monster!, now, ["当前回合已经推进，本次准备请求已忽略。"]), "StaleRound");
        if (room!.Status == RoomStatus.BattleOver) return (BuildResult(room, slots!, monster!, now, ["战斗已经结束，请重置房间。"]), "BattleOver");
        if (room.Status == RoomStatus.WaveTransition) return (BuildResult(room, slots!, monster!, now, ["下一名敌人正在接近。"]), "WaveTransition");
        var isRoundCoolingDown = room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc > now;

        var aliveSlots = slots!.Where(x => x.Character.Hp > 0).ToList();
        if (monster!.Hp <= 0 || aliveSlots.Count == 0)
        {
            ClearRoundState(room, slots);
            SetBattleOver(room, now);
            var logs = new List<string>();
            if (aliveSlots.Count == 0) await rewardService.SettleAsync(room, false, now, logs);
            logs.Add(monster.Hp <= 0 ? "怪物已经被击败，请重置房间。" : "全队已经战败，无法继续战斗。");
            return await SaveResultAsync(room, slots, monster, now, logs);
        }

        if (!aliveSlots.Any(x => x.Slot.UserId == user!.Id)) return (null, "NoOwnedAliveCharacters");
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId);
        if (isRoundCoolingDown)
        {
            if (aliveSlots.Where(x => x.Slot.UserId == user.Id).All(x => x.Slot.IsConfirmed))
                return (BuildResult(room, slots, monster, now, ["你的角色已经为下一回合做好准备。"]), "AlreadyPrepared");

            foreach (var entry in aliveSlots.Where(x => x.Slot.UserId == user.Id || IsSlotAuto(room, x, clearedCharacterIds, slots)))
                entry.Slot.IsConfirmed = true;
            return await SaveResultAsync(room, slots, monster, now, []);
        }

        if (room.Status != RoomStatus.Preparing)
        {
            room.Status = RoomStatus.Preparing;
            room.NextRoundAvailableAtUtc = null;
            room.RoundCooldownDurationSeconds = null;
            room.PreparationStartedAtUtc ??= now;
            room.BattleEndedAtUtc = null;
        }
        else if (aliveSlots.Where(x => x.Slot.UserId == user.Id).All(x => x.Slot.IsConfirmed))
        {
            return (BuildResult(room, slots, monster, now, ["你的角色已经准备完毕。"]), "AlreadyPrepared");
        }

        foreach (var entry in aliveSlots.Where(x => x.Slot.UserId == user.Id || IsSlotAuto(room, x, clearedCharacterIds, slots))) entry.Slot.IsConfirmed = true;
        if (aliveSlots.All(x => x.Slot.IsConfirmed)) return await ExecutePreparedRoundAsync(room, slots, monster, now, []);
        return await SaveResultAsync(room, slots, monster, now, []);
    }

    public async Task<(BattleResult? Result, string? Error)> CancelPreparationAsync(int roomId, string? token,
        int expectedRoundNumber, int expectedRunSequence)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.RoundNumber != expectedRoundNumber || room.RunSequence != expectedRunSequence)
            return (BuildResult(room, slots!, monster!, now, []), "StaleRound");
        if (room.Status == RoomStatus.BattleOver || monster!.Hp <= 0)
            return (BuildResult(room, slots!, monster!, now, []), "BattleOver");
        if (room.Status == RoomStatus.WaveTransition)
            return (BuildResult(room, slots!, monster!, now, []), "WaveTransition");
        var ownedAlive = slots!.Where(entry => entry.Slot.UserId == user!.Id && entry.Character.Hp > 0).ToList();
        if (ownedAlive.Count == 0) return (null, "NoOwnedAliveCharacters");
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId);
        var manualSlots = ownedAlive.Where(entry => !IsSlotAuto(room, entry, clearedCharacterIds, slots)).ToList();
        if (manualSlots.Count == 0) return (null, "PreparationCancellationDenied");
        if (manualSlots.All(entry => !entry.Slot.IsConfirmed && !entry.Slot.IsTemporaryAuto))
            return (BuildResult(room, slots, monster!, now, []), null);
        foreach (var entry in manualSlots)
        {
            entry.Slot.IsConfirmed = false;
            entry.Slot.IsTemporaryAuto = false;
        }
        // Preserve queued actions and both deadlines. Saving the room version makes
        // cancellation race safely with the background worker's round settlement.
        return await SaveResultAsync(room, slots, monster!, now, ["已取消准备，保留已安排的技能与道具。"]);
    }

    public async Task<(BattleResult? Result, string? Error)> SyncAsync(int roomId, string? token)
    {
        var (room, slots, monster, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        try { await dbContext.SaveChangesAsync(); }
        catch (DbUpdateException) { return (null, "ConcurrencyConflict"); }
        return await SyncCoreAsync(room!, slots!, monster!);
    }

    public async Task<(BattleResult? Result, string? Error)> SyncRoomAsync(int roomId)
    {
        var (room, slots, monster, error) = await GetRoomStateAsync(roomId);
        if (error is not null) return (null, error);
        return await SyncCoreAsync(room!, slots!, monster!);
    }

    private async Task<(BattleResult? Result, string? Error)> SyncCoreAsync(Room room, List<SlotCharacter> slots, Monster monster)
    {
        var now = DateTime.UtcNow;
        if (room.ClosedAtUtc.HasValue) return (null, "RoomClosed");
        if (room.IsRepeatBattle && room.ExpiresAtUtc is DateTime deadline && now >= deadline &&
            (room.Status == RoomStatus.BattleOver ||
             (room.Status is RoomStatus.NotStarted or RoomStatus.Cooldown) && room.RoundNumber == 0))
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, now);
            return await SaveResultAsync(room, slots, monster, now, ["任务已达到时限，本轮结束后停止重复战斗。"]);
        }
        var restartedBattle = false;
        if (room.Status == RoomStatus.BattleOver && room.IsRepeatBattle && monster.Hp <= 0 &&
            room.BattleEndedAtUtc is DateTime endedAt && now >= endedAt.AddSeconds(BattleRules.RepeatBattleDelaySeconds))
        {
            var respawnAt = endedAt.AddSeconds(BattleRules.RepeatBattleDelaySeconds);
            monster = await GetDungeonRunService().ResetEncounterAsync(room);
            foreach (var entry in slots)
            {
                BattleConsumableBonusCalculator.Apply(entry.Character, null);
                entry.Character.Hp = TalentRules.EffectiveMaxHp(entry.Character);
            }
            await ResetConsumableCooldownsAsync(room.Id);
            await ResetOperationPotionStatesAsync(room.Id);
            await ResetSkillCooldownsAsync(room.Id);
            ClearRoundState(room, slots);
            ResetRunParticipation(slots);
            room.RoundNumber = 0;
            room.RunSequence++;
            room.Status = RoomStatus.WaveTransition;
            room.NextRoundAvailableAtUtc = respawnAt;
            room.RoundCooldownDurationSeconds = BattleRules.RepeatBattleDelaySeconds;
            room.PreparationStartedAtUtc = null;
            room.BattleEndedAtUtc = null;
            await _partyScaling.SynchronizeAsync(room, slots.Select(entry => entry.Slot).ToList());
            if (monsterCombatService is not null) await monsterCombatService.EnsureIntentAsync(room, monster);
            restartedBattle = true;
        }

        var aliveSlots = slots.Where(x => x.Character.Hp > 0).ToList();
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId);
        var allAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(x => IsSlotAuto(room, x, clearedCharacterIds, slots));
        var stateChanged = restartedBattle;
        if (room.Status is RoomStatus.NotStarted or RoomStatus.Preparing or RoomStatus.Cooldown)
        {
            // Auto members are ready in mixed parties too. Only manual members
            // may subsequently be marked as temporary timeout attackers.
            foreach (var entry in aliveSlots.Where(entry => IsSlotAuto(room, entry, clearedCharacterIds, slots)))
            {
                if (!entry.Slot.IsConfirmed || entry.Slot.IsTemporaryAuto) stateChanged = true;
                entry.Slot.IsConfirmed = true;
                entry.Slot.IsTemporaryAuto = false;
            }
        }
        var transitionCompleted = false;
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc is DateTime cooldownDeadline)
        {
            if (!allAliveMembersAuto && room.RoundCooldownDurationSeconds == BattleRules.AutoRoundCooldownSeconds)
            {
                room.RoundCooldownDurationSeconds = BattleRules.RoundCooldownSeconds;
                room.NextRoundAvailableAtUtc = cooldownDeadline.AddSeconds(
                    BattleRules.RoundCooldownSeconds - BattleRules.AutoRoundCooldownSeconds);
                stateChanged = true;
            }
            else if (allAliveMembersAuto && room.RoundCooldownDurationSeconds == BattleRules.RoundCooldownSeconds)
            {
                room.RoundCooldownDurationSeconds = BattleRules.AutoRoundCooldownSeconds;
                room.NextRoundAvailableAtUtc = cooldownDeadline.AddSeconds(
                    BattleRules.AutoRoundCooldownSeconds - BattleRules.RoundCooldownSeconds);
                stateChanged = true;
            }
        }
        if (room.Status == RoomStatus.WaveTransition && room.NextRoundAvailableAtUtc <= now)
        {
            var transitionDeadline = room.NextRoundAvailableAtUtc.Value;
            room.Status = RoomStatus.Cooldown;
            room.NextRoundAvailableAtUtc = transitionDeadline.AddSeconds(
                (allAliveMembersAuto ? BattleRules.AutoRoundCooldownSeconds : BattleRules.RoundCooldownSeconds) -
                BattleRules.WaveTransitionSeconds);
            room.RoundCooldownDurationSeconds = allAliveMembersAuto
                ? BattleRules.AutoRoundCooldownSeconds : BattleRules.RoundCooldownSeconds;
            room.PreparationStartedAtUtc = null;
            stateChanged = true;
            transitionCompleted = true;
        }
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now && !allAliveMembersAuto)
        {
            if (aliveSlots.Count > 0 && monster.Hp > 0 && aliveSlots.All(x => x.Slot.IsConfirmed))
            {
                room.Status = RoomStatus.Preparing;
                room.NextRoundAvailableAtUtc = null;
                room.RoundCooldownDurationSeconds = null;
                room.PreparationStartedAtUtc = now;
                return await ExecutePreparedRoundAsync(room, slots, monster, now, ["回合冷却结束，已准备的操作开始结算。"]);
            }

            room.Status = RoomStatus.NotStarted;
            room.NextRoundAvailableAtUtc = null;
            room.RoundCooldownDurationSeconds = null;
            room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            stateChanged = true;
        }

        if (!allAliveMembersAuto && (room.Status is RoomStatus.NotStarted or RoomStatus.Preparing) &&
            aliveSlots.Count > 0 && monster.Hp > 0 && aliveSlots.All(entry => entry.Slot.IsConfirmed))
        {
            room.Status = RoomStatus.Preparing;
            return await ExecutePreparedRoundAsync(room, slots, monster, now, ["全队已准备完毕，本回合继续结算。"]);
        }
        if ((room.Status == RoomStatus.Preparing || room.Status == RoomStatus.NotStarted && !allAliveMembersAuto) &&
            room.IsPreparationTimeoutEnabled && aliveSlots.Count > 0 && monster.Hp > 0)
        {
            if (room.PreparationStartedAtUtc is null)
            {
                room.PreparationStartedAtUtc = now;
                stateChanged = true;
            }
            if (now >= room.PreparationStartedAtUtc.Value.Add(PreparationTimeout))
            {
                var logs = new List<string>();
                foreach (var entry in aliveSlots.Where(x => !x.Slot.IsConfirmed))
                {
                    entry.Slot.IsTemporaryAuto = true;
                    entry.Slot.IsConfirmed = true;
                    logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} 准备超时，本回合执行自动攻击（不释放自动技能）。");
                }
                logs.Add("准备阶段已超时，本回合自动开始。");
                return await ExecutePreparedRoundAsync(room, slots, monster, now, logs);
            }
        }
        if (room.Status == RoomStatus.Preparing)
        {
            if (aliveSlots.Count > 0 && monster.Hp > 0 && aliveSlots.All(entry => entry.Slot.IsConfirmed))
            {
                foreach (var entry in aliveSlots) entry.Slot.IsConfirmed = true;
                return await ExecutePreparedRoundAsync(room, slots, monster, now,
                    ["全队已准备完毕，本回合继续结算。"]);
            }
            return stateChanged ? await SaveResultAsync(room, slots, monster, now, []) : (BuildResult(room, slots, monster, now, []), null);
        }
        if (room.Status == RoomStatus.NotStarted && !allAliveMembersAuto)
        {
            return stateChanged
                ? await SaveResultAsync(room, slots, monster, now, restartedBattle
                    ? ["下一场副本战斗已经就绪，全队生命值已恢复。"]
                    : transitionCompleted ? [$"第 {room.CurrentWaveNumber} 波战斗已经就绪。"] : [],
                    resetLog: restartedBattle)
                : (BuildResult(room, slots, monster, now, []), null);
        }

        var autoRoundCanStart = room.Status == RoomStatus.NotStarted ||
            (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now);
        if (autoRoundCanStart && aliveSlots.Count > 0 && monster.Hp > 0 && allAliveMembersAuto)
        {
            room.Status = RoomStatus.Preparing;
            room.NextRoundAvailableAtUtc = null;
            room.PreparationStartedAtUtc = now;
            foreach (var entry in aliveSlots) entry.Slot.IsConfirmed = true;
            var logs = restartedBattle
                ? new List<string> { "下一场副本战斗开始，全队生命值已恢复。", "全队均已开启自动战斗，本回合自动开始。" }
                : transitionCompleted
                    ? new List<string> { $"第 {room.CurrentWaveNumber} 波战斗开始。", "全队均已开启自动战斗，本回合自动开始。" }
                    : new List<string> { "全队均已开启自动战斗，本回合自动开始。" };
            return await ExecutePreparedRoundAsync(room, slots, monster, now, logs, resetLog: restartedBattle);
        }
        if (stateChanged)
        {
            var logs = restartedBattle
                ? new List<string> { "下一场副本战斗已经就绪，全队生命值已恢复。" }
                : transitionCompleted && allAliveMembersAuto
                    ? new List<string> { $"第 {room.CurrentWaveNumber} 波敌人已经就绪，自动战斗将在回合冷却结束后继续。" }
                    : [];
            return await SaveResultAsync(room, slots, monster, now, logs, resetLog: restartedBattle);
        }
        return (BuildResult(room, slots, monster, now, []), null);
    }

    public async Task<(BattleResult? Result, string? Error)> SetSlotAutoAsync(int roomId, SetSlotAutoRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var entry = slots!.SingleOrDefault(x => x.Slot.SlotIndex == request.SlotIndex);
        if (entry is null || entry.Slot.UserId != user!.Id) return (null, "AutoConfigurationDenied");
        var ownedSlots = slots.Where(slot => slot.Slot.UserId == user.Id).ToList();
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room!.DungeonId);
        if (request.IsAutoEnabled && !ownedSlots.Any(slot => slot.Character.Hp > 0 &&
                clearedCharacterIds.Contains(slot.Character.Id))) return (null, "AutoNotUnlocked");
        var wasUserAutoEnabled = RoomAutoPolicy.IsEnabled(room, entry.Slot, slots.Select(slot => slot.Slot).ToArray());
        var wasAllAliveMembersAuto = slots.Where(slot => slot.Character.Hp > 0)
            .All(slot => IsSlotAuto(room, slot, clearedCharacterIds, slots));
        if (user.Id == room.OwnerUserId) room.IsOwnerAutoEnabled = request.IsAutoEnabled;
        foreach (var owned in ownedSlots)
        {
            owned.Slot.IsAutoEnabled = request.IsAutoEnabled;
            if (!request.IsAutoEnabled && wasUserAutoEnabled)
            {
                owned.Slot.IsConfirmed = false;
                owned.Slot.IsTemporaryAuto = false;
            }
        }
        var now = DateTime.UtcNow;
        if (request.IsAutoEnabled && room.Status is RoomStatus.NotStarted or RoomStatus.Preparing or RoomStatus.Cooldown)
            foreach (var owned in ownedSlots.Where(slot => slot.Character.Hp > 0 && clearedCharacterIds.Contains(slot.Character.Id)))
                owned.Slot.IsConfirmed = true;
        if (request.IsAutoEnabled && room!.Status == RoomStatus.NotStarted)
        {
            if (slots.Where(slot => slot.Character.Hp > 0).All(slot => IsSlotAuto(room, slot, clearedCharacterIds, slots)))
                return await SyncCoreAsync(room, slots, monster!);
        }
        if (!request.IsAutoEnabled && room!.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc is DateTime deadline &&
            (room.RoundCooldownDurationSeconds == BattleRules.AutoRoundCooldownSeconds ||
                room.RoundCooldownDurationSeconds is null && wasAllAliveMembersAuto))
        {
            var manualDeadline = deadline - AutoRoundCooldown + RoundCooldown;
            room.RoundCooldownDurationSeconds = BattleRules.RoundCooldownSeconds;
            if (manualDeadline <= now)
            {
                room.Status = RoomStatus.NotStarted;
                room.NextRoundAvailableAtUtc = null;
                room.RoundCooldownDurationSeconds = null;
                room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            }
            else
            {
                room.NextRoundAvailableAtUtc = manualDeadline;
            }
        }
        if (room!.Status == RoomStatus.Preparing && request.IsAutoEnabled)
        {
            if (slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed))
                return await ExecutePreparedRoundAsync(room, slots, monster!, now, []);
        }

        return await SaveResultAsync(room, slots, monster!, now, []);
    }

    public async Task<(bool Success, string? Error)> QueueSkillAsync(QueueSkillRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(request.RoomId, token);
        if (error is not null) return (false, error);
        if (room!.Status == RoomStatus.BattleOver || monster!.Hp <= 0) return (false, "BattleOver");
        if (request.SkillSlotIndex is < 1 or > SkillRules.SlotCount) return (false, "InvalidSlotIndex");
        var participant = slots!.SingleOrDefault(entry => entry.Character.Id == request.CharacterId);
        if (participant is null || participant.Slot.UserId != user!.Id) return (false, "NotCharacterOwner");
        if (participant.Character.Hp <= 0) return (false, "CharacterDead");

        var mask = SkillRules.SlotMask(request.SkillSlotIndex);
        if (request.IsQueued)
        {
            var equipped = await dbContext.CharacterSkillSlots.SingleOrDefaultAsync(
                slot => slot.CharacterId == participant.Character.Id && slot.SlotIndex == request.SkillSlotIndex);
            var professionLevels = (await dbContext.CharacterCombatProfessions
                .Where(entry => entry.CharacterId == participant.Character.Id).ToListAsync())
                .ToDictionary(entry => entry.ProfessionCode, entry => entry.Level, StringComparer.OrdinalIgnoreCase);
            var skill = skillCatalog.ResolveSkillForLevel(participant.Character, equipped?.SkillCode, professionLevels);
            var purchasedNodes = (await dbContext.CharacterSkillTalents
                .Where(node => node.CharacterId == participant.Character.Id).ToListAsync())
                .ToDictionary(node => node.NodeCode, node => node.PointsSpent, StringComparer.OrdinalIgnoreCase);
            if (skill is null || !skillCatalog.IsLearned(participant.Character, skill.Code, purchasedNodes, professionLevels))
                return (false, "SkillNotEquipped");
            var cooldown = await dbContext.BattleSkillCooldowns.SingleOrDefaultAsync(entry =>
                entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id && entry.SkillCode == skill.Code);
            if ((cooldown?.ReadyAtRound ?? skill.InitialCooldownRounds) > room.RoundNumber)
                return (false, "SkillCooldown");
            if (request.TargetCharacterId.HasValue &&
                (!SkillTargetRules.CanChooseAllyTarget(SkillCatalog.EffectsFor(skill).Select(effect => (effect.Type, effect.Target))) ||
                 !slots!.Any(entry => entry.Character.Id == request.TargetCharacterId && entry.Character.Hp > 0)))
                return (false, "InvalidSkillTarget");
            if (!await CanSkillApplyAsync(room, monster, skill, participant, slots!, request.TargetCharacterId))
                return (false, "NoValidSkillTarget");
            SkillQueueRules.Queue(participant.Slot, request.SkillSlotIndex, request.TargetCharacterId);
        }
        else SkillQueueRules.Clear(participant.Slot, mask);

        room.Version++;
        return await SaveAsync();
    }

    public async Task<(bool Success, string? Error)> QueueSoulImprintAsync(QueueSoulImprintRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(request.RoomId, token);
        if (error is not null) return (false, error);
        if (room!.Status == RoomStatus.BattleOver || monster!.Hp <= 0) return (false, "BattleOver");
        var participant = slots!.SingleOrDefault(entry => entry.Character.Id == request.CharacterId);
        if (participant is null || participant.Slot.UserId != user!.Id) return (false, "NotCharacterOwner");
        if (participant.Character.Hp <= 0) return (false, "CharacterDead");
        if (request.IsQueued)
        {
            if (soulImprintCatalog is null) return (false, "SoulImprintNotEquipped");
            var equipped = await dbContext.CharacterSoulImprints.SingleOrDefaultAsync(entry =>
                entry.CharacterId == request.CharacterId && entry.EquippedSlotIndex == SoulImprintRules.SlotIndex);
            var definition = soulImprintCatalog.Find(equipped?.SoulImprintCode);
            if (definition is null) return (false, "SoulImprintNotEquipped");
            var cooldown = await dbContext.BattleSkillCooldowns.SingleOrDefaultAsync(entry =>
                entry.RoomId == room.Id && entry.CharacterId == request.CharacterId &&
                entry.SkillCode == SoulImprintRules.CooldownCode(definition.Code));
            var readyAtRound = cooldown?.ReadyAtRound ?? definition.InitialCooldownRounds;
            if (readyAtRound > room.RoundNumber) return (false, "SoulImprintCooldown");
            if (!await CanSoulImprintApplyAsync(room, monster, definition, participant, slots))
                return (false, "NoValidSoulImprintTarget");
        }
        participant.Slot.IsSoulImprintQueued = request.IsQueued;
        room.Version++;
        return await SaveAsync();
    }

    public async Task<(BattleResult? Result, string? Error)> ExecuteRoundAsync(int roomId, string? token)
    {
        var (room, slots, monster, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.Status != RoomStatus.Preparing || slots!.Where(x => x.Character.Hp > 0).Any(x => !x.Slot.IsConfirmed)) return (BuildResult(room, slots, monster!, now, ["所有存活角色准备完毕后才能执行本回合。"]), "PreparationRequired");
        return await ExecutePreparedRoundAsync(room, slots, monster!, now, []);
    }

    public Task<(BattleResult? Result, string? Error)> ExecuteBattleAsync(int roomId, string? token) => ExecuteRoundAsync(roomId, token);

    public async Task<(bool Success, string? Error)> ResetBattleAsync(int roomId, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        if (room!.OwnerUserId != user!.Id) return (false, "NotOwner");
        if (room.Status != RoomStatus.BattleOver) return (false, "BattleNotOver");
        if (room.ClosedAtUtc.HasValue) return (false, "RoomClosed");
        if (room.IsRepeatBattle && monster!.Hp <= 0) return (false, "RepeatBattlePending");
        monster = await GetDungeonRunService().ResetEncounterAsync(room);
        foreach (var entry in slots!)
        {
            BattleConsumableBonusCalculator.Apply(entry.Character, null);
            entry.Character.Hp = TalentRules.EffectiveMaxHp(entry.Character);
        }
        await ResetConsumableCooldownsAsync(room.Id);
        await ResetOperationPotionStatesAsync(room.Id);
        await ResetSkillCooldownsAsync(room.Id);
        ClearRoundState(room, slots);
        ResetRunParticipation(slots);
        room.RoundNumber = 0;
        room.RunSequence++;
        room.Status = RoomStatus.NotStarted;
        room.NextRoundAvailableAtUtc = null;
        room.RoundCooldownDurationSeconds = null;
        room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? DateTime.UtcNow : null;
        room.BattleEndedAtUtc = null;
        await _partyScaling.SynchronizeAsync(room, slots.Select(entry => entry.Slot).ToList());
        if (monsterCombatService is not null) await monsterCombatService.EnsureIntentAsync(room, monster);
        room.Version++;
        var save = await SaveAsync();
        if (save.Success) battleLogStore?.Clear(room.Id);
        return save;
    }

    private async Task<(BattleResult? Result, string? Error)> ExecutePreparedRoundAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs, bool resetLog = false)
    {
        var aliveSlots = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).ToList();
        if (aliveSlots.Count == 0 || aliveSlots.Any(x => !x.Slot.IsConfirmed)) return (null, "PreparationRequired");
        await _partyScaling.SynchronizeAsync(room, slots.Select(entry => entry.Slot).ToList());
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId);
        foreach (var entry in aliveSlots)
        {
            entry.Slot.HasParticipatedInRun = true;
            entry.Slot.LastParticipatedMonsterId = monster.Id;
        }
        await rewardService.CaptureDungeonParticipantsAsync(room, aliveSlots.Select(entry => entry.Character.Id));
        var combatParticipants = slots.OrderBy(x => x.Slot.SlotIndex)
            .Select(x => new MonsterCombatParticipant(x.Slot, x.Character)).ToList();
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var operationBonuses = await ApplyOperationPotionsAsync(room, aliveSlots, logs);
        await UpdateTemporaryWeaponBonusesAsync(room, aliveSlots);
        await ApplyCombatBuffsAsync(room, aliveSlots, logs);
        await UpdateTemporaryWeaponBonusesAsync(room, aliveSlots);
        var mainWeaponElements = await dbContext.CharacterWeapons
            .Where(weapon => characterIds.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex)
            .ToDictionaryAsync(weapon => weapon.CharacterId, weapon => weapon.Element);
        var pendingTalentEcho = new Dictionary<int, decimal>();
        foreach (var entry in aliveSlots)
        {
            var echo = 0m;
            if (await ConsumeTalentStateAsync(room, entry.Character.Id, "talent-sword-rhythm", requireEarlierRound: true)) echo += 25;
            if (await ConsumeTalentStateAsync(room, entry.Character.Id, "talent-intercept-echo", requireEarlierRound: true))
                echo += skillCatalog.FindTalentNode("sword-disruption")?.ValuePerRank ?? 50;
            if (await ConsumeTalentStateAsync(room, entry.Character.Id, "talent-guard-echo", requireEarlierRound: false))
                echo += skillCatalog.FindTalentNode("sword-counteroffense")?.ValuePerRank ?? 75;
            pendingTalentEcho[entry.Character.Id] = echo;
        }
        var autoCharacterIds = aliveSlots.Where(entry => !entry.Slot.IsTemporaryAuto &&
                IsSlotAuto(room, entry, clearedCharacterIds, slots)).Select(entry => entry.Character.Id).ToHashSet();
        await ApplySoulImprintsAsync(room, aliveSlots, monster, operationBonuses, autoCharacterIds, logs);
        var roundDefense = await ApplyCombatSkillsAsync(room, aliveSlots, monster, mainWeaponElements,
            operationBonuses, autoCharacterIds, logs);
        var monsterReduction = monsterCombatService is null ? 0m :
            await monsterCombatService.GetModifierAsync(room, "Monster", monster.Id, "ReductionPercent");
        foreach (var entry in aliveSlots)
        {
            if (monster.Hp <= 0) break;
            var talentEcho = pendingTalentEcho[entry.Character.Id];
            var element = mainWeaponElements.TryGetValue(entry.Character.Id, out var mainElement) ? mainElement : (ElementType?)null;
            var statusAttack = monsterCombatService is null ? 0m :
                await monsterCombatService.GetModifierAsync(room, "Character", entry.Character.Id, "AttackPercent");
            var healthPercent = WeaponCombatRules.HealthDamagePercent(entry.Character.Hp, TalentRules.EffectiveMaxHp(entry.Character),
                entry.Character.WeaponStaminaPercent + entry.Character.TemporaryWeaponStaminaPercent,
                entry.Character.WeaponEnmityPercent + entry.Character.TemporaryWeaponEnmityPercent);
            var adrenalineChance = monsterCombatService is null ? 0m :
                await monsterCombatService.GetModifierAsync(room, "Character", entry.Character.Id, "DoubleAttackChancePercent");
            var coordinatedEcho = monsterCombatService is null ? 0 :
                await monsterCombatService.GetHunterCoordinatedPercentAsync(room, entry.Character.Id);
            var hits = WeaponCombatRules.RollPercent(entry.Character.WeaponDoubleAttackChancePercent +
                entry.Character.TemporaryWeaponDoubleAttackChancePercent + adrenalineChance, random) ? 2 : 1;
            for (var hit = 0; hit < hits && monster.Hp > 0; hit++)
            {
                var critical = RollCritical(entry.Character);
                var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(entry.Character), monster.Defense,
                    factors: new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(entry.Character, room.RoundNumber) + statusAttack + entry.Character.TalentNormalAttackPercent + operationBonuses.GetValueOrDefault(entry.Character.Id).AttackPercent,
                        HealthPercent: healthPercent,
                        CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                        ElementPercent: WeaponCombatRules.ElementAttackPercent(element, monster.Element, entry.Character.CombatWeaponElementAdvantagePercent),
                        ReductionPercent: monsterReduction,
                        ConsumablePercent: operationBonuses.GetValueOrDefault(entry.Character.Id).FinalDamagePercent +
                            operationBonuses.GetValueOrDefault(entry.Character.Id).NormalAttackDamagePercent));
                if (monsterCombatService is not null)
                    damage = await monsterCombatService.AmplifyHunterDamageAsync(room, monster.Id, damage);
                monster.Hp = Math.Max(0, monster.Hp - damage);
                logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} {(hit == 0 ? "普通攻击" : "二连击")} {monster.Name}，造成 {damage} 点伤害{(critical ? "（暴击）" : "")}。");
                var echo = WeaponCombatRules.EchoDamage(damage,
                    entry.Character.WeaponNormalEchoPercent + entry.Character.TemporaryWeaponNormalEchoPercent +
                    talentEcho + coordinatedEcho);
                if (monster.Hp > 0 && echo > 0)
                {
                    monster.Hp = Math.Max(0, monster.Hp - echo);
                    logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} 对 {monster.Name} 造成 {echo} 点普攻追击伤害。");
                }
            }
            if (monster.Hp <= 0) break;
        }
        var monsterDefeated = monster.Hp <= 0;
        if (monsterDefeated)
        {
            // A wave-ending player action still completes this combat round for healing auras.
            if (monsterCombatService is not null)
                await monsterCombatService.ResolveEndOfRoundAsync(room, monster, combatParticipants, logs,
                    operationBonuses, healingOnly: true);
            await ApplyVictoryCooldownTalentAsync(room, characterIds, logs);
            var participants = slots.Where(slot => slot.Slot.UserId.HasValue)
                .Select(slot => new RewardParticipant(slot.Slot.UserId!.Value, slot.Character)).ToList();
            var advance = await GetDungeonRunService().AdvanceAfterDefeatAsync(room, monster, participants, now, logs,
                slots.Where(slot => slot.Slot.LastParticipatedMonsterId == monster.Id).Select(slot => slot.Character.Id).ToList(),
                slots.Where(slot => slot.Slot.HasParticipatedInRun).Select(slot => slot.Character.Id).ToList());
            if (advance.Error is not null) return (null, advance.Error);
            monster = advance.ActiveMonster;
        }
        if (!monsterDefeated)
        {
            await ApplyCombatConsumablesAsync(room, aliveSlots, logs);
            if (!slots.Any(x => x.Character.Hp > 0))
            {
                SetBattleOver(room, now);
                await rewardService.SettleAsync(room, false, now, logs);
                logs.Add("全队已战败。");
            }
            else
            {
                if (monsterCombatService is not null)
                {
                    await monsterCombatService.ExecuteIntentAsync(room, monster, combatParticipants,
                        mainWeaponElements, roundDefense, logs, operationBonuses);
                    await monsterCombatService.ResolveEndOfRoundAsync(room, monster, combatParticipants, logs,
                        operationBonuses);
                }
                else
                {
                    var target = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).First();
                    var targetElement = mainWeaponElements.TryGetValue(target.Character.Id, out var mainElement) ? mainElement : (ElementType?)null;
                    var damage = DamageCalculator.Calculate(monster.Attack, 0,
                        factors: new DamageFactors(ElementPercent: ElementMatchup.MonsterAttackPercent(monster.Element, targetElement),
                            ReductionPercent: WeaponCombatRules.CombinedDirectReductionPercent(
                                roundDefense.ForCharacter(target.Character.Id).ReductionPercent -
                                operationBonuses.GetValueOrDefault(target.Character.Id).DamageTakenPercent, target.Character)));
                    target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                    logs.Add($"{monster.Name} 普通攻击 {target.Slot.SlotIndex}号位 {target.Character.Name}，造成 {damage} 点伤害。");
                }

                if (monster.Hp <= 0)
                {
                    await ApplyVictoryCooldownTalentAsync(room, characterIds, logs);
                    var participants = slots.Where(slot => slot.Slot.UserId.HasValue)
                        .Select(slot => new RewardParticipant(slot.Slot.UserId!.Value, slot.Character)).ToList();
                    var advance = await GetDungeonRunService().AdvanceAfterDefeatAsync(room, monster, participants, now, logs,
                        slots.Where(slot => slot.Slot.LastParticipatedMonsterId == monster.Id).Select(slot => slot.Character.Id).ToList(),
                        slots.Where(slot => slot.Slot.HasParticipatedInRun).Select(slot => slot.Character.Id).ToList());
                    if (advance.Error is not null) return (null, advance.Error);
                    monster = advance.ActiveMonster;
                }
                else if (!slots.Any(x => x.Character.Hp > 0))
                {
                    SetBattleOver(room, now);
                    await rewardService.SettleAsync(room, false, now, logs);
                    logs.Add("全队已战败。");
                }
                else
                {
                    room.Status = RoomStatus.Cooldown;
                    room.RoundCooldownDurationSeconds = slots.Where(slot => slot.Character.Hp > 0)
                        .All(slot => IsSlotAuto(room, slot, clearedCharacterIds, slots))
                        ? BattleRules.AutoRoundCooldownSeconds : BattleRules.RoundCooldownSeconds;
                    room.NextRoundAvailableAtUtc = now.AddSeconds(room.RoundCooldownDurationSeconds.Value);
                    room.BattleEndedAtUtc = null;
                }
            }
        }
        room.RoundNumber++;
        await UpdateTemporaryWeaponBonusesAsync(room, slots);
        ClearRoundState(room, slots);
        if (room.IsRepeatBattle && room.Status == RoomStatus.BattleOver &&
            (monster.Hp > 0 || room.ExpiresAtUtc is DateTime deadline && now >= deadline))
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, now);
            logs.Add(monster.Hp > 0 ? "队伍战败，重复战斗已停止。" : "任务已达到时限，本轮结算后停止重复战斗。");
        }
        if (monsterCombatService is not null && room.Status != RoomStatus.BattleOver && monster.Hp > 0)
            await monsterCombatService.EnsureIntentAsync(room, monster);
        return await SaveResultAsync(room, slots, monster, now, logs, resetLog);
    }

    private async Task ApplySoulImprintsAsync(Room room, List<SlotCharacter> aliveSlots, Monster monster,
        IReadOnlyDictionary<int, OperationPotionBonuses> operationBonuses,
        IReadOnlyCollection<int> autoCharacterIds, List<string> logs)
    {
        if (soulImprintCatalog is null || aliveSlots.Count == 0 || monster.Hp <= 0) return;
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var equipped = await dbContext.CharacterSoulImprints.Where(entry =>
            characterIds.Contains(entry.CharacterId) && entry.EquippedSlotIndex == SoulImprintRules.SlotIndex).ToListAsync();
        if (equipped.Count == 0) return;
        var cooldownCodes = equipped.Select(entry => SoulImprintRules.CooldownCode(entry.SoulImprintCode)).ToList();
        var cooldowns = await dbContext.BattleSkillCooldowns.Where(entry => entry.RoomId == room.Id &&
            characterIds.Contains(entry.CharacterId) && cooldownCodes.Contains(entry.SkillCode)).ToListAsync();

        foreach (var participant in aliveSlots.OrderBy(entry => entry.Slot.SlotIndex))
        {
            if (monster.Hp <= 0) break;
            var imprint = equipped.SingleOrDefault(entry => entry.CharacterId == participant.Character.Id);
            var definition = soulImprintCatalog.Find(imprint?.SoulImprintCode);
            if (imprint is null || definition is null) continue;
            var automatic = !participant.Slot.IsSoulImprintQueued && imprint.AutoUseEnabled &&
                autoCharacterIds.Contains(participant.Character.Id);
            if (!participant.Slot.IsSoulImprintQueued && !automatic) continue;
            var cooldownCode = SoulImprintRules.CooldownCode(definition.Code);
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == participant.Character.Id &&
                entry.SkillCode == cooldownCode);
            var readyAtRound = cooldown?.ReadyAtRound ?? definition.InitialCooldownRounds;
            if (readyAtRound > room.RoundNumber ||
                !await CanSoulImprintApplyAsync(room, monster, definition, participant, aliveSlots) ||
                automatic && !await MeetsSoulImprintAutoConditionAsync(room, monster, definition, participant, aliveSlots)) continue;

            async Task<int> DealSoulDamageAsync()
            {
                var statusAttack = monsterCombatService is null ? 0m :
                    await monsterCombatService.GetModifierAsync(room, "Character", participant.Character.Id, "AttackPercent");
                var monsterReduction = monsterCombatService is null ? 0m :
                    await monsterCombatService.GetModifierAsync(room, "Monster", monster.Id, "ReductionPercent");
                var healthPercent = WeaponCombatRules.HealthDamagePercent(participant.Character.Hp,
                    TalentRules.EffectiveMaxHp(participant.Character),
                    participant.Character.WeaponStaminaPercent + participant.Character.TemporaryWeaponStaminaPercent,
                    participant.Character.WeaponEnmityPercent + participant.Character.TemporaryWeaponEnmityPercent);
                var critical = RollCritical(participant.Character, isSkill: true);
                var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(participant.Character), monster.Defense,
                    factors: new DamageFactors(
                        AttackPercent: WeaponCombatRules.AttackBonusPercent(participant.Character, room.RoundNumber) + statusAttack +
                            operationBonuses.GetValueOrDefault(participant.Character.Id).AttackPercent,
                        HealthPercent: healthPercent,
                        CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                        ElementPercent: WeaponCombatRules.ElementAttackPercent(definition.Element, monster.Element, participant.Character.CombatWeaponElementAdvantagePercent),
                        ReductionPercent: monsterReduction,
                        SkillDamagePercent: participant.Character.WeaponSkillDamagePercent +
                            participant.Character.TemporaryWeaponSkillDamagePercent + participant.Character.TalentSkillDamagePercent,
                        ConsumablePercent: operationBonuses.GetValueOrDefault(participant.Character.Id).FinalDamagePercent),
                    attackPowerPercent: definition.PowerPercent);
                if (monsterCombatService is not null)
                    damage = await monsterCombatService.AmplifyHunterDamageAsync(room, monster.Id, damage);
                monster.Hp = Math.Max(0, monster.Hp - damage);
                logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 释放魂印「{definition.Name}」攻击 {monster.Name}，造成 {damage} 点{WeaponRules.ElementName(definition.Element)}属性伤害{(critical ? "（暴击）" : "")}。");
                return damage;
            }

            switch (definition.EffectType)
            {
                case SoulImprintEffectType.DamageArmorBreak:
                    await DealSoulDamageAsync();
                    if (monster.Hp > 0 && monsterCombatService is not null)
                        await monsterCombatService.ApplyStatusAsync(room, "Monster", monster.Id, definition.StatusCode!,
                            definition.DurationRounds, logs, monster.Name);
                    break;
                case SoulImprintEffectType.DamageEcho:
                {
                    var damage = await DealSoulDamageAsync();
                    var echo = Math.Min(monster.Hp, (int)decimal.Floor(damage * definition.SecondaryPowerPercent / 100m));
                    if (echo > 0)
                    {
                        monster.Hp -= echo;
                        logs.Add($"魂印毒蚀对 {monster.Name} 追加 {echo} 点无视防御伤害。");
                    }
                    break;
                }
                case SoulImprintEffectType.Interrupt:
                    await DealSoulDamageAsync();
                    if (monster.Hp > 0 && monsterCombatService is not null &&
                        await monsterCombatService.InterruptCurrentIntentAsync(room, monster))
                        logs.Add($"魂印「{definition.Name}」打断了 {monster.Name} 的行动。");
                    break;
                case SoulImprintEffectType.CooldownReduction:
                {
                    var reduction = Math.Max(1, definition.SecondaryPowerPercent);
                    var affected = await dbContext.BattleSkillCooldowns.Where(entry => entry.RoomId == room.Id &&
                        entry.CharacterId == participant.Character.Id &&
                        !entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix) &&
                        entry.ReadyAtRound > room.RoundNumber).ToListAsync();
                    foreach (var skillCooldown in affected)
                        skillCooldown.ReadyAtRound = Math.Max(room.RoundNumber, skillCooldown.ReadyAtRound - reduction);
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 释放魂印「{definition.Name}」，{affected.Count} 个职业技能的剩余冷却缩短 {reduction} 回合。");
                    break;
                }
                case SoulImprintEffectType.HealCleanse:
                    foreach (var target in aliveSlots.Where(entry => entry.Character.Hp > 0))
                    {
                        var maxHp = TalentRules.EffectiveMaxHp(target.Character);
                        var heal = Math.Min(maxHp - target.Character.Hp,
                            (int)decimal.Floor(maxHp * definition.PowerPercent / 100m));
                        if (heal > 0)
                        {
                            target.Character.Hp += heal;
                            logs.Add($"魂印「{definition.Name}」为 {target.Slot.SlotIndex}号位 {target.Character.Name} 恢复 {heal} 点生命值。");
                        }
                        if (monsterCombatService is not null)
                        {
                            for (var count = 0; count < definition.SecondaryPowerPercent; count++)
                            {
                                var removed = await monsterCombatService.RemoveFirstStatusAsync(room, "Character",
                                    [target.Character.Id], false);
                                if (removed is null) break;
                                logs.Add($"魂印「{definition.Name}」移除了 {target.Slot.SlotIndex}号位 {target.Character.Name} 的 {removed.Name}。");
                            }
                        }
                    }
                    break;
                case SoulImprintEffectType.GuardCounter:
                    if (monsterCombatService is not null)
                        await monsterCombatService.ApplyStatusAsync(room, "Character", participant.Character.Id,
                            definition.StatusCode!, definition.DurationRounds, logs,
                            $"{participant.Slot.SlotIndex}号位 {participant.Character.Name}");
                    await DealSoulDamageAsync();
                    break;
            }

            if (cooldown is null)
            {
                cooldown = new BattleSkillCooldown
                {
                    RoomId = room.Id, CharacterId = participant.Character.Id, SkillCode = cooldownCode
                };
                cooldowns.Add(cooldown);
                dbContext.BattleSkillCooldowns.Add(cooldown);
            }
            cooldown.ReadyAtRound = checked(room.RoundNumber + definition.CooldownRounds + 1);
        }
    }

    private async Task<bool> CanSoulImprintApplyAsync(Room room, Monster monster,
        SoulImprintDefinitionOptions definition, SlotCharacter participant, IReadOnlyCollection<SlotCharacter> aliveSlots)
    {
        if (monster.Hp <= 0) return false;
        return definition.EffectType switch
        {
            SoulImprintEffectType.Interrupt => monsterCombatService is not null &&
                await monsterCombatService.CanInterruptCurrentIntentAsync(room, monster),
            SoulImprintEffectType.HealCleanse => aliveSlots.Any(entry => entry.Character.Hp > 0 &&
                entry.Character.Hp < TalentRules.EffectiveMaxHp(entry.Character)) || monsterCombatService is not null &&
                await monsterCombatService.HasRemovableStatusAsync(room, "Character",
                    aliveSlots.Select(entry => entry.Character.Id).ToList(), false),
            SoulImprintEffectType.CooldownReduction => await dbContext.BattleSkillCooldowns.AnyAsync(entry =>
                entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id &&
                !entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix) && entry.ReadyAtRound > room.RoundNumber),
            _ => true
        };
    }

    private async Task<bool> MeetsSoulImprintAutoConditionAsync(Room room, Monster monster,
        SoulImprintDefinitionOptions definition, SlotCharacter participant,
        IReadOnlyCollection<SlotCharacter> aliveSlots)
    {
        switch (definition.EffectType)
        {
            case SoulImprintEffectType.HealCleanse:
                if (monsterCombatService is not null &&
                    await monsterCombatService.HasRemovableStatusAsync(room, "Character",
                        aliveSlots.Select(entry => entry.Character.Id).ToList(), false))
                    return true;
                return aliveSlots.Any(entry => entry.Character.Hp > 0 &&
                    (long)entry.Character.Hp * 100 <=
                    (long)TalentRules.EffectiveMaxHp(entry.Character) * definition.AutoHpThresholdPercent);
            case SoulImprintEffectType.GuardCounter:
            {
                if (monsterCombatService is null) return false;
                var intent = await monsterCombatService.EnsureIntentAsync(room, monster);
                return intent is { IsInterrupted: false } &&
                    (intent.TargetType == "AllAlive" || intent.TargetCharacterId == participant.Character.Id);
            }
            default:
                return true;
        }
    }

    private async Task<PlayerRoundDefense> ApplyCombatSkillsAsync(Room room, List<SlotCharacter> aliveSlots, Monster monster,
        IReadOnlyDictionary<int, ElementType> mainWeaponElements,
        IReadOnlyDictionary<int, OperationPotionBonuses> operationBonuses,
        IReadOnlyCollection<int> autoCharacterIds, List<string> logs)
    {
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var mageEchoTriggeredCharacters = new HashSet<int>();

        async Task TryMageEchoAsync(SlotCharacter participant)
        {
            if (monsterCombatService is null || monster.Hp <= 0 ||
                !mageEchoTriggeredCharacters.Add(participant.Character.Id)) return;
            if (await monsterCombatService.GetMageDisorderStacksAsync(room, participant.Character.Id, monster.Id) < 3)
            {
                mageEchoTriggeredCharacters.Remove(participant.Character.Id);
                return;
            }
            await monsterCombatService.ConsumeMageDisorderAsync(room, participant.Character.Id, monster.Id);
            var domainRank = await monsterCombatService.GetMageDomainRankAsync(room, participant.Character.Id);
            var power = domainRank > 0 ? 50 : 30;
            var element = mainWeaponElements.TryGetValue(participant.Character.Id, out var mainElement)
                ? mainElement : (ElementType?)null;
            var attackModifier = await monsterCombatService.GetModifierAsync(room, "Character", participant.Character.Id, "AttackPercent");
            var monsterReduction = await monsterCombatService.GetModifierAsync(room, "Monster", monster.Id, "ReductionPercent");
            var healthPercent = WeaponCombatRules.HealthDamagePercent(participant.Character.Hp,
                TalentRules.EffectiveMaxHp(participant.Character),
                participant.Character.WeaponStaminaPercent + participant.Character.TemporaryWeaponStaminaPercent,
                participant.Character.WeaponEnmityPercent + participant.Character.TemporaryWeaponEnmityPercent);
            var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(participant.Character), monster.Defense,
                factors: new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(participant.Character, room.RoundNumber) +
                    attackModifier + operationBonuses.GetValueOrDefault(participant.Character.Id).AttackPercent,
                    HealthPercent: healthPercent,
                    ElementPercent: WeaponCombatRules.ElementAttackPercent(element, monster.Element,
                        participant.Character.CombatWeaponElementAdvantagePercent),
                    ReductionPercent: monsterReduction,
                    SkillDamagePercent: participant.Character.WeaponSkillDamagePercent +
                        participant.Character.TemporaryWeaponSkillDamagePercent + participant.Character.TalentSkillDamagePercent,
                    ConsumablePercent: operationBonuses.GetValueOrDefault(participant.Character.Id).FinalDamagePercent),
                attackPowerPercent: power);
            damage = await monsterCombatService.AmplifyHunterDamageAsync(room, monster.Id, damage);
            monster.Hp = Math.Max(0, monster.Hp - damage);
            logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 触发失序回响，对 {monster.Name} 造成 {damage} 点伤害。");
            if (monster.Hp > 0)
                await monsterCombatService.ApplyMageSkillDisruptionAsync(room, monster.Id,
                    domainRank == 3 ? 25 : domainRank == 2 ? 20 : 15, logs, monster.Name);
        }

        async Task AddMageDisorderAndEchoAsync(SlotCharacter participant)
        {
            if (monsterCombatService is null || monster.Hp <= 0) return;
            await monsterCombatService.AddMageDisorderAsync(room, participant.Character.Id, monster.Id,
                logs, $"{participant.Slot.SlotIndex}号位 {participant.Character.Name}");
            await TryMageEchoAsync(participant);
        }

        if (monsterCombatService is not null)
        {
            foreach (var participant in aliveSlots.Where(entry =>
                         string.Equals(entry.Character.ProfessionCode, "mage", StringComparison.OrdinalIgnoreCase)))
            {
                if (monster.Hp <= 0) break;
                var continuousDispel = await dbContext.BattleStatusEffects.AnyAsync(effect =>
                    effect.RoomId == room.Id && effect.RunSequence == room.RunSequence &&
                    effect.TargetType == "Character" && effect.TargetId == participant.Character.Id &&
                    effect.EffectCode == "mage-spellbreak-continuous" &&
                    effect.AppliedRound < room.RoundNumber && effect.ExpiresAfterRound >= room.RoundNumber);
                if (continuousDispel)
                {
                    var removed = await monsterCombatService.RemoveFirstStatusAsync(room, "Monster", [monster.Id], true);
                    if (removed is not null)
                        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 的法术反制持续驱散了 {monster.Name} 的 {removed.Name}。");
                }
                if (await monsterCombatService.GetMageDomainRankAsync(room, participant.Character.Id) > 0)
                    await AddMageDisorderAndEchoAsync(participant);
                else
                    await TryMageEchoAsync(participant);
            }
        }
        if (monster.Hp <= 0) return default;
        var equipment = await dbContext.CharacterSkillSlots
            .Where(slot => characterIds.Contains(slot.CharacterId) && slot.SkillCode != null)
            .OrderBy(slot => slot.SlotIndex).ToListAsync();
        if (equipment.Count == 0) return default;
        var cooldowns = await dbContext.BattleSkillCooldowns
            .Where(cooldown => cooldown.RoomId == room.Id && characterIds.Contains(cooldown.CharacterId)).ToListAsync();
        var purchasedNodes = (await dbContext.CharacterSkillTalents
            .Where(node => characterIds.Contains(node.CharacterId)).ToListAsync())
            .GroupBy(node => node.CharacterId)
            .ToDictionary(group => group.Key,
                group => group.ToDictionary(node => node.NodeCode, node => node.PointsSpent, StringComparer.OrdinalIgnoreCase));
        var professionLevels = (await dbContext.CharacterCombatProfessions
            .Where(entry => characterIds.Contains(entry.CharacterId)).ToListAsync())
            .GroupBy(entry => entry.CharacterId)
            .ToDictionary(group => group.Key, group => group.ToDictionary(entry => entry.ProfessionCode,
                entry => entry.Level, StringComparer.OrdinalIgnoreCase));
        var guardsByCharacter = new Dictionary<int, CharacterRoundDefense>();
        var usedByCharacter = aliveSlots.ToDictionary(entry => entry.Character.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        async Task<bool> TryUseAsync(SlotCharacter participant, CharacterSkillSlot slot, bool automatic)
        {
            if (monster.Hp <= 0) return false;
            var levels = professionLevels.GetValueOrDefault(participant.Character.Id) ??
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var skill = skillCatalog.ResolveSkillForLevel(participant.Character, slot.SkillCode, levels);
            var used = usedByCharacter[participant.Character.Id];
            var ranks = purchasedNodes.GetValueOrDefault(participant.Character.Id) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (skill is null || !skillCatalog.IsLearned(participant.Character, skill.Code, ranks, levels) ||
                used.Contains(skill.Code)) return false;
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == participant.Character.Id && entry.SkillCode == skill.Code);
            if ((cooldown?.ReadyAtRound ?? skill.InitialCooldownRounds) > room.RoundNumber ||
                automatic && !slot.AutoUseEnabled) return false;
            if (automatic && !await MeetsAutoConditionAsync(room, monster, skill, participant, aliveSlots,
                    slot, ranks)) return false;
            var chosenTargetId = automatic ? null : SkillQueueRules.TargetCharacterId(participant.Slot, slot.SlotIndex);
            if (!await CanSkillApplyAsync(room, monster, skill, participant, aliveSlots, chosenTargetId)) return false;

            var applied = false;
            var totalDamage = 0;
            var slashCharges = skill.Code == "rogue-execution-slash" && monsterCombatService is not null
                ? await monsterCombatService.ConsumeShadowChargesAsync(room, participant.Character.Id) : 0;
            var nativeHunter = monsterCombatService is not null &&
                string.Equals(participant.Character.ProfessionCode, "hunter", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(skill.ProfessionCode, "hunter", StringComparison.OrdinalIgnoreCase);
            var nativeAcolyte = monsterCombatService is not null &&
                string.Equals(participant.Character.ProfessionCode, "acolyte", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(skill.ProfessionCode, "acolyte", StringComparison.OrdinalIgnoreCase);
            var acolyteRank = nativeAcolyte ? SkillCatalog.RankFor(skill, participant.Character.Level) : 0;
            var acolyteDamageEnhanced = nativeAcolyte &&
                await monsterCombatService!.HasAcolyteEnhancementAsync(room, participant.Character.Id,
                    forHealing: false, includeRevelation: skill.Code != "acolyte-revelation");
            var acolyteHealEnhanced = nativeAcolyte &&
                await monsterCombatService!.HasAcolyteEnhancementAsync(room, participant.Character.Id,
                    forHealing: true);
            var acolyteCleanseRevelation = nativeAcolyte && skill.Code == "acolyte-purify" &&
                await monsterCombatService!.GetStatusStacksAsync(room, "Character", participant.Character.Id,
                    "acolyte-revelation") > 0;
            var hunterRank = nativeHunter ? SkillCatalog.RankFor(skill, participant.Character.Level) : 0;
            var hunterConsumesMark = skill.Code is "hunter-precision-shot" or "hunter-expose-shot" or "hunter-hunting-signal";
            var hunterMarked = nativeHunter && hunterConsumesMark &&
                await monsterCombatService!.HasHunterMarkAsync(room, participant.Character.Id, monster.Id);
            if (slashCharges > 0)
                logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 的斩击消耗 {slashCharges} 层影之蓄势，伤害提高 {slashCharges * 20}%。");
            var healingOccurred = false;
            var cleansingOccurred = false;
            var lowestAllyAtCast = skill.Code == "knight-invigorate" ? FindLowestHpTarget(aliveSlots) : null;
            var invigoratedTargets = new HashSet<int>();
            var healthPercent = WeaponCombatRules.HealthDamagePercent(participant.Character.Hp,
                TalentRules.EffectiveMaxHp(participant.Character),
                participant.Character.WeaponStaminaPercent + participant.Character.TemporaryWeaponStaminaPercent,
                participant.Character.WeaponEnmityPercent + participant.Character.TemporaryWeaponEnmityPercent);
            var requiredStatusMet = skill.RequiredTargetStatusCode is null || monsterCombatService is not null &&
                await monsterCombatService.HasStatusAsync(room, "Monster", monster.Id, skill.RequiredTargetStatusCode);
            var targetHpConditionMet = skill.TargetHpBelowPercent is null ||
                (long)monster.Hp * 100 <= (long)monster.MaxHp * skill.TargetHpBelowPercent.Value;
            var conditionalDamageBonus = requiredStatusMet && targetHpConditionMet
                ? skill.ConditionalDamageBonusPercent : 0m;

            int ReduceDamageSkillCooldowns(int rounds, string sourceName)
            {
                var affected = 0;
                foreach (var entry in cooldowns.Where(entry => entry.CharacterId == participant.Character.Id &&
                             entry.SkillCode != skill.Code && entry.ReadyAtRound > room.RoundNumber &&
                             !entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix) &&
                             skillCatalog.ResolveSkillForLevel(participant.Character, entry.SkillCode, levels) is { } coolingSkill &&
                             SkillCatalog.EffectsFor(coolingSkill).Any(coolingEffect => coolingEffect.Type == "Damage")))
                {
                    entry.ReadyAtRound = Math.Max(room.RoundNumber, entry.ReadyAtRound - rounds);
                    affected++;
                }
                if (affected > 0)
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 的 {sourceName} 使 {affected} 个伤害技能的冷却缩短 {rounds} 回合。");
                return affected;
            }

            async Task<int> DealSkillDamageAsync(CombatSkillEffectOptions damageEffect, string sourceName)
            {
                if (monster.Hp <= 0) return 0;
                var element = mainWeaponElements.TryGetValue(participant.Character.Id, out var mainElement)
                    ? mainElement : (ElementType?)null;
                var critical = RollCritical(participant.Character, isSkill: true);
                var attackModifier = monsterCombatService is null ? 0m :
                    await monsterCombatService.GetModifierAsync(room, "Character", participant.Character.Id, "AttackPercent");
                var monsterReduction = monsterCombatService is null ? 0m :
                    await monsterCombatService.GetModifierAsync(room, "Monster", monster.Id, "ReductionPercent");
                var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(participant.Character), monster.Defense,
                    damageEffect.Power, new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(participant.Character, room.RoundNumber) + attackModifier + operationBonuses.GetValueOrDefault(participant.Character.Id).AttackPercent,
                        HealthPercent: healthPercent,
                        CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                        ElementPercent: WeaponCombatRules.ElementAttackPercent(element, monster.Element, participant.Character.CombatWeaponElementAdvantagePercent),
                        ReductionPercent: monsterReduction,
                        SkillDamagePercent: participant.Character.WeaponSkillDamagePercent + participant.Character.TemporaryWeaponSkillDamagePercent + participant.Character.TalentSkillDamagePercent + conditionalDamageBonus,
                        ConsumablePercent: operationBonuses.GetValueOrDefault(participant.Character.Id).FinalDamagePercent),
                    damageEffect.AttackPowerPercent + (skill.Code == "hunter-precision-shot" && hunterMarked
                        ? hunterRank == 3 ? 40 : hunterRank == 2 ? 35 : 30 : 0));
                if (skill.Code == "rogue-execution-slash" && slashCharges > 0)
                    damage = (int)Math.Min(int.MaxValue, decimal.Floor(damage * (1m + slashCharges * .20m)));
                if (acolyteDamageEnhanced)
                    damage = (int)Math.Min(int.MaxValue, decimal.Floor(damage * 1.15m));
                if (monsterCombatService is not null)
                    damage = await monsterCombatService.AmplifyHunterDamageAsync(room, monster.Id, damage);
                var actualDamage = Math.Min(monster.Hp, damage);
                monster.Hp = Math.Max(0, monster.Hp - damage);
                var action = string.Equals(sourceName, skill.Name, StringComparison.Ordinal)
                    ? $"使用 {sourceName}" : $"触发 {sourceName}";
                logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} {action} 攻击 {monster.Name}，造成 {damage} 点伤害{(critical ? "（暴击）" : "")}。");
                return skill.Code == "rogue-execution-slash" ? actualDamage : damage;
            }

            foreach (var effect in SkillCatalog.EffectsFor(skill))
            {
                switch (effect.Type)
                {
                    case "Damage" when monster.Hp > 0:
                    {
                        var damage = await DealSkillDamageAsync(effect, skill.Name);
                        totalDamage += damage;
                        if (skill.Code == "rogue-execution-slash" && damage > 0 && monster.Hp > 0 &&
                            (long)monster.Hp * 100 < (long)monster.MaxHp * 35)
                        {
                            var followUp = Math.Min(monster.Hp, (int)decimal.Floor(damage * .30m));
                            if (followUp > 0)
                            {
                                monster.Hp -= followUp;
                                totalDamage += followUp;
                                logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 的斩杀追击对 {monster.Name} 造成 {followUp} 点伤害。");
                            }
                        }
                        applied = true;
                        break;
                    }
                    case "Heal":
                    {
                        var targets = effect.Target == "LowestHpAllyFixed" && lowestAllyAtCast is not null
                            ? new List<SlotCharacter> { lowestAllyAtCast }
                            : ResolveSkillAllyTargets(effect, participant, aliveSlots, chosenTargetId);
                        foreach (var target in targets)
                        {
                            if (skill.Code == "knight-invigorate" && !invigoratedTargets.Add(target.Character.Id)) continue;
                            var maxHp = TalentRules.EffectiveMaxHp(target.Character);
                            if (target.Character.Hp >= maxHp) continue;
                            var missing = Math.Max(0, maxHp - target.Character.Hp);
                            var bonus = participant.Character.TalentHealingDonePercent +
                                target.Character.TalentHealingReceivedPercent;
                            var raw = (int)decimal.Floor(RecoveryCalculator.Calculate(maxHp, effect.Power, effect.HealMaxHpPercent) * (1 + bonus / 100m));
                            if (acolyteHealEnhanced)
                                raw = (int)Math.Min(int.MaxValue, decimal.Floor(raw * 1.20m));
                            var healed = Math.Min(raw, missing);
                            target.Character.Hp += healed;
                            if (healed > 0)
                            {
                                logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，为 {target.Slot.SlotIndex}号位 {target.Character.Name} 恢复 {healed} 点生命值。");
                                healingOccurred = true;
                                applied = true;
                            }
                        }
                        break;
                    }
                    case "Guard":
                    {
                        foreach (var target in ResolveSkillAllyTargets(effect, participant, aliveSlots, chosenTargetId))
                        {
                            var power = skill.Code == "sword-parry" && purchasedNodes.GetValueOrDefault(participant.Character.Id)?.GetValueOrDefault("sword-guard-stance") > 0 ? 50 : effect.Power;
                            power = Math.Min(BattleRules.MaxGuardDamageReductionPercent, power);
                            // Stronger same-round protection wins; the group effect cannot add to the self effect.
                            if (guardsByCharacter.GetValueOrDefault(target.Character.Id).ReductionPercent >= power) continue;
                            guardsByCharacter[target.Character.Id] = new CharacterRoundDefense(power, participant.Character.Id);
                            logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，守护 {target.Slot.SlotIndex}号位 {target.Character.Name}。");
                            applied = true;
                        }
                        break;
                    }
                    case "Cleanse" when monsterCombatService is not null:
                    {
                        var targetIds = ResolveSkillAllyTargets(effect, participant, aliveSlots, chosenTargetId)
                            .Select(entry => entry.Character.Id).ToArray();
                        if (nativeAcolyte && acolyteRank >= 2 && chosenTargetId is null)
                            targetIds = targetIds.Where(id => id != participant.Character.Id)
                                .Concat(targetIds.Where(id => id == participant.Character.Id)).ToArray();
                        var removed = await monsterCombatService.RemoveFirstStatusAsync(room, "Character", targetIds, false);
                        void LogCleanse(RemovedBattleStatus cleared)
                        {
                            var target = aliveSlots.Single(entry => entry.Character.Id == cleared.TargetId);
                            logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，移除了 {target.Slot.SlotIndex}号位 {target.Character.Name} 的 {cleared.Name}。");
                            cleansingOccurred = true;
                            applied = true;
                        }
                        if (removed is not null) LogCleanse(removed);
                        if (nativeAcolyte && skill.Code == "acolyte-purify")
                        {
                            var chosenOrAutoTargetId = chosenTargetId ?? removed?.TargetId;
                            if (acolyteRank >= 2 && chosenOrAutoTargetId != participant.Character.Id)
                            {
                                var selfRemoved = await monsterCombatService.RemoveFirstStatusAsync(room,
                                    "Character", [participant.Character.Id], false);
                                if (selfRemoved is not null) LogCleanse(selfRemoved);
                            }
                            if (acolyteCleanseRevelation && chosenOrAutoTargetId is int bonusTargetId)
                            {
                                var bonusRemoved = await monsterCombatService.RemoveFirstStatusAsync(room,
                                    "Character", [bonusTargetId], false);
                                if (bonusRemoved is not null) LogCleanse(bonusRemoved);
                            }
                        }
                        break;
                    }
                    case "Dispel" when monsterCombatService is not null:
                    {
                        var removed = await monsterCombatService.RemoveFirstStatusAsync(room, "Monster", [monster.Id], true);
                        if (removed is null) break;
                        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，驱散了 {monster.Name} 的 {removed.Name}。");
                        applied = true;
                        break;
                    }
                    case "Interrupt" when monsterCombatService is not null:
                        if (await monsterCombatService.InterruptCurrentIntentAsync(room, monster))
                        {
                            logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，打断了 {monster.Name} 的行动。");
                            if (ranks.GetValueOrDefault("sword-disruption") > 0 && skill.Code == "sword-intercept")
                                await SetTalentStateAsync(room, participant.Character.Id, "talent-intercept-echo", 3);
                            if (ranks.GetValueOrDefault("rogue-opportunist") > 0 && skill.Code == "rogue-gouge" && monster.Hp > 0 && monsterCombatService is not null)
                                await monsterCombatService.ApplyStatusAsync(room, "Monster", monster.Id, "rogue-opening", 2, logs, monster.Name);
                            applied = true;
                        }
                        break;
                    case "ApplyStatus" when monsterCombatService is not null && effect.StatusCode is not null:
                    {
                        if (effect.Target == "Monster")
                        {
                            if (monster.Hp <= 0) break;
                            if (effect.StatusCode.StartsWith("hunter-vulnerability-", StringComparison.Ordinal))
                            {
                                await monsterCombatService.ApplyHunterVulnerabilityAsync(room, monster.Id,
                                    effect.StatusCode, effect.DurationRounds, logs, monster.Name);
                                applied = true;
                                break;
                            }
                            if (effect.StatusCode.StartsWith("mage-chill-", StringComparison.Ordinal))
                            {
                                var incomingPower = effect.StatusCode == "mage-chill-3" ? 15 : 10;
                                if (incomingPower < 15 &&
                                    await monsterCombatService.HasStatusAsync(room, "Monster", monster.Id, "mage-chill-3"))
                                    break;
                                foreach (var chillCode in new[] { "mage-chill-1", "mage-chill-2", "mage-chill-3" })
                                    if (chillCode != effect.StatusCode)
                                        await monsterCombatService.RemoveStatusAsync(room, "Monster", monster.Id, chillCode);
                            }
                            int? perTickValue = null;
                            if (effect.StatusCode is "knight-holy-burn" or "rogue-poison" or "mage-scorch-dot")
                            {
                                var statusAttack = await monsterCombatService.GetModifierAsync(room, "Character",
                                    participant.Character.Id, "AttackPercent");
                                var attackBonus = WeaponCombatRules.AttackBonusPercent(participant.Character, room.RoundNumber) +
                                    statusAttack + operationBonuses.GetValueOrDefault(participant.Character.Id).AttackPercent;
                                var snapshottedAttack = TalentRules.EffectiveAttack(participant.Character) *
                                    Math.Max(0m, 1m + attackBonus / 100m);
                                perTickValue = Math.Max(1, (int)Math.Min(int.MaxValue,
                                    decimal.Floor(snapshottedAttack * effect.AttackPowerPercent / 100m)));
                            }
                            applied |= await monsterCombatService.ApplyStatusAsync(room, "Monster", monster.Id,
                                effect.StatusCode, effect.DurationRounds, logs, monster.Name, perTickValue);
                        }
                        else
                        {
                            foreach (var allyTarget in ResolveSkillAllyTargets(effect, participant, aliveSlots, chosenTargetId))
                            {
                                var targetLabel = $"{allyTarget.Slot.SlotIndex}号位 {allyTarget.Character.Name}";
                                if (effect.StatusCode == "hunter-prey-mark")
                                {
                                    if (monster.Hp > 0)
                                    {
                                        await monsterCombatService.ApplyHunterMarkAsync(room, allyTarget.Character.Id,
                                            monster.Id, effect.DurationRounds, logs, targetLabel);
                                        applied = true;
                                    }
                                    continue;
                                }
                                if (effect.StatusCode.StartsWith("hunter-eagle-eye-", StringComparison.Ordinal))
                                {
                                    await monsterCombatService.ApplyHunterEagleEyeAsync(room, allyTarget.Character.Id,
                                        effect.StatusCode, effect.DurationRounds, logs, targetLabel);
                                    applied = true;
                                    continue;
                                }
                                if (effect.StatusCode.StartsWith("hunter-coordinated-", StringComparison.Ordinal))
                                {
                                    var coordinatedCode = hunterMarked ? $"hunter-coordinated-{(hunterRank == 3 ? 20 : hunterRank == 2 ? 16 : 12)}"
                                        : effect.StatusCode;
                                    await monsterCombatService.ApplyHunterCoordinatedAsync(room, allyTarget.Character.Id,
                                        coordinatedCode, effect.DurationRounds, logs, targetLabel);
                                    applied = true;
                                    continue;
                                }
                                int? perTickValue = effect.StatusCode == "knight-holy-renew"
                                    ? Math.Max(1, (int)decimal.Floor(TalentRules.EffectiveMaxHp(allyTarget.Character) * effect.HealMaxHpPercent / 100m))
                                    : null;
                                if (effect.StatusCode.StartsWith("rogue-adrenaline-", StringComparison.Ordinal))
                                {
                                    foreach (var adrenalineCode in new[] { "rogue-adrenaline-1", "rogue-adrenaline-2", "rogue-adrenaline-3" })
                                        if (adrenalineCode != effect.StatusCode)
                                            await monsterCombatService.RemoveStatusAsync(room, "Character", allyTarget.Character.Id, adrenalineCode);
                                }
                                if (effect.StatusCode.StartsWith("mage-domain-", StringComparison.Ordinal))
                                {
                                    foreach (var domainCode in new[] { "mage-domain-1", "mage-domain-2", "mage-domain-3" })
                                        if (domainCode != effect.StatusCode)
                                            await monsterCombatService.RemoveStatusAsync(room, "Character", allyTarget.Character.Id, domainCode);
                                }
                                applied |= await monsterCombatService.ApplyStatusAsync(room, "Character", allyTarget.Character.Id,
                                    effect.StatusCode, effect.DurationRounds, logs, targetLabel, perTickValue);
                            }
                        }
                        break;
                    }
                    case "CooldownReduction":
                        applied |= ReduceDamageSkillCooldowns(effect.Power, skill.Name) > 0;
                        break;
                }
            }
            if (monsterCombatService is not null && nativeHunter && hunterMarked &&
                skill.Code == "hunter-expose-shot" && monster.Hp > 0)
            {
                await monsterCombatService.ApplyHunterVulnerabilityAsync(room, monster.Id,
                    $"hunter-vulnerability-{(hunterRank == 3 ? 12 : hunterRank == 2 ? 10 : 8)}",
                    1, logs, monster.Name);
                applied = true;
            }
            if (monsterCombatService is not null && hunterMarked && applied)
                await monsterCombatService.ConsumeHunterMarkAsync(room, participant.Character.Id, monster.Id,
                    logs, $"{participant.Slot.SlotIndex}号位 {participant.Character.Name}");
            if (monster.Hp > 0 && skill.Code == "rogue-blade-flurry" && ranks.GetValueOrDefault("rogue-relentless-assault") > 0)
            {
                totalDamage += await DealSkillDamageAsync(new CombatSkillEffectOptions
                    { Type = "Damage", Target = "Monster", Power = 1, AttackPowerPercent = 40 }, "夺命连攻追加攻击");
                applied = true;
            }
            if (monsterCombatService is not null && HasSelfCleanseTalent(skill, ranks))
            {
                var removed = await monsterCombatService.RemoveFirstStatusAsync(room, "Character", [participant.Character.Id], false);
                if (removed is not null)
                {
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 借助 {skill.Name} 移除了 {removed.Name}。");
                    applied = true;
                }
            }
            if (!applied) return false;
            if (nativeAcolyte && monsterCombatService is not null)
            {
                var acolyteLabel = $"{participant.Slot.SlotIndex}号位 {participant.Character.Name}";
                if (totalDamage > 0)
                {
                    if (acolyteDamageEnhanced)
                    {
                        await monsterCombatService.ConsumeAcolyteEnhancementAsync(room,
                            participant.Character.Id, forHealing: false,
                            includeRevelation: skill.Code != "acolyte-revelation");
                        logs.Add($"{acolyteLabel} 的辉光使 {skill.Name} 伤害提高 15%。");
                    }
                    await monsterCombatService.GrantAcolyteEnhancementAsync(room,
                        participant.Character.Id, forHealing: true);
                }
                if (skill.Code == "acolyte-revelation")
                {
                    await monsterCombatService.SetAcolyteRevelationAsync(room,
                        participant.Character.Id, acolyteRank);
                    logs.Add($"{acolyteLabel} 的神启使接下来 {acolyteRank} 次本职技能固定获得强化。");
                }
                if (healingOccurred)
                {
                    if (acolyteHealEnhanced)
                    {
                        await monsterCombatService.ConsumeAcolyteEnhancementAsync(room,
                            participant.Character.Id, forHealing: true);
                        logs.Add($"{acolyteLabel} 的恩泽使 {skill.Name} 治疗提高 20%。");
                    }
                    await monsterCombatService.GrantAcolyteEnhancementAsync(room,
                        participant.Character.Id, forHealing: false);
                }
                if (cleansingOccurred)
                {
                    if (acolyteCleanseRevelation)
                        await monsterCombatService.ConsumeAcolyteRevelationAsync(room,
                            participant.Character.Id);
                    await monsterCombatService.GrantAcolyteEnhancementAsync(room,
                        participant.Character.Id, forHealing: false);
                }
            }
            if (skill.Code == "rogue-shadow-strike" && monsterCombatService is not null)
                await monsterCombatService.AddShadowChargeAsync(room, participant.Character.Id, logs,
                    $"{participant.Slot.SlotIndex}号位 {participant.Character.Name}");
            if (monsterCombatService is not null &&
                string.Equals(participant.Character.ProfessionCode, "mage", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(skill.ProfessionCode, "mage", StringComparison.OrdinalIgnoreCase))
            {
                await AddMageDisorderAndEchoAsync(participant);
                if (skill.Code == "mage-arcane-domain")
                    await AddMageDisorderAndEchoAsync(participant);
            }
            if (totalDamage > 0)
            {
                if (ranks.GetValueOrDefault("sword-rhythm") > 0)
                    await SetTalentStateAsync(room, participant.Character.Id, "talent-sword-rhythm", 3);
                if (ranks.GetValueOrDefault("sword-assault-stance") > 0)
                {
                    var maxHp = TalentRules.EffectiveMaxHp(participant.Character);
                    var healed = Math.Min((int)decimal.Floor(totalDamage * .10m), maxHp - participant.Character.Hp);
                    if (healed > 0)
                    {
                        participant.Character.Hp += healed;
                        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 从猛攻中恢复 {healed} 点生命值。");
                    }
                }
            }
            if (cooldown is null)
            {
                cooldown = new BattleSkillCooldown { RoomId = room.Id, CharacterId = participant.Character.Id, SkillCode = skill.Code };
                cooldowns.Add(cooldown);
                dbContext.BattleSkillCooldowns.Add(cooldown);
            }
            cooldown.ReadyAtRound = checked(room.RoundNumber + skill.CooldownRounds + 1);
            used.Add(skill.Code);
            return true;
        }

        foreach (var participant in aliveSlots)
        {
            var characterEquipment = equipment.Where(slot => slot.CharacterId == participant.Character.Id);
            foreach (var slot in characterEquipment.Where(slot => (participant.Slot.PendingSkillSlotMask & SkillRules.SlotMask(slot.SlotIndex)) != 0))
            {
                if (monster.Hp <= 0) break;
                await TryUseAsync(participant, slot, automatic: false);
            }
            if (monster.Hp <= 0) break;
        }
        foreach (var participant in aliveSlots)
        {
            if (!autoCharacterIds.Contains(participant.Character.Id)) continue;
            var characterEquipment = equipment.Where(slot => slot.CharacterId == participant.Character.Id);
            foreach (var slot in characterEquipment)
            {
                if (monster.Hp <= 0) break;
                if ((participant.Slot.PendingSkillSlotMask & SkillRules.SlotMask(slot.SlotIndex)) == 0)
                    await TryUseAsync(participant, slot, automatic: true);
            }
            if (monster.Hp <= 0) break;
        }
        return new PlayerRoundDefense(0, null, GuardsByCharacter: guardsByCharacter);
    }

    private async Task<bool> CanSkillApplyAsync(Room room, Monster monster, CombatSkillOptions skill,
        SlotCharacter participant, IReadOnlyList<SlotCharacter> slots, int? chosenTargetId = null)
    {
        var effects = SkillCatalog.EffectsFor(skill);
        if (chosenTargetId.HasValue &&
            (!SkillTargetRules.CanChooseAllyTarget(effects.Select(effect => (effect.Type, effect.Target))) ||
             !slots.Any(entry => entry.Character.Id == chosenTargetId && entry.Character.Hp > 0))) return false;
        // Damage remains usable independently of the interrupt; Auto timing is checked separately.
        if (SkillRules.RequiresInterruptibleTarget(effects.Select(effect => effect.Type)))
            return monsterCombatService is not null &&
                await monsterCombatService.CanInterruptCurrentIntentAsync(room, monster);
        var alive = slots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList();
        foreach (var effect in effects)
        {
            switch (effect.Type)
            {
                case "Damage" when monster.Hp > 0:
                    return true;
                case "Guard":
                    if (ResolveSkillAllyTargets(effect, participant, alive, chosenTargetId).Count > 0) return true;
                    break;
                case "Heal":
                    if (ResolveSkillAllyTargets(effect, participant, alive, chosenTargetId)
                        .Any(target => target.Character.Hp < TalentRules.EffectiveMaxHp(target.Character)))
                        return true;
                    break;
                case "Cleanse" when monsterCombatService is not null:
                    var cleanseTargets = ResolveSkillAllyTargets(effect, participant, alive, chosenTargetId)
                        .Select(entry => entry.Character.Id).ToArray();
                    if (await monsterCombatService.HasRemovableStatusAsync(room, "Character", cleanseTargets, false))
                        return true;
                    if (skill.Code == "acolyte-purify" &&
                        string.Equals(participant.Character.ProfessionCode, "acolyte", StringComparison.OrdinalIgnoreCase) &&
                        SkillCatalog.RankFor(skill, participant.Character.Level) >= 2 &&
                        await monsterCombatService.HasRemovableStatusAsync(room, "Character",
                            [participant.Character.Id], false)) return true;
                    break;
                case "Dispel" when monsterCombatService is not null:
                    if (await monsterCombatService.HasRemovableStatusAsync(room, "Monster", [monster.Id], true))
                        return true;
                    break;
                case "Interrupt" when monsterCombatService is not null:
                    if (await monsterCombatService.CanInterruptCurrentIntentAsync(room, monster)) return true;
                    break;
                case "ApplyStatus" when monsterCombatService is not null && effect.StatusCode is not null:
                    if (effect.Target == "Monster" ? monster.Hp > 0 :
                        ResolveSkillAllyTargets(effect, participant, alive, chosenTargetId).Count > 0) return true;
                    break;
                case "CooldownReduction":
                    if (await dbContext.BattleSkillCooldowns.AnyAsync(entry => entry.RoomId == room.Id &&
                            entry.CharacterId == participant.Character.Id && entry.SkillCode != skill.Code &&
                            !entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix) &&
                            entry.ReadyAtRound > room.RoundNumber)) return true;
                    break;
            }
        }
        return false;
    }

    private static List<SlotCharacter> ResolveSkillAllyTargets(CombatSkillEffectOptions effect,
        SlotCharacter participant, IReadOnlyList<SlotCharacter> slots, int? chosenTargetId)
    {
        var alive = slots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList();
        if (chosenTargetId.HasValue && SkillTargetRules.UsesChosenTarget(effect.Type, effect.Target))
            return alive.Where(entry => entry.Character.Id == chosenTargetId).ToList();
        if (effect.Target is "AllAlive" or "FirstDebuffedAlly") return alive;
        if (effect.Target == "AllOtherAlive") return alive.Where(entry => entry.Character.Id != participant.Character.Id).ToList();
        var target = effect.Target switch
        {
            "Self" => participant.Character.Hp > 0 ? participant : null,
            "LowestHpAlly" => FindLowestHpTarget(alive),
            "LowestHpAllyFixed" => FindLowestHpTarget(alive),
            "FrontAlly" or "FrontAllyFixed" => alive.FirstOrDefault(),
            _ => null
        };
        return target is null ? [] : [target];
    }

    private async Task<bool> MeetsAutoConditionAsync(Room room, Monster monster, CombatSkillOptions skill,
        SlotCharacter participant, IReadOnlyList<SlotCharacter> slots, CharacterSkillSlot skillSlot,
        IReadOnlyDictionary<string, int> ranks)
    {
        if (skillSlot.AutoConditionOverride is null && monsterCombatService is not null && HasSelfCleanseTalent(skill, ranks) &&
            await monsterCombatService.HasRemovableStatusAsync(room, "Character", [participant.Character.Id], false))
            return true;
        var alive = slots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList();
        var hpThresholdPercent = skillSlot.AutoHpThresholdPercent;
        bool HpMatches(SlotCharacter target) => (long)target.Character.Hp * 100 <=
            (long)TalentRules.EffectiveMaxHp(target.Character) * hpThresholdPercent;
        return (skillSlot.AutoConditionOverride ?? SkillCatalog.AutoConditionFor(skill)) switch
        {
            "Always" => true,
            "LowestHpBelowThreshold" => GetHpConditionTarget(skill, participant, alive) is { } target && HpMatches(target),
            "SelfHpBelowThreshold" => HpMatches(participant),
            "AllyHpBelowThreshold" => alive.Any(HpMatches),
            "FrontAllyHpBelowThreshold" => alive.FirstOrDefault() is { } front && HpMatches(front),
            "MonsterHpBelowThreshold" => (long)monster.Hp * 100 <= (long)monster.MaxHp * hpThresholdPercent,
            "AllyHasDebuff" => monsterCombatService is not null &&
                await monsterCombatService.HasRemovableStatusAsync(room, "Character",
                    alive.Select(entry => entry.Character.Id).ToArray(), false),
            "MonsterHasBuff" => monsterCombatService is not null &&
                await monsterCombatService.HasRemovableStatusAsync(room, "Monster", [monster.Id], true),
            "InterruptibleIntent" => monsterCombatService is not null &&
                await monsterCombatService.CanInterruptCurrentIntentAsync(room, monster),
            "PreferInterrupt" => monsterCombatService is null ||
                !monsterCombatService.HasAnyInterruptibleSkill(monster) ||
                await monsterCombatService.CanInterruptCurrentIntentAsync(room, monster),
            _ => false
        };
    }

    private static bool HasSelfCleanseTalent(CombatSkillOptions skill, IReadOnlyDictionary<string, int> ranks) =>
        skill.Code == "rogue-evasion" && ranks.GetValueOrDefault("rogue-escape-artist") > 0;

    private static SlotCharacter? GetHpConditionTarget(CombatSkillOptions skill, SlotCharacter participant,
        IReadOnlyList<SlotCharacter> alive)
    {
        var effects = SkillCatalog.EffectsFor(skill);
        if (effects.Any(effect => effect.Type == "Guard" && effect.Target == "Self")) return participant;
        if (effects.Any(effect => effect.Type == "Guard")) return alive.FirstOrDefault();
        if (effects.Any(effect => effect.Type == "Heal" && effect.Target == "Self"))
            return effects.Any(effect => effect.Type == "Heal" && effect.Target == "LowestHpAllyFixed")
                ? FindLowestHpTarget(alive) : participant;
        return FindLowestHpTarget(alive);
    }

    private static SlotCharacter? FindLowestHpTarget(IEnumerable<SlotCharacter> slots) => slots
        .Where(entry => entry.Character.Hp > 0)
        .OrderBy(entry => (decimal)entry.Character.Hp / TalentRules.EffectiveMaxHp(entry.Character))
        .ThenBy(entry => entry.Slot.SlotIndex).FirstOrDefault();

    private bool RollCritical(Character character, bool isSkill = false)
    {
        var chance = character.WeaponCriticalChancePercent + character.TemporaryWeaponCriticalChancePercent +
            (isSkill ? character.TalentSkillCriticalChancePercent : 0);
        return WeaponCombatRules.RollPercent(chance, random);
    }

    private async Task<Dictionary<int, OperationPotionBonuses>> ApplyOperationPotionsAsync(Room room,
        List<SlotCharacter> aliveSlots, List<string> logs)
    {
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var states = await dbContext.BattleOperationPotionStates.Where(state =>
                state.RoomId == room.Id && state.RunSequence == room.RunSequence &&
                characterIds.Contains(state.CharacterId))
            .ToDictionaryAsync(state => state.CharacterId);
        var equipped = await dbContext.CharacterConsumableSlots.Where(slot =>
                characterIds.Contains(slot.CharacterId) &&
                slot.SlotIndex == ConsumableRules.OperationPotionSlotIndex)
            .ToDictionaryAsync(slot => slot.CharacterId);
        var itemCodes = equipped.Values.Where(slot => slot.ItemCode is not null)
            .Select(slot => slot.ItemCode!).Distinct().ToList();
        var stocks = await dbContext.CharacterItemStacks.Where(stack =>
                characterIds.Contains(stack.CharacterId) && itemCodes.Contains(stack.ItemCode))
            .ToDictionaryAsync(stack => (stack.CharacterId, stack.ItemCode));

        foreach (var participant in aliveSlots)
        {
            var characterId = participant.Character.Id;
            if (states.ContainsKey(characterId)) continue;
            var state = new BattleOperationPotionState
            {
                RoomId = room.Id, RunSequence = room.RunSequence, CharacterId = characterId
            };
            if (equipped.TryGetValue(characterId, out var slot) &&
                consumableCatalog.FindItem(slot.ItemCode) is { Kind: "OperationPotion" } item)
            {
                var usable = participant.Character.Level >= (item.Tier - 1) * 10 + 1 &&
                    ConsumableRules.EffectScalePercent(item.Tier, participant.Character.Level) > 0;
                if (usable && stocks.TryGetValue((characterId, item.Code), out var stock) && stock.Quantity > 0)
                {
                    stock.Quantity--;
                    stock.Version++;
                    state.ItemCode = item.Code;
                    state.AttackPercent = ConsumableRules.ScaledPercent(item.AttackPercent, item.Tier, participant.Character.Level);
                    state.FinalDamagePercent = ConsumableRules.ScaledPercent(item.FinalDamagePercent, item.Tier, participant.Character.Level);
                    state.NormalAttackDamagePercent = ConsumableRules.ScaledPercent(item.NormalAttackDamagePercent, item.Tier, participant.Character.Level);
                    state.AreaDamageReductionPercent = ConsumableRules.ScaledPercent(item.AreaDamageReductionPercent, item.Tier, participant.Character.Level);
                    state.DamageTakenPercent = item.DamageTakenPercent;
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {item.Name}，{ConsumableCatalog.Description(item, participant.Character.Level)}。");
                }
                else
                {
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 的 {item.Name} {(usable ? "库存不足" : "当前等级无法生效")}，本场跳过使用。");
                }
            }
            states[characterId] = state;
            dbContext.BattleOperationPotionStates.Add(state);
        }
        return states.ToDictionary(entry => entry.Key, entry => new OperationPotionBonuses(
            entry.Value.AttackPercent, entry.Value.FinalDamagePercent, entry.Value.DamageTakenPercent,
            entry.Value.NormalAttackDamagePercent, entry.Value.AreaDamageReductionPercent));
    }

    private async Task UpdateTemporaryWeaponBonusesAsync(Room room, IEnumerable<SlotCharacter> participants)
    {
        var entries = participants.ToList();
        if (entries.Count == 0) return;
        var ids = entries.Select(entry => entry.Character.Id).ToList();
        var active = room.Status == RoomStatus.BattleOver
            ? []
            : await dbContext.BattleConsumableBuffs.Where(buff => buff.RoomId == room.Id &&
                buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber &&
                ids.Contains(buff.CharacterId)).ToListAsync();
        active = active.Concat(dbContext.BattleConsumableBuffs.Local.Where(buff =>
            dbContext.Entry(buff).State == EntityState.Added && buff.RoomId == room.Id &&
            buff.RunSequence == room.RunSequence && buff.ExpiresAfterRound >= room.RoundNumber &&
            ids.Contains(buff.CharacterId))).ToList();
        var weapons = weaponCatalog is null ? [] :
            await dbContext.CharacterWeapons.Include(weapon => weapon.Skills)
                .Where(weapon => ids.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex != null).ToListAsync();
        foreach (var entry in entries)
        {
            var buffs = active.Where(buff => buff.CharacterId == entry.Character.Id).ToList();
            if (buffs.Count == 0 || weaponCatalog is null)
            {
                BattleConsumableBonusCalculator.Apply(entry.Character, null);
                if (weaponCatalog is not null)
                    BattleConsumableBonusCalculator.ApplyCombatEffects(entry.Character,
                        weaponCatalog.CalculateBonuses(weapons.Where(weapon => weapon.CharacterId == entry.Character.Id)));
            }
            else
            {
                var levels = buffs.GroupBy(buff => buff.WeaponSkillCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Sum(buff => buff.SkillLevel), StringComparer.OrdinalIgnoreCase);
                BattleConsumableBonusCalculator.Apply(entry.Character,
                    weaponCatalog.CalculateBonuses(weapons.Where(weapon => weapon.CharacterId == entry.Character.Id), levels));
            }
            entry.Character.Hp = Math.Min(entry.Character.Hp, TalentRules.EffectiveMaxHp(entry.Character));
        }
    }

    private async Task ResetConsumableCooldownsAsync(int roomId)
    {
        foreach (var cooldown in await dbContext.BattleConsumableCooldowns.Where(entry => entry.RoomId == roomId).ToListAsync())
            cooldown.ReadyAtRound = 0;
    }

    private async Task ResetOperationPotionStatesAsync(int roomId)
    {
        dbContext.BattleOperationPotionStates.RemoveRange(await dbContext.BattleOperationPotionStates
            .Where(state => state.RoomId == roomId).ToListAsync());
        dbContext.BattleConsumableBuffs.RemoveRange(await dbContext.BattleConsumableBuffs
            .Where(buff => buff.RoomId == roomId).ToListAsync());
    }

    private async Task ResetSkillCooldownsAsync(int roomId)
    {
        var cooldowns = await dbContext.BattleSkillCooldowns.Where(entry => entry.RoomId == roomId).ToListAsync();
        dbContext.BattleSkillCooldowns.RemoveRange(cooldowns.Where(entry =>
            entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix)));
        foreach (var cooldown in cooldowns.Where(entry =>
                     !entry.SkillCode.StartsWith(SoulImprintRules.CooldownPrefix)))
            cooldown.ReadyAtRound = 0;
    }

    private async Task SetTalentStateAsync(Room room, int characterId, string code, int durationRounds)
    {
        var state = dbContext.BattleStatusEffects.Local.FirstOrDefault(item => item.RoomId == room.Id &&
            item.RunSequence == room.RunSequence && item.TargetType == "Character" && item.TargetId == characterId && item.EffectCode == code)
            ?? await dbContext.BattleStatusEffects.SingleOrDefaultAsync(item => item.RoomId == room.Id &&
                item.RunSequence == room.RunSequence && item.TargetType == "Character" && item.TargetId == characterId && item.EffectCode == code);
        if (state is not null && dbContext.Entry(state).State == EntityState.Deleted)
            dbContext.Entry(state).State = EntityState.Modified;
        if (state is null)
        {
            state = new BattleStatusEffect { RoomId = room.Id, RunSequence = room.RunSequence, TargetType = "Character",
                TargetId = characterId, EffectCode = code, Stacks = 1 };
            dbContext.BattleStatusEffects.Add(state);
        }
        state.AppliedRound = room.RoundNumber;
        state.ExpiresAfterRound = room.RoundNumber + durationRounds;
    }

    private async Task<bool> ConsumeTalentStateAsync(Room room, int characterId, string code, bool requireEarlierRound)
    {
        var state = dbContext.BattleStatusEffects.Local.FirstOrDefault(item => item.RoomId == room.Id && item.RunSequence == room.RunSequence &&
            item.TargetType == "Character" && item.TargetId == characterId && item.EffectCode == code &&
            dbContext.Entry(item).State != EntityState.Deleted)
            ?? await dbContext.BattleStatusEffects.SingleOrDefaultAsync(item => item.RoomId == room.Id && item.RunSequence == room.RunSequence &&
                item.TargetType == "Character" && item.TargetId == characterId && item.EffectCode == code);
        if (state is null || state.ExpiresAfterRound < room.RoundNumber || requireEarlierRound && state.AppliedRound >= room.RoundNumber) return false;
        dbContext.BattleStatusEffects.Remove(state);
        return true;
    }

    private async Task ApplyVictoryCooldownTalentAsync(Room room, IReadOnlyCollection<int> characterIds, List<string> logs)
    {
        var eligible = await dbContext.CharacterSkillTalents.Where(node => characterIds.Contains(node.CharacterId) &&
                node.NodeCode == "sword-pursuit" && node.PointsSpent > 0)
            .Select(node => node.CharacterId).ToListAsync();
        if (eligible.Count == 0) return;
        var cooldowns = await dbContext.BattleSkillCooldowns.Where(item => item.RoomId == room.Id &&
            eligible.Contains(item.CharacterId)).ToListAsync();
        foreach (var cooldown in cooldowns.Where(item => skillCatalog.FindSkill(item.SkillCode) is { } skill &&
                     SkillCatalog.EffectsFor(skill).Any(effect => effect.Type == "Damage")))
            cooldown.ReadyAtRound = Math.Max(room.RoundNumber + 1, cooldown.ReadyAtRound - 1);
        var characters = await dbContext.Characters.Where(character => eligible.Contains(character.Id)).ToListAsync();
        foreach (var character in characters.Where(character => character.Hp > 0))
        {
            var maxHp = TalentRules.EffectiveMaxHp(character);
            var recovered = Math.Min((int)decimal.Floor(maxHp * .08m), maxHp - character.Hp);
            if (recovered > 0) character.Hp += recovered;
            logs.Add(recovered > 0
                ? $"{character.Name} 的乘胜追击恢复了 {recovered} 点生命，并使伤害技能冷却缩短 1 回合。"
                : $"{character.Name} 的乘胜追击使伤害技能冷却缩短 1 回合。");
        }
    }

    private async Task<(BattleResult? Result, string? Error)> SaveResultAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs, bool resetLog = false)
    {
        room.Version++;
        var save = await SaveAsync();
        if (save.Success)
        {
            if (resetLog) battleLogStore?.Replace(room.Id, logs, now);
            else battleLogStore?.Append(room.Id, logs, now);
        }
        return (save.Success ? BuildResult(room, slots, monster, now, logs) : null, save.Error);
    }
    private async Task<(bool Success, string? Error)> SaveAsync()
    {
        dbContext.ChangeTracker.DetectChanges();
        foreach (var entry in dbContext.ChangeTracker.Entries<Character>().Where(entry => entry.State == EntityState.Modified))
            entry.Entity.Version++;
        try
        {
            await dbContext.SaveChangesAsync();
            return (true, null);
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return (false, "ConcurrencyConflict");
        }
    }
    private static bool IsSlotAuto(Room room, SlotCharacter entry, IReadOnlyCollection<int> clearedCharacterIds,
        IReadOnlyCollection<SlotCharacter> slots) =>
        RoomAutoPolicy.IsAuto(room, entry.Slot, clearedCharacterIds, slots.Select(item => item.Slot).ToArray());

    private async Task<List<int>> GetClearedCharacterIdsAsync(int dungeonId)
    {
        var dungeonCode = await dbContext.Dungeons.Where(dungeon => dungeon.Id == dungeonId)
            .Select(dungeon => dungeon.Code).SingleAsync();
        return await dbContext.CharacterBattleMilestones.Where(milestone =>
            milestone.Kind == BattleMilestoneService.DungeonClearKind &&
            milestone.TargetCode == dungeonCode && milestone.Count > 0)
            .Select(milestone => milestone.CharacterId).ToListAsync();
    }

    private static void ClearRoundState(Room room, IEnumerable<SlotCharacter> slots) { room.PreparationStartedAtUtc = null; foreach (var entry in slots) { entry.Slot.IsConfirmed = false; entry.Slot.IsTemporaryAuto = false; entry.Slot.PendingConsumableSlotMask = 0; SkillQueueRules.Clear(entry.Slot); entry.Slot.IsSoulImprintQueued = false; } }
    private static void ResetRunParticipation(IEnumerable<SlotCharacter> slots) { foreach (var entry in slots) { entry.Slot.HasParticipatedInRun = false; entry.Slot.LastParticipatedMonsterId = null; } }
    private static void SetBattleOver(Room room, DateTime now) { room.Status = RoomStatus.BattleOver; room.NextRoundAvailableAtUtc = null; room.RoundCooldownDurationSeconds = null; room.PreparationStartedAtUtc = null; room.BattleEndedAtUtc = now; }
    private DungeonRunService GetDungeonRunService() => dungeonRunService ?? new DungeonRunService(dbContext, rewardService, monsterCombatService, battleMilestones);
    private static BattleResult BuildResult(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs) => new() { RoomId = room.Id, CharacterHp = slots.OrderBy(x => x.Slot.SlotIndex).FirstOrDefault()?.Character.Hp ?? 0, CharacterMaxHp = slots.OrderBy(x => x.Slot.SlotIndex).Select(x => TalentRules.EffectiveMaxHp(x.Character)).FirstOrDefault(), MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp, CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount, RoomStatus = room.Status, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, BattleEndedAtUtc = room.BattleEndedAtUtc, ServerTimeUtc = now, CanExecuteRound = room.Status == RoomStatus.Preparing && slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed) && monster.Hp > 0, IsVictory = room.Status == RoomStatus.BattleOver && monster.Hp <= 0, IsCharacterDead = !slots.Any(x => x.Character.Hp > 0), Logs = logs };

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, User? User, string? Error)> GetBattleContextAsync(int roomId, string? token)
    {
        var (room, slots, monster, roomError) = await GetRoomStateAsync(roomId);
        if (roomError is not null) return (room, slots, monster, null, roomError);
        if (room!.ClosedAtUtc.HasValue) return (room, slots, monster, null, "RoomClosed");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (room, null, null, null, error);
        if (!slots!.Any(x => x.Slot.UserId == user!.Id)) return (room, null, null, user, "NotInRoom");
        var seenAt = DateTime.UtcNow;
        var ownSlots = slots.Where(entry => entry.Slot.UserId == user.Id).ToList();
        if (ownSlots.Any(entry => RoomAutoPolicy.IsOffline(room, entry.Slot, seenAt))) room.Version++;
        foreach (var slot in ownSlots)
            slot.Slot.LastSeenAtUtc = seenAt;
        return (room, slots, monster, user, null);
    }

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, string? Error)> GetRoomStateAsync(int roomId)
    {
        // Apply reservations before reading the party and before an automatic restart or round.
        if (roomService is not null) await roomService.ProcessPendingOperationsAsync(roomId);
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, null, null, "NotFound");
        var slotRows = await dbContext.RoomSlots.Where(x => x.RoomId == roomId && x.CharacterId.HasValue).OrderBy(x => x.SlotIndex).ToListAsync();
        var ids = slotRows.Select(x => x.CharacterId!.Value).ToList();
        var characters = await dbContext.Characters.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var slots = slotRows.Where(x => characters.ContainsKey(x.CharacterId!.Value)).Select(x => new SlotCharacter(x, characters[x.CharacterId!.Value])).ToList();
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        return monster is null ? (room, slots, null, "MonsterNotFound") : (room, slots, monster, null);
    }
    private sealed record SlotCharacter(RoomSlot Slot, Character Character);
}
