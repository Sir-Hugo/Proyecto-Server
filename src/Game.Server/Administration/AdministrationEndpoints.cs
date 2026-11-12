using System.Security.Claims;
using Game.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Game.Server.Administration;

public sealed record SuspendUserRequest(string? Reason,DateTimeOffset? ExpiresAt);
public sealed record AdministrativeReasonRequest(string? Reason);

public static class AdministrationEndpoints
{
    public const string Policy="GameAdministrator";
    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin=endpoints.MapGroup("/api/admin").WithTags("Administration").RequireAuthorization(Policy);
        admin.MapGet("/matches/active",async(int? limit,PostgresGameManagementStore store,CancellationToken ct)=>Results.Ok(await store.GetActiveMatchesAsync(limit??100,ct)))
            .WithName("AdminListActiveMatches").WithSummary("Lista partidas activas para operación administrativa.");
        admin.MapGet("/connections",(ConnectionRegistry registry)=>Results.Ok(registry.List()))
            .WithName("AdminListConnections").WithSummary("Lista conexiones SignalR activas de la instancia actual.");
        admin.MapPost("/matches/{matchId:guid}/terminate",async(Guid matchId,AdministrativeReasonRequest? request,ClaimsPrincipal user,PostgresGameManagementStore store,CancellationToken ct)=>
        {
            if(!ValidReason(request?.Reason))return InvalidReason();
            return await store.TerminateMatchAsync(Subject(user),matchId,request!.Reason!.Trim(),ct)?Results.Ok(new{matchId,status="completed",reason="administrative"}):Results.Conflict(new{code="match_not_active"});
        }).WithName("AdminTerminateMatch").WithSummary("Termina una partida activa y registra la acción en auditoría.");
        admin.MapPut("/users/{subject}/suspension",async(string subject,SuspendUserRequest? request,ClaimsPrincipal user,PostgresGameManagementStore store,CancellationToken ct)=>
        {
            if(string.IsNullOrWhiteSpace(subject)||!ValidReason(request?.Reason)||request?.ExpiresAt<=DateTimeOffset.UtcNow)return Results.BadRequest(new{code="invalid_suspension"});
            await store.SuspendAsync(Subject(user),subject,request!.Reason!.Trim(),request.ExpiresAt,ct);return Results.Ok(new{subject,suspended=true,request.ExpiresAt});
        }).WithName("AdminSuspendUser").WithSummary("Suspende un usuario y audita la decisión.");
        admin.MapDelete("/users/{subject}/suspension",async(string subject,[FromBody] AdministrativeReasonRequest? request,ClaimsPrincipal user,PostgresGameManagementStore store,CancellationToken ct)=>
        {
            if(string.IsNullOrWhiteSpace(subject)||!ValidReason(request?.Reason))return InvalidReason();
            return await store.UnsuspendAsync(Subject(user),subject,request!.Reason!.Trim(),ct)?Results.Ok(new{subject,suspended=false}):Results.NotFound();
        }).WithName("AdminUnsuspendUser").WithSummary("Retira una suspensión y registra la acción.");
        admin.MapGet("/audit",async(int? page,int? pageSize,PostgresGameManagementStore store,CancellationToken ct)=>Results.Ok(await store.GetAuditAsync(page??1,pageSize??50,ct)))
            .WithName("AdminAuditLog").WithSummary("Consulta paginada de auditoría administrativa append-only.")
            .Produces<PageResult<AdminAuditView>>().Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
        return endpoints;
    }
    private static bool ValidReason(string? reason)=>!string.IsNullOrWhiteSpace(reason)&&reason.Trim().Length<=500;
    private static IResult InvalidReason()=>Results.BadRequest(new{code="invalid_reason"});
    private static string Subject(ClaimsPrincipal user)=>user.FindFirst("sub")?.Value??throw new InvalidOperationException("Authenticated administrator lacks sub.");
}
