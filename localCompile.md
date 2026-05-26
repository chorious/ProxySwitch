# Local Compile Guide

本机编译 ProxySwitch + ProxiFyre 的完整环境配置与命令。

## 工具链路径

| 工具 | 路径 | 用途 |
|---|---|---|
| dotnet SDK | `C:\Users\MUSHI\AppData\Local\Microsoft\dotnet\dotnet.exe` | ProxySwitch 编译 |
| MSBuild | `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe` | ProxiFyre (C++/CLI) 编译 |
| nuget.exe | 下载到 PATH 或本地 | 恢复 ProxiFyre 的 NuGet 包 |

## 环境依赖

### ProxySwitch

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### ProxiFyre

- Visual Studio 2022 BuildTools（含 C++ 工作负载）
- .NET Framework 4.7.2 Developer Pack
- [Windows Packet Filter driver](https://github.com/wiresock/ndisapi/releases)（运行时依赖，编译不需要）
- vcpkg + `boost-pool:x64-windows` + `ms-gsl:x64-windows`

## 首次配置

### 1. vcpkg（ProxiFyre C++ 依赖）

```powershell
# 设置代理（GitHub 下载需要）
$env:HTTP_PROXY = "http://127.0.0.1:10708"
$env:HTTPS_PROXY = "http://127.0.0.1:10708"

# 假设 vcpkg 已克隆到 D:\guCodex\vcpkg
D:\guCodex\vcpkg\vcpkg.exe install boost-pool:x64-windows ms-gsl:x64-windows
```

### 2. NuGet 包恢复（ProxiFyre C# 依赖）

```powershell
cd ProxiFyre
nuget restore ProxiFyre\ProxiFyre.csproj -SolutionDirectory .
```

依赖包：`Topshelf`、`Newtonsoft.Json`、`NLog`、`System.Runtime.InteropServices.RuntimeInformation`

### 3. .NET Framework 4.7.2 Developer Pack

如未安装，MSBuild 会报错 `MSB3644`。下载地址：
```
https://download.microsoft.com/download/7/1/7/71795fde-1cca-41b0-b495-00b1ab656994/NDP472-DevPack-ENU.exe
```

## 编译命令

### ProxySwitch

```powershell
cd src/ProxySwitch
& 'C:\Users\MUSHI\AppData\Local\Microsoft\dotnet\dotnet.exe' publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

输出：`src/ProxySwitch/bin/Release/net8.0-windows/win-x64/publish/ProxySwitch.exe`

### ProxiFyre

```powershell
cd ProxiFyre
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' `
  socksify.sln /m /p:Configuration=Release /p:Platform=x64
```

输出：`ProxiFyre/bin/exe/x64/Release/ProxiFyre.exe`

## 已知问题

| 问题 | 原因 | 解决 |
|---|---|---|
| `MSB3644` 找不到 .NETFramework 4.7.2 | 未装 Developer Pack | 安装 NDP472-DevPack-ENU.exe |
| `error C1083: 无法打开包括文件: "gsl/gsl"` | GSL 头文件缺失 | vcpkg install `ms-gsl:x64-windows`，确保 `<AdditionalIncludeDirectories>` 指向 vcpkg installed 目录 |
| `has_pid_route()` const 编译错误 | `shared_mutex` 非 mutable | `std::shared_mutex lock_` → `mutable std::shared_mutex lock_` |
| vcpkg 下载挂 0% | GitHub 直连不通 | 设置 `HTTP_PROXY`/`HTTPS_PROXY` 为 `127.0.0.1:10708` |
| NuGet 包找不到 | 未 restore | `nuget restore ProxiFyre\ProxiFyre.csproj -SolutionDirectory .` |

## 快速验证

```powershell
# ProxySwitch
cd src/ProxySwitch
 dotnet --version  # 应显示 8.x
dotnet build

# ProxiFyre
cd ProxiFyre
MSBuild socksify.sln /p:Configuration=Release /p:Platform=x64
```
