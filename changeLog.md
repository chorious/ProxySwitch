# ChangeLog

## 版本速查

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
| v0.7.1 | ✅ | GPT review v0.7 修复 |
| v0.7.2 | ✅ | Opus review v0.7.1 修复 |
| v0.7.3 | ✅ | Launch-first / apply-async — drop 后 app 立刻起来，UAC+sc 重启走后台 task |
| v0.8.0 | ✅ | Stitch UI refresh — Theme tokens + IconRenderer + LaunchZoneControl 重写 |
| v0.8.1 | ✅ | Opus review v0.8.0 修复 |
| v0.8.2 | ✅ | UI 截断/列溢出修复 |
| v0.9 | ✅ | App Identity Resolver + Session Supervisor |
| v1.0 | ✅ | PID/Session Routing — named-pipe IPC 替代全局 appNames 规则 |

---

## v0.1 — MVP

**问题：** 切换浏览器代理需要手动改命令行或系统代理，不方便同时跑多个 Chrome。
**方案：** 托盘应用 + 配置文件启动不同 Chrome 实例（`--proxy-server` / `--no-proxy-server`），不碰系统代理。

---

## v0.2 — 配置 GUI

**问题：** 纯 json 配置对非技术用户不友好。
**方案：** Settings 窗体 + 动态托盘图标（直连/代理状态可视化）+ 最近使用记录。

---

## v0.3 — Dashboard

**问题：** 不知道哪些应用在跑、走了什么代理。
**方案：** Dashboard 窗体 + Drop Zone 拖放启动 + Session Tracking + 30s 心跳检测存活。

### v0.3.1

**问题：** Drop Zone 拖放后区域不刷新；快捷方式 `.lnk` 无法解析。
**方案：** 修复重绘事件；加入 `ShellLink` COM 解析 `.lnk` 目标路径。

### v0.3.2

**问题：** Session 卡片信息 stale；Drop Zone 绘制残留；子进程 WorkingDirectory 错误导致部分 app 启动失败。
**方案：** 卡片定时刷新；Drop Zone 强制重绘；启动时设置 `WorkingDirectory` 为 exe 所在目录。

---

## v0.4 — 子进程与 Handoff

**问题：** Steam/Epic 等 launcher 启动后会 spawn child process，launcher 自己退出，session 误判为已结束。
**方案：** 子进程树追踪（descendant tracking），launcher 退出后检查是否还有后代进程存活。

### v0.4.1 — Assist Mode

**问题：** Proxifier 规则按 exe 名匹配，launcher 启动的 child 不会自动继承规则。
**方案：** Assist Mode — launcher 退出但 child 存活时，Dashboard 显示 `Add Rule` 按钮，自动把 child.exe 写入 Proxifier `.ppx` 并通知重载。
**同时：** 加入 PID 复用守卫（防止进程退出后 PID 被复用导致误判）；浏览器明确不走 handoff 逻辑（Chrome 自己就是叶子进程）。

### v0.4.2 — Correlated Handoff

**问题：** 某些 launcher 不走父子链（ShellExecute → broker → real-app；COM 调用；UAC 提权），descendant tracking 看不到。
**方案：** 相关性 handoff 跟踪 — 启动前拍 baseline 快照，launcher 退出后拍 after 快照，启发式打分（同目录、命令行、创建时间）匹配新进程。
- ≥70 分：自动 attach
- 40-69 分：弹窗让用户确认
- <40 分：标 exited

### v0.4.3.1

**问题：** `WaitForConnectionAsync().Wait()` 导致 sync-over-async 死锁（UI 线程捕获上下文）。
**方案：** 改为 `Task.Run(() => WaitForConnectionAsync()).Wait()`，或彻底异步化。

---

## v0.5 — Routing Decoupled（方向调整）

**问题：** 所有 Proxifier 集成代码基于"Proxifier 已安装"假设，实测发现根本没装。代码从未端到端验证过。
**方案：** 诚实调整 — 删除 ProxifierProfileGenerator / AddAssistRule / .ppx 写入；配置 label 改为 `Clash Verge 10708` / `v2ray 10808`；generic app 路由显示 `External router`，不假装控制；改为 **Copy Rule Hint** 让用户复制规则片段到自己路由器。

**边界：**
- 浏览器 `--proxy-server` → ProxySwitch **真控制**
- Generic app + proxy intent → **外部路由器决定**
- ProxySwitch 只做 launcher + monitor + Copy Rule Hint + Open Routing Config

---

## v0.6 — ProxiFyre 集成（真路由）

**问题：** v0.5 的 "External router" 是自欺欺人，ProxySwitch 对 generic app 没有实际控制能力。
**方案：** 引入开源 ProxiFyre（Windows NDISAPI packet filter）作为透明 per-app 代理后端。ProxySwitch 写 `app-config.json` → ProxiFyre 读取 → 重定向 TCP/UDP 到 SOCKS5。

### v0.6.1

**问题：** 配置写坏后无法恢复；用户不知道配置文件在哪。
**方案：** 配置 trap 修复（写前备份 .bak）；pre-flight 检测（端口、exe 存在性）；增加 Open Config 菜单入口；timestamp 备份策略。

### v0.6.2

**问题：** 每次改 ProxiFyre 配置后需要手动 PowerShell 以管理员重启 service。
**方案：** ProxySwitch 检测到需要重启时，自动弹 UAC 用 `runas` + `sc.exe` 重启 ProxiFyre。

### v0.6.3

**问题：** 临时路由和持久路由混在一个文件里，持久路由被误删。
**方案：** tmp/set 分层 — `tmp/` 放动态路由，`set/` 放用户持久路由；持久路由持久化到 proxyswitch.json；Dashboard 加 Restart 按钮手动重载。

