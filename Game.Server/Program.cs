using Game.Server.Data;
using Game.Server.Services;
using Game.Server.Configuration;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<GameDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("GameDb")));

builder.Services.AddScoped<RoomService>();
builder.Services.AddScoped<BattleService>();
builder.Services.AddScoped<RewardService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<TalentService>();
builder.Services.AddScoped<ConsumableService>();
builder.Services.AddScoped<SkillService>();
builder.Services.AddScoped<WeaponService>();
builder.Services.AddScoped<ShopService>();
builder.Services.Configure<ProgressionOptions>(builder.Configuration.GetSection(ProgressionOptions.SectionName));
builder.Services.Configure<ConsumableOptions>(builder.Configuration.GetSection(ConsumableOptions.SectionName));
builder.Services.Configure<SkillOptions>(builder.Configuration.GetSection(SkillOptions.SectionName));
builder.Services.Configure<WeaponOptions>(builder.Configuration.GetSection(WeaponOptions.SectionName));
builder.Services.Configure<RewardOptions>(builder.Configuration.GetSection(RewardOptions.SectionName));
builder.Services.Configure<ShopOptions>(builder.Configuration.GetSection(ShopOptions.SectionName));
builder.Services.AddSingleton<ProgressionService>();
builder.Services.AddSingleton<ConsumableCatalog>();
builder.Services.AddSingleton<SkillCatalog>();
builder.Services.AddSingleton<WeaponCatalog>();
builder.Services.AddSingleton<RewardCatalog>();
builder.Services.AddSingleton<ShopCatalog>();
builder.Services.AddHostedService<RoomCycleService>();

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
    await DbInitializer.InitializeAsync(dbContext, scope.ServiceProvider.GetRequiredService<WeaponCatalog>());
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("AllowClient");
app.UseAuthorization();

app.MapControllers();

app.Run();
