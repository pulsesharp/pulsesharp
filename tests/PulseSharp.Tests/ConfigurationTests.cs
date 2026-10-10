using Microsoft.Extensions.DependencyInjection;

namespace PulseSharp.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void RefusesTwoInlineHandlersWithTheSameName()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Configure(pulse =>
        {
            pulse.On<SaleCompleted>("receipts", (_, _, _) => Task.CompletedTask);
            pulse.On<SaleRefunded>("receipts", (_, _, _) => Task.CompletedTask);
        }));

        Assert.Contains("'receipts' is used twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTwoHandlerClassesWithTheSameName()
    {
        Assert.Throws<InvalidOperationException>(() => Configure(pulse =>
        {
            pulse.AddHandler<SaleCompleted, FirstStockHandler>();
            pulse.AddHandler<SaleCompleted, SecondStockHandler>();
        }));
    }

    [Fact]
    public void RefusesTwoEventTypesWithTheSameName()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Configure(pulse =>
        {
            pulse.On<SaleCompleted>("first", (_, _, _) => Task.CompletedTask);
            pulse.On<Other.SaleCompleted>("second", (_, _, _) => Task.CompletedTask);
        }));

        Assert.Contains("[EventName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsTheSameHandlerClassRegisteredTwice()
    {
        var services = Configure(pulse =>
        {
            pulse.AddHandler<SaleCompleted, FirstStockHandler>();
            pulse.AddHandler<SaleCompleted, FirstStockHandler>();
        });

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IPulse));
    }

    [Fact]
    public void RefusesAHandlerClassWithoutEventHandlers()
    {
        Assert.Throws<InvalidOperationException>(() => Configure(pulse => pulse.AddHandler<NotAHandler>()));
    }

    [Fact]
    public void RequiresAStore()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddPulseSharp(_ => { })
        );

        Assert.Contains("UseInMemory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesBeingAddedTwice()
    {
        var services = Configure(_ => { });

        Assert.Throws<InvalidOperationException>(() => services.AddPulseSharp(pulse => pulse.UseInMemory()));
    }

    [Fact]
    public void RefusesAnEmptyNode()
    {
        Assert.Throws<InvalidOperationException>(() => Configure(pulse => pulse.Node = " "));
    }

    [Fact]
    public void RefusesFewerThanOneAttempt()
    {
        Assert.Throws<InvalidOperationException>(() => Configure(pulse => pulse.MaxAttempts = 0));
    }

    [Fact]
    public void RefusesANonPositiveRetryDelay()
    {
        Assert.Throws<InvalidOperationException>(() => Configure(pulse => pulse.RetryDelay = TimeSpan.Zero));
    }

    [Fact]
    public void RefusesAnEmptyHandlerName()
    {
        Assert.Throws<ArgumentException>(() => Configure(pulse => pulse.On<SaleCompleted>(" ", (_, _, _) => Task.CompletedTask)));
    }

    private static IServiceCollection Configure(Action<PulseSharpBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            configure(pulse);
        });
        return services;
    }

    [HandlerName("stock")]
    private sealed class FirstStockHandler : IEventHandler<SaleCompleted>
    {
        public Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    [HandlerName("stock")]
    private sealed class SecondStockHandler : IEventHandler<SaleCompleted>
    {
        public Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NotAHandler;
}
