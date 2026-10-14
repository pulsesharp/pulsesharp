using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PulseSharp.Tests;

internal sealed class TestHost : IAsyncDisposable
{
    private readonly IHost _host;

    private TestHost(IHost host)
    {
        _host = host;
    }

    public IPulse Pulse
    {
        get
        {
            return _host.Services.GetRequiredService<IPulse>();
        }
    }

    public IPulseControl Control
    {
        get
        {
            return _host.Services.GetRequiredService<IPulseControl>();
        }
    }

    public static async Task<TestHost> Start(
        Action<PulseSharpBuilder> configure,
        Action<IServiceCollection>? registerServices = null
    )
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        registerServices?.Invoke(builder.Services);
        builder.Services.AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.Node = "test-node";
            pulse.RetryDelay = TimeSpan.FromMilliseconds(20);
            pulse.Advanced.PollInterval = TimeSpan.FromMilliseconds(50);
            configure(pulse);
        });

        var host = builder.Build();
        await host.StartAsync();
        return new TestHost(host);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
