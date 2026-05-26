# ChangeLog

This file records project history and design decisions. It is not a usage guide and should not duplicate README or localCompile.

## Version Index

| Version | Status | Summary |
|---|---|---|
| v0.1 | done | MVP launcher, port checks, browser proxy launch. |
| v0.2 | done | GUI configuration and dynamic status display. |
| v0.3 | done | Dashboard, drop zones, session tracking. |
| v0.4 | done | Child process tracking and handoff detection. |
| v0.5 | done | Routing Decoupled: removed fake Proxifier control and moved to rule hints. |
| v0.6 | done | ProxiFyre transparent per-app backend for generic apps. |
| v0.7 | done | External process watcher and persistent route attach. |
| v0.8 | done | Stitch UI refresh and dashboard/settings polish. |
| v0.9 | done | Stable app identity and session supervisor. |
| v1.0 | in progress | PID/session routing over named-pipe IPC for Store Apps. |

## v0.1 - MVP Launcher

Problem:
- Switching browser proxy settings manually was slow and error-prone.
- The app needed a simple way to launch isolated browser profiles through different proxy ports.

Decision:
- Use a WinForms launcher with JSON config.
- Launch Chrome with `--proxy-server` or direct mode.
- Add basic port checks and lightweight status display.

## v0.2 - GUI Configuration

Problem:
- Editing raw JSON was not acceptable for normal usage.

Decision:
- Add a settings UI for proxies, apps, and routes.
- Keep `proxyswitch.json` as the source of truth.

## v0.3 - Dashboard and Sessions

Problem:
- Users could launch apps, but could not see what was running or how it was routed.

Decision:
- Add dashboard drop zones, pinned apps, session cards, and event logging.
- Track launched root processes and refresh their status periodically.

## v0.4 - Child Processes and Handoff

Problem:
- Launchers such as Steam, Epic, shell brokers, and UAC flows can exit while the real app keeps running.
- A root PID alone was not enough to represent an app session.

Decision:
- Track descendants and correlated processes.
- Add handoff scoring based on process snapshots, command line, path, creation time, and parent relationships.
- Keep the session alive when the launcher exits but a credible child or correlated process remains.

## v0.5 - Routing Decoupled

Problem:
- The project pretended to control Proxifier profiles, but this could not be reliably verified end to end.
- Generic app routing was outside ProxySwitch control.

Decision:
- Remove fake Proxifier integration.
- Rename route intents around real local proxy endpoints such as Clash Verge and v2ray.
- Keep browser proxy launch under direct ProxySwitch control.
- For generic apps, expose copyable route hints until a real transparent backend exists.

## v0.6 - ProxiFyre Backend

Problem:
- Generic desktop apps needed real per-app transparent routing.

Decision:
- Integrate open-source ProxiFyre as the transparent backend.
- ProxySwitch writes `backend/proxifyre/app-config.json`.
- ProxiFyre service reads app-name/path rules and redirects matching TCP/UDP traffic to SOCKS5 endpoints.

Important tradeoff:
- Legacy ProxiFyre routes are global app-name/path rules. They are useful for explicit desktop apps, but unsafe for Store App sessions where child names can be generic.

## v0.7 - External Process Watcher

Problem:
- Persistent routes should be visible even when the app is started outside ProxySwitch.

Decision:
- Add an external process watcher that attaches matching processes to session cards.
- Keep route state visible for app routes created earlier.

## v0.8 - UI Refresh

Problem:
- The dashboard and settings UI were becoming dense and inconsistent.

Decision:
- Add shared UI tokens and components.
- Rework launch zones, pill buttons, status display, events, tabs, and settings layout.
- Prefer in-place rebinding and stable sizing over full UI resets.

## v0.9 - Stable App Identity and Session Supervisor

Problem:
- Store Apps and MSIX apps are better identified by package/app identity than by process name.
- Some app sessions exit and restart within a short window, causing false session death.

Decision:
- Add App Identity Resolver for AUMID, package family, manifest, and executable path resolution.
- Add Session Supervisor with a restart grace window.
- Reattach compatible restarted processes to the original session.

## v1.0 - PID/Session Routing

Problem:
- Store App routing by global `appNames` polluted unrelated processes.
- Rules such as `codex.exe`, `git.exe`, `python.exe`, `powershell.exe`, or `conda.exe` can accidentally capture CLI sessions.
- Every config change could restart ProxiFyre and trigger UAC.
- App-name rules have no session boundary and no automatic cleanup.

Decision:
- Add runtime PID routing to ProxiFyre.
- Add named-pipe IPC between ProxySwitch and the ProxiFyre service.
- Store App launches create a session, then add root/child/correlated PIDs to that session.
- PID entries include process creation time to guard against PID reuse.
- Store App IPC routes are ephemeral and should not be written to persistent appRoutes.

Core implementation:
- `ProxiFyre/ProxiFyre/SessionRouteServer.cs`: named-pipe IPC server.
- `src/ProxySwitch/Services/ProxiFyreIpcClient.cs`: IPC client.
- `src/ProxySwitch/Services/SessionManager.cs`: Store App IPC session lifecycle.
- `ProxiFyre/netlib/src/proxy/socks_local_router.h`: `pid_to_proxy_` route table and PID creation-time validation.

Known open issues as of 2026-05-26:
- `app-config.json` endpoint emission is fixed; `BuildConfigJson()` now emits all configured proxies including `127.0.0.1:10608`.
- Monorepo ProxiFyre build succeeds locally; reproducibility on a clean clone depends on `ProxiFyre/ProxiFyre/*.config` being tracked by git.
- Deployment is handled by `tools/proxifyre/build-and-deploy.ps1`; the script fails non-zero when required file copies fail.

## Documentation Policy

- Use this file for historical decisions and version rationale.
- Use `README.md` for current usage and project facts.
- Use `localCompile.md` for build, restore, deploy, and environment issues.
- Use `LLMTips.md` only as a concise navigation card for LLM agents.