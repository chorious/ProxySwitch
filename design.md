# ProxySwitch UI Design Draft for Google Stitch

版本基线：v0.7.3  
目标：把当前 WinForms 托盘应用整理成一版可视化 UI 初稿，供 Google Stitch 生成界面 mockup。  
产品定位：Windows 桌面工具，用于把指定应用通过 ProxiFyre 路由到本机 SOCKS5 端口，同时监控代理端口、进程树、路由应用状态。

---

## Stitch Prompt

Design a compact Windows desktop utility called **ProxySwitch**. It is not a marketing website. It should look like a quiet, professional operations console for launching applications through different local proxy routes and monitoring whether routing is active.

Create the UI as a desktop app window, primary size around **960 x 680**, minimum size around **720 x 480**. Use a restrained Windows 11 style: light background, crisp typography, thin borders, subtle status colors, dense but readable layout. Avoid large hero sections, decorative gradients, oversized cards, or playful illustrations.

The app has four main areas:

1. **Launch Zones**
   - Top row with three equal drop zones:
     - Direct
       - subtitle: "Drop app - no proxy"
       - neutral gray accent
     - Clash Verge 10708
       - subtitle: "Drop app - route through ProxiFyre"
       - green accent
     - v2ray 10808
       - subtitle: "Drop app - route through ProxiFyre"
       - blue accent
   - Each zone accepts `.exe` drag and drop.
   - While dragging over a zone, increase border contrast and tint the background.
   - Use simple icons: monitor/app icon for Direct, shield/network icon for proxy zones.

2. **Pinned Apps**
   - A compact horizontal strip under the launch zones.
   - Show small pill-like buttons for saved launch templates:
     - Chrome Direct
     - Chrome via Clash Verge 10708
     - Chrome via v2ray 10808
     - Edge Direct
     - Edge via Clash Verge 10708
   - Buttons should be compact, not large cards.

3. **Sessions**
   - Main center area.
   - Show active and recent launch sessions as dense rows.
   - Each session row has columns:
     - App name and launch mode
     - Process status and elapsed time
     - Routing status
     - PID or process count
     - Actions
   - Example session rows:
     - Obsidian / proxy-10708 / Running / 00:02:14 / ProxiFyre active / Processes: 2 / Stop, Route Child
     - Chrome / browser-proxy / Running / 00:08:40 / Browser proxy / PID: 12348 / Stop
     - Steam / proxy-10808 / Via child / 00:01:20 / Applying (UAC) / Processes: 3 / Stop, Route Child
     - UnknownApp / proxy-10708 / Failed / 00:00:04 / ProxiFyre failed / PID: ? / Retry, Copy Rule Hint
   - Use row background colors for state:
     - running: pale green
     - running via child: pale yellow
     - running via correlated handoff: pale orange
     - checking correlated: pale blue
     - exited: light gray
     - failed: pale red
   - Routing status text colors:
     - active: dark green
     - launching: red
     - applying/restarting: amber
     - pending/restart needed: amber
     - failed: red
     - external router required: amber
     - direct: gray

4. **Bottom Monitoring Area**
   - Split bottom area into two panes.
   - Left pane: "Proxies & Backend"
     - Show port heartbeat status:
       - Clash Verge 10708: ONLINE, 12ms, checked 10:42:31
       - v2ray 10808: ONLINE, 18ms, checked 10:42:31
     - Show backend status:
       - ProxiFyre: running - manual mode (app-config.json)
     - Show a compact button:
       - Restart ProxiFyre (UAC)
     - If stale live rules exist, change button text to:
       - Restart ProxiFyre - unload stale rules (UAC)
       - amber tinted background
   - Right pane: "Events"
     - Console-like list with recent events, monospace font.
     - Example events:
       - 10:42:03 LaunchStarted Obsidian proxy-10708
       - 10:42:04 AppRouteAdded Obsidian.exe -> p10708 session-only
       - 10:42:04 LaunchSucceeded PID=19320
       - 10:42:05 RouteApplying restarting ProxiFyre to activate route
       - 10:42:13 RouteActivated ProxiFyre route active

Also design these secondary screens:

1. **Launch Confirmation Modal**
   - Triggered when dropping an app into 10708 or 10808.
   - Title: "Launch via Clash Verge 10708?"
   - Body explains three choices clearly:
     - Save route permanently
     - Just this session
     - Cancel
   - Mention that ProxiFyre may request UAC to restart the service.
   - Buttons: "Save Route", "Just This Session", "Cancel"
   - Keep the copy concise and operational.

2. **Route Child Modal**
   - Title: "Route Children: Steam"
   - Used when launcher-style apps spawn child processes.
   - Show selectable rows with checkbox, process name, PID list, executable path.
   - Explain that ProxiFyre matches by executable path.
   - Buttons: "Route Selected", "Cancel"

3. **Confirm Handoff Modal**
   - Title: "Confirm Handoff"
   - Used when the app detects a process that may be the real launched target.
   - Show up to five candidates.
   - Candidate row includes app name, PID, confidence score, path, reasons.
   - Buttons: "Track Selected", "Open Location", "Ignore"

