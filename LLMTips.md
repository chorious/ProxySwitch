# LLMTips — ProxySwitch 项目速查

> 任何涉及 ProxySwitch 的改动，**先读这个文件**。它提供架构上下文、关键组件职责和数据流，帮你避免在代码海里迷路。

---

## 1. 项目总览

ProxySwitch 是一个 Windows 托盘应用，核心目标：**让用户在不修改系统代理的前提下，为不同应用指定不同代理（或直连）**。

### 两大组件

| 组件 | 技术栈 | 角色 |
|---|---|---|
| **ProxySwitch** | .NET 8 WinForms | 用户桌面进程：托盘 UI、Dashboard、配置管理、应用启动器、session 监控 |
| **ProxiFyre** | C++/CLI + C# (.NET Framework 4.7.2) | Windows Service：NDISAPI 包过滤，透明 SOCKS5 代理转发 |

两者通过 **named-pipe IPC** 通信（`\\.\pipe\ProxySwitch.ProxiFyre.SessionRoute`）。ProxiFyre 是 [wiresock/proxifyre](https://github.com/wiresock/proxifyre) 的 fork，AGPL-3.0 授权。

---

## 2. 仓库目录地图

```
proxyswitch/
├── src/ProxySwitch/              # ProxySwitch 主程序（MIT）
│   ├── Program.cs                # 入口，全局异常捕获
│   ├── MainForm.cs               # 托盘图标、全局菜单、Dashboard 宿主
│   ├── DashboardForm.cs          # 主面板：Drop Zone、Sessions、Events
│   ├── SettingsForm.cs           # 配置编辑器（SplitContainer 左 rail）
│   ├── Services/                 # 核心业务逻辑
│   │   ├── SessionManager.cs     # session 生命周期（创建、监控、销毁）
│   │   ├── AppLauncher.cs        # 启动浏览器 / 通用 exe / Store App
│   │   ├── ProcessMonitor.cs     # 进程树追踪、WMI 查询
│   │   ├── ProxiFyreBackend.cs   # ProxiFyre 配置管理（读写 app-config.json）
│   │   ├── ProxiFyreIpcClient.cs # Named-pipe IPC 客户端（v1.0+）
│   │   ├── AppIdentityResolver.cs# AUMID → exe 路径 / 包家族名 解析（v0.9+）
│   │   ├── SessionSupervisor.cs  # grace window 重连逻辑（v0.9+）
│   │   ├── ExternalProcessWatcher.cs # 外部启动进程自动 attach（v0.7+）
│   │   ├── HandoffScorer.cs      # Correlated handoff 启发式打分（v0.4.2+）
│   │   ├── EventStore.cs         # 事件流（Dashboard Events 控制台数据源）
│   │   ├── Logger.cs             # 文件日志
│   │   └── PortMonitor.cs        # 代理端口存活检测
│   ├── Models/                   # 数据模型
│   │   ├── LaunchSession.cs      # session 状态（进程列表、路由状态、UI 状态）
│   │   ├── LaunchTarget.cs       # 启动目标配置
│   │   ├── TrackedProcess.cs     # 单个被追踪进程
│   │   ├── ProcessSnapshot.cs    # 进程快照（handoff baseline/after 对比用）
│   │   ├── ProxyConfig.cs        # 配置读写（proxyswitch.json）
│   │   └── ProxyRuntimeStatus.cs # 代理端口实时状态
│   ├── UI/                       # 可复用 UI 组件
│   │   ├── Theme.cs              # Theme tokens（颜色/字体/圆角）
│   │   ├── IconRenderer.cs       # 图标渲染（GDI 对象缓存）
│   │   ├── PillButton.cs         # 裁角按钮（Region 裁角）
│   │   └── TopTabStrip.cs        # 顶部 tab 导航
│   ├── Controls/                 # 复合控件
│   │   └── LaunchZoneControl.cs  # Drop Zone 拖放区域
│   └── Dialogs/                  # 对话框
│       ├── LaunchConfirmDialog.cs
│       ├── RouteChildDialog.cs
│       ├── CorrelatedHandoffDialog.cs
│       └── StoreAppPickerDialog.cs
├── ProxiFyre/                    # ProxiFyre fork 源码（AGPL-3.0）
│   ├── ProxiFyre/                # C# service（Topshelf）
│   │   ├── Program.cs            # Service 入口
│   │   └── SessionRouteServer.cs # Named-pipe IPC server（v1.0+）
│   ├── socksify/                 # C++/CLI 包装层
│   │   ├── Socksifier.cpp/.h     # 托管/非托管桥接
│   │   └── socksify_unmanaged.cpp/.h # 纯 C++ 入口
│   └── netlib/src/proxy/         # C++ 核心网络库
│       └── socks_local_router.h  # PID 路由表、包过滤逻辑（v1.0+ 关键改动）
├── src/ProxiFyre/                # ProxiFyre 文档（README + netlib-README）
├── config/                       # 用户配置（proxyswitch.json）
├── backend/                      # 运行时二进制（ProxiFyre.exe、app-config.json）
├── plan&review/                  # 历史 plan 和 review 文件（重要！）
│   ├── old_plan/                 # 各版本实现计划
│   ├── review/                   # Opus/GPT review 结果
│   └── check_list/               # 测试清单和报告
├── README.md                     # 项目介绍、用法、许可
├── changeLog.md                  # 版本历史（问题→方案）
├── localCompile.md               # 本机编译环境配置
└── LLMTips.md                    # 本文件
```

---

## 3. 核心数据流

### 3.1 应用启动流程

```
用户拖放 exe 到 Dashboard Drop Zone
  │
  ▼
DashboardForm → SessionManager.LaunchTarget()
  │
  ├── 浏览器模式 ──────────────────────┐
  │   AppLauncher.LaunchBrowser()      │
  │   带 --proxy-server 或 --no-proxy  │
  │   启动 Chrome                      │
  │                                    │
  ├── 通用 exe 模式 ───────────────────┤
  │   AppLauncher.LaunchGenericApp()   │
  │   启动 exe                         │
  │   ├─ ProxiFyre enabled → EnsureRoute()
  │   │   写 backend/proxifyre/app-config.json
  │   │   （可能需要 UAC 重启 service）
  │   └─ ProxiFyre disabled → External router
  │                                    │
  └── Store App 模式 ──────────────────┘
      AppLauncher.LaunchStoreApp()
      IApplicationActivationManager.ActivateApplication()
      ├─ v1.0+ IPC available:
      │   ProxiFyreIpcClient.CreateSessionAsync()
      │   ProxiFyreIpcClient.AddPidAsync(rootPid)
      │   status = proxifyre-route-active
      └─ IPC unavailable → IpcUnavailable 报错
  │
  ▼
SessionManager 创建 LaunchSession
加入 _sessions 列表 → SessionsChanged 事件
Dashboard 刷新 DataGridView
```

### 3.2 Session 生命周期监控

```
ProcessMonitor 定时轮询（默认 2s）
  │
  ▼
检查每个 session 的根进程和后代进程
  │
  ├── 根进程存活 → 继续监控
  │
  ├── 根进程退出，有后代 ───────────┐
  │   → 状态变为 "Running via child"  │
  │   → Dashboard 显示 Route Child 按钮│
  │                                    │
  ├── 根进程退出，无后代 ────────────┤
  │   → 进入 grace window（20s）       │
  │   SessionSupervisor 等待重连       │
  │   ├─ 重连成功 → reattach           │
  │   └─ 超时 → 标 exited，清理路由    │
  │                                    │
  └── Correlated Handoff ────────────┘
      拍 baseline/after 快照
      HandoffScorer 启发式打分
      ≥70 自动 attach / 40-69 弹窗 / <40 忽略
```

### 3.3 ProxiFyre 包过滤路径（C++ 侧）

```
NDISAPI 捕获到出站 TCP/UDP 包
  │
  ▼
socks_local_router::get_proxy_port_tcp/udp()
  │
  ▼
has_pid_route(pid)? ──Yes──▶ 查 pid_to_proxy_ 表
  │                           creation_time 校验
  │                           → 返回对应 SOCKS5 endpoint
  │
  No
  │
  ▼
bypass? ──Yes──▶ 直接放行
  │
  No
  ▼
proxy_to_names_（legacy appNames 匹配）
```

---

## 4. 关键组件详解

### 4.1 SessionManager

**职责：** session 的完整生命周期管理。

**核心方法：**
- `LaunchBrowser()` — 启动浏览器，session 状态直接 `running`
- `LaunchTarget()` — 通用启动入口，处理 IPC / config-file 路由
- `TryMergeActiveSession()` — grace window 内检测到同应用重启时 reattach
- `TryConfirmRouteFromProcess()` — 子进程 / handoff 进程确认路由（**IPC session 不走这里**）
- `DisposeSession()` — 清理：kill 进程、RemovePid、CloseSession、删 persistent route

**重要状态字段：**
- `Status` — UI 显示状态：`running`, `exited`, `waiting-for-restart`, `launching`, `restarting`
- `RoutingStatus` — 路由状态：`direct`, `browser-proxy-active`, `proxiFyre active`, `proxiFyre pending`, `proxiFyre failed`, `external router`, `proxifyre-route-session-created`, `proxifyre-route-active`

### 4.2 AppLauncher

**职责：** 实际执行进程启动。

**三种启动方式：**
1. `LaunchBrowser()` — `Process.Start()` 带 `--proxy-server`
2. `LaunchGenericApp()` — `Process.Start()` + `WorkingDirectory` 修复
3. `LaunchStoreApp()` — `IApplicationActivationManager.ActivateApplication()`（AUMID）

**注意：** Store App 启动后可能没有立刻返回 PID，需要后续通过 `AppIdentityResolver` 或进程扫描匹配。

### 4.3 ProxiFyreIpcClient

**职责：** 与 ProxiFyre service 的 named-pipe 通信。

**协议方法：**
- `CreateSessionAsync(sessionId, endpoint)` — 注册 session
- `AddPidAsync(sessionId, pid, createdAtUtc)` — 添加 PID（**createdAt 必须是 WMI 获取的真实创建时间**）
- `RemovePidAsync(sessionId, pid)` — 移除单个 PID
- `CloseSessionAsync(sessionId)` — 关闭 session，清理该 session 所有 PID

**安全：** 每个请求带 `SharedToken` GUID，ProxiFyre `SessionRouteServer` 校验。

### 4.4 AppIdentityResolver

**职责：** 把 Store App AUMID 解析成可执行信息。

**解析路径：** AUMID → `PackageManager` → `InstalledLocation` → 找 `AppxManifest.xml` → 提取 `PackageFamilyName` + `Application Id` + exe 路径。

**缓存：** 解析结果缓存在内存，避免重复查询 UWP API。

### 4.5 SessionSupervisor

**职责：** grace window 管理。

**逻辑：** launcher 退出后 session 不立刻死亡，进入 20s grace window。期间如果同一应用（按 exe 路径或 AUMID 匹配）被重新启动，自动把新进程 attach 到原 session，保留历史事件和状态。

### 4.6 socks_local_router.h（ProxiFyre C++）

**职责：** 包过滤核心，决定每个连接走哪个代理。

**v1.0 新增：**
- `pid_route_entry` 结构：`{proxy_id, FILETIME creation_time}`
- `pid_to_proxy_`: `unordered_map<DWORD, pid_route_entry>`
- `has_pid_route(pid)`: 轻量存在检查（`shared_lock`）
- `validate_pid_creation_time()`: `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `GetProcessTimes()`
- `cleanup_stale_pid_routes()`: 维护 sweep 时清理过期 PID

**重要：** `associate_process_id_to_proxy()` 中，`refresh_process_lookups()` **必须在释放 `lock_` 后调用**，避免阻塞包过滤热路径。

---

## 5. 关键设计决策

### 5.1 为什么用 named-pipe IPC 而不是改 C++/CLI 互操作层？

C++/CLI `Socksifier` 是托管/非托管桥接，改接口需要同时动 C#、C++/CLI、C++ 三层，编译和调试成本高。named-pipe 让 ProxySwitch（.NET 8）和 ProxiFyre（.NET Framework 4.7.2）解耦，各自独立编译。

### 5.2 为什么 Store App 和通用 exe 走不同路由路径？

Store App（UWP/MSIX）进程有 AUMID，可以通过 IPC 精准路由，且子进程名（`codex.exe`）容易和系统其他进程冲突，不适合全局 `appNames` 规则。

通用 exe 启动时可能还没装 ProxiFyre IPC server（旧版），所以保留 legacy config-file 路径作为 fallback。

### 5.3 为什么 IPC session 要先 `CreateSession` 再 `AddPid`？

Store App AUMID 启动有时不返回 PID（`IApplicationActivationManager` 的异步性）。如果等不到 PID 就不创建 session，后续 child/correlated 检测想 `AddPid` 时会报 `session not found`。先 `CreateSession` 确保 endpoint 预留，后续任何 PID 都可以加入。

### 5.4 PID Reuse Guard 为什么用 FILETIME 而不是 PID+时间戳？

Windows PID 是循环复用的 DWORD。`GetProcessTimes()` 返回的 `CreationTime` 是 `FILETIME`（100ns 精度），和任务管理器显示的创建时间一致，最可靠。ProxySwitch 通过 WMI `Win32_Process` 获取 `CreationDate`，转成 UTC `DateTime`，再转 `FileTimeUtc` 传给 ProxiFyre。

### 5.5 为什么 `lock_` 要 mutable？

`has_pid_route()` 是 `const` 方法（包过滤热路径频繁调用），但内部需要 `std::shared_lock<std::shared_mutex>`。C++ 要求 `const` 方法只能锁定 `mutable` 成员，否则编译错误。

---

## 6. 常见修改场景速查

| 你想改什么 | 先看哪里 | 注意 |
|---|---|---|
| 加新的路由状态 | `LaunchSession.cs` + `SessionManager.cs` | 确保 Dashboard 和 Events 都更新 |
| 改 IPC 协议 | `ProxiFyreIpcClient.cs` + `ProxiFyre/SessionRouteServer.cs` | 两边必须同步改，token 不能变 |
| 改 ProxiFyre 包过滤逻辑 | `netlib/src/proxy/socks_local_router.h` | 锁范围要窄，别阻塞热路径 |
| 改 UI 主题 | `UI/Theme.cs` | 所有颜色/字体走 token，别硬编码 |
| 改 Session 卡片行为 | `DashboardForm.cs` + `SessionManager.cs` | DataGridView 用 in-place rebind，别 reset |
| 改配置结构 | `ProxyConfig.cs` + `proxyswitch.json` | 加字段要考虑旧配置兼容性 |
| 加新的启动方式 | `AppLauncher.cs` + `SessionManager.cs` | 记得写 EventStore 事件 |

---

## 7. 调试技巧

### 看日志
- ProxySwitch: `E:\proxyswitch\logs\proxyswitch.log`
- ProxiFyre: Windows Event Log（Topshelf 默认）或 `NLog` 配置的目标

### 快速测试 IPC
```powershell
# 检查 pipe 是否存在
[System.IO.Directory]::GetFiles("\\.\\pipe\\") | Where-Object { $_ -like "*ProxiFyre*" }
```

### 检查 ProxiFyre service 状态
```powershell
Get-Service ProxiFyre
# 或
sc query ProxiFyre
```

### 验证 PID 路由表（需要 ProxiFyre 日志级别调为 Debug）
在 `ProxiFyre.exe.config` 里改 NLog 级别。

---

## 8. Review / Plan 文件索引

历史设计决策和 review 结论都在 `plan&review/`：

| 文件 | 内容 |
|---|---|
| `plan&review/review/reviewVer1.0.0.md` | v1.0 PID Routing 的 review（3 个 blocking + 2 high-priority） |
| `plan&review/old_plan/planPidVer1.0.0.md` | v1.0 实现计划 |
| `plan&review/review/opus_review_v0.8.0.md` | v0.8 UI 重构 review |
| `plan&review/review/gpt_review_v0.7.md` | v0.7 External Watcher review |
| `plan&review/summary-2026-05-26-pid-routing-implementation.md` | v1.0 实现总结 |

**如果你要改一个已有功能，先去 `plan&review/review/` 搜对应版本的 review**，里面记录了当时发现的坑和修复方案，避免重复踩坑。

---

## 9. 术语表

| 术语 | 含义 |
|---|---|
| **AUMID** | Application User Model ID，Store App 唯一标识，如 `OpenAI.Codex_...!App` |
| **Handoff** | Launcher 启动后把执行权交给另一个进程（如 Steam → game.exe） |
| **Correlated Handoff** | 非父子链的 handoff（ShellExecute/COM/UAC），通过启发式匹配 |
| **Grace Window** | Launcher 退出后的等待期（默认 20s），期间同应用重启可 reattach |
| **NDISAPI** | Windows Packet Filter API，ProxiFyre 用它来拦截/重定向网络包 |
| **PID Reuse Guard** | 用进程创建时间验证 PID 是否被操作系统复用 |
| **Routing Decoupled** | v0.5 方向调整：承认 ProxySwitch 不控制 generic app 路由，只提供 hint |
| **Session** | 一次应用启动的完整生命周期对象，包含进程列表、状态、路由信息 |
| **Store App** | UWP / MSIX 打包应用，通过 AUMID 启动 |
