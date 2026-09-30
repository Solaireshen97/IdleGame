using Microsoft.JSInterop;

namespace Game.Client.Services;

public class UserSessionService(IJSRuntime jsRuntime)
{
    private const string TokenStorageKey = "authToken";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    public long Revision { get; private set; }
    public event Action? Changed;

    private void Observe(string? token)
    {
        if (string.Equals(_token, token, StringComparison.Ordinal)) return;
        _token = token;
        Revision++;
        Changed?.Invoke();
    }

    public async ValueTask SetToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            await ClearToken();
            return;
        }

        await _gate.WaitAsync();
        try
        {
            await jsRuntime.InvokeVoidAsync("localStorage.setItem", TokenStorageKey, token);
            Observe(token);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<string?> GetToken()
    {
        await _gate.WaitAsync();
        try
        {
            var token = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", TokenStorageKey);
            Observe(string.IsNullOrWhiteSpace(token) ? null : token);
            return _token;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ClearToken()
    {
        await _gate.WaitAsync();
        try
        {
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", TokenStorageKey);
            Observe(null);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ClearTokenIfCurrent(string? expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)) return;
        await _gate.WaitAsync();
        try
        {
            var stored = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", TokenStorageKey);
            if (!string.Equals(stored, expectedToken, StringComparison.Ordinal)) { Observe(stored); return; }
            await jsRuntime.InvokeVoidAsync("localStorage.removeItem", TokenStorageKey);
            Observe(null);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<bool> IsLoggedIn()
    {
        return !string.IsNullOrWhiteSpace(await GetToken());
    }
}