4. **Settings Window**
   - Desktop window around 820 x 560.
   - Use tabs:
     - Proxies
     - Apps
     - Routing Backends
     - App Routes
     - Transparent Backend
   - Proxies tab: editable grid with ID, Name, Type, Host, Port.
   - Apps tab: editable grid with ID, Name, Executable Path, Mode, Proxy ID, User Data Dir.
   - Routing Backends tab: editable grid for Clash Verge and v2ray config locations.
   - App Routes tab: editable grid for ProxiFyre rules with Name, Executable, Process, Proxy, Enabled, Saved, Source.
   - Transparent Backend tab:
     - Use transparent backend checkbox
     - Type dropdown: none / proxifyre
     - ProxiFyre exe path input
     - Config path input
     - Service name input
     - Manage service checkbox
     - Auto restart on config change checkbox
   - Bottom buttons: Save, Cancel

Visual style:

- Use white and light gray surfaces.
- Use status colors only where they communicate state.
- Use icons sparingly inside buttons and section headers.
- Use compact row heights and readable data density.
- Avoid nested cards. Use tables, split panes, rows, and thin dividers.
- Typography should be utilitarian: Segoe UI or similar.
- Make all button labels fit at 720 px minimum width.

The primary screen should immediately show the working dashboard, not a landing page.

---

## 当前产品进度摘要

当前代码已经推进到 v0.7.3。核心能力包括：

- 托盘常驻，双击打开 Dashboard。
- 10708 端口对应 Clash Verge，10808 端口对应 v2ray。
- 浏览器类应用通过启动参数实现 `--proxy-server` 和独立 user-data-dir。
- generic app 通过 ProxiFyre 生成 `app-config.json`，走应用级透明路由。
- Dashboard 支持把 `.exe` 拖到 Direct / 10708 / 10808 三个区域启动。
- 拖入代理区时可选择永久保存规则或仅本次 session。
- v0.7.3 已改为 launch-first / apply-async：应用先启动，ProxiFyre 重启和 UAC 在后台推进。
- Session 卡片显示进程状态、路由状态、PID/进程数、Stop、Route Child、Retry、Copy Rule Hint。
- 外部启动已保存 AppRoute 的 exe 时，External Process Watcher 会自动创建 session card。
- 端口心跳和 backend 状态在 Dashboard 底部显示。
- Settings 已有 Proxies / Apps / Routing Backends / App Routes / Transparent Backend 五个 tab。

---

## 信息架构

### 1. Tray

托盘不是主工作区，只承担快速入口：

- Open Dashboard
- Quick Launch
- Proxy Status
- Routing
  - Clash Verge
    - Open App
    - Open Config
  - v2ray
    - Open App
    - Open Config
- ProxiFyre
  - Restart Service (UAC)
  - Open Config
  - Open Logs
  - Open Folder
- Settings
- Quit

托盘图标颜色：

- 10708 在线：绿色
- 10808 在线：蓝色
- 都离线：灰色
- 都在线：绿色，tooltip 展示两个端口状态

### 2. Dashboard

Dashboard 是主屏，应该是一个操作台。

布局：

```text
┌──────────────────────────────────────────────────────────────┐
│ Drop Zone: Direct │ Drop Zone: Clash 10708 │ Drop Zone: v2ray │
├──────────────────────────────────────────────────────────────┤
│ Pinned Apps: Chrome Direct / Chrome 10708 / Chrome 10808 ... │
├──────────────────────────────────────────────────────────────┤
│ Sessions                                                     │
│ app | status | routing | pid/processes | actions             │
│ app | status | routing | pid/processes | actions             │
├───────────────────────────────┬──────────────────────────────┤
│ Proxies & Backend             │ Events                       │
└───────────────────────────────┴──────────────────────────────┘
```

Dashboard 视觉重点：

- 顶部三块 Drop Zone 是主要入口。
- Sessions 是最高信息密度区域。
- Bottom area 用于判断“代理端口是否活着”和“ProxiFyre 是否活着”。
- Events 是排障区，不要抢主视觉。

### 3. Settings

Settings 是配置编辑器，不应该做成向导。

设计原则：

- 使用 tab + grid，适合多次维护。
- ProxiFyre 设置独立成 tab。
- App Routes 是用户最需要理解的持久路由表，必须可见。
- Save 后热重载，Cancel 不应影响当前 Dashboard。

---

## 状态设计

### Session Status

| 内部状态 | UI 文案 | 行背景 |
|---|---|---|
| running | Running | 淡绿 |
| running-via-child | Via child | 淡黄 |
| running-via-correlated | Via correlated | 淡橙 |
| checking-correlated | Checking... | 淡蓝 |
| exited | Exited | 浅灰 |
| failed | Failed | 淡红 |

### Routing Status

