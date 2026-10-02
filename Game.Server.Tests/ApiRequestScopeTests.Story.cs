using System.Net;
using Game.Client.Services;
using Game.Shared.Dtos.Story;
using Xunit;

namespace Game.Server.Tests;

public sealed partial class ApiRequestScopeTests
{
    [Theory]
    [InlineData(409, "ConcurrencyConflict", true)]
    [InlineData(409, "RequestIdConflict", true)]
    [InlineData(500, "ConcurrencyConflict", false)]
    public async Task StoryRetryResetIsOnlySignalledForDefinitiveConflict(int status, string code, bool definitive)
    {
        using var http = Client(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(code) }));
        using var api = new ApiService(http, new UserSessionService(new Storage()));
        var result = await api.TurnInStoryAsync("quest", new() { RequestId = "same-operation", ExpectedVersion = 3 });
        Assert.Null(result.Response);
        Assert.Equal(definitive, result.ErrorMessage is ApiService.StoryVersionChangedError or ApiService.StoryRequestConflictError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoryResponseCannotCrossAccountBoundary(bool mutation)
    {
        var entered = Completion<bool>();
        var pending = Completion<HttpResponseMessage>();
        var storage = new Storage();
        using var http = Client(_ => { entered.TrySetResult(true); return pending.Task; });
        using var api = new ApiService(http, new UserSessionService(storage));
        async Task<StoryOverviewResponse?> Read() => mutation
            ? (await api.TurnInStoryAsync("quest", new() { RequestId = "same-operation", ExpectedVersion = 3 })).Response
            : await api.GetStoryAsync();
        var command = Read();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        storage.Token = "new-account";
        pending.SetResult(Ok(new StoryOverviewResponse { UserId = 1, Version = 4 }));
        Assert.Null(await command);
    }
}
