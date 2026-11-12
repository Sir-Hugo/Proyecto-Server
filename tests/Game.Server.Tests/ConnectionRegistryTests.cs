using Game.Server.Administration;
using Microsoft.Extensions.Configuration;

namespace Game.Server.Tests;

public sealed class ConnectionRegistryTests
{
    [Fact]
    public void EnforcesMaximumConnectionsPerUserAndReleasesDisconnectedSlots()
    {
        IConfiguration configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["RateLimiting:Hub:MaximumConnectionsPerUser"]="2"
        }).Build();
        var registry=new ConnectionRegistry(configuration);
        Assert.True(registry.TryAdd("alice-1","auth0|alice"));
        Assert.True(registry.TryAdd("alice-2","auth0|alice"));
        Assert.False(registry.TryAdd("alice-3","auth0|alice"));
        Assert.True(registry.TryAdd("bob-1","auth0|bob"));
        registry.Remove("alice-1");
        Assert.True(registry.TryAdd("alice-3","auth0|alice"));
        Assert.Equal(3,registry.List().Count);
    }
}
