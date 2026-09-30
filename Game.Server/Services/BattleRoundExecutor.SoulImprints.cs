using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed partial class BattleRoundExecutor
{
    private async Task ApplySoulImprintsAsync(Room room, List<BattleParticipant> aliveSlots, Monster monster,
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

        var battle = new BattleExecutionContext(room, monster,
            aliveSlots.Select(entry => new BattleParticipant(entry.Slot, entry.Character)).ToList(),
            new Dictionary<int, ElementType>(), operationBonuses, logs);
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

            var soulSource = BattleActor.ForCharacter(new(participant.Slot, participant.Character));
            using var soulAction = _events.ActionScope(soulSource, definition.Code, definition.Name, BattleActionKind.SoulImprint);
            async Task<int> DealSoulDamageAsync()
            {
                var damageResult = await Effects.Damage.CharacterDamageAsync(battle,
                    BattleActor.ForCharacter(new(participant.Slot, participant.Character)), BattleSkillEffect.Damage(definition.PowerPercent),
                    BattleDamageOrigin.Skill, true, damageElement: definition.Element);
                var damage = damageResult.CalculatedAmount;
                var critical = damageResult.IsCritical;
                logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 释放魂印「{definition.Name}」攻击 {monster.Name}，造成 {damage} 点{WeaponRules.ElementName(definition.Element)}属性伤害{(critical ? "（暴击）" : "")}。");
                return damage;
            }

            switch (definition.EffectType)
            {
                case SoulImprintEffectType.DamageArmorBreak:
                    await DealSoulDamageAsync();
                    if (monster.Hp > 0 && monsterCombatService is not null)
                        await monsterCombatService.ApplyStatusAsync(room, "Monster", monster.Id, definition.StatusCode!,
                            definition.DurationRounds, logs, monster.Name, source: new("Character", participant.Character.Id, definition.Code));
                    break;
                case SoulImprintEffectType.DamageEcho:
                {
                    var damage = await DealSoulDamageAsync();
                    var calculatedEcho = (int)decimal.Floor(damage * definition.SecondaryPowerPercent / 100m);
                    var echo = Math.Min(monster.Hp, calculatedEcho);
                    if (echo > 0)
                    {
                        var before = monster.Hp;
                        monster.Hp -= echo;
                        using var followUp = _events.ActionScope(soulSource, definition.Code, "魂印毒蚀", BattleActionKind.FollowUp);
                        _events.Hp(room, BattleEventKind.Damage, soulSource, battle.Enemy, calculatedEcho, echo, before);
                        logs.Add($"魂印毒蚀对 {monster.Name} 追加 {echo} 点无视防御伤害。");
                    }
                    break;
                }
                case SoulImprintEffectType.Interrupt:
                    await DealSoulDamageAsync();
                    if (monster.Hp > 0 && monsterCombatService is not null &&
                        await monsterCombatService.InterruptCurrentIntentAsync(room, monster))
                    {
                        _events.Utility(room, BattleEventKind.Interrupt, battle.Enemy, label: "打断");
                        logs.Add($"魂印「{definition.Name}」打断了 {monster.Name} 的行动。");
                    }
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
                    if (affected.Count > 0) _events.Utility(room, BattleEventKind.Cooldown, soulSource, reduction, $"冷却缩短 {reduction} 回合");
                    logs.Add($"{participant.Slot.SlotIndex}号位 {participant.Character.Name} 释放魂印「{definition.Name}」，{affected.Count} 个职业技能的剩余冷却缩短 {reduction} 回合。");
                    break;
                }
                case SoulImprintEffectType.HealCleanse:
                    foreach (var target in aliveSlots.Where(entry => entry.Character.Hp > 0))
                    {
                        var hpBefore = target.Character.Hp;
                        var healingTarget = BattleActor.ForCharacter(new(target.Slot, target.Character));
                        var calculated = BattleDamageService.CalculateHealing(soulSource, healingTarget,
                            new(BattleEffectKind.Heal, BattleEffectTarget.ForCharacter("AllAlive"), HealMaxHpPercent: definition.PowerPercent),
                            applyHealingBonuses: false);
                        var heal = BattleDamageService.RestoreHp(healingTarget, calculated);
                        if (heal > 0)
                        {
                            _events.Hp(room, BattleEventKind.Heal, soulSource, healingTarget, calculated, heal, hpBefore);
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
                            $"{participant.Slot.SlotIndex}号位 {participant.Character.Name}", source: new("Character", participant.Character.Id, definition.Code));
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

    public async Task<bool> CanSoulImprintApplyAsync(Room room, Monster monster,
        SoulImprintDefinitionOptions definition, BattleParticipant participant, IReadOnlyCollection<BattleParticipant> aliveSlots)
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
        SoulImprintDefinitionOptions definition, BattleParticipant participant,
        IReadOnlyCollection<BattleParticipant> aliveSlots)
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

}
