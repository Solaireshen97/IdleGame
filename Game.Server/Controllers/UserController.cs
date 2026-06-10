using Game.Server.Services;
using Game.Shared.Dtos.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Controllers;

[ApiController]
[Route("api/user")]
public class UserController(UserService userService) : ControllerBase
{
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var (response, error) = await userService.RegisterAsync(request);
        return error switch
        {
            null => Ok(response),
            "UserNameRequired" => BadRequest("UserName cannot be empty."),
            "PasswordRequired" => BadRequest("Password cannot be empty."),
            "DuplicateUserName" => BadRequest("UserName already exists."),
            _ => BadRequest("Registration failed.")
        };
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest? request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        var (response, error) = await userService.LoginAsync(request);
        return error switch
        {
            null => Ok(response),
            "UserNameRequired" => BadRequest("UserName cannot be empty."),
            "PasswordRequired" => BadRequest("Password cannot be empty."),
            "InvalidCredentials" => Unauthorized("Invalid user name or password."),
            _ => BadRequest("Login failed.")
        };
    }

    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        var (response, error) = await userService.GetCurrentUserAsync(GetBearerToken());
        if (error is not null)
        {
            return Unauthorized();
        }

        return Ok(response);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var (success, error) = await userService.LogoutAsync(GetBearerToken());
        if (!success)
        {
            return error == "Unauthorized" ? Unauthorized() : BadRequest();
        }

        return NoContent();
    }

    private string? GetBearerToken()
    {
        const string prefix = "Bearer ";
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return authorization[prefix.Length..].Trim();
    }
}
