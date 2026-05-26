# ProxySwitch

Windows 托盘应用，把应用级代理切换从命令行里抽离出来。

## 干什么用的

- 两个 Chrome 同时跑，一个直连，一个走代理
- 指定应用走指定代理（浏览器 / 编辑器 / Git）
- 不修改 Windows 系统代理
- 托盘里一键切换、一键启动

## 依赖

- Windows 10/11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)（SDK 不需要，只装 Runtime 就行）
- [ProxiFyre](src/ProxiFyre/README.md)（可选，用于透明 per-app 代理；Store App 路由在 v1.0+ 变为硬依赖）

## 下载

从 [Releases](https://github.com/chorious/ProxySwitch/releases) 下载 `ProxySwitch.exe`，单文件，双击即用。

## 用法

1. 放到 `E:\proxyswitch`（或改源码里的 `RootPath`）
2. 改 `config/proxyswitch.json` 里的路径和代理端口
3. 双击 `ProxySwitch.exe`，托盘右下角会出现图标
4. **双击托盘图标**打开 Dashboard
5. Dashboard 里：
   - **拖放 .exe** 到 Direct / 10708 / 10808 区域直接启动
   - **Pinned Apps** 一键启动常用应用
   - **Sessions** 实时看运行状态和进程数
   - **Events** 看启动、退出、代理状态变化全链路
6. 右键托盘图标也有 Quick Launch 和 Proxifier Profile

## 配置

`config/proxyswitch.json`：

```json
{
  "proxies": [
    { "id": "p10708", "name": "Local 10708", "type": "socks5", "host": "127.0.0.1", "port": 10708 },
    { "id": "p10808", "name": "Local 10808", "type": "socks5", "host": "127.0.0.1", "port": 10808 }
  ],
  "apps": [
    { "id": "chrome-direct", "name": "Chrome Direct", "exe": "...", "mode": "browser-direct", "userDataDir": "..." },
    { "id": "chrome-10708", "name": "Chrome via 10708", "exe": "...", "mode": "browser-proxy", "proxyId": "p10708", "userDataDir": "..." }
  ],
  "proxifier": {
    "enabled": true,
    "exe": "C:\\Program Files (x86)\\Proxifier\\Proxifier.exe",
    "profiles": {
      "direct": "E:\\proxyswitch\\profiles\\proxifier\\direct.ppx",
      "all10708": "E:\\proxyswitch\\profiles\\proxifier\\all-10708.ppx"
    }
  }
}
```

## 编译

```bash
cd src/ProxySwitch
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

## 版本

| 版本 | 状态 | 内容 |
|---|---|---|
| v0.1 | ✅ | 托盘、端口检测、启动浏览器、加载 Proxifier profile |
| v0.2 | ✅ | 动态托盘图标、GUI 配置页、最近使用记录 |
| v0.3 | ✅ | Dashboard、Drop Zone、Session Tracking、30s 心跳 |
| v0.3.1 | ✅ | 修复 Drop Zone bug + `.lnk` 支持 + ProcessMonitor 后台化 |
| v0.3.2 | ✅ | Session 卡片刷新、Drop zone 重绘、WorkingDirectory 修复 |
| v0.4 | ✅ | 子进程跟踪、launcher handoff 检测、Proxifier Assist 提示（UI 空壳） |
| v0.4.1 | ✅ | Assist Mode 实装（XML 写 .ppx）、PID 复用守卫、浏览器不走 handoff、WMI 字段裁剪 |
| v0.4.2 | ✅ | Correlated Handoff Tracking（ShellExecute / COM / UAC handoff 检测） |
| v0.4.3.1 | ✅ | 修 sync-over-async 死锁 |
| v0.5 | ✅ | Routing Decoupled — 删 Proxifier 假装集成，改名 Clash Verge / v2ray，Copy Rule Hint |
| v0.6 | ✅ | ProxiFyre transparent per-app backend（generic app 真路由） |
| v0.6.1 | ✅ | 配置 trap 修复 / pre-flight 检测 / Open Config 入口 / timestamp backup |
| v0.6.2 | ✅ | 拖 app 时 ProxySwitch 自动弹 UAC 重启 ProxiFyre（不再手动 PowerShell） |
| v0.6.3 | ✅ | tmp/set 分层 / persistent route 持久化 / Dashboard 加 Restart 按钮 |
| v0.6.4 | ✅ | Hot reload Settings / Drop 异步 / tmp 生命周期 / Launch rollback / AppRoutes tab / Service name 校验 |
| v0.7 | ✅ | External Process Watcher — persistent route 的 exe 从外部启动也自动建 session card |
| v0.7.1 | ✅ | GPT review v0.7 修复 — config always-write / 删 route 后 live 残留提示 / launch 回滚 flush / child route 继承父级 scope / Restart/Route/Stop async / Settings atomic save + Cancel 不重载 |
| v0.7.2 | ✅ | Opus review v0.7.1 修复 — external routing 状态对齐 / Settings AutoRestart 永远可点 / AttachExternalLaunch 单锁 + 子进程追踪 / Route Child 多子进程 pick-list / WriteConfig 单次 build / OnFormClosing 2s timeout / Restart 按钮 AutoSize / Dashboard 改 in-place rebind |
| v0.7.3 | ✅ | Launch-first / apply-async — drop 后 app 立刻起来,UAC+sc 重启走后台 task,卡片走 launching(红) → restarting(橙) → active(绿) + 托盘气泡;WaitForStatus 10s → 30s 消假阳 |
| v0.8.0 | ✅ | Stitch UI refresh — Theme tokens + IconRenderer + LaunchZoneControl 重写 + PillButton(Region 裁角) + Pinned pill + StatusStrip + Sections + DataGridView Sessions + 深色 Events 控制台 + 顶部 tab strip + SettingsForm SplitContainer 左 rail + LaunchConfirmDialog/RouteChild/CorrelatedHandoff Stitch 风格 |
| v0.8.1 | ✅ | Opus review v0.8.0 修复 — + Add Pinned 走共享 OpenSettings(ReloadRuntimeServices 不再被绕过) / Actions cell 命中测试字体一致 / OpenSettings 加 owner(Dashboard 路径 Z-order + taskbar 正确) / Actions cell font 缓存重用(消除每帧 GDI alloc) / IconRenderer using var 统一 |
| v0.8.2 | ✅ | UI 截断/列溢出修复 — PillButton GetPreferredSize 算 AccentDot + NoPadding flag(Pinned Apps 文字不再被截) / Settings Apps/Backends/Routes 三个 grid 改 Fill+MinimumWidth(不再溢视口) / DataGridView WrapMode=False / SettingsForm 默认尺寸 960×600 |
| v0.9 | ✅ | App Identity Resolver + Session Supervisor — AUMID 启动、进程树追踪、grace window 重连、外部进程自动 attach |
| v1.0 | ✅ | PID/Session Routing — 用 named-pipe IPC 把 Store App / 通用 exe 的 PID 实时推给 ProxiFyre，替代全局 appNames 规则，零 UAC、会话隔离、PID 复用防护 |

## v0.5 重要方向调整：Routing Decoupled

从 v0.4 一路下来，所有"Assist Mode"/"Add Rule"/"AssistRouting" 都是基于"Proxifier 已安装"的假设——实测发现根本没装。这意味着所有 Proxifier 集成代码从未端到端验证过 Proxifier 是否真识别我们生成的 `.ppx`。

v0.5 做了诚实的方向调整：

| 之前的假设 | 实际情况 | v0.5 处理 |
|---|---|---|
| Proxifier 是路由后端 | 没装 | **删除** ProxifierProfileGenerator / AddAssistRule / .ppx 写入 |
| 10708 / 10808 后面是 Proxifier | 实际是 Clash Verge / v2ray | 配置 label 改成 `Clash Verge 10708` / `v2ray 10808` |
| ProxySwitch 控制 generic app 路由 | 控制不了，由 Clash/v2ray 规则决定 | session card 显示 `Routing: External router`，**不**假装路由有效 |
| Add Rule 自动改 .ppx | 实际不工作 | 改为 **Copy Rule Hint**：生成 Clash/v2ray 规则片段让用户复制 |

**v0.5 边界：**
- 浏览器 `--proxy-server` → ProxySwitch **真控制**（启动参数）
- Generic app + proxy intent → **外部路由器决定**（Clash Verge / v2ray rule）
- ProxySwitch 只做 launcher + monitor + Copy Rule Hint + Open Routing Config

v0.6 计划用开源的 **ProxiFyre**（Windows packet filter）真做 per-app 透明代理，把 generic app 路由真正抓回 ProxySwitch 管理。

## v0.6 ProxiFyre 集成

v0.6 加入开源的 [ProxiFyre](https://github.com/wiresock/proxifyre) 作为透明 per-app 代理后端。**真控制 generic app 的路由**——不再"假装"。

### 工作原理

```
拖 Obsidian.exe 到 Clash Verge 10708 zone
  ↓
ProxySwitch 把 Obsidian.exe 加进 backend/proxifyre/app-config.json 的路由表
  ↓
ProxySwitch 写文件（atomic + .bak）
  ↓
ProxiFyre.exe（独立运行的 Windows service / 进程）读取 config
  ↓
所有 Obsidian.exe 发出的 TCP/UDP 流量被 NDISAPI 重定向到 127.0.0.1:10708
  ↓
Clash Verge 看到 SOCKS5 连接 → 应用它的规则路由到出口
```

### 首次配置步骤

1. 下载 ProxiFyre release（已自动下载到 `E:\proxyswitch\backend\proxifyre\`）
2. 安装 **Windows Packet Filter driver**（[wiresock 官方下载](https://github.com/wiresock/ndisapi/releases)）—— 这是 ProxiFyre 的硬依赖
3. 以管理员身份注册 ProxiFyre 为 Windows Service：
   ```powershell
   cd E:\proxyswitch\backend\proxifyre
   .\ProxiFyre.exe install
   .\ProxiFyre.exe start
   ```
4. 打开 ProxySwitch → Settings → Transparent Backend tab，勾选 `Enabled`
5. Dashboard 底部 `Backend` 行应显示 `ProxiFyre: running`

### 使用流程

| 场景 | RoutingStatus | 含义 |
|---|---|---|
| 浏览器走 `--proxy-server` | `Browser proxy` | ProxySwitch 控制（启动参数） |
| Generic app + 10708 zone（ProxiFyre enabled） | `ProxiFyre active` | ProxiFyre 路由到 Clash Verge |
| Generic app + 10708 zone（ProxiFyre pending） | `ProxiFyre pending` | config 已写但 service 未重启 |
| Generic app + 10708 zone（ProxiFyre failed） | `ProxiFyre failed` | 写 config 失败或 backend 不可用 |
| Generic app + 10708 zone（ProxiFyre disabled） | `External router` | 走 v0.5 Copy Rule Hint 路径 |
| 任何 direct intent | `Direct` | 不路由 |

### 子进程 / Launcher Handoff

如果 Steam / Epic 类 launcher 启动后产生 child process，Dashboard 上的 session card 会显示 `Route Child` 按钮。点击后 ProxySwitch 把 child.exe 也加入 ProxiFyre 路由表 + 重写 config。

⚠ ProxiFyre 路由是 **基于 exe 名/路径** 的，不是 PID 隔离的。其他地方启动同名 exe 也会被路由。

### 不会自动做的事

- ProxySwitch **不**自动装 Windows Packet Filter driver
- ProxySwitch **不**自动注册 ProxiFyre 为 service
- ProxySwitch **不**自动以管理员重启 service（除非用户勾 `manageService`）
- 这些都要用户主动操作，避免静默改系统

> **v1.0 更新：** 上述 config-file + service 重启模式已被 PID/Session Routing 取代，见下文 v1.0 章节。

## v1.0 PID/Session Routing（Named-Pipe IPC）

v1.0 用 **PID 级实时路由** 彻底替换了 v0.6 的 "写 config → 重启 service → 全局 appNames 匹配" 模式。解决三个核心问题：

| 问题 | v0.6 行为 | v1.0 行为 |
|---|---|---|
| **路由污染** | Store App 子进程名（`codex.exe`、`git.exe`）变成全局规则，影响无关 session | PID 绑定到 session，session 结束即清除，零残留 |
| **反复 UAC** | 每改一次 config 就 `runas` + `sc.exe` 重启 service | named-pipe IPC 实时推送 addPid/removePid，**无需重启** |
| **无会话边界** | `appNames` 规则持久且全局，不知道谁创建、何时该删 | 内存 session 表，`closeSession` 原子清理所有 PID |

### 架构

```
ProxySwitch (用户桌面进程)
  │
  ├── Store App 启动 → IApplicationActivationManager
  │     ↓
  ├── 创建 IPC session  →  Named Pipe  →  ProxiFyre SessionRouteServer
  │     ├─ createSession(endpoint)
  │     ├─ addPid(pid, creationTime)     ← 根进程 + 子进程
  │     └─ closeSession()                ← session 退出时
  │
  └── 通用 exe 启动 → 仍走 legacy config-file 路径（需 service 重启）

ProxiFyre (LocalSystem service)
  │
  ├── SessionRouteServer 监听 \\.\pipe\ProxySwitch.ProxiFyre.SessionRoute
  │     ├─ Pipe ACL: InteractiveSid + LocalSystem + Administrators
  │     └─ 应用层 token 校验（防止任意进程伪造 addPid）
  │
  ├── socks_local_router::pid_to_proxy_  内存 PID 路由表
  │     ├─ 键: PID (DWORD)
  │     └─ 值: {proxy_id, FILETIME creation_time}
  │
  └── 包过滤路径
        ├─ has_pid_route(pid) → 优先检查 PID 路由
        ├─ creation_time 校验 → OpenProcess + GetProcessTimes
        └─ 回退 proxy_to_names_（legacy 全局规则）
```

### 安全设计

1. **Named Pipe ACL** — 仅允许交互用户、LocalSystem、管理员连接；非提升进程可正常连接。
2. **Shared Token** — 应用层固定 GUID token，服务端拒绝缺失/错误 token 的请求。
3. **PID Reuse Guard** — 每个 PID 路由记录进程创建时间；包过滤路径实时 `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `GetProcessTimes()` 比对，创建时间不匹配则拒绝路由。
4. **Session Cleanup** — `Dispose()` 时遍历所有 session 的 PID 集合并调用 `RemoveProcessId`，再 `ClearPidRoutes()`，确保服务崩溃/重启不留孤儿路由。

### 状态机

Store App session 的 `RoutingStatus` 在 v1.0 变为三态：

| 状态 | 触发条件 | 含义 |
|---|---|---|
| `proxifyre-route-session-created` | `CreateSessionAsync` 成功 | session 已在 ProxiFyre 注册，等待第一个 PID |
| `proxifyre-route-active` | 第一个 `AddPidAsync` 成功 | 至少有一个 PID 被路由，流量生效 |
| `proxifyre-route-failed` | `CreateSessionAsync` 或 `AddPidAsync` 失败 | IPC 不可用或 endpoint 不存在 |

### 兼容性

- **ProxiFyre 旧版（无 IPC server）**: Store App 启动时检测到 IPC 不可用 → 直接报错 `IpcUnavailable`，**不**回退到 legacy child-route 模式，避免路由污染。
- **通用 exe 启动**: 仍走 v0.6 的 config-file + service 重启路径（`EnsureRoute` + `WriteConfig` + `ApplyRoutingInBackgroundAsync`），不受 IPC 影响。

## v0.9 App Identity + Session Supervisor

v0.9 解决两个长期问题：

1. **App Identity Resolver** — 自动把 AUMID（`OpenAI.Codex_2p2nqsd0c76g0!App`）解析成可执行路径、包家族名、进程名。Store App 不再需要用户手动填 exe 路径。
2. **Session Supervisor** —  launcher 退出后 session 不是立刻死亡，而是进入 `waiting-for-restart` grace window（默认 20s）。期间同一应用重新启动可自动 reattach，避免误标 exited。

## v0.8 Stitch UI Refresh

v0.8 完全重写 UI 层：

- **Theme Tokens** — 颜色、字体、圆角、边距全部 token 化，深色/浅色切换不硬编码
- **IconRenderer** — 统一图标渲染，消除每帧 GDI 对象分配
- **LaunchZoneControl** — Drop Zone 重写，Region 裁角 PillButton
- **DataGridView Sessions** — 列表区 in-place rebind，不复位滚动条
- **深色 Events 控制台** — 纯黑背景 + 语法高亮事件流
- **SettingsForm SplitContainer** — 左 rail 导航 + 右内容区

## v0.4.1 Assist Mode 使用

ProxySwitch 不能自动给 launcher 启动的 child 进程加 Proxifier 规则——这是 Proxifier 自己的限制（规则按 exe 名匹配，不会自动继承）。Assist Mode 是 ProxySwitch 提供的协助路径：

1. **在 Proxifier 里建好 base 模板**（一次性）：
   - 创建 `generated-10708.ppx`，里面有一个空规则 `ProxySwitch_10708_AssistedApps` 指向 SOCKS5 127.0.0.1:10708
   - 同理建 `generated-10808.ppx`
   - 见 `profiles/proxifier/README.md`
2. **拖一个 launcher 类应用到 10708 zone**
3. 当 launcher 退出但 child 还在跑时，Dashboard 上 session 卡片变黄、显示 `Via child` 状态、底部出现 ⚠ 警告条 + `Add Rule` 按钮
4. **点 Add Rule** — ProxySwitch 把 launcher.exe 和 child.exe 写入 `generated-10708.ppx` 的规则 `Applications`，然后通知 Proxifier 重新加载
5. 卡片状态变成 `assisted`，未来 child 进程的流量就走 10708 了

⚠ 规则是 exe 名匹配，不限 PID。其他地方启动同名 exe 也会被路由。

## v0.4.2 Correlated Handoff Tracking

某些 launcher 启动应用不走父子进程链：

- `launcher.exe → ShellExecute → broker → real-app.exe`
- `launcher.exe → COM 调用 → real-app.exe`
- `launcher.exe → UAC 提权 → consent → elevated-app.exe`
- `launcher.exe → IPC → 已经在跑的 app`

这种情况下 `real-app.exe` 的 `ParentProcessId` 不指向 launcher，v0.4.1 的 descendant tracking 看不到它，session 会错误标 exited。

v0.4.2 加了**相关性 handoff 跟踪**作为第二条检测路径：

1. 启动前拍进程快照（baseline）
2. 等 launcher 退 + 没有 descendant 时，拍 after snapshot，diff 出窗口期内的新进程
3. 启发式打分：同目录、命令行包含 launcher 路径、创建时间在 0-5 秒内 = 强信号；系统 broker（explorer / svchost / RuntimeBroker）= 负分
4. 阈值：
   - ≥70 高置信度 → 自动 attach 为 `Running via correlated` 状态
   - 40-69 中置信度 → 弹对话框让用户选 `Track Selected / Ignore / Open Location`
   - <40 → 忽略，session 标 exited

**重要：相关性 handoff 是启发式推测，不是证明**。Dashboard 会清楚标注 `unverified routing`，Add Rule 仍需要用户确认。

## 仓库结构

```
proxyswitch/
├── src/ProxySwitch/          ProxySwitch 主程序（.NET 8 WinForms）
├── src/ProxiFyre/            ProxiFyre 文档
├── ProxiFyre/                ProxiFyre 源码 fork（C++/CLI + C# service）
├── backend/                  运行时二进制（.gitignore，本地生成）
└── config/                   用户配置
```

## License

本项目采用**多许可证**发布：

- **ProxySwitch**（`src/ProxySwitch/` 目录下的所有代码）：**MIT License**
- **ProxiFyre**（`ProxiFyre/` 目录下的所有代码）：**GNU Affero General Public License v3 (AGPL-3.0)**

ProxiFyre 是 [wiresock/proxifyre](https://github.com/wiresock/proxifyre) 的 fork，基于 AGPL-3.0 授权。ProxySwitch 与 ProxiFyre 通过 named-pipe IPC 通信，作为两个独立程序运行。详见 `ProxiFyre/LICENSE`。
