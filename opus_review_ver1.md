# ProxySwitch 项目评估 — Opus Review v1

评估日期：2026-05-11
评估范围：`E:\proxyswitch\` 全部源码、配置、PLAN.md

---

## 整体判断

**计划合理、实现与 PLAN 高度一致、MVP 已经基本完成。**

这是个克制、定位清晰的小工具 —— 拒绝做网络拦截、不抢系统代理、把 Proxifier 当成集成边界，这些边界划得很对。代码质量符合一个小托盘工具的水准，约 500 行 C#，没有过度设计。

---

## 与 PLAN 对照的完成度

| PLAN 项 | 状态 |
|---|---|
| C# WinForms 托盘 | 完成（`MainForm.cs`） |
| 读取 `proxyswitch.json` | 完成 |
| 10708/10808 在线检测 | 完成（5s 轮询） |
| Chrome Direct/10708/10808 启动 | 完成 |
| Edge Direct/10708 | 完成（配置里已加） |
| 打开 Proxifier | 完成 |
| 加载 `.ppx` profile | 完成（命令行参数） |
| 打开配置目录 | 完成 |
| 日志 | 完成 |
| 单文件 exe | 配置已开 `PublishSingleFile` |

PLAN 里的 MVP 9 项**全部覆盖**，已编译出 `ProxySwitch.exe`。

---

## 实现里值得肯定的地方

- `PortMonitor` 用 `TcpClient` 直接连，Online 判定可靠。状态变化才触发 `StatusChanged`，避免无谓刷新。
- `AppLauncher` 启动前检查 `File.Exists`，失败弹错误框 + 写日志，UX 合理。
- 配置完全外置 JSON，加新应用不用改代码。
- 边界守得住：不做 hook、不写驱动、不接管系统代理。
- `Program.cs` 装了 `UnhandledException` 和 `ThreadException` 兜底，崩溃时有日志和提示。
- `MainForm` 用 `InvokeRequired` 处理跨线程 UI 更新（虽然目前 Timer 在 UI 线程上其实不需要，但写法是稳的）。

---

## 潜在问题（按严重程度排序）

### 1. `PortMonitor.Check()` 同步阻塞，代理离线时 UI 会卡 ~42 秒 【严重】

**位置**：`src/ProxySwitch/Services/PortMonitor.cs:50-62`

`TcpClient.Connect()` 是同步阻塞调用，默认超时取决于 Windows TCP RTO，最坏情况可达 21 秒。当 10708 和 10808 都离线时，每次轮询会把 UI 线程卡住约 21 × 2 = 42 秒。`_timer` 是 `System.Windows.Forms.Timer`，跑在 UI 线程上，期间托盘菜单点不开。

**修法**：改成 `ConnectAsync` + `CancellationToken`，1s 超时：

```csharp
private static async Task<bool> CheckAsync(string host, int port)
{
    try
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(1000);
        await client.ConnectAsync(host, port, cts.Token);
        return client.Connected;
    }
    catch
    {
        return false;
    }
}
```

然后 `CheckAll` 改成 `async void` 或用 `Task.Run` 后 `Invoke` 回 UI 线程。

---

### 2. `Path.Combine("E:", ...)` 不是合法的绝对路径 【中高】

**位置**：
- `src/ProxySwitch/MainForm.cs:51`
- `src/ProxySwitch/MainForm.cs:187`
- `src/ProxySwitch/Services/Logger.cs:5-6`

`Path.Combine("E:", "proxyswitch", "config")` 在 Windows 上返回的是 `E:proxyswitch\config` —— **盘符相对路径**，依赖当前进程的工作目录。它**不等于** `E:\proxyswitch\config`。

目前能跑只是因为程序启动时 CWD 恰好在 `E:\` 根或类似位置。如果从其他盘启动、或被任务计划程序拉起，会读不到配置。

**修法**：

```csharp
private static readonly string Root = @"E:\proxyswitch";
// 或更好：基于 exe 位置
private static readonly string Root = Path.GetDirectoryName(AppContext.BaseDirectory) ?? @"E:\proxyswitch";
```

---

### 3. 硬编码 `E:\proxyswitch` 路径 【中】

`Logger`、`MainForm.LoadConfig`、`OpenConfigFolder` 都把根路径写死了。如果 exe 被移到别处，整个程序就不工作。

**修法**：用 `AppContext.BaseDirectory` 推导，或读环境变量 `PROXYSWITCH_HOME`，把硬编码集中到一个 `Paths` 静态类里。

---

### 4. Proxifier 命令行加载 `.ppx` 的行为不确定 【中】

**位置**：`src/ProxySwitch/Services/AppLauncher.cs:115-119`

PLAN.md 里你自己也提到 "Proxifier 命令行能力有限"。实测里 Proxifier Standard Edition 用 `Proxifier.exe profile.ppx` 通常会**打开一个新实例**或**仅打开 profile 编辑器**，而不是真正切换当前 profile。

**建议**：
- 实测一下命令行行为；
- 如果不是切换而是新开实例，需要先 `Process.GetProcessesByName("Proxifier")` kill 再启动；
- 或者退化为打开 `.ppx` 文件关联（`UseShellExecute = true`，`FileName = path`）让 Windows 自己处理。

---

### 5. `profiles/proxifier/` 目录为空 【低，但影响首次体验】

4 个 `.ppx`（`direct.ppx`、`all-10708.ppx`、`all-10808.ppx`、`mixed-rules.ppx`）都还没有。用户第一次点 "Load 10708 Rules" 会弹 "Profile Missing" 对话框。

**建议**：
- 在 README 或托盘菜单里加 "Export from Proxifier first" 的引导；
- 或者预置 4 个最小合法的 `.ppx` 模板（用 Proxifier 建好后 commit 进来）。

---

### 6. 托盘图标不反映状态 【低，PLAN v0.2 已记】

`MainForm.cs:42` 用的是 `SystemIcons.Application`，永远不变。状态变化只反映在 tooltip 文字上，托盘瞥一眼看不出代理是否在线。

**建议**：准备两个 16×16 ico（绿/灰），按 `_monitor.IsOnline` 切换。PLAN v0.2 里已经规划了这条。

---

### 7. 左键点击的交互略冗余 【低】

**位置**：`MainForm.cs:156-164`

`TrayClick` 在左键点击时主动 `_tray.ContextMenuStrip?.Show(...)`。但 `NotifyIcon` 在**右键**点击时本身就会自动弹 `ContextMenuStrip`，左键再弹一次会让用户感觉双重。

**建议**：左键改成"启动默认应用"（比如 Chrome Direct）或"刷新状态"，右键保留弹菜单。或者干脆不处理左键，符合 Windows 托盘惯例。

---

## 建议的下一步（按优先级）

1. **修 `PortMonitor` 的同步阻塞** —— 改 async + 1s 超时。这是当前最影响实际体验的 bug。
2. **修 `Path.Combine("E:", ...)`** —— 改成 `@"E:\proxyswitch"` 或基于 exe 路径。
3. **实测 Proxifier 命令行加载** —— 决定要不要加 kill 重启逻辑，或退化为文件关联。
4. **导出 4 个 `.ppx` 初始模板** —— 在 Proxifier 里建好并放到目标目录，让首次体验完整。
5. （v0.2）动态托盘图标 + 改左键交互。

---

## 不需要改的地方

- 没必要把硬编码路径改成完全可配置 —— 这是个人工具，过度抽象反而拖累迭代。
- 没必要换技术栈 —— C# WinForms 对这个场景就是最合适的选择，PLAN 里的判断成立。
- 没必要现在就自己生成 `.ppx` —— PLAN 里"让用户自己在 Proxifier 中导出"的边界是对的，往后如果格式稳定再做也不迟。
