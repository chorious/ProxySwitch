# LLMTips - ProxySwitch

This is the short navigation card for Codex, CC, and other LLM agents. Keep it concise. Do not turn it into a changelog or architecture essay.

## Read First

1. `README.md` for current project facts and usage.
2. `LLMTips.md` for file map and rules.
3. `localCompile.md` when build, deploy, dependencies, or service state is involved.
4. `changeLog.md` when design history or version rationale matters.

## Project Shape

- `src/ProxySwitch/`: .NET 8 WinForms app, MIT licensed.
- `ProxiFyre/`: integrated ProxiFyre fork, C++/CLI + C# service, AGPL-3.0.
- `backend/proxifyre/`: local runtime ProxiFyre binaries and `app-config.json`; ignored.
- `config/proxyswitch.json`: user config and persistent app routes.
- `plan&review/`: local plans, review reports, and temporary analysis; ignored.

## Core Flows

Browser launch:

```text
Dashboard -> SessionManager -> AppLauncher -> chrome/msedge --proxy-server or direct
```

Generic desktop app route:

```text
Dashboard/drop -> SessionManager -> ProxiFyreBackend -> app-config.json -> service restart
```

PID/session route:

```text
AUMID or IPC-managed launch -> SessionManager -> ProxiFyreIpcClient -> SessionRouteServer -> pid_to_proxy_
```

Destination direct rule:

```text
packet -> socks_local_router -> destinationRules(process/protocol/port/CIDR) -> DIRECT or SOCKS5
```

## Hot Files

| Area | Files |
|---|---|
| Session lifecycle | `src/ProxySwitch/Services/SessionManager.cs` |
| App launching | `src/ProxySwitch/Services/AppLauncher.cs` |
| External process attach | `src/ProxySwitch/Services/ExternalProcessWatcher.cs`, `SessionSupervisor.cs` |
| App identity | `src/ProxySwitch/Services/AppIdentityResolver.cs` |
| ProxiFyre config | `src/ProxySwitch/Services/ProxiFyreBackend.cs` |
| IPC client | `src/ProxySwitch/Services/ProxiFyreIpcClient.cs` |
| IPC server | `ProxiFyre/ProxiFyre/SessionRouteServer.cs` |
| PID and destination router | `ProxiFyre/netlib/src/proxy/socks_local_router.h` |
| C++/CLI wrapper | `ProxiFyre/socksify/Socksifier.*`, `socksify_unmanaged.*` |
| Config model | `src/ProxySwitch/Models/ProxyConfig.cs`, `LaunchSession.cs` |
| Steam direct rules | `backend/proxifyre/rules/steam-valve-ipv4.txt`, `steam-observed-download-cdn-ipv4.txt` |

## Non-Negotiables

- Do not route Store Apps through broad global process-name rules.
- Do not add `codex.exe`, `git.exe`, `python.exe`, `powershell.exe`, or `conda.exe` as persistent ProxiFyre `appNames`.
- PID routes must validate process creation time to avoid PID reuse bugs.
- Store App IPC sessions are ephemeral; they should not persist child process names to `proxyswitch.json`.
- MSIX/AUMID identity routes must not fall back to bare process-name matching; that can capture unrelated CLI tools with the same exe name.
- Keep Steam direct rules scoped to `steam.exe`; do not direct-route `steamwebhelper.exe`.
- Steam CDN/download bypass belongs in ProxiFyre destination rules, not Clash domain rules.
- Runtime ProxiFyre binaries in `backend/proxifyre` must match the monorepo source when testing IPC.
- `app-config.json` must include SOCKS5 endpoints needed by IPC, even if their `appNames` list is empty.

## Common Tasks

- Build ProxySwitch: see `localCompile.md`.
- Build or deploy ProxiFyre: see `localCompile.md`.
- Understand PID/session routing: read `changeLog.md` v1.0 and `SessionManager.cs`.
- Diagnose Store App routing: check `ProxiFyreIpcClient.cs`, `SessionRouteServer.cs`, `app-config.json`, and ProxiFyre service logs.
- Diagnose Steam downloads: check ProxiFyre `TrafficDecision` logs and `backend/proxifyre/rules/`.
- Diagnose Steam web/Workshop SSL issues: verify `steamwebhelper.exe` is proxied and Clash sniffer is enabled for Steam domains.
- Diagnose Chrome routing: inspect browser launch args and existing Chrome profile/process state before touching ProxiFyre.

## Commit Documentation Checklist

Before committing, check whether the change affects these docs:

- User-visible usage, config, directory structure, or license: update `README.md`.
- Version behavior, architectural decision, or migration rationale: update `changeLog.md`.
- Build tools, dependencies, deploy path, service setup, or known local failure: update `localCompile.md`.
- Hot files, core flows, non-negotiables, or common LLM tasks: update `LLMTips.md`.

Do not update a document just to create churn. If no category changed, leave the docs alone.
