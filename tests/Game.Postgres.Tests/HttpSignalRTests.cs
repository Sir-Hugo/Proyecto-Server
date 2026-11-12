using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Game.Server.Rooms;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Game.Postgres.Tests;

[Trait("Category", "PostgreSQL")]
public sealed class HttpSignalRTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [PostgresFact] public async Task TwoPlayersCompleteRealRestAndSignalRFlowWithDurableResult()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await using var app = new GameApplication(db.ConnectionString);
        using var alice = app.Player("auth0|http-alice"); using var bob = app.Player("auth0|http-bob");
        var createdResponse = await alice.PostAsJsonAsync("/api/rooms", new { visibility = "private" });
        createdResponse.EnsureSuccessStatusCode();
        var created = (await createdResponse.Content.ReadFromJsonAsync<CreatedRoomResponse>())!;
        (await bob.PostAsJsonAsync($"/api/rooms/{created.Room.RoomId}/join", new { inviteCode = created.InviteCode })).EnsureSuccessStatusCode();
        await using var hubA = app.Hub("auth0|http-alice"); await using var hubB = app.Hub("auth0|http-bob");
        var statesA = Channel.CreateUnbounded<JsonElement>(); var statesB = Channel.CreateUnbounded<JsonElement>();
        var roundsA = Channel.CreateUnbounded<JsonElement>(); var roundsB = Channel.CreateUnbounded<JsonElement>();
        var roomStarts = Channel.CreateUnbounded<JsonElement>();
        hubA.On<JsonElement>("match.state.snapshot", message => statesA.Writer.TryWrite(message));
        hubB.On<JsonElement>("match.state.snapshot", message => statesB.Writer.TryWrite(message));
        hubA.On<JsonElement>("round.resolved", message => roundsA.Writer.TryWrite(message));
        hubB.On<JsonElement>("round.resolved", message => roundsB.Writer.TryWrite(message));
        hubA.On<JsonElement>("room.match.started", message => roomStarts.Writer.TryWrite(message));
        await Task.WhenAll(hubA.StartAsync(), hubB.StartAsync());
        var roomMessage = new { type = "room.join", version = 1, roomId = created.Room.RoomId };
        await hubA.InvokeAsync("JoinRoom", roomMessage); await hubB.InvokeAsync("JoinRoom", roomMessage);
        (await alice.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true })).EnsureSuccessStatusCode();
        var readyResponse = await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true }); readyResponse.EnsureSuccessStatusCode();
        var ready = (await readyResponse.Content.ReadFromJsonAsync<RoomResponse>())!; Guid matchId = ready.MatchId!.Value;
        Assert.Equal(matchId, (await NextAsync(roomStarts)).GetProperty("matchId").GetGuid());
        var join = new { type = "match.join", version = 1, matchId };
        await hubA.InvokeAsync("JoinMatch", join); await hubB.InvokeAsync("JoinMatch", join);
        await hubA.InvokeAsync("SetMatchNickname", new { type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId, nickname = "Alice" });
        await hubB.InvokeAsync("SetMatchNickname", new { type = "match.nickname.set", version = 1, requestId = Guid.NewGuid(), matchId, nickname = "Bob" });
        var stateA = await ActiveAsync(statesA); var stateB = await ActiveAsync(statesB);
        int resolved = 0;
        while (stateA.GetProperty("status").GetString() == "active")
        {
            long sequence = stateA.GetProperty("sequence").GetInt64();
            var a = new { type = "round.plan.submit", version = 1, requestId = Guid.NewGuid(), matchId, expectedSequence = sequence,
                actions = Enumerable.Repeat("Attack", stateA.GetProperty("you").GetProperty("energy").GetInt32()).ToArray() };
            var b = new { type = "round.plan.submit", version = 1, requestId = Guid.NewGuid(), matchId, expectedSequence = sequence,
                actions = Enumerable.Repeat("Reserve", stateB.GetProperty("you").GetProperty("energy").GetInt32()).ToArray() };
            await Task.WhenAll(hubA.InvokeAsync("SubmitRoundPlan", a), hubB.InvokeAsync("SubmitRoundPlan", b));
            stateA = await NextAsync(roundsA); stateB = await NextAsync(roundsB); resolved++;
            Assert.Equal(stateA.GetProperty("sequence").GetInt64(), stateB.GetProperty("sequence").GetInt64());
            Assert.DoesNotContain("actions", stateA.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var stored = (await db.Store.GetAsync(matchId))!;
        Assert.Equal(Game.Domain.MatchStatus.Finished, stored.State.Status);
        Assert.Equal(stored.State.FirstPlayer.PlayerId, stored.State.WinnerId);
        Assert.Equal(resolved, stored.State.SequenceNumber);
        Assert.Equal((long)resolved + 1, await db.ScalarAsync<long>("SELECT count(*) FROM match_events"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM pending_round_plans"));
        var room = (await alice.GetFromJsonAsync<RoomResponse>($"/api/rooms/{created.Room.RoomId}"))!;
        Assert.Equal("Partida finalizada", room.Status);
        (await alice.DeleteAsync($"/api/rooms/{created.Room.RoomId}/membership")).EnsureSuccessStatusCode();
    }

    [PostgresFact] public async Task HttpAndHubEnforceAuthenticatedHumanParticipant()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await using var app = new GameApplication(db.ConnectionString);
        using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/rooms", new { visibility = "public" })).StatusCode);
        using var machine = app.Player("machine@clients");
        Assert.Equal(HttpStatusCode.Forbidden, (await machine.PostAsJsonAsync("/api/rooms", new { visibility = "public" })).StatusCode);
        var game = new LiveMatch(db); await game.CreateAsync();
        await using var outsider = app.Hub("auth0|outsider"); await outsider.StartAsync();
        var error = await Assert.ThrowsAsync<Microsoft.AspNetCore.SignalR.HubException>(() => outsider.InvokeAsync("JoinMatch", new { type = "match.join", version = 1, matchId = game.Initial.MatchId }));
        Assert.Contains("not_match_participant", error.Message);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM match_commands"));
    }

    [PostgresFact] public async Task RestReportsProblemDetailsWithoutConfirmingFailedCreation()
    {
        await using var db = await fixture.CreateDatabaseAsync(); await using var app = new GameApplication(db.ConnectionString);
        using var alice = app.Player("auth0|failure-alice"); using var bob = app.Player("auth0|failure-bob");
        var response = await alice.PostAsJsonAsync("/api/rooms", new { visibility = "public" }); response.EnsureSuccessStatusCode();
        var created = (await response.Content.ReadFromJsonAsync<CreatedRoomResponse>())!;
        (await bob.PostAsJsonAsync($"/api/rooms/{created.Room.RoomId}/join", new { inviteCode = (string?)null })).EnsureSuccessStatusCode();
        (await alice.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true })).EnsureSuccessStatusCode();
        await db.ExecuteAsync("""
            CREATE FUNCTION injected_http_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected failure' USING ERRCODE='23514'; END; $$;
            CREATE TRIGGER injected_http_event_failure BEFORE INSERT ON match_events FOR EACH ROW EXECUTE FUNCTION injected_http_failure();
            """);
        var failed = await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("application/problem+json", failed.Content.Headers.ContentType!.MediaType);
        var problem = (await failed.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("database_unavailable", problem.GetProperty("code").GetString());
        Assert.DoesNotContain("auth0|", problem.GetRawText()); Assert.DoesNotContain("Password", problem.GetRawText());
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
        await db.ExecuteAsync("DROP TRIGGER injected_http_event_failure ON match_events");
        (await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready", new { isReady = true })).EnsureSuccessStatusCode();
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM matches"));
    }

    [PostgresFact] public async Task NewHostResumesPendingPlanAndCompletesRoundExactlyOnce()
    {
        await using var db=await fixture.CreateDatabaseAsync();var key=new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));Guid matchId;
        await using(var first=new GameApplication(db.ConnectionString,key))
        {
            using var alice=first.Player("auth0|restart-alice");using var bob=first.Player("auth0|restart-bob");
            var response=await alice.PostAsJsonAsync("/api/rooms",new{visibility="public"});response.EnsureSuccessStatusCode();var room=(await response.Content.ReadFromJsonAsync<CreatedRoomResponse>())!;
            (await bob.PostAsJsonAsync($"/api/rooms/{room.Room.RoomId}/join",new{inviteCode=(string?)null})).EnsureSuccessStatusCode();
            (await alice.PutAsJsonAsync($"/api/rooms/{room.Room.RoomId}/ready",new{isReady=true})).EnsureSuccessStatusCode();var ready=await bob.PutAsJsonAsync($"/api/rooms/{room.Room.RoomId}/ready",new{isReady=true});ready.EnsureSuccessStatusCode();matchId=(await ready.Content.ReadFromJsonAsync<RoomResponse>())!.MatchId!.Value;
            await using var hubA=first.Hub("auth0|restart-alice");await using var hubB=first.Hub("auth0|restart-bob");await Task.WhenAll(hubA.StartAsync(),hubB.StartAsync());
            var join=new{type="match.join",version=1,matchId};await hubA.InvokeAsync("JoinMatch",join);await hubB.InvokeAsync("JoinMatch",join);
            await hubA.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId,nickname="Alice"});
            await hubB.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId,nickname="Bob"});
            await hubA.InvokeAsync("SubmitRoundPlan",new{type="round.plan.submit",version=1,requestId=Guid.NewGuid(),matchId,expectedSequence=0,actions=new[]{"Attack"}});
        }
        await using(var restarted=new GameApplication(db.ConnectionString,key))
        {
            await using var hubA=restarted.Hub("auth0|restart-alice");await using var hubB=restarted.Hub("auth0|restart-bob");var statesA=Channel.CreateUnbounded<JsonElement>();var statesB=Channel.CreateUnbounded<JsonElement>();var rounds=Channel.CreateUnbounded<JsonElement>();
            hubA.On<JsonElement>("match.state.snapshot",m=>statesA.Writer.TryWrite(m));hubB.On<JsonElement>("match.state.snapshot",m=>statesB.Writer.TryWrite(m));hubB.On<JsonElement>("round.resolved",m=>rounds.Writer.TryWrite(m));
            await Task.WhenAll(hubA.StartAsync(),hubB.StartAsync());var resume=new{type="match.resume",version=1,matchId=(Guid?)null,lastSequence=(long?)null};await hubA.InvokeAsync("ResumeMatch",resume);await hubB.InvokeAsync("ResumeMatch",resume);
            var stateA=await NextAsync(statesA);var stateB=await NextAsync(statesB);Assert.Equal(matchId,stateA.GetProperty("matchId").GetGuid());Assert.True(stateA.GetProperty("hasSubmittedPlan").GetBoolean());Assert.False(stateB.GetProperty("hasSubmittedPlan").GetBoolean());
            await hubB.InvokeAsync("SubmitRoundPlan",new{type="round.plan.submit",version=1,requestId=Guid.NewGuid(),matchId,expectedSequence=0,actions=new[]{"Attack"}});Assert.Equal(1,(await NextAsync(rounds)).GetProperty("sequence").GetInt64());
        }
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM match_events WHERE match_id=$1 AND event_type='round.resolved'",matchId));Assert.Equal(1,(await db.Store.GetAsync(matchId))!.State.SequenceNumber);
    }

    [PostgresFact] public async Task HistoryIsPagedParticipantOnlyAndReplayOmitsPrivateEventData()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);var service=await game.CreateAsync();
        await service.AbandonAsync(game.Initial.MatchId,game.Bob);
        await using var app=new GameApplication(db.ConnectionString);using var alice=app.Player(game.Alice);using var outsider=app.Player("auth0|history-outsider");using var anonymous=app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/history")).StatusCode);
        var page=await alice.GetFromJsonAsync<JsonElement>("/api/history?page=1&pageSize=1");
        Assert.Equal(1,page.GetProperty("total").GetInt32());Assert.Single(page.GetProperty("items").EnumerateArray());
        var detail=await alice.GetAsync($"/api/history/{game.Initial.MatchId}");detail.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"/api/history/{game.Initial.MatchId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await outsider.GetAsync($"/api/history/{game.Initial.MatchId}/events")).StatusCode);
        var replay=await alice.GetAsync($"/api/history/{game.Initial.MatchId}/events");replay.EnsureSuccessStatusCode();string json=await replay.Content.ReadAsStringAsync();
        Assert.DoesNotContain("firstPlan",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("secondPlan",json,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("criticalRolls",json,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("auth0|",json,StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(JsonDocument.Parse(json).RootElement.GetProperty("events").EnumerateArray());
    }

    [PostgresFact] public async Task AdministrationRequiresAuth0PermissionTerminatesAtomicallyAndIsDocumented()
    {
        await using var db=await fixture.CreateDatabaseAsync();var game=new LiveMatch(db);await game.CreateAsync();await using var app=new GameApplication(db.ConnectionString);
        using var anonymous=app.CreateClient();using var player=app.Player(game.Alice);using var fakeRole=app.Player("auth0|fake-admin",role:"admin");using var admin=app.Player("auth0|administrator",admin:true);
        Assert.Equal(HttpStatusCode.Unauthorized,(await anonymous.GetAsync("/api/admin/matches/active")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await player.GetAsync("/api/admin/matches/active")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await fakeRole.GetAsync("/api/admin/matches/active")).StatusCode);
        var active=await admin.GetAsync("/api/admin/matches/active");active.EnsureSuccessStatusCode();Assert.Contains(game.Initial.MatchId.ToString(),await active.Content.ReadAsStringAsync());
        var terminated=await admin.PostAsJsonAsync($"/api/admin/matches/{game.Initial.MatchId}/terminate",new{reason="Entrega universitaria"});terminated.EnsureSuccessStatusCode();
        Assert.Equal("completed",await db.ScalarAsync<string>("SELECT status::text FROM matches WHERE id=$1",game.Initial.MatchId));
        Assert.Equal("administrative",await db.ScalarAsync<string>("SELECT terminal_reason FROM matches WHERE id=$1",game.Initial.MatchId));
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM admin_audit_log WHERE action='match.terminated' AND match_id=$1",game.Initial.MatchId));
        Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM match_outbox WHERE match_id=$1",game.Initial.MatchId));
        (await admin.GetAsync("/api/admin/audit?page=1&pageSize=10")).EnsureSuccessStatusCode();
        var openApi=await anonymous.GetStringAsync("/openapi/v1.json");Assert.Contains("/api/history",openApi);Assert.Contains("/api/admin/audit",openApi);Assert.Contains("bearer",openApi,StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact] public async Task SuspensionBlocksHttpAndExistingHubAndAdministrativeActionsAreAppendOnly()
    {
        await using var db=await fixture.CreateDatabaseAsync();await using var app=new GameApplication(db.ConnectionString);const string subject="auth0|suspended-player";
        using var admin=app.Player("auth0|administrator",admin:true);using var player=app.Player(subject);await using var hub=app.Hub(subject);await hub.StartAsync();
        var connections=await admin.GetStringAsync("/api/admin/connections");Assert.Contains(subject,connections);
        string route=$"/api/admin/users/{Uri.EscapeDataString(subject)}/suspension";
        var suspended=await admin.PutAsJsonAsync(route,new{reason="Incumplimiento comprobado",expiresAt=(DateTimeOffset?)null});suspended.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await player.GetAsync("/api/history")).StatusCode);
        var blocked=await Record.ExceptionAsync(()=>hub.InvokeAsync("ResumeMatch",new{type="match.resume",version=1,matchId=(Guid?)null,lastSequence=(long?)null}));
        Assert.NotNull(blocked);Assert.True(blocked is Microsoft.AspNetCore.SignalR.HubException{Message:"user_suspended"} or HttpRequestException{StatusCode:HttpStatusCode.Forbidden});
        Assert.Equal(1L,await db.ScalarAsync<long>("SELECT count(*) FROM admin_audit_log WHERE action='user.suspended' AND target_subject=$1",subject));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(()=>db.ExecuteAsync("UPDATE admin_audit_log SET reason='changed'"));
        using var request=new HttpRequestMessage(HttpMethod.Delete,route){Content=JsonContent.Create(new{reason="Revisión completada"})};var restored=await admin.SendAsync(request);restored.EnsureSuccessStatusCode();
        (await player.GetAsync("/api/history")).EnsureSuccessStatusCode();Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM admin_audit_log WHERE target_subject=$1",subject));
    }

    [PostgresFact] public async Task SamePlayersStartRecoverAndFinishTwoConsecutiveRoomMatches()
    {
        await using var db=await fixture.CreateDatabaseAsync();await using var app=new GameApplication(db.ConnectionString);
        const string aliceSubject="auth0|consecutive-alice",bobSubject="auth0|consecutive-bob";
        using var alice=app.Player(aliceSubject);using var bob=app.Player(bobSubject);
        await using var hubA=app.Hub(aliceSubject);await using var hubB=app.Hub(bobSubject);await Task.WhenAll(hubA.StartAsync(),hubB.StartAsync());

        async Task<Guid> StartRoomMatchAsync()
        {
            var createdResponse=await alice.PostAsJsonAsync("/api/rooms",new{visibility="private"});createdResponse.EnsureSuccessStatusCode();
            var created=(await createdResponse.Content.ReadFromJsonAsync<CreatedRoomResponse>())!;
            (await bob.PostAsJsonAsync($"/api/rooms/{created.Room.RoomId}/join",new{inviteCode=created.InviteCode})).EnsureSuccessStatusCode();
            (await alice.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready",new{isReady=true})).EnsureSuccessStatusCode();
            var ready=await bob.PutAsJsonAsync($"/api/rooms/{created.Room.RoomId}/ready",new{isReady=true});ready.EnsureSuccessStatusCode();
            return (await ready.Content.ReadFromJsonAsync<RoomResponse>())!.MatchId!.Value;
        }

        Guid first=await StartRoomMatchAsync();
        await hubA.InvokeAsync("JoinMatch",new{type="match.join",version=1,matchId=first});await hubB.InvokeAsync("JoinMatch",new{type="match.join",version=1,matchId=first});
        await hubA.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId=first,nickname="Alice"});
        await hubB.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId=first,nickname="Bob"});
        await hubB.InvokeAsync("AbandonMatch",first);

        Guid second=await StartRoomMatchAsync();Assert.NotEqual(first,second);
        var secondSnapshots=Channel.CreateUnbounded<JsonElement>();hubB.On<JsonElement>("match.state.snapshot",message=>secondSnapshots.Writer.TryWrite(message));
        await hubB.InvokeAsync("JoinMatch",new{type="match.join",version=1,matchId=second});Assert.Equal(second,(await NextAsync(secondSnapshots)).GetProperty("matchId").GetGuid());
        await using var reloadedAlice=app.Hub(aliceSubject);var recovered=Channel.CreateUnbounded<JsonElement>();reloadedAlice.On<JsonElement>("match.state.snapshot",message=>recovered.Writer.TryWrite(message));await reloadedAlice.StartAsync();
        await reloadedAlice.InvokeAsync("ResumeMatch",new{type="match.resume",version=1,matchId=(Guid?)null,lastSequence=(long?)null});
        var recoveredSnapshot=await NextAsync(recovered);Assert.Equal(second,recoveredSnapshot.GetProperty("matchId").GetGuid());Assert.NotEqual("completed",recoveredSnapshot.GetProperty("status").GetString());
        await reloadedAlice.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId=second,nickname="Alice2"});
        await hubB.InvokeAsync("SetMatchNickname",new{type="match.nickname.set",version=1,requestId=Guid.NewGuid(),matchId=second,nickname="Bob2"});
        await hubB.InvokeAsync("AbandonMatch",second);

        var aliceHistory=await alice.GetFromJsonAsync<JsonElement>("/api/history?page=1&pageSize=10");var bobHistory=await bob.GetFromJsonAsync<JsonElement>("/api/history?page=1&pageSize=10");
        Assert.Equal(2,aliceHistory.GetProperty("total").GetInt32());Assert.Equal(2,bobHistory.GetProperty("total").GetInt32());
        Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM matches WHERE id=$1 OR id=$2",first,second));
        Assert.Equal(2L,await db.ScalarAsync<long>("SELECT count(*) FROM match_events WHERE event_type='match.ended' AND (match_id=$1 OR match_id=$2)",first,second));
    }

    private static async Task<JsonElement> NextAsync(Channel<JsonElement> channel) =>
        await channel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    private static async Task<JsonElement> ActiveAsync(Channel<JsonElement> channel)
    { JsonElement message; do { message = await NextAsync(channel); } while (message.GetProperty("status").GetString() != "active"); return message; }

    // Real ASP.NET authentication middleware, HTTP and SignalR; only the issuer/key are test-local.
    // Auth0 setup and secrets in the production app are never changed.
    private sealed class GameApplication(string connectionString, SymmetricSecurityKey? sharedKey = null) : WebApplicationFactory<Program>
    {
        private readonly SymmetricSecurityKey key = sharedKey ?? new(RandomNumberGenerator.GetBytes(64));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // Host settings are available during the minimal entry point, before Build().
            builder.UseSetting("ConnectionStrings:GameDatabase", connectionString);
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            builder.UseSetting("Authentication:Authority", "https://identity.test/");
            builder.UseSetting("Authentication:Audience", "phase2b-tests");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:GameDatabase"] = connectionString,
                ["Cors:AllowedOrigins:0"] = "http://localhost:5173",
                ["Authentication:Authority"] = "https://identity.test/",
                ["Authentication:Audience"] = "phase2b-tests"
            }));
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration { Issuer = "https://identity.test/" };
                configuration.SigningKeys.Add(key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = configuration.Issuer,
                    ValidateAudience = true, ValidAudience = "phase2b-tests",
                    ValidateLifetime = true, RequireSignedTokens = true, ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key, ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                };
            }));
        }
        private string Token(string subject,bool admin=false,string? role=null) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer: "https://identity.test/", audience: "phase2b-tests", claims: Claims(subject,admin,role),
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new(key, SecurityAlgorithms.HmacSha256)));
        private static IEnumerable<Claim> Claims(string subject,bool admin,string? role)
        {
            yield return new Claim("sub",subject);if(admin)yield return new Claim("permissions","admin:game");if(role is not null)yield return new Claim("role",role);
        }
        public HttpClient Player(string subject,bool admin=false,string? role=null)
        {
            var client = CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(subject,admin,role)); return client;
        }
        public HubConnection Hub(string subject)
        {
            // LongPolling runs through TestServer; no external listening ports or Auth0 calls.
            using var client = CreateClient();
            return new HubConnectionBuilder().WithUrl(new Uri(client.BaseAddress!, "/hubs/game"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(Token(subject));
            }).Build();
        }
    }
}
