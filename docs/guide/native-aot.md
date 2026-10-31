# Trimming and Native AOT

PulseSharp itself is built and checked with the trimming and Native AOT analyzers.

## Registration

Every way to add a handler names its types in your code, so nothing is found by reflection at runtime:

```csharp
pulse.On<SaleCompleted>("sales-log", HandleSale);
pulse.AddHandler<SaleCompleted, UpdateStock>();
pulse.OnJob<ExportReport>(RunExport);
pulse.AddJobHandler<ExportReport, ExportReportHandler>();
```

## JSON

Events and jobs are stored as JSON. In a trimmed or Native AOT app, give PulseSharp a source-generated `JsonSerializerContext` through `pulse.Advanced.JsonOptions` - see [How data is stored](/guide/stores#how-data-is-stored).

## Names

`[EventName]`, `[JobName]` and `[HandlerName]` are read from your types; keep them on the types you register explicitly, and the trimmer keeps them too.