### v0.6.4

**问题：** Settings 改后需要重启 ProxySwitch 才生效；Drop 同步阻塞 UI；tmp 路由生命周期混乱；Launch 失败时部分状态已提交无法回滚。
**方案：** Hot reload Settings（不重启）；Drop 异步处理；tmp 生命周期明确（session 结束即删）；Launch rollback（失败时 flush 中间状态）；新增 AppRoutes tab；Service name 校验。

---

## v0.7 — External Process Watcher

**问题：** 外部启动的 persistent route 应用（如用户自己双击 Obsidian）不会被 ProxySwitch 追踪，Dashboard 看不到。
**方案：** External Process Watcher — 扫描持久路由对应的 exe 进程，自动 attach 建 session card。

### v0.7.1

**问题：** GPT review 发现的边界情况 — config 不是 always-write；删 route 后 live 状态残留；launch 回滚未 flush；child route 未继承父级 scope。
**方案：** 修复全部 review 点。见 `plan&review/review/gpt_review_v0.7.md`。

### v0.7.2

**问题：** Opus review 发现的外部路由状态不对齐；Settings AutoRestart 逻辑错误；AttachExternalLaunch 多锁竞争；Route Child 多子进程时随机 pick。
**方案：** 状态对齐；AutoRestart 永远可点；AttachExternalLaunch 单锁 + 子进程追踪；Route Child 多子进程时显示 pick-list；WriteConfig 单次 build；OnFormClosing 2s timeout；Restart 按钮 AutoSize；Dashboard in-place rebind 不复位滚动条。

### v0.7.3

**问题：** Drop 后 app 启动慢（等 UAC + service 重启）；status 假阳性（WaitForStatus 10s 不够）。
**方案：** Launch-first / apply-async — drop 后 app 立刻启动，UAC+sc 重启走后台 task；卡片状态机 `launching(红)` → `restarting(橙)` → `active(绿)` + 托盘气泡提示；WaitForStatus 10s → 30s。

---

## v0.8 — Stitch UI Refresh

**问题：** v0.7 之前 UI 是拼凑的，没有设计系统，深色模式硬编码，GDI 对象泄漏。
**方案：** 完全重写 UI 层：
- Theme Tokens — 颜色/字体/圆角/边距 token 化
- IconRenderer — 统一渲染，消除每帧 GDI alloc
- LaunchZoneControl — Region 裁角 PillButton 重写
- DataGridView Sessions — in-place rebind
- 深色 Events 控制台 — 纯黑背景 + 语法高亮
- SettingsForm SplitContainer — 左 rail 导航

### v0.8.1

**问题：** Opus review — Add Pinned 绕过 OpenSettings 导致 ReloadRuntimeServices 被跳过；Actions cell 命中测试字体不一致；OpenSettings 无 owner 导致 Z-order 错误。
**方案：** + Add Pinned 走共享 OpenSettings；命中测试字体缓存一致；OpenSettings 加 owner；Actions cell font 缓存重用；IconRenderer using var 统一。

### v0.8.2

**问题：** PillButton 文字截断；Settings grid 列溢出视口；DataGridView WrapMode 干扰布局。
**方案：** PillButton GetPreferredSize 算 AccentDot + NoPadding flag；三个 grid 改 Fill+MinimumWidth；WrapMode=False；SettingsForm 默认尺寸 960×600。

---

## v0.9 — App Identity + Session Supervisor

**问题 1：** Store App 需要用户手动填 exe 路径，AUMID（如 `OpenAI.Codex_...!App`）解析困难。
**方案：** App Identity Resolver — 自动把 AUMID 解析成可执行路径、包家族名、进程名。

**问题 2：** launcher 退出后 session 立刻标 died，但如果 app 在 grace window 内重启会误标。
**方案：** Session Supervisor — launcher 退出后进入 `waiting-for-restart` grace window（默认 20s），期间同一应用重新启动可自动 reattach。

---

## v1.0 — PID/Session Routing

**问题：** v0.6 的 "写 config → 重启 service → 全局 appNames 匹配" 有三个致命缺陷：
1. **路由污染** — Store App 子进程名（`codex.exe`、`git.exe`）变成全局规则，影响无关 session
2. **反复 UAC** — 每改一次 config 就 `runas` + `sc.exe` 重启 service
3. **无会话边界** — `appNames` 规则持久且全局，不知道谁创建、何时该删

**方案：** PID 级实时路由，用 named-pipe IPC 彻底替换 config-file 模式：
- `CreateSessionAsync(endpoint)` — 在 ProxiFyre 注册 session
- `AddPidAsync(pid, creationTime)` — 实时推送 PID
- `CloseSessionAsync()` — session 结束原子清理
- ProxiFyre 内存维护 `pid_to_proxy_` 路由表，包过滤路径优先检查 PID 路由，creation_time 防 PID 复用

**状态机：**
- `proxifyre-route-session-created` — session 已注册，等待第一个 PID
- `proxifyre-route-active` — 至少一个 PID 被路由
- `proxifyre-route-failed` — IPC 失败

**安全：**
- Named Pipe ACL — InteractiveSid + LocalSystem + Administrators
- Shared Token — 应用层固定 GUID，服务端校验
- PID Reuse Guard — `OpenProcess` + `GetProcessTimes` 实时比对 creation_time
- Session Cleanup — `Dispose()` 遍历清理所有 PID

**兼容性：**
- ProxiFyre 旧版（无 IPC server）：Store App 启动时检测到 IPC 不可用 → 直接报错 `IpcUnavailable`，**不**回退 legacy 模式
- 通用 exe 启动：仍走 v0.6 config-file + service 重启路径，不受 IPC 影响
