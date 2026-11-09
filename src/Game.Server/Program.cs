using Game.Server.Auth;
using Game.Server.Hubs;
using Game.Server.Rooms;
using Game.Domain;
using Microsoft.AspNetCore.Builder;
// Utilizamos Scalar.AspNetCore para soporte de OpenAPI y que los errores sigan RFC 9457 Problem Details
using Scalar.AspNetCore; 

var builder = WebApplication.CreateBuilder(args);
const string webClientCorsPolicy = "WebClient";
string[] allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

if (allowedOrigins.Length == 0)
{
    throw new InvalidOperationException(
        "Configure at least one trusted web origin in Cors:AllowedOrigins.");
}

builder.Services.AddProblemDetails();
builder.Services.AddGameAuthentication(builder.Configuration);
builder.Services.AddSingleton<ICriticalHitSource, SecureCriticalHitSource>();
builder.Services.AddSingleton<RoundResolver>();
builder.Services.AddSingleton<MatchRoundService>();
// Agregamos servicios de CORS para permitir solicitudes desde el cliente web
builder.Services.AddCors(options =>
{
    options.AddPolicy(webClientCorsPolicy, policy =>
    {
        policy // Solo se permiten solicitudes desde los orígenes confiables configurados
            .WithOrigins(allowedOrigins)
            .WithMethods("GET", "POST", "PUT", "DELETE")
            .AllowAnyHeader()
            .AllowCredentials();
    });
});
builder.Services.AddSingleton<InMemoryRoomService>();
builder.Services.AddSingleton<MatchmakingService>();
builder.Services.AddHostedService<RoomExpirationService>();
builder.Services.AddHostedService<MatchmakingExpirationService>();
builder.Services.AddHostedService<MatchExpirationService>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 8 * 1024;
});

builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1;
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
WebSocketOptions webSocketOptions = new();
foreach (string origin in allowedOrigins)
{
    webSocketOptions.AllowedOrigins.Add(origin);
}

app.UseWebSockets(webSocketOptions);
app.UseCors(webClientCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options.WithTitle("Game Server API"));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithSummary("Comprueba que el servidor está respondiendo.")
    .WithName("HealthCheck");

app.MapRoomEndpoints();
app.MapHub<GameHub>("/hubs/game").RequireAuthorization();

app.Run();
