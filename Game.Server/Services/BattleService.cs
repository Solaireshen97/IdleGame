using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public partial class BattleService(GameDbContext dbContext, UserService userService, ConsumableCatalog consumableCatalog, SkillCatalog skillCatalog, RewardService rewardService, DungeonRunService? dungeonRunService = null, MonsterCombatService? monsterCombatService = null, BattleLogStore? battleLogStore = null, Random? random = null, BattleMilestoneService? battleMilestones = null, WeaponCatalog? weaponCatalog = null, SoulImprintCatalog? soulImprintCatalog = null, PartyScalingService? partyScalingService = null, RoomService? roomService = null, BattleEffectExecutor? battleEffects = null, ProfessionMechanicCatalog? mechanics = null, DungeonRunRulesService? runRules = null)
{
    private readonly PartyScalingService _partyScaling = partyScalingService ?? new(dbContext, PartyScalingCatalog.Default, runRules);
    private readonly SkillBattleSnapshotFactory _skillSnapshots = new(dbContext, skillCatalog, monsterCombatService, mechanics);
    private readonly BattleEventCollector _events = battleEffects?.Events ?? monsterCombatService?.Statuses.Events ?? new();
    private BattleStatusService? _statusService;
    private BattleStatusService Statuses => _statusService ??= _effectExecutor?.Statuses ?? monsterCombatService?.Statuses ??
        new BattleStatusService(dbContext, new BattleStatusCatalog(Options.Create(new MonsterCombatOptions())), _events, mechanics, runRules);
    private BattleGuardService? _guardService;
    private BattleGuardService _guards => _guardService ??= _effectExecutor?.Guards ?? new(Statuses);
    private BattleEffectExecutor? _effectExecutor = battleEffects;
    private BattleEffectExecutor Effects => _effectExecutor ??= new(skillCatalog, Statuses, _guards,
        new BattleDamageService(Statuses, _guards, random, _events, runRules, phases: monsterCombatService?.Phases));
    private BattleRoundExecutor? _roundExecutor;
    private BattleRoundExecutor Rounds => _roundExecutor ??= new(dbContext, consumableCatalog, skillCatalog,
        Statuses, Effects, monsterCombatService, weaponCatalog, soulImprintCatalog, random, mechanics);
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
            await Statuses.ClearRunAsync(room);
            var logs = new List<string>();
            if (aliveSlots.Count == 0) await rewardService.SettleAsync(room, false, now, logs);
            logs.Add(monster.Hp <= 0 ? "怪物已经被击败，请重置房间。" : "全队已经战败，无法继续战斗。");
            return await SaveResultAsync(room, slots, monster, now, logs);
        }

        if (!aliveSlots.Any(x => x.Slot.UserId == user!.Id)) return (null, "NoOwnedAliveCharacters");
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId, slots.Select(entry => entry.Character.Id).ToArray());
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
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId, slots.Select(entry => entry.Character.Id).ToArray());
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
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception)) { return (null, "ConcurrencyConflict"); }
        return await SyncCoreAsync(room!, slots!, monster!);
    }

    public async Task<(BattleResult? Result, string? Error)> SyncRoomAsync(int roomId)
    {
        var (room, slots, monster, error) = await GetRoomStateAsync(roomId);
        if (error is not null) return (null, error);
        return await SyncCoreAsync(room!, slots!, monster!);
    }

    private async Task<(BattleResult? Result, string? Error)> SyncCoreAsync(Room room, List<BattleParticipant> slots, Monster monster)
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
            monster = await RunLifecycle.BeginNextRunAsync(room, slots, respawnAt, automatic: true);
            restartedBattle = true;
        }

        var aliveSlots = slots.Where(x => x.Character.Hp > 0).ToList();
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId, slots.Select(entry => entry.Character.Id).ToArray());
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
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room!.DungeonId, slots.Select(member => member.Character.Id).ToArray());
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
            var skill = skillCatalog.Resolve(participant.Character, equipped?.SkillCode, professionLevels);
            if (skill is null)
                return (false, "SkillNotEquipped");
            var cooldown = await dbContext.BattleSkillCooldowns.SingleOrDefaultAsync(entry =>
                entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id && entry.SkillCode == skill.Code);
            if ((cooldown?.ReadyAtRound ?? skill.InitialCooldownRounds) > room.RoundNumber)
                return (false, "SkillCooldown");
            if (request.TargetCharacterId.HasValue &&
                (!SkillBattlePolicy.CanChooseAllyTarget(skill) ||
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
            if (!await Rounds.CanSoulImprintApplyAsync(room, monster, definition, participant, slots))
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
        await RunLifecycle.BeginNextRunAsync(room, slots!, DateTime.UtcNow, automatic: false);
        room.Version++;
        var save = await SaveAsync();
        if (save.Success) battleLogStore?.Clear(room.Id);
        return save;
    }

    private async Task<(BattleResult? Result, string? Error)> ExecutePreparedRoundAsync(Room room, List<BattleParticipant> slots, Monster monster, DateTime now, List<string> logs, bool resetLog = false)
    {
        var aliveSlots = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).ToList();
        if (aliveSlots.Count == 0 || aliveSlots.Any(x => !x.Slot.IsConfirmed)) return (null, "PreparationRequired");
        await _partyScaling.SynchronizeAsync(room, slots.Select(entry => entry.Slot).ToList());
        var clearedCharacterIds = await GetClearedCharacterIdsAsync(room.DungeonId, slots.Select(entry => entry.Character.Id).ToArray());
        foreach (var entry in aliveSlots)
        {
            entry.Slot.HasParticipatedInRun = true;
            entry.Slot.LastParticipatedMonsterId = monster.Id;
        }
        await rewardService.CaptureDungeonParticipantsAsync(room, aliveSlots.Select(entry => entry.Character.Id));
        // The scope spans calculation, wave/reward changes and the one atomic save.
        using var statusSettlement = await Statuses.BeginSettlementAsync(room);
        using var recording = _events.Begin(room, monster, slots.OrderBy(entry => entry.Slot.SlotIndex).ToList());
        var autoCharacterIds = aliveSlots.Where(entry => !entry.Slot.IsTemporaryAuto &&
            IsSlotAuto(room, entry, clearedCharacterIds, slots)).Select(entry => entry.Character.Id).ToHashSet();
        var outcome = await Rounds.ExecuteAsync(room, monster, slots, autoCharacterIds, logs);
        if (outcome == BattleRoundOutcome.MonsterDefeated)
        {
            var participants = slots.Where(slot => slot.Slot.UserId.HasValue)
                .Select(slot => new RewardParticipant(slot.Slot.UserId!.Value, slot.Character)).ToList();
            var advance = await GetDungeonRunService().AdvanceAfterDefeatAsync(room, monster, participants, now, logs,
                slots.Where(slot => slot.Slot.LastParticipatedMonsterId == monster.Id).Select(slot => slot.Character.Id).ToList(),
                slots.Where(slot => slot.Slot.HasParticipatedInRun).Select(slot => slot.Character.Id).ToList());
            if (advance.Error is not null) return (null, advance.Error);
            monster = advance.ActiveMonster;
        }
        else if (outcome == BattleRoundOutcome.PartyDefeated)
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
        await Rounds.FinishRoundEffectsAsync(room, slots);
        if (room.Status == RoomStatus.BattleOver) await Statuses.ClearRunAsync(room);
        room.RoundNumber++;
        await Rounds.UpdateTemporaryWeaponBonusesAsync(room, slots);
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

    private async Task<bool> CanSkillApplyAsync(Room room, Monster monster, CharacterSkillDefinition skill,
        BattleParticipant participant, IReadOnlyList<BattleParticipant> slots, int? chosenTargetId = null)
    {
        var snapshot = await _skillSnapshots.CaptureAsync(room, monster, skill, participant.Character.Id,
            slots.Select(entry => (entry.Slot, entry.Character)));
        return SkillBattlePolicy.HasApplicableEffect(skill, snapshot, chosenTargetId);
    }

    private async Task<(BattleResult? Result, string? Error)> SaveResultAsync(Room room, List<BattleParticipant> slots, Monster monster, DateTime now, List<string> logs, bool resetLog = false)
    {
        room.Version++;
        var save = await SaveAsync();
        if (!save.Success) return (null, save.Error);
        var events = _events.Snapshot(room);
        var published = resetLog ? battleLogStore?.Replace(room.Id, logs, now, events) : battleLogStore?.Append(room.Id, logs, now, events);
        var result = BuildResult(room, slots, monster, now, logs);
        result.Events = published ?? events;
        result.BattleHistoryEpoch = battleLogStore?.Epoch ?? "";
        return (result, null);
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
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            dbContext.ChangeTracker.Clear();
            return (false, "ConcurrencyConflict");
        }
    }
    private static bool IsSlotAuto(Room room, BattleParticipant entry, IReadOnlyCollection<int> clearedCharacterIds,
        IReadOnlyCollection<BattleParticipant> slots) =>
        RoomAutoPolicy.IsAuto(room, entry.Slot, clearedCharacterIds, slots.Select(item => item.Slot).ToArray());

    private async Task<List<int>> GetClearedCharacterIdsAsync(int dungeonId, IReadOnlyCollection<int> characterIds)
    {
        if (characterIds.Count == 0) return [];
        var dungeonCode = await dbContext.Dungeons.Where(dungeon => dungeon.Id == dungeonId)
            .Select(dungeon => dungeon.Code).SingleAsync();
        return await dbContext.CharacterBattleMilestones.Where(milestone =>
            characterIds.Contains(milestone.CharacterId) && milestone.Kind == BattleMilestoneService.DungeonClearKind &&
            milestone.TargetCode == dungeonCode && milestone.Count > 0)
            .Select(milestone => milestone.CharacterId).ToListAsync();
    }

    private static void ClearRoundState(Room room, IEnumerable<BattleParticipant> slots) => DungeonRunLifecycleService.ClearRoundState(room, slots);
    private static void SetBattleOver(Room room, DateTime now) { room.Status = RoomStatus.BattleOver; room.NextRoundAvailableAtUtc = null; room.RoundCooldownDurationSeconds = null; room.PreparationStartedAtUtc = null; room.BattleEndedAtUtc = now; }
    private DungeonRunService GetDungeonRunService() => dungeonRunService ?? new DungeonRunService(dbContext, rewardService, monsterCombatService, battleMilestones, runRules: runRules);
    private DungeonRunLifecycleService RunLifecycle => new(dbContext, GetDungeonRunService(), _partyScaling, monsterCombatService, runRules);
    private static BattleResult BuildResult(Room room, List<BattleParticipant> slots, Monster monster, DateTime now, List<string> logs) => new() { RoomId = room.Id, CharacterHp = slots.OrderBy(x => x.Slot.SlotIndex).FirstOrDefault()?.Character.Hp ?? 0, CharacterMaxHp = slots.OrderBy(x => x.Slot.SlotIndex).Select(x => TalentRules.EffectiveMaxHp(x.Character)).FirstOrDefault(), MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp, CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount, RoomStatus = room.Status, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, BattleEndedAtUtc = room.BattleEndedAtUtc, ServerTimeUtc = now, CanExecuteRound = room.Status == RoomStatus.Preparing && slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed) && monster.Hp > 0, IsVictory = room.Status == RoomStatus.BattleOver && monster.Hp <= 0, IsCharacterDead = !slots.Any(x => x.Character.Hp > 0), Logs = logs };

    private async Task<(Room? Room, List<BattleParticipant>? Slots, Monster? Monster, User? User, string? Error)> GetBattleContextAsync(int roomId, string? token)
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

    private async Task<(Room? Room, List<BattleParticipant>? Slots, Monster? Monster, string? Error)> GetRoomStateAsync(int roomId)
    {
        // Apply reservations before reading the party and before an automatic restart or round.
        if (roomService is not null) await roomService.ProcessPendingOperationsAsync(roomId);
        var room = await dbContext.Rooms.FirstOrDefaultAsync(x => x.Id == roomId);
        if (room is null) return (null, null, null, "NotFound");
        var slotRows = await dbContext.RoomSlots.Where(x => x.RoomId == roomId && x.CharacterId.HasValue).OrderBy(x => x.SlotIndex).ToListAsync();
        var ids = slotRows.Select(x => x.CharacterId!.Value).ToList();
        var characters = await dbContext.Characters.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var slots = slotRows.Where(x => characters.ContainsKey(x.CharacterId!.Value)).Select(x => new BattleParticipant(x, characters[x.CharacterId!.Value])).ToList();
        var monster = await dbContext.Monsters.FindAsync(room.MonsterId);
        return monster is null ? (room, slots, null, "MonsterNotFound") : (room, slots, monster, null);
    }
}
