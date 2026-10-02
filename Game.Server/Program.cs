using Game.Server.Data;
using Game.Server.Services;
using Game.Server.Configuration;
using Game.Server.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "IdleGame");
builder.Configuration.AddJsonFile("world.json", optional: false, reloadOnChange: false);

builder.Services.AddControllers();
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var gameDbConnectionString = builder.Configuration.GetConnectionString("GameDb")
    ?? throw new InvalidOperationException("Connection string 'GameDb' is not configured.");
var gameDbConnection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(gameDbConnectionString);
if (!Path.IsPathRooted(gameDbConnection.DataSource))
{
    gameDbConnection.DataSource = Path.Combine(builder.Environment.ContentRootPath, gameDbConnection.DataSource);
}

builder.Services.AddSingleton<RoomProjectionRevision>();
builder.Services.AddSingleton<RoomProjectionCache>();
builder.Services.AddSingleton<RoomProjectionInvalidation>();
builder.Services.AddSingleton<RoomProjectionTransactionInvalidation>();
builder.Services.AddDbContext<GameDbContext>((services, options) =>
    options.UseSqlite(gameDbConnection.ToString()).AddInterceptors(
        services.GetRequiredService<RoomProjectionInvalidation>(),
        services.GetRequiredService<RoomProjectionTransactionInvalidation>()));

builder.Services.AddBattleServices();
builder.Services.AddScoped<RareSeedService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ConsumableService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddScoped<CombatProfessionService>();
builder.Services.AddFormationServices();
builder.Services.Configure<FormationOptions>(builder.Configuration.GetSection("Formations"));
builder.Services.AddScoped<WeaponService>();
builder.Services.AddScoped<SoulImprintService>();
builder.Services.AddScoped<ShopService>();
builder.Services.AddScoped<PlantingService>();
builder.Services.AddScoped<ProductionService>();
builder.Services.Configure<SessionCleanupOptions>(builder.Configuration.GetSection(SessionCleanupOptions.SectionName));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<BackgroundCycleHealth>();
builder.Services.Configure<ProgressionOptions>(builder.Configuration.GetSection(ProgressionOptions.SectionName));
builder.Services.Configure<ConsumableOptions>(builder.Configuration.GetSection(ConsumableOptions.SectionName));
builder.Services.Configure<SkillOptions>(builder.Configuration.GetSection(SkillOptions.SectionName));
builder.Services.Configure<WeaponOptions>(builder.Configuration.GetSection(WeaponOptions.SectionName));
builder.Services.Configure<SoulImprintOptions>(builder.Configuration.GetSection(SoulImprintOptions.SectionName));
builder.Services.Configure<RewardOptions>(builder.Configuration.GetSection(RewardOptions.SectionName));
builder.Services.AddOptions<CombatDamageOptions>()
    .Bind(builder.Configuration.GetSection(CombatDamageOptions.SectionName))
    .Validate(options => options.VariancePercent is >= 0 and <= 100, "Damage variance must be between 0 and 100 percent.")
    .ValidateOnStart();
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
builder.Services.AddSingleton<BattleStatusCatalog>();
builder.Services.AddSingleton(_ => ProfessionMechanicCatalog.Default);
builder.Services.AddSingleton<SkillInformationService>();
builder.Services.AddSingleton<ShopCatalog>();
builder.Services.AddSingleton<MaterialCatalog>();
builder.Services.AddSingleton<PlantingCatalog>();
builder.Services.AddSingleton<ProductionCatalog>();
builder.Services.AddSingleton<DungeonExchangeCatalog>();
builder.Services.AddSingleton<WorldCatalog>();
builder.Services.AddSingleton<DungeonContentValidator>();
builder.Services.AddSingleton<ContentCatalogStore>();
builder.Services.AddSingleton<CharacterSlotCatalog>();
builder.Services.AddSingleton<BattleLogStore>();
builder.Services.AddHostedService<RoomCycleService>();
builder.Services.AddHostedService<ProductionCycleService>();
builder.Services.AddHostedService<SessionCleanupService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowClient", policy =>
    {
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseExceptionHandler();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ProfessionMechanicCatalog>().Validate(
        scope.ServiceProvider.GetRequiredService<SkillCatalog>(), scope.ServiceProvider.GetRequiredService<BattleStatusCatalog>());
    var dbContext = scope.ServiceProvider.GetRequiredService<GameDbContext>();
    var world = scope.ServiceProvider.GetRequiredService<WorldCatalog>();
    var weapons = scope.ServiceProvider.GetRequiredService<WeaponCatalog>();
    _ = scope.ServiceProvider.GetRequiredService<SoulImprintCatalog>();
    var encounters = scope.ServiceProvider.GetRequiredService<DungeonEncounterCatalog>();
    scope.ServiceProvider.GetRequiredService<DungeonContentValidator>().Validate();
    _ = scope.ServiceProvider.GetRequiredService<PlantingCatalog>();
    _ = scope.ServiceProvider.GetRequiredService<ProductionCatalog>();
    await DbInitializer.InitializeAsync(dbContext, weapons, world, encounters);
    await scope.ServiceProvider.GetRequiredService<FormationBackfillService>().BackfillAsync();
    // Finish the legacy cutover before background cycles or HTTP requests can claim a snapshot.
    while (true)
    {
        using var freezeScope = app.Services.CreateScope();
        var freezeDb = freezeScope.ServiceProvider.GetRequiredService<GameDbContext>();
        var rooms = await freezeDb.Rooms.Where(room =>
                !freezeDb.DungeonRunRuleSnapshots.Any(snapshot => snapshot.RoomId == room.Id))
            .OrderBy(room => room.Id).Take(100).ToListAsync();
        if (rooms.Count == 0) break;
        var rules = freezeScope.ServiceProvider.GetRequiredService<DungeonRunRulesService>();
        foreach (var room in rooms) await rules.EnsureAsync(room);
        await freezeDb.SaveChangesAsync();
    }
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
app.MapServerHealth();
app.MapFallbackToFile("index.html");

app.Run();
