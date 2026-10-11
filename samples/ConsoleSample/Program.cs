using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PulseSharp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSingleton<ReceiptPrinter>();

builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();
    pulse.Node = "till-1";
    pulse.MaxAttempts = 2;
    pulse.RetryDelay = TimeSpan.FromMilliseconds(200);

    pulse.On<SaleCompleted>("sales-log", (sale, context, _) =>
    {
        Console.WriteLine($"  [sales-log]     {context.Stream} #{context.StreamPosition}: {sale.Total:C}");
        return Task.CompletedTask;
    });
    pulse.AddHandler<SaleCompleted, PrintReceipt>();
    pulse.AddJobHandler<ExportDailyReport, ExportDailyReportHandler>();
});

using var host = builder.Build();
await host.StartAsync();

var pulse = host.Services.GetRequiredService<IPulse>();
var control = host.Services.GetRequiredService<IPulseControl>();
var printer = host.Services.GetRequiredService<ReceiptPrinter>();

Console.WriteLine("1. Three sales. The printer is out of paper, so the receipts fail.");
printer.HasPaper = false;
await pulse.Emit(new SaleStream(1), new SaleCompleted(1, 12.50m));
await pulse.Emit(new SaleStream(2), new SaleCompleted(2, 4.99m));
await pulse.Emit(new SaleStream(3), new SaleCompleted(3, 20.00m));
await Task.Delay(TimeSpan.FromSeconds(1));

Console.WriteLine();
Console.WriteLine("2. The cursors of the receipt handler:");
foreach (var cursor in await control.GetCursors("print-receipt"))
{
    Console.WriteLine($"  {cursor.Stream}: {cursor.State}, {cursor.Attempts} attempt(s)");
}

Console.WriteLine();
Console.WriteLine("3. Paper is loaded. Retry the failed receipts.");
printer.HasPaper = true;
foreach (var cursor in await control.GetCursors("print-receipt"))
{
    await control.Retry(cursor.HandlerName, cursor.Stream);
}

await Task.Delay(TimeSpan.FromMilliseconds(500));

Console.WriteLine();
Console.WriteLine("4. End of day: enqueue a job that exports the report, and watch its progress.");
var jobId = await pulse.Enqueue(new ExportDailyReport(DateOnly.FromDateTime(DateTime.Today)));
JobInfo? job;
do
{
    await Task.Delay(TimeSpan.FromMilliseconds(150));
    job = await control.GetJob(jobId);
    Console.WriteLine($"  [job] {job!.State} {job.Done}/{job.Total}");
}
while (job.State is JobState.Pending or JobState.Running);

await host.StopAsync();

public sealed record SaleCompleted(int SaleNumber, decimal Total);

public sealed record SaleStream(int SaleNumber) : StreamName
{
    public override string Value()
    {
        return $"Sale-{SaleNumber}";
    }
}

public sealed record ExportDailyReport(DateOnly Day);

public sealed class ExportDailyReportHandler : IJobHandler<ExportDailyReport>
{
    public async Task Handle(ExportDailyReport job, JobContext context, CancellationToken cancellationToken)
    {
        const int Pages = 5;
        for (var page = 1; page <= Pages; page++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            await context.Report(page, Pages, cancellationToken);
        }
    }
}

public sealed class ReceiptPrinter
{
    public bool HasPaper { get; set; } = true;
}

[HandlerName("print-receipt")]
public sealed class PrintReceipt(ReceiptPrinter printer) : IEventHandler<SaleCompleted>
{
    public Task Handle(SaleCompleted @event, EventContext context, CancellationToken cancellationToken)
    {
        if (!printer.HasPaper)
        {
            throw new InvalidOperationException("The printer is out of paper.");
        }

        Console.WriteLine($"  [print-receipt] Receipt for sale {@event.SaleNumber}: {@event.Total:C}");
        return Task.CompletedTask;
    }
}
