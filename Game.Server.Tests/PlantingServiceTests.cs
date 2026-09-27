using Game.Server.Configuration;
using Game.Server.Data;
using Game.Server.Services;
using Game.Shared.Dtos.Planting;
using Game.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;
namespace Game.Server.Tests;
public sealed class PlantingServiceTests
{
    [Fact] public async Task OfflineHarvestIsAtomicAndOldVersionCannotHarvestNextCrop()
    {
        await using var test = await Context.CreateAsync();
        var view = await test.Service.GetAsync("token"); Assert.Equal(4, view.Response!.Plots.Count);
        var request = new PlantGardenRequest(1, Guid.NewGuid().ToString(), "herb", [new(0, 0), new(1, 0)]);
        Assert.Null((await test.Service.PlantAsync("token", request)).Error);
        Assert.Null((await test.Service.PlantAsync("token", request with { RequestId = Guid.Parse(request.RequestId).ToString("N") })).Error);
        Assert.Equal(2, (await test.Db.CharacterItemStacks.SingleAsync(s => s.ItemCode == "seed-herb")).Quantity);
        Assert.Equal("RequestIdConflict", (await test.Service.PlantAsync("token", request with { Plots = [new(2, 0)] })).Error);
        Assert.Equal("PlantNotMature", (await test.Service.HarvestAsync("token", new(1, [new(0,1)]))).Error);
        foreach(var plot in await test.Db.CharacterGardenPlots.Where(p=>p.PlantCode!=null).ToListAsync()) plot.MaturesAtUtc = DateTime.UtcNow.AddHours(-12);
        await test.Db.SaveChangesAsync();
        var harvest = new HarvestGardenRequest(1,[new(0,1),new(1,1)]);
        Assert.Null((await test.Service.HarvestAsync("token",harvest)).Error);
        Assert.Equal(180,(await test.Db.CharacterItemStacks.SingleAsync(s=>s.ItemCode=="herb")).Quantity);
        Assert.Equal("StalePlot",(await test.Service.HarvestAsync("token",harvest)).Error);
        Assert.Null((await test.Service.PlantAsync("token",new(1,Guid.NewGuid().ToString(),"herb",[new(0,2)]))).Error);
        Assert.Equal("StalePlot",(await test.Service.HarvestAsync("token",harvest)).Error);
    }
    [Fact] public async Task BatchValidationKeepsSeedsAndPlotsUnchangedAndChecksActiveCharacter()
    {
        await using var test = await Context.CreateAsync(); await test.Service.GetAsync("token");
        Assert.Equal("ActiveCharacterChanged",(await test.Service.PlantAsync("token",new(2,Guid.NewGuid().ToString(),"herb",[new(0,0)]))).Error);
        Assert.Equal("InvalidRequest",(await test.Service.PlantAsync("token",new(1,Guid.NewGuid().ToString(),"herb",[new(0,0),new(0,0)]))).Error);
        Assert.Equal("StalePlot",(await test.Service.PlantAsync("token",new(1,Guid.NewGuid().ToString(),"herb",[new(0,0),new(1,9)]))).Error);
        Assert.Equal(4,(await test.Db.CharacterItemStacks.SingleAsync()).Quantity);
        Assert.All(await test.Db.CharacterGardenPlots.ToListAsync(),p=>Assert.Null(p.PlantCode));
    }
    [Fact] public async Task BattleActivityDoesNotBlockPlantingAndRareSeedsNeedNoProfessionOrMilestone()
    {
        await using var test = await Context.CreateAsync(); await test.Service.GetAsync("token");
        test.Db.CharacterActivities.Add(new CharacterActivity { CharacterId=1, Kind="Battle", SourceId=7 });
        test.Db.CharacterItemStacks.Add(new(){CharacterId=1,ItemCode="seed-rare",Quantity=1}); await test.Db.SaveChangesAsync();
        Assert.Null((await test.Service.PlantAsync("token",new(1,Guid.NewGuid().ToString(),"rare",[new(0,0)]))).Error);
        Assert.Single(await test.Db.CharacterActivities.ToListAsync());
        Assert.Equal(1,(await test.Db.Characters.FindAsync(1))!.GatheringLevel);
    }
    private sealed class Context : IAsyncDisposable
    {
        public required GameDbContext Db { get; init; }
        public required PlantingService Service { get; init; }
        public static async Task<Context> CreateAsync()
        {
            var db=new GameDbContext(new DbContextOptionsBuilder<GameDbContext>().UseSqlite("Data Source=:memory:").Options);
            await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
            db.AddRange(new User {Id=1,UserName="owner",PasswordHash="x",ActiveCharacterId=1},new Character {Id=1,UserId=1,Name="First",Level=1},new Character {Id=2,UserId=1,Name="Second"},new UserLoginSession {UserId=1,Token="token",CreatedAt=DateTime.UtcNow,ExpireAt=DateTime.UtcNow.AddDays(1)},new CharacterBattleMilestone {CharacterId=1,Kind="MonsterKill",TargetCode="wolf",Count=1,FirstAtUtc=DateTime.UtcNow,LastAtUtc=DateTime.UtcNow},new CharacterItemStack {CharacterId=1,ItemCode="seed-herb",Quantity=4});
            await db.SaveChangesAsync();
            var catalog=new PlantingCatalog(Options.Create(new PlantingOptions {Plants=[new(){Code="herb",SeedPrice=30,Name="Herb",SeedCode="seed-herb",MaterialCode="herb",GrowthSeconds=14400,HarvestQuantity=90,UnlockTargetCode="wolf"},new(){Code="rare",Name="Rare",SeedCode="seed-rare",MaterialCode="rare",IsRare=true,GrowthSeconds=21600,HarvestQuantity=8,UnlockTargetCode="boss"}]}));
            return new(){Db=db,Service=new(db,new UserService(db,ProgressionTestFactory.Create(),SkillTestFactory.Create()),catalog)};
        }
        public async ValueTask DisposeAsync()=>await Db.DisposeAsync();
    }
}


