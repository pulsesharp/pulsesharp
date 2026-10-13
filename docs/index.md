---
layout: home

hero:
  name: PulseSharp
  text: Events and jobs for .NET
  tagline: Lightweight and durable - from the device to the cloud. Write a handler once, run it in a desktop app or on a server farm.
  image:
    light: /mark-on-light.svg
    dark: /mark-on-dark.svg
    alt: PulseSharp
  actions:
    - theme: brand
      text: Get started
      link: /guide/getting-started
    - theme: alt
      text: Why PulseSharp?
      link: /guide/introduction
    - theme: alt
      text: GitHub
      link: https://github.com/pulsesharp/pulsesharp

features:
  - icon: 🪶
    title: Lightweight
    details: The contracts package has zero dependencies. The engine needs only the Microsoft.Extensions abstractions. Add only what you use.
  - icon: 🔁
    title: Durable events
    details: Events are saved first, then delivered to every handler - in order per stream, with retries, and one failing stream never blocks the rest.
  - icon: ⏱️
    title: Background jobs
    details: Enqueue work now or later. Retries, progress reports, cancellation and leases that recover jobs from a stopped app instance.
  - icon: 🎛️
    title: Full control
    details: See every handler's cursor and every job. Retry, skip, rewind, cancel - from code or, soon, from the Monitor UI.
  - icon: 🖥️
    title: Any app
    details: Desktop, web, worker or console - anything that uses the .NET Generic Host. No ASP.NET Core in the core packages. Native AOT friendly.
  - icon: ☁️
    title: Device to cloud
    details: Built for offline-first apps. Events carry a global id and their origin node, ready for device ↔ cloud sync (coming in 0.5).
---

<div class="vp-doc" style="max-width: 1152px; margin: 64px auto 0; padding: 0 24px;">

## A quick look

```bash
dotnet add package PulseSharp --prerelease
```

```csharp
builder.Services.AddPulseSharp(pulse =>
{
    pulse.UseInMemory();
    pulse.On<SaleCompleted>("print-receipt", async (sale, context, ct) => await PrintReceipt(sale, ct));
    pulse.OnJob<ExportReport>(async (job, context, ct) => await Export(job, context, ct));
});

await pulse.Emit(new SaleStream(sale.Id), new SaleCompleted(sale.Id, sale.Total));
await pulse.Enqueue(new ExportReport(today));
```

::: warning Preview
PulseSharp is in preview. Events and jobs work with the in-memory, SQLite and PostgreSQL stores today; device-to-cloud sync is next on the [roadmap](/concepts/vision#roadmap). The API may change before 1.0.
:::

</div>
