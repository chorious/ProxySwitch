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
4. 右键托盘图标：
   - **Status**：看 10708 / 10808 是否在线
   - **Launch**：启动 Chrome / Edge（直连或走代理）
   - **Proxifier Profile**：加载 Proxifier 规则文件
   - **Settings...**：图形化编辑配置

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
| v0.3 | 📋 | 自动发现应用路径、导入 .ppx、应用规则预设 |
| v0.4 | 📋 | 全局热键、复制启动命令、代理延迟检测 |
| v1.0 | 📋 | 安装包、开机自启 |

## License

MIT
