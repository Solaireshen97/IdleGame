using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared;
using Game.Shared.Dtos;
using Game.Shared.Enums;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Game.Server.Services;

public sealed class MonsterCombatService(GameDbContext dbContext, MonsterCombatCatalog catalog, Random? random = null)
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
        var silenced = selectedSkill?.IsInterruptible == true &&
            (await GetActiveEffectsAsync(room, "Monster", [monster.Id])).Any(effect =>
                catalog.FindStatus(effect.EffectCode)?.EffectType == "SilenceNextIntent" &&
                effect.AppliedRound < room.RoundNumber);
        var targetType = selectedSkill?.TargetType ?? "Front";
        var intent = new MonsterIntent
        {
            RoomId = room.Id,
            RunSequence = room.RunSequence,
            RoundNumber = room.RoundNumber,
            MonsterId = monster.Id,
            ActionType = selectedSkill is null ? "BasicAttack" : "Skill",
            SkillCode = selectedSkill?.Code,
            IsInterrupted = silenced,
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

    public bool HasAnyInterruptibleSkill(Monster monster)
    {
        var profile = catalog.FindProfile(monster.CombatProfileCode);
        return profile?.Skills.Any(entry => catalog.FindSkill(entry.Code) is { IsInterruptible: true } skill &&
            (profile.SkillUseChancePercent > 0 || skill.ForcedPriority > 0)) == true;
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

    public async Task<bool> HasStatusAsync(Room room, string targetType, int targetId, string statusCode)
    {
        var effects = await GetActiveEffectsAsync(room, targetType, [targetId]);
        return effects.Any(effect => string.Equals(effect.EffectCode, statusCode, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<int> GetStatusStacksAsync(Room room, string targetType, int targetId, string statusCode)
    {
        var effects = await GetActiveEffectsAsync(room, targetType, [targetId]);
        return effects.FirstOrDefault(effect =>
            string.Equals(effect.EffectCode, statusCode, StringComparison.OrdinalIgnoreCase))?.Stacks ?? 0;
    }

    public async Task<int> ConsumeShadowChargesAsync(Room room, int characterId)
    {
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        var charge = effects.FirstOrDefault(effect => effect.EffectCode == "rogue-shadow-charge");
        if (charge is null) return 0;
        dbContext.BattleStatusEffects.Remove(charge);
        return charge.Stacks;
    }

    public async Task RemoveStatusAsync(Room room, string targetType, int targetId, string statusCode)
    {
        var effects = await GetActiveEffectsAsync(room, targetType, [targetId]);
        foreach (var effect in effects.Where(effect => effect.EffectCode == statusCode))
            dbContext.BattleStatusEffects.Remove(effect);
    }

    public async Task<bool> AddShadowChargeAsync(Room room, int characterId, List<string> logs, string targetLabel)
    {
        var definition = catalog.FindStatus("rogue-shadow-charge");
        if (definition is null) return false;
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        var charge = effects.FirstOrDefault(effect => effect.EffectCode == "rogue-shadow-charge");
        if (charge is null)
        {
            charge = dbContext.BattleStatusEffects.Local.FirstOrDefault(effect => effect.RoomId == room.Id &&
                effect.RunSequence == room.RunSequence && effect.TargetType == "Character" &&
                effect.TargetId == characterId && effect.EffectCode == "rogue-shadow-charge" &&
                dbContext.Entry(effect).State == EntityState.Deleted);
            if (charge is not null)
            {
                dbContext.Entry(charge).State = EntityState.Modified;
                charge.Stacks = 0;
            }
        }
        if (charge is null)
        {
            charge = new BattleStatusEffect { RoomId = room.Id, RunSequence = room.RunSequence,
                TargetType = "Character", TargetId = characterId, EffectCode = "rogue-shadow-charge",
                AppliedRound = room.RoundNumber, ExpiresAfterRound = int.MaxValue, Stacks = 1 };
            dbContext.BattleStatusEffects.Add(charge);
        }
        else charge.Stacks = Math.Min(definition.MaxStacks, charge.Stacks + 1);
        logs.Add($"{targetLabel} 获得 {definition.Name}（{charge.Stacks} 层）。");
        return true;
    }

    public async Task<int> GetMageDisorderStacksAsync(Room room, int characterId, int monsterId)
    {
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        return effects.FirstOrDefault(effect => effect.EffectCode == "mage-disorder" &&
            effect.PerTickValue == monsterId)?.Stacks ?? 0;
    }

    public async Task<int> AddMageDisorderAsync(Room room, int characterId, int monsterId,
        List<string> logs, string characterName)
    {
        var definition = catalog.FindStatus("mage-disorder");
        if (definition is null) return 0;
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        var disorder = effects.FirstOrDefault(effect => effect.EffectCode == "mage-disorder");
        if (disorder is not null && disorder.PerTickValue != monsterId)
        {
            dbContext.BattleStatusEffects.Remove(disorder);
            disorder = null;
        }
        if (disorder is null)
        {
            disorder = dbContext.BattleStatusEffects.Local.FirstOrDefault(effect =>
                effect.RoomId == room.Id && effect.RunSequence == room.RunSequence &&
                effect.TargetType == "Character" && effect.TargetId == characterId &&
                effect.EffectCode == "mage-disorder" && dbContext.Entry(effect).State == EntityState.Deleted);
            if (disorder is not null)
            {
                dbContext.Entry(disorder).State = EntityState.Modified;
                disorder.Stacks = 0;
            }
        }
        if (disorder is null)
        {
            disorder = new BattleStatusEffect
            {
                RoomId = room.Id, RunSequence = room.RunSequence, TargetType = "Character",
                TargetId = characterId, EffectCode = "mage-disorder", Stacks = 0
            };
            dbContext.BattleStatusEffects.Add(disorder);
        }
        disorder.PerTickValue = monsterId;
        disorder.AppliedRound = room.RoundNumber;
        disorder.ExpiresAfterRound = int.MaxValue;
        disorder.Stacks = Math.Min(definition.MaxStacks, disorder.Stacks + 1);
        logs.Add($"{characterName} 使当前怪物获得奥术失序（{disorder.Stacks} 层）。");
        return disorder.Stacks;
    }

    public async Task<int> ConsumeMageDisorderAsync(Room room, int characterId, int monsterId)
    {
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        var disorder = effects.FirstOrDefault(effect => effect.EffectCode == "mage-disorder" &&
            effect.PerTickValue == monsterId);
        if (disorder is null || disorder.Stacks < 3) return 0;
        disorder.Stacks -= 3;
        var remaining = disorder.Stacks;
        if (remaining == 0) dbContext.BattleStatusEffects.Remove(disorder);
        return remaining;
    }

    public async Task<int> GetMageDomainRankAsync(Room room, int characterId)
    {
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        for (var rank = 3; rank >= 1; rank--)
            if (effects.Any(effect => effect.EffectCode == $"mage-domain-{rank}")) return rank;
        return 0;
    }

    public async Task ApplyMageSkillDisruptionAsync(Room room, int monsterId, int percent,
        List<string> logs, string monsterName)
    {
        var current = await GetActiveEffectsAsync(room, "Monster", [monsterId]);
        var currentPower = current.Where(effect => effect.EffectCode.StartsWith("mage-skill-disruption-", StringComparison.Ordinal))
            .Select(effect => int.TryParse(effect.EffectCode.AsSpan("mage-skill-disruption-".Length), out var value) ? value : 0)
            .DefaultIfEmpty(0).Max();
        if (currentPower > percent) return;
        foreach (var effect in current.Where(effect => effect.EffectCode.StartsWith("mage-skill-disruption-", StringComparison.Ordinal)))
            dbContext.BattleStatusEffects.Remove(effect);
        await ApplyStatusAsync(room, "Monster", monsterId, $"mage-skill-disruption-{percent}",
            int.MaxValue - room.RoundNumber, logs, monsterName);
    }

    private async Task<int> ConsumeMageSkillDisruptionAsync(Room room, int monsterId)
    {
        var effects = await GetActiveEffectsAsync(room, "Monster", [monsterId]);
        var marker = effects.FirstOrDefault(effect =>
            effect.EffectCode.StartsWith("mage-skill-disruption-", StringComparison.Ordinal));
        if (marker is null) return 0;
        dbContext.BattleStatusEffects.Remove(marker);
        return int.TryParse(marker.EffectCode.AsSpan("mage-skill-disruption-".Length), out var value) ? value : 0;
    }

    private const string HunterMarkCode = "hunter-prey-mark";
    private const string HunterVulnerabilityPrefix = "hunter-vulnerability-";
    private const string HunterCoordinatedPrefix = "hunter-coordinated-";
    private const string HunterEagleEyePrefix = "hunter-eagle-eye-";

    private static int HunterStatusPower(string code, string prefix)
    {
        if (!code.StartsWith(prefix, StringComparison.Ordinal)) return 0;
        var suffix = code.AsSpan(prefix.Length);
        var separator = suffix.IndexOf('-');
        if (separator >= 0) suffix = suffix[..separator];
        return int.TryParse(suffix, out var power) ? power : 0;
    }

    public async Task<bool> HasHunterMarkAsync(Room room, int characterId, int monsterId) =>
        (await GetActiveEffectsAsync(room, "Character", [characterId])).Any(effect =>
            effect.EffectCode == HunterMarkCode && effect.PerTickValue == monsterId);

    public async Task ApplyHunterMarkAsync(Room room, int characterId, int monsterId, int durationRounds,
        List<string> logs, string characterName)
    {
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        var mark = effects.FirstOrDefault(effect => effect.EffectCode == HunterMarkCode);
        if (mark is null)
        {
            mark = dbContext.BattleStatusEffects.Local.FirstOrDefault(effect =>
                effect.RoomId == room.Id && effect.RunSequence == room.RunSequence &&
                effect.TargetType == "Character" && effect.TargetId == characterId &&
                effect.EffectCode == HunterMarkCode && dbContext.Entry(effect).State == EntityState.Deleted);
            if (mark is not null) dbContext.Entry(mark).State = EntityState.Modified;
        }
        if (mark is null)
        {
            mark = new BattleStatusEffect
            {
                RoomId = room.Id, RunSequence = room.RunSequence, TargetType = "Character",
                TargetId = characterId, EffectCode = HunterMarkCode
            };
            dbContext.BattleStatusEffects.Add(mark);
        }
        mark.PerTickValue = monsterId;
        mark.AppliedRound = room.RoundNumber;
        mark.ExpiresAfterRound = checked(room.RoundNumber + durationRounds);
        mark.Stacks = 1;
        logs.Add($"{characterName} 标记了当前猎物，持续 {durationRounds + 1} 回合。");
    }

    public async Task ApplyHunterEagleEyeAsync(Room room, int characterId, string statusCode,
        int durationRounds, List<string> logs, string characterName)
    {
        var charges = HunterStatusPower(statusCode, HunterEagleEyePrefix);
        if (charges is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(statusCode));
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        foreach (var old in effects.Where(effect => effect.EffectCode.StartsWith(HunterEagleEyePrefix, StringComparison.Ordinal) &&
                     effect.EffectCode != statusCode))
            dbContext.BattleStatusEffects.Remove(old);
        await ApplyStatusAsync(room, "Character", characterId, statusCode, durationRounds, logs, characterName);
        var renewed = (await GetActiveEffectsAsync(room, "Character", [characterId]))
            .First(effect => effect.EffectCode == statusCode);
        renewed.Stacks = charges;
    }

    public async Task<bool> ConsumeHunterMarkAsync(Room room, int characterId, int monsterId,
        List<string> logs, string characterName)
    {
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        var mark = effects.FirstOrDefault(effect => effect.EffectCode == HunterMarkCode &&
            effect.PerTickValue == monsterId);
        if (mark is null) return false;
        var eagleEye = effects.FirstOrDefault(effect =>
            effect.EffectCode.StartsWith(HunterEagleEyePrefix, StringComparison.Ordinal));
        if (eagleEye is not null && eagleEye.Stacks > 0)
        {
            eagleEye.Stacks--;
            if (eagleEye.Stacks == 0) dbContext.BattleStatusEffects.Remove(eagleEye);
            logs.Add($"{characterName} 借助鹰眼时刻保留了猎物标记。");
            return true;
        }
        dbContext.BattleStatusEffects.Remove(mark);
        logs.Add($"{characterName} 消耗了猎物标记。");
        return true;
    }

    public async Task ApplyHunterVulnerabilityAsync(Room room, int monsterId, string statusCode,
        int durationRounds, List<string> logs, string monsterName)
    {
        var incoming = HunterStatusPower(statusCode, HunterVulnerabilityPrefix);
        if (incoming <= 0) throw new ArgumentOutOfRangeException(nameof(statusCode));
        var effects = await GetActiveEffectsAsync(room, "Monster", [monsterId]);
        var current = effects.Where(effect => effect.EffectCode.StartsWith(HunterVulnerabilityPrefix, StringComparison.Ordinal))
            .ToList();
        if (current.Any(effect => HunterStatusPower(effect.EffectCode, HunterVulnerabilityPrefix) > incoming)) return;
        foreach (var old in current.Where(effect => effect.EffectCode != statusCode))
            dbContext.BattleStatusEffects.Remove(old);
        await ApplyStatusAsync(room, "Monster", monsterId, statusCode, durationRounds, logs, monsterName);
    }

    public async Task<int> GetHunterVulnerabilityPercentAsync(Room room, int monsterId) =>
        (await GetActiveEffectsAsync(room, "Monster", [monsterId]))
        .Select(effect => HunterStatusPower(effect.EffectCode, HunterVulnerabilityPrefix))
        .DefaultIfEmpty(0).Max();

    public async Task<int> AmplifyHunterDamageAsync(Room room, int monsterId, int damage)
    {
        if (damage <= 0) return damage;
        var vulnerability = await GetHunterVulnerabilityPercentAsync(room, monsterId);
        return vulnerability == 0 ? damage : (int)Math.Min(int.MaxValue,
            decimal.Floor(damage * (1m + vulnerability / 100m)));
    }

    public async Task ApplyHunterCoordinatedAsync(Room room, int characterId, string statusCode,
        int durationRounds, List<string> logs, string characterName)
    {
        var incoming = HunterStatusPower(statusCode, HunterCoordinatedPrefix);
        if (incoming <= 0) throw new ArgumentOutOfRangeException(nameof(statusCode));
        var effects = await GetActiveEffectsAsync(room, "Character", [characterId]);
        var current = effects.Where(effect => effect.EffectCode.StartsWith(HunterCoordinatedPrefix, StringComparison.Ordinal))
            .ToList();
        if (current.Any(effect => HunterStatusPower(effect.EffectCode, HunterCoordinatedPrefix) > incoming)) return;
        foreach (var old in current.Where(effect => effect.EffectCode != statusCode))
            dbContext.BattleStatusEffects.Remove(old);
        await ApplyStatusAsync(room, "Character", characterId, statusCode, durationRounds, logs, characterName);
    }

    public async Task<int> GetHunterCoordinatedPercentAsync(Room room, int characterId) =>
        (await GetActiveEffectsAsync(room, "Character", [characterId]))
        .Select(effect => HunterStatusPower(effect.EffectCode, HunterCoordinatedPrefix))
        .DefaultIfEmpty(0).Max();

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
        int durationRounds, List<string> logs, string targetLabel, int? perTickValue = null) =>
        ApplyStatusCoreAsync(room, targetType, targetId,
            new MonsterStatusApplicationOptions { StatusCode = statusCode, DurationRounds = durationRounds },
            logs, targetLabel, perTickValue);

    public async Task<List<BattleStatusEffectResponse>> GetStatusResponsesAsync(Room room, string targetType, int targetId)
    {
        var effects = await dbContext.BattleStatusEffects.Where(effect => effect.RoomId == room.Id &&
            effect.RunSequence == room.RunSequence && effect.TargetType == targetType && effect.TargetId == targetId &&
            effect.ExpiresAfterRound >= room.RoundNumber).OrderBy(effect => effect.Id).ToListAsync();
        var responses = effects.Where(effect => effect.EffectCode is not ("mage-disorder" or HunterMarkCode) &&
            catalog.FindStatus(effect.EffectCode) is not null).Select(effect =>
        {
            var definition = catalog.FindStatus(effect.EffectCode);
            var persistent = effect.EffectCode is "rogue-shadow-charge" or "mage-disorder" ||
                effect.EffectCode.StartsWith("mage-skill-disruption-", StringComparison.Ordinal);
            return new BattleStatusEffectResponse
            {
                Code = effect.EffectCode,
                Name = definition?.Name ?? effect.EffectCode,
                Description = definition?.Description ?? string.Empty,
                IsPositive = definition?.IsPositive ?? false,
                CanDispel = definition?.IsDispellable ?? false,
                Stacks = effect.Stacks,
                RemainingRounds = persistent ? 0 : Math.Max(0, effect.ExpiresAfterRound - room.RoundNumber + 1),
                ExpiresWithRun = persistent
            };
        }).ToList();
        if (targetType != "Monster") return responses;

        // Disorder is keyed by its caster so several mages cannot consume each other's stacks.
        // Present those caster-owned counters on the monster, where players expect to see them.
        var disorder = await dbContext.BattleStatusEffects.Where(effect => effect.RoomId == room.Id &&
            effect.RunSequence == room.RunSequence && effect.TargetType == "Character" &&
            effect.EffectCode == "mage-disorder" && effect.PerTickValue == targetId &&
            effect.ExpiresAfterRound >= room.RoundNumber).ToListAsync();
        disorder.RemoveAll(effect => dbContext.Entry(effect).State == EntityState.Deleted);
        foreach (var local in dbContext.BattleStatusEffects.Local.Where(effect => effect.RoomId == room.Id &&
                     effect.RunSequence == room.RunSequence && effect.TargetType == "Character" &&
                     effect.EffectCode == "mage-disorder" && effect.PerTickValue == targetId &&
                     effect.ExpiresAfterRound >= room.RoundNumber &&
                     dbContext.Entry(effect).State != EntityState.Deleted))
            if (!disorder.Contains(local)) disorder.Add(local);
        var marks = await dbContext.BattleStatusEffects.Where(effect => effect.RoomId == room.Id &&
            effect.RunSequence == room.RunSequence && effect.TargetType == "Character" &&
            effect.EffectCode == HunterMarkCode && effect.PerTickValue == targetId &&
            effect.ExpiresAfterRound >= room.RoundNumber).ToListAsync();
        marks.RemoveAll(effect => dbContext.Entry(effect).State == EntityState.Deleted);
        foreach (var local in dbContext.BattleStatusEffects.Local.Where(effect => effect.RoomId == room.Id &&
                     effect.RunSequence == room.RunSequence && effect.TargetType == "Character" &&
                     effect.EffectCode == HunterMarkCode && effect.PerTickValue == targetId &&
                     effect.ExpiresAfterRound >= room.RoundNumber &&
                     dbContext.Entry(effect).State != EntityState.Deleted))
            if (!marks.Contains(local)) marks.Add(local);
        var casterIds = disorder.Select(effect => effect.TargetId).Concat(marks.Select(effect => effect.TargetId))
            .Distinct().ToList();
        if (casterIds.Count == 0) return responses;
        var casterNames = await dbContext.Characters.Where(character => casterIds.Contains(character.Id))
            .ToDictionaryAsync(character => character.Id, character => character.Name);
        responses.AddRange(disorder.OrderBy(effect => effect.TargetId).Select(effect => new BattleStatusEffectResponse
        {
            Code = "mage-disorder",
            Name = $"失序（{casterNames.GetValueOrDefault(effect.TargetId, "法师")}）",
            Description = "该法师每积累 3 层失序就触发一次回响；当前怪物死亡时清空。",
            IsPositive = false,
            CanDispel = false,
            Stacks = effect.Stacks,
            RemainingRounds = 0,
            ExpiresWithRun = true
        }));
        responses.AddRange(marks.OrderBy(effect => effect.TargetId).Select(effect => new BattleStatusEffectResponse
        {
            Code = HunterMarkCode,
            Name = $"猎物标记（{casterNames.GetValueOrDefault(effect.TargetId, "猎人")}）",
            Description = "该猎人可以消耗标记强化精准射击、破绽射击或协猎信号。",
            IsPositive = false,
            CanDispel = false,
            Stacks = 1,
            RemainingRounds = Math.Max(0, effect.ExpiresAfterRound - room.RoundNumber + 1),
            ExpiresWithRun = false
        }));
        return responses;
    }

    public async Task ExecuteIntentAsync(Room room, Monster monster,
        IReadOnlyList<MonsterCombatParticipant> participants,
        IReadOnlyDictionary<int, ElementType> mainWeaponElements,
        PlayerRoundDefense defense, List<string> logs,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses = null)
    {
        var intent = await EnsureIntentAsync(room, monster);
        dbContext.MonsterIntents.Remove(intent);
        var skill = intent.ActionType == "Skill" ? catalog.FindSkill(intent.SkillCode) : null;
        if (intent.IsInterrupted)
        {
            if (skill is not null) await StartCooldownAsync(room, monster, skill);
            var wasSilenced = (await GetActiveEffectsAsync(room, "Monster", [monster.Id])).Any(effect =>
                catalog.FindStatus(effect.EffectCode)?.EffectType == "SilenceNextIntent" &&
                effect.AppliedRound < room.RoundNumber);
            logs.Add(wasSilenced
                ? $"{monster.Name} 受到沉默影响，{skill?.Name ?? "行动"} 被自动打断，本回合行动取消。"
                : $"{monster.Name} 的 {skill?.Name ?? "行动"} 已被打断，本回合行动取消。");
            return;
        }
        if (skill is null)
        {
            var target = participants.SingleOrDefault(entry => entry.Character.Id == intent.TargetCharacterId && entry.Character.Hp > 0);
            if (target is null)
            {
                logs.Add($"{monster.Name} 的预定目标已经失效，本回合攻击取消。");
                return;
            }
            await DealDamageAsync(room, monster, target, 100, mainWeaponElements, defense, logs, null,
                operationBonuses, false);
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
            logs.Add($"{monster.Name} 的 {skill.Name} 失去有效目标，本回合行动取消。");
            return;
        }

        logs.Add($"{monster.Name} 使用 {skill.Name}。");
        var mageSkillReduction = skill.DamagePowerPercent > 0 && targets.Count > 0
            ? await ConsumeMageSkillDisruptionAsync(room, monster.Id) : 0;
        if (mageSkillReduction > 0)
            logs.Add($"{monster.Name} 的 {skill.Name} 受到奥术扰乱，直接伤害降低 {mageSkillReduction}%。");
        if (skill.DamagePowerPercent > 0)
        {
            foreach (var target in targets)
                await DealDamageAsync(room, monster, target, skill.DamagePowerPercent,
                    mainWeaponElements, defense, logs, skill.Name, operationBonuses,
                    skill.TargetType == "AllAlive", mageSkillReduction);
        }
        foreach (var application in skill.Statuses)
        {
            if (skill.TargetType == "Self")
                await ApplyStatusCoreAsync(room, "Monster", monster.Id, application, logs, monster.Name);
            else
                foreach (var target in targets.Where(target => target.Character.Hp > 0))
                    await ApplyStatusCoreAsync(room, "Character", target.Character.Id, application, logs,
                        $"{target.Slot.SlotIndex}号位 {target.Character.Name}");
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
        IReadOnlyList<MonsterCombatParticipant> participants, List<string> logs,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses = null,
        bool healingOnly = false)
    {
        var effects = await dbContext.BattleStatusEffects.Where(effect =>
            effect.RoomId == room.Id && effect.RunSequence == room.RunSequence).ToListAsync();
        effects.RemoveAll(effect => dbContext.Entry(effect).State == EntityState.Deleted);
        foreach (var effect in effects.Where(effect => effect.AppliedRound < room.RoundNumber &&
                     effect.ExpiresAfterRound >= room.RoundNumber))
        {
            var definition = catalog.FindStatus(effect.EffectCode);
            if (healingOnly && definition?.EffectType != "HealOverTime") continue;
            if (definition?.EffectType == "HealOverTime")
            {
                if (effect.TargetType != "Character") continue;
                var target = participants.SingleOrDefault(entry => entry.Character.Id == effect.TargetId);
                if (target is null || target.Character.Hp <= 0) continue;
                var maxHp = TalentRules.EffectiveMaxHp(target.Character);
                var healing = Math.Max(1, effect.PerTickValue ??
                    (int)decimal.Floor(Math.Abs(definition.ValuePerStack) * effect.Stacks));
                var restored = Math.Min(healing, Math.Max(0, maxHp - target.Character.Hp));
                if (restored <= 0) continue;
                target.Character.Hp += restored;
                logs.Add($"{target.Slot.SlotIndex}号位 {target.Character.Name} 受到 {definition.Name} 治疗，恢复 {restored} 点生命。");
                continue;
            }
            if (definition?.EffectType != "DamageOverTime") continue;
            var damage = Math.Max(1, effect.PerTickValue ??
                (int)decimal.Floor(Math.Abs(definition.ValuePerStack) * effect.Stacks));
            if (effect.TargetType == "Character")
            {
                var target = participants.SingleOrDefault(entry => entry.Character.Id == effect.TargetId);
                if (target is null || target.Character.Hp <= 0) continue;
                var taken = operationBonuses?.GetValueOrDefault(target.Character.Id).DamageTakenPercent ?? 0;
                damage = Math.Max(1, (int)decimal.Floor(damage * (1m + taken / 100m)));
                target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
                logs.Add($"{target.Slot.SlotIndex}号位 {target.Character.Name} 受到 {definition.Name} 造成的 {damage} 点伤害。");
            }
            else if (effect.TargetType == "Monster" && effect.TargetId == monster.Id && monster.Hp > 0)
            {
                damage = await AmplifyHunterDamageAsync(room, monster.Id, damage);
                monster.Hp = Math.Max(0, monster.Hp - damage);
                logs.Add($"{monster.Name} 受到 {definition.Name} 造成的 {damage} 点伤害。");
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
        dbContext.BattleStatusEffects.RemoveRange(dbContext.BattleStatusEffects.Local.Where(entry =>
            entry.RoomId == roomId && entry.TargetType == "Monster" && entry.TargetId == monsterId &&
            dbContext.Entry(entry).State != EntityState.Deleted).ToList());
        dbContext.BattleStatusEffects.RemoveRange(await dbContext.BattleStatusEffects
            .Where(entry => entry.RoomId == roomId && entry.TargetType == "Character" &&
                entry.EffectCode == "mage-disorder" && entry.PerTickValue == monsterId).ToListAsync());
        dbContext.BattleStatusEffects.RemoveRange(dbContext.BattleStatusEffects.Local.Where(entry =>
            entry.RoomId == roomId && entry.TargetType == "Character" &&
            entry.EffectCode == "mage-disorder" && entry.PerTickValue == monsterId &&
            dbContext.Entry(entry).State != EntityState.Deleted).ToList());
        dbContext.BattleStatusEffects.RemoveRange(await dbContext.BattleStatusEffects
            .Where(entry => entry.RoomId == roomId && entry.TargetType == "Character" &&
                entry.EffectCode == HunterMarkCode && entry.PerTickValue == monsterId).ToListAsync());
        dbContext.BattleStatusEffects.RemoveRange(dbContext.BattleStatusEffects.Local.Where(entry =>
            entry.RoomId == roomId && entry.TargetType == "Character" &&
            entry.EffectCode == HunterMarkCode && entry.PerTickValue == monsterId &&
            dbContext.Entry(entry).State != EntityState.Deleted).ToList());
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
        if (profile is null || profile.Skills.Count == 0) return null;
        var cooldowns = await dbContext.BattleMonsterSkillCooldowns.Where(entry =>
            entry.RoomId == room.Id && entry.MonsterId == monster.Id).ToListAsync();
        foreach (var local in dbContext.BattleMonsterSkillCooldowns.Local.Where(entry =>
                     entry.RoomId == room.Id && entry.MonsterId == monster.Id))
            if (!cooldowns.Contains(local)) cooldowns.Add(local);
        var eligible = profile.Skills.Select(entry => (Entry: entry, Skill: catalog.FindSkill(entry.Code)))
            .Where(candidate => candidate.Skill is not null &&
                (candidate.Skill.SelfHpBelowPercent is null ||
                 (long)monster.Hp * 100 <= (long)monster.MaxHp * candidate.Skill.SelfHpBelowPercent) &&
                (candidate.Skill.RoomRoundAtLeast is null || room.RoundNumber >= candidate.Skill.RoomRoundAtLeast) &&
                cooldowns.All(cooldown => !string.Equals(cooldown.SkillCode, candidate.Skill.Code, StringComparison.OrdinalIgnoreCase) ||
                    cooldown.ReadyAtRound <= room.RoundNumber)).ToList();
        if (eligible.Count == 0) return null;
        var forced = eligible.Where(candidate => candidate.Skill!.ForcedPriority > 0)
            .OrderByDescending(candidate => candidate.Skill!.ForcedPriority)
            .ThenByDescending(candidate => candidate.Skill!.RoomRoundAtLeast)
            .FirstOrDefault();
        if (forced.Skill is not null) return forced.Skill;
        if (profile.SkillUseChancePercent <= 0 ||
            (random ?? Random.Shared).Next(1, 101) > profile.SkillUseChancePercent) return null;
        var roll = (random ?? Random.Shared).Next(eligible.Sum(candidate => candidate.Entry.Weight));
        foreach (var candidate in eligible)
        {
            if (roll < candidate.Entry.Weight) return candidate.Skill;
            roll -= candidate.Entry.Weight;
        }
        return eligible[^1].Skill;
    }

    private async Task<bool> ApplyStatusCoreAsync(Room room, string targetType, int targetId,
        MonsterStatusApplicationOptions application, List<string> logs, string targetLabel,
        int? perTickValue = null)
    {
        var definition = catalog.FindStatus(application.StatusCode);
        if (definition is null) return false;
        if (perTickValue is <= 0) throw new ArgumentOutOfRangeException(nameof(perTickValue));
        var effect = dbContext.BattleStatusEffects.Local.FirstOrDefault(entry => entry.RoomId == room.Id &&
                entry.RunSequence == room.RunSequence && entry.TargetType == targetType && entry.TargetId == targetId &&
                entry.EffectCode == definition.Code);
        effect ??= await dbContext.BattleStatusEffects.SingleOrDefaultAsync(entry => entry.RoomId == room.Id &&
            entry.RunSequence == room.RunSequence && entry.TargetType == targetType && entry.TargetId == targetId &&
            entry.EffectCode == definition.Code);
        var wasActive = effect is not null && dbContext.Entry(effect).State != EntityState.Deleted &&
            effect.ExpiresAfterRound >= room.RoundNumber;
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
            if (!wasActive) effect.Stacks = 1;
            else if (definition.Stacking == "AddStack") effect.Stacks = Math.Min(definition.MaxStacks, effect.Stacks + 1);
            // Only a new exposure gets the first-round grace period. Refreshing an
            // existing poison/burn must not suppress its already-due damage tick.
            if (!wasActive || definition.EffectType is not ("DamageOverTime" or "HealOverTime"))
                effect.AppliedRound = room.RoundNumber;
        }
        if (!wasActive)
            effect.PerTickValue = perTickValue;
        else if (perTickValue.HasValue && definition.EffectType is "DamageOverTime" or "HealOverTime")
        {
            var currentPotency = effect.PerTickValue ??
                Math.Max(1, (int)decimal.Floor(Math.Abs(definition.ValuePerStack) * effect.Stacks));
            effect.PerTickValue = definition.Code == "mage-scorch-dot"
                ? perTickValue.Value : Math.Max(currentPotency, perTickValue.Value);
        }
        var expiresAfterRound = checked(room.RoundNumber + application.DurationRounds);
        // A shorter poison application from an ally must not cut an existing extended
        // poison short. New exposure after expiry or cleansing starts its own duration.
        effect.ExpiresAfterRound = wasActive && definition.EffectType is "DamageOverTime" or "HealOverTime"
            ? Math.Max(effect.ExpiresAfterRound, expiresAfterRound) : expiresAfterRound;
        logs.Add($"{targetLabel} 获得 {definition.Name}，持续 {application.DurationRounds} 回合{(effect.Stacks > 1 ? $"（{effect.Stacks} 层）" : "")}。");
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
        PlayerRoundDefense defense, List<string> logs, string? skillName,
        IReadOnlyDictionary<int, OperationPotionBonuses>? operationBonuses, bool isAreaAttack,
        int mageSkillReductionPercent = 0)
    {
        var talents = await dbContext.CharacterSkillTalents.Where(node => node.CharacterId == target.Character.Id &&
            (node.NodeCode == "sword-guard-stance" || node.NodeCode == "sword-counteroffense"))
            .Select(node => node.NodeCode).ToListAsync();
        var attackPercent = await GetModifierAsync(room, "Monster", monster.Id, "AttackPercent");
        var targetReduction = await GetModifierAsync(room, "Character", target.Character.Id, "ReductionPercent");
        if (talents.Contains("sword-guard-stance", StringComparer.OrdinalIgnoreCase)) targetReduction += 10;
        var guard = defense.ForCharacter(target.Character.Id);
        var potion = operationBonuses?.GetValueOrDefault(target.Character.Id) ?? default;
        var element = mainWeaponElements.TryGetValue(target.Character.Id, out var mainElement) ? mainElement : (ElementType?)null;
        var scaledAttack = Math.Max(1, (int)decimal.Floor(monster.Attack * powerPercent / 100m *
            (1m - mageSkillReductionPercent / 100m)));
        var damage = DamageCalculator.Calculate(scaledAttack, 0,
            factors: new DamageFactors(AttackPercent: attackPercent,
                ElementPercent: ElementMatchup.MonsterAttackPercent(monster.Element, element),
                ReductionPercent: WeaponCombatRules.CombinedDirectReductionPercent(
                    guard.ReductionPercent + targetReduction + (isAreaAttack ? potion.AreaDamageReductionPercent : 0) -
                    potion.DamageTakenPercent, target.Character)));
        target.Character.Hp = Math.Max(0, target.Character.Hp - damage);
        logs.Add(skillName is null
            ? $"{monster.Name} 普通攻击 {target.Slot.SlotIndex}号位 {target.Character.Name}，造成 {damage} 点伤害。"
            : $"{monster.Name} 使用 {skillName} 攻击 {target.Slot.SlotIndex}号位 {target.Character.Name}，造成 {damage} 点伤害。");
        if (monster.Hp > 0 && guard.ReductionPercent > 0 && guard.SourceCharacterId == target.Character.Id)
        {
            if (talents.Contains("sword-guard-stance", StringComparer.OrdinalIgnoreCase))
            {
                const int counterPower = 50;
                var counter = DamageCalculator.Calculate(TalentRules.EffectiveAttack(target.Character), monster.Defense,
                    factors: new DamageFactors(AttackPercent: WeaponCombatRules.AttackBonusPercent(target.Character, room.RoundNumber) +
                        potion.AttackPercent,
                        ConsumablePercent: potion.FinalDamagePercent), attackPowerPercent: counterPower);
                counter = await AmplifyHunterDamageAsync(room, monster.Id, counter);
                monster.Hp = Math.Max(0, monster.Hp - counter);
                logs.Add($"{target.Slot.SlotIndex}号位 {target.Character.Name} 招架后反击 {monster.Name}，造成 {counter} 点伤害。");
            }
            if (talents.Contains("sword-counteroffense", StringComparer.OrdinalIgnoreCase))
            {
                var state = dbContext.BattleStatusEffects.Local.FirstOrDefault(item => item.RoomId == room.Id && item.RunSequence == room.RunSequence &&
                    item.TargetType == "Character" && item.TargetId == target.Character.Id && item.EffectCode == "talent-guard-echo")
                    ?? await dbContext.BattleStatusEffects.SingleOrDefaultAsync(item => item.RoomId == room.Id && item.RunSequence == room.RunSequence &&
                        item.TargetType == "Character" && item.TargetId == target.Character.Id && item.EffectCode == "talent-guard-echo");
                if (state is null)
                {
                    state = new BattleStatusEffect { RoomId = room.Id, RunSequence = room.RunSequence, TargetType = "Character",
                        TargetId = target.Character.Id, EffectCode = "talent-guard-echo", Stacks = 1 };
                    dbContext.BattleStatusEffects.Add(state);
                }
                state.AppliedRound = room.RoundNumber;
                state.ExpiresAfterRound = room.RoundNumber + 3;
            }
        }
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
public readonly record struct CharacterRoundDefense(int ReductionPercent, int? SourceCharacterId = null);
public readonly record struct PlayerRoundDefense(int ReductionPercent, int? TargetCharacterId, int? SourceCharacterId = null,
    IReadOnlyDictionary<int, CharacterRoundDefense>? GuardsByCharacter = null)
{
    public CharacterRoundDefense ForCharacter(int characterId) => GuardsByCharacter is not null
        ? GuardsByCharacter.GetValueOrDefault(characterId)
        : TargetCharacterId == characterId ? new CharacterRoundDefense(ReductionPercent, SourceCharacterId) : default;
}
public sealed record RemovedBattleStatus(int TargetId, string Code, string Name, bool IsPositive);
