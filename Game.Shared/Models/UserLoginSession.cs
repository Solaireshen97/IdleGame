namespace Game.Shared.Models;

public class UserLoginSession
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpireAt { get; set; }
}
