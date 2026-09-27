using Game.Server.Data;
using Game.Server.Services;
using Game.Server.Configuration;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "IdleGame");
builder.Configuration.AddJsonFile("world.json", optional: false, reloadOnChange: false);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var gameDbConnectionString = builder.Configuration.GetConnectionString("GameDb")
    ?? throw new InvalidOperationException("Connection string 'GameDb' is not configured.");
var gameDbConnection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(gameDbConnectionString);
if (!Path.IsPathRooted(gameDbConnection.DataSource))
{
    gameDbConnection.DataSource = Path.Combine(builder.Environment.ContentRootPath, gameDbConnection.DataSource);
}

builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseSqlite(gameDbConnection.ToString()));

builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<BattleService>();
builder.Services.AddScoped<DungeonRunService>();
builder.Services.AddScoped<DungeonDepthProgressService>();
builder.Services.AddScoped<PartyScalingService>();
builder.Services.AddScoped<MonsterCombatService>();
builder.Services.AddScoped<RewardService>();
builder.Services.AddScoped<BattleMilestoneService>();
builder.Services.AddScoped<RareSeedService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<TalentService>();
builder.Services.AddScoped<ConsumableService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddScoped<WeaponService>();
builder.Services.AddScoped<SoulImprintService>();
builder.Services.AddScoped<ShopService>();
builder.Services.AddScoped<PlantingService>();
builder.Services.AddScoped<ProductionService>();
builder.Services.Configure<ProgressionOptions>(builder.Configuration.GetSection(ProgressionOptions.SectionName));
builder.Services.Configure<ConsumableOptions>(builder.Configuration.GetSection(ConsumableOptions.SectionName));
builder.Services.Configure<SkillOptions>(builder.Configuration.GetSection(SkillOptions.SectionName));
builder.Services.Configure<WeaponOptions>(builder.Configuration.GetSection(WeaponOptions.SectionName));
builder.Services.Configure<SoulImprintOptions>(builder.Configuration.GetSection(SoulImprintOptions.SectionName));
builder.Services.Configure<RewardOptions>(builder.Configuration.GetSection(RewardOptions.SectionName));
builder.Services.Configure<DungeonEncounterOptions>(builder.Configuration.GetSection(DungeonEncounterOptions.SectionName));
builder.Services.Configure<DungeonDepthOptions>(builder.Configuration.GetSection(DungeonDepthOptions.SectionName));
builder.Services.Configure<WeaponBreakthroughOptions>(builder.Configuration.GetSection(WeaponBreakthroughOptions.SectionName));
builder.Services.Configure<PartyScalingOptions>(builder.Configuration.GetSection(PartyScalingOptions.SectionName));
builder.Services.Configure<MonsterCombatOptions>(builder.Configuration.GetSection(MonsterCombatOptions.SectionName));
builder.Services.Configure<ShopOptions>(builder.Configuration.GetSection(ShopOptions.SectionName));
builder.Services.Configure<MaterialOptions>(builder.Configuration.GetSection(MaterialOptions.SectionName));
builder.Services.Configure<DungeonExchangeOptions>(builder.Configuration.GetSection(DungeonExchangeOptions.SectionName));
builder.Services.Configure<WorldOptions>(builder.Configuration.GetSection(WorldOptions.SectionName));
builder.Services.Configure<CharacterSlotOptions>(builder.Configuration.GetSection(CharacterSlotOptions.SectionName));
builder.Services.Configure<ActivityOptions>(builder.Configuration.GetSection(ActivityOptions.SectionName));
builder.Services.Configure<PlantingOptions>(builder.Configuration.GetSection(PlantingOptions.SectionName));
builder.Services.Configure<ProductionOptions>(builder.Configuration.GetSection(ProductionOptions.SectionName));
builder.Services.AddSingleton<ProgressionService>();
builder.Services.AddSingleton<ConsumableCatalog>();
builder.Services.AddSingleton<SkillCatalog>();
builder.Services.AddSingleton<WeaponCatalog>();
builder.Services.AddSingleton<SoulImprintCatalog>();
builder.Services.AddSingleton<RewardCatalog>();
builder.Services.AddSingleton<DungeonEncounterCatalog>();
builder.Services.AddSingleton<DungeonDepthCatalog>();
builder.Services.AddSingleton<WeaponBreakthroughCatalog>();
builder.Services.AddSingleton<PartyScalingCatalog>();
builder.Services.AddSingleton<MonsterCombatCatalog>();
builder.Services.AddSingleton<ShopCatalog>();
builder.Services.AddSingleton<MaterialCatalog>();
builder.Services.AddSingleton<PlantingCatalog>();
builder.Services.AddSingleton<ProductionCatalog>();
builder.Services.AddSingleton<DungeonExchangeCatalog>();
builder.Services.AddSingleton<WorldCatalog>();
builder.Services.AddSingleton<CharacterSlotCatalog>();
builder.Services.AddSingleton<BattleLogStore>();
builder.Services.AddHostedService<RoomCycleService>();
builder.Services.AddHostedService<ProductionCycleService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowClient", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<GameDbContext>();
    var world = scope.ServiceProvider.GetRequiredService<WorldCatalog>();
    var weapons = scope.ServiceProvider.GetRequiredService<WeaponCatalog>();
    _ = scope.ServiceProvider.GetRequiredService<SoulImprintCatalog>();
    var encounters = scope.ServiceProvider.GetRequiredService<DungeonEncounterCatalog>();
    world.ValidateContent(weapons, encounters,
        scope.ServiceProvider.GetRequiredService<RewardCatalog>(), scope.ServiceProvider.GetRequiredService<DungeonExchangeCatalog>());
    var depthCatalog = scope.ServiceProvider.GetRequiredService<DungeonDepthCatalog>();
    var materials = scope.ServiceProvider.GetRequiredService<MaterialCatalog>();
    foreach (var dungeon in world.Dungeons)
    {
        if (depthCatalog.Find(dungeon.Code) is not { } depth) continue;
        depthCatalog.ValidateStats(dungeon.Code, dungeon.MonsterMaxHp, dungeon.MonsterAttack, dungeon.MonsterName);
        if (materials.FindItem(depth.ChallengeFragmentCode) is null)
            throw new InvalidOperationException($"Unknown challenge fragment for {dungeon.Code}: {depth.ChallengeFragmentCode}");
    }
    foreach (var recipe in scope.ServiceProvider.GetRequiredService<WeaponBreakthroughCatalog>().Recipes)
        if (materials.FindItem(recipe.FragmentCode) is null || materials.FindItem(recipe.StoneCode) is null)
            throw new InvalidOperationException($"Unknown breakthrough materials for T{recipe.Tier}");
    _ = scope.ServiceProvider.GetRequiredService<PlantingCatalog>();
    _ = scope.ServiceProvider.GetRequiredService<ProductionCatalog>();
    await DbInitializer.InitializeAsync(dbContext, weapons, world, encounters);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowClient");
app.UseDefaultFiles();
var staticFileContentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
staticFileContentTypes.Mappings[".dat"] = "application/octet-stream";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = staticFileContentTypes
});
app.UseAuthorization();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
