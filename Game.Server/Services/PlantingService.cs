using Game.Server.Data;
using Game.Shared.Dtos.Planting;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
namespace Game.Server.Services;
public sealed class PlantingService(GameDbContext db, UserService users, PlantingCatalog catalog, ProductionService? production = null, WorldCatalog? world = null)
{
    public async Task<(PlantingOverviewResponse? Response, string? Error)> GetAsync(string? token)
    {
        var (_, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error != null) return (null, error);
        await EnsurePlotsAsync(character!.Id);
        return (await OverviewAsync(character), null);
    }
    private async Task EnsurePlotsAsync(int characterId)
    {
        var indices = await db.CharacterGardenPlots.Where(p => p.CharacterId == characterId).Select(p => p.PlotIndex).ToListAsync();
        if (indices.Count == 4) return;
        foreach (var index in Enumerable.Range(0, 4).Except(indices)) db.CharacterGardenPlots.Add(new() { CharacterId = characterId, PlotIndex = index });
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception)) { db.ChangeTracker.Clear(); if (await db.CharacterGardenPlots.CountAsync(p => p.CharacterId == characterId) != 4) throw; }
    }
    private async Task<int> ProgressAsync(int characterId, Game.Server.Configuration.PlantOptions plant)
    {
        var targets = plant.AlternativeUnlockTargetCodes.Prepend(plant.UnlockTargetCode).ToList();
        return await db.CharacterBattleMilestones.Where(m => m.CharacterId == characterId && m.Kind == plant.UnlockKind && targets.Contains(m.TargetCode)).Select(m => (int?)m.Count).MaxAsync() ?? 0;
    }
    public async Task<(PlantingOverviewResponse? Response, string? Error)> PlantAsync(string? token, PlantGardenRequest request)
    {
        var (_, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error != null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        if (!Guid.TryParse(request.RequestId, out var guid) || guid == Guid.Empty || !ValidPlots(request.Plots)) return (null, "InvalidRequest");
        var plant = catalog.FindPlant(request.PlantCode);
        if (plant == null) return (null, "PlantNotFound");
        var requestId = guid.ToString("N");
        var fingerprint = plant.Code + ":" + string.Join(",", request.Plots.OrderBy(p => p.PlotIndex).Select(p => $"{p.PlotIndex}:{p.ExpectedVersion}"));
        await EnsurePlotsAsync(character.Id);
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var previous = await db.LogisticsRequests.FindAsync(character.Id, requestId);
            if (previous != null) return previous.Kind == "Plant" && previous.Fingerprint == fingerprint ? (await OverviewAsync(character), null) : (null, "RequestIdConflict");
            if (!plant.IsRare && (character.Level < plant.MinimumCharacterLevel || await ProgressAsync(character.Id, plant) < plant.RequiredCount)) return (null, "PlantLocked");
            var indices = request.Plots.Select(p => p.PlotIndex).ToList();
            var plots = await db.CharacterGardenPlots.Where(p => p.CharacterId == character.Id && indices.Contains(p.PlotIndex)).ToListAsync();
            if (plots.Count != request.Plots.Count || plots.Any(p => p.Version != request.Plots.Single(r => r.PlotIndex == p.PlotIndex).ExpectedVersion)) return (null, "StalePlot");
            if (plots.Any(p => p.PlantCode != null)) return (null, "PlotOccupied");
            var seeds = await db.CharacterItemStacks.SingleOrDefaultAsync(s => s.CharacterId == character.Id && s.ItemCode == plant.SeedCode);
            if (seeds == null || seeds.Quantity < plots.Count) return (null, "NotEnoughSeeds");
            seeds.Quantity -= plots.Count; seeds.Version++;
            var now = DateTime.UtcNow;
            foreach (var plot in plots)
            {
                plot.PlantCode = plant.Code; plot.PlantName = plant.Name; plot.SeedCode = plant.SeedCode; plot.MaterialCode = plant.MaterialCode;
                plot.HarvestQuantity = plant.HarvestQuantity; plot.GrowthSeconds = plant.GrowthSeconds; plot.PlantedAtUtc = now; plot.MaturesAtUtc = now.AddSeconds(plant.GrowthSeconds); plot.Version++;
            }
            db.LogisticsRequests.Add(new() { CharacterId = character.Id, RequestId = requestId, Kind = "Plant", Fingerprint = fingerprint, CompletedAtUtc = now });
            await db.SaveChangesAsync(); await transaction.CommitAsync();
            return (await OverviewAsync(character), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception)) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict"); }
    }
    public async Task<(PlantingOverviewResponse? Response, string? Error)> HarvestAsync(string? token, HarvestGardenRequest request)
    {
        var (_, character, error) = await users.GetCurrentUserAndActiveCharacterAsync(token);
        if (error != null) return (null, error);
        if (character!.Id != request.CharacterId) return (null, "ActiveCharacterChanged");
        if (!ValidPlots(request.Plots)) return (null, "InvalidRequest");
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var indices = request.Plots.Select(p => p.PlotIndex).ToList();
            var plots = await db.CharacterGardenPlots.Where(p => p.CharacterId == character.Id && indices.Contains(p.PlotIndex)).ToListAsync();
            if (plots.Count != request.Plots.Count || plots.Any(p => p.Version != request.Plots.Single(r => r.PlotIndex == p.PlotIndex).ExpectedVersion)) return (null, "StalePlot");
            var now = DateTime.UtcNow;
            if (plots.Any(p => p.PlantCode == null || p.MaturesAtUtc == null || p.MaturesAtUtc > now)) return (null, "PlantNotMature");
            if (production != null) await production.SettleCharacterTrackedAsync(character.Id, now);
            foreach (var group in plots.GroupBy(p => p.MaterialCode!))
            {
                var quantity = group.Sum(p => (long)p.HarvestQuantity);
                var stack = db.CharacterItemStacks.Local.FirstOrDefault(s => s.CharacterId == character.Id && s.ItemCode == group.Key) ?? await db.CharacterItemStacks.SingleOrDefaultAsync(s => s.CharacterId == character.Id && s.ItemCode == group.Key);
                if (quantity > int.MaxValue - (stack?.Quantity ?? 0)) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "InventoryFull"); }
                if (stack == null) db.CharacterItemStacks.Add(new() { CharacterId = character.Id, ItemCode = group.Key, Quantity = (int)quantity });
                else { stack.Quantity += (int)quantity; stack.Version++; }
            }
            foreach (var plot in plots)
            {
                plot.PlantCode = plot.PlantName = plot.SeedCode = plot.MaterialCode = null; plot.HarvestQuantity = plot.GrowthSeconds = 0;
                plot.PlantedAtUtc = plot.MaturesAtUtc = null; plot.Version++;
            }
            await db.SaveChangesAsync(); await transaction.CommitAsync(); return (await OverviewAsync(character), null);
        }
        catch (DbUpdateException exception) when (DatabaseWriteErrors.IsConflict(exception)) { await transaction.RollbackAsync(); db.ChangeTracker.Clear(); return (null, "ConcurrencyConflict"); }
    }
    private static bool ValidPlots(List<PlotVersionRequest>? plots) => plots is { Count: >= 1 and <= 4 } && plots.All(p => p != null && p.PlotIndex is >= 0 and < 4 && p.ExpectedVersion >= 0) && plots.Select(p => p.PlotIndex).Distinct().Count() == plots.Count;
    private async Task<PlantingOverviewResponse> OverviewAsync(Character character)
    {
        var plots = await db.CharacterGardenPlots.AsNoTracking().Where(p => p.CharacterId == character.Id).OrderBy(p => p.PlotIndex).ToListAsync();
        var seeds = await db.CharacterItemStacks.AsNoTracking().Where(s => s.CharacterId == character.Id).ToDictionaryAsync(s => s.ItemCode, s => s.Quantity);
        var plants = new List<PlantDto>();
        foreach (var plant in catalog.Plants)
        {
            var progress = await ProgressAsync(character.Id, plant);
            plants.Add(new(plant.Code, plant.Name, plant.SeedCode, plant.MaterialCode, plant.RegionCode, plant.IsRare, plant.GrowthSeconds, plant.HarvestQuantity, plant.SeedPrice, seeds.GetValueOrDefault(plant.SeedCode), plant.IsRare || character.Level >= plant.MinimumCharacterLevel && progress >= plant.RequiredCount, plant.UnlockKind, plant.UnlockTargetCode, plant.RequiredCount, progress, string.Join(" / ", plant.AlternativeUnlockTargetCodes.Prepend(plant.UnlockTargetCode).Select(code => world?.Dungeons.FirstOrDefault(d => d.Code == code)?.Name ?? "对应战斗地点"))));
        }
        return new(character.Id, character.Name, DateTime.UtcNow, plots.Select(p => new GardenPlotDto(p.PlotIndex, p.Version, p.PlantCode, p.PlantName, p.MaterialCode, p.HarvestQuantity, p.PlantedAtUtc, p.MaturesAtUtc)).ToList(), plants);
    }
}


