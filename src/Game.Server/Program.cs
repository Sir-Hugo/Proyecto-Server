using Game.Domain;
using Game.Infrastructure;
using Game.Server.Auth;
using Game.Server.Health;
using Game.Server.Hubs;
using Game.Server.Rooms;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
bool production = builder.Environment.IsProduction();
string? authority = builder.Configuration["Authentication:Authority"];
string? audience = builder.Configuration["Authentication:Audience"];
string? auth0ClientId = builder.Configuration["Auth0:ClientId"];
if (production && (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(auth0ClientId)))
    throw new InvalidOperationException("Production requires Authentication:Authority, Authentication:Audience and Auth0:ClientId.");

builder.Services.AddGamePostgres(builder.Configuration);
const string webClientCorsPolicy = "WebClient";
string[] allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length == 0)
    throw new InvalidOperationException("Configure at least one trusted web origin in Cors:AllowedOrigins.");

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddCheck<PostgresReadinessHealthCheck>("postgresql", tags: ["ready"]);
builder.Services.AddGameAuthentication(builder.Configuration);
builder.Services.AddSingleton<ICriticalHitSource, SecureCriticalHitSource>();
builder.Services.AddSingleton<RoundResolver>();
builder.Services.AddSingleton<MatchRoundService>();
builder.Services.AddCors(options => options.AddPolicy(webClientCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins).WithMethods("GET", "POST", "PUT", "DELETE").AllowAnyHeader().AllowCredentials()));
builder.Services.AddSingleton<InMemoryRoomService>();
builder.Services.AddSingleton<MatchmakingService>();
builder.Services.AddHostedService<RoomExpirationService>();
builder.Services.AddHostedService<MatchmakingExpirationService>();
builder.Services.AddHostedService<MatchExpirationService>();
builder.Services.AddHostedService<OutboxDeliveryService>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 8 * 1024;
    options.KeepAliveInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("SignalR:KeepAliveSeconds", 15));
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(builder.Configuration.GetValue("SignalR:ClientTimeoutSeconds", 45));
});
builder.Services.AddOpenApi(options =>
{
    options.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1;
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

var app = builder.Build();
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);
if (production) app.UseHttpsRedirection();
app.UseExceptionHandler();
app.UseStatusCodePages();
WebSocketOptions webSocketOptions = new();
foreach (string origin in allowedOrigins) webSocketOptions.AllowedOrigins.Add(origin);
app.UseWebSockets(webSocketOptions);
app.UseCors(webClientCorsPolicy);
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapScalarApiReference("/docs", options => options.WithTitle("Game Server API"));
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/health", () => Results.Redirect("/health/ready", permanent: false)).ExcludeFromDescription();
app.MapGet("/config/client.json", (IConfiguration configuration) => Results.Ok(new
{
    auth0Domain = new Uri(configuration["Authentication:Authority"]!).Host,
    auth0ClientId = configuration["Auth0:ClientId"],
    auth0Audience = configuration["Authentication:Audience"]
})).AllowAnonymous().ExcludeFromDescription();
app.MapRoomEndpoints();
app.MapHub<GameHub>("/hubs/game").RequireAuthorization();
app.MapFallback(async context =>
{
    var accepts = context.Request.GetTypedHeaders().Accept;
    bool wantsHtml = accepts is not null && accepts.Any(value =>
        value.MediaType.Value?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true);
    if (!HttpMethods.IsGet(context.Request.Method) || !wantsHtml)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    string index = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
    if (!File.Exists(index)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});

app.Run();
public partial class Program;
