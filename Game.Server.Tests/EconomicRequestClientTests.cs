using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Game.Client.Services;
using Game.Shared.Dtos.Characters;
using Game.Shared.Dtos.Shop;
using Xunit;

namespace Game.Server.Tests;

public partial class ApiRequestScopeTests
{
    [Theory]
    [InlineData("enhance")]
    [InlineData("exchange")]
    public async Task UncertainEconomicCommandReusesIdUntilSuccessAndThenStartsANewCommand(string command)
    {
        var ids = new List<string>();
        using var client = Client(async request =>
        {
            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
            ids.Add(body.RootElement.GetProperty("requestId").GetString()!);
            if (ids.Count == 1) throw new HttpRequestException("response was lost after commit");
            return command == "enhance" ? Ok(new CharacterWeaponsResponse { CharacterId = 1 }) :
                Ok(new DungeonExchangeResultResponse { Shop = new ShopResponse { CharacterId = 1 }, RewardDisplayName = "reward" });
        });
        using var api = new ApiService(client, new UserSessionService(new Storage()));
        async Task Send()
        {
            if (command == "enhance") Assert.NotNull((await api.EnhanceWeaponSkillAsync(1, 2, 1)).Response);
            else Assert.NotNull((await api.ExchangeDungeonRewardAsync(1, "offer")).Response);
        }
        await Assert.ThrowsAsync<HttpRequestException>(Send);
        await Send();
        await Send();
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[1], ids[2]);
    }

    [Fact]
    public async Task UncertainEconomicCommandDoesNotCarryItsIdToAnotherAccount()
    {
        var ids = new List<string>();
        var storage = new Storage();
        using var client = Client(async request =>
        {
            using var body = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
            ids.Add(body.RootElement.GetProperty("requestId").GetString()!);
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("DatabaseBusy") };
        });
        using var api = new ApiService(client, new UserSessionService(storage));
        await api.EnhanceWeaponSkillAsync(1, 2, 1);
        await api.EnhanceWeaponSkillAsync(1, 2, 1);
        storage.Token = "different-account";
        await api.EnhanceWeaponSkillAsync(1, 2, 1);
        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[1], ids[2]);
    }
}
