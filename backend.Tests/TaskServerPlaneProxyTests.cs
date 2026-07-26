using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskServerPlaneProxyTests
{
    [Fact]
    public void Standalone_base_url_disables_every_local_v1_owner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071",
            })
            .Build();

        Assert.False(EndpointMapping.MapsLocalV1(configuration));
        Assert.True(TaskServerPlaneProxy.IsConfigured(configuration));
    }

    [Fact]
    public void Proxy_maps_one_catch_all_v1_surface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["TaskServer:BaseUrl"] = "http://127.0.0.1:5071";
        builder.Services.AddTaskServerPlaneProxy(builder.Configuration);
        var app = builder.Build();

        Assert.True(app.MapTaskServerPlaneProxy());

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        Assert.Equal(["/api/v1/{**path}"], routes);
    }
}
