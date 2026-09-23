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
    private readonly ProfessionCatalog progression = professions ?? ProfessionCatalog.Default;

    public async Task<(ProductionOverviewResponse? Response, string? Error)> GetAsync(string? token)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        return error is null ? (await BuildResponseAsync(user!, character!), null) : (null, error);
    }

    public async Task<(ProductionOverviewResponse? Response, string? Error)> StartAsync(
        string? token, StartProductionRequest request)
    {
        var (user, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error is not null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        var recipe = catalog.FindRecipe(request.RecipeCode);
        if (recipe is null) return (null, "RecipeNotFound");
        if (character.Level < recipe.MinimumCharacterLevel) return (null, "LevelTooLow");
        if (character.AlchemyLevel < recipe.MinimumAlchemyLevel) return (null, "AlchemyLevelTooLow");
        var progress = await db.CharacterBattleMilestones.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && item.Kind == recipe.UnlockKind &&
            item.TargetCode == recipe.UnlockTargetCode).Select(item => (int?)item.Count).SingleOrDefaultAsync() ?? 0;
        if (progress < recipe.RequiredCount) return (null, "RecipeLocked");
        if (await CharacterActivityManager.IsBusyAsync(db, character.Id)) return (null, "CharacterBusy");

        var inputCodes = recipe.Ingredients.Select(item => item.Code).ToList();
        var stocks = await db.CharacterItemStacks.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && inputCodes.Contains(item.ItemCode))
            .ToDictionaryAsync(item => item.ItemCode, item => item.Quantity);
        if (recipe.Ingredients.Any(item => stocks.GetValueOrDefault(item.Code) < item.Quantity))
            return (null, "InsufficientMaterials");

        var talents = await db.CharacterProfessionTalents.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && item.ProfessionCode == ProfessionCatalog.AlchemyCode).ToListAsync();
        var cycleSeconds = Math.Max(1, recipe.CycleSeconds - progression.EffectValue(talents,
            ProfessionCatalog.AlchemyCode, "CycleReductionSeconds"));
        var now = DateTime.UtcNow;
        var deadline = now.AddHours(Math.Clamp(activities.Value.MaximumHours, 1, 12));
        var cycleTicks = TimeSpan.FromSeconds(cycleSeconds).Ticks;
        var cycleCount = (deadline.Ticks - now.Ticks + cycleTicks - 1) / cycleTicks;
        var task = new ProductionTask
        {
            UserId = user.Id, CharacterId = character.Id, RecipeCode = recipe.Code,
            OutputCode = recipe.OutputCode, OutputQuantity = recipe.OutputQuantity,
            ExtraYieldChancePercent = progression.EffectValue(talents,
                ProfessionCatalog.AlchemyCode, "ExtraYieldChancePercent"),
            IngredientSaveChancePercent = progression.EffectValue(talents,
                ProfessionCatalog.AlchemyCode, "IngredientSaveChancePercent"),
            IngredientsJson = JsonSerializer.Serialize(recipe.Ingredients),
            CycleSeconds = cycleSeconds, StartedAtUtc = now,
            EndsAtUtc = now.AddTicks(cycleCount * cycleTicks),
            NextCycleAtUtc = now.AddSeconds(cycleSeconds)
        };
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            db.ProductionTasks.Add(task);
            await db.SaveChangesAsync();
            CharacterActivityManager.StartProduction(db, task);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
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
        var task = await db.ProductionTasks.FindAsync(taskId);
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

    private async Task AdvanceCoreAsync(ProductionTask task, DateTime now)
    {
        var cutoff = now < task.EndsAtUtc ? now : task.EndsAtUtc;
        if (task.NextCycleAtUtc <= cutoff)
        {
            var due = checked((int)((cutoff.Ticks - task.NextCycleAtUtc.Ticks) /
                TimeSpan.FromSeconds(task.CycleSeconds).Ticks + 1));
            var ingredients = JsonSerializer.Deserialize<List<ProductionIngredientOptions>>(task.IngredientsJson)!;
            var inputCodes = ingredients.Select(item => item.Code).ToList();
            var inputs = await db.CharacterItemStacks.Where(item =>
                item.CharacterId == task.CharacterId && inputCodes.Contains(item.ItemCode))
                .ToDictionaryAsync(item => item.ItemCode);
            var output = await db.CharacterItemStacks.SingleOrDefaultAsync(item =>
                item.CharacterId == task.CharacterId && item.ItemCode == task.OutputCode);
            var cycles = 0;
            var produced = 0;
            var extraProduced = 0;
            var saved = 0;
            var stopReason = "MaterialShortage";
            for (var cycle = 0; cycle < due; cycle++)
            {
                if (task.CompletedCycles > int.MaxValue - cycle - 1 ||
                    task.SavedIngredientQuantity > int.MaxValue - saved - 1 ||
                    task.ExtraYieldQuantity > int.MaxValue - extraProduced - 1)
                {
                    stopReason = "InventoryFull";
                    break;
                }
                if (ingredients.Any(item => (inputs.GetValueOrDefault(item.Code)?.Quantity ?? 0) < item.Quantity))
                    break;
                var extra = ProfessionRoll.Succeeds(task.Id, task.CompletedCycles + cycle + 1, 3,
                    task.ExtraYieldChancePercent) ? 1 : 0;
                var nextOutput = (long)task.OutputQuantity + extra;
                if (nextOutput > int.MaxValue - (long)(output?.Quantity ?? 0) - produced ||
                    nextOutput > int.MaxValue - (long)task.TotalQuantity - produced)
                {
                    stopReason = "InventoryFull";
                    break;
                }
                foreach (var ingredient in ingredients)
                    inputs[ingredient.Code].Quantity -= ingredient.Quantity;
                if (ProfessionRoll.Succeeds(task.Id, task.CompletedCycles + cycle + 1, 4,
                    task.IngredientSaveChancePercent))
                {
                    inputs[ingredients[0].Code].Quantity++;
                    saved++;
                }
                produced += (int)nextOutput;
                extraProduced += extra;
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
                task.CompletedCycles += cycles;
                task.TotalQuantity += produced;
                task.ExtraYieldQuantity += extraProduced;
                task.SavedIngredientQuantity += saved;
                task.NextCycleAtUtc = task.NextCycleAtUtc.AddSeconds((long)cycles * task.CycleSeconds);
                var character = await db.Characters.FindAsync(task.CharacterId);
                if (character is not null)
                    progression.GrantExperience(character, ProfessionCatalog.AlchemyCode,
                        (long)cycles * progression.AlchemyExperiencePerCycle);
            }
            if (cycles < due)
            {
                task.Status = stopReason;
                task.StoppedAtUtc = task.NextCycleAtUtc;
                await ReleaseActivityAsync(task);
                return;
            }
        }
        if (now >= task.EndsAtUtc)
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
        var profession = await progression.BuildProgressAsync(db, character, ProfessionCatalog.AlchemyCode);
        var talents = await db.CharacterProfessionTalents.AsNoTracking().Where(item =>
            item.CharacterId == character.Id && item.ProfessionCode == ProfessionCatalog.AlchemyCode).ToListAsync();
        var cycleReduction = progression.EffectValue(talents, ProfessionCatalog.AlchemyCode, "CycleReductionSeconds");
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
            var count = milestones.FirstOrDefault(item => item.Kind == recipe.UnlockKind &&
                item.TargetCode == recipe.UnlockTargetCode)?.Count ?? 0;
            return new ProductionRecipeResponse
            {
                Code = recipe.Code, Name = recipe.Name,
                OutputCode = recipe.OutputCode,
                OutputName = consumables.FindItem(recipe.OutputCode)!.Name,
                OutputQuantity = recipe.OutputQuantity,
                CharacterQuantity = bag.GetValueOrDefault(recipe.OutputCode),
                CycleSeconds = Math.Max(1, recipe.CycleSeconds - cycleReduction),
                MinimumCharacterLevel = recipe.MinimumCharacterLevel,
                MinimumAlchemyLevel = recipe.MinimumAlchemyLevel,
                UnlockDescription = recipe.UnlockKind == BattleMilestoneService.MonsterKillKind
                    ? $"击败 {source.MonsterName} {recipe.RequiredCount} 次"
                    : $"通关 {source.Name} {recipe.RequiredCount} 次",
                UnlockProgress = Math.Min(count, recipe.RequiredCount),
                UnlockRequired = recipe.RequiredCount,
                IsUnlocked = count >= recipe.RequiredCount,
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
            RecipeName = catalog.FindRecipe(task.RecipeCode)?.Name ?? task.RecipeCode,
            OutputName = consumables.FindItem(task.OutputCode)?.Name ?? task.OutputCode,
            Status = task.Status, CycleSeconds = task.CycleSeconds,
            StartedAtUtc = task.StartedAtUtc, EndsAtUtc = task.EndsAtUtc,
            NextCycleAtUtc = task.NextCycleAtUtc, StoppedAtUtc = task.StoppedAtUtc,
            CompletedCycles = task.CompletedCycles, TotalQuantity = task.TotalQuantity,
            ExtraYieldQuantity = task.ExtraYieldQuantity,
            SavedIngredientQuantity = task.SavedIngredientQuantity
        };
        return new ProductionOverviewResponse
        {
            CharacterId = character.Id, CharacterName = character.Name,
            AlchemyLevel = character.AlchemyLevel, Profession = profession, ServerTimeUtc = DateTime.UtcNow,
            Recipes = recipes,
            ActiveTask = tasks.FirstOrDefault(task => task.Status == "Running") is { } active ? Map(active) : null,
            RecentTasks = tasks.Where(task => task.Status != "Running").Take(5).Select(Map).ToList()
        };
    }
}
