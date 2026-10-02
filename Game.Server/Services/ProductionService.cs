using System.Text.Json;
using Game.Server.Configuration;
using Game.Server.Data;
using Game.Shared.Dtos.Production;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Server.Services;

public sealed class ProductionService(GameDbContext db, UserService users, ProductionCatalog catalog,
    WorldCatalog world, MaterialCatalog materials, ConsumableCatalog consumables,
    IOptions<ActivityOptions> activities, ProfessionCatalog? professions = null)
{
    public async Task<(ProductionOverviewResponse? Response, string? Error)> GetAsync(string? token)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        await SettleCharacterTrackedAsync(character!.Id, DateTime.UtcNow);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception)) { db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict"); }
        return (await BuildResponseAsync(user!, character), null);
    }

    public async Task<(ProductionOverviewResponse? Response, string? Error)> StartAsync(
        string? token, StartProductionRequest request)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        if (request.TargetCycles is < 1 or > 4320) return (null, "InvalidTargetCycles");
        if (!Guid.TryParse(request.RequestId, out _)) return (null, "InvalidRequestId");
        var requestId = Guid.Parse(request.RequestId!).ToString("N");
        if (requestId is not null)
        {
            var prior = await db.ProductionTasks.AsNoTracking().SingleOrDefaultAsync(item => item.CharacterId == character.Id && item.RequestId == requestId);
            if (prior is not null) return prior.RecipeCode.Equals(request.RecipeCode, StringComparison.OrdinalIgnoreCase) && prior.TargetCycles == request.TargetCycles
                ? await GetAsync(token) : (null, "RequestIdConflict");
        }
        var recipe = catalog.FindRecipe(request.RecipeCode);
        if (recipe is null) return (null, "RecipeNotFound");
        if (character.Level < recipe.MinimumCharacterLevel) return (null, "LevelTooLow");

        var unlockTargets = recipe.AlternativeUnlockTargetCodes.Prepend(recipe.UnlockTargetCode).ToList();
        var progress = await db.CharacterBattleMilestones.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && item.Kind == recipe.UnlockKind &&
            unlockTargets.Contains(item.TargetCode)).Select(item => (int?)item.Count).MaxAsync() ?? 0;
        if (progress < recipe.RequiredCount) return (null, "RecipeLocked");
        await using var transaction = await db.Database.BeginTransactionAsync();
        await SettleCharacterTrackedAsync(character.Id, DateTime.UtcNow);
        if (db.ProductionTasks.Local.Any(item => item.CharacterId == character.Id && item.Status == "Running"))
        {
            try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
            catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception)) { db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict"); }
            return (null, "CharacterBusy");
        }

        var inputCodes = recipe.Ingredients.Select(item => item.Code).ToList();
        await db.CharacterItemStacks.Where(item => item.CharacterId == character.Id && inputCodes.Contains(item.ItemCode)).LoadAsync();
        var stocks = db.CharacterItemStacks.Local.Where(item => item.CharacterId == character.Id && inputCodes.Contains(item.ItemCode))
            .ToDictionary(item => item.ItemCode, item => item.Quantity);
        if (recipe.Ingredients.Any(item => stocks.GetValueOrDefault(item.Code) < item.Quantity))
        {
            try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
            catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception)) { db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict"); }
            return (null, "InsufficientMaterials");
        }

        var cycleSeconds = recipe.CycleSeconds;
        var now = DateTime.UtcNow;
        var deadline = now.AddHours(Math.Clamp(activities.Value.MaximumHours, 1, 12));
        var cycleTicks = TimeSpan.FromSeconds(cycleSeconds).Ticks;
        var cycleCount = request.TargetCycles ?? (deadline.Ticks - now.Ticks + cycleTicks - 1) / cycleTicks;
        var task = new ProductionTask
        {
            UserId = user!.Id, CharacterId = character.Id, RecipeCode = recipe.Code,
            OutputCode = recipe.OutputCode, OutputQuantity = recipe.OutputQuantity,
            TargetCycles = request.TargetCycles, RequestId = requestId,
            IngredientsJson = JsonSerializer.Serialize(recipe.Ingredients),
            CycleSeconds = cycleSeconds, StartedAtUtc = now,
            EndsAtUtc = now.AddTicks(cycleCount * cycleTicks),
            NextCycleAtUtc = now.AddSeconds(cycleSeconds)
        };
        try
        {
            character.Version++;
            db.ProductionTasks.Add(task);
            await db.SaveChangesAsync();

            await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return (null, "ConcurrencyConflict");
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            var prior = await db.ProductionTasks.AsNoTracking().SingleOrDefaultAsync(item =>
                item.CharacterId == request.CharacterId && item.RequestId == requestId);
            if (prior is not null)
                return prior.RecipeCode.Equals(request.RecipeCode, StringComparison.OrdinalIgnoreCase) && prior.TargetCycles == request.TargetCycles
                    ? await GetAsync(token) : (null, "RequestIdConflict");
            return (null, "CharacterBusy");
        }
        return (await BuildResponseAsync(user, character), null);
    }

    public async Task<(ProductionOverviewResponse? Response, string? Error)> StopAsync(string? token, int taskId)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        var task = await db.ProductionTasks.FindAsync(taskId);
        if (task is null || task.UserId != user!.Id || task.CharacterId != character!.Id)
            return (null, "TaskNotFound");
        await using var transaction = await db.Database.BeginTransactionAsync();
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
            try { await db.SaveChangesAsync(); await transaction.CommitAsync(); }
            catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
            {
                await transaction.RollbackAsync();
                db.ChangeTracker.Clear();
                return (null, "ConcurrencyConflict");
            }
        }
        return (await BuildResponseAsync(user, character), null);
    }

    public async Task<string?> AdvanceDueAsync(int taskId, DateTime now)
    {
        var task = await db.ProductionTasks.FindAsync(taskId);
        if (task is null || task.Status != "Running") return null;
        if (now < task.NextCycleAtUtc && now < task.EndsAtUtc) return null;
        await using var transaction = await db.Database.BeginTransactionAsync();
        await AdvanceCoreAsync(task, now);
        task.Version++;
        try { await db.SaveChangesAsync(); await transaction.CommitAsync(); return null; }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception))
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            return "ConcurrencyConflict";
        }
    }

    public async Task SettleCharacterTrackedAsync(int characterId, DateTime now)
    {
        await db.ProductionTasks.Where(item => item.CharacterId == characterId && item.Status == "Running").LoadAsync();
        foreach (var task in db.ProductionTasks.Local.Where(item => item.CharacterId == characterId && item.Status == "Running").ToList())
        {
            if (now < task.NextCycleAtUtc && now < task.EndsAtUtc) continue;
            await AdvanceCoreAsync(task, now);
            task.Version++;
        }
    }

    private async Task AdvanceCoreAsync(ProductionTask task, DateTime now)
    {
        var cutoff = now < task.EndsAtUtc ? now : task.EndsAtUtc;
        if (task.NextCycleAtUtc <= cutoff)
        {
            var due = checked((int)((cutoff.Ticks - task.NextCycleAtUtc.Ticks) /
                TimeSpan.FromSeconds(task.CycleSeconds).Ticks + 1));
            if (task.TargetCycles is { } target) due = Math.Min(due, Math.Max(0, target - task.CompletedCycles));
            var ingredients = JsonSerializer.Deserialize<List<ProductionIngredientOptions>>(task.IngredientsJson)!;
            var inputCodes = ingredients.Select(item => item.Code).ToList();
            var inputs = await db.CharacterItemStacks.Where(item =>
                item.CharacterId == task.CharacterId && inputCodes.Contains(item.ItemCode))
                .ToDictionaryAsync(item => item.ItemCode);
            foreach (var local in db.CharacterItemStacks.Local.Where(item => item.CharacterId == task.CharacterId && inputCodes.Contains(item.ItemCode))) inputs[local.ItemCode] = local;
            var output = db.CharacterItemStacks.Local.FirstOrDefault(item => item.CharacterId == task.CharacterId && item.ItemCode == task.OutputCode)
                ?? await db.CharacterItemStacks.SingleOrDefaultAsync(item =>
                item.CharacterId == task.CharacterId && item.ItemCode == task.OutputCode);
            var cycles = 0;
            var produced = 0;

            var stopReason = "MaterialShortage";
            for (var cycle = 0; cycle < due; cycle++)
            {
                if (task.CompletedCycles > int.MaxValue - cycle - 1)
                {
                    stopReason = "InventoryFull";
                    break;
                }
                if (ingredients.Any(item => (inputs.GetValueOrDefault(item.Code)?.Quantity ?? 0) < item.Quantity))
                    break;
                var nextOutput = (long)task.OutputQuantity;
                if (nextOutput > int.MaxValue - (long)(output?.Quantity ?? 0) - produced ||
                    nextOutput > int.MaxValue - (long)task.TotalQuantity - produced)
                {
                    stopReason = "InventoryFull";
                    break;
                }
                foreach (var ingredient in ingredients)
                    inputs[ingredient.Code].Quantity -= ingredient.Quantity;
                produced += (int)nextOutput;
                cycles++;
            }
            if (cycles > 0)
            {
                foreach (var ingredient in ingredients)
                    inputs[ingredient.Code].Version++;
                if (output is null)
                    db.CharacterItemStacks.Add(new CharacterItemStack
                    {
                        CharacterId = task.CharacterId, ItemCode = task.OutputCode, Quantity = produced
                    });
                else
                {
                    output.Quantity += produced;
                    output.Version++;
                }
                var oldCompletedCycles = task.CompletedCycles;
                task.CompletedCycles += cycles;
                task.TotalQuantity += produced;
                await StoryProgressService.RecordAsync(db, task.CharacterId, "Produce", task.OutputCode,
                    $"production:{task.Id}:{oldCompletedCycles}:{task.CompletedCycles}", produced, now);
                await StoryProgressService.RefreshAsync(db, task.CharacterId, now);

                task.NextCycleAtUtc = task.NextCycleAtUtc.AddSeconds((long)cycles * task.CycleSeconds);

            }
            if (cycles < due)
            {
                task.Status = stopReason;
                task.StoppedAtUtc = task.NextCycleAtUtc;
                await ReleaseActivityAsync(task);
                return;
            }
        }
        if (now >= task.EndsAtUtc || task.TargetCycles is { } targetCycles && task.CompletedCycles >= targetCycles)
        {
            task.Status = "Completed";
            task.StoppedAtUtc = task.EndsAtUtc;
            await ReleaseActivityAsync(task);
        }
    }

    private async Task ReleaseActivityAsync(ProductionTask task)
    {
        var activity = await db.CharacterActivities.FindAsync(task.CharacterId);
        if (activity is { Kind: CharacterActivityManager.ProductionKind } && activity.SourceId == task.Id)
            db.CharacterActivities.Remove(activity);
    }

    private async Task<ProductionOverviewResponse> BuildResponseAsync(User user, Character character)
    {
        var milestones = await db.CharacterBattleMilestones.AsNoTracking()
            .Where(item => item.CharacterId == character.Id).ToListAsync();
        var bag = await db.CharacterItemStacks.AsNoTracking()
            .Where(item => item.CharacterId == character.Id)
            .ToDictionaryAsync(item => item.ItemCode, item => item.Quantity);
        var tasks = await db.ProductionTasks.AsNoTracking()
            .Where(item => item.UserId == user.Id && item.CharacterId == character.Id)
            .OrderByDescending(item => item.Id).Take(6).ToListAsync();
        var recipes = catalog.Recipes.Select(recipe =>
        {
            var source = world.Dungeons.Single(dungeon => dungeon.Code == recipe.UnlockTargetCode);
            var output = consumables.FindItem(recipe.OutputCode)!;
            var unlockTargets = recipe.AlternativeUnlockTargetCodes.Prepend(recipe.UnlockTargetCode).ToHashSet();
            var count = milestones.Where(item => item.Kind == recipe.UnlockKind &&
                unlockTargets.Contains(item.TargetCode)).Select(item => item.Count).DefaultIfEmpty(0).Max();
            return new ProductionRecipeResponse
            {
                Code = recipe.Code, Name = recipe.Name,
                OutputCode = recipe.OutputCode,
                OutputName = output.Name,
                OutputDescription = ConsumableCatalog.Description(output, character.Level),
                OutputQuantity = recipe.OutputQuantity,
                CharacterQuantity = bag.GetValueOrDefault(recipe.OutputCode),
                CycleSeconds = recipe.CycleSeconds,
                MinimumCharacterLevel = recipe.MinimumCharacterLevel,
                UnlockDescription = recipe.AlternativeUnlockTargetCodes.Count > 0
                    ? recipe.UnlockKind == BattleMilestoneService.MonsterKillKind
                        ? $"击败以下任一怪物 {recipe.RequiredCount} 次：{string.Join("、", world.Dungeons.Where(dungeon => unlockTargets.Contains(dungeon.Code)).Select(dungeon => dungeon.MonsterName))}"
                        : $"通关以下任一副本 {recipe.RequiredCount} 次：{string.Join("、", world.Dungeons.Where(dungeon => unlockTargets.Contains(dungeon.Code)).Select(dungeon => dungeon.Name))}"
                    : recipe.UnlockKind == BattleMilestoneService.MonsterKillKind
                    ? $"击败 {source.MonsterName} {recipe.RequiredCount} 次"
                    : $"通关 {source.Name} {recipe.RequiredCount} 次",
                UnlockProgress = Math.Min(count, recipe.RequiredCount),
                UnlockRequired = recipe.RequiredCount,
                IsUnlocked = character.Level >= recipe.MinimumCharacterLevel && count >= recipe.RequiredCount,
                Ingredients = recipe.Ingredients.Select(item => new ProductionIngredientResponse
                {
                    Code = item.Code, Name = materials.FindItem(item.Code)!.Name,
                    Quantity = item.Quantity,
                    CharacterQuantity = bag.GetValueOrDefault(item.Code)
                }).ToList()
            };
        }).ToList();
        ProductionTaskResponse Map(ProductionTask task) => new()
        {
            Id = task.Id, RecipeCode = task.RecipeCode,
            RecipeName = catalog.FindRecipe(task.RecipeCode)?.Name ??
                consumables.FindItem(task.OutputCode)?.Name ?? task.RecipeCode,
            OutputName = consumables.FindItem(task.OutputCode)?.Name ?? task.OutputCode,
            Status = task.Status, CycleSeconds = task.CycleSeconds,
            StartedAtUtc = task.StartedAtUtc, EndsAtUtc = task.EndsAtUtc,
            NextCycleAtUtc = task.NextCycleAtUtc, StoppedAtUtc = task.StoppedAtUtc,
            TargetCycles = task.TargetCycles, CompletedCycles = task.CompletedCycles, TotalQuantity = task.TotalQuantity,
            ExtraYieldQuantity = task.ExtraYieldQuantity,
            SavedIngredientQuantity = task.SavedIngredientQuantity
        };
        return new ProductionOverviewResponse
        {
            CharacterId = character.Id, CharacterName = character.Name,
            ServerTimeUtc = DateTime.UtcNow,
            Recipes = recipes,
            ActiveTask = tasks.FirstOrDefault(task => task.Status == "Running") is { } active ? Map(active) : null,
            RecentTasks = tasks.Where(task => task.Status != "Running").Take(5).Select(Map).ToList()
        };
    }
}
