using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public class BattleService(GameDbContext dbContext, UserService userService, ConsumableCatalog consumableCatalog, SkillCatalog skillCatalog, RewardService rewardService, DungeonRunService? dungeonRunService = null, MonsterCombatService? monsterCombatService = null)
{
    private static readonly TimeSpan RoundCooldown = TimeSpan.FromSeconds(BattleRules.RoundCooldownSeconds);
    private static readonly TimeSpan AutoRoundCooldown = TimeSpan.FromSeconds(BattleRules.AutoRoundCooldownSeconds);
    private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(BattleRules.PreparationTimeoutSeconds);

    public async Task<(BattleResult? Result, string? Error)> StartPreparationAsync(int roomId, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (null, error);
        var now = DateTime.UtcNow;
        if (room!.Status == RoomStatus.BattleOver) return (BuildResult(room, slots!, monster!, now, ["Battle is over. Please reset the room."]), "BattleOver");
        if (room.Status == RoomStatus.WaveTransition) return (BuildResult(room, slots!, monster!, now, ["The next enemy is approaching."]), "WaveTransition");
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc > now) return (BuildResult(room, slots!, monster!, now, ["Round is on cooldown."]), "RoundCooldown");

        var aliveSlots = slots!.Where(x => x.Character.Hp > 0).ToList();
        if (monster!.Hp <= 0 || aliveSlots.Count == 0)
        {
            ClearRoundState(room, slots);
            SetBattleOver(room, now);
            var logs = new List<string>();
            if (aliveSlots.Count == 0) await rewardService.SettleAsync(room, false, now, logs);
            logs.Add(monster.Hp <= 0 ? "Monster is already defeated. Please reset the room." : "All characters are defeated and cannot battle.");
            return await SaveResultAsync(room, slots, monster, now, logs);
        }

        if (!aliveSlots.Any(x => x.Slot.UserId == user!.Id)) return (null, "NoOwnedAliveCharacters");
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
            return (BuildResult(room, slots, monster, now, ["Your characters are already prepared."]), "AlreadyPrepared");
        }

        var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
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
            restartedBattle = true;
        }

        var aliveSlots = slots.Where(x => x.Character.Hp > 0).ToList();
        var clearedUserIds = await GetClearedUserIdsAsync(room.DungeonId);
        var allAliveMembersAuto = aliveSlots.Count > 0 && aliveSlots.All(x => IsSlotAuto(room, x, clearedUserIds));
        var stateChanged = restartedBattle;
        var transitionCompleted = false;
        if (room.Status == RoomStatus.WaveTransition && room.NextRoundAvailableAtUtc <= now)
        {
            room.Status = RoomStatus.NotStarted;
            room.NextRoundAvailableAtUtc = null;
            room.RoundCooldownDurationSeconds = null;
            room.PreparationStartedAtUtc = room.IsPreparationTimeoutEnabled ? now : null;
            stateChanged = true;
            transitionCompleted = true;
        }
        if (room.Status == RoomStatus.Cooldown && room.NextRoundAvailableAtUtc <= now && !allAliveMembersAuto)
        {
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
                    logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} timed out and was temporarily set to Auto.");
                }
                logs.Add("Preparation timed out. The round starts automatically.");
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
                    ? ["The next dungeon battle is ready. The party is at full HP."]
                    : transitionCompleted ? [$"Wave {room.CurrentWaveNumber} is ready."] : [])
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
                ? new List<string> { "The next dungeon battle begins with the party at full HP.", "All members are on Auto. The round starts automatically." }
                : transitionCompleted
                    ? new List<string> { $"Wave {room.CurrentWaveNumber} begins.", "All members are on Auto. The round starts automatically." }
                    : new List<string> { "All members are on Auto. The round starts automatically." };
            return await ExecutePreparedRoundAsync(room, slots, monster, now, logs);
        }
        return restartedBattle
            ? await SaveResultAsync(room, slots, monster, now, ["The next dungeon battle is ready. The party is at full HP."])
            : (BuildResult(room, slots, monster, now, []), null);
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
                .Where(node => node.CharacterId == participant.Character.Id).Select(node => node.NodeCode).ToListAsync())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
        if (room!.Status != RoomStatus.Preparing || slots!.Where(x => x.Character.Hp > 0).Any(x => !x.Slot.IsConfirmed)) return (BuildResult(room, slots, monster!, now, ["Preparation is required before executing a round."]), "PreparationRequired");
        return await ExecutePreparedRoundAsync(room, slots, monster!, now, []);
    }

    public Task<(BattleResult? Result, string? Error)> ExecuteBattleAsync(int roomId, string? token) => ExecuteRoundAsync(roomId, token);

    public async Task<(bool Success, string? Error)> ResetBattleAsync(int roomId, string? token)
    {
        var (room, slots, monster, user, error) = await GetBattleContextAsync(roomId, token);
        if (error is not null) return (false, error);
        if (room!.OwnerUserId != user!.Id) return (false, "NotOwner");
        if (room.Status != RoomStatus.BattleOver) return (false, "BattleNotOver");
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
        return await SaveAsync();
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
            var element = mainWeaponElements.TryGetValue(entry.Character.Id, out var mainElement) ? mainElement : (ElementType?)null;
            var critical = RollCritical(entry.Character);
            var statusAttack = monsterCombatService is null ? 0m :
                await monsterCombatService.GetModifierAsync(room, "Character", entry.Character.Id, "AttackPercent");
            var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(entry.Character), monster.Defense,
                factors: new DamageFactors(AttackPercent: entry.Character.WeaponAttackBonusPercent + statusAttack,
                    CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                    ElementPercent: ElementMatchup.PlayerAttackPercent(element, monster.Element),
                    ReductionPercent: monsterReduction));
            monster.Hp = Math.Max(0, monster.Hp - damage);
            logs.Add($"Slot {entry.Slot.SlotIndex} {entry.Character.Name} attacks {monster.Name} for {damage} damage{(critical ? " (critical)" : "")}.");
            if (monster.Hp <= 0) break;
        }
        var roundDefense = monster.Hp > 0
            ? await ApplyCombatSkillsAsync(room, aliveSlots, monster, mainWeaponElements, logs)
            : default;
        var monsterDefeated = monster.Hp <= 0;
        if (monsterDefeated)
        {
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
                logs.Add("All characters are defeated.");
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
                    var damage = DamageCalculator.Calculate(monster.Attack, TalentRules.EffectiveDefense(target.Character),
                        factors: new DamageFactors(ElementPercent: ElementMatchup.MonsterAttackPercent(monster.Element, targetElement),
                            ReductionPercent: roundDefense.ReductionPercent));
                    target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                    logs.Add($"{monster.Name} attacks Slot {target.Slot.SlotIndex} {target.Character.Name} for {damage} damage.");
                }

                if (monster.Hp <= 0)
                {
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
                    logs.Add("All characters are defeated.");
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
                group => group.Select(node => node.NodeCode).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var guardPercent = 0;
        int? guardTargetCharacterId = null;
        var usedByCharacter = aliveSlots.ToDictionary(entry => entry.Character.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        async Task<bool> TryUseAsync(SlotCharacter participant, CharacterSkillSlot slot, bool automatic)
        {
            var skill = skillCatalog.FindSkill(slot.SkillCode);
            var used = usedByCharacter[participant.Character.Id];
            if (skill is null || !skillCatalog.IsLearned(participant.Character, skill.Code,
                    purchasedNodes.GetValueOrDefault(participant.Character.Id) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)) ||
                used.Contains(skill.Code)) return false;
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == participant.Character.Id && entry.SkillCode == skill.Code);
            if (cooldown?.ReadyAtRound > room.RoundNumber || automatic && !slot.AutoUseEnabled) return false;
            if (automatic && !await MeetsAutoConditionAsync(room, monster, skill, participant, aliveSlots,
                    slot.AutoHpThresholdPercent)) return false;
            if (!await CanSkillApplyAsync(room, monster, skill, participant, aliveSlots)) return false;

            var applied = false;
            foreach (var effect in SkillCatalog.EffectsFor(skill))
            {
                switch (effect.Type)
                {
                    case "Damage" when monster.Hp > 0:
                    {
                        var element = mainWeaponElements.TryGetValue(participant.Character.Id, out var mainElement)
                            ? mainElement : (ElementType?)null;
                        var critical = RollCritical(participant.Character);
                        var attackModifier = monsterCombatService is null ? 0m :
                            await monsterCombatService.GetModifierAsync(room, "Character", participant.Character.Id, "AttackPercent");
                        var monsterReduction = monsterCombatService is null ? 0m :
                            await monsterCombatService.GetModifierAsync(room, "Monster", monster.Id, "ReductionPercent");
                        var damage = DamageCalculator.Calculate(TalentRules.EffectiveAttack(participant.Character), monster.Defense,
                            effect.Power, new DamageFactors(AttackPercent: participant.Character.WeaponAttackBonusPercent + attackModifier,
                                CriticalPercent: critical ? BattleRules.CriticalDamageBonusPercent : 0,
                                ElementPercent: ElementMatchup.PlayerAttackPercent(element, monster.Element),
                                ReductionPercent: monsterReduction));
                        monster.Hp = Math.Max(0, monster.Hp - damage);
                        logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} uses {skill.Name} on {monster.Name} for {damage} damage{(critical ? " (critical)" : "")}.");
                        applied = true;
                        break;
                    }
                    case "Heal":
                    {
                        var target = effect.Target == "Self" ? participant : FindLowestHpTarget(aliveSlots);
                        if (target is null || target.Character.Hp >= TalentRules.EffectiveMaxHp(target.Character)) break;
                        var healed = Math.Min(effect.Power, TalentRules.EffectiveMaxHp(target.Character) - target.Character.Hp);
                        target.Character.Hp += healed;
                        logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} uses {skill.Name} on Slot {target.Slot.SlotIndex} {target.Character.Name} and restores {healed} HP.");
                        applied = true;
                        break;
                    }
                    case "Guard":
                    {
                        var front = aliveSlots.FirstOrDefault(entry => entry.Character.Hp > 0);
                        if (front is null || guardPercent >= BattleRules.MaxGuardDamageReductionPercent) break;
                        guardPercent = Math.Min(BattleRules.MaxGuardDamageReductionPercent, guardPercent + effect.Power);
                        guardTargetCharacterId = front.Character.Id;
                        logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} uses {skill.Name} to protect Slot {front.Slot.SlotIndex} {front.Character.Name}.");
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
                        logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} uses {skill.Name} and removes {removed.Name} from Slot {target.Slot.SlotIndex} {target.Character.Name}.");
                        applied = true;
                        break;
                    }
                    case "Dispel" when monsterCombatService is not null:
                    {
                        var removed = await monsterCombatService.RemoveFirstStatusAsync(room, "Monster", [monster.Id], true);
                        if (removed is null) break;
                        logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} uses {skill.Name} and removes {removed.Name} from {monster.Name}.");
                        applied = true;
                        break;
                    }
                    case "Interrupt" when monsterCombatService is not null:
                        if (await monsterCombatService.InterruptCurrentIntentAsync(room, monster))
                        {
                            logs.Add($"Slot {participant.Slot.SlotIndex} {participant.Character.Name} uses {skill.Name} and interrupts {monster.Name}.");
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
                            $"Slot {aliveSlots.Single(entry => entry.Character.Id == targetId).Slot.SlotIndex} {aliveSlots.Single(entry => entry.Character.Id == targetId).Character.Name}";
                        applied |= await monsterCombatService.ApplyStatusAsync(room, targetType, targetId, effect.StatusCode,
                            effect.DurationRounds, logs, targetLabel);
                        break;
                    }
                }
            }
            if (!applied) return false;
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
        return new PlayerRoundDefense(guardPercent, guardTargetCharacterId);
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

    private static bool RollCritical(Character character)
    {
        var chance = character.WeaponCriticalChancePercent;
        return chance >= 100m || chance > 0m && (decimal)Random.Shared.NextDouble() * 100m < chance;
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

                var healed = Math.Min(item.HealAmount, maxHp - character.Hp);
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
                logs.Add($"Slot {participant.Slot.SlotIndex} {character.Name} uses {item.Name} and restores {healed} HP.");
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

    private async Task<(BattleResult? Result, string? Error)> SaveResultAsync(Room room, List<SlotCharacter> slots, Monster monster, DateTime now, List<string> logs)
    {
        room.Version++;
        var save = await SaveAsync();
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
