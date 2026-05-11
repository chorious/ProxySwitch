# ProxySwitch 应用规划

## 目标

ProxySwitch 是一个 Windows 托盘应用，用来把“应用级代理切换”从命令行和系统代理设置里抽离出来。它不接管系统代理，核心能力是通过 Proxifier profile 和规则，让指定应用走 `127.0.0.1:10708`、`127.0.0.1:10808` 或直连。

第一阶段不做网络拦截引擎，直接复用 Proxifier 的成熟能力。ProxySwitch 负责配置、可视化状态、快捷启动和 profile 切换。

## 核心场景

1. 两个 Chrome 同时运行，一个直连，一个走代理。
2. 指定应用走指定代理，例如 Cursor / VSCode / Git / 浏览器 / 数据库客户端。
3. 不修改 Windows 系统代理。
4. 不需要每次手写命令启动应用。
5. 托盘菜单里快速选择代理策略。
6. 检测 `10708` 和 `10808` 是否在线。
7. 后续可扩展为规则模板和一键导入 Proxifier profile。

## 技术路线

### 方案分层

```text
ProxySwitch 托盘应用
  ├─ App Launcher：按模板启动 Chrome / Edge / 其他应用
  ├─ Port Monitor：检测 10708 / 10808 是否可连接
  ├─ Profile Manager：管理本地代理策略配置
  └─ Proxifier Adapter：调用 Proxifier 加载 profile 或生成规则

Proxifier
  ├─ 进程级代理规则
  ├─ 目标域名/IP/端口规则
  ├─ SOCKS5 / HTTPS 代理
  └─ Direct / Block / Proxy Chain 动作
```

### 第一阶段边界

ProxySwitch 不实现以下能力：

- 不自己 hook 网络调用。
- 不写驱动。
- 不做 TUN / VPN。
- 不替代 Proxifier 的规则引擎。
- 不强制接管系统代理。

ProxySwitch 只做：

- 托盘 UI。
- 配置管理。
- 启动应用。
- 检查端口。
- 调用 Proxifier profile。
- 生成或维护 Proxifier 可导入配置。

## MVP 功能

### 1. 托盘菜单

菜单建议：

```text
ProxySwitch
├─ Status
│  ├─ 10708: Online / Offline
│  └─ 10808: Online / Offline
├─ Launch
│  ├─ Chrome Direct
│  ├─ Chrome via 10708
│  ├─ Chrome via 10808
│  ├─ Edge Direct
│  └─ Edge via 10708
├─ Proxifier Profile
│  ├─ Load Direct
│  ├─ Load 10708 Rules
│  ├─ Load 10808 Rules
│  └─ Load Mixed Rules
├─ Open Config
├─ Open Proxifier
└─ Quit
```

### 2. 浏览器启动模板

Chrome 示例：

```powershell
chrome.exe --user-data-dir="E:\proxyswitch\profiles\chrome-direct" --no-proxy-server
chrome.exe --user-data-dir="E:\proxyswitch\profiles\chrome-10708" --proxy-server="socks5://127.0.0.1:10708"
chrome.exe --user-data-dir="E:\proxyswitch\profiles\chrome-10808" --proxy-server="socks5://127.0.0.1:10808"
```

说明：

- 每个代理实例必须使用独立 `user-data-dir`。
- 这样 Chrome 不会复用已有进程。
- Cookie、插件、登录态相互隔离。
- 浏览器场景可以不经过 Proxifier，启动参数更直接。

### 3. Proxifier profile 管理

建议预置四类 profile：

```text
profiles/proxifier/direct.ppx
profiles/proxifier/all-10708.ppx
profiles/proxifier/all-10808.ppx
profiles/proxifier/mixed-rules.ppx
```

含义：

- `direct.ppx`：所有应用直连。
- `all-10708.ppx`：指定应用走 10708。
- `all-10808.ppx`：指定应用走 10808。
- `mixed-rules.ppx`：不同应用按规则走不同代理。

Proxifier 规则建议：

```text
Rule: ChromeProxy10708
Applications: chrome.exe from E:\proxyswitch\profiles\chrome-10708 context if needed
Action: Proxy 127.0.0.1:10708

Rule: DevTools10808
Applications: cursor.exe, code.exe, git.exe, node.exe, python.exe
Action: Proxy 127.0.0.1:10808

Rule: DefaultDirect
Applications: Any
Action: Direct
```

注意：Proxifier 的 profile 文件格式可能包含内部结构和版本差异。第一版更稳的方式是让用户在 Proxifier 中创建并导出 `.ppx`，ProxySwitch 只负责加载这些 `.ppx`。

### 4. 端口状态检测

检测目标：

```text
127.0.0.1:10708
127.0.0.1:10808
```

状态：

- Online：TCP 可连接。
- Offline：连接失败或超时。
- Unknown：检测中或权限不足。

刷新策略：

- 启动时检测一次。
- 每 5 秒后台检测一次。
- 托盘菜单打开时强制刷新。

### 5. 配置文件

建议使用 JSON：

