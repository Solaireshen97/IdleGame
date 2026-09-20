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
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<TalentService>();
builder.Services.AddScoped<ConsumableService>();
builder.Services.AddScoped<SkillService>();
builder.Services.Configure<ProgressionOptions>(builder.Configuration.GetSection(ProgressionOptions.SectionName));
builder.Services.Configure<ConsumableOptions>(builder.Configuration.GetSection(ConsumableOptions.SectionName));
builder.Services.Configure<SkillOptions>(builder.Configuration.GetSection(SkillOptions.SectionName));
builder.Services.AddSingleton<ProgressionService>();
builder.Services.AddSingleton<ConsumableCatalog>();
builder.Services.AddSingleton<SkillCatalog>();
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
    await DbInitializer.InitializeAsync(dbContext);
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
