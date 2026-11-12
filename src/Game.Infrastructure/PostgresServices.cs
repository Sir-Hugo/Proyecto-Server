using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Game.Infrastructure;

public static class PostgresServices
{
    public static IServiceCollection AddGamePostgres(this IServiceCollection services, IConfiguration configuration)
    {
        string? connectionString = configuration.GetConnectionString("GameDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:GameDatabase is required. Apply migrations explicitly before starting the server.");
        services.AddSingleton(_ => CreateDataSource(connectionString));
        services.AddSingleton<SchemaMigrator>();
        services.AddSingleton<PostgresMatchStore>();
        services.AddSingleton<PostgresGameManagementStore>();
        services.AddSingleton<IMatchSessionStore, PostgresMatchSessionStore>();
        return services;
    }

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(connectionString)
            {
                IncludeErrorDetail = false,
                PersistSecurityInfo = false,
                GssEncryptionMode = GssEncryptionMode.Disable,
                SearchPath = "public"
            };
            // No parameter logging or provider logger is enabled.
            return new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
        }
        catch (ArgumentException)
        {
            throw new PersistenceException("configure connection");
        }
    }
}
