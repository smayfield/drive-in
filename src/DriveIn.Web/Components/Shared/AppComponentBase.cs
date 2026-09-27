using System.Security.Claims;
using DriveIn.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace DriveIn.Web.Components.Shared;

// Base for app pages: resolves the signed-in user and turns service exceptions into
// UI feedback (AppValidationException/NotFoundException -> alert, AccessDeniedException -> AccessDenied page).
public abstract class AppComponentBase : ComponentBase
{
    [CascadingParameter]
    private Task<AuthenticationState> AuthState { get; set; } = default!;

    [Inject]
    protected NavigationManager Nav { get; set; } = default!;

    protected ClaimsPrincipal User { get; private set; } = new();
    protected string? Error { get; set; }
    protected string? Success { get; set; }

    protected string BaseUri => Nav.BaseUri;

    // Runs on first render and whenever route parameters change (the same page instance is reused).
    protected override async Task OnParametersSetAsync()
    {
        User = (await AuthState).User;
        await RunAsync(LoadAsync);
    }

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task<bool> RunAsync(Func<Task> action, string? success = null)
    {
        Error = null;
        Success = null;
        try
        {
            await action();
            if (success is not null)
                Success = success;
            return true;
        }
        catch (AccessDeniedException)
        {
            Nav.NavigateTo("Account/AccessDenied");
        }
        catch (Exception ex) when (ex is AppValidationException or NotFoundException)
        {
            Error = ex.Message;
        }
        return false;
    }
}
