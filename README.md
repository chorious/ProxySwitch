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
- [ProxiFyre](ProxiFyre/)（可选，用于透明 per-app 代理；Store App 路由在 v1.0+ 变为硬依赖）

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

详见 [`localCompile.md`](localCompile.md)。

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
