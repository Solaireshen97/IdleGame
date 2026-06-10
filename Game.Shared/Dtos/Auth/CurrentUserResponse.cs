namespace Game.Shared.Dtos.Auth;

public class CurrentUserResponse
{
    public int UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
}
