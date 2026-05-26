# Local Compile Guide

This file documents the local build and deploy path for ProxySwitch and the integrated ProxiFyre fork.

## Toolchain Paths

| Tool | Local path | Purpose |
|---|---|---|
| dotnet | `C:\Users\MUSHI\AppData\Local\Microsoft\dotnet\dotnet.exe` | Build ProxySwitch. |
| MSBuild | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe` | Build ProxiFyre solution. |
| vcpkg | `C:\vcpkg` or local configured vcpkg | C++ dependencies for ProxiFyre. |
| nuget.exe | PATH or local download | Restore ProxiFyre .NET Framework packages. |

## Required Components

ProxySwitch:
- .NET 8 SDK for build.
- .NET 8 Runtime for run.

ProxiFyre:
- Visual Studio 2022 Build Tools.
- C++ desktop workload with C++/CLI support.
- .NET Framework 4.7.2 Developer Pack.
- Windows 10/11 SDK.
- vcpkg packages: `boost-pool:x64-windows`, `ms-gsl:x64-windows`.
- NuGet packages from `ProxiFyre/ProxiFyre/packages.config`.
- Windows Packet Filter driver for runtime traffic capture.

## Restore Dependencies

vcpkg:

```powershell
$env:HTTP_PROXY = 'http://127.0.0.1:10708'
$env:HTTPS_PROXY = 'http://127.0.0.1:10708'
C:\vcpkg\vcpkg.exe install boost-pool:x64-windows ms-gsl:x64-windows
```

NuGet:

```powershell
cd E:\proxyswitch\ProxiFyre
nuget restore ProxiFyre\ProxiFyre.csproj -SolutionDirectory .
```

If `nuget.exe` is not available, install it or use Visual Studio restore. The expected packages include Topshelf, Newtonsoft.Json, NLog, and System.Runtime.InteropServices.RuntimeInformation.

## Build Commands

ProxySwitch debug build:

```powershell
cd E:\proxyswitch
& 'C:\Users\MUSHI\AppData\Local\Microsoft\dotnet\dotnet.exe' build src\ProxySwitch\ProxySwitch.csproj
```

ProxySwitch publish:

```powershell
cd E:\proxyswitch\src\ProxySwitch
& 'C:\Users\MUSHI\AppData\Local\Microsoft\dotnet\dotnet.exe' publish `
  -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

ProxiFyre release build:

```powershell
cd E:\proxyswitch\ProxiFyre
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' `
  socksify.sln /m /p:Configuration=Release /p:Platform=x64
```

Expected ProxiFyre outputs:

```text
E:\proxyswitch\ProxiFyre\bin\exe\x64\Release\ProxiFyre.exe
E:\proxyswitch\ProxiFyre\bin\dll\x64\Release\socksify.dll
```

## Deploy ProxiFyre Runtime

ProxySwitch currently reads runtime ProxiFyre from:

```text
E:\proxyswitch\backend\proxifyre\ProxiFyre.exe
E:\proxyswitch\backend\proxifyre\app-config.json
```

After a successful ProxiFyre build, deploy the generated executable, DLL, config files, and dependencies into `backend/proxifyre`. Then restart `ProxiFyreService` if the service is installed.

Minimal verification:

```powershell
Get-ChildItem E:\proxyswitch\backend\proxifyre\ProxiFyre.exe,E:\proxyswitch\backend\proxifyre\socksify.dll |
  Select-Object FullName,Length,LastWriteTime

Get-Service ProxiFyreService
```

## Known Failures

| Symptom | Cause | Fix |
|---|---|---|
| `MSB3644` for `.NETFramework,Version=v4.7.2` | Developer Pack missing | Install .NET Framework 4.7.2 Developer Pack. |
| `fatal error C1083: gsl/gsl` | GSL headers missing | Install `ms-gsl:x64-windows` or vendor `ProxiFyre/GSL`. |
| NuGet references missing | packages not restored | Run `nuget restore ProxiFyre\ProxiFyre.csproj -SolutionDirectory .`. |
| `App.config` missing | incomplete ProxiFyre source copy | Restore `App.config`, `NLog.config`, and `packages.config` into `ProxiFyre/ProxiFyre/`. |
| `pwsh.exe` not recognized during vcpkg applocal | PowerShell 7 missing | MSBuild may fallback to Windows PowerShell; install PowerShell 7 if applocal fails hard. |
| Runtime still lacks IPC | old `backend/proxifyre` binaries | Deploy current ProxiFyre build outputs and restart service. |
| Store App IPC returns `proxy endpoint not found` | endpoint absent from `app-config.json` | Emit all configured proxy endpoints, even with empty `appNames`. |

## Current Verification Snapshot

As of 2026-05-26:

- ProxySwitch builds successfully with `dotnet build`.
- Monorepo ProxiFyre build is blocked by missing project files unless restored.
- Runtime backend binaries under `backend/proxifyre` may still be older than monorepo source outputs.
- `app-config.json` must include endpoints needed by IPC, especially `127.0.0.1:10608` for Store Codex scenarios.