using System.Security.Claims;
using System.Text.Json;
using Game.Domain;
using Game.Infrastructure;

namespace Game.Server.History;

public sealed record MatchReplayView(Guid MatchId,IReadOnlyList<object> Events);

public static class HistoryEndpoints
{
    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var history=endpoints.MapGroup("/api/history").WithTags("History").RequireAuthorization();
        history.MapGet("",async(int? page,int? pageSize,ClaimsPrincipal user,PostgresGameManagementStore store,CancellationToken ct)=>
            Results.Ok(await store.GetHistoryAsync(Subject(user),page??1,pageSize??20,ct)))
            .WithName("GetMatchHistory").WithSummary("Lista de forma paginada las partidas del usuario autenticado.")
            .Produces<PageResult<MatchHistoryItem>>().Produces(StatusCodes.Status401Unauthorized);
        history.MapGet("/{matchId:guid}",async(Guid matchId,ClaimsPrincipal user,PostgresGameManagementStore store,CancellationToken ct)=>
        {
            var detail=await store.GetDetailAsync(Subject(user),matchId,ct);
            return detail is null?Results.NotFound():Results.Ok(detail);
        }).WithName("GetMatchHistoryDetail").WithSummary("Obtiene una partida únicamente si el usuario participó en ella.")
            .Produces<MatchDetailView>().Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status404NotFound);
        history.MapGet("/{matchId:guid}/events",async(Guid matchId,ClaimsPrincipal user,PostgresGameManagementStore store,CancellationToken ct)=>
        {
            string subject=Subject(user);var detail=await store.GetDetailAsync(subject,matchId,ct);if(detail is null)return Results.NotFound();
            var events=await store.GetReplayAsync(subject,matchId,ct);if(events is null)return Results.NotFound();
            return Results.Ok(new MatchReplayView(matchId,events.Select(item=>Sanitize(item,detail.RequestingPlayerId)).ToArray()));
        }).WithName("ReplayMatchHistory").WithSummary("Reproduce eventos sin exponer planes, tiradas críticas ni subjects de autenticación.")
            .Produces<MatchReplayView>().Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static object Sanitize(ReplayEvent item,Guid requestingPlayer)
    {
        if(item.Type=="round.resolved"&&item.Payload.TryGetProperty("resolution",out var resolutionElement))
        {
            var resolution=resolutionElement.Deserialize<RoundResolution>()!;bool first=resolution.Match.FirstPlayer.PlayerId==requestingPlayer;
            var you=first?resolution.Match.FirstPlayer:resolution.Match.SecondPlayer;var opponent=first?resolution.Match.SecondPlayer:resolution.Match.FirstPlayer;
            return new{item.Sequence,item.Type,item.Version,item.OccurredAt,roundNumber=item.Payload.GetProperty("before").GetProperty("RoundNumber").GetInt32(),
                yourDamage=first?resolution.FirstPlayerDamage:resolution.SecondPlayerDamage,opponentDamage=first?resolution.SecondPlayerDamage:resolution.FirstPlayerDamage,
                yourCriticalHits=first?resolution.FirstPlayerCriticalHits:resolution.SecondPlayerCriticalHits,opponentCriticalHits=first?resolution.SecondPlayerCriticalHits:resolution.FirstPlayerCriticalHits,
                yourAttacksBlocked=first?resolution.FirstPlayerAttacksBlocked:resolution.SecondPlayerAttacksBlocked,opponentAttacksBlocked=first?resolution.SecondPlayerAttacksBlocked:resolution.FirstPlayerAttacksBlocked,
                you=new{health=you.Health,energy=you.Energy},opponent=new{health=opponent.Health},status=resolution.Match.Status==MatchStatus.Active?"active":"completed",winnerId=resolution.Match.WinnerId};
        }
        if(item.Payload.TryGetProperty("state",out var stateElement))return StateEvent(item,stateElement.Deserialize<MatchState>()!,requestingPlayer,null);
        if(item.Payload.TryGetProperty("after",out var afterElement))return StateEvent(item,afterElement.Deserialize<MatchState>()!,requestingPlayer,item.Payload.TryGetProperty("reason",out var reason)?reason.GetString():null);
        return new{item.Sequence,item.Type,item.Version,item.OccurredAt};
    }
    private static object StateEvent(ReplayEvent item,MatchState state,Guid requestingPlayer,string? reason)
    {
        bool first=state.FirstPlayer.PlayerId==requestingPlayer;var you=first?state.FirstPlayer:state.SecondPlayer;var opponent=first?state.SecondPlayer:state.FirstPlayer;
        return new{item.Sequence,item.Type,item.Version,item.OccurredAt,state.RoundNumber,status=state.Status==MatchStatus.Active?"active":"completed",state.WinnerId,reason,you=new{health=you.Health,energy=you.Energy},opponent=new{health=opponent.Health}};
    }
    private static string Subject(ClaimsPrincipal user)=>user.FindFirst("sub")?.Value??throw new InvalidOperationException("Authenticated token lacks sub.");
}
