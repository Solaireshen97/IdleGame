using System.Net;
using System.Net.Http.Json;
using Game.Client.Services;
using Game.Shared.Dtos;
using Microsoft.JSInterop;
using Xunit;

namespace Game.Server.Tests;

public sealed class RoomLoadRecoveryTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound, RoomLoadStatus.Unavailable)]
    [InlineData(HttpStatusCode.Forbidden, RoomLoadStatus.Unavailable)]
    [InlineData(HttpStatusCode.Gone, RoomLoadStatus.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, RoomLoadStatus.RetryableError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, RoomLoadStatus.RetryableError)]
    [InlineData(HttpStatusCode.RequestTimeout, RoomLoadStatus.RetryableError)]
    public async Task MissingRoomsAreDistinguishedFromTemporaryServerFailures(HttpStatusCode status, RoomLoadStatus expected)
    {
        var storage = new SessionStorage();
        using var client = CreateClient((request, _) =>
        {
            Assert.Equal("/api/rooms/123", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("session-token", request.Headers.Authorization.Parameter);
            return Task.FromResult(new HttpResponseMessage(status));
        });
        var api = new ApiService(client, new UserSessionService(storage));

        var result = await api.LoadRoomDetailAsync(123);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Room);
        Assert.Equal("session-token", storage.Token);
    }

    [Fact]
    public async Task ExpiredAuthenticationClearsTheSessionForLoginRecovery()
    {
        var storage = new SessionStorage();
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var api = new ApiService(client, new UserSessionService(storage));

        var result = await api.LoadRoomDetailAsync(123);

        Assert.Equal(RoomLoadStatus.Unauthorized, result.Status);
        Assert.Null(result.Room);
        Assert.Null(storage.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingRoomsAndSettlementRecordsRemainReadable(bool closed)
    {
        var room = new RoomDetailResponse { RoomId = 123, ClosedAtUtc = closed ? DateTime.UtcNow : null };
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(room)
        }));
        var api = new ApiService(client, new UserSessionService(new SessionStorage()));

        var result = await api.LoadRoomDetailAsync(123);

        Assert.Equal(RoomLoadStatus.Success, result.Status);
        Assert.Equal(room.RoomId, result.Room!.RoomId);
        Assert.Equal(room.ClosedAtUtc, result.Room.ClosedAtUtc);
    }

    [Theory]
    [InlineData("null", RoomLoadStatus.RetryableError)]
    [InlineData("invalid response", RoomLoadStatus.RetryableError)]
    [InlineData("{\"roomId\":456}", RoomLoadStatus.Unavailable)]
    public async Task InvalidResponsesCannotLeaveAnUnresolvedRoomLoading(string body, RoomLoadStatus expected)
    {
        using var client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        }));
        var api = new ApiService(client, new UserSessionService(new SessionStorage()));

        var result = await api.LoadRoomDetailAsync(123);

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Room);
    }

    [Fact]
    public async Task ConnectionFailuresExposeRetryWithoutClearingAuthentication()
    {
        var storage = new SessionStorage();
        using var client = CreateClient((_, _) => throw new HttpRequestException("connection lost"));
        var api = new ApiService(client, new UserSessionService(storage));

        var result = await api.LoadRoomDetailAsync(123);

        Assert.Equal(RoomLoadStatus.RetryableError, result.Status);
        Assert.Equal("session-token", storage.Token);
    }

    [Fact]
    public async Task TimedOutRequestsResolveToRetryInsteadOfThrowingIntoThePage()
    {
        using var client = CreateClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        client.Timeout = TimeSpan.FromMilliseconds(20);
        var api = new ApiService(client, new UserSessionService(new SessionStorage()));

        var result = await api.LoadRoomDetailAsync(123);

        Assert.Equal(RoomLoadStatus.RetryableError, result.Status);
        Assert.Null(result.Room);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new ResponseHandler(send)) { BaseAddress = new Uri("http://localhost/") };

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class SessionStorage : IJSRuntime
    {
        public string? Token { get; private set; } = "session-token";

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "localStorage.getItem") return ValueTask.FromResult((TValue)(object?)Token!);
            if (identifier == "localStorage.removeItem") Token = null;
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
