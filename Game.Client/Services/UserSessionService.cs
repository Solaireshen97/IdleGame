using Microsoft.JSInterop;

namespace Game.Client.Services;

public class UserSessionService(IJSRuntime jsRuntime)
{
    private const string TokenStorageKey = "authToken";

    public async ValueTask SetToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await ClearToken();
            return;
        }

        await jsRuntime.InvokeVoidAsync("localStorage.setItem", TokenStorageKey, token);
    }

    public async ValueTask<string?> GetToken()
    {
        var token = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", TokenStorageKey);
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public async ValueTask ClearToken()
    {
        await jsRuntime.InvokeVoidAsync("localStorage.removeItem", TokenStorageKey);
    }

    public async ValueTask<bool> IsLoggedIn()
    {
        return !string.IsNullOrWhiteSpace(await GetToken());
    }
}
