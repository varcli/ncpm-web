using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using System.Security.Claims;

namespace Ncpm.Services;

public class AuthStateProvider : AuthenticationStateProvider
{
    private readonly ILocalStorageService _localStorage;
    private readonly AuthService _authService;
    private readonly ILogger<AuthStateProvider> _logger;

    public AuthStateProvider(
        ILocalStorageService localStorage,
        AuthService authService,
        ILogger<AuthStateProvider> logger)
    {
        _localStorage = localStorage;
        _authService = authService;
        _logger = logger;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        try
        {
            // Login writes with SetItemAsStringAsync, so read the raw string back.
            // GetItemAsync<string> would attempt a JSON deserialize and fail.
            var token = await _localStorage.GetItemAsStringAsync("auth_token");

            if (string.IsNullOrEmpty(token))
            {
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            var authToken = _authService.ValidateToken(token);
            if (authToken == null)
            {
                await _localStorage.RemoveItemAsync("auth_token");
                await _localStorage.RemoveItemAsync("auth_user");
                return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
            }

            return new AuthenticationState(BuildPrincipal(authToken));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("JavaScript interop calls cannot be issued"))
        {
            // During prerendering, return anonymous state
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting authentication state");
            return new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        }
    }

    public void NotifyUserAuthentication(string token)
    {
        var authToken = _authService.ValidateToken(token);
        if (authToken == null)
            return;

        NotifyAuthenticationStateChanged(
            Task.FromResult(new AuthenticationState(BuildPrincipal(authToken))));
    }

    public async Task NotifyUserLogout()
    {
        var token = await _localStorage.GetItemAsStringAsync("auth_token");
        if (!string.IsNullOrEmpty(token))
        {
            _authService.Logout(token);
        }

        await _localStorage.RemoveItemAsync("auth_token");
        await _localStorage.RemoveItemAsync("auth_user");

        NotifyAuthenticationStateChanged(
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
    }

    private static ClaimsPrincipal BuildPrincipal(Data.AuthToken authToken)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, authToken.UserId),
            new Claim(ClaimTypes.Name, authToken.Username),
            new Claim(ClaimTypes.Role, authToken.Role.ToString())
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "NcpmAuth"));
    }
}
