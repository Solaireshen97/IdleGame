using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService, ConsumableCatalog consumableCatalog, SkillCatalog skillCatalog, RewardService rewardService, DungeonRunService? dungeonRunService = null, MonsterCombatService? monsterCombatService = null, BattleLogStore? battleLogStore = null, Random? random = null)
{
    private static readonly TimeSpan RoundCooldown = TimeSpan.FromSeconds(BattleRules.RoundCooldownSeconds);
    private static readonly TimeSpan AutoRoundCooldown = TimeSpan.FromSeconds(BattleRules.AutoRoundCooldownSeconds);
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(BattleRules.PreparationTimeoutSeconds);

    public async Task<(BattleResult? Result, string? Error)> StartPreparationAsync(int roomId, string? token, int? expectedRoundNumber = null)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.IsRepeatBattle && room.ExpiresAtUtc is DateTime deadline && now >= deadline &&
            room.Status == RoomStatus.NotStarted && room.RoundNumber == 0)
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, now);
            await SaveResultAsync(room, slots!, monster!, now, ["任务已达到时限，无法开始新一轮战斗。"]);
            return (null, "RoomClosed");
        }
        if (expectedRoundNumber.HasValue && room!.RoundNumber != expectedRoundNumber.Value)
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
        var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
        if (isRoundCoolingDown)
        {
            if (aliveSlots.Where(x => x.Slot.UserId == user.Id).All(x => x.Slot.IsConfirmed))
                return (BuildResult(room, slots, monster, now, ["你的角色已经为下一回合做好准备。"]), "AlreadyPrepared");

            foreach (var entry in aliveSlots.Where(x => x.Slot.UserId == user.Id || IsSlotAuto(room, x, clearedUserIds)))
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

        foreach (var entry in aliveSlots.Where(x => x.Slot.UserId == user.Id || IsSlotAuto(room, x, clearedUserIds))) entry.Slot.IsConfirmed = true;
        if (aliveSlots.All(x => x.Slot.IsConfirmed)) return await ExecutePreparedRoundAsync(room, slots, monster, now, []);
        return await SaveResultAsync(room, slots, monster, now, []);
    }

    public async Task<(BattleResult? Result, string? Error)> SyncAsync(int roomId, string? token)
    {
        var (room, slots, monster, _, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
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
            (room.Status == RoomStatus.BattleOver || room.Status == RoomStatus.NotStarted && room.RoundNumber == 0))
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, now);
            return await SaveResultAsync(room, slots, monster, now, ["任务已达到时限，本轮结束后停止重复战斗。"]);
        }
        var restartedBattle = false;
        if (room.Status == RoomStatus.BattleOver && room.IsRepeatBattle && monster.Hp <= 0 &&
            room.BattleEndedAtUtc is DateTime endedAt && now >= endedAt.AddSeconds(BattleRules.RepeatBattleDelaySeconds))
        {
            monster = await GetDungeonRunService().ResetEncounterAsync(room);
            foreach (var entry in slots) entry.Character.Hp = TalentRules.EffectiveMaxHp(entry.Character);
            await ResetConsumableCooldownsAsync(room.Id);
            await ResetSkillCooldownsAsync(room.Id);
            ClearRoundState(room, slots);
            room.RoundNumber = 0;
            room.RunSequence++;
            room.Status = RoomStatus.NotStarted;
            room.NextRoundAvailableAtUtc = null;
            room.RoundCooldownDurationSeconds = null;
            room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            room.BattleEndedAtUtc = null;
            if (monsterCombatService is not null) await monsterCombatService.EnsureIntentAsync(room, monster);
            battleLogStore?.Clear(room.Id);
            restartedBattle = true;
        }

        var aliveSlots = slots.Where(x => x.Character.Hp > 0).ToList();
        var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
        var allAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(x => IsSlotAuto(room, x, clearedUserIds));
        var stateChanged = restartedBattle;
        var transitionCompleted = false;
        if (room.Status == RoomStatus.WaveTransition && room.NextRoundAvailableAtUtc <= now)
        {
            var transitionDeadline = room.NextRoundAvailableAtUtc.Value;
            room.Status = allAliveMembersAuto ? RoomStatus.Cooldown : RoomStatus.NotStarted;
            room.NextRoundAvailableAtUtc = allAliveMembersAuto
                ? transitionDeadline.AddSeconds(BattleRules.AutoRoundCooldownSeconds - BattleRules.WaveTransitionSeconds)
                : null;
            room.RoundCooldownDurationSeconds = allAliveMembersAuto ? BattleRules.AutoRoundCooldownSeconds : null;
            room.PreparationStartedAtUtc = !allAliveMembersAuto && room.IsPreparationTimeoutEnabled ? now : null;
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
                    logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} 准备超时，本回合临时切换为自动战斗。");
                }
                logs.Add("准备阶段已超时，本回合自动开始。");
                return await ExecutePreparedRoundAsync(room, slots, monster, now, logs);
            }
        }
        if (room.Status == RoomStatus.Preparing)
        {
            return stateChanged ? await SaveResultAsync(room, slots, monster, now, []) : (BuildResult(room, slots, monster, now, []), null);
        }
        if (room.Status == RoomStatus.NotStarted && !allAliveMembersAuto)
        {
            return stateChanged
                ? await SaveResultAsync(room, slots, monster, now, restartedBattle
                    ? ["下一场副本战斗已经就绪，全队生命值已恢复。"]
                    : transitionCompleted ? [$"第 {room.CurrentWaveNumber} 波战斗已经就绪。"] : [])
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
            return await ExecutePreparedRoundAsync(room, slots, monster, now, logs);
        }
        if (stateChanged)
        {
            var logs = restartedBattle
                ? new List<string> { "下一场副本战斗已经就绪，全队生命值已恢复。" }
                : transitionCompleted && allAliveMembersAuto
                    ? new List<string> { $"第 {room.CurrentWaveNumber} 波敌人已经就绪，自动战斗将在回合冷却结束后继续。" }
                    : [];
            return await SaveResultAsync(room, slots, monster, now, logs);
        }
        return (BuildResult(room, slots, monster, now, []), null);
    }

    public async Task<(BattleResult? Result, string? Error)> SetSlotAutoAsync(int roomId, SetSlotAutoRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var entry = slots!.SingleOrDefault(x => x.Slot.SlotIndex == request.SlotIndex);
        if (entry is null || entry.Slot.UserId != user!.Id || entry.Character.Hp <= 0 || (entry.Slot.UserId == room!.OwnerUserId && !entry.Slot.IsMainControl)) return (null, "AutoConfigurationDenied");
        if (request.IsAutoEnabled && !await dbContext.UserDungeonClears.AnyAsync(clear => clear.UserId == user.Id && clear.DungeonId == room!.DungeonId)) return (null, "AutoNotUnlocked");

        var clearedUserIds = !request.IsAutoEnabled ? await GetClearedUserIdsAsync(room!.DungeonId) : [];
        var wasAllAliveMembersAuto = !request.IsAutoEnabled && slots.Where(slot => slot.Character.Hp > 0)
            .All(slot => IsSlotAuto(room!, slot, clearedUserIds));
        entry.Slot.IsAutoEnabled = request.IsAutoEnabled;
        var now = DateTime.UtcNow;
        if (request.IsAutoEnabled && room!.Status == RoomStatus.NotStarted)
        {
            var enabledClearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
            if (slots.Where(slot => slot.Character.Hp > 0).All(slot => IsSlotAuto(room, slot, enabledClearedUserIds)))
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
                room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            }
            else
            {
                room.NextRoundAvailableAtUtc = manualDeadline;
            }
        }
        if (room!.Status == RoomStatus.Preparing && request.IsAutoEnabled)
        {
            entry.Slot.IsConfirmed = true;
            if (slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed))
                return await ExecutePreparedRoundAsync(room, slots, monster!, now, []);
        }

        return await SaveResultAsync(room, slots, monster!, now, []);
    }

    public async Task<(bool Success, string? Error)> QueueConsumableAsync(QueueConsumableRequest request, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(request.RoomId, token);
        if (error is not null) return (false, error);
        if (room!.Status == RoomStatus.BattleOver || monster!.Hp <= 0) return (false, "BattleOver");

        var participant = slots!.SingleOrDefault(entry => entry.Character.Id == request.CharacterId);
        if (participant is null || participant.Slot.UserId != user!.Id) return (false, "NotCharacterOwner");
        if (participant.Character.Hp <= 0) return (false, "CharacterDead");
        if (request.ConsumableSlotIndex is int slotIndex)
        {
            if (slotIndex < 1 || slotIndex > ConsumableRules.SlotCount) return (false, "InvalidSlotIndex");
            var equipped = await dbContext.CharacterConsumableSlots.SingleOrDefaultAsync(
                slot => slot.CharacterId == participant.Character.Id && slot.SlotIndex == slotIndex);
            var item = consumableCatalog.FindItem(equipped?.ItemCode);
            if (item is null) return (false, "NoConsumableEquipped");
            if (participant.Character.Hp >= TalentRules.EffectiveMaxHp(participant.Character)) return (false, "HpFull");
            var stock = await dbContext.CharacterItemStacks.SingleOrDefaultAsync(
                stack => stack.CharacterId == participant.Character.Id && stack.ItemCode == item.Code);
            if (stock?.Quantity is not > 0) return (false, "OutOfStock");
            var cooldown = await dbContext.BattleConsumableCooldowns.SingleOrDefaultAsync(
                entry => entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id &&
                    entry.CooldownGroup == item.CooldownGroup);
            if (cooldown?.ReadyAtRound > room.RoundNumber) return (false, "ConsumableCooldown");
        }

        participant.Slot.PendingConsumableSlotIndex = request.ConsumableSlotIndex;
        room.Version++;
        return await SaveAsync();
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
            var skill = skillCatalog.FindSkill(equipped?.SkillCode);
            var purchasedNodes = (await dbContext.CharacterSkillTalents
                .Where(node => node.CharacterId == participant.Character.Id).ToListAsync())
                .ToDictionary(node => node.NodeCode, node => node.PointsSpent, StringComparer.OrdinalIgnoreCase);
            if (skill is null || !skillCatalog.IsLearned(participant.Character, skill.Code, purchasedNodes))
                return (false, "SkillNotEquipped");
            var cooldown = await dbContext.BattleSkillCooldowns.SingleOrDefaultAsync(entry =>
                entry.RoomId == room.Id && entry.CharacterId == participant.Character.Id && entry.SkillCode == skill.Code);
            if (cooldown?.ReadyAtRound > room.RoundNumber) return (false, "SkillCooldown");
            if (!await CanSkillApplyAsync(room, monster, skill, participant, slots))
                return (false, "NoValidSkillTarget");
            participant.Slot.PendingSkillSlotMask |= mask;
        }
        else participant.Slot.PendingSkillSlotMask &= ~mask;

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
        foreach (var entry in slots!) entry.Character.Hp = TalentRules.EffectiveMaxHp(entry.Character);
        await ResetConsumableCooldownsAsync(room.Id);
        await ResetSkillCooldownsAsync(room.Id);
        ClearRoundState(room, slots);
        room.RoundNumber = 0;
        room.RunSequence++;
        room.Status = RoomStatus.NotStarted;
        room.NextRoundAvailableAtUtc = null;
        room.RoundCooldownDurationSeconds = null;
        room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? DateTime.UtcNow : null;
        room.BattleEndedAtUtc = null;
        if (monsterCombatService is not null) await monsterCombatService.EnsureIntentAsync(room, monster);
        room.Version++;
        var save = await SaveAsync();
        if (save.Success) battleLogStore?.Clear(room.Id);
        return save;
    }

    private async Task<(BattleResult? Result, string? Error)> ExecutePreparedRoundAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        var aliveSlots = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).ToList();
        if (aliveSlots.Count == 0 || aliveSlots.Any(x => !x.Slot.IsConfirmed)) return (null, "PreparationRequired");
        var combatParticipants = slots.OrderBy(x => x.Slot.SlotIndex)
            .Select(x => new MonsterCombatParticipant(x.Slot, x.Character)).ToList();
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var mainWeaponElements = await dbContext.CharacterWeapons
            .Where(weapon => characterIds.Contains(weapon.CharacterId) && weapon.EquippedSlotIndex == WeaponRules.MainSlotIndex)
            .ToDictionaryAsync(weapon => weapon.CharacterId, weapon => weapon.Element);
        var monsterReduction = monsterCombatService is null ? 0m :
            await monsterCombatService.GetModifierAsync(room, "Monster", monster.Id, "ReductionPercent");
        foreach (var entry in aliveSlots)
        {
            var talentEcho = 0m;
            if (await ConsumeTalentStateAsync(room, entry.Character.Id, "talent-sword-rhythm", requireEarlierRound: true)) talentEcho += 25;
            if (await ConsumeTalentStateAsync(room, entry.Character.Id, "talent-guard-echo", requireEarlierRound: false))
                talentEcho += skillCatalog.FindTalentNode("sword-counteroffense")?.ValuePerRank ?? 75;
            var element = mainWeaponElements.TryGetValue(entry.Character.Id, out var mainElement) ? mainElement : (ElementType?)null;
            var statusAttack = monsterCombatService is null ? 0m :
                await monsterCombatService.GetModifierAsync(room, "Character", entry.Character.Id, "AttackPercent");
            var healthPercent = WeaponCombatRules.HealthDamagePercent(entry.Character.Hp, TalentRules.EffectiveMaxHp(entry.Character),
                entry.Character.WeaponStaminaPercent, entry.Character.WeaponEnmityPercent);
            var hits = WeaponCombatRules.RollPercent(entry.Character.WeaponDoubleAttackChancePercent, random) ? 2 : 1;
            for (var hit = 0; hit < hits && monster.Hp > 0; hit++)
            {
                var critical = RollCritical(entry.Character);
                var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(entry.Character), monster.Defense,
                    factors: new DamageFactors(AttackPercent: entry.Character.WeaponAttackBonusPercent + statusAttack + entry.Character.TalentNormalAttackPercent,
                        HealthPercent: healthPercent,
                        CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                        ElementPercent: ElementMatchup.PlayerAttackPercent(element, monster.Element),
                        ReductionPercent: monsterReduction));
                monster.Hp = Math.Max(0, monster.Hp - damage);
                logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} {(hit == 0 ? "普通攻击" : "二连击")} {monster.Name}，造成 {damage} 点伤害{(critical ? "（暴击）" : "")}。");
                var echo = WeaponCombatRules.EchoDamage(damage, entry.Character.WeaponNormalEchoPercent + talentEcho);
                if (monster.Hp > 0 && echo > 0)
                {
                    monster.Hp = Math.Max(0, monster.Hp - echo);
                    logs.Add($"{entry.Slot.SlotIndex}号位 {entry.Character.Name} 对 {monster.Name} 造成 {echo} 点普攻追击伤害。");
                }
            }
            if (monster.Hp <= 0) break;
        }
        var roundDefense = monster.Hp > 0
            ? await ApplyCombatSkillsAsync(room, aliveSlots, monster, mainWeaponElements, logs)
            : default;
        var monsterDefeated = monster.Hp <= 0;
        if (monsterDefeated)
        {
            await ApplyVictoryCooldownTalentAsync(room, characterIds, logs);
            var participants = slots.Where(slot => slot.Slot.UserId.HasValue)
                .Select(slot => new RewardParticipant(slot.Slot.UserId!.Value, slot.Character)).ToList();
            var advance = await GetDungeonRunService().AdvanceAfterDefeatAsync(room, monster, participants, now, logs);
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
                        mainWeaponElements, roundDefense, logs);
                    await monsterCombatService.ResolveEndOfRoundAsync(room, monster, combatParticipants, logs);
                }
                else
                {
                    var target = slots.Where(x => x.Character.Hp > 0).OrderBy(x => x.Slot.SlotIndex).First();
                    var targetElement = mainWeaponElements.TryGetValue(target.Character.Id, out var mainElement) ? mainElement : (ElementType?)null;
                    var damage = DamageCalculator.Calculate(monster.Attack, 0,
                        factors: new DamageFactors(ElementPercent: ElementMatchup.MonsterAttackPercent(monster.Element, targetElement),
                            ReductionPercent: roundDefense.ReductionPercent));
                    target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                    logs.Add($"{monster.Name} 普通攻击 {target.Slot.SlotIndex}号位 {target.Character.Name}，造成 {damage} 点伤害。");
                }

                if (monster.Hp <= 0)
                {
                    await ApplyVictoryCooldownTalentAsync(room, characterIds, logs);
                    var participants = slots.Where(slot => slot.Slot.UserId.HasValue)
                        .Select(slot => new RewardParticipant(slot.Slot.UserId!.Value, slot.Character)).ToList();
                    var advance = await GetDungeonRunService().AdvanceAfterDefeatAsync(room, monster, participants, now, logs);
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
                    var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
                    room.Status = RoomStatus.Cooldown;
                    room.RoundCooldownDurationSeconds = aliveSlots.All(slot => IsSlotAuto(room, slot, clearedUserIds))
                        ? BattleRules.AutoRoundCooldownSeconds : BattleRules.RoundCooldownSeconds;
                    room.NextRoundAvailableAtUtc = now.AddSeconds(room.RoundCooldownDurationSeconds.Value);
                    room.BattleEndedAtUtc = null;
                }
            }
        }
        room.RoundNumber++;
        ClearRoundState(room, slots);
        if (room.IsRepeatBattle && room.Status == RoomStatus.BattleOver &&
            (monster.Hp > 0 || room.ExpiresAtUtc is DateTime deadline && now >= deadline))
        {
            await CharacterActivityManager.CloseBattleRoomAsync(dbContext, room, now);
            logs.Add(monster.Hp > 0 ? "队伍战败，重复战斗已停止。" : "任务已达到时限，本轮结算后停止重复战斗。");
        }
        if (monsterCombatService is not null && room.Status != RoomStatus.BattleOver && monster.Hp > 0)
            await monsterCombatService.EnsureIntentAsync(room, monster);
        return await SaveResultAsync(room, slots, monster, now, logs);
    }

    private async Task<PlayerRoundDefense> ApplyCombatSkillsAsync(Room room, List<SlotCharacter> aliveSlots, Monster monster,
        IReadOnlyDictionary<int, ElementType> mainWeaponElements, List<string> logs)
    {
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
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
        var guardPercent = 0;
        int? guardTargetCharacterId = null;
        int? guardSourceCharacterId = null;
        var usedByCharacter = aliveSlots.ToDictionary(entry => entry.Character.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        async Task<bool> TryUseAsync(SlotCharacter participant, CharacterSkillSlot slot, bool automatic)
        {
            var skill = skillCatalog.FindSkill(slot.SkillCode);
            var used = usedByCharacter[participant.Character.Id];
            if (skill is null || !skillCatalog.IsLearned(participant.Character, skill.Code,
                    purchasedNodes.GetValueOrDefault(participant.Character.Id) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)) ||
                used.Contains(skill.Code)) return false;
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == participant.Character.Id && entry.SkillCode == skill.Code);
            if (cooldown?.ReadyAtRound > room.RoundNumber || automatic && !slot.AutoUseEnabled) return false;
            if (automatic && !await MeetsAutoConditionAsync(room, monster, skill, participant, aliveSlots,
                    slot.AutoHpThresholdPercent)) return false;
            if (!await CanSkillApplyAsync(room, monster, skill, participant, aliveSlots)) return false;

            var applied = false;
            var totalDamage = 0;
            var ranks = purchasedNodes.GetValueOrDefault(participant.Character.Id) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var holyDamageEcho = skill.Code == "acolyte-holy-bolt" && await ConsumeTalentStateAsync(room, participant.Character.Id, "talent-holy-damage", false) ? 20m : 0m;
            var holyHealEcho = SkillCatalog.EffectsFor(skill).Any(effect => effect.Type == "Heal") &&
                await ConsumeTalentStateAsync(room, participant.Character.Id, "talent-holy-heal", false) ? 10m : 0m;
            var healthPercent = WeaponCombatRules.HealthDamagePercent(participant.Character.Hp,
                TalentRules.EffectiveMaxHp(participant.Character), participant.Character.WeaponStaminaPercent,
                participant.Character.WeaponEnmityPercent);
            foreach (var effect in SkillCatalog.EffectsFor(skill))
            {
                switch (effect.Type)
                {
                    case "Damage" when monster.Hp > 0:
                    {
                        var element = mainWeaponElements.TryGetValue(participant.Character.Id, out var mainElement)
                            ? mainElement : (ElementType?)null;
                        var critical = RollCritical(participant.Character, isSkill: true);
                        var attackModifier = monsterCombatService is null ? 0m :
                            await monsterCombatService.GetModifierAsync(room, "Character", participant.Character.Id, "AttackPercent");
                        var monsterReduction = monsterCombatService is null ? 0m :
                            await monsterCombatService.GetModifierAsync(room, "Monster", monster.Id, "ReductionPercent");
                        var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(participant.Character), monster.Defense,
                            effect.Power, new DamageFactors(AttackPercent: participant.Character.WeaponAttackBonusPercent + attackModifier,
                                HealthPercent: healthPercent,
                                CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                                ElementPercent: ElementMatchup.PlayerAttackPercent(element, monster.Element),
                                ReductionPercent: monsterReduction,
                                SkillDamagePercent: participant.Character.WeaponSkillDamagePercent + participant.Character.TalentSkillDamagePercent + holyDamageEcho +
                                    (skill.Code == "acolyte-holy-bolt" ? purchasedNodes.GetValueOrDefault(participant.Character.Id)?.GetValueOrDefault("acolyte-light-training") * 4 ?? 0 : 0)), effect.AttackPowerPercent);
                        monster.Hp = Math.Max(0, monster.Hp - damage);
                        totalDamage += damage;
                        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name} 攻击 {monster.Name}，造成 {damage} 点伤害{(critical ? "（暴击）" : "")}。");
                        applied = true;
                        break;
                    }
                    case "Heal":
                    {
                        var targets = effect.Target == "AllAlive" ? aliveSlots.Where(entry => entry.Character.Hp > 0).ToList() :
                            new[] { effect.Target == "Self" ? participant : FindLowestHpTarget(aliveSlots) }.Where(entry => entry is not null).Select(entry => entry!).ToList();
                        foreach (var target in targets)
                        {
                            var maxHp = TalentRules.EffectiveMaxHp(target.Character);
                            if (target.Character.Hp >= maxHp) continue;
                            var wasBelowHalf = (long)target.Character.Hp * 2 < maxHp;
                            var missing = maxHp - target.Character.Hp;
                            var bonus = participant.Character.TalentHealingDonePercent + target.Character.TalentHealingReceivedPercent + holyHealEcho +
                                (skill.Code == "acolyte-heal" ? purchasedNodes.GetValueOrDefault(participant.Character.Id)?.GetValueOrDefault("acolyte-heal-training") * 4 ?? 0 : 0);
                            var raw = (int)decimal.Floor(RecoveryCalculator.Calculate(maxHp, effect.Power, effect.HealMaxHpPercent) * (1 + bonus / 100m));
                            var healed = Math.Min(raw, maxHp - target.Character.Hp);
                            target.Character.Hp += healed;
                            logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，为 {target.Slot.SlotIndex}号位 {target.Character.Name} 恢复 {healed} 点生命值。");
                            if (monsterCombatService is not null && ranks.GetValueOrDefault("acolyte-mercy") > 0 && wasBelowHalf)
                                await monsterCombatService.ApplyStatusAsync(room, "Character", target.Character.Id, "mercy-ward", 1, logs, $"{target.Slot.SlotIndex}号位 {target.Character.Name}");
                            if (monsterCombatService is not null && ranks.GetValueOrDefault("acolyte-afterglow") > 0 && raw > missing)
                                await monsterCombatService.ApplyStatusAsync(room, "Character", target.Character.Id, "mercy-ward", 1, logs, $"{target.Slot.SlotIndex}号位 {target.Character.Name}");
                            applied = true;
                        }
                        break;
                    }
                    case "Guard":
                    {
                        var target = skill.Code == "sword-parry" ? participant : aliveSlots.FirstOrDefault(entry => entry.Character.Hp > 0);
                        if (target is null || guardPercent >= BattleRules.MaxGuardDamageReductionPercent) break;
                        if (automatic && guardPercent > 0 && guardTargetCharacterId == target.Character.Id) break;
                        var power = skill.Code == "sword-parry" && purchasedNodes.GetValueOrDefault(participant.Character.Id)?.GetValueOrDefault("sword-guard-stance") > 0 ? 50 : effect.Power;
                        guardPercent = Math.Min(BattleRules.MaxGuardDamageReductionPercent, guardPercent + power);
                        guardTargetCharacterId = target.Character.Id;
                        guardSourceCharacterId = participant.Character.Id;
                        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，守护 {target.Slot.SlotIndex}号位 {target.Character.Name}。");
                        applied = true;
                        break;
                    }
                    case "Cleanse" when monsterCombatService is not null:
                    {
                        var targetIds = effect.Target == "Self"
                            ? new[] { participant.Character.Id }
                            : aliveSlots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex)
                                .Select(entry => entry.Character.Id).ToArray();
                        var removed = await monsterCombatService.RemoveFirstStatusAsync(room, "Character", targetIds, false);
                        if (removed is null) break;
                        var target = aliveSlots.Single(entry => entry.Character.Id == removed.TargetId);
                        logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 使用 {skill.Name}，移除了 {target.Slot.SlotIndex}号位 {target.Character.Name} 的 {removed.Name}。");
                        applied = true;
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
                            applied = true;
                        }
                        break;
                    case "ApplyStatus" when monsterCombatService is not null && effect.StatusCode is not null:
                    {
                        var targetType = effect.Target == "Monster" ? "Monster" : "Character";
                        var targetId = effect.Target switch
                        {
                            "Monster" => monster.Id,
                            "Self" => participant.Character.Id,
                            _ => aliveSlots.First(entry => entry.Character.Hp > 0).Character.Id
                        };
                        var targetLabel = effect.Target == "Monster" ? monster.Name :
                            $"{aliveSlots.Single(entry => entry.Character.Id == targetId).Slot.SlotIndex}号位 {aliveSlots.Single(entry => entry.Character.Id == targetId).Character.Name}";
                        applied |= await monsterCombatService.ApplyStatusAsync(room, targetType, targetId, effect.StatusCode,
                            effect.DurationRounds, logs, targetLabel);
                        break;
                    }
                }
            }
            if (!applied) return false;
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
                if (ranks.GetValueOrDefault("acolyte-echo") > 0)
                    await SetTalentStateAsync(room, participant.Character.Id, "talent-holy-heal", 3);
                if (skill.Code == "acolyte-holy-bolt" && monsterCombatService is not null && ranks.GetValueOrDefault("acolyte-judgment") > 0 && monster.Hp > 0)
                    await monsterCombatService.ApplyStatusAsync(room, "Monster", monster.Id, "holy-vulnerability", 1, logs, monster.Name);
                if (skill.Code == "acolyte-holy-bolt" && ranks.GetValueOrDefault("acolyte-light-return") > 0)
                {
                    var target = FindLowestHpTarget(aliveSlots);
                    if (target is not null)
                    {
                        var maxHp = TalentRules.EffectiveMaxHp(target.Character);
                        var healed = Math.Min((int)decimal.Floor(totalDamage * .15m), maxHp - target.Character.Hp);
                        if (healed > 0) { target.Character.Hp += healed; logs.Add($"圣光回流为 {target.Slot.SlotIndex}号位 {target.Character.Name} 恢复 {healed} 点生命值。"); }
                    }
                }
            }
            if (SkillCatalog.EffectsFor(skill).Any(effect => effect.Type == "Heal") && ranks.GetValueOrDefault("acolyte-echo") > 0)
                await SetTalentStateAsync(room, participant.Character.Id, "talent-holy-damage", 3);
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
            var characterEquipment = equipment.Where(slot => slot.CharacterId == participant.Character.Id);
            foreach (var slot in characterEquipment)
            {
                if (monster.Hp <= 0) break;
                if ((participant.Slot.PendingSkillSlotMask & SkillRules.SlotMask(slot.SlotIndex)) == 0)
                    await TryUseAsync(participant, slot, automatic: true);
            }
            if (monster.Hp <= 0) break;
        }
        return new PlayerRoundDefense(guardPercent, guardTargetCharacterId, guardSourceCharacterId);
    }

    private async Task<bool> CanSkillApplyAsync(Room room, Monster monster, CombatSkillOptions skill,
        SlotCharacter participant, IReadOnlyList<SlotCharacter> slots)
    {
        var alive = slots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList();
        foreach (var effect in SkillCatalog.EffectsFor(skill))
        {
            switch (effect.Type)
            {
                case "Damage" when monster.Hp > 0:
                case "Guard" when alive.Count > 0:
                    return true;
                case "Heal":
                    var healTarget = effect.Target == "Self" ? participant : FindLowestHpTarget(alive);
                    if (healTarget is not null && healTarget.Character.Hp < TalentRules.EffectiveMaxHp(healTarget.Character))
                        return true;
                    break;
                case "Cleanse" when monsterCombatService is not null:
                    var cleanseTargets = effect.Target == "Self"
                        ? new[] { participant.Character.Id }
                        : alive.Select(entry => entry.Character.Id).ToArray();
                    if (await monsterCombatService.HasRemovableStatusAsync(room, "Character", cleanseTargets, false))
                        return true;
                    break;
                case "Dispel" when monsterCombatService is not null:
                    if (await monsterCombatService.HasRemovableStatusAsync(room, "Monster", [monster.Id], true))
                        return true;
                    break;
                case "Interrupt" when monsterCombatService is not null:
                    if (await monsterCombatService.CanInterruptCurrentIntentAsync(room, monster)) return true;
                    break;
                case "ApplyStatus" when monsterCombatService is not null && effect.StatusCode is not null:
                    if (effect.Target != "Monster" || monster.Hp > 0) return true;
                    break;
            }
        }
        return false;
    }

    private async Task<bool> MeetsAutoConditionAsync(Room room, Monster monster, CombatSkillOptions skill,
        SlotCharacter participant, IReadOnlyList<SlotCharacter> slots, int hpThresholdPercent)
    {
        var alive = slots.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList();
        return SkillCatalog.AutoConditionFor(skill) switch
        {
            "Always" => true,
            "LowestHpBelowThreshold" => GetHpConditionTarget(skill, participant, alive) is { } target &&
                (long)target.Character.Hp * 100 <=
                (long)TalentRules.EffectiveMaxHp(target.Character) * hpThresholdPercent,
            "AllyHasDebuff" => monsterCombatService is not null &&
                await monsterCombatService.HasRemovableStatusAsync(room, "Character",
                    alive.Select(entry => entry.Character.Id).ToArray(), false),
            "MonsterHasBuff" => monsterCombatService is not null &&
                await monsterCombatService.HasRemovableStatusAsync(room, "Monster", [monster.Id], true),
            "InterruptibleIntent" => monsterCombatService is not null &&
                await monsterCombatService.CanInterruptCurrentIntentAsync(room, monster),
            _ => false
        };
    }

    private static SlotCharacter? GetHpConditionTarget(CombatSkillOptions skill, SlotCharacter participant,
        IReadOnlyList<SlotCharacter> alive)
    {
        var effects = SkillCatalog.EffectsFor(skill);
        if (effects.Any(effect => effect.Type == "Guard")) return alive.FirstOrDefault();
        if (effects.Any(effect => effect.Type == "Heal" && effect.Target == "Self")) return participant;
        return FindLowestHpTarget(alive);
    }

    private static SlotCharacter? FindLowestHpTarget(IEnumerable<SlotCharacter> slots) => slots
        .Where(entry => entry.Character.Hp > 0)
        .OrderBy(entry => (long)entry.Character.Hp * 100 / TalentRules.EffectiveMaxHp(entry.Character))
        .ThenBy(entry => entry.Slot.SlotIndex).FirstOrDefault();

    private bool RollCritical(Character character, bool isSkill = false)
    {
        var chance = character.WeaponCriticalChancePercent + (isSkill ? character.TalentSkillCriticalChancePercent : 0);
        return WeaponCombatRules.RollPercent(chance, random);
    }

    private async Task ApplyCombatConsumablesAsync(Room room, List<SlotCharacter> aliveSlots, List<string> logs)
    {
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var equipment = await dbContext.CharacterConsumableSlots
            .Where(slot => characterIds.Contains(slot.CharacterId) && slot.ItemCode != null)
            .OrderBy(slot => slot.SlotIndex)
            .ToListAsync();
        if (equipment.Count == 0) return;
        var stocks = await dbContext.CharacterItemStacks
            .Where(stack => characterIds.Contains(stack.CharacterId))
            .ToListAsync();
        var cooldowns = await dbContext.BattleConsumableCooldowns
            .Where(cooldown => cooldown.RoomId == room.Id && characterIds.Contains(cooldown.CharacterId))
            .ToListAsync();

        foreach (var participant in aliveSlots)
        {
            var character = participant.Character;
            var characterEquipment = equipment.Where(slot => slot.CharacterId == character.Id).ToList();
            if (character.Hp >= TalentRules.EffectiveMaxHp(character)) continue;

            bool TryUse(CharacterConsumableSlot slot, bool automatic)
            {
                var item = consumableCatalog.FindItem(slot.ItemCode);
                if (item is null) return false;
                var maxHp = TalentRules.EffectiveMaxHp(character);
                if (automatic && (!slot.AutoUseEnabled || (long)character.Hp * 100 > (long)maxHp * slot.AutoHpThresholdPercent))
                    return false;
                var stock = stocks.SingleOrDefault(stack => stack.CharacterId == character.Id && stack.ItemCode == item.Code);
                if (stock?.Quantity is not > 0) return false;
                var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == character.Id &&
                    string.Equals(entry.CooldownGroup, item.CooldownGroup, StringComparison.OrdinalIgnoreCase));
                if (cooldown?.ReadyAtRound > room.RoundNumber) return false;

                var raw = (int)decimal.Floor(ConsumableCatalog.HealAmountFor(item, maxHp) * (1 + character.TalentHealingReceivedPercent / 100m));
                var healed = Math.Min(raw, maxHp - character.Hp);
                if (healed <= 0) return false;
                character.Hp += healed;
                stock.Quantity--;
                stock.Version++;
                if (cooldown is null)
                {
                    cooldown = new BattleConsumableCooldown
                    {
                        RoomId = room.Id,
                        CharacterId = character.Id,
                        CooldownGroup = item.CooldownGroup
                    };
                    cooldowns.Add(cooldown);
                    dbContext.BattleConsumableCooldowns.Add(cooldown);
                }
                cooldown.ReadyAtRound = checked(room.RoundNumber + item.CooldownRounds + 1);
                logs.Add($"{participant.Slot.SlotIndex}号位 {character.Name} 使用 {item.Name}，恢复 {healed} 点生命值。");
                return true;
            }

            var selected = characterEquipment.SingleOrDefault(slot => slot.SlotIndex == participant.Slot.PendingConsumableSlotIndex);
            if (selected is not null && TryUse(selected, automatic: false)) continue;
            foreach (var slot in characterEquipment)
            {
                if (TryUse(slot, automatic: true)) break;
            }
        }
    }

    private async Task ResetConsumableCooldownsAsync(int roomId)
    {
        foreach (var cooldown in await dbContext.BattleConsumableCooldowns.Where(entry => entry.RoomId == roomId).ToListAsync())
            cooldown.ReadyAtRound = 0;
    }

    private async Task ResetSkillCooldownsAsync(int roomId)
    {
        foreach (var cooldown in await dbContext.BattleSkillCooldowns.Where(entry => entry.RoomId == roomId).ToListAsync())
            cooldown.ReadyAtRound = 0;
    }

    private async Task SetTalentStateAsync(Room room, int characterId, string code, int durationRounds)
    {
        var state = dbContext.BattleStatusEffects.Local.FirstOrDefault(item => item.RoomId == room.Id &&
            item.RunSequence == room.RunSequence && item.TargetType == "Character" && item.TargetId == characterId && item.EffectCode == code)
            ?? await dbContext.BattleStatusEffects.SingleOrDefaultAsync(item => item.RoomId == room.Id &&
                item.RunSequence == room.RunSequence && item.TargetType == "Character" && item.TargetId == characterId && item.EffectCode == code);
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

    private async Task<(BattleResult? Result, string? Error)> SaveResultAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        room.Version++;
        var save = await SaveAsync();
        if (save.Success) battleLogStore?.Append(room.Id, logs, now);
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
            return (false, "ConcurrencyConflict");
        }
    }
    private static bool IsSlotAuto(Room room, SlotCharacter entry, List<int> clearedUserIds) =>
        entry.Slot.UserId.HasValue && clearedUserIds.Contains(entry.Slot.UserId.Value) &&
        (entry.Slot.IsAutoEnabled || (entry.Slot.UserId == room.OwnerUserId && !entry.Slot.IsMainControl));

    private Task<List<int>> GetClearedUserIdsAsync(int dungeonId) =>
        dbContext.UserDungeonClears.Where(clear => clear.DungeonId == dungeonId).Select(clear => clear.UserId).ToListAsync();

    private static void ClearRoundState(Room room, IEnumerable<SlotCharacter> slots) { room.PreparationStartedAtUtc = null; foreach (var entry in slots) { entry.Slot.IsConfirmed = false; entry.Slot.IsTemporaryAuto = false; entry.Slot.PendingConsumableSlotIndex = null; entry.Slot.PendingSkillSlotMask = 0; } }
    private static void SetBattleOver(Room room, DateTime now) { room.Status = RoomStatus.BattleOver; room.NextRoundAvailableAtUtc = null; room.RoundCooldownDurationSeconds = null; room.PreparationStartedAtUtc = null; room.BattleEndedAtUtc = now; }
    private DungeonRunService GetDungeonRunService() => dungeonRunService ?? new DungeonRunService(dbContext, rewardService, monsterCombatService);
    private static BattleResult BuildResult(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs) => new() { RoomId = room.Id, CharacterHp = slots.OrderBy(x => x.Slot.SlotIndex).FirstOrDefault()?.Character.Hp ?? 0, CharacterMaxHp = slots.OrderBy(x => x.Slot.SlotIndex).Select(x => TalentRules.EffectiveMaxHp(x.Character)).FirstOrDefault(), MonsterHp = monster.Hp, MonsterMaxHp = monster.MaxHp, CurrentWaveNumber = room.CurrentWaveNumber, TotalWaveCount = room.TotalWaveCount, RoomStatus = room.Status, NextRoundAvailableAtUtc = room.NextRoundAvailableAtUtc, BattleEndedAtUtc = room.BattleEndedAtUtc, ServerTimeUtc = now, CanExecuteRound = room.Status == RoomStatus.Preparing && slots.Where(x => x.Character.Hp > 0).All(x => x.Slot.IsConfirmed) && monster.Hp > 0, IsVictory = room.Status == RoomStatus.BattleOver && monster.Hp <= 0, IsCharacterDead = !slots.Any(x => x.Character.Hp > 0), Logs = logs };

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, User? User, string? Error)> GetBattleContextAsync(int roomId, string? token)
    {
        var (room, slots, monster, roomError) = await GetRoomStateAsync(roomId);
        if (roomError is not null) return (room, slots, monster, null, roomError);
        if (room!.ClosedAtUtc.HasValue) return (room, slots, monster, null, "RoomClosed");
        var (user, error) = await userService.GetCurrentUserEntityAsync(token);
        if (error is not null) return (room, null, null, null, error);
        if (!slots!.Any(x => x.Slot.UserId == user!.Id)) return (room, null, null, user, "NotInRoom");
        return (room, slots, monster, user, null);
    }

    private async Task<(Room? Room, List<SlotCharacter>? Slots, Monster? Monster, string? Error)> GetRoomStateAsync(int roomId)
    {
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
