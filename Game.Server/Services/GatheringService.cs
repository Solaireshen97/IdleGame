using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Dtos.Gathering;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class GatheringService(GameDbContext db, UserService users, GatheringCatalog catalog,
    WorldCatalog world, MaterialCatalog materials, IOptions<ActivityOptions> activities,
    ProfessionCatalog? professions = null)
{
    private readonly ProfessionCatalog progression = professions ?? ProfessionCatalog.Default;

    public async Task<(GatheringOverviewResponse? Response, string? Error)> GetAsync(string? token)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        return error is null ? (await BuildResponseAsync(user!, character!), null) : (null, error);
    }

    public async Task<(GatheringOverviewResponse? Response, string? Error)> StartAsync(
        string? token, StartGatheringRequest request)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        var point = catalog.FindPoint(request.PointCode);
        if (point is null) return (null, "PointNotFound");
        if (character.Level < point.MinimumCharacterLevel) return (null, "LevelTooLow");
        if (character.GatheringLevel < point.MinimumGatheringLevel) return (null, "GatheringLevelTooLow");
        var progress = await db.CharacterBattleMilestones.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && item.Kind == point.UnlockKind &&
            item.TargetCode == point.UnlockTargetCode).Select(item => (int?)item.Count).SingleOrDefaultAsync() ?? 0;
        if (progress < point.RequiredCount) return (null, "PointLocked");
        if (await CharacterActivityManager.IsBusyAsync(db, character.Id)) return (null, "CharacterBusy");
        if (point.IsRare && !await db.CharacterGatheringOpportunities.AnyAsync(item =>
                item.CharacterId == character.Id && item.PointCode == point.Code && item.AvailableCount > 0))
            return (null, "NoGatheringOpportunity");

        var talents = await db.CharacterProfessionTalents.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && item.ProfessionCode == ProfessionCatalog.GatheringCode).ToListAsync();
        var cycleSeconds = Math.Max(1, point.CycleSeconds - progression.EffectValue(talents,
            ProfessionCatalog.GatheringCode, "CycleReductionSeconds"));
        var now = DateTime.UtcNow;
        var hours = Math.Clamp(activities.Value.MaximumHours, 1, 12);
        var deadline = now.AddHours(hours);
        var cycleTicks = TimeSpan.FromSeconds(cycleSeconds).Ticks;
        var cycleCount = point.IsRare ? 1 : (deadline.Ticks - now.Ticks + cycleTicks - 1) / cycleTicks;
        var task = new GatheringTask
        {
            UserId = user!.Id, CharacterId = character.Id, PointCode = point.Code,
            MaterialCode = point.MaterialCode, IsRare = point.IsRare, CycleSeconds = cycleSeconds,
            OutputQuantity = point.OutputQuantity,
            ExtraYieldChancePercent = point.IsRare ? 0 : progression.EffectValue(talents,
                ProfessionCatalog.GatheringCode, "ExtraYieldChancePercent"),
            RareBonusChancePercent = point.IsRare ? progression.EffectValue(talents,
                ProfessionCatalog.GatheringCode, "RareBonusChancePercent") : 0,
            BonusMaterialCode = point.IsRare ? point.BonusMaterialCode : null,
            StartedAtUtc = now, EndsAtUtc = now.AddTicks(cycleCount * cycleTicks),
            NextCycleAtUtc = now.AddSeconds(cycleSeconds)
        };
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            character.Version++;
            db.GatheringTasks.Add(task);
            await db.SaveChangesAsync();
            CharacterActivityManager.StartGathering(db, task);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return (null, "CharacterBusy");
        }
        return (await BuildResponseAsync(user, character), null);
    }

    public async Task<(GatheringOverviewResponse? Response, string? Error)> StopAsync(string? token, int taskId)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        var task = await db.GatheringTasks.FindAsync(taskId);
        if (task is null || task.UserId != user!.Id || task.CharacterId != character!.Id)
            return (null, "TaskNotFound");
        if (task.Status == "Running")
        {
            var now = DateTime.UtcNow;
            await AdvanceCoreAsync(task, now);
            if (task.Status == "Running")
            {
                task.Status = "Stopped";
                task.StoppedAtUtc = now;
                await ReleaseActivityAsync(task);
            }
            task.Version++;
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                return (null, "ConcurrencyConflict");
            }
        }
        return (await BuildResponseAsync(user, character), null);
    }

    public async Task<string?> AdvanceDueAsync(int taskId, DateTime now)
    {
        var task = await db.GatheringTasks.FindAsync(taskId);
        if (task is null || task.Status != "Running") return null;
        if (now < task.NextCycleAtUtc && now < task.EndsAtUtc) return null;
        await AdvanceCoreAsync(task, now);
        task.Version++;
        try { await db.SaveChangesAsync(); return null; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return "ConcurrencyConflict";
        }
    }

    private async Task AdvanceCoreAsync(GatheringTask task, DateTime now)
    {
        var cutoff = now < task.EndsAtUtc ? now : task.EndsAtUtc;
        if (task.NextCycleAtUtc <= cutoff)
        {
            var cycles = task.IsRare ? 1 : checked((int)((cutoff.Ticks - task.NextCycleAtUtc.Ticks) /
                TimeSpan.FromSeconds(task.CycleSeconds).Ticks + 1));
            var opportunity = task.IsRare
                ? await db.CharacterGatheringOpportunities.SingleOrDefaultAsync(item =>
                    item.CharacterId == task.CharacterId && item.PointCode == task.PointCode)
                : null;
            if (task.IsRare && opportunity is not { AvailableCount: > 0 })
            {
                task.Status = "NoOpportunity";
                task.StoppedAtUtc = now;
                await ReleaseActivityAsync(task);
                return;
            }
            if (task.CompletedCycles > int.MaxValue - cycles)
            {
                task.Status = "InventoryFull";
                task.StoppedAtUtc = now;
                await ReleaseActivityAsync(task);
                return;
            }
            var extra = 0;
            for (var cycle = 1; cycle <= cycles; cycle++)
                if (ProfessionRoll.Succeeds(task.Id, task.CompletedCycles + cycle, 1,
                    task.ExtraYieldChancePercent)) extra++;
            var bonus = task.IsRare && task.BonusMaterialCode is not null &&
                ProfessionRoll.Succeeds(task.Id, task.CompletedCycles + 1, 2,
                    task.RareBonusChancePercent) ? 1 : 0;
            var quantityLong = (long)cycles * task.OutputQuantity + extra;
            var stack = await db.CharacterItemStacks.SingleOrDefaultAsync(item =>
                item.CharacterId == task.CharacterId && item.ItemCode == task.MaterialCode);
            var bonusStack = bonus > 0 ? await db.CharacterItemStacks.SingleOrDefaultAsync(item =>
                item.CharacterId == task.CharacterId && item.ItemCode == task.BonusMaterialCode) : null;
            if (quantityLong > int.MaxValue ||
                stack is not null && stack.Quantity > int.MaxValue - quantityLong ||
                task.TotalQuantity > int.MaxValue - quantityLong || task.ExtraYieldQuantity > int.MaxValue - extra ||
                bonus > 0 && (bonusStack?.Quantity == int.MaxValue || task.BonusQuantity == int.MaxValue))
            {
                task.Status = "InventoryFull";
                task.StoppedAtUtc = now;
                await ReleaseActivityAsync(task);
                return;
            }
            var quantity = (int)quantityLong;
            if (stack is null)
                db.CharacterItemStacks.Add(new CharacterItemStack
                {
                    CharacterId = task.CharacterId, ItemCode = task.MaterialCode, Quantity = quantity
                });
            else
            {
                stack.Quantity += quantity;
                stack.Version++;
            }
            if (bonus > 0)
            {
                if (bonusStack is null)
                    db.CharacterItemStacks.Add(new CharacterItemStack
                    {
                        CharacterId = task.CharacterId, ItemCode = task.BonusMaterialCode!, Quantity = bonus
                    });
                else
                {
                    bonusStack.Quantity += bonus;
                    bonusStack.Version++;
                }
                task.BonusQuantity += bonus;
            }
            task.CompletedCycles += cycles;
            task.TotalQuantity += quantity;
            task.ExtraYieldQuantity += extra;
            task.NextCycleAtUtc = task.NextCycleAtUtc.AddSeconds((long)cycles * task.CycleSeconds);
            var character = await db.Characters.FindAsync(task.CharacterId);
            if (character is not null)
                progression.GrantExperience(character, ProfessionCatalog.GatheringCode,
                    (long)cycles * (task.IsRare ? progression.RareGatheringExperiencePerCycle : progression.GatheringExperiencePerCycle));
            if (opportunity is not null)
            {
                opportunity.AvailableCount--;
                opportunity.SpentCount++;
                opportunity.Version++;
            }
        }
        if ((task.IsRare && task.CompletedCycles > 0) || now >= task.EndsAtUtc)
        {
            task.Status = "Completed";
            task.StoppedAtUtc = task.EndsAtUtc;
            await ReleaseActivityAsync(task);
        }
    }

    private async Task ReleaseActivityAsync(GatheringTask task)
    {
        var activity = await db.CharacterActivities.FindAsync(task.CharacterId);
        if (activity is { Kind: CharacterActivityManager.GatheringKind } && activity.SourceId == task.Id)
            db.CharacterActivities.Remove(activity);
    }

    private async Task<GatheringOverviewResponse> BuildResponseAsync(User user, Character character)
    {
        var talents = await db.CharacterProfessionTalents.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && item.ProfessionCode == ProfessionCatalog.GatheringCode).ToListAsync();
        var cycleReduction = progression.EffectValue(talents, ProfessionCatalog.GatheringCode, "CycleReductionSeconds");
        var milestones = await db.CharacterBattleMilestones.AsNoTracking()
            .Where(item => item.CharacterId == character.Id).ToListAsync();
        var opportunities = await db.CharacterGatheringOpportunities.AsNoTracking()
            .Where(item => item.CharacterId == character.Id)
            .ToDictionaryAsync(item => item.PointCode, item => item.AvailableCount);
        var stacks = await db.CharacterItemStacks.AsNoTracking()
            .Where(item => item.CharacterId == character.Id)
            .ToDictionaryAsync(item => item.ItemCode, item => item.Quantity);
        var tasks = await db.GatheringTasks.AsNoTracking()
            .Where(item => item.CharacterId == character.Id && item.UserId == user.Id)
            .OrderByDescending(item => item.Id).Take(6).ToListAsync();
        var points = catalog.Points.Select(point =>
        {
            var target = world.Dungeons.Single(dungeon => dungeon.Code == point.UnlockTargetCode);
            var count = milestones.FirstOrDefault(item => item.Kind == point.UnlockKind &&
                item.TargetCode == point.UnlockTargetCode)?.Count ?? 0;
            return new GatheringPointResponse
            {
                Code = point.Code, Name = point.Name,
                IsRare = point.IsRare,
                AvailableOpportunities = opportunities.GetValueOrDefault(point.Code),
                RegionName = world.Regions.Single(region => region.Code == point.RegionCode).Name,
                MaterialCode = point.MaterialCode,
                MaterialName = materials.FindItem(point.MaterialCode)!.Name,
                CharacterQuantity = stacks.GetValueOrDefault(point.MaterialCode),
                OutputQuantity = point.OutputQuantity, CycleSeconds = Math.Max(1, point.CycleSeconds - cycleReduction),
                MinimumCharacterLevel = point.MinimumCharacterLevel,
                MinimumGatheringLevel = point.MinimumGatheringLevel,
                UnlockDescription = point.UnlockKind == BattleMilestoneService.MonsterKillKind
                    ? $"击败 {target.MonsterName} {point.RequiredCount} 次"
                    : $"通关 {target.Name} {point.RequiredCount} 次",
                UnlockProgress = Math.Min(count, point.RequiredCount),
                UnlockRequired = point.RequiredCount,
                IsUnlocked = count >= point.RequiredCount
            };
        }).ToList();
        GatheringTaskResponse Map(GatheringTask task)
        {
            var point = catalog.FindPoint(task.PointCode);
            return new GatheringTaskResponse
            {
                Id = task.Id, PointCode = task.PointCode, PointName = point?.Name ?? task.PointCode,
                IsRare = task.IsRare,
                MaterialName = materials.FindItem(task.MaterialCode)?.Name ?? task.MaterialCode,
                Status = task.Status, StartedAtUtc = task.StartedAtUtc, EndsAtUtc = task.EndsAtUtc,
                NextCycleAtUtc = task.NextCycleAtUtc, StoppedAtUtc = task.StoppedAtUtc,
                CycleSeconds = task.CycleSeconds, CompletedCycles = task.CompletedCycles,
                TotalQuantity = task.TotalQuantity,
                ExtraYieldQuantity = task.ExtraYieldQuantity,
                BonusMaterialName = materials.FindItem(task.BonusMaterialCode)?.Name,
                BonusQuantity = task.BonusQuantity
            };
        }
        return new GatheringOverviewResponse
        {
            CharacterId = character.Id, CharacterName = character.Name, ServerTimeUtc = DateTime.UtcNow,
            GatheringLevel = character.GatheringLevel,
            Points = points,
            ActiveTask = tasks.FirstOrDefault(task => task.Status == "Running") is { } active ? Map(active) : null,
            RecentTasks = tasks.Where(task => task.Status != "Running").Take(5).Select(Map).ToList()
        };
    }
}