```json
{
  "proxies": [
    {
      "id": "p10708",
      "name": "Local 10708",
      "type": "socks5",
      "host": "127.0.0.1",
      "port": 10708
    },
    {
      "id": "p10808",
      "name": "Local 10808",
      "type": "socks5",
      "host": "127.0.0.1",
      "port": 10808
    }
  ],
  "apps": [
    {
      "id": "chrome-direct",
      "name": "Chrome Direct",
      "exe": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "mode": "browser-direct",
      "userDataDir": "E:\\proxyswitch\\profiles\\chrome-direct"
    },
    {
      "id": "chrome-10708",
      "name": "Chrome via 10708",
      "exe": "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe",
      "mode": "browser-proxy",
      "proxyId": "p10708",
      "userDataDir": "E:\\proxyswitch\\profiles\\chrome-10708"
    }
  ],
  "proxifier": {
    "enabled": true,
    "exe": "C:\\Program Files (x86)\\Proxifier\\Proxifier.exe",
    "profiles": {
      "direct": "E:\\proxyswitch\\profiles\\proxifier\\direct.ppx",
      "all10708": "E:\\proxyswitch\\profiles\\proxifier\\all-10708.ppx",
      "all10808": "E:\\proxyswitch\\profiles\\proxifier\\all-10808.ppx",
      "mixed": "E:\\proxyswitch\\profiles\\proxifier\\mixed-rules.ppx"
    }
  }
}
```

## 推荐技术栈

### 选项 A：C# / .NET 8 / WinForms

优点：

- Windows 托盘应用天然合适。
- 单文件发布比较简单。
- 调用进程、检测端口、读写 JSON 都方便。
- 不需要带 Chromium 运行时。

适合第一版。

### 选项 B：Tauri

优点：

- UI 更现代。
- Rust 后端能力强。
- 包体比 Electron 小。

缺点：

- 工程复杂度更高。
- 对这个工具来说前端 UI 不是核心。

### 选项 C：Electron

优点：

- 开发速度快。
- 托盘和菜单生态成熟。

缺点：

- 包体大。
- 为一个托盘工具引入整套 Chromium 偏重。

### 建议

第一版用 C# WinForms。它足够直接，维护成本低，和 Windows / Proxifier 的集成最顺。

## Proxifier 集成方式

### 推荐：profile 文件加载

第一版使用外部 `.ppx` profile 文件作为集成边界。

流程：

1. 用户在 Proxifier 中建好规则。
2. 导出 profile 到 `E:\proxyswitch\profiles\proxifier\`。
3. ProxySwitch 托盘菜单调用 Proxifier 加载 profile。
4. Proxifier 负责执行应用级代理。

优点：

- 不依赖 Proxifier 内部配置格式。
- 不容易被版本变更影响。
- 出问题时用户可以直接在 Proxifier UI 中排查。

### 可选：profile 生成器

后续可以研究 `.ppx` 格式，如果稳定，再加入自动生成能力。

风险：

- `.ppx` 可能随 Proxifier 版本变化。
- 自动写错 profile 可能导致规则不可用。
- 不如让 Proxifier 自己导出可靠。

## 数据目录结构

建议目录：

```text
E:\proxyswitch
├─ PLAN.md
├─ config
│  └─ proxyswitch.json
├─ profiles
│  ├─ chrome-direct
│  ├─ chrome-10708
│  ├─ chrome-10808
│  └─ proxifier
│     ├─ direct.ppx
│     ├─ all-10708.ppx
│     ├─ all-10808.ppx
│     └─ mixed-rules.ppx
├─ logs
│  └─ proxyswitch.log
└─ src
   └─ ProxySwitch
```

## MVP 交付清单

1. 创建 C# WinForms 托盘应用。
2. 读取 `config/proxyswitch.json`。
3. 托盘显示 `10708` 和 `10808` 在线状态。
4. 支持启动 Chrome Direct / Chrome 10708 / Chrome 10808。
5. 支持打开 Proxifier。
6. 支持加载 Proxifier profile。
7. 支持打开配置文件目录。
8. 写入简单日志。
9. 发布为单文件 exe。

## 风险与处理

### Chrome 参数不生效

原因通常是复用了已有 Chrome 进程。

处理：

- 必须使用不同 `--user-data-dir`。
- 必要时指定不同快捷方式图标和窗口名称。

### Proxifier 命令行能力有限

处理：

- 第一版把 `.ppx` 作为用户可维护边界。
- 如果命令行加载不稳定，退化为打开 `.ppx` 文件关联或打开 Proxifier UI。

### 代理端口协议不确定

处理：

- 配置中保留 `type` 字段。
- 支持 `socks5`、`http`、`https`。
- 浏览器启动参数根据类型生成。

### 权限问题

处理：

- 默认不需要管理员权限。
- 如果 Proxifier 本身需要提升权限，由 Proxifier 自己处理。
- ProxySwitch 不主动申请管理员权限。

## 后续版本

### v0.2

- GUI 配置页。
- 自定义应用启动模板。
- Profile 最近使用记录。
- 托盘图标颜色反映端口状态。

### v0.3

- 自动发现 Chrome / Edge / Proxifier 路径。
- 支持导入和校验 `.ppx` 文件。
- 支持应用规则预设。

### v0.4

- 支持全局热键。
- 支持从托盘快速复制启动命令。
- 支持代理延迟检测。

### v1.0

- 稳定配置 UI。
- 完整日志和错误提示。
- 安装包。
- 开机自启选项。

## 第一版开发顺序

1. 建立项目结构。
2. 实现配置读取。
3. 实现端口检测。
4. 实现托盘菜单。
5. 实现 Chrome 启动模板。
6. 实现 Proxifier 打开和 profile 加载。
7. 打包单文件 exe。
8. 用 10708 / 10808 实测。

## 成功标准

第一版完成后，应能做到：

- 同时打开 Chrome Direct 和 Chrome via 10708。
- 不修改系统代理。
- 托盘能看出两个代理端口是否在线。
- 能一键打开 Proxifier。
- 能一键加载一个 Proxifier profile。
- 常用应用代理策略不用再手写命令。
