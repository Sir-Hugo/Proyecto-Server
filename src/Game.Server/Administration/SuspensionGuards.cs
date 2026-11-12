using System.Security.Claims;
using Game.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace Game.Server.Administration;

public sealed class SuspendedUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PostgresGameManagementStore store)
    {
        string? subject = context.User.FindFirst("sub")?.Value;
        if (context.User.Identity?.IsAuthenticated == true && !string.IsNullOrWhiteSpace(subject) &&
            await store.IsSuspendedAsync(subject, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { title = "Acceso suspendido.", status = 403, code = "user_suspended" }, context.RequestAborted);
            return;
        }
        await next(context);
    }
}

public sealed class SuspendedUserHubFilter(PostgresGameManagementStore store) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        string? subject = invocationContext.Context.User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(subject) && await store.IsSuspendedAsync(subject, invocationContext.Context.ConnectionAborted))
            throw new HubException("user_suspended");
        return await next(invocationContext);
    }
}
