using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Game.Server.Hubs;
using Game.Server.Security;

namespace Game.Server.Rooms;

public static class RoomEndpoints
{
    public static IEndpointRouteBuilder MapRoomEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder rooms = endpoints.MapGroup("/api/rooms")
            .WithTags("Rooms")
            .RequireAuthorization()
            .RequireRateLimiting(SecurityRateLimits.ApiPolicy);

        rooms.MapPost("", (CreateRoomRequest? request, ClaimsPrincipal user, InMemoryRoomService service) =>
        {
            if (!TryGetHumanSubject(user, out string subject))
            {
                return HumanIdentityRequired();
            }

            if (request is null)
            {
                return Failure(StatusCodes.Status400BadRequest, "request_required");
            }

            RoomOperationResult<CreatedRoomResponse> result = service.Create(subject, request.Visibility);
            if (!result.IsSuccess)
            {
                return Failure(result.ErrorStatusCode!.Value, result.ErrorCode!);
            }

            CreatedRoomResponse response = result.Value!;
            return Results.Created($"/api/rooms/{response.Room.RoomId}", response);
        })
        .WithName("CreateRoom")
        .WithSummary("Crea una sala pública o privada.");

        rooms.MapGet("", (ClaimsPrincipal user, InMemoryRoomService service) =>
        {
            if (!TryGetHumanSubject(user, out string subject))
            {
                return HumanIdentityRequired();
            }

            return Results.Ok(service.ListPublicRooms(subject));
        })
        .WithName("ListPublicRooms")
        .WithSummary("Lista las salas públicas que todavía admiten jugadores.");

        rooms.MapGet("/{roomId:guid}", async (Guid roomId, ClaimsPrincipal user, InMemoryRoomService service, CancellationToken cancellationToken) =>
        {
            if (!TryGetHumanSubject(user, out string subject))
            {
                return HumanIdentityRequired();
            }

            RoomOperationResult<RoomResponse> result = await service.GetAsync(roomId, subject, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Failure(result.ErrorStatusCode!.Value, result.ErrorCode!);
        })
        .WithName("GetRoom")
        .WithSummary("Obtiene una sala pública o una sala privada de la que el usuario es miembro.");

        rooms.MapPost("/{roomId:guid}/join", (Guid roomId, JoinRoomRequest? request, ClaimsPrincipal user,
            InMemoryRoomService service, CancellationToken cancellationToken) =>
        {
            if (!TryGetHumanSubject(user, out string subject))
            {
                return HumanIdentityRequired();
            }

            if (request is null)
            {
                return Failure(StatusCodes.Status400BadRequest, "request_required");
            }

            RoomOperationResult<RoomResponse> result = service.Join(roomId, subject, request.InviteCode);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Failure(result.ErrorStatusCode!.Value, result.ErrorCode!);
        })
        .WithName("JoinRoom")
        .WithSummary("Se une a una sala; las privadas requieren su código de invitación.");

        rooms.MapDelete("/{roomId:guid}/membership", async (Guid roomId, ClaimsPrincipal user,
            InMemoryRoomService service, CancellationToken cancellationToken) =>
        {
            if (!TryGetHumanSubject(user, out string subject))
            {
                return HumanIdentityRequired();
            }

            RoomOperationResult<LeaveRoomResponse> result = await service.LeaveAsync(roomId, subject, cancellationToken);
            return result.IsSuccess
                ? Results.Ok(result.Value)
                : Failure(result.ErrorStatusCode!.Value, result.ErrorCode!);
        })
        .WithName("LeaveRoom")
        .WithSummary("Sale de una sala; si sale quien la creó, la sala se cierra.");

        rooms.MapPut("/{roomId:guid}/ready", async (Guid roomId, SetRoomReadyRequest? request, ClaimsPrincipal user,
            InMemoryRoomService service, IHubContext<GameHub> hub, ILogger<InMemoryRoomService> logger, CancellationToken cancellationToken) =>
        {
            if (!TryGetHumanSubject(user, out string subject))
            {
                return HumanIdentityRequired();
            }

            if (request is null)
            {
                return Failure(StatusCodes.Status400BadRequest, "request_required");
            }

            RoomOperationResult<RoomResponse> result = await service.SetReadyAsync(roomId, subject, request.IsReady, cancellationToken);
            if (!result.IsSuccess)
                return Failure(result.ErrorStatusCode!.Value, result.ErrorCode!);

            if (result.Value!.MatchId is Guid matchId)
            {
                try
                {
                    await hub.Clients.Group($"room:{roomId}").SendAsync("room.match.started", new
                    { type = "room.match.started", version = 1, roomId, matchId });
                }
                catch (Exception)
                {
                    // Already committed: GET room / JoinMatch can recover the matchId and snapshot.
                    logger.LogWarning("SignalR room start delivery failed after commit for match {MatchId}", matchId);
                }
            }

            return Results.Ok(result.Value);
        })
        .WithName("SetRoomReady")
        .WithSummary("Marca al miembro como listo o no listo.");

        return endpoints;
    }

    private static bool TryGetHumanSubject(ClaimsPrincipal user, out string subject)
    {
        string? grantType = user.FindFirst("gty")?.Value;
        string? claimSubject = user.FindFirst("sub")?.Value;

        bool isMachineToken =
            string.Equals(grantType, "client-credentials", StringComparison.OrdinalIgnoreCase) ||
            claimSubject?.EndsWith("@clients", StringComparison.OrdinalIgnoreCase) is true;

        if (string.IsNullOrWhiteSpace(claimSubject) || isMachineToken)
        {
            subject = string.Empty;
            return false;
        }

        subject = claimSubject;
        return true;
    }

    private static IResult HumanIdentityRequired() =>
        Failure(StatusCodes.Status403Forbidden, "human_user_token_required");

    private static IResult Failure(int statusCode, string code) =>
        Results.Problem(
            statusCode: statusCode,
            title: "La solicitud de sala no pudo completarse.",
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
