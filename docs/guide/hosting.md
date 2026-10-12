# Desktop, web and worker apps

PulseSharp delivers events and runs jobs in a **hosted service**, so it works in any app built on the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host). It has no dependency on ASP.NET Core.

## Web app (ASP.NET Core)

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();
    pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
});

var app = builder.Build();

app.MapPost("/sales", async (Sale sale, IPulse pulse) =>
{
    await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total));
    return Results.Accepted();
});

app.Run();
```

## Worker service

```bash
dotnet new worker -n Workers
```

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();
    pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
});

builder.Build().Run();
```

## Desktop app (WPF, WinForms, Avalonia, MAUI)

Build a host when the app starts, start it, and stop it when the app closes:

```csharp
public partial class App
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddPulseSharp(pulse =>
        {
            pulse.UseInMemory();
            pulse.Node = "till-1";
            pulse.AddHandlersFromAssembly(typeof(App).Assembly);
        });
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host!.StopAsync();
        _host.Dispose();
    }
}
```

Handlers run on background threads. To update the UI from a handler, marshal to the UI thread (`Dispatcher.InvokeAsync` in WPF, `Dispatcher.UIThread.Post` in Avalonia).

::: tip Offline-first desktop apps
With `pulse.UseSqlite("Data Source=pos.db")`, a desktop app keeps its events and jobs on disk and continues after a restart - see [Stores](/guide/stores). Device ↔ cloud sync (0.5) will then send them to the server when the device is online.
:::

## Console app

See [Getting started](/guide/getting-started).
