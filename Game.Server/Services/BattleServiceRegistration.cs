namespace Game.Server.Services;

/// <summary>Shared scoped graph for HTTP requests, background room cycles and integration tests.</summary>
public static class BattleServiceRegistration
{
    public static IServiceCollection AddBattleServices(this IServiceCollection services)
    {
        services.AddScoped<RoomService>();
        services.AddScoped<BattleService>();
        services.AddScoped<BattleContextPreparation>();
        services.AddScoped<BattleSynchronizationService>();
        services.AddScoped<BattleStatisticsQuery>();
        services.AddScoped<DungeonRunService>();
        services.AddScoped<DungeonDepthProgressService>();
        services.AddScoped<DungeonRunRulesService>();
        services.AddScoped<PartyScalingService>();
        services.AddScoped<MonsterCombatService>();
        services.AddScoped<BattleEventCollector>();
        services.AddScoped<BattleStatusService>();
        services.AddScoped<MonsterPhaseService>();
        services.AddScoped<BattleGuardService>();
        services.AddScoped<BattleDamageService>();
        services.AddScoped<BattleEffectExecutor>();
        services.AddScoped<RewardService>();
        services.AddScoped<BattleMilestoneService>();
        return services;
    }

    public static IServiceCollection AddFormationServices(this IServiceCollection services)
    {
        services.AddScoped<CombatLoadoutService>();
        services.AddScoped<BattleLoadoutIntegrityService>();
        services.AddScoped<FormationService>();
        services.AddScoped<FormationBackfillService>();
        return services;
    }
}
