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
- [Proxifier](https://www.proxifier.com/)（可选，用于进程级代理规则）

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
| v0.7 | 📋 | 自动发现应用路径、应用规则预设 |
| v1.0 | 📋 | 安装包、开机自启 |

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
| v0.6 | 📋 | 全局热键、复制启动命令、代理延迟检测 |
| v1.0 | 📋 | 安装包、开机自启 |

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

## License

MIT
