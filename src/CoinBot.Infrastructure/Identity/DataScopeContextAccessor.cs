using CoinBot.Application.Abstractions.DataScope;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace CoinBot.Infrastructure.Identity;

public sealed class DataScopeContextAccessor(IHttpContextAccessor httpContextAccessor) : IDataScopeContextAccessor
{
    private ScopeOverride? scopeOverride;

    public string? UserId => scopeOverride?.UserId ?? ResolveCurrentUserId();

    public bool HasIsolationBypass => scopeOverride?.HasIsolationBypass ?? false;

    public IDisposable BeginScope(string? userId = null, bool hasIsolationBypass = false)
    {
        if (string.IsNullOrWhiteSpace(userId) && !hasIsolationBypass)
        {
            throw new ArgumentException("A data scope override requires a user id or an explicit isolation bypass.", nameof(userId));
        }

        var previousOverride = scopeOverride;
        scopeOverride = new ScopeOverride(userId?.Trim(), hasIsolationBypass);

        return new RevertScope(() => scopeOverride = previousOverride);
    }

    private string? ResolveCurrentUserId()
    {
        var principal = httpContextAccessor.HttpContext?.User;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return principal.FindFirstValue(ClaimTypes.NameIdentifier)?.Trim();
    }

    private sealed record ScopeOverride(string? UserId, bool HasIsolationBypass);

    private sealed class RevertScope(Action onDispose) : IDisposable
    {
        private Action? onDispose = onDispose;

        public void Dispose()
        {
            onDispose?.Invoke();
            onDispose = null;
        }
    }
}
