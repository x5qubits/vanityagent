using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>
/// Native OS-level Computer &amp; Screen perception tool.
/// Provides visual screen capture (JPEG data URLs) and synthetic mouse/keyboard control
/// to interact with arbitrary desktop software (browsers, IDEs, Photoshop, native apps).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ComputerTool : IVisualTool
{
    private const int MaxEdge = 1440;
    private const int JpegQuality = 70;

    // The desktop is one shared resource: two actions running at once would interleave their mouse-down/up or
    // modifier-down/up sequences (a stuck Shift, a click released mid-drag). One process-wide gate serializes every
    // action across all instances (two personas on the screen at once, a parallel agent batch).
    private static readonly SemaphoreSlim InputGate = new(1, 1);

    private readonly object _lock = new();
    private int _shotW, _shotH;
    private int _vsX, _vsY, _vsW, _vsH;
    private bool _haveShot;
    private int _screen = int.MinValue;

    public ComputerTool(int defaultScreen = int.MinValue)
    {
        _screen = defaultScreen;
    }

    public ToolDefinition Definition { get; } = new()
    {
        Name = "computer",
        Description =
            "See and control this computer's screen and desktop apps (GUI automation). Use it for anything that has no " +
            "command-line path: clicking buttons, filling desktop app forms, menus, drag-and-drop, reading what is on " +
            "screen. Prefer the shell tools for anything scriptable (files, installs, app CLIs) - they are faster and " +
            "exact. WORKFLOW: call action='screenshot' first to see the screen, then issue ONE action (click/type/etc.) " +
            "using coordinates read from that screenshot, and you will get a fresh image back to check the result. " +
            "Coordinates are pixels in the image you were last given (top-left is 0,0). Every image marks the REAL " +
            "cursor position with a red crosshair. Pointer actions (move/click/scroll/drag) return a NATIVE-resolution " +
            "close-up of where they acted, so you always see the exact control under the cursor. CLICKS ARE " +
            "AIM-THEN-COMMIT: a click on a fresh target first only moves the cursor there and returns the close-up " +
            "(with the hover tooltip naming the control); repeat the same click at the same spot to actually press. " +
            "So the efficient flow is: screenshot -> click your target (acts as the aim) -> check the crosshair/" +
            "tooltip in the close-up -> repeat the click to press (or correct the spot first). For tiny targets use " +
            "action='zoom' before aiming. find_element clicks a control by its accessible name without coordinates. " +
            "VERIFY honestly: the expected UI change must be VISIBLE in the returned image; if the state did not " +
            "change or a neighboring control reacted, the action missed - correct it, never claim success from an " +
            "unchanged screen.",
        Parameters = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["action"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["enum"] = new[]
                    {
                        "screenshot", "zoom", "cursor_position", "mouse_move", "left_click", "right_click", "middle_click",
                        "double_click", "left_click_drag", "scroll", "type", "key", "launch", "wait",
                        "find_element", "get_window_text",
                    },
                    ["description"] =
                        "screenshot: capture the screen. zoom: native-resolution close-up at [x,y] - use it whenever the " +
                        "target is small or text is unreadable; your next coordinates refer to the zoomed image and clicks " +
                        "there are pixel-accurate (a new screenshot zooms back out). " +
                        "mouse_move/left_click/right_click/middle_click/double_click: at [x,y] (first click on a fresh spot = aim, repeat = press). " +
                        "left_click_drag: press at [x,y] and release at to=[x,y]. scroll: at [x,y] in direction by amount. " +
                        "type: enter text (end with \\n to submit). key: press hotkeys ('ctrl c', 'alt f4', 'enter'). " +
                        "launch: start executable or application by path/name. wait: delay seconds. " +
                        "find_element: find and click a UI element by its visible label/text (uses Windows Accessibility API — works without knowing pixel coords). " +
                        "get_window_text: list all visible text in the foreground window via accessibility tree.",
                },
                ["coordinate"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "integer" },
                    ["description"] = "[x, y] in the last screenshot's pixel space.",
                },
                ["to"] = new Dictionary<string, object>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object> { ["type"] = "integer" },
                    ["description"] = "[x, y] release target for left_click_drag.",
                },
                ["text"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "Text to type, key combo to press ('ctrl c'), or app name/path to launch.",
                },
                ["path"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["description"] = "Executable path or command name to launch (action='launch').",
                },
                ["direction"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "up", "down", "left", "right" },
                    ["description"] = "Scroll direction (action='scroll'). Default 'down'.",
                },
                ["amount"] = new Dictionary<string, object>
                {
                    ["type"] = "integer",
                    ["description"] = "Scroll amount in notches (action='scroll'). Default 3.",
                },
                ["seconds"] = new Dictionary<string, object>
                {
                    ["type"] = "number",
                    ["description"] = "Seconds to wait (action='wait'). Default 2, max 15.",
                },
                ["screen"] = new Dictionary<string, object>
                {
                    ["type"] = "integer",
                    ["description"] = "Monitor index (0, 1, ...) or -1 for virtual desktop. Default is primary.",
                },
            },
            ["required"] = new[] { "action" },
        },
    };

    public async Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
    {
        var result = await ExecuteVisualAsync("", argsJson, ct).ConfigureAwait(false);
        return result.Output;
    }

    public async Task<ToolResultRecord> ExecuteVisualAsync(string toolCallId, string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var args = doc.RootElement;

        string action = "";
        if (args.TryGetProperty("action", out var actEl) && actEl.ValueKind == JsonValueKind.String)
            action = actEl.GetString()?.Trim().ToLowerInvariant() ?? "";

        if (string.IsNullOrWhiteSpace(action))
            return Fail(toolCallId, "computer tool requires an 'action'.");

        Log.Debug($"[computer] action: {action}");

        if (args.TryGetProperty("screen", out var scGlobal) && scGlobal.ValueKind == JsonValueKind.Number && scGlobal.TryGetInt32(out int siGlobal))
        {
            lock (_lock) _screen = siGlobal;
        }

        await InputGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunActionAsync(toolCallId, action, args, ct).ConfigureAwait(false);
        }
        finally
        {
            InputGate.Release();
        }
    }

    private async Task<ToolResultRecord> RunActionAsync(string toolCallId, string action, JsonElement args, CancellationToken ct)
    {
        try
        {
            switch (action)
            {
                case "screenshot":
                {
                    if (args.TryGetProperty("screen", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out int si))
                    {
                        lock (_lock) _screen = si;
                    }
                    return Capture(toolCallId, "screenshot");
                }

                case "cursor_position":
                {
                    GetCursorPos(out POINT pt);
                    return ToImageSpace(pt.X, pt.Y, out int ix, out int iy)
                        ? Capture(toolCallId, $"cursor is at ({ix},{iy}) in image space")
                        : Capture(toolCallId, $"cursor is at real ({pt.X},{pt.Y}); take a screenshot first to establish image frame");
                }

                case "mouse_move":
                {
                    if (!MapCoord(args, "coordinate", out int rx, out int ry, out string err)) return Fail(toolCallId, err);
                    if (!MoveCursorVerified(rx, ry, out string merr)) return Fail(toolCallId, merr);
                    // Long enough for the hovered control's TOOLTIP to appear in the close-up - hovering is how you
                    // IDENTIFY a small ambiguous button (e.g. stop vs restart) before committing to a click.
                    await Task.Delay(900, ct).ConfigureAwait(false);
                    return CaptureZoom(toolCallId, rx, ry, "Cursor moved - the crosshair must sit EXACTLY on your target (a " +
                        "tooltip, if one appeared, names the hovered control); if it is on a neighboring control, " +
                        "move again before clicking.", 800, 600);
                }

                case "zoom":
                {
                    if (!MapCoord(args, "coordinate", out int rx, out int ry, out string err)) return Fail(toolCallId, err);
                    return CaptureZoom(toolCallId, rx, ry);
                }

                case "left_click":
                case "right_click":
                case "middle_click":
                case "double_click":
                {
                    if (args.TryGetProperty("coordinate", out _))
                    {
                        if (!MapCoord(args, "coordinate", out int rx, out int ry, out string err)) return Fail(toolCallId, err);
                        if (InputBlockCheck("click", rx, ry) is { } cblock) return Fail(toolCallId, cblock);
                        // AIM-THEN-COMMIT: a click on a point the cursor is NOT already on does not press anything -
                        // it moves there and shows the close-up (with hover tooltip) so the model SEES what is under
                        // the crosshair before committing. Repeating the click at the same spot performs the press.
                        // Cost-neutral for a disciplined mouse_move-then-click flow; blocks the observed failure mode
                        // of pressing a near-identical neighbor (stop vs restart) straight from the low-res wide view.
                        bool aimed = GetCursorPos(out POINT cur) && Math.Abs(cur.X - rx) <= 25 && Math.Abs(cur.Y - ry) <= 25;
                        if (!MoveCursorVerified(rx, ry, out string merr)) return Fail(toolCallId, merr);
                        if (!aimed)
                        {
                            await Task.Delay(900, ct).ConfigureAwait(false);   // let the hover tooltip appear
                            return CaptureZoom(toolCallId, rx, ry, "AIM CHECK - NOT clicked yet. The crosshair now sits on your " +
                                "requested point; the tooltip (if one appeared) names the hovered control. If this is the " +
                                "RIGHT control, repeat the click action at the crosshair coordinate stated in this result to PRESS it " +
                                "(this close-up is the new coordinate frame - do not reuse the full-screenshot numbers). " +
                                "If it is a neighboring control, click at the corrected spot in this close-up instead.", 800, 600);
                        }
                        await Task.Delay(40, ct).ConfigureAwait(false);
                        Click(action, rx, ry);
                        await Task.Delay(140, ct).ConfigureAwait(false);   // let the UI react before we re-observe
                        return CaptureZoom(toolCallId, rx, ry, action.Replace('_', ' ') + " done. VERIFY in this close-up that the " +
                            "RIGHT control was pressed and the expected change happened. If the state did not change or " +
                            "the wrong control reacted, correct it now; never report success from an unchanged screen.", 800, 600);
                    }
                    else
                    {
                        // No coordinate = press where the cursor already is (the commit half of aim-then-commit).
                        GetCursorPos(out POINT pt);
                        if (InputBlockCheck("click", pt.X, pt.Y) is { } cblock) return Fail(toolCallId, cblock);
                        Click(action, pt.X, pt.Y);
                        await Task.Delay(140, ct).ConfigureAwait(false);
                        return CaptureZoom(toolCallId, pt.X, pt.Y, action.Replace('_', ' ') + " done at the cursor. VERIFY the " +
                            "expected change in this close-up; never report success from an unchanged screen.", 800, 600);
                    }
                }

                case "left_click_drag":
                {
                    if (!MapCoord(args, "coordinate", out int sx, out int sy, out string e1)) return Fail(toolCallId, e1);
                    if (!MapCoord(args, "to", out int ex, out int ey, out string e2)) return Fail(toolCallId, "left_click_drag needs 'to' [x,y]: " + e2);
                    if (InputBlockCheck("drag", sx, sy) is { } dblock) return Fail(toolCallId, dblock);
                    if (!MoveCursorVerified(sx, sy, out string merr)) return Fail(toolCallId, merr);
                    await Task.Delay(60, ct).ConfigureAwait(false);
                    MouseDown();
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    Steps(sx, sy, ex, ey, 12, ct);   // step the move so drag-aware UIs register it
                    SetCursorPos(ex, ey);
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    MouseUp();
                    await Task.Delay(140, ct).ConfigureAwait(false);
                    return CaptureZoom(toolCallId, ex, ey, $"Dragged from ({sx},{sy}) to ({ex},{ey}) - verify the drop landed where intended.", 800, 600);
                }

                case "scroll":
                {
                    bool at = false; int rx = 0, ry = 0;
                    if (args.TryGetProperty("coordinate", out _))
                    {
                        if (!MapCoord(args, "coordinate", out rx, out ry, out string err)) return Fail(toolCallId, err);
                        if (InputBlockCheck("scroll", rx, ry) is { } sblock) return Fail(toolCallId, sblock);
                        if (!MoveCursorVerified(rx, ry, out string merr)) return Fail(toolCallId, merr);
                        at = true;
                    }
                    else if (InputBlockCheck("scroll") is { } sblock) return Fail(toolCallId, sblock);
                    string dir = (args.TryGetProperty("direction", out var de) ? de.GetString() : "down")?.Trim().ToLowerInvariant() ?? "down";
                    if (dir is not ("up" or "down" or "left" or "right"))
                        return Fail(toolCallId, "direction must be up, down, left or right (got '" + dir + "').");
                    int amount = args.TryGetProperty("amount", out var ae) && ae.ValueKind == JsonValueKind.Number ? Math.Clamp(ae.GetInt32(), 1, 30) : 3;
                    await Task.Delay(40, ct).ConfigureAwait(false);
                    Scroll(dir, amount);
                    await Task.Delay(160, ct).ConfigureAwait(false);
                    return at ? CaptureZoom(toolCallId, rx, ry, $"Scrolled {dir} x{amount}.", 800, 600)
                              : Capture(toolCallId, $"scrolled {dir} x{amount}");
                }

                case "type":
                {
                    string text = args.TryGetProperty("text", out var te) ? te.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(text)) return Fail(toolCallId, "type needs non-empty 'text'.");
                    text = text.Replace("\r\n", "\n");   // one line break per newline; "\r\n" used to press Enter twice
                    if (InputBlockCheck("keyboard") is { } tblock) return Fail(toolCallId, tblock);
                    bool submit = text.EndsWith('\n');
                    string body = submit ? text[..^1] : text;
                    TypeUnicode(body, ct);
                    if (submit)
                    {
                        await Task.Delay(40, ct).ConfigureAwait(false);
                        TapVk(VK_RETURN);
                    }
                    await Task.Delay(80, ct).ConfigureAwait(false);
                    return Capture(toolCallId, submit ? "typed and submitted (Enter)" : "typed text");
                }

                case "key":
                {
                    string combo = args.TryGetProperty("text", out var ke) ? ke.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(combo)) return Fail(toolCallId, "key needs 'text' specifying key combo (e.g. 'ctrl c', 'enter').");
                    if (InputBlockCheck("keyboard") is { } kblock) return Fail(toolCallId, kblock);
                    if (!Hotkey(combo, out string kerr)) return Fail(toolCallId, kerr);
                    await Task.Delay(140, ct).ConfigureAwait(false);
                    return Capture(toolCallId, $"pressed '{combo}'");
                }

                case "launch":
                {
                    string path = "";
                    if (args.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String)
                        path = pe.GetString() ?? "";
                    else if (args.TryGetProperty("text", out var te) && te.ValueKind == JsonValueKind.String)
                        path = te.GetString() ?? "";

                    if (string.IsNullOrWhiteSpace(path))
                        return Fail(toolCallId, "launch needs 'path' or 'text' specifying executable (e.g. 'notepad', 'calc').");

                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = path,
                            UseShellExecute = true,
                        };
                        Process.Start(psi);
                    }
                    catch (Exception ex)
                    {
                        return Fail(toolCallId, $"Failed to launch '{path}': {ex.Message}");
                    }
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    return Capture(toolCallId, $"launched '{path}'");
                }

                case "wait":
                {
                    double secs = 2;
                    if (args.TryGetProperty("seconds", out var se) && se.ValueKind == JsonValueKind.Number)
                        secs = se.GetDouble();
                    secs = Math.Clamp(secs, 0.2, 15.0);
                    await Task.Delay((int)(secs * 1000), ct).ConfigureAwait(false);
                    return Capture(toolCallId, $"waited {secs:0.#}s");
                }

                case "find_element":
                {
                    string needle = (args.TryGetProperty("text", out var nt) ? nt.GetString() : null)?.Trim() ?? "";
                    if (string.IsNullOrEmpty(needle)) return Fail(toolCallId, "find_element needs 'text' — the visible label/name of the element.");
                    bool doClick = !(args.TryGetProperty("click", out var cl) && cl.ValueKind == JsonValueKind.False);
                    if (!FindElementByName(needle, out int ex, out int ey))
                        return Fail(toolCallId, $"Element '{needle}' not found in foreground window.");
                    if (doClick)
                    {
                        if (!MoveCursorVerified(ex, ey, out string merr)) return Fail(toolCallId, merr);
                        await Task.Delay(40, ct).ConfigureAwait(false);
                        Click("left_click", ex, ey);
                        await Task.Delay(100, ct).ConfigureAwait(false);
                        return Capture(toolCallId, $"found and clicked '{needle}'");
                    }
                    return Capture(toolCallId, $"found '{needle}' at screen ({ex},{ey})");
                }

                case "get_window_text":
                {
                    string text = GetAccessibleWindowText();
                    var info = GetForegroundWindowInfo(Monitors());
                    string title = info?.Title ?? "(no foreground window)";
                    return new ToolResultRecord
                    {
                        ToolCallId = toolCallId, ToolName = "computer",
                        Output = $"[Window: '{title}']\n{text}",
                    };
                }

                default:
                    return Fail(toolCallId, $"Unknown computer action '{action}'.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(toolCallId, $"computer action '{action}' failed: {ex.Message}");
        }
    }

    private static ToolResultRecord Fail(string toolCallId, string message) => new()
    {
        ToolCallId = toolCallId,
        ToolName = "computer",
        IsError = true,
        Output = message,
    };

    // ── Screen Perception ────────────────────────────────────────────────────────

    /// <summary>Resolve the capture area from the current screen selection: -1 = all monitors; a valid index = that
    /// monitor; default (unset / out of range) = the primary monitor (else the first).</summary>
    private (List<Mon> Mons, Rectangle Area, string Which, int Sel) SelectArea()
    {
        var mons = Monitors();
        Rectangle area;
        string which;
        int sel;
        lock (_lock) sel = _screen;

        if (sel == -1)
        {
            area = VirtualBounds();
            which = "all monitors";
        }
        else if (sel >= 0 && sel < mons.Count)
        {
            area = mons[sel].Bounds;
            which = $"monitor {sel} ({mons[sel].Label})";
        }
        else
        {
            var prim = mons.FirstOrDefault(m => m.Primary);
            if (mons.Count == 0)
            {
                area = VirtualBounds();
                which = "all monitors";
            }
            else
            {
                var m = prim.Bounds.Width > 0 ? prim : mons[0];
                area = m.Bounds;
                which = $"monitor {m.Index} ({m.Label})";
            }
        }
        return (mons, area, which, sel);
    }

    /// <summary>Draw a red crosshair (white halo) where the cursor REALLY is, so the model can visually verify its
    /// aim on every image instead of guessing. Skipped when the cursor is outside the captured area.</summary>
    private static void DrawCursorMarker(Bitmap img, Rectangle area)
    {
        if (!GetCursorPos(out POINT p)) return;
        if (p.X < area.X || p.X >= area.X + area.Width || p.Y < area.Y || p.Y >= area.Y + area.Height) return;
        int cx = (int)((p.X - area.X) * (long)img.Width / Math.Max(1, area.Width));
        int cy = (int)((p.Y - area.Y) * (long)img.Height / Math.Max(1, area.Height));
        void Px(int x, int y, Color c) { if (x >= 0 && x < img.Width && y >= 0 && y < img.Height) img.SetPixel(x, y, c); }
        for (int d = -9; d <= 9; d++)
        {
            if (Math.Abs(d) < 2) continue;   // keep the exact hotspot pixel unobscured
            Px(cx + d, cy - 1, Color.White); Px(cx + d, cy + 1, Color.White);
            Px(cx - 1, cy + d, Color.White); Px(cx + 1, cy + d, Color.White);
            Px(cx + d, cy, Color.Red); Px(cx, cy + d, Color.Red);
        }
    }

    /// <summary>Native-resolution close-up around a real-screen point. No downscale, so tiny toolbar buttons and
    /// small text are actually legible; the coordinate mapping is retargeted to the zoomed region, so the model's
    /// next click INSIDE the zoom view is pixel-accurate. A normal screenshot zooms back out.</summary>
    private ToolResultRecord CaptureZoom(string toolCallId, int realX, int realY, string? lead = null, int zw = 1000, int zh = 750)
    {
        var (_, area, which, _) = SelectArea();
        if (area.Width <= 0 || area.Height <= 0) return Fail(toolCallId, "no display screen available (headless session?).");
        int w = Math.Min(zw, area.Width), h = Math.Min(zh, area.Height);
        int x0 = Math.Clamp(realX - w / 2, area.X, area.X + area.Width - w);
        int y0 = Math.Clamp(realY - h / 2, area.Y, area.Y + area.Height - h);
        var region = new Rectangle(x0, y0, w, h);

        string dataUrl;
        using (var img = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(img))
                g.CopyFromScreen(x0, y0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            DrawCursorMarker(img, region);
            dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(EncodeJpeg(img, 80));   // crisper: this view exists to READ
        }

        lock (_lock)
        {
            _shotW = w; _shotH = h;
            _vsX = x0; _vsY = y0; _vsW = w; _vsH = h;
            _haveShot = true;
        }
        return new ToolResultRecord
        {
            ToolCallId = toolCallId,
            ToolName = "computer",
            Output = $"[zoom {w}x{h} NATIVE-resolution close-up of {which}] " + (lead is null ? "" : lead + " ") +
                     $"Coordinates now refer to THIS close-up; the crosshair (your requested point) is at [{realX - x0},{realY - y0}] here. " +
                     "Coordinates you give next are pixels in THIS zoomed image (0,0 = top-left) - clicks here are " +
                     "pixel-accurate. The red crosshair is the current cursor position. Take action='screenshot' to zoom " +
                     "back out to the full screen.",
            ScreenshotDataUrl = dataUrl,
        };
    }

    private ToolResultRecord Capture(string toolCallId, string note)
    {
        var (mons, area, which, sel) = SelectArea();

        if (area.Width <= 0 || area.Height <= 0)
            return Fail(toolCallId, "no display screen available (headless session?).");

        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(area.X, area.Y, 0, 0, new Size(area.Width, area.Height), CopyPixelOperation.SourceCopy);
        }

        double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(area.Width, area.Height));
        int outW = Math.Max(1, (int)Math.Round(area.Width * scale));
        int outH = Math.Max(1, (int)Math.Round(area.Height * scale));

        string dataUrl;
        bool blank;
        using (var scaled = new Bitmap(outW, outH, PixelFormat.Format24bppRgb))
        {
            using (var g2 = Graphics.FromImage(scaled))
            {
                g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g2.DrawImage(full, 0, 0, outW, outH);
            }
            blank = IsBlank(scaled);
            if (!blank) DrawCursorMarker(scaled, area);
            dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(EncodeJpeg(scaled, JpegQuality));
        }

        lock (_lock)
        {
            _shotW = outW;
            _shotH = outH;
            _vsX = area.X;
            _vsY = area.Y;
            _vsW = area.Width;
            _vsH = area.Height;
            _haveShot = true;
        }

        if (blank)
        {
            return new ToolResultRecord
            {
                ToolCallId = toolCallId,
                ToolName = "computer",
                IsError = true,
                ScreenshotDataUrl = dataUrl,
                Output = $"[screenshot {outW}x{outH} of {which}] The captured screen is BLANK (all black). This usually means the machine is LOCKED, or this app is running without access to the interactive desktop (a Session-0 / 'run whether logged on or not' service). GUI automation cannot work in this state. Do NOT pretend to see or click anything - tell the operator the screen isn't visible (unlock the machine / run the host in the logged-in session) and stop.",
            };
        }

        string monitorList = mons.Count <= 1 ? "" :
            " Monitors: " + string.Join(", ", mons.Select(m => $"[{m.Index}] {m.Label}")) + ".";
        string fgNotice = "";
        var fg = GetForegroundWindowInfo(mons);
        if (fg.HasValue && !string.IsNullOrEmpty(fg.Value.Title))
        {
            if (sel != -1 && fg.Value.MonitorIndex != -1 && fg.Value.MonitorIndex != sel)
            {
                fgNotice = $" [ALERT: Active foreground/modal window '{fg.Value.Title}' is on monitor {fg.Value.MonitorIndex}, NOT on current monitor {sel}! Switch with screen={fg.Value.MonitorIndex} or screen=-1 to interact with it.]";
            }
            else if (sel != -1)
            {
                fgNotice = $" [Active window: '{fg.Value.Title}']";
            }
        }

        string text = $"[screenshot {outW}x{outH} of {which}] {note}. Coordinates you give next are pixels in THIS image " +
                      "(0,0 = top-left). The red crosshair is the current cursor position. If your target is small or hard " +
                      $"to read, use action='zoom' at its location before clicking.{fgNotice}{monitorList}";

        return new ToolResultRecord
        {
            ToolCallId = toolCallId,
            ToolName = "computer",
            Output = text,
            ScreenshotDataUrl = dataUrl,
        };
    }

    private static bool IsBlank(Bitmap bmp)
    {
        int stepX = Math.Max(1, bmp.Width / 24);
        int stepY = Math.Max(1, bmp.Height / 24);
        int bright = 0, total = 0;
        for (int y = 0; y < bmp.Height; y += stepY)
        {
            for (int x = 0; x < bmp.Width; x += stepX)
            {
                total++;
                var c = bmp.GetPixel(x, y);
                if (c.R > 14 || c.G > 14 || c.B > 14) bright++;
            }
        }
        return total > 0 && bright * 200 < total;
    }

    private static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        var enc = GetJpegEncoder();
        using var ms = new MemoryStream();
        if (enc != null)
        {
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
            bmp.Save(ms, enc, ps);
        }
        else
        {
            bmp.Save(ms, ImageFormat.Jpeg);
        }
        return ms.ToArray();
    }

    private static ImageCodecInfo? GetJpegEncoder()
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        return null;
    }

    // ── Coordinate Mapping ──────────────────────────────────────────────────────
    private bool MapCoord(JsonElement args, string key, out int realX, out int realY, out string err)
    {
        realX = realY = 0; err = "";
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var arr)
            || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 2)
        {
            err = $"'{key}' must be an array of [x, y].";
            return false;
        }

        int ix = arr[0].GetInt32(), iy = arr[1].GetInt32();
        lock (_lock)
        {
            if (!_haveShot) { err = "take a screenshot first to establish coordinate space."; return false; }
            double sx = _shotW > 0 ? (double)_vsW / _shotW : 1.0;
            double sy = _shotH > 0 ? (double)_vsH / _shotH : 1.0;
            realX = _vsX + (int)Math.Round(ix * sx);
            realY = _vsY + (int)Math.Round(iy * sy);
        }
        return true;
    }

    private bool ToImageSpace(int realX, int realY, out int ix, out int iy)
    {
        lock (_lock)
        {
            if (!_haveShot || _vsW <= 0 || _vsH <= 0) { ix = iy = 0; return false; }
            ix = (int)Math.Round((realX - _vsX) * (double)_shotW / _vsW);
            iy = (int)Math.Round((realY - _vsY) * (double)_shotH / _vsH);
        }
        return true;
    }

    private static bool MoveCursorVerified(int rx, int ry, out string err)
    {
        err = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            SetCursorPos(rx, ry);
            Thread.Sleep(30);
            if (GetCursorPos(out POINT p) && Math.Abs(p.X - rx) <= 3 && Math.Abs(p.Y - ry) <= 3) return true;
            Thread.Sleep(80);
        }
        GetCursorPos(out POINT now);
        err = $"the OS refused to move the cursor to ({rx},{ry}) - it is at ({now.X},{now.Y}). The action was NOT " +
              "performed." + ForegroundInputDiagnosis() + " Take a screenshot to see the current state and " +
              "report the blocker if it persists.";
        return false;
    }

    // ── Input blocking (UIPI / secure desktop) ───────────────────────────────────

    /// <summary>Names the foreground window/process and compares its elevation to ours. The #1 real-world input
    /// blocker is an elevated target with a non-elevated agent (Windows UIPI) - say it outright so the model reports
    /// the actual fix instead of guessing.</summary>
    private static string ForegroundInputDiagnosis()
    {
        try
        {
            GetForegroundState(out bool weElevated, out bool? fgElevated, out string who);
            if (who.Length == 0)
                return " No window holds the foreground (secure desktop: UAC prompt, lock screen, or Ctrl+Alt+Del surface) - no synthetic input can reach those.";
            if (fgElevated == true && !weElevated)
                return " Foreground window " + who + " runs ELEVATED (as administrator) while this agent does NOT - Windows blocks all synthetic mouse and keyboard input to it. Fix: run this agent as administrator, or run the target app non-elevated.";
            if (fgElevated == null && !weElevated)
                return " Foreground window " + who + " could not be inspected (likely higher privilege than this non-elevated agent) - input to it is blocked. Fix: run this agent as administrator.";
            return " Foreground window: " + who + " (agent elevated: " + (weElevated ? "yes" : "no")
                 + ", target elevated: " + (fgElevated == null ? "unknown" : fgElevated.Value ? "yes" : "no") + ").";
        }
        catch { return ""; }
    }

    /// <summary>SendInput/mouse_event from a non-elevated process to an elevated foreground window is filtered
    /// SILENTLY - the model would believe a click or keystroke was delivered when nothing arrived. Returns an honest
    /// error for that case, null when input can proceed. Pointer actions judge the window AT the coordinate (UIPI
    /// filters per target window, not per foreground window).</summary>
    private static string? InputBlockCheck(string kind, int? targetX = null, int? targetY = null)
    {
        try
        {
            GetForegroundState(out bool weElevated, out bool? fgElevated, out string who, targetX, targetY);
            if (weElevated) return null;
            if (who.Length == 0)
                return kind + " input NOT sent: no window holds the foreground (secure desktop / UAC prompt / lock screen) - synthetic input cannot reach it.";
            if (fgElevated != false)
                return kind + " input NOT sent: the target window " + who + " runs elevated (as administrator) while this agent does not, so Windows would silently discard the " + kind + ". Fix: run this agent as administrator, or run the target app non-elevated.";
        }
        catch { }
        return null;
    }

    private static void GetForegroundState(out bool weElevated, out bool? fgElevated, out string who, int? targetX = null, int? targetY = null)
    {
        weElevated = IsProcessElevated(GetCurrentProcess());
        fgElevated = null;
        who = "";
        IntPtr hwnd = IntPtr.Zero;
        if (targetX is { } px && targetY is { } py)
        {
            try { hwnd = GetAncestor(WindowFromPoint(new POINT { X = px, Y = py }), 2); } catch { hwnd = IntPtr.Zero; }
        }
        if (hwnd == IntPtr.Zero) hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        string title = "", proc = "";
        try { var sb = new System.Text.StringBuilder(256); if (GetWindowText(hwnd, sb, sb.Capacity) > 0) title = sb.ToString(); } catch { }
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                try { proc = Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { }
                fgElevated = IsProcessElevated(h);
            }
            finally { CloseHandle(h); }
        }
        if (title.Length > 60) title = title.Substring(0, 60) + "...";
        who = (proc.Length > 0 ? proc : "pid " + pid) + (title.Length > 0 ? " ('" + title + "')" : "");
    }

    private static bool IsProcessElevated(IntPtr processHandle)
    {
        if (!OpenProcessToken(processHandle, TOKEN_QUERY, out IntPtr tok)) return false;
        try { return GetTokenInformation(tok, TokenElevationClass, out int elev, sizeof(int), out _) && elev != 0; }
        finally { CloseHandle(tok); }
    }

    private static void Steps(int x0, int y0, int x1, int y1, int n, CancellationToken ct)
    {
        for (int i = 1; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            SetCursorPos(x0 + (x1 - x0) * i / n, y0 + (y1 - y0) * i / n);
            Thread.Sleep(12);
        }
    }

    // ── Mouse Input ─────────────────────────────────────────────────────────────
    private static void Click(string action, int x, int y)
    {
        switch (action)
        {
            case "right_click":
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                break;
            case "middle_click":
                mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
                break;
            case "double_click":
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                break;
            default:
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                break;
        }
    }

    private static void MouseDown() => mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
    private static void MouseUp() => mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);

    private static void Scroll(string dir, int clicks)
    {
        if (dir == "up") mouse_event(MOUSEEVENTF_WHEEL, 0, 0, 120 * clicks, UIntPtr.Zero);
        else if (dir == "down") mouse_event(MOUSEEVENTF_WHEEL, 0, 0, -120 * clicks, UIntPtr.Zero);
        else if (dir == "right") mouse_event(MOUSEEVENTF_HWHEEL, 0, 0, 120 * clicks, UIntPtr.Zero);
        else if (dir == "left") mouse_event(MOUSEEVENTF_HWHEEL, 0, 0, -120 * clicks, UIntPtr.Zero);
    }

    // ── Keyboard Input ──────────────────────────────────────────────────────────
    private static void TypeUnicode(string text, CancellationToken ct)
    {
        foreach (char c in text)
        {
            ct.ThrowIfCancellationRequested();
            if (c == '\n' || c == '\r') { TapVk(VK_RETURN); continue; }
            if (c == '\t') { TapVk(VK_TAB); continue; }
            var down = MakeUnicode(c, false);
            var up = MakeUnicode(c, true);
            SendInput(2, new[] { down, up }, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(2);
        }
    }

    private static bool Hotkey(string combo, out string err)
    {
        err = "";
        // '+' is a separator only BETWEEN tokens ("ctrl+s"); a trailing or spaced '+' is the plus key ("ctrl +").
        var tokens = System.Text.RegularExpressions.Regex.Split(combo.Trim(), @"\s+|(?<=\S)\+(?=\S)")
            .Where(t => t.Length > 0).ToArray();
        if (tokens.Length == 0) { err = "empty key combo."; return false; }
        var vks = new List<ushort>();
        foreach (var t in tokens)
        {
            if (!TryVk(t, out ushort vk)) { err = $"unknown key '{t}' in combo '{combo}'."; return false; }
            vks.Add(vk);
        }
        int downed = 0;
        try
        {
            for (int i = 0; i < vks.Count; i++) { KeyDown(vks[i]); downed++; }
            Thread.Sleep(30);
        }
        finally
        {
            for (int i = downed - 1; i >= 0; i--) KeyUp(vks[i]);
            ReleaseModifiers();
        }
        return true;
    }

    private static void ReleaseModifiers()
    {
        foreach (var vk in new ushort[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN }) KeyUp(vk);
    }

    private static void TapVk(ushort vk) { KeyDown(vk); KeyUp(vk); }
    private static void KeyDown(ushort vk) => SendInput(1, new[] { MakeVk(vk, false) }, Marshal.SizeOf(typeof(INPUT)));
    private static void KeyUp(ushort vk)
    {
        if (SendInput(1, new[] { MakeVk(vk, true) }, Marshal.SizeOf(typeof(INPUT))) == 0)
        {
            Thread.Sleep(25);
            SendInput(1, new[] { MakeVk(vk, true) }, Marshal.SizeOf(typeof(INPUT)));
        }
    }

    private static INPUT MakeVk(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = up ? KEYEVENTF_KEYUP : 0, time = 0, dwExtraInfo = UIntPtr.Zero } },
    };

    private static INPUT MakeUnicode(char c, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0), time = 0, dwExtraInfo = UIntPtr.Zero } },
    };

    private static bool TryVk(string name, out ushort vk)
    {
        string k = name.Trim().ToLowerInvariant();
        switch (k)
        {
            case "ctrl": case "control": vk = VK_CONTROL; return true;
            case "alt": case "menu": vk = VK_MENU; return true;
            case "shift": vk = VK_SHIFT; return true;
            case "win": case "super": case "meta": case "cmd": vk = VK_LWIN; return true;
            case "enter": case "return": vk = VK_RETURN; return true;
            case "tab": vk = VK_TAB; return true;
            case "esc": case "escape": vk = VK_ESCAPE; return true;
            case "space": case "spacebar": vk = VK_SPACE; return true;
            case "backspace": case "back": vk = VK_BACK; return true;
            case "delete": case "del": vk = VK_DELETE; return true;
            case "home": vk = VK_HOME; return true;
            case "end": vk = VK_END; return true;
            case "pageup": case "pgup": vk = VK_PRIOR; return true;
            case "pagedown": case "pgdn": vk = VK_NEXT; return true;
            case "insert": case "ins": vk = VK_INSERT; return true;
            case "left": case "arrowleft": vk = VK_LEFT; return true;
            case "right": case "arrowright": vk = VK_RIGHT; return true;
            case "up": case "arrowup": vk = VK_UP; return true;
            case "down": case "arrowdown": vk = VK_DOWN; return true;
            case "capslock": vk = VK_CAPITAL; return true;
            case "[": case "{": case "bracketleft": case "leftbracket": vk = 0xDB; return true;
            case "]": case "}": case "bracketright": case "rightbracket": vk = 0xDD; return true;
            case "-": case "minus": case "dash": vk = 0xBD; return true;
            case "=": case "+": case "plus": case "equal": vk = 0xBB; return true;
            case ";": case ":": case "semicolon": vk = 0xBA; return true;
            case "'": case "\"": case "quote": vk = 0xDE; return true;
            case ",": case "<": case "comma": vk = 0xBC; return true;
            case ".": case ">": case "period": case "dot": vk = 0xBE; return true;
            case "/": case "?": case "slash": vk = 0xBF; return true;
            case "\\": case "|": case "backslash": vk = 0xDC; return true;
            case "`": case "~": case "tilde": case "backtick": vk = 0xC0; return true;
        }
        if (k.Length == 2 && k[0] == 'f' && char.IsDigit(k[1])) { vk = (ushort)(VK_F1 + (k[1] - '1')); return true; }
        if (k.Length == 3 && k[0] == 'f' && k[1] == '1' && char.IsDigit(k[2])) { vk = (ushort)(VK_F10 + (k[2] - '0')); return true; }
        if (k.Length == 1 && k[0] >= 'a' && k[0] <= 'z') { vk = (ushort)char.ToUpperInvariant(k[0]); return true; }
        if (k.Length == 1 && k[0] >= '0' && k[0] <= '9') { vk = (ushort)k[0]; return true; }
        vk = 0; return false;
    }

    // ── Monitors & Displays ─────────────────────────────────────────────────────
    internal readonly struct Mon
    {
        public readonly int Index;
        public readonly Rectangle Bounds;
        public readonly bool Primary;
        public readonly string Label;
        public Mon(int i, Rectangle b, bool p, string l) { Index = i; Bounds = b; Primary = p; Label = l; }
    }

    internal static List<Mon> Monitors()
    {
        var list = new List<(Rectangle Bounds, bool Primary, string Name)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (GetMonitorInfo(hMon, ref mi))
            {
                var bounds = new Rectangle(mi.rcMonitor.Left, mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top);
                bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                list.Add((bounds, isPrimary, mi.szDevice ?? ""));
            }
            return true;
        }, IntPtr.Zero);

        var ordered = list.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
        var result = new List<Mon>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            string pos = ordered.Count <= 1 ? "" : i == 0 ? "left" : i == ordered.Count - 1 ? "right" : "center";
            string label = (m.Primary ? "Primary" : (pos.Length > 0 ? char.ToUpperInvariant(pos[0]) + pos[1..] : "Monitor " + i))
                + $" ({m.Bounds.Width}x{m.Bounds.Height})";
            result.Add(new Mon(i, m.Bounds, m.Primary, label));
        }
        return result;
    }

    private static Rectangle VirtualBounds() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    // ── Win32 P/Invoke ──────────────────────────────────────────────────────────
    private static (string Title, Rectangle Rect, int MonitorIndex)? GetForegroundWindowInfo(List<Mon> mons)
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (GetWindowRect(hwnd, out RECT r))
            {
                var rect = new Rectangle(r.Left, r.Top, Math.Max(0, r.Right - r.Left), Math.Max(0, r.Bottom - r.Top));
                int cx = rect.X + rect.Width / 2;
                int cy = rect.Y + rect.Height / 2;
                int monIdx = -1;
                for (int i = 0; i < mons.Count; i++)
                {
                    if (mons[i].Bounds.Contains(cx, cy))
                    {
                        monIdx = mons[i].Index;
                        break;
                    }
                }
                return (title, rect, monIdx);
            }
        }
        catch { }
        return null;
    }

    // ── Windows Accessibility (Win32 EnumChildWindows) ──────────────────────────
    private static bool FindElementByName(string name, out int screenX, out int screenY)
    {
        screenX = screenY = 0;
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        int fx = 0, fy = 0;
        bool found = false;
        EnumChildWindows(fg, (child, _) =>
        {
            if (found) return false;
            var csb = new System.Text.StringBuilder(512);
            GetWindowText(child, csb, csb.Capacity);
            if (csb.ToString().IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 &&
                GetWindowRect(child, out RECT r) && r.Right > r.Left && r.Bottom > r.Top)
            {
                fx = (r.Left + r.Right) / 2;
                fy = (r.Top + r.Bottom) / 2;
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        screenX = fx; screenY = fy;
        return found;
    }

    private static string GetAccessibleWindowText()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "(no foreground window)";
        var lines = new System.Text.StringBuilder();
        // Title of the foreground window itself
        var titleBuf = new System.Text.StringBuilder(512);
        GetWindowText(fg, titleBuf, titleBuf.Capacity);
        if (titleBuf.Length > 0) lines.AppendLine("[title] " + titleBuf);
        // All child windows
        EnumChildWindows(fg, (child, _) =>
        {
            var csb = new System.Text.StringBuilder(512);
            GetWindowText(child, csb, csb.Capacity);
            string t = csb.ToString().Trim();
            if (t.Length > 0) lines.AppendLine(t);
            return true;
        }, IntPtr.Zero);
        return lines.Length > 0 ? lines.ToString().Trim() : "(no text found)";
    }

    // ── Win32 P/Invoke ──────────────────────────────────────────
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);
    // Foreground-elevation diagnosis (UIPI input blocking)
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevationClass = 20;   // TOKEN_INFORMATION_CLASS.TokenElevation
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);   // GA_ROOT = 2
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int len, out int retLen);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public UIntPtr dwExtraInfo; }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint MONITORINFOF_PRIMARY = 0x00000001;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x01000;

    private const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
        VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22,
        VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_INSERT = 0x2D, VK_DELETE = 0x2E, VK_LWIN = 0x5B, VK_F1 = 0x70, VK_F10 = 0x79;
}
