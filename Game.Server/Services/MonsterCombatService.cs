using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class MonsterCombatService(GameDbContext dbContext, MonsterCombatCatalog catalog)
{
    public async Task<MonsterIntent> EnsureIntentAsync(Room room, Monster monster)
    {
        var existing = dbContext.MonsterIntents.Local.FirstOrDefault(intent =>
                intent.RoomId == room.Id && intent.RunSequence == room.RunSequence &&
                intent.RoundNumber == room.RoundNumber && intent.MonsterId == monster.Id)
            ?? await dbContext.MonsterIntents.FirstOrDefaultAsync(intent =>
                intent.RoomId == room.Id && intent.RunSequence == room.RunSequence &&
                intent.RoundNumber == room.RoundNumber && intent.MonsterId == monster.Id);
        if (existing is not null)
        {
            if (existing.TargetType == "Front")
                existing.TargetCharacterId = await FindFrontCharacterIdAsync(room.Id);
            return existing;
        }

        var selectedSkill = await SelectSkillAsync(room, monster);
        var targetType = selectedSkill?.TargetType ?? "Front";
        var intent = new MonsterIntent
        {
            RoomId = room.Id,
            RunSequence = room.RunSequence,
            RoundNumber = room.RoundNumber,
            MonsterId = monster.Id,
            ActionType = selectedSkill is null ? "BasicAttack" : "Skill",
            SkillCode = selectedSkill?.Code,
            TargetType = targetType,
            TargetCharacterId = targetType == "Front" ? await FindFrontCharacterIdAsync(room.Id) : null,
            CreatedAtUtc = DateTime.UtcNow
        };
        dbContext.MonsterIntents.Add(intent);
        return intent;
    }

    public async Task<MonsterIntentResponse?> GetIntentResponseAsync(Room room, Monster monster)
    {
        if (room.Status == RoomStatus.BattleOver || monster.Hp <= 0) return null;
        var intent = await EnsureIntentAsync(room, monster);
        var skill = catalog.FindSkill(intent.SkillCode);
        var targetLabel = intent.TargetType switch
        {
            "Self" => monster.Name,
            "AllAlive" => "全体角色",
            _ => await GetCharacterTargetLabelAsync(room.Id, intent.TargetCharacterId)
        };
        return new MonsterIntentResponse
        {
            ActionType = intent.ActionType,
            SkillCode = intent.SkillCode,
            ActionName = skill?.Name ?? "普通攻击",
            Description = skill?.Description ?? "攻击预告中指定的前排角色。",
            TargetType = intent.TargetType,
            TargetCharacterId = intent.TargetCharacterId,
            TargetLabel = targetLabel,
            IsInterruptible = skill?.IsInterruptible == true,
            IsInterrupted = intent.IsInterrupted,
            DangerLevel = skill?.DangerLevel ?? "Normal"
        };
    }

    public async Task<bool> CanInterruptCurrentIntentAsync(Room room, Monster monster)
    {
        var intent = await EnsureIntentAsync(room, monster);
        return intent.ActionType == "Skill" && !intent.IsInterrupted &&
               catalog.FindSkill(intent.SkillCode)?.IsInterruptible == true;
    }

    public async Task<bool> InterruptCurrentIntentAsync(Room room, Monster monster)
    {
        var intent = await EnsureIntentAsync(room, monster);
        if (intent.ActionType != "Skill" || intent.IsInterrupted ||
            catalog.FindSkill(intent.SkillCode)?.IsInterruptible != true) return false;
        intent.IsInterrupted = true;
        return true;
    }

    public async Task<bool> HasRemovableStatusAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds, bool isPositive)
    {
        var effects = await GetActiveEffectsAsync(room, targetType, targetIds);
        return effects.Any(effect => catalog.FindStatus(effect.EffectCode) is { } definition &&
            definition.IsPositive == isPositive && definition.IsDispellable);
    }

    public async Task<RemovedBattleStatus?> RemoveFirstStatusAsync(Room room, string targetType,
        IReadOnlyList<int> targetIds, bool isPositive)
    {
        var effects = await GetActiveEffectsAsync(room, targetType, targetIds);
        var targetOrder = targetIds.Select((id, index) => (id, index)).ToDictionary(entry => entry.id, entry => entry.index);
        var effect = effects.OrderBy(effect => targetOrder.GetValueOrDefault(effect.TargetId, int.MaxValue))
            .ThenBy(effect => effect.Id)
            .FirstOrDefault(effect => catalog.FindStatus(effect.EffectCode) is { } definition &&
                definition.IsPositive == isPositive && definition.IsDispellable);
        if (effect is null) return null;
        var status = catalog.FindStatus(effect.EffectCode)!;
        dbContext.BattleStatusEffects.Remove(effect);
        return new RemovedBattleStatus(effect.TargetId, status.Code, status.Name, status.IsPositive);
    }

    public Task<bool> ApplyStatusAsync(Room room, string targetType, int targetId, string statusCode,
        int durationRounds, List<string> logs, string targetLabel) =>
        ApplyStatusCoreAsync(room, targetType, targetId,
            new MonsterStatusApplicationOptions { StatusCode = statusCode, DurationRounds = durationRounds },
            logs, targetLabel);

    public async Task<List<BattleStatusEffectResponse>> GetStatusResponsesAsync(Room room, string targetType, int targetId)
    {
        var effects = await dbContext.BattleStatusEffects.Where(effect => effect.RoomId == room.Id &&
            effect.RunSequence == room.RunSequence && effect.TargetType == targetType && effect.TargetId == targetId &&
            effect.ExpiresAfterRound >= room.RoundNumber).OrderBy(effect => effect.Id).ToListAsync();
        return effects.Select(effect =>
        {
            var definition = catalog.FindStatus(effect.EffectCode);
            return new BattleStatusEffectResponse
            {
                Code = effect.EffectCode,
                Name = definition?.Name ?? effect.EffectCode,
                Description = definition?.Description ?? string.Empty,
                IsPositive = definition?.IsPositive ?? false,
                CanDispel = definition?.IsDispellable ?? false,
                Stacks = effect.Stacks,
                RemainingRounds = Math.Max(0, effect.ExpiresAfterRound - room.RoundNumber + 1)
            };
        }).ToList();
    }

    public async Task ExecuteIntentAsync(Room room, Monster monster,
        IReadOnlyList<MonsterCombatParticipant> participants,
        IReadOnlyDictionary<int, ElementType> mainWeaponElements,
        PlayerRoundDefense defense, List<string> logs)
    {
        var intent = await EnsureIntentAsync(room, monster);
        dbContext.MonsterIntents.Remove(intent);
        var skill = intent.ActionType == "Skill" ? catalog.FindSkill(intent.SkillCode) : null;
        if (intent.IsInterrupted)
        {
            if (skill is not null) await StartCooldownAsync(room, monster, skill);
            logs.Add($"{monster.Name}'s {skill?.Name ?? "action"} is interrupted.");
            return;
        }
        if (skill is null)
        {
            var target = participants.SingleOrDefault(entry => entry.Character.Id == intent.TargetCharacterId && entry.Character.Hp > 0);
            if (target is null)
            {
                logs.Add($"{monster.Name}'s intended target is no longer available. The attack is cancelled.");
                return;
            }
            await DealDamageAsync(room, monster, target, 100, mainWeaponElements, defense, logs, "attacks");
            return;
        }

        var targets = skill.TargetType switch
        {
            "Self" => [],
            "AllAlive" => participants.Where(entry => entry.Character.Hp > 0).OrderBy(entry => entry.Slot.SlotIndex).ToList(),
            _ => participants.Where(entry => entry.Character.Id == intent.TargetCharacterId && entry.Character.Hp > 0).ToList()
        };
        if (skill.TargetType != "Self" && targets.Count == 0)
        {
            logs.Add($"{monster.Name}'s {skill.Name} loses its target and is cancelled.");
            return;
        }

        logs.Add($"{monster.Name} uses {skill.Name}.");
        if (skill.DamagePowerPercent > 0)
        {
            foreach (var target in targets)
                await DealDamageAsync(room, monster, target, skill.DamagePowerPercent,
                    mainWeaponElements, defense, logs, skill.Name);
        }
        foreach (var application in skill.Statuses)
        {
            if (skill.TargetType == "Self")
                await ApplyStatusCoreAsync(room, "Monster", monster.Id, application, logs, monster.Name);
            else
                foreach (var target in targets.Where(target => target.Character.Hp > 0))
                    await ApplyStatusCoreAsync(room, "Character", target.Character.Id, application, logs,
                        $"Slot {target.Slot.SlotIndex} {target.Character.Name}");
        }

        await StartCooldownAsync(room, monster, skill);
    }

    private async Task StartCooldownAsync(Room room, Monster monster, MonsterSkillOptions skill)
    {
        var cooldown = dbContext.BattleMonsterSkillCooldowns.Local.FirstOrDefault(entry =>
                entry.RoomId == room.Id && entry.MonsterId == monster.Id && entry.SkillCode == skill.Code)
            ?? await dbContext.BattleMonsterSkillCooldowns.SingleOrDefaultAsync(entry =>
                entry.RoomId == room.Id && entry.MonsterId == monster.Id && entry.SkillCode == skill.Code);
        if (cooldown is null)
        {
            cooldown = new BattleMonsterSkillCooldown
            {
                RoomId = room.Id,
                MonsterId = monster.Id,
                SkillCode = skill.Code
            };
            dbContext.BattleMonsterSkillCooldowns.Add(cooldown);
        }
        cooldown.ReadyAtRound = checked(room.RoundNumber + skill.CooldownRounds + 1);
    }

    public async Task ResolveEndOfRoundAsync(Room room, Monster monster,
        IReadOnlyList<MonsterCombatParticipant> participants, List<string> logs)
    {
        var effects = await dbContext.BattleStatusEffects.Where(effect =>
            effect.RoomId == room.Id && effect.RunSequence == room.RunSequence).ToListAsync();
        effects.RemoveAll(effect => dbContext.Entry(effect).State == EntityState.Deleted);
        foreach (var effect in effects.Where(effect => effect.AppliedRound < room.RoundNumber))
        {
            var definition = catalog.FindStatus(effect.EffectCode);
            if (definition?.EffectType != "DamageOverTime") continue;
            var damage = Math.Max(1, (int)decimal.Floor(Math.Abs(definition.ValuePerStack) * effect.Stacks));
            if (effect.TargetType == "Character")
            {
                var target = participants.SingleOrDefault(entry => entry.Character.Id == effect.TargetId);
                if (target is null || target.Character.Hp <= 0) continue;
                target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                logs.Add($"Slot {target.Slot.SlotIndex} {target.Character.Name} takes {damage} damage from {definition.Name}.");
            }
            else if (effect.TargetType == "Monster" && effect.TargetId == monster.Id && monster.Hp > 0)
            {
                monster.Hp = Math.Max(0, monster.Hp - damage);
                logs.Add($"{monster.Name} takes {damage} damage from {definition.Name}.");
            }
        }

        dbContext.BattleStatusEffects.RemoveRange(effects.Where(effect => effect.ExpiresAfterRound <= room.RoundNumber));
    }

    public async Task ResetRoomStateAsync(int roomId)
    {
        dbContext.MonsterIntents.RemoveRange(await dbContext.MonsterIntents.Where(entry => entry.RoomId == roomId).ToListAsync());
        dbContext.BattleStatusEffects.RemoveRange(await dbContext.BattleStatusEffects.Where(entry => entry.RoomId == roomId).ToListAsync());
        dbContext.BattleMonsterSkillCooldowns.RemoveRange(await dbContext.BattleMonsterSkillCooldowns.Where(entry => entry.RoomId == roomId).ToListAsync());
    }

    public async Task RemoveMonsterStateAsync(int roomId, int monsterId)
    {
        dbContext.MonsterIntents.RemoveRange(await dbContext.MonsterIntents
            .Where(entry => entry.RoomId == roomId && entry.MonsterId == monsterId).ToListAsync());
        dbContext.BattleStatusEffects.RemoveRange(await dbContext.BattleStatusEffects
            .Where(entry => entry.RoomId == roomId && entry.TargetType == "Monster" && entry.TargetId == monsterId).ToListAsync());
    }

    public async Task<decimal> GetModifierAsync(Room room, string targetType, int targetId, string effectType)
    {
        var effects = await GetActiveEffectsAsync(room, targetType, [targetId]);
        return effects.Sum(effect => catalog.FindStatus(effect.EffectCode) is { EffectType: var type } definition && type == effectType
            ? definition.ValuePerStack * effect.Stacks : 0m);
    }

    private async Task<MonsterSkillOptions?> SelectSkillAsync(Room room, Monster monster)
    {
        var profile = catalog.FindProfile(monster.CombatProfileCode);
        if (profile is null || profile.Skills.Count == 0 || profile.SkillUseChancePercent <= 0 ||
            Random.Shared.Next(1, 101) > profile.SkillUseChancePercent) return null;
        var cooldowns = await dbContext.BattleMonsterSkillCooldowns.Where(entry =>
            entry.RoomId == room.Id && entry.MonsterId == monster.Id).ToListAsync();
        foreach (var local in dbContext.BattleMonsterSkillCooldowns.Local.Where(entry =>
                     entry.RoomId == room.Id && entry.MonsterId == monster.Id))
            if (!cooldowns.Contains(local)) cooldowns.Add(local);
        var eligible = profile.Skills.Select(entry => (Entry: entry, Skill: catalog.FindSkill(entry.Code)))
            .Where(candidate => candidate.Skill is not null &&
                (candidate.Skill.SelfHpBelowPercent is null ||
                 (long)monster.Hp * 100 <= (long)monster.MaxHp * candidate.Skill.SelfHpBelowPercent) &&
                cooldowns.All(cooldown => !string.Equals(cooldown.SkillCode, candidate.Skill.Code, StringComparison.OrdinalIgnoreCase) ||
                    cooldown.ReadyAtRound <= room.RoundNumber)).ToList();
        if (eligible.Count == 0) return null;
        var roll = Random.Shared.Next(eligible.Sum(candidate => candidate.Entry.Weight));
        foreach (var candidate in eligible)
        {
            if (roll < candidate.Entry.Weight) return candidate.Skill;
            roll -= candidate.Entry.Weight;
        }
        return eligible[^1].Skill;
    }

    private async Task<bool> ApplyStatusCoreAsync(Room room, string targetType, int targetId,
        MonsterStatusApplicationOptions application, List<string> logs, string targetLabel)
    {
        var definition = catalog.FindStatus(application.StatusCode);
        if (definition is null) return false;
        var effect = dbContext.BattleStatusEffects.Local.FirstOrDefault(entry => entry.RoomId == room.Id &&
                entry.RunSequence == room.RunSequence && entry.TargetType == targetType && entry.TargetId == targetId &&
                entry.EffectCode == definition.Code);
        effect ??= await dbContext.BattleStatusEffects.SingleOrDefaultAsync(entry => entry.RoomId == room.Id &&
            entry.RunSequence == room.RunSequence && entry.TargetType == targetType && entry.TargetId == targetId &&
            entry.EffectCode == definition.Code);
        if (effect is not null && dbContext.Entry(effect).State == EntityState.Deleted)
            dbContext.Entry(effect).State = EntityState.Modified;
        if (effect is null)
        {
            effect = new BattleStatusEffect
            {
                RoomId = room.Id, RunSequence = room.RunSequence, TargetType = targetType, TargetId = targetId,
                EffectCode = definition.Code, AppliedRound = room.RoundNumber, Stacks = 1
            };
            dbContext.BattleStatusEffects.Add(effect);
        }
        else
        {
            if (definition.Stacking == "AddStack") effect.Stacks = Math.Min(definition.MaxStacks, effect.Stacks + 1);
            effect.AppliedRound = room.RoundNumber;
        }
        effect.ExpiresAfterRound = checked(room.RoundNumber + application.DurationRounds);
        logs.Add($"{targetLabel} gains {definition.Name} for {application.DurationRounds} round(s){(effect.Stacks > 1 ? $" (x{effect.Stacks})" : "")}.");
        return true;
    }

    private async Task<List<BattleStatusEffect>> GetActiveEffectsAsync(Room room, string targetType,
        IReadOnlyCollection<int> targetIds)
    {
        var effects = await dbContext.BattleStatusEffects.Where(effect => effect.RoomId == room.Id &&
            effect.RunSequence == room.RunSequence && effect.TargetType == targetType &&
            targetIds.Contains(effect.TargetId) && effect.ExpiresAfterRound >= room.RoundNumber).ToListAsync();
        effects.RemoveAll(effect => dbContext.Entry(effect).State == EntityState.Deleted);
        foreach (var local in dbContext.BattleStatusEffects.Local.Where(effect => effect.RoomId == room.Id &&
                     effect.RunSequence == room.RunSequence && effect.TargetType == targetType &&
                     targetIds.Contains(effect.TargetId) && effect.ExpiresAfterRound >= room.RoundNumber &&
                     dbContext.Entry(effect).State != EntityState.Deleted))
            if (!effects.Contains(local)) effects.Add(local);
        return effects;
    }

    private async Task DealDamageAsync(Room room, Monster monster, MonsterCombatParticipant target,
        int powerPercent, IReadOnlyDictionary<int, ElementType> mainWeaponElements,
        PlayerRoundDefense defense, List<string> logs, string actionName)
    {
        var attackPercent = await GetModifierAsync(room, "Monster", monster.Id, "AttackPercent");
        var targetReduction = await GetModifierAsync(room, "Character", target.Character.Id, "ReductionPercent");
        var guard = defense.TargetCharacterId == target.Character.Id ? defense.ReductionPercent : 0;
        var element = mainWeaponElements.TryGetValue(target.Character.Id, out var mainElement) ? mainElement : (ElementType?)null;
        var scaledAttack = Math.Max(1, (int)decimal.Floor(monster.Attack * powerPercent / 100m));
        var damage = DamageCalculator.Calculate(scaledAttack, TalentRules.EffectiveDefense(target.Character),
            factors: new DamageFactors(AttackPercent: attackPercent,
                ElementPercent: ElementMatchup.MonsterAttackPercent(monster.Element, element),
                ReductionPercent: guard + targetReduction));
        target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
        logs.Add($"{monster.Name} {actionName} Slot {target.Slot.SlotIndex} {target.Character.Name} for {damage} damage.");
    }

    private async Task<int?> FindFrontCharacterIdAsync(int roomId) => await (
        from slot in dbContext.RoomSlots
        join character in dbContext.Characters on slot.CharacterId equals character.Id
        where slot.RoomId == roomId && character.Hp > 0
        orderby slot.SlotIndex
        select (int?)character.Id).FirstOrDefaultAsync();

    private async Task<string> GetCharacterTargetLabelAsync(int roomId, int? characterId)
    {
        if (!characterId.HasValue) return "无有效目标";
        var target = await (from slot in dbContext.RoomSlots
            join character in dbContext.Characters on slot.CharacterId equals character.Id
            where slot.RoomId == roomId && character.Id == characterId.Value
            select new { slot.SlotIndex, character.Name }).FirstOrDefaultAsync();
        return target is null ? "目标已失效" : $"{target.SlotIndex} 号位 {target.Name}";
    }
}

public sealed record MonsterCombatParticipant(RoomSlot Slot, Character Character);
public readonly record struct PlayerRoundDefense(int ReductionPercent, int? TargetCharacterId);
public sealed record RemovedBattleStatus(int TargetId, string Code, string Name, bool IsPositive);
