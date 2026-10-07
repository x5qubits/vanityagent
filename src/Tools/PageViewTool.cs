using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp.Processing;
using System.Text.Json.Nodes;
using VanityAgent.Llm;
using VanityAgent.Tools;
using VanityAgent.Infra;

namespace VanityAgent.Tools;

/// <summary>
/// page_view: render a URL or local HTML file headlessly and return a screenshot.
/// Optionally inject JS, scroll to a position, or wait for a CSS selector.
/// Falls back gracefully if no browser is found.
/// </summary>
public sealed class PageViewTool : ITool, IVisualTool
{
    /// <param name="workspace">The project, so layout defects already reported three times are remembered with its
    /// state (layout-known.json) instead of being re-reported to every later task. Null = per-process only.</param>
    public PageViewTool(string? workspace = null)
    {
        try { _knownFile = workspace is null ? null : Path.Combine(VanityAgent.Infra.AgentConfig.ProjectDir(workspace), "layout-known.json"); }
        catch { _knownFile = null; }
    }

    /// <summary>Viewports this wide or narrower get the automatic horizontal-overflow probe.</summary>
    internal const int MobileCheckMaxWidth = 600;

    /// <summary>Returns "" when no text is cut off, otherwise the elements whose text content is larger than the
    /// box that clips it (badges with two lines in a one-line box, labels wider than their pill, headings cut by
    /// a fixed-height container). Whole DOM, any viewport.</summary>
    internal const string ClippedTextProbeJs = @"(() => {
  const hits = [];
  const seen = new Set();
  // Sliders, menus and animated wrappers clip on purpose. Match whole class tokens: 'tracking-wider' is not a
  // 'track', 'hero-title' is not the hero wrapper.
  const skipTok = /^(carousel|slider|slides?|swiper|marquee|ticker|snap|scroller|track|hero|reveal|collapse|modal|drawer|menu|dropdown|accordion|faq|nav|quotes)(-|_|$)/i;
  const isSkipped = (e) => ((typeof e.className === 'string' ? e.className : '') + ' ' + (e.id || '')).split(/\s+/).some(t => skipTok.test(t));
  for (const el of document.querySelectorAll('body *')) {
    if (!(el instanceof HTMLElement)) continue;
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden' || cs.position === 'fixed') continue;
    if (el.closest('[aria-hidden=""true""], [hidden]')) continue;                        // honeypots, decorative boxes
    { const rr = el.getBoundingClientRect(); if (rr.right < -100 || rr.bottom < -100) continue; }   // parked off-screen on purpose
    const clipsX = cs.overflowX === 'hidden' || cs.overflowX === 'clip';
    const clipsY = cs.overflowY === 'hidden' || cs.overflowY === 'clip';
    // A pill/badge/button with its own background whose text is taller or wider than the box: the text spills
    // out of the coloured shape (often white on white = invisible). Same defect as clipping, no overflow rule needed.
    const bg = cs.backgroundColor || '';
    const hasBg = bg !== '' && bg !== 'transparent' && !/^rgba\(\s*\d+,\s*\d+,\s*\d+,\s*0\)$/.test(bg);
    if (cs.textOverflow === 'ellipsis') continue;                       // deliberate truncation, not a defect
    if (!clipsX && !clipsY && !hasBg) continue;
    if (isSkipped(el)) continue;
    const text = (el.innerText || '').trim();
    if (text.length < 2 || text.length > 160) continue;              // labels, badges, buttons, short headings only
    if (el.querySelector('img, video, svg, canvas, iframe, table')) continue;
    if (cs.display === 'inline' || el.clientHeight === 0) continue;   // inline boxes have no client box to compare
    const r = el.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) continue;
    const overY = (clipsY || hasBg) && el.scrollHeight > el.clientHeight + 3;
    const overX = (clipsX || (hasBg && cs.whiteSpace === 'nowrap')) && el.scrollWidth > el.clientWidth + 3;
    if (!overY && !overX) continue;
    const key = text.slice(0, 40);
    if (seen.has(key)) continue;
    seen.add(key);
    const id = el.id ? '#' + el.id : '';
    const cls = (typeof el.className === 'string' && el.className.trim()) ? '.' + el.className.trim().split(/\s+/).slice(0, 3).join('.') : '';
    hits.push(el.tagName.toLowerCase() + id + cls + ' ""' + key + (text.length > 40 ? '…' : '') + '"" (content ' + Math.round(overY ? el.scrollHeight : el.scrollWidth) + 'px ' + (overY ? 'tall' : 'wide') + ' in a ' + Math.round(overY ? el.clientHeight : el.clientWidth) + 'px box)');
  }
  // Second form of the same defect: a label that sits outside the box of an ancestor that clips (a badge
  // pinned above a card with overflow:hidden, a ribbon poking out of a rounded container).
  for (const el of document.querySelectorAll('body *')) {
    if (!(el instanceof HTMLElement)) continue;
    const text = (el.innerText || '').trim();
    if (text.length < 2 || text.length > 60 || el.children.length > 2) continue;   // labels and badges only
    const cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden' || cs.position === 'fixed' || cs.opacity === '0') continue;
    const r = el.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) continue;
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      const pcs = getComputedStyle(p);
      const clips = ['hidden', 'clip'].includes(pcs.overflowX) || ['hidden', 'clip'].includes(pcs.overflowY);
      if (!clips) continue;
      if (isSkipped(p) || isSkipped(el)) break;
      const pr = p.getBoundingClientRect();
      const outTop = pr.top - r.top, outBottom = r.bottom - pr.bottom, outLeft = pr.left - r.left, outRight = r.right - pr.right;
      const worst = Math.max(outTop, outBottom, outLeft, outRight);
      if (worst > 3 && worst < r.height + r.width) {            // partly outside: cut, not merely off-screen
        const key = 'c:' + text.slice(0, 40);
        if (!seen.has(key)) {
          seen.add(key);
          const side = worst === outTop ? 'top' : worst === outBottom ? 'bottom' : worst === outLeft ? 'left' : 'right';
          const cls = (typeof el.className === 'string' && el.className.trim()) ? '.' + el.className.trim().split(/\s+/).slice(0, 3).join('.') : '';
          const pcls = (typeof p.className === 'string' && p.className.trim()) ? '.' + p.className.trim().split(/\s+/).slice(0, 2).join('.') : '';
          hits.push(el.tagName.toLowerCase() + cls + ' ""' + text.slice(0, 40) + '"" sticks ' + Math.round(worst) + 'px out of the ' + side + ' of its clipping container ' + p.tagName.toLowerCase() + pcls);
        }
      }
      break;
    }
  }
  if (hits.length === 0) return '';
  return '[layout check] CLIPPED TEXT: ' + hits.length + ' element(s) show cut-off text: ' + hits.slice(0, 6).join('; ') + '. Fix: let the box grow (height:auto, white-space:normal, padding), shorten the text, remove overflow:hidden from the container or move the label inside it.';
})()";

    /// <summary>Content blocks that are invisible right after load (opacity 0 / visibility hidden, waiting for a script):
    /// a full-page capture, a crawler or any JS error shows those sections empty. Measured before any scrolling.</summary>
    internal const string HiddenAtLoadProbeJs = @"(() => {
  const hits = [];
  const skip = (el) => el.closest('[data-slider], .hs-slide, [aria-hidden=""true""], .dropdown-content, .modal, .tooltip, .toast, dialog, [hidden], nav, header, [role=""dialog""], .cookie, [class*=""cookie""], [id*=""cookie""], [class*=""consent""]');
  for (const el of document.querySelectorAll('main *, section *, footer *, article *')) {
    if (!(el instanceof HTMLElement)) continue;
    const text = (el.innerText || '').trim();
    if (text.length < 20) continue;
    const cs = getComputedStyle(el);
    if (cs.display === 'none') continue;
    const invisible = parseFloat(cs.opacity) === 0 || cs.visibility === 'hidden';
    if (!invisible) continue;
    if (skip(el)) continue;
    if (hits.some(h => h.el.contains(el))) continue;                 // report the outermost hidden block only
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) continue;
    const id = el.id ? '#' + el.id : '';
    const cls = (typeof el.className === 'string' && el.className.trim()) ? '.' + el.className.trim().split(/\s+/).slice(0, 3).join('.') : '';
    hits.push({ el, s: el.tagName.toLowerCase() + id + cls + ' ""' + text.slice(0, 40).replace(/\s+/g, ' ') + '""' });
  }
  if (hits.length === 0) return '';
  return '[layout check] HIDDEN AT LOAD: ' + hits.length + ' content block(s) are invisible until a script adds a class (opacity 0 / visibility hidden): ' + hits.slice(0, 6).map(h => h.s).join('; ') + '. A full-page capture, a crawler or any JS error shows these sections empty. Fix: content visible by default (no opacity:0 at rest, in no media query); animate in from the added class instead.';
})()";

    /// <summary>Adjacent links in navigation or footer whose boxes touch on the same line: the menu reads as one word.</summary>
    internal const string CrampedLinksProbeJs = @"(() => {
  const hits = [];
  const seen = new Set();
  for (const a of document.querySelectorAll('nav a, header a, footer a, .menu a, .navbar a')) {
    const cs = getComputedStyle(a);
    if (cs.display === 'none' || cs.visibility === 'hidden' || parseFloat(cs.opacity) === 0) continue;
    const ta = (a.innerText || '').trim(); if (!ta) continue;
    const r = a.getBoundingClientRect(); if (r.width === 0) continue;
    let n = a.nextSibling;
    while (n && n.nodeType === 3 && !n.textContent.trim()) n = n.nextSibling;      // whitespace-only text between links
    if (!n || n.nodeType !== 1) { const p = a.parentElement; if (p && p.tagName === 'LI') { let q = p.nextSibling; while (q && q.nodeType === 3 && !q.textContent.trim()) q = q.nextSibling; n = q && q.nodeType === 1 ? q.querySelector('a') : null; } }
    if (!n || n.tagName !== 'A') continue;
    const tb = (n.innerText || '').trim(); if (!tb) continue;
    const r2 = n.getBoundingClientRect();
    if (Math.abs(r2.top - r.top) > 6) continue;                                      // not on the same line
    const csb = getComputedStyle(n);
    const gap = (r2.left - r.right) + parseFloat(cs.paddingRight || 0) + parseFloat(csb.paddingLeft || 0);   // distance between the two texts
    if (gap >= 12) continue;
    const box = a.closest('nav, header, footer, .menu') || a.parentElement;
    const key = ta + '|' + tb; if (seen.has(key)) continue; seen.add(key);
    hits.push('""' + ta.slice(0, 20) + '"" / ""' + tb.slice(0, 20) + '"" in ' + box.tagName.toLowerCase() + (box.id ? '#' + box.id : '') + ' (' + Math.round(gap) + 'px apart)');
  }
  if (hits.length === 0) return '';
  return '[layout check] LINKS RUN TOGETHER: ' + hits.length + ' pair(s) of links touch on the same line: ' + hits.slice(0, 5).join('; ') + '. Fix: give the links room (gap-x-6 on the list, px-3 py-2 on each link).';
})()";

    /// <summary>Returns "" when the page fits, otherwise a one-paragraph report naming the widest elements.</summary>
    internal const string OverflowProbeJs = @"(() => {
  const vw = window.innerWidth;
  // Measure element boxes, not scrollWidth: with overflow clipped on <html>/<body> the document width never
  // grows, yet content past the right edge is still invisible to a phone visitor.
  const hits = [];
  let maxRight = vw;
  const sliderRe = /carousel|slider|slide|swiper|marquee|ticker|snap|scroller|track/i;
  const clipper = (el) => {            // nearest ancestor (below body) that clips horizontally
    for (let p = el.parentElement; p && p !== document.body && p !== document.documentElement; p = p.parentElement) {
      const ox = getComputedStyle(p).overflowX;
      if (ox === 'hidden' || ox === 'clip' || ox === 'auto' || ox === 'scroll') return { el: p, ox };
    }
    return null;
  };
  for (const el of document.querySelectorAll('body *')) {
    const r = el.getBoundingClientRect();
    if (r.width === 0 || r.height === 0) continue;
    const cs = getComputedStyle(el);
    if (cs.position === 'fixed' || cs.visibility === 'hidden' || cs.display === 'none') continue;
    if (cs.opacity === '0' && !el.classList.contains('reveal')) continue;          // hidden decorations
    if (el.closest('[aria-hidden=""true""]')) continue;
    // Only elements a visitor can partly see and that are cut: fully off-screen boxes (off-canvas menus,
    // hidden slides, collapsed sidebars) are intentional and ignored.
    const cutRight = r.right > vw + 2 && r.left < vw - 2;
    const cutLeft  = r.left < -2 && r.right > 2;
    if (cutRight || cutLeft) {
      const c = clipper(el);
      if (c) {
        // Inside an intentional horizontal scroller / slider: fine. Inside a plain overflow:hidden box that
        // itself fits the viewport: the content is being cut off — that is the defect we want.
        if (c.ox === 'auto' || c.ox === 'scroll') continue;
        const cname = (c.el.id || '') + ' ' + (typeof c.el.className === 'string' ? c.el.className : '');
        if (sliderRe.test(cname) || sliderRe.test(typeof el.className === 'string' ? el.className : '')) continue;
        if (el.closest('[data-slider], [data-carousel], [role=""region""][aria-roledescription]')) continue;
      }
      maxRight = Math.max(maxRight, r.right);
      const id = el.id ? '#' + el.id : '';
      const cls = (typeof el.className === 'string' && el.className.trim()) ? '.' + el.className.trim().split(/\s+/).slice(0, 3).join('.') : '';
      hits.push({ s: el.tagName.toLowerCase() + id + cls, w: Math.round(r.width), right: Math.round(r.right), left: Math.round(r.left) });
    }
  }
  if (hits.length === 0) return '';
  hits.sort((a, b) => b.w - a.w);
  const top = hits.slice(0, 6).map(h => h.s + ' (' + h.w + 'px wide, from ' + h.left + 'px to ' + h.right + 'px)').join('; ');
  return '[layout check] HORIZONTAL OVERFLOW at ' + vw + 'px viewport: ' + hits.length + ' element(s) extend past the edge (up to ' + Math.round(maxRight) + 'px). Widest: ' + top + '. Fix: let these shrink (max-width:100%, min-width:0, flex-wrap, hide secondary items under md:) — a phone visitor sees clipped content.';
})()";

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "page_view",
        Description = "Render a URL or local HTML file in a headless browser and return a screenshot. Supports interactive actions (click, hover, scroll, type, eval) before the screenshot. " +
                      "Several pages to check the same way go in ONE call through urls instead of one call each. " +
                      "To MEASURE the DOM or try a CSS change with eval, pass screenshot:false: the actions and js run and only the JS result comes back, in a second and without an image. Take the image only when you need to see the page.",
        Parameters  = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object>
            {
                ["url"]         = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "URL or file:// path to render. Omit it when you pass urls." },
                ["urls"]        = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "string" }, ["description"] = "Several pages in ONE call, up to " + MaxUrlsPerCall + ": each is rendered with the same settings you pass here (width, full_page, screenshot, js, wait_for, focus, actions) and the results come back in the order given, one section per page. Use url, not urls, for a page you interact with on its own." },
                ["actions"]     = new Dictionary<string, object> { ["type"] = "array",   ["description"] = "Interactions before the screenshot, in order, one object each: {\"click\":\"<css selector>\"} (waits for a navigation it starts), {\"hover\":\"<selector>\"}, {\"input\":\"<selector>\",\"value\":\"<text>\"}, {\"scroll\":<y px>}, {\"scroll_to\":\"<selector>\"} (the section to look at, scrolled to the top of the viewport once the page has settled), {\"wait_ms\":<ms>}, {\"wait_for\":\"<selector>\"}, {\"eval\":\"<js>\"}. Put every eval check you currently know you need in this SAME array (e.g. icon glyph, font, margin, offset - one {\"eval\":...} entry each): each result comes back numbered and the page loads once. Never spend a separate page_view call on the next single fact when you can name it now; only start a new call once the last result tells you what to check next. The result names any action it could not run and the page address when a click navigated.", ["items"] = new Dictionary<string, object> { ["type"] = "object" } },
                ["js"]          = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "JavaScript evaluated after the actions; its value comes back as text. An expression or a function (called for you, awaited if async). Prefer several {\"eval\":\"<js>\"} entries in the actions array over this field when checking more than one thing." },
                ["wait_for"]    = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "CSS selector to wait for (up to 5s)." },
                ["scroll_y"]    = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Pixels to scroll down before the screenshot. To look at a section, prefer focus with its selector instead of guessing an offset." },
                ["focus"]       = new Dictionary<string, object> { ["type"] = "string",  ["description"] = "CSS selector of the section to look at: after the page has settled it is scrolled to the top of the viewport and the screenshot (and crop_height) start there. Use this, not scroll_y, whenever you know the element." },
                ["width"]       = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Viewport width (default 1280)." },
                ["widths"]      = new Dictionary<string, object> { ["type"] = "array", ["items"] = new Dictionary<string, object> { ["type"] = "integer" }, ["description"] = "Capture the page at several widths in ONE call, e.g. [1280, 375]: the result is one image with the captures side by side and one layout check per width. Use this instead of one call per width." },
                ["height"]      = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Viewport height (default 800)." },
                ["crop_height"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Crop the screenshot to this height, starting at the current scroll position (after focus, scroll_y or scroll actions)." },
                ["full_page"]   = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "Capture the whole page in ONE image (up to 6000px tall) instead of the viewport. Use this for reviews: one call per width shows every section." },
                ["timeout_ms"]  = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "Navigation timeout ms (default 15000)." },
                ["screenshot"]  = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "false = no image: run actions and js, return the JS result only (measurements, CSS trials). Default true." },
            },
            // No top-level "required": a call names either url or urls, and the tool says so when both are missing.
        },
    };

    public Task<string> ExecuteAsync(string argsJson, CancellationToken ct = default)
        => Task.FromResult("Use page_view to get a screenshot — the result will be shown as an image.");

    /// <summary>How many pages one call opens.</summary>
    public const int MaxUrlsPerCall = 8;

    /// <summary>Several pages in ONE call: each URL goes through the single-page path with the caller's own
    /// settings, and the answers are joined in order, one section per page, with every screenshot kept.
    /// A final pass over a site opened its pages one call at a time - 126 page views straight after another page
    /// view in one project build - and every one of those calls re-sent the whole task so far (2026-10-04).</summary>
    private async Task<ToolResultRecord> ViewSeveralAsync(string toolCallId, JsonElement root, JsonElement urlsEl, CancellationToken ct)
    {
        var urls = urlsEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => (e.GetString() ?? "").Trim())
            .Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        // A call that names url as well means that page too, first.
        var single = root.TryGetProperty("url", out var su) && su.ValueKind == JsonValueKind.String ? (su.GetString() ?? "").Trim() : "";
        if (single.Length > 0 && !urls.Contains(single, StringComparer.Ordinal)) urls.Insert(0, single);
        if (urls.Count == 0) return Error(toolCallId, "urls is empty. Give at least one URL.");
        int skipped = Math.Max(0, urls.Count - MaxUrlsPerCall);

        var text = new StringBuilder();
        var images = new List<string>();
        bool anyOk = false;
        foreach (var u in urls.Take(MaxUrlsPerCall))
        {
            if (ct.IsCancellationRequested) break;
            var args = JsonNode.Parse(root.GetRawText())!.AsObject();
            args.Remove("urls");
            args["url"] = u;
            var one = await ExecuteVisualAsync(toolCallId, args.ToJsonString(), ct).ConfigureAwait(false);
            var shots = one.ImageDataUrls is { Count: > 0 } ? one.ImageDataUrls
                      : one.ScreenshotDataUrl is { Length: > 0 } s ? [s] : new List<string>();
            if (text.Length > 0) text.Append("\n\n");
            text.Append("=== ").Append(u);
            if (shots.Count > 0) text.Append(" (image ").Append(images.Count + 1).Append(')');
            text.Append(" ===\n").Append(one.Output);
            images.AddRange(shots);
            if (!one.IsError) anyOk = true;
        }
        if (skipped > 0) text.Append($"\n\n[{skipped} more URL(s) not opened: one call takes up to {MaxUrlsPerCall}]");
        return new ToolResultRecord
        {
            ToolCallId = toolCallId, ToolName = "page_view", Output = text.ToString(), IsError = !anyOk,
            // Both fields: the request to the model sends the list when it is set, and the parts of the harness that
            // only know one screenshot per result (the chat log, the screenshot-retention pass) still see one.
            ScreenshotDataUrl = images.Count > 0 ? images[0] : null,
            ImageDataUrls     = images.Count > 1 ? images : null,
        };
    }

    public async Task<ToolResultRecord> ExecuteVisualAsync(string toolCallId, string argsJson, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(argsJson);
        var root      = doc.RootElement;
        if (root.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == JsonValueKind.Array)
            return await ViewSeveralAsync(toolCallId, root, urlsEl, ct).ConfigureAwait(false);
        var url       = root.TryGetProperty("url",         out var u)  ? u.GetString()?.Trim()  ?? "" : "";
        var js        = root.TryGetProperty("js",          out var j)  ? j.GetString()?.Trim()  ?? "" : "";
        var waitFor   = root.TryGetProperty("wait_for",    out var w)  ? w.GetString()?.Trim()  ?? "" : "";
        var scrollY   = root.TryGetProperty("scroll_y",    out var sy) && sy.ValueKind == JsonValueKind.Number ? sy.GetInt32() : 0;
        var focus     = root.TryGetProperty("focus",       out var fc) ? fc.GetString()?.Trim() ?? "" : "";
        var viewW     = root.TryGetProperty("width",       out var vw) && vw.ValueKind == JsonValueKind.Number ? vw.GetInt32() : 1280;
        var viewH     = root.TryGetProperty("height",      out var vh) && vh.ValueKind == JsonValueKind.Number ? vh.GetInt32() : 800;
        var cropH     = root.TryGetProperty("crop_height", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0;
        var fullPage  = root.TryGetProperty("full_page",   out var fp) && fp.ValueKind == JsonValueKind.True;
        var timeoutMs = root.TryGetProperty("timeout_ms",  out var tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetInt32() : 30_000;
        var actions   = root.TryGetProperty("actions",     out var ac) && ac.ValueKind == JsonValueKind.Array
                            ? ac.EnumerateArray().Select(e => e.Clone()).ToList()
                            : new List<JsonElement>();

        if (string.IsNullOrEmpty(url))
            return Error(toolCallId, "url (or urls) is required.");

        var browser = BrowserLocator.FindBrowser();
        if (browser == null)
            return Error(toolCallId, "No Chrome/Edge found. Install Chrome or Edge to use page_view.");

        try
        {
            var widths = root.TryGetProperty("widths", out var wsEl) && wsEl.ValueKind == JsonValueKind.Array
                ? wsEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => Math.Clamp(e.GetInt32(), 320, 2560)).Distinct().Take(3).ToList()
                : [];
            if (widths.Count >= 2)
            {
                var shots = new List<(int W, byte[] Bytes, string? Js)>();
                foreach (var w0 in widths)
                {
                    var one = await CdpScreenshotAsync(browser, url, js, waitFor, scrollY, actions, w0, viewH, cropH, timeoutMs, ct, fullPage, focus: focus).ConfigureAwait(false);
                    if (one.Screenshot is { Length: > 0 }) shots.Add((w0, one.Screenshot, one.JsResult));
                }
                if (shots.Count == 0) return Error(toolCallId, "Screenshot was empty.");
                var composed = ComposeSideBySide(shots.Select(x => x.Bytes).ToList());
                var text = new System.Text.StringBuilder($"[screenshots of {url} at {string.Join(" and ", shots.Select(x => x.W + "px"))}, side by side, left to right]");
                foreach (var x in shots)
                    text.Append(Describe(x.Js, $"[{x.W}px] "));
                return new ToolResultRecord
                {
                    ToolCallId = toolCallId, ToolName = "page_view",
                    Output = ThrottleLayoutReports(text.ToString(), url),
                    ScreenshotDataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(composed),
                };
            }
            // Mobile widths always go through CDP so the overflow check below runs on every phone-size screenshot.
            // Every screenshot goes through CDP: the page digest and the layout checks need the DOM. The plain
            // --screenshot path stays as the fallback when CDP is unavailable.
            bool useCdp = true;
            // screenshot:false = run the actions and the JS, return the JS result only. A coder measured button
            // widths and tried CSS with eval through 18 full screenshots, each 8 s and an image in the context, when
            // every one of them only needed the number back (2026-09-22).
            bool wantImage = !(root.TryGetProperty("screenshot", out var scp) && scp.ValueKind == JsonValueKind.False);
            byte[]? bytes;
            string? jsResult = null;
            if (useCdp)
            {
                var cdp = await CdpScreenshotAsync(browser, url, js, waitFor, scrollY, actions, viewW, viewH, cropH, timeoutMs, ct, fullPage, captureImage: true, focus: focus).ConfigureAwait(false);
                bytes    = cdp.Screenshot;
                jsResult = cdp.JsResult;
                if (!wantImage)
                    return new ToolResultRecord
                    {
                        ToolCallId = toolCallId, ToolName = "page_view",
                        Output = $"[evaluated on {url}, no screenshot taken]" + (jsResult is null ? "\nJS result: (nothing returned)" : Describe(jsResult)),
                    };
            }
            else
            {
                bytes = await SimpleScreenshotAsync(browser, url, viewW, viewH, timeoutMs, ct).ConfigureAwait(false);
            }

            if (bytes == null || bytes.Length == 0)
                return Error(toolCallId, "Screenshot was empty.");

            // Crop if requested (simple path returns PNG; crop + re-encode as JPEG)
            if (cropH > 0 && !useCdp && OperatingSystem.IsWindows())
                bytes = CropToJpeg(bytes, cropH);

            var mime    = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg" : "image/png";
            var dataUrl = $"data:{mime};base64," + Convert.ToBase64String(bytes);

            return new ToolResultRecord
            {
                ToolCallId        = toolCallId,
                ToolName          = "page_view",
                Output            = ThrottleLayoutReports($"[screenshot of {url}]" + Describe(jsResult), url),
                ScreenshotDataUrl = dataUrl,
            };
        }
        // The operator's stop arrives here as the same exception as the navigation timeout; say which it was.
        catch (OperationCanceledException) { return Error(toolCallId, ct.IsCancellationRequested ? "page_view stopped by the operator." : "page_view timed out."); }
        catch (Exception ex)
        {
            Log.Error(ex);
            return Error(toolCallId, "page_view: " + ex.Message);
        }
    }

    // ── Batching + loop guards ────────────────────────────────────────────────

    /// <summary>One JPEG with the captures next to each other (desktop left, mobile right), so a review of two
    /// widths is one call and one image.</summary>
    internal static byte[] ComposeSideBySide(IReadOnlyList<byte[]> images, int gap = 24)
    {
        var loaded = images.Select(b => SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(b)).ToList();
        try
        {
            var width  = loaded.Sum(i => i.Width) + gap * (loaded.Count - 1);
            var height = loaded.Max(i => i.Height);
            using var canvas = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height, new SixLabors.ImageSharp.PixelFormats.Rgba32(235, 235, 235, 255));
            int x = 0;
            foreach (var img in loaded)
            {
                var pos = new SixLabors.ImageSharp.Point(x, 0);
                canvas.Mutate(c => c.DrawImage(img, pos, 1f));
                x += img.Width + gap;
            }
            using var ms = new MemoryStream();
            canvas.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 82 });
            return ms.ToArray();
        }
        finally { foreach (var i in loaded) i.Dispose(); }
    }

    /// <summary>What is on the page, as text: title, visible headings, forms with their fields, visible inputs,
    /// buttons and nav links, each with a selector the model can act on. Capped per group.</summary>
    private const string PageDigestJs = @"(() => {
  const q = s => Array.from(document.querySelectorAll(s));
  const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
  const txt = e => ((e.innerText || e.value || e.getAttribute('aria-label') || e.getAttribute('placeholder') || '') + '').trim().replace(/\s+/g, ' ').slice(0, 60);
  const sel = e => e.tagName.toLowerCase() + (e.id ? '#' + e.id : '') + (typeof e.className === 'string' && e.className.trim() ? '.' + e.className.trim().split(/\s+/).slice(0, 3).join('.') : '');
  const out = [];
  // HTTP status of the document itself. A 404 page with a title, a menu and a newsletter form read like a normal
  // page and a coder screenshotted it three times before a urllib probe told it the truth (2026-09-24).
  const st = ((performance.getEntriesByType('navigation') || [])[0] || {}).responseStatus || 0;
  out.push((st ? 'http ' + st + (st >= 400 ? ' ERROR' : '') + ' | ' : '') + 'title: ' + document.title + ' | url: ' + location.href);
  const hs = q('h1,h2,h3').filter(vis).slice(0, 12).map(e => e.tagName.toLowerCase() + ' ""' + txt(e) + '""');
  if (hs.length) out.push('headings: ' + hs.join(' | '));
  const forms = q('form').slice(0, 6).map(f => sel(f) + ' action=' + (f.getAttribute('action') || '') + ' [' + q('input,select,textarea,button').filter(i => f.contains(i)).slice(0, 8).map(i => i.tagName.toLowerCase() + (i.name ? '[name=' + i.name + ']' : '') + (i.id ? '#' + i.id : '') + (i.type ? ':' + i.type : '')).join(', ') + ']');
  if (forms.length) out.push('forms: ' + forms.join(' | '));
  const inputs = q('input:not([type=hidden]),textarea,select').filter(vis).slice(0, 15).map(e => sel(e) + (e.name ? '[name=' + e.name + ']' : '') + (e.placeholder ? ' ""' + e.placeholder.slice(0, 30) + '""' : ''));
  if (inputs.length) out.push('inputs: ' + inputs.join(' | '));
  const btns = q('button,a.btn,[role=button],input[type=submit]').filter(vis).slice(0, 20).map(e => sel(e) + ' ""' + txt(e) + '""');
  if (btns.length) out.push('buttons: ' + btns.join(' | '));
  const nav = q('nav a, header a').filter(vis).map(e => txt(e)).filter(t => t).slice(0, 20);
  if (nav.length) out.push('nav links: ' + nav.join(', '));
  return out.join('\n');
})()";

    /// <summary>The text after a screenshot: the model's JS result when it asked for one, then the page digest.</summary>
    private static string Describe(string? js, string prefix = "")
    {
        if (string.IsNullOrEmpty(js)) return "";
        var i = js.IndexOf("[page digest]", StringComparison.Ordinal);
        var head = i < 0 ? js : js[..i].TrimEnd('\n', '\r');
        var digest = i < 0 ? "" : js[i..];
        var sb = new StringBuilder();
        if (head.Length > 0) sb.Append('\n').Append(prefix).Append("JS result: ").Append(head);
        if (digest.Length > 0) sb.Append('\n').Append(prefix).Append(digest);
        return sb.ToString();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _layoutReportsSeen = new();
    private static readonly object _knownGate = new();

    /// <summary>The same layout report, unchanged after two fix rounds, must not eat a third: the model is told to
    /// note it and move on (a designer once spent 30 turns on one AdminLTE truncation). From that point the defect
    /// is KNOWN for the URL, persisted with the project, and every later screenshot - this run or the next task -
    /// lists it in one line as not this task's. A colour task on the menu spent 20 calls on a category card that
    /// had overflowed at 375px since an earlier task, because every screenshot reported it in full (2026-09-22).
    /// A defect a task introduces is new, so it is still reported in full.</summary>
    internal string ThrottleLayoutReports(string output, string url)
    {
        if (!output.Contains("[layout check]")) return output;
        var run = AgentToolContext.RunId ?? AgentToolContext.AgentId ?? "";
        var key = NormalizeUrl(url);
        var known = LoadKnown(key);
        var kept = new List<string>(); var knownHits = new List<string>();
        bool nudge = false, knownChanged = false;
        foreach (var line in output.Split('\n'))
        {
            var idx = line.IndexOf("[layout check]", StringComparison.Ordinal);
            if (idx < 0) { kept.Add(line); continue; }
            var sig = line[idx..]; if (sig.Length > 140) sig = sig[..140];
            if (known.Contains(sig))
            {
                var label = sig["[layout check]".Length..].Trim();
                knownHits.Add(label.Length > 90 ? label[..90] + "…" : label);
                continue;
            }
            kept.Add(line);
            var n = _layoutReportsSeen.AddOrUpdate(run + "|" + key + "|" + sig, 1, (_, c) => c + 1);
            if (n == 3) { nudge = true; known.Add(sig); knownChanged = true; }
        }
        if (knownHits.Count > 0)
            kept.Add("[layout check] known from earlier rounds, not this task's: " + string.Join("; ", knownHits.Distinct()) + ". Fix only when a task targets it.");
        if (nudge)
            kept.Add("[harness] this layout report is unchanged after two fix rounds. Stop editing for it: write it in the `notes` of your TASK_RESULT (or the reviewer's defect list) and continue with the next step of your sequence.");
        if (knownChanged) SaveKnown(key, known);
        return string.Join('\n', kept);
    }

    private static string NormalizeUrl(string url)
    {
        var u = (url ?? "").Trim().ToLowerInvariant();
        var cut = u.IndexOfAny(new[] { '#' }); if (cut >= 0) u = u[..cut];
        return u.TrimEnd('/');
    }

    private readonly string? _knownFile;
    private Dictionary<string, HashSet<string>>? _known;

    private HashSet<string> LoadKnown(string key)
    {
        lock (_knownGate)
        {
            if (_known is null)
            {
                _known = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                try
                {
                    if (_knownFile != null && File.Exists(_knownFile))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(_knownFile));
                        foreach (var p in doc.RootElement.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Array)
                                _known[p.Name] = p.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToHashSet(StringComparer.Ordinal);
                    }
                }
                catch { /* unreadable = nothing known */ }
            }
            if (!_known.TryGetValue(key, out var set)) { set = new HashSet<string>(StringComparer.Ordinal); _known[key] = set; }
            return set;
        }
    }

    private void SaveKnown(string key, HashSet<string> set)
    {
        if (_knownFile is null || _known is null) return;
        lock (_knownGate)
        {
            try
            {
                _known[key] = set;
                var o = new JsonObject();
                foreach (var kv in _known.Where(kv => kv.Value.Count > 0))
                    o[kv.Key] = new JsonArray(kv.Value.Select(s => (JsonNode)s).ToArray());
                Directory.CreateDirectory(Path.GetDirectoryName(_knownFile)!);
                File.WriteAllText(_knownFile, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Warn("[page_view] known layout defects not saved: " + ex.Message); }
        }
    }

    // ── Simple --screenshot path (no CDP) ─────────────────────────────────────

    private static readonly HashSet<string> ActionTypes = new(StringComparer.OrdinalIgnoreCase) { "click", "hover", "type", "scroll", "scroll_to", "wait", "wait_ms", "wait_for", "eval" };

    /// <summary>One action in the form the runner reads ({"type":"click","selector":…}). The model writes the
    /// natural shorthand at least as often - {"click":"a.btn"}, {"wait_ms":1000}, {"eval":"…"}, {"scroll":600} -
    /// and until 2026-09-21 every such action was skipped without a word: a "click" that never happened made a
    /// working link look broken for 40 steps. Shorthand is mapped here; anything else is named in the result.</summary>
    private static JsonElement CanonAction(JsonElement raw, int no, List<string> ignored)
    {
        if (raw.ValueKind != JsonValueKind.Object) { ignored.Add($"action {no} is not an object"); return raw; }
        if (raw.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && ActionTypes.Contains(t.GetString() ?? "")) return raw;
        var o = new JsonObject();
        string? S(string k) => raw.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? N(string k) => raw.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        if (S("click") is { } c) { o["type"] = "click"; o["selector"] = c; }
        else if (S("hover") is { } h) { o["type"] = "hover"; o["selector"] = h; }
        else if ((S("input") ?? S("type_into")) is { } ti) { o["type"] = "type"; o["selector"] = ti; o["value"] = S("value") ?? ""; }
        else if (N("scroll") is { } sy) { o["type"] = "scroll"; o["y"] = sy; }
        // The DOM's own name for it is scrollIntoView, and the model reaches for that spelling first
        // ({"scroll_into_view": ".footer"} - ignored, hero captured, one call lost; magicdanube 2026-09-23).
        else if ((S("scroll_to") ?? S("scroll_into_view") ?? S("scrollIntoView") ?? S("scroll_to_element") ?? S("focus")) is { } st) { o["type"] = "scroll_to"; o["selector"] = st; }
        else if ((N("wait_ms") ?? N("wait")) is { } ms) { o["type"] = "wait_ms"; o["ms"] = ms; }
        else if (S("wait_for") is { } wf) { o["type"] = "wait_for"; o["selector"] = wf; }
        else if ((S("eval") ?? S("js")) is { } js) { o["type"] = "eval"; o["js"] = js; }
        else
        {
            ignored.Add($"action {no} not understood (keys: {string.Join(", ", raw.EnumerateObject().Select(p => p.Name))}) - use {{\"click\":\"<selector>\"}}, {{\"hover\":…}}, {{\"input\":\"<selector>\",\"value\":…}}, {{\"scroll\":<y>}}, {{\"scroll_to\":\"<selector>\"}}, {{\"wait_ms\":<ms>}}, {{\"wait_for\":\"<selector>\"}} or {{\"eval\":\"<js>\"}}");
            return raw;
        }
        return JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
    }

    /// <summary>A function expression handed to eval ("() => {…}") evaluates to the function itself and returns
    /// nothing; the model meant its result. Called here, and awaited when it is async.</summary>
    private static readonly System.Text.RegularExpressions.Regex FunctionExpr = new(@"^\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>|^\s*(?:async\s+)?function\b", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static string Callable(string js)
    {
        var t = js.Trim();
        // "(() => {…})()" is a function the caller ALREADY invoked - the commonest way a model writes a probe.
        // FunctionExpr sees its leading "(()" as a parameter list and wraps it again, so the page evaluated
        // "((() => {…})())()" and called the IIFE's RESULT as a function: every such probe came back
        // "(intermediate value)(…) is not a function" and the model had to write it a second time (2026-09-22).
        return AlreadyInvoked(t) ? t
             : FunctionExpr.IsMatch(t) ? "(" + t + ")()" : t;
    }

    /// <summary>Does the expression open with a parenthesised group that is immediately called?</summary>
    private static bool AlreadyInvoked(string t)
    {
        if (t.Length == 0 || t[0] != '(') return false;
        int depth = 0;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '(') depth++;
            else if (t[i] == ')' && --depth == 0)
            {
                int j = i + 1;
                while (j < t.Length && char.IsWhiteSpace(t[j])) j++;
                return j < t.Length && t[j] == '(';
            }
        }
        return false;
    }

    /// <summary>The host runs elevated (the Slave's manifest says requireAdministrator). A Chromium browser started
    /// from an elevated process re-launches itself de-elevated through the shell and the process we started exits at
    /// once: the simple path then found no screenshot file after 0.2s ("Screenshot was empty"), the CDP path talked
    /// to the re-launched browser but its cleanup saw HasExited and skipped the kill, so every capture left a headless
    /// browser and its profile folder behind (21 browsers, 47 folders on 2026-09-21). With this switch the process we
    /// start IS the browser: it stays in our job object, the tree kill reaches it and the profile folder can be deleted.</summary>
    private const string NoDeElevate = "--do-not-de-elevate";

    internal static async Task<byte[]?> SimpleScreenshotAsync(string browser, string url,
        int w, int h, int timeoutMs, CancellationToken ct)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"vanity_ss_{Guid.NewGuid().ToString("N")[..8]}.png");
        var args    = $"--headless=new --disable-gpu --no-sandbox --disable-dev-shm-usage {NoDeElevate} " +
                      $"--window-size={w},{h} --screenshot=\"{outFile}\" \"{url}\"";
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(browser, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            },
        };
        proc.Start();
        VanityAgent.Infra.ChildJob.Own(proc);   // the browser dies with the host
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        // entireProcessTree: a browser is a LAUNCHER plus a renderer, GPU and utility child per launch. Killing only
        // the launcher orphans all of them, and they never exit: a few hundred stray processes and many GB of RAM
        // after a day of page checks (2026-09-19). Every kill of a browser must take the whole tree.
        catch { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } }
        try
        {
            if (!File.Exists(outFile)) return null;
            var b = await File.ReadAllBytesAsync(outFile, ct).ConfigureAwait(false);
            File.Delete(outFile);
            return b;
        }
        catch { return null; }
    }

    // ── CDP path (JS inject, scroll, wait_for, crop via clip) ─────────────────

    /// <summary>Tallest single full-page part. Models downscale tall images: at 3000px a 1280-wide capture is still
    /// readable (16px text ≈ 8px after scaling); at 6000px body text turns to mush. Longer pages come in parts.</summary>
    internal const int MaxFullPageHeight = 3000;

    /// <summary>A TCP port the OS confirms is free right now. Used for the browser's debug endpoint.</summary>
    private static int FreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    internal static JsonElement CoderPageTarget(JsonElement targets)
    {
        var pages = targets.EnumerateArray().Where(t =>
            t.TryGetProperty("type", out var type) && type.GetString() == "page"
            && t.TryGetProperty("webSocketDebuggerUrl", out _)).ToList();
        return pages.OrderByDescending(t => t.TryGetProperty("url", out var u) && u.GetString() == "about:blank")
            .FirstOrDefault() is var page && page.ValueKind != JsonValueKind.Undefined ? page : targets[0];
    }

    private static async Task<(byte[]? Screenshot, string? JsResult)> CdpScreenshotAsync(string browser, string url,
        string js, string waitFor, int scrollY, List<JsonElement> actions, int w, int h, int cropH, int timeoutMs, CancellationToken ct, bool fullPage = false, bool captureImage = true, string focus = "")
    {
        var profileDir = Path.Combine(Path.GetTempPath(), $"vanity_pv_{Guid.NewGuid().ToString("N")[..8]}");
        // Ask the OS for a free port instead of guessing one. `9222 + new Random().Next(500)` seeded a fresh Random
        // per call, so two captures in the same clock tick drew the SAME port, and nothing checked it was free
        // anyway: the second browser exited at once, /json answered for the wrong instance or not at all, and the
        // call came back "Screenshot was empty." in under a second (2026-09-19). Binding port 0 and releasing it
        // leaves a brief race, but a port the OS just handed out is far safer than a guess.
        var debugPort = FreeTcpPort();
        Directory.CreateDirectory(profileDir);

        var args = $"--headless=new --disable-gpu --no-sandbox --disable-dev-shm-usage {NoDeElevate} " +
                   $"--window-size={w},{h} --remote-debugging-port={debugPort} " +
                   $"--user-data-dir=\"{profileDir}\" about:blank";
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo(browser, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            },
        };
        proc.Start();
        VanityAgent.Infra.ChildJob.Own(proc);   // the browser dies with the host

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            // Poll until Chrome's debug endpoint is ready (up to 8s, 200ms intervals)
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            string? jsonStr = null;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
                try
                {
                    jsonStr = await http.GetStringAsync($"http://localhost:{debugPort}/json", cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(jsonStr)) break;
                }
                catch { /* Chrome not ready yet */ }
                // The browser exiting on its own is terminal - it will never open the endpoint, so waiting the full
                // 8s only delays the failure. Its own stderr says why (a port already in use, a bad profile dir),
                // which is what the operator needs instead of a bare "Screenshot was empty."
                if (proc.HasExited)
                    throw new InvalidOperationException(
                        "the browser exited immediately (" + proc.ExitCode + "): "
                        + (await proc.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false)).Trim().Split('\n').FirstOrDefault());
            }
            if (string.IsNullOrEmpty(jsonStr))
                throw new InvalidOperationException("Chrome debug endpoint did not become ready.");

            using var jdoc = JsonDocument.Parse(jsonStr);
            // The page target, for every persona: /json lists service workers and other targets too, in no fixed order,
            // and the first entry is not always the tab. Only the coder picked the page; site-ops took entry [0], sent
            // Page.navigate to a target that never answers, and every page_view it made timed out at 30 s - 4 of 4 on
            // websisco while the coder's 33 calls on the same machine all worked (2026-09-30).
            var target = CoderPageTarget(jdoc.RootElement);
            var wsUrl = target.GetProperty("webSocketDebuggerUrl").GetString()!;

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token).ConfigureAwait(false);

            // What the browser itself reports while the page loads and runs: uncaught script errors, console.error
            // calls, requests that failed or came back 4xx/5xx. A screenshot shows none of it - a page with a dead
            // script and a missing logo looks fine - and the coder's "verify live" step passed exactly such pages
            // (websisco order form, 2026-10-06: an uncaught SyntaxError in the page's script, a logo that was not
            // a logo). Every event the socket delivers while a command is awaited is kept here.
            var browserSignals = new BrowserSignals();
            int msgId = 1;
            async Task<JsonNode?> Cdp(string method, object? par = null)
            {
                var payload = JsonSerializer.Serialize(new { id = msgId++, method, @params = par ?? new { } });
                await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
                while (true)
                {
                    var buf = new byte[65536]; var sb2 = new StringBuilder(); WebSocketReceiveResult res;
                    do { res = await ws.ReceiveAsync(buf, cts.Token).ConfigureAwait(false); sb2.Append(Encoding.UTF8.GetString(buf, 0, res.Count)); }
                    while (!res.EndOfMessage);
                    var node = JsonNode.Parse(sb2.ToString());
                    if (node?["id"]?.GetValue<int>() == msgId - 1) return node["result"];
                    if (node?["method"] is not null) browserSignals.Take(node);
                }
            }

            await Cdp("Page.enable").ConfigureAwait(false);
            await Cdp("Runtime.enable").ConfigureAwait(false);
            await Cdp("Network.enable").ConfigureAwait(false);
            // Chrome ignores --window-size below ~500px; a real phone viewport needs device-metrics emulation.
            await Cdp("Emulation.setDeviceMetricsOverride", new { width = w, height = h, deviceScaleFactor = 1, mobile = w <= MobileCheckMaxWidth }).ConfigureAwait(false);
            await Cdp("Page.navigate", new { url }).ConfigureAwait(false);
            // Wait for the document, not for the clock. A flat 2s sleep was paid on EVERY capture even when the page
            // was ready in a fraction of it - twenty captures in one run spent forty seconds asleep (2026-09-22).
            // Same poll the click path below already uses: leave as soon as it is complete, and never wait longer
            // than the old fixed delay, so a slow page is no worse off.
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(200, cts.Token).ConfigureAwait(false);
                try
                {
                    var st = await Cdp("Runtime.evaluate", new { expression = "document.readyState", returnByValue = true }).ConfigureAwait(false);
                    if (st?["result"]?["value"]?.GetValue<string>() == "complete") break;
                }
                catch { /* context torn down mid-navigation: poll again */ }
            }
            var ignored = new List<string>();   // actions that could not run, reported in the result
            var evalResults = new List<string>();   // one entry per `eval` action, in order

            // The document has to settle before a scroll means anything: readyState "complete" fires while fonts,
            // lazy images and sliders are still changing the heights above the target, so scrollTo(0, 3200) landed
            // on a different section than the one measured a second later (2026-09-23). Settle = fonts ready, then
            // document height, scroll position and the tracked element's offset unchanged across two checks 150ms
            // apart, bounded at 2.5s so a page that never stops animating still gets captured. Leaves as soon as
            // it is stable, so a static page pays one check.
            async Task Settle(string trackSelector = "")
            {
                var expr = $@"(async () => {{
  const deadline = Date.now() + 2500;
  try {{ await Promise.race([document.fonts.ready, new Promise(r => setTimeout(r, 1500))]); }} catch (e) {{}}
  const sel = {JsonSerializer.Serialize(trackSelector)};
  const probe = () => {{
    const el = sel ? document.querySelector(sel) : null;
    return Math.max(document.documentElement.scrollHeight, document.body.scrollHeight) + '|' + window.scrollY + '|' + (el ? Math.round(el.getBoundingClientRect().top + window.scrollY) : '');
  }};
  let last = probe();
  while (Date.now() < deadline) {{
    await new Promise(r => setTimeout(r, 150));
    const now = probe();
    if (now === last) return now;
    last = now;
  }}
  return last;
}})()";
                try { await Cdp("Runtime.evaluate", new { expression = expr, returnByValue = true, awaitPromise = true }).ConfigureAwait(false); }
                catch { /* a torn-down context is reported by the next step */ }
            }
            // Scroll a selector to the top of the viewport and let what it woke up settle. Reported when missing.
            var framed = "";   // the last selector scrolled to: a crop starts at that element, not at the viewport
            async Task ScrollTo(string sel, string what)
            {
                framed = sel;
                var node = await Cdp("Runtime.evaluate", new
                {
                    expression = $@"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(sel)}); if (!el) return 'not found'; el.scrollIntoView({{block:'start'}}); return 'ok'; }})()",
                    returnByValue = true,
                }).ConfigureAwait(false);
                if (node?["result"]?["value"]?.GetValue<string>() != "ok") ignored.Add($"{what} '{sel}' not found on the page");
                else await Settle(sel).ConfigureAwait(false);
            }
            await Settle(focus).ConfigureAwait(false);

            // Helper: click a selector via JS (works for synthetic clicks on any element)
            async Task ClickSelector(string sel)
            {
                var expr = $@"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(sel)}); if (!el) return 'not found'; el.scrollIntoView({{block:'center'}}); el.click(); return 'clicked'; }})()";
                var clicked = await Cdp("Runtime.evaluate", new { expression = expr, returnByValue = true }).ConfigureAwait(false);
                if (clicked?["result"]?["value"]?.GetValue<string>() == "not found") ignored.Add($"click: nothing matches {sel}");
                // A click may navigate: give the new document up to 3s to be complete before the next step reads it.
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(300, cts.Token).ConfigureAwait(false);
                    try
                    {
                        var st = await Cdp("Runtime.evaluate", new { expression = "document.readyState", returnByValue = true }).ConfigureAwait(false);
                        if (st?["result"]?["value"]?.GetValue<string>() == "complete") break;
                    }
                    catch { /* context torn down mid-navigation: poll again */ }
                }
            }

            // Helper: wait for a selector to appear
            async Task WaitForSelector(string sel)
            {
                for (int i = 0; i < 10; i++)
                {
                    var r = await Cdp("Runtime.evaluate", new { expression = $"!!document.querySelector({JsonSerializer.Serialize(sel)})", returnByValue = true }).ConfigureAwait(false);
                    if (r?["result"]?["value"]?.GetValue<bool>() == true) break;
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                }
            }

            // Built-in shorthands (backward compat)
            if (waitFor.Length > 0) await WaitForSelector(waitFor).ConfigureAwait(false);
            if (scrollY > 0)
            {
                await Cdp("Runtime.evaluate", new { expression = $"window.scrollTo(0,{scrollY})", returnByValue = true }).ConfigureAwait(false);
                await Settle().ConfigureAwait(false);
            }
            if (focus.Length > 0) await ScrollTo(focus, "focus").ConfigureAwait(false);

            // Execute actions sequence
            string? jsResult = null;
            int actionNo = 0;
            foreach (var raw in actions)
            {
                actionNo++;
                var action = CanonAction(raw, actionNo, ignored);
                var type = action.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                switch (type)
                {
                    case "click":
                        if (action.TryGetProperty("selector", out var cs))
                            await ClickSelector(cs.GetString() ?? "").ConfigureAwait(false);
                        break;
                    case "wait":
                    {   // {"wait": 2500} — let loaders, fonts and lazy content settle before the capture
                        var waitMs = action.TryGetProperty("ms", out var wv) && wv.ValueKind == JsonValueKind.Number ? Math.Clamp(wv.GetInt32(), 0, 15000) : 1000;
                        await Task.Delay(waitMs, cts.Token).ConfigureAwait(false);
                        break;
                    }
                    case "hover":
                        if (action.TryGetProperty("selector", out var hs))
                        {
                            var expr = $@"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(hs.GetString())}); if (el) {{ el.scrollIntoView({{block:'center'}}); el.dispatchEvent(new MouseEvent('mouseover',{{bubbles:true}})); el.dispatchEvent(new MouseEvent('mouseenter',{{bubbles:true}})); }} }})()";
                            await Cdp("Runtime.evaluate", new { expression = expr, returnByValue = true }).ConfigureAwait(false);
                            await Task.Delay(300, cts.Token).ConfigureAwait(false);
                        }
                        break;
                    case "type":
                        if (action.TryGetProperty("selector", out var ts) && action.TryGetProperty("value", out var tv))
                        {
                            var typeSel = ts.GetString() ?? "";
                            var typeVal = tv.GetString() ?? "";
                            string? fallbackSel = typeVal.Contains('@') && typeVal.Contains('.')
                                ? "input[type=\"email\"]"
                                : typeSel.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0
                                ? "input[type=\"password\"]"
                                : null;
                            var fillJs = $@"(() => {{
  let el = document.querySelector({JsonSerializer.Serialize(typeSel)});
  let used = {JsonSerializer.Serialize(typeSel)};
  if (!el) {{ const fb = {JsonSerializer.Serialize(fallbackSel ?? "")}; if (fb) {{ el = document.querySelector(fb); used = fb; }} }}
  if (!el) return 'none';
  el.scrollIntoView({{block:'center'}}); el.focus();
  el.value = {JsonSerializer.Serialize(typeVal)};
  el.dispatchEvent(new Event('input', {{bubbles:true}}));
  el.dispatchEvent(new Event('change', {{bubbles:true}}));
  return used;
}})()";
                            var fillNode = await Cdp("Runtime.evaluate", new { expression = fillJs, returnByValue = true }).ConfigureAwait(false);
                            var filled = fillNode?["result"]?["value"]?.GetValue<string>() ?? "none";
                            if (filled == "none")
                                ignored.Add($"input: nothing matches {typeSel}" + (fallbackSel != null ? $" or {fallbackSel}" : ""));
                            else if (filled != typeSel)
                                ignored.Add($"input: nothing matches {typeSel}, filled {filled} instead");
                            await Task.Delay(200, cts.Token).ConfigureAwait(false);
                        }
                        break;
                    case "scroll":
                        var sy2 = action.TryGetProperty("y", out var syv) && syv.ValueKind == JsonValueKind.Number ? syv.GetInt32() : 0;
                        await Cdp("Runtime.evaluate", new { expression = $"window.scrollTo(0,{sy2})", returnByValue = true }).ConfigureAwait(false);
                        await Settle().ConfigureAwait(false);
                        break;
                    case "scroll_to":
                        if (action.TryGetProperty("selector", out var sts))
                            await ScrollTo(sts.GetString() ?? "", "scroll_to").ConfigureAwait(false);
                        break;
                    case "wait_ms":
                        var ms = action.TryGetProperty("ms", out var msv) && msv.ValueKind == JsonValueKind.Number ? msv.GetInt32() : 500;
                        await Task.Delay(Math.Min(ms, 5000), cts.Token).ConfigureAwait(false);
                        break;
                    case "wait_for":
                        if (action.TryGetProperty("selector", out var wfs))
                            await WaitForSelector(wfs.GetString() ?? "").ConfigureAwait(false);
                        break;
                    case "eval":
                        if (action.TryGetProperty("js", out var ejs))
                        {
                            var jsNode = await Cdp("Runtime.evaluate", new { expression = Callable(ejs.GetString() ?? ""), returnByValue = true, awaitPromise = true }).ConfigureAwait(false);
                            var evalVal = jsNode?["result"]?["value"];
                            var text = evalVal != null ? evalVal.ToJsonString()
                                     : jsNode?["exceptionDetails"] is { } exd
                                         ? "JS error: " + (exd["exception"]?["description"]?.GetValue<string>() ?? exd["text"]?.GetValue<string>() ?? "exception")
                                         : null;
                            // EVERY eval's value comes back, numbered. Overwriting meant a batch of probes returned
                            // only its last one, so the only way to learn five things about a page was five calls -
                            // and each call is a browser launch plus the whole conversation re-sent (2026-09-22).
                            // One entry per eval, ALWAYS: a probe that evaluated to nothing still takes its place, or
                            // the numbering stops matching the probes that were sent and the caller cannot tell which
                            // of them was the one that came back empty.
                            evalResults.Add(text ?? "undefined");
                        }
                        break;
                }
            }

            // Shorthand js param: evaluated last, so it reads whatever the actions left behind.
            if (js.Length > 0)
            {
                var jsNode = await Cdp("Runtime.evaluate", new { expression = Callable(js), returnByValue = true, awaitPromise = true }).ConfigureAwait(false);
                var val = jsNode?["result"]?["value"];
                if (val != null) jsResult = val.ToJsonString();
                else if (jsNode?["exceptionDetails"] is { } exd)                       // a throwing expression is reported, not swallowed
                    jsResult = "JS error: " + (exd["exception"]?["description"]?.GetValue<string>() ?? exd["text"]?.GetValue<string>() ?? "exception");
                evalResults.Add(jsResult ?? "undefined");   // one slot per probe sent, same rule as the eval actions
            }
            if (evalResults.Count > 0)
            {
                // Batching must stay cheaper than the calls it replaces. One probe that dumps a whole stylesheet
                // would otherwise make the batch bigger than the turns it saved, and that result is re-sent on every
                // later call. Each entry is clipped on its own, so one greedy probe cannot crowd out the others.
                const int PerEval = 2_000;
                static string Clip(string r) => r.Length <= PerEval ? r : r[..PerEval] + $"… (+{r.Length - PerEval:N0} chars)";
                jsResult = evalResults.Count == 1
                    ? Clip(evalResults[0])
                    : string.Join("\n", evalResults.Select((r, k) => $"[{k + 1}] {Clip(r)}"));
            }
            // The current address after the actions: a click that navigated is visible without asking for it.
            if (actions.Count > 0)
            {
                var loc = await Cdp("Runtime.evaluate", new { expression = "location.href", returnByValue = true }).ConfigureAwait(false);
                var now = loc?["result"]?["value"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(now) && !string.Equals(now, url, StringComparison.Ordinal)) jsResult = (jsResult is null ? "" : jsResult + "\n") + "[after the actions the page is " + now + "]";
            }
            if (ignored.Count > 0) jsResult = (jsResult is null ? "" : jsResult + "\n") + "[" + string.Join("; ", ignored) + "]";

            // A coder measurement should not capture an image only to discard it, or run unrelated layout probes.
            if (!captureImage) return (null, jsResult);

            // Words that outlive the image. The screenshot leaves the context after a couple of tool calls (see
            // PruneStaleScreenshots) and "[screenshot of URL]" alone left the model with no record of what it saw,
            // so it took the same screenshot 20 times in one run (2026-09-22). The digest - title, headings, forms,
            // inputs, buttons and nav links with their selectors - stays in the history as text.
            try
            {
                var dg = await Cdp("Runtime.evaluate", new { expression = PageDigestJs, returnByValue = true }).ConfigureAwait(false);
                var dv = dg?["result"]?["value"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(dv))
                    jsResult = (jsResult is null ? "" : jsResult + "\n") + "[page digest]\n" + (dv.Length > 2500 ? dv[..2500] + "…" : dv);
            }
            catch (Exception ex) { Log.Debug("[page_view] digest skipped: " + ex.Message); }

            // Deterministic layout checks the model cannot be trusted to see in a screenshot:
            //  - horizontal overflow at phone widths (names the widest offenders),
            //  - clipped text at any width (a badge, button or heading whose content is cut by its own box),
            // both scanned over the whole DOM, not just the part in the viewport.
            try
            {
                if (w <= MobileCheckMaxWidth)
                {
                    var ovNode = await Cdp("Runtime.evaluate", new { expression = OverflowProbeJs, returnByValue = true }).ConfigureAwait(false);
                    var ov = ovNode?["result"]?["value"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(ov)) jsResult = (jsResult is null ? "" : jsResult + "\n") + ov;
                }
                var clNode = await Cdp("Runtime.evaluate", new { expression = ClippedTextProbeJs, returnByValue = true }).ConfigureAwait(false);
                var cl = clNode?["result"]?["value"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(cl)) jsResult = (jsResult is null ? "" : jsResult + "\n") + cl;
                if (scrollY == 0)
                {   // measured at the top, before any scrolling: what a capture tool, a crawler or a visitor without JS sees
                    var hdNode = await Cdp("Runtime.evaluate", new { expression = HiddenAtLoadProbeJs, returnByValue = true }).ConfigureAwait(false);
                    var hd = hdNode?["result"]?["value"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(hd)) jsResult = (jsResult is null ? "" : jsResult + "\n") + hd;
                }
                var lkNode = await Cdp("Runtime.evaluate", new { expression = CrampedLinksProbeJs, returnByValue = true }).ConfigureAwait(false);
                var lk = lkNode?["result"]?["value"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(lk)) jsResult = (jsResult is null ? "" : jsResult + "\n") + lk;
                // The browser's own findings, gathered since Page.navigate: read last so every event up to here is in.
                var signals = browserSignals.Report();
                if (signals.Length > 0) jsResult = (jsResult is null ? "" : jsResult + "\n") + signals;
            }
            catch { /* probes are best-effort */ }

            await Settle(focus).ConfigureAwait(false);

            // CDP clip for crop. With captureBeyondViewport the clip is in document coordinates, so y must be the
            // current scroll position: at y = 0 every cropped capture showed the top of the page whatever scroll_y,
            // a scroll action or a focus had done, and the model retried with new offsets (2026-09-23).
            int pageY = 0;
            try
            {
                // A section near the end of the page cannot be scrolled to the top of the viewport (the page runs
                // out first), so a crop from the scroll position shows what is above it. When an element was
                // named, the crop starts at the element itself, a little above it; captureBeyondViewport renders
                // any document region.
                var expr = framed.Length > 0
                    ? $@"(() => {{ const el = document.querySelector({JsonSerializer.Serialize(framed)}); return Math.round(el ? Math.max(0, el.getBoundingClientRect().top + window.scrollY - 16) : window.scrollY); }})()"
                    : "Math.round(window.scrollY)";
                var syNode = await Cdp("Runtime.evaluate", new { expression = expr, returnByValue = true }).ConfigureAwait(false);
                pageY = syNode?["result"]?["value"]?.GetValue<int>() ?? 0;
            }
            catch { }
            object? clip = cropH > 0 ? new { x = 0, y = pageY, width = w, height = Math.Min(cropH, h), scale = 1 } : null;
            if (fullPage)
            {
                // Keep the real viewport (so 100vh sections keep their true size), wake up lazy images and
                // scroll-reveal blocks by scrolling through the page once, then capture beyond the viewport.
                await Cdp("Runtime.evaluate", new
                {
                    expression = @"(async () => {
  document.querySelectorAll('img[loading=""lazy""]').forEach(i => i.loading = 'eager');
  const step = Math.max(300, window.innerHeight - 100);
  const total = Math.max(document.documentElement.scrollHeight, document.body.scrollHeight);
  for (let y = 0; y < total; y += step) { window.scrollTo(0, y); await new Promise(r => setTimeout(r, 60)); }
  window.scrollTo(0, 0);
  await new Promise(r => setTimeout(r, 400));
  return Math.max(document.documentElement.scrollHeight, document.body.scrollHeight);
})()", returnByValue = true, awaitPromise = true,
                }).ConfigureAwait(false);
                var metrics = await Cdp("Page.getLayoutMetrics").ConfigureAwait(false);
                var contentH = (int)Math.Ceiling(metrics?["cssContentSize"]?["height"]?.GetValue<double>() ?? metrics?["contentSize"]?["height"]?.GetValue<double>() ?? h);
                var startY = Math.Max(0, scrollY);
                var partH  = Math.Clamp(contentH - startY, Math.Min(h, contentH), MaxFullPageHeight);
                clip = new { x = 0, y = startY, width = w, height = partH, scale = 1 };
                var parts = (int)Math.Ceiling(contentH / (double)MaxFullPageHeight);
                var partNo = startY / MaxFullPageHeight + 1;
                jsResult = (jsResult is null ? "" : jsResult + "\n") +
                    (parts > 1
                        ? $"[full-page capture {w}x{partH}, part {partNo} of {parts} (page is {contentH}px tall) — for the next part call again with full_page and scroll_y: {startY + MaxFullPageHeight}]"
                        : $"[full-page capture {w}x{partH} — the whole page]");
            }
            var shot = await Cdp("Page.captureScreenshot",
                clip != null ? new { format = "jpeg", quality = fullPage ? 70 : 80, clip, captureBeyondViewport = true }
                             : (object)new { format = "jpeg", quality = 80 }
            ).ConfigureAwait(false);

            var b64 = shot?["data"]?.GetValue<string>();
            return (b64 == null ? null : Convert.FromBase64String(b64), jsResult);
        }
        catch (OperationCanceledException) { throw; }
        finally
        {
            // Kill the whole tree unconditionally. When the launcher exited early (de-elevation escape or
            // startup crash) its GPU/renderer children are still alive. Kill(entireProcessTree) walks the
            // snapshot by parent PID and reaches them even if proc itself has already exited; the resulting
            // exception from killing an exited process is swallowed by the catch.
            try { proc.Kill(entireProcessTree: true); proc.WaitForExit(2000); } catch { }
            try { Directory.Delete(profileDir, true); } catch { }
        }
    }

    // ── Image helpers (System.Drawing.Common — Windows only, already in project) ──

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] CropToJpeg(byte[] pngBytes, int cropH)
    {
        try
        {
            using var ms  = new MemoryStream(pngBytes);
            using var bmp = new Bitmap(ms);
            int h         = Math.Min(cropH, bmp.Height);
            using var cropped = bmp.Clone(new Rectangle(0, 0, bmp.Width, h), bmp.PixelFormat);
            using var out_ = new MemoryStream();
            var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            var encParams  = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            cropped.Save(out_, jpegCodec, encParams);
            return out_.ToArray();
        }
        catch { return pngBytes; }
    }

    private static ToolResultRecord Error(string toolCallId, string msg) => new()
    {
        ToolCallId = toolCallId,
        ToolName   = "page_view",
        Output     = msg,
        IsError    = true,
    };
}
