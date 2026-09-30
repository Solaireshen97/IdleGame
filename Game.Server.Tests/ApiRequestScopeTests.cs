using System.Net;
using System.Net.Http.Json;
using Game.Client.Services;
using Game.Shared.Dtos;
using Game.Shared.Dtos.Characters;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class ApiRequestScopeTests
{
    [Theory]
    [InlineData("skill")]
    [InlineData("soul")]
    [InlineData("consumable")]
    [InlineData("assign")]
    [InlineData("remove")]
    [InlineData("leave")]
    [InlineData("auto")]
    [InlineData("prepare")]
    [InlineData("reset")]
    [InlineData("snapshot")]
    [InlineData("detail")]
    [InlineData("rewards")]
    [InlineData("quick")]
    public async Task ExplicitRoomOrCharacterCommandSurvivesSameAccountSelectionChange(string operation)
    {
        var entered = Completion<bool>();
        var pending = Completion<HttpResponseMessage>();
        using var http = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/user/character/select")
                return Task.FromResult(Ok(new CharacterSummaryResponse { CharacterId = 2 }));
            if (request.RequestUri.AbsolutePath == "/api/user/character")
                return Task.FromResult(Ok(new CurrentCharacterResponse { CharacterId = 1 }));
            entered.TrySetResult(true);
            return pending.Task;
        });
        using var api = new ApiService(http, new UserSessionService(new Storage()));
        using var characters = new ActiveCharacterState(api);
        using var balance = new AccountBalanceState(characters, api);
        await characters.EnsureLoadedAsync();
        balance.Update(1, 10);
        var session = api.SessionRevision;
        var selection = api.CharacterSelectionRevision;
        var command = ExecuteAsync(api, operation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var selected = await api.SelectCurrentCharacterAsync(2);
        Assert.NotNull(selected.Response);
        Assert.Equal(session, api.SessionRevision);
        Assert.Equal(selection + 1, api.CharacterSelectionRevision);
        pending.SetResult(OperationResponse(operation));

        Assert.True(await command);
        // A room response is not a write-back to the globally selected character.
        Assert.Null(characters.Current);
        Assert.Null(balance.Gold);
    }

    [Theory]
    [InlineData("shop")]
    [InlineData("join")]
    [InlineData("create")]
    public async Task ImplicitCurrentCharacterResponseIsRejectedAfterSelectionChanges(string operation)
    {
        var entered = Completion<bool>();
        var pending = Completion<HttpResponseMessage>();
        using var http = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/user/character/select")
                return Task.FromResult(Ok(new CharacterSummaryResponse { CharacterId = 2 }));
            entered.TrySetResult(true);
            return pending.Task;
        });
        using var api = new ApiService(http, new UserSessionService(new Storage()));
        var command = operation switch
        {
            "shop" => ShopSucceeded(api),
            "join" => RoomSucceeded(api.JoinRoomAsync(7, 0)),
            _ => RoomSucceeded(api.CreateRoomAsync(new()))
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await api.SelectCurrentCharacterAsync(2);
        pending.SetResult(Ok(new RoomDetailResponse { RoomId = 7 }));
        Assert.False(await command);
    }

    [Theory]
    [InlineData("skill", false)]
    [InlineData("skill", true)]
    [InlineData("snapshot", false)]
    [InlineData("snapshot", true)]
    [InlineData("rewards", false)]
    [InlineData("rewards", true)]
    public async Task RoomResponseCannotCrossAccountBoundary(string operation, bool unauthorized)
    {
        var entered = Completion<bool>();
        var pending = Completion<HttpResponseMessage>();
        var storage = new Storage();
        var session = new UserSessionService(storage);
        using var http = Client(_ => { entered.TrySetResult(true); return pending.Task; });
        using var api = new ApiService(http, session);
        var command = ExecuteAsync(api, operation);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Simulate a second window logging into a different account.
        storage.Token = "new-account";
        pending.SetResult(unauthorized ? new(HttpStatusCode.Unauthorized) : OperationResponse(operation));

        Assert.False(await command);
        Assert.Equal("new-account", await session.GetToken());
        Assert.Equal(0, storage.Removals);
        Assert.Equal(0, api.CharacterSelectionRevision);
    }

    [Fact]
    public async Task CurrentRoomUnauthorizedStillClearsTheCurrentSession()
    {
        var storage = new Storage();
        using var http = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var api = new ApiService(http, new UserSessionService(storage));
        Assert.False(await ExecuteAsync(api, "skill"));
        Assert.Null(storage.Token);
        Assert.Equal(1, storage.Removals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningToTheSameTokenDoesNotReviveAnOlderSessionResponse(bool unauthorized)
    {
        var pending = Completion<HttpResponseMessage>();
        var entered = Completion<bool>();
        var storage = new Storage();
        var session = new UserSessionService(storage);
        using var http = Client(_ => { entered.TrySetResult(true); return pending.Task; });
        using var api = new ApiService(http, session);
        var old = ExecuteAsync(api, "skill");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.SetToken("other-account");
        await session.SetToken("original-account");
        pending.SetResult(unauthorized ? new(HttpStatusCode.Unauthorized) : OperationResponse("skill"));
        Assert.False(await old);
        Assert.Equal("original-account", await session.GetToken());
        Assert.Equal(0, storage.Removals);
    }

    [Theory]
    [InlineData("select", "ConcurrencyConflict", "角色信息刚刚变化，请刷新后重试。")]
    [InlineData("delete", "ConcurrencyConflict", "角色信息刚刚变化，请刷新后重试。")]
    [InlineData("profession", "UnsupportedSkillLoadoutVersion", "技能配置版本暂不支持，请更新后重试。")]
    public async Task NewLifecycleAndLoadoutConflictsHaveActionableMessages(string operation, string error, string expected)
    {
        using var http = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(error)
        }));
        using var api = new ApiService(http, new UserSessionService(new Storage()));
        var actual = operation switch
        {
            "select" => (await api.SelectCurrentCharacterAsync(2)).ErrorMessage,
            "delete" => (await api.DeleteCharacterAsync(2)).ErrorMessage,
            _ => (await api.SwitchCharacterProfessionAsync(1, "knight")).ErrorMessage
        };
        Assert.Equal(expected, actual);
    }

    private static async Task<bool> ExecuteAsync(ApiService api, string operation) => operation switch
    {
        "skill" => (await api.QueueSkillAsync(7, 1, 0, true, 3)).Detail is not null,
        "soul" => (await api.QueueSoulImprintAsync(7, 1, true)).Detail is not null,
        "consumable" => (await api.QueueConsumableAsync(7, 1, 0, true, 1, 1)).Detail is not null,
        "assign" => (await api.AssignRoomSlotAsync(7, 0, 1)).Detail is not null,
        "remove" => (await api.RemoveRoomSlotAsync(7, 0)).Detail is not null,
        "leave" => (await api.LeaveRoomAsync(7)).Detail is not null,
        "auto" => (await api.SetSlotAutoAsync(7, 0, true)).Response is not null,
        "prepare" => (await api.StartPreparationAsync(7, 1, 1)).Result is not null,
        "reset" => (await api.ResetBattleAsync(7)).Detail is not null,
        "snapshot" => (await api.LoadBattleSnapshotAsync(new() { RoomId = 7 })).Status == RoomLoadStatus.Success,
        "detail" => (await api.LoadRoomDetailAsync(7)).Status == RoomLoadStatus.Success,
        "rewards" => await api.GetRoomRewardsAsync(7) is not null,
        "quick" => await api.SetQuickSkillCastAsync(1, true) is null,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static HttpResponseMessage OperationResponse(string operation) => operation switch
    {
        "snapshot" => Ok(new BattleSyncResponse { Room = new() { RoomId = 7 } }),
        "rewards" => Ok(new RoomRewardsResponse { RoomId = 7 }),
        "auto" => Ok(new SetSlotAutoResponse { Room = new() { RoomId = 7 } }),
        "prepare" => Ok(new BattleResult { RoomId = 7 }),
        "quick" => new(HttpStatusCode.NoContent),
        _ => Ok(new RoomDetailResponse { RoomId = 7 })
    };

    private static async Task<bool> ShopSucceeded(ApiService api) => await api.GetShopAsync() is not null;
    private static async Task<bool> RoomSucceeded(Task<(RoomDetailResponse? Detail, string? ErrorMessage)> read) =>
        (await read).Detail is not null;
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new("http://localhost/") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Storage : IJSRuntime
    {
        public string? Token { get; set; } = "original-account";
        public int Removals { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem") return ValueTask.FromResult((T)(object?)Token!);
            if (identifier == "localStorage.setItem") Token = (string)args![1]!;
            if (identifier == "localStorage.removeItem") { Token = null; Removals++; }
            return ValueTask.FromResult(default(T)!);
        }
    }
}
