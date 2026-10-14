# Trimming and Native AOT

PulseSharp itself is built and checked with the trimming and Native AOT analyzers. Whether your app is AOT-ready depends on how you register handlers.

## AOT-safe registration

These methods use no reflection:

```csharp
pulse.On<SaleCompleted>("sales-log", HandleSale);
pulse.AddHandler<SaleCompleted, UpdateStock>();
pulse.OnJob<ExportReport>(RunExport);
pulse.AddJobHandler<ExportReport, ExportReportHandler>();
```

## Reflection-based registration

These find types at runtime, which trimming can't see:

```csharp
pulse.AddHandler<UpdateStock>();
pulse.AddHandlersFromAssembly(typeof(Program).Assembly);
```

They are marked `[RequiresDynamicCode]` and `[RequiresUnreferencedCode]`, so the compiler warns you when you publish with `PublishAot` or `PublishTrimmed`. Use the explicit methods in those apps.

## JSON

Events and jobs are stored as JSON. In a trimmed or Native AOT app, give PulseSharp a source-generated `JsonSerializerContext` through `pulse.Advanced.JsonOptions` - see [How data is stored](/guide/stores#how-data-is-stored).

## Names

`[EventName]`, `[JobName]` and `[HandlerName]` are read from your types; keep them on the types you register explicitly, and the trimmer keeps them too.
