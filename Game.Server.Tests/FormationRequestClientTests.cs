using System.Net.Http.Json;
using System.Text.Json;
using Game.Client.Services;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Formations;
using Game.Shared.Enums;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class ApiRequestScopeTests
{
    [Theory]
    [InlineData("join")]
    [InlineData("assign")]
    [InlineData("create")]
    public async Task FormationAdmissionCarriesConfirmedCharacterVersionAndStableRequestId(string action)
    {
        var bodies = new List<JsonElement>();
        using var client = Client(async request =>
        {
            using var json = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
            bodies.Add(json.RootElement.Clone());
            if (bodies.Count == 1) throw new HttpRequestException("lost admission response");
            return Ok(new RoomDetailResponse { RoomId = 5 });
        });
        using var api = new ApiService(client, new UserSessionService(new Storage()));
        var selection = new LoadoutSelection { Mode = "SavedFormation", FormationId = 17, ExpectedVersion = 3, RememberForEncounter = false };
        async Task Send()
        {
            var result = action switch
            {
                "join" => await api.JoinRoomAsync(5, 2, 9, selection, "same-request"),
                "assign" => await api.AssignRoomSlotAsync(5, 2, 9, selection, "same-request"),
                _ => await api.CreateRoomAsync(new CreateRoomRequest { DungeonId = 4 }, 9, selection, "same-request")
            };
            Assert.NotNull(result.Detail);
        }
        await Assert.ThrowsAsync<HttpRequestException>(Send);
        await Send();
        Assert.Equal(2, bodies.Count);
        foreach (var body in bodies)
        {
            Assert.Equal(9, body.GetProperty("characterId").GetInt32());
            Assert.Equal("same-request", body.GetProperty("requestId").GetString());
            var choice = body.GetProperty("loadoutSelection");
            Assert.Equal("SavedFormation", choice.GetProperty("mode").GetString());
            Assert.Equal(17, choice.GetProperty("formationId").GetInt32());
            Assert.Equal(3, choice.GetProperty("expectedVersion").GetInt32());
            Assert.False(choice.GetProperty("rememberForEncounter").GetBoolean());
        }
    }

    [Fact]
    public async Task FormationDraftSaveDoesNotUseImmediateEquipmentEndpoints()
    {
        string? path = null;
        JsonElement body = default;
        using var client = Client(async request =>
        {
            path = request.RequestUri!.AbsolutePath;
            using var json = await JsonDocument.ParseAsync(await request.Content!.ReadAsStreamAsync());
            body = json.RootElement.Clone();
            return Ok(new FormationResponse { Id = 7, CharacterId = 9, Version = 4 });
        });
        using var api = new ApiService(client, new UserSessionService(new Storage()));
        var draft = new SaveFormationRequest { ExpectedVersion = 3, GroupElement = ElementType.Water, Name = "水队", Loadout = new() { ProfessionCode = "mage", Consumables = [new() { SlotIndex = 3, ItemCode = "mixture" }] } };
        Assert.NotNull((await api.SaveFormationAsync(9, 7, draft)).Response);
        Assert.Equal("/api/user/characters/9/formations/7", path);
        Assert.Equal(3, body.GetProperty("expectedVersion").GetInt32());
        Assert.Equal(3, body.GetProperty("loadout").GetProperty("consumables")[0].GetProperty("slotIndex").GetInt32());
    }
}
