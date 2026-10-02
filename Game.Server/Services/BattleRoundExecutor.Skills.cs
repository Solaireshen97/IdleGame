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
    private async Task ApplyCombatSkillsAsync(Room room, List<BattleParticipant> aliveSlots, Monster monster,
        IReadOnlyDictionary<int, ElementType> mainWeaponElements,
        IReadOnlyDictionary<int, OperationPotionBonuses> operationBonuses,
        IReadOnlyCollection<int> autoCharacterIds, List<string> logs)
    {
        var characterIds = aliveSlots.Select(entry => entry.Character.Id).ToList();
        var battle = new BattleExecutionContext(room, monster,
            aliveSlots.Select(entry => new BattleParticipant(entry.Slot, entry.Character)).ToList(), mainWeaponElements,
            operationBonuses, logs, monsterCombatService is null ? null : () => monsterCombatService.InterruptCurrentIntentAsync(room, monster));
        await MageMechanics.RoundStartAsync(battle, Effects, mechanics);
        if (monster.Hp <= 0) return;
        var equipment = await dbContext.CharacterSkillSlots
            .Where(slot => characterIds.Contains(slot.CharacterId) && slot.SkillCode != null)
            .OrderBy(slot => slot.SlotIndex).ToListAsync();
        if (equipment.Count == 0) return;
        var cooldowns = await dbContext.BattleSkillCooldowns
            .Where(cooldown => cooldown.RoomId == room.Id && characterIds.Contains(cooldown.CharacterId)).ToListAsync();
        var purchasedNodes = await LegacyTalents.RanksAsync(characterIds);
        var professionLevels = (await dbContext.CharacterCombatProfessions
            .Where(entry => characterIds.Contains(entry.CharacterId)).ToListAsync())
            .GroupBy(entry => entry.CharacterId)
            .ToDictionary(group => group.Key, group => group.ToDictionary(entry => entry.ProfessionCode,
                entry => entry.Level, StringComparer.OrdinalIgnoreCase));
        var usedByCharacter = aliveSlots.ToDictionary(entry => entry.Character.Id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        async Task<bool> TryUseAsync(BattleParticipant participant, CharacterSkillSlot slot, bool automatic)
        {
            if (monster.Hp <= 0 || participant.Character.Hp <= 0) return false;
            if (await Statuses.SkipBlockedActionAsync(room, participant.Character.Id)) return false;
            var levels = professionLevels.GetValueOrDefault(participant.Character.Id) ??
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var skill = skillCatalog.Resolve(participant.Character, slot.SkillCode, levels);
            var used = usedByCharacter[participant.Character.Id];
            var ranks = purchasedNodes.GetValueOrDefault(participant.Character.Id) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (skill is null || used.Contains(skill.Code)) return false;
            var cooldown = cooldowns.SingleOrDefault(entry => entry.CharacterId == participant.Character.Id && entry.SkillCode == skill.Code);
            if ((cooldown?.ReadyAtRound ?? skill.InitialCooldownRounds) > room.RoundNumber ||
                automatic && !slot.AutoUseEnabled) return false;
            var snapshot = await _skillSnapshots.CaptureAsync(room, monster, skill, participant.Character.Id,
                aliveSlots.Select(entry => (entry.Slot, entry.Character)), levels, cooldowns);
            if (automatic && !SkillBattlePolicy.MeetsAutoCondition(skill, snapshot, slot.AutoConditionOverride,
                    slot.AutoHpThresholdPercent, LegacySkillTalentAdapter.AutoSelfCleanse(skill, ranks))) return false;
            var chosenTargetId = automatic ? null : SkillQueueRules.TargetCharacterId(participant.Slot, slot.SlotIndex);
            if (!SkillBattlePolicy.HasApplicableEffect(skill, snapshot, chosenTargetId)) return false;

            var source = BattleActor.ForCharacter(battle.Party.Single(entry => entry.Character.Id == participant.Character.Id),
                battle.StatsFor(participant.Character));
            var cast = new BattleCastExecution(battle, skill, source)
            {
                ChosenTargetId = chosenTargetId, Cooldowns = cooldowns, ProfessionLevels = levels
            };
            var mechanic = _professionMechanics.For(skill);
            if (ranks.Count > 0) mechanic = new LegacySkillTalentAdapter(mechanic, ranks,
                (characterId, code, duration) => LegacyTalents.GrantEchoAsync(room, characterId, code, duration, cast.Skill.Code));
            var result = await Effects.ExecuteAsync(cast, mechanic);
            if (!result.Applied) return false;
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
    }

}
