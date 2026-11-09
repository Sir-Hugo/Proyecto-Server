using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Game.Server.Auth;

public static class GameAuthenticationExtensions
{
    public static IServiceCollection AddGameAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Para Auth0, Authority es el dominio del tenant Auth0, con esquema HTTPS y `/` al final
        // mientras que Audience es el identificador de la API registrado en Auth0
        string? authority = configuration["Authentication:Authority"];
        string? audience = configuration["Authentication:Audience"];

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.RequireHttpsMetadata = true;
                options.MapInboundClaims = false;
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Browser WebSocket clients may need to send the access token in the query string.
                        string? accessToken = context.Request.Query["access_token"];
                        PathString hubPath = context.HttpContext.Request.Path;

                        if (!string.IsNullOrEmpty(accessToken) &&
                            // Aceptar únicamente para la ruta del hub de SignalR; nunca para endpoints HTTP ordinarios.
                            hubPath.StartsWithSegments("/hubs/game"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization();
        return services;
    }
}