| 内部状态 | UI 文案 | 颜色 |
|---|---|---|
| direct | Direct | 灰 |
| browser-proxy-active | Browser proxy | 深绿 |
| external-routing-required | External router | 琥珀 |
| proxifyre-route-launching | Setting up... | 红 |
| proxifyre-route-restarting | Applying (UAC) | 琥珀 |
| proxifyre-route-active | ProxiFyre active | 深绿 |
| proxifyre-route-pending | ProxiFyre pending | 琥珀 |
| proxifyre-route-needs-restart | Restart needed | 琥珀 |
| proxifyre-route-failed | ProxiFyre failed | 红 |

### Backend Status

| 状态 | UI 文案 |
|---|---|
| disabled | ProxiFyre: disabled |
| not-configured | ProxiFyre: not configured |
| exe-missing | ProxiFyre: exe missing |
| service-not-installed | ProxiFyre: service not installed |
| running | ProxiFyre: running - manual/managed mode |
| stopped | ProxiFyre: stopped - start service for routing |
| stale live rules | Restart ProxiFyre - unload stale rules (UAC) |

---

## 关键交互

### 拖放启动

用户把 `.exe` 拖到某个区域：

1. Direct：直接启动，session 显示 Direct。
2. 10708 / 10808：弹确认框。
3. 用户选 Save Route：写入 AppRoutes，持久保存。
4. 用户选 Just This Session：仅绑定当前 session。
5. app 立即启动。
6. session routing 状态从 `Setting up...` 到 `Applying (UAC)` 再到 `ProxiFyre active`。
7. 完成后托盘气泡提示 route active。

### Route Child

当 launcher 退出但 child 还活着：

1. session 状态显示 Via child。
2. action 区显示 Route Child。
3. 如果只有一个 child exe，弹简单确认。
4. 如果多个 child exe，弹 Route Child Modal 多选。
5. child 路由继承父 session 的 scope：永久或本次 session。

### External Launch

用户没有通过 ProxySwitch 启动，但从系统其他地方启动了已保存 AppRoute：

1. ExternalProcessWatcher 捕捉进程创建。
2. Dashboard 自动出现 session card。
3. session 显示为已 attach 的运行进程。
4. routing 状态根据 ProxiFyre backend 当前状态显示 active 或 restart needed。

### Handoff

如果进程不是父子关系，而是 ShellExecute / COM / UAC broker 类 handoff：

1. session 进入 Checking...
2. 若候选进程置信度足够，自动 attach 或弹 Confirm Handoff。
3. 用户可 Track Selected / Open Location / Ignore。

---

## 建议的视觉规格

### 尺寸

- Dashboard 默认：960 x 680
- Dashboard 最小：720 x 480
- Settings 默认：820 x 560
- Handoff Modal：740 x 520
- Route Child Modal：720 x 480

### 颜色

基础色：

- Window background: `#F8FAFC`
- Panel background: `#FFFFFF`
- Border: `#D1D5DB`
- Text primary: `#111827`
- Text secondary: `#6B7280`

状态色：

- Direct gray: `#6B7280`
- Clash green: `#22C55E`
- v2ray blue: `#3B82F6`
- Active green text: `#166534`
- Pending amber text: `#B45309`
- Failed red text: `#B91C1C`

行背景：

- running: `#DCFCE7`
- via child: `#FEF9C3`
- via correlated: `#FED7AA`
- checking: `#DBEAFE`
- exited: `#F3F4F6`
- failed: `#FEE2E2`

### 字体

- 默认：Segoe UI 9-10pt
- session app name：Segoe UI 9-10pt semibold
- events：Consolas 9pt
- section label：Segoe UI 10pt semibold

---

## Stitch 生成时的注意事项

- 不要生成 landing page。
- 不要生成移动 App 风格。
- 不要生成大面积插画、渐变背景或宣传型 hero。
- 不要把每个区域都做成大卡片；Dashboard 是工具，不是 SaaS 官网。
- Sessions 区域要像任务管理器/运维面板，信息紧凑。
- Drop Zone 可以稍微更醒目，因为它是主操作入口。
- Settings 需要像真实配置编辑器，表格优先。
- UAC、service restart、stale live rules 这些风险状态要可见，但不要吓人。

---

## 可直接给 Stitch 的短版 Prompt

Design a Windows desktop app dashboard for "ProxySwitch", a tray utility that launches apps through local proxy routes and monitors ProxiFyre per-app routing. Use a compact operations-console layout, not a landing page. Main window 960x680. Top row: three drag-and-drop launch zones: Direct, Clash Verge 10708, v2ray 10808. Second row: compact pinned app buttons. Center: dense session table/cards with app, status, routing state, PID/process count, and actions like Stop, Route Child, Retry, Copy Rule Hint. Bottom split pane: Proxies & Backend status on the left, Events console on the right. Use Windows 11 light style, thin dividers, Segoe UI, subtle status colors: green active, amber pending/UAC, red failed, blue checking, gray exited. Also design Settings with tabs for Proxies, Apps, Routing Backends, App Routes, Transparent Backend, plus modals for Launch Confirmation, Route Child selection, and Confirm Handoff. Keep it utilitarian, readable, and suitable for repeated daily use.
