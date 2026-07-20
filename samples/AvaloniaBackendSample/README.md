# Avalonia backend authoring sample

This sample references `HtmlML.Backend.Abstractions` and
`HtmlML.Backend.Avalonia` directly. It mounts the public backend contract, verifies
required capabilities, creates persistent backend nodes, attaches them to the backend
root, and arranges them without involving the compatibility facade.

```sh
dotnet run --project samples/AvaloniaBackendSample -c Release
```
