namespace Game.Client.Services;

public sealed class AccountBalanceState
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
