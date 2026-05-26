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

Use the build-and-deploy helper:

```powershell
cd E:\proxyswitch
.\tools\proxifyre\build-and-deploy.ps1 -RestartService
```

This builds `ProxiFyre/socksify.sln` (Release|x64), copies outputs to `backend/proxifyre`, and restarts `ProxiFyreService`.

If the service is running and you do not want to restart it, omit `-RestartService`:

```powershell
.\tools\proxifyre\build-and-deploy.ps1
```

The script exits non-zero if required files (`ProxiFyre.exe`, `socksify.dll`, `ProxiFyre.exe.config`, `NLog.config`) fail to copy.

Manual verification after deploy:

```powershell
Get-ChildItem E:\proxyswitch\backend\proxifyre\ProxiFyre.exe,E:\proxyswitch\backend\proxifyre\socksify.dll |
  Select-Object FullName,Length,LastWriteTime

Get-Service ProxiFyreService
```

## Known Failures

| Symptom | Cause | Fix |
|---|---|---|
| `MSB3644` for `.NETFramework,Version=v4.7.2` | Developer Pack missing | Install .NET Framework 4.7.2 Developer Pack. |
| `fatal error C1083: gsl/gsl` | GSL headers missing | Install `ms-gsl:x64-windows` via vcpkg. Dead `$(SolutionDir)GSL\include` path was removed from `socksify.vcxproj`. |
| NuGet references missing | packages not restored | Run `nuget restore ProxiFyre\ProxiFyre.csproj -SolutionDirectory .`. |
| `App.config` missing after clean clone | `.gitignore` ignores `*.config` | Ensure `ProxiFyre/.gitignore` unignores the three files and they are committed. |
| `pwsh.exe` not recognized during vcpkg applocal | PowerShell 7 missing | MSBuild may fallback to Windows PowerShell; install PowerShell 7 if applocal fails hard. |
| Deploy script exits 0 despite copy failures | locked files or missing outputs not propagated | Script now fails non-zero when required files fail to copy. Use `-RestartService` to stop/start the service during deploy. |

## Current Verification Snapshot

As of 2026-05-26:

- ProxySwitch builds successfully with `dotnet build`.
- Monorepo ProxiFyre builds successfully with MSBuild (Release|x64) when dependencies are restored.
- `BuildConfigJson()` emits all configured proxy endpoints, including `127.0.0.1:10608` with empty `appNames`.
- `tools/proxifyre/build-and-deploy.ps1` automates deploy and fails non-zero when required copies fail.
- Clean-clone reproducibility depends on `ProxiFyre/ProxiFyre/*.config` being tracked by git.