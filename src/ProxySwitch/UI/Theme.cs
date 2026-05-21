namespace ProxySwitch.UI;

/// <summary>
/// Centralized design tokens derived from Stitch DESIGN.md (v0.8.0). All Forms
/// should reference these instead of inlining colors / fonts — single source of
/// truth so the Stitch palette stays consistent across Dashboard, Settings, and
/// the dialogs.
/// </summary>
internal static class Theme
{
    // ---- Surfaces ----
    public static readonly Color WindowBg = Color.FromArgb(248, 250, 252);          // #F8FAFC
    public static readonly Color PanelBg = Color.White;                              // #FFFFFF
    public static readonly Color SurfaceContainerLow = Color.FromArgb(242, 243, 253); // #F2F3FD (alt row)
    public static readonly Color BorderSubtle = Color.FromArgb(209, 213, 219);       // #D1D5DB
    public static readonly Color OutlineVariant = Color.FromArgb(194, 198, 214);     // #C2C6D6

    // ---- Text ----
    public static readonly Color TextPrimary = Color.FromArgb(17, 24, 39);           // #111827
    public static readonly Color TextSecondary = Color.FromArgb(107, 114, 128);      // #6B7280

    // ---- Drop-zone accents ----
    public static readonly Color AccentDirect = Color.FromArgb(107, 114, 128);       // #6B7280
    public static readonly Color AccentClash = Color.FromArgb(34, 197, 94);          // #22C55E
    public static readonly Color AccentV2ray = Color.FromArgb(59, 130, 246);         // #3B82F6

    // ---- Primary / actionable ----
    public static readonly Color PrimaryBlue = Color.FromArgb(0, 88, 190);           // #0058BE
    public static readonly Color OnPrimary = Color.White;

    // ---- Routing status text colors ----
    public static readonly Color StatusActiveGreen = Color.FromArgb(22, 101, 52);    // #166534
    public static readonly Color StatusPendingAmber = Color.FromArgb(180, 83, 9);    // #B45309
    public static readonly Color StatusFailedRed = Color.FromArgb(185, 28, 28);      // #B91C1C
    public static readonly Color StatusInfoBlue = Color.FromArgb(29, 78, 216);       // #1D4ED8

    // ---- Session row backgrounds ----
    public static readonly Color BgRunning = Color.FromArgb(220, 252, 231);          // #DCFCE7
    public static readonly Color BgViaChild = Color.FromArgb(254, 249, 195);         // #FEF9C3
    public static readonly Color BgViaCorrelated = Color.FromArgb(254, 215, 170);    // #FED7AA
    public static readonly Color BgChecking = Color.FromArgb(219, 234, 254);         // #DBEAFE
    public static readonly Color BgWaiting = Color.FromArgb(254, 243, 199);          // #FEF3C7 amber
    public static readonly Color BgExited = Color.FromArgb(243, 244, 246);           // #F3F4F6
    public static readonly Color BgFailed = Color.FromArgb(254, 226, 226);           // #FEE2E2

    // ---- Events console (dark) ----
    public static readonly Color EventsBg = Color.FromArgb(31, 41, 55);              // #1F2937 (slate-800)
    public static readonly Color EventsFg = Color.FromArgb(229, 231, 235);           // #E5E7EB (slate-200)
    public static readonly Color EventInfo = Color.FromArgb(34, 197, 94);            // #22C55E
    public static readonly Color EventDebug = Color.FromArgb(6, 182, 212);           // #06B6D4
    public static readonly Color EventWarn = Color.FromArgb(245, 158, 11);           // #F59E0B
    public static readonly Color EventError = Color.FromArgb(239, 68, 68);           // #EF4444

    // ---- Hover state ----
    public static readonly Color HoverTint = Color.FromArgb(230, 231, 242);          // #E6E7F2

    // ---- Restart-button stale tint ----
    public static readonly Color StaleRulesTint = Color.FromArgb(254, 215, 170);     // #FED7AA

    // ---- Fonts ----
    // design.md explicitly accepts Segoe UI as a fallback for Inter, and Consolas
    // for JetBrains Mono. Stay with Windows-native fonts to avoid embed/license
    // hassle and to render crisply on every machine.
    public static readonly Font BodyFont = new("Segoe UI", 9f, FontStyle.Regular);
    public static readonly Font BodySemibold = new("Segoe UI", 9f, FontStyle.Bold);
    public static readonly Font SectionHeader = new("Segoe UI", 10f, FontStyle.Bold);
    public static readonly Font DropzoneTitle = new("Segoe UI", 12f, FontStyle.Bold);
    public static readonly Font DropzoneSubtitle = new("Segoe UI", 9f, FontStyle.Regular);
    public static readonly Font ConsoleFont = new("Consolas", 9f, FontStyle.Regular);
    public static readonly Font StatusLabel = new("Segoe UI", 8.5f, FontStyle.Regular);

    // ---- Spacing (from DESIGN.md spacing block) ----
    public const int Gutter = 12;
    public const int ContainerPadding = 16;
    public const int RowHeightDense = 32;
    public const int RowHeightStandard = 44;
    public const int SectionGap = 20;

    /// <summary>
    /// Background color for a session row based on Status. Used by Sessions
    /// table painter and (during transition) the existing card painter.
    /// </summary>
    public static Color SessionRowBg(string status) => status switch
    {
        "running" => BgRunning,
        "running-via-child" => BgViaChild,
        "running-via-correlated" => BgViaCorrelated,
        "checking-correlated" => BgChecking,
        "waiting-for-restart" => BgWaiting,
        "exited" => BgExited,
        "failed" => BgFailed,
        _ => PanelBg
    };

    /// <summary>
    /// Text color for a routing-status badge. Matches design.md's status palette
    /// without re-inlining magic colors at each call site.
    /// </summary>
    public static Color RoutingColor(string routingStatus) => routingStatus switch
    {
        "browser-proxy-active" => StatusActiveGreen,
        "proxifyre-route-active" => StatusActiveGreen,
        "proxifyre-route-launching" => StatusFailedRed,
        "proxifyre-route-restarting" => StatusPendingAmber,
        "proxifyre-route-pending" => StatusPendingAmber,
        "proxifyre-route-needs-restart" => StatusPendingAmber,
        "proxifyre-route-failed" => StatusFailedRed,
        "external-routing-required" => StatusPendingAmber,
        _ => TextSecondary
    };

    /// <summary>
    /// Pick a color for an Events-panel log line based on the event type prefix.
    /// EventStore stores type strings like "ServiceRestartFailed", "RouteActivated",
    /// "BackendNotReady"; lacking a structured level, classify by suffix/keyword.
    /// </summary>
    public static Color EventLevelColor(string eventType)
    {
        if (string.IsNullOrEmpty(eventType)) return EventsFg;
        if (eventType.Contains("Failed", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Error", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Declined", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
            return EventError;
        if (eventType.Contains("Activated", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Succeeded", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Restarted", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Started", StringComparison.OrdinalIgnoreCase))
            return EventInfo;
        if (eventType.Contains("Applying", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Pending", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Stale", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("NotReady", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Required", StringComparison.OrdinalIgnoreCase))
            return EventWarn;
        if (eventType.Contains("Detected", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Tracking", StringComparison.OrdinalIgnoreCase)
            || eventType.Contains("Checking", StringComparison.OrdinalIgnoreCase))
            return EventDebug;
        return EventsFg;
    }
}
