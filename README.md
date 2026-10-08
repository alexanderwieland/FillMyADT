# FillMyADT

A Windows desktop app (.NET 10, WPF host with BlazorWebView) that rebuilds a work day from local activity and turns it into time slots for the ADT (Arbeitsdokutool).

## Event sources

| Source | What it reads |
|---|---|
| Windows Events | System event log (Kernel-Power / Kernel-General / Power-Troubleshooter) to work out work start, lunch break and work end |
| Outlook Calendar | The day's meetings, via Outlook COM interop |
| Git History | Commits and branch switches (reflog) from configured or auto-discovered repositories |
| Edge History | Visited pages from the Microsoft Edge history database |

Each source can be turned on or off in the app's settings. Results are merged in priority order (Windows, Outlook, Git, others) and converted into time slots.

## Requirements

- Windows
- .NET 10 SDK
- Git CLI on `PATH` (for the Git source)
- Microsoft Outlook (optional, for the calendar source)

## Build & run

```powershell
dotnet build FillMyADT.slnx
dotnet run --project FillMyADT\FillMyADT.csproj
```

## Logs

Serilog writes logs to `%LocalAppData%\FillMyADT\Logs\log-*.txt`, including how long each source takes.
