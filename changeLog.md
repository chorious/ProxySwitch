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

## v1.0.3.2 — Store App Path-Root Session Attach

Problem (Review Report v1.0.3.2):
- AUMID activation returns a transient PID (e.g., shell broker), not the real Store App process that owns user traffic.
- The live Store Codex process (`AppData\Local\OpenAI\Codex\bin\...`) predates the AUMID launch and was never added to the IPC session.
- ProxiFyre local proxy listeners had zero accepted connections, but there were no diagnostic logs to explain why.
- ProxySwitch exited after creating the IPC session, stopping SessionSupervisor and losing the ability to discover restarted or late-started Store App processes.

Decision:
1. **Path-root process discovery for Store Apps**: After AUMID launch, wait ~2.5s, then WMI-query processes by exe name. Score candidates by path (prioritize `WindowsApps\`, `AppData\Local\Packages\`, `AppData\Local\OpenAI\Codex\`; exclude `AppData\Roaming\npm\` for CLI isolation). Derive `StoreAppRuntimeRoot` from the best match and attach all viable PIDs to the IPC session.
2. **Path-root merge condition**: `TryMergeActiveSession` now matches processes whose `ExecutablePath` starts with `StoreAppRuntimeRoot`, so `SessionSupervisor` automatically reattaches restarted/late Store App processes.
3. **ProxiFyre routing diagnostics**: Add structured `NETLIB_LOG` at `info`/`warning`/`debug` levels to `associate_process_id_to_proxy`, `get_proxy_port_tcp/udp`, `process_tcp_packet`, `cleanup_stale_pid_routes`, and proxy startup. Covers PID route add/hit/miss/stale, process lookup miss, and local proxy port mapping.
4. **ProxySwitch lifetime keep-alive**: Intercept `UserClosing` in `MainForm`. If IPC-managed sessions are active, minimize to tray instead of exiting. Tray `Quit` sets `_forceExit` and allows actual exit.

Files changed:
- `src/ProxySwitch/Models/LaunchSession.cs` — `StoreAppRuntimeRoot`
- `src/ProxySwitch/Services/SessionManager.cs` — `DiscoverStoreAppProcessesAsync`, `QueryStoreAppCandidates`, `DeriveStoreAppRuntimeRoot`, path-root merge
- `src/ProxySwitch/MainForm.cs` — `_forceExit`, `OnFormClosing` intercept, tray `Quit`
- `ProxiFyre/netlib/src/proxy/socks_local_router.h` — diagnostic logs throughout routing hot path

Known open issues as of 2026-05-26:
- `app-config.json` endpoint emission is fixed; `BuildConfigJson()` now emits all configured proxies including `127.0.0.1:10608`.
- Monorepo ProxiFyre build succeeds locally; reproducibility on a clean clone depends on `ProxiFyre/ProxiFyre/*.config` being tracked by git.
- Deployment is handled by `tools/proxifyre/build-and-deploy.ps1`; the script fails non-zero when required file copies fail.
- C4244 warning from `wchar_t`→`char` narrowing in debug-log wstring conversion is benign for ASCII paths and accepted.

## v1.0.3.3 — PID Creation-Time Diagnostics

Problem:
- IPC `AddPid` could report success while data-plane PID routing still missed.
- ProxySwitch parsed WMI `CreationDate` at second precision only, losing fractional seconds and the DMTF timezone offset.
- ProxiFyre validates PID routes against exact process creation `FILETIME` when packets arrive, so a rounded creation time makes the route look stale even though the session and PID were loaded.
- ProxiFyre config generation wrote `logLevel: Error`, hiding the new PID hit/miss diagnostics.

Decision:
- Parse WMI DMTF timestamps through `ManagementDateTimeConverter` and pass the resulting exact `FILETIME` over IPC.
- Add request/response logging around every ProxiFyre IPC call, including session id, PID, endpoint, local timestamp, and `createdAtFileTime`.
- Add ProxiFyre service logs for endpoint-to-proxy-handle registration, session creation, and PID add/remove.
- Add router logs for PID add/hit/stale, expected vs actual creation filetime, app-name fallback hits, process lookup misses, and bypass-cache passes.
- Make ProxiFyre `logLevel` configurable from `transparentBackend.logLevel`; local debug config now uses `Debug`.

## v1.0.3.4 — IPC Child Routing Recovery

Problem:
- `Route Child` still used the legacy config-file path for IPC-managed Store App sessions.
- That path wrote child executable routes, restarted ProxiFyre, and wiped the in-memory IPC session.
- Hot-added child PIDs then failed with `session not found` even though ProxySwitch still considered the session active.

Decision:
- Treat IPC-managed session children as included by default: new descendants and merged processes are added through IPC, not by writing `app-config.json`.
- Hide `Route Child` for IPC-managed active sessions; the action is only meaningful for legacy executable-rule sessions.
- If any IPC `AddPid` returns `session not found`, recreate the same session endpoint and replay all currently live PIDs before retrying the failed add.
- Keep the old config-file child-route flow only for non-IPC generic executable launches.

## Documentation Policy

- Use this file for historical decisions and version rationale.
- Use `README.md` for current usage and project facts.
- Use `localCompile.md` for build, restore, deploy, and environment issues.
- Use `LLMTips.md` only as a concise navigation card for LLM agents.
