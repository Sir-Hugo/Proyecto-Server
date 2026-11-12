using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Game.Server.Rooms;
using Game.Server.Contracts.V1;
using Game.Domain;
using Game.Server.Administration;

namespace Game.Server.Hubs;

/// <summary>
/// Hub autenticado de SignalR
/// Recibe comandos de cola rápida, valida la identidad participante al abrir una partida y envía un estado inicial privado.
/// Las conexiones requieren un JWT válido.
/// </summary>
[Authorize]
public sealed class GameHub(MatchRoundService connectionTracker, ConnectionRegistry connectionRegistry) : Hub
{
    public override async Task OnConnectedAsync()
    {
        connectionRegistry.Add(Context.ConnectionId, GetHumanSubject());
        await base.OnConnectedAsync();
    }
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connectionTracker.DetachConnection(Context.ConnectionId);
        connectionRegistry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
    public async Task JoinRoom(JoinRoomMessage message, InMemoryRoomService rooms)
    {
        ValidateSimpleMessage(message.Type, message.Version, JoinRoomMessage.MessageType, message.RoomId);
        Guid roomId = message.RoomId;
        if (!rooms.IsMember(roomId, GetHumanSubject()))
            throw new HubException("room_membership_required");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"room:{roomId}");
    }

    public Task LeaveRoom(LeaveRoomMessage message)
    {
        ValidateSimpleMessage(message.Type, message.Version, LeaveRoomMessage.MessageType, message.RoomId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"room:{message.RoomId}");
    }

    public Task JoinMatchmakingQueue(JoinMatchmakingQueueMessage message, MatchmakingService matchmaking)
    {
        ValidateQueueMessage(message.Type, message.Version, "matchmaking.queue.join");
        string subject = GetHumanSubject();
        return matchmaking.JoinAsync(subject, Context.ConnectionId, message.RequestId, Context.ConnectionAborted);
    }

    public Task CancelMatchmakingQueue(CancelMatchmakingQueueMessage message, MatchmakingService matchmaking)
    {
        ValidateQueueMessage(message.Type, message.Version, "matchmaking.queue.cancel");
        string subject = GetHumanSubject();
        return matchmaking.CancelAsync(subject, message.RequestId, Context.ConnectionAborted);
    }

    public async Task JoinMatch(JoinMatchMessage message, MatchRoundService rounds)
    {
        ValidateSimpleMessage(message.Type, message.Version, JoinMatchMessage.MessageType, message.MatchId);
        object snapshot = await rounds.AttachAsync(message.MatchId, GetHumanSubject(), Context.ConnectionId, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, $"match:{message.MatchId}", Context.ConnectionAborted);
        await Clients.Caller.SendAsync("match.state.snapshot", snapshot, Context.ConnectionAborted);
        await rounds.DeliverPendingAsync(message.MatchId, GetHumanSubject(), Context.ConnectionAborted);
    }

    public async Task ResumeMatch(MatchResumeMessage message, MatchRoundService rounds)
    {
        if (!string.Equals(message.Type, "match.resume", StringComparison.Ordinal) || message.Version != 1)
            throw new HubException("invalid_message");
        string subject=GetHumanSubject();
        Guid? matchId=message.MatchId ?? await rounds.FindLatestMatchAsync(subject,Context.ConnectionAborted);
        if(matchId is null) throw new HubException("active_match_not_found");
        object snapshot=await rounds.AttachAsync(matchId.Value,subject,Context.ConnectionId,Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId,$"match:{matchId}",Context.ConnectionAborted);
        await Clients.Caller.SendAsync("match.state.snapshot",snapshot,Context.ConnectionAborted);
        await rounds.DeliverPendingAsync(matchId,subject,Context.ConnectionAborted);
    }

    public Task SubmitRoundPlan(SubmitRoundPlanMessage message, MatchRoundService rounds)
    {
        string subject = GetHumanSubject();
        return rounds.SubmitAsync(subject, Context.ConnectionId, message, Context.ConnectionAborted);
    }

    public Task AbandonMatch(Guid matchId, MatchRoundService rounds)
    {
        if (matchId == Guid.Empty)
            throw new HubException("invalid_match_id");
        return rounds.AbandonAsync(matchId, GetHumanSubject(), Context.ConnectionAborted);
    }

    public Task SetMatchNickname(SetMatchNicknameMessage message, MatchRoundService rounds)
    {
        string subject = GetHumanSubject();
        if (!string.Equals(message.Type, SetMatchNicknameMessage.MessageType, StringComparison.Ordinal) ||
            message.Version != SetMatchNicknameMessage.CurrentVersion || message.RequestId == Guid.Empty ||
            message.MatchId == Guid.Empty || string.IsNullOrEmpty(message.Nickname))
            throw new HubException("invalid_message");
        return rounds.SetNicknameAsync(message.MatchId, subject, Context.ConnectionId, message.Nickname, Context.ConnectionAborted);
    }

    private static void ValidateQueueMessage(string type, int version, string expectedType)
    {
        if (!string.Equals(type, expectedType, StringComparison.Ordinal) || version != 1)
        {
            throw new HubException("invalid_message");
        }
    }

    private static void ValidateSimpleMessage(string? type, int version, string expectedType, Guid id)
    {
        if (!string.Equals(type, expectedType, StringComparison.Ordinal) || version != 1 || id == Guid.Empty)
            throw new HubException("invalid_message");
    }

    private string GetHumanSubject()
    {
        string? subject = Context.User?.FindFirst("sub")?.Value;
        string? grantType = Context.User?.FindFirst("gty")?.Value;
        if (string.IsNullOrWhiteSpace(subject) ||
            string.Equals(grantType, "client-credentials", StringComparison.OrdinalIgnoreCase) ||
            subject.EndsWith("@clients", StringComparison.OrdinalIgnoreCase))
        {
            throw new HubException("human_user_token_required");
        }

        return subject;
    }
}

public sealed record MatchResumeMessage(string Type,int Version,Guid? MatchId,long? LastSequence);
