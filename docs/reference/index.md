# API reference

The reference is generated from the XML documentation in the source code, so it always matches the published packages.

| Package | Contents |
|---|---|
| [PulseSharp.Abstractions](/reference/abstractions) | `IPulse`, handler interfaces, contexts, stream names, options, attributes. Reference it from libraries. |
| [PulseSharp](/reference/core) | `AddPulseSharp`, `PulseSharpBuilder`, `IPulseControl` and its types. Reference it from the app. |

Everything public lives in the `PulseSharp` namespace, except `AddPulseSharp`, which is in `Microsoft.Extensions.DependencyInjection` so it shows up next to your other registrations.
