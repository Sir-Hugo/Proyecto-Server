using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Game.Postgres.Tests;

[Trait("Category", "Deployment")]
public sealed class Phase3AHostTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact] public async Task LivenessDoesNotDependOnPostgresAndReadinessDoes()
    {
        await using var app=new DeploymentApplication("Host=127.0.0.1;Port=1;Database=missing;Username=none;Password=none;Timeout=1;Command Timeout=1");
        using var client=app.CreateClient(new(){AllowAutoRedirect=false});
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/health/live")).StatusCode);
        var ready=await client.GetAsync("/health/ready");Assert.Equal(HttpStatusCode.ServiceUnavailable,ready.StatusCode);
        Assert.DoesNotContain("Password",await ready.Content.ReadAsStringAsync(),StringComparison.OrdinalIgnoreCase);
    }

    [PostgresFact] public async Task ReadinessIsHealthyWithMigratedPostgres()
    {
        await using var db=await fixture.CreateDatabaseAsync();await using var app=new DeploymentApplication(db.ConnectionString);
        using var client=app.CreateClient();Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact] public async Task StaticSpaFallbackAndPublicAuthConfigurationAreServedSafely()
    {
        string root=Path.Combine(Path.GetTempPath(),"game-web-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root,"index.html"),"<!doctype html><title>phase3a-spa</title>");
        try
        {
            await using var app=new DeploymentApplication("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1",root);using var client=app.CreateClient();
            using var request=new HttpRequestMessage(HttpMethod.Get,"/battle/reconnect");request.Headers.Accept.ParseAdd("text/html");var spa=await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK,spa.StatusCode);Assert.Contains("phase3a-spa",await spa.Content.ReadAsStringAsync());
            var config=await client.GetFromJsonAsync<Dictionary<string,string>>("/config/client.json");Assert.Equal("identity.test",config!["auth0Domain"]);Assert.DoesNotContain(config.Keys,key=>key.Contains("password",StringComparison.OrdinalIgnoreCase));
            using var missing=new HttpRequestMessage(HttpMethod.Get,"/api/not-real");missing.Headers.Accept.ParseAdd("application/json");Assert.Equal(HttpStatusCode.NotFound,(await client.SendAsync(missing)).StatusCode);
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact] public void ProductionRefusesMissingRequiredPublicAuthenticationSettings()
    {
        using var app=new DeploymentApplication("Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",environment:"Production",includeClientId:false);
        var exception=Assert.ThrowsAny<Exception>(()=>app.CreateClient());Assert.Contains("Auth0:ClientId",exception.ToString());
    }

    private sealed class DeploymentApplication(string connectionString,string? webRoot=null,string environment="Testing",bool includeClientId=true):WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging=>logging.ClearProviders());
            builder.UseEnvironment(environment);if(webRoot is not null)builder.UseWebRoot(webRoot);
            builder.UseSetting("ConnectionStrings:GameDatabase",connectionString);
            builder.UseSetting("Cors:AllowedOrigins:0","https://game.test");
            builder.UseSetting("Authentication:Authority","https://identity.test/");
            builder.UseSetting("Authentication:Audience","game-api");
            if(includeClientId)builder.UseSetting("Auth0:ClientId","public-client");
            builder.ConfigureAppConfiguration((_,config)=>config.AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["ConnectionStrings:GameDatabase"]=connectionString,["Cors:AllowedOrigins:0"]="https://game.test",
                ["Authentication:Authority"]="https://identity.test/",["Authentication:Audience"]="game-api",
                ["Auth0:ClientId"]=includeClientId?"public-client":null
            }));
        }
    }
}
