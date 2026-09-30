using System.Net;
using System.Text.Json;
using Game.Server.Data;
using Game.Server.Infrastructure;
using Game.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Game.Server.Tests;

public sealed class ServerHealthEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HealthRoutesBypassFallbackAndDatabaseFailureIsSanitized(bool createSchema)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<GameDbContext>(options => options.UseSqlite(connection));
        var health = new BackgroundCycleHealth();
        health.CompleteScan(BackgroundCycleHealth.Rooms, 0, 0);
        health.CompleteScan(BackgroundCycleHealth.Production, 0, 0);
        builder.Services.AddSingleton(health);
        await using var app = builder.Build();
        if (createSchema)
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<GameDbContext>().Database.EnsureCreatedAsync();
        }
        app.MapServerHealth();
        app.MapFallback(() => "spa fallback");
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses;
            using var client = new HttpClient { BaseAddress = new Uri(Assert.Single(addresses)) };
            Assert.Equal("spa fallback", await client.GetStringAsync("/unmapped-client-route"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/liveness")).StatusCode);
            foreach (var route in new[] { "/readiness", "/health" })
            {
                using var response = await client.GetAsync(route);
                Assert.Equal(createSchema ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                    response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
                Assert.True(response.Headers.CacheControl!.NoStore);
                var body = await response.Content.ReadAsStringAsync();
                using var payload = JsonDocument.Parse(body);
                Assert.Equal(createSchema ? "Healthy" : "Unhealthy",
                    payload.RootElement.GetProperty("database").GetString());
                Assert.DoesNotContain("SQLite", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("no such table", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
            }

            if (createSchema)
            {
                health.CompleteScan(BackgroundCycleHealth.Rooms, 3, 1);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/readiness")).StatusCode);
                health.CompleteScan(BackgroundCycleHealth.Rooms, 0, 0);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/readiness")).StatusCode);
            }
        }
        finally { await app.StopAsync(); }
    }
}
