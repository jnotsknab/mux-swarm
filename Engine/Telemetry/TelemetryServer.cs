using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MuxSwarm.Engine.Telemetry;

/// <summary>
/// Standalone Kestrel dashboard for the persistent telemetry sink (/telemetry, --telemetry).
/// Grafana-style stat tiles, canvas time-series charts, and per-model / per-agent / per-tool tables over
/// <see cref="TelemetryStore"/>, themed to match the main web UI (same Zinc palette, fonts,
/// theme presets, and settings-panel conventions) so the two surfaces feel like one product.
/// Zero external dependencies: one embedded HTML document, no chart library, no npm. Binds
/// loopback by default via <c>serve.address</c> discipline; runs on its own port (default 6725)
/// so it can live alongside --serve.
/// </summary>
public static class TelemetryServer
{
    /// <summary>Default dashboard port (web UI default + 2, leaving 6724 for test instances).</summary>
    public const int DefaultPort = 6725;

    private static WebApplication? _app;

    /// <summary>True while a dashboard instance is listening (used by /telemetry toggle text).</summary>
    public static bool IsRunning => _app is not null;

    /// <summary>Start the dashboard on <paramref name="port"/>; no-op when already running.</summary>
    public static async Task<string> StartAsync(int port = DefaultPort)
    {
        if (_app is not null) return $"Telemetry dashboard already running on port {port}.";
        var builder = WebApplication.CreateSlimBuilder();
        string address = App.Config?.ServeAddress ?? "127.0.0.1";
        builder.WebHost.UseUrls($"http://{address}:{port}");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/", () => Results.Content(DashboardHtml, "text/html"));
        app.MapGet("/api/telemetry/summary", (string? range) =>
        {
            var events = TelemetryStore.Load(ParseRange(range));
            return Results.Json(TelemetryStore.Summarize(events));
        });
        app.MapGet("/api/telemetry/series", (string? range) =>
        {
            var since = ParseRange(range);
            var events = TelemetryStore.Load(since);
            return Results.Json(TelemetryStore.Series(events, since));
        });
        app.MapGet("/api/telemetry/models", (string? range) =>
        {
            var events = TelemetryStore.Load(ParseRange(range));
            return Results.Json(new
            {
                models = TelemetryStore.ByModel(events),
                agents = TelemetryStore.ByAgent(events),
            });
        });
        app.MapGet("/api/telemetry/tools", (string? range) =>
        {
            var events = TelemetryStore.Load(ParseRange(range));
            return Results.Json(TelemetryStore.ByTool(events));
        });

        await app.StartAsync();
        _app = app;
        return $"Telemetry dashboard: http://{(address == "0.0.0.0" ? "localhost" : address)}:{port}/";
    }

    /// <summary>Stop the dashboard (used by /telemetry off and shutdown).</summary>
    public static async Task StopAsync()
    {
        var app = _app;
        _app = null;
        if (app is not null)
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await app.StopAsync(stopCts.Token); } catch { /* best-effort */ }
        }
    }

    /// <summary>Parse the dashboard range parameter: 24h / 7d / 30d; null or "all" = all time.</summary>
    internal static DateTime? ParseRange(string? range) => (range ?? "").Trim().ToLowerInvariant() switch
    {
        "24h" => DateTime.UtcNow.AddHours(-24),
        "7d" => DateTime.UtcNow.AddDays(-7),
        "30d" => DateTime.UtcNow.AddDays(-30),
        _ => null,
    };

    // The dashboard document. Same CSS variable names, Zinc default palette, font stack, and
    // theme presets as Runtime/mux-web-app/index.html so the pages read as one family; the
    // settings popover mirrors the web UI's settings-panel conventions (theme preset dots +
    // native pickers). Charts are hand-drawn canvas - no external libraries.
    private const string DashboardHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Mux-Swarm - Telemetry</title>
<style>
:root {
  --bg: #09090b; --bg-card: rgba(24,24,27,.6); --bg-input: rgba(39,39,42,.5);
  --border: rgba(63,63,70,.4); --text: #fafafa; --muted: #a1a1aa;
  --blue: #3b82f6; --green: #10b981; --amber: #f59e0b; --red: #ef4444;
  --agent: #8b5cf6; --accent: #64B4DC; --accent-rgb: 100,180,220;
  --font-mono: 'JetBrains Mono','Cascadia Code','Fira Code',monospace;
  --font-sans: 'IBM Plex Sans',-apple-system,sans-serif;
  --radius: .5rem;
}
* { box-sizing: border-box; margin: 0; }
body { background: var(--bg); color: var(--text); font-family: var(--font-sans); min-height: 100vh; }
header { display:flex; align-items:center; gap:.75rem; padding:.9rem 1.25rem; border-bottom:1px solid var(--border); position:sticky; top:0; background:var(--bg); z-index:5; }
header h1 { font-size:1rem; font-weight:600; letter-spacing:.02em; }
header .dot { width:.55rem; height:.55rem; border-radius:50%; background:var(--accent); box-shadow:0 0 8px rgba(var(--accent-rgb),.8); }
header .spacer { flex:1; }
.range-group { display:flex; gap:.25rem; background:var(--bg-input); border:1px solid var(--border); border-radius:var(--radius); padding:.2rem; }
.range-btn { background:none; border:none; color:var(--muted); font:inherit; font-size:.8rem; padding:.3rem .7rem; border-radius:calc(var(--radius) - .2rem); cursor:pointer; }
.range-btn.active { background:rgba(var(--accent-rgb),.18); color:var(--accent); }
.icon-btn { background:var(--bg-input); border:1px solid var(--border); color:var(--muted); border-radius:var(--radius); width:2rem; height:2rem; cursor:pointer; display:grid; place-items:center; }
.icon-btn:hover { color:var(--text); }
main { max-width: 1180px; margin: 0 auto; padding: 1.25rem; display:grid; gap:1rem; }
.tiles { display:grid; grid-template-columns:repeat(auto-fit,minmax(160px,1fr)); gap:.75rem; }
.tile { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); padding: .85rem 1rem; }
.tile .label { font-size:.72rem; text-transform:uppercase; letter-spacing:.08em; color:var(--muted); }
.tile .value { font-family:var(--font-mono); font-size:1.35rem; margin-top:.3rem; }
.tile .sub { font-size:.72rem; color:var(--muted); margin-top:.15rem; }
.card { background:var(--bg-card); border:1px solid var(--border); border-radius:var(--radius); padding:1rem; }
.card h2 { font-size:.8rem; text-transform:uppercase; letter-spacing:.08em; color:var(--muted); margin-bottom:.75rem; }
canvas { width:100%; height:220px; display:block; }
table { width:100%; border-collapse:collapse; font-size:.85rem; }
th { text-align:left; color:var(--muted); font-weight:500; font-size:.72rem; text-transform:uppercase; letter-spacing:.06em; padding:.4rem .5rem; border-bottom:1px solid var(--border); }
td { padding:.45rem .5rem; border-bottom:1px solid var(--border); font-family:var(--font-mono); font-size:.8rem; }
td.name { font-family:var(--font-sans); }
.grid2 { display:grid; grid-template-columns:1fr 1fr; gap:1rem; }
@media (max-width: 860px) { .grid2 { grid-template-columns:1fr; } }
.empty { color:var(--muted); font-size:.85rem; padding:.75rem .25rem; }
/* Settings popover - mirrors the web UI settings panel + theme picker conventions. */
.settings-pop { position:fixed; top:3.4rem; right:1.25rem; width:260px; background:var(--bg-card); backdrop-filter:blur(12px); border:1px solid var(--border); border-radius:var(--radius); padding:1rem; display:none; z-index:10; }
.settings-pop.open { display:block; }
.settings-pop h3 { font-size:.72rem; text-transform:uppercase; letter-spacing:.08em; color:var(--muted); margin-bottom:.6rem; }
.theme-presets { display:flex; flex-wrap:wrap; gap:.45rem; }
.theme-preset-btn { display:flex; align-items:center; gap:.4rem; background:var(--bg-input); border:1px solid var(--border); color:var(--muted); border-radius:var(--radius); padding:.3rem .6rem; font:inherit; font-size:.78rem; cursor:pointer; }
.theme-preset-btn.active { color:var(--text); border-color:rgba(var(--accent-rgb),.6); }
.theme-preset-btn .dot { width:.6rem; height:.6rem; border-radius:50%; border:1px solid var(--border); }
</style>
</head>
<body>
<header>
  <span class="dot"></span>
  <h1>Mux-Swarm Telemetry</h1>
  <span class="spacer"></span>
  <div class="range-group" id="ranges">
    <button class="range-btn" data-range="24h">24h</button>
    <button class="range-btn" data-range="7d">7d</button>
    <button class="range-btn" data-range="30d">30d</button>
    <button class="range-btn active" data-range="all">All</button>
  </div>
  <button class="icon-btn" id="settingsBtn" title="Theme">
    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="3"/><path d="M12 1v3m0 16v3M4.2 4.2l2.1 2.1m11.4 11.4 2.1 2.1M1 12h3m16 0h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1"/></svg>
  </button>
</header>
<div class="settings-pop" id="settingsPop">
  <h3>Theme</h3>
  <div class="theme-presets" id="themePresets"></div>
</div>
<main>
  <div class="tiles" id="tiles"></div>
  <div class="card"><h2>Tokens over time</h2><canvas id="tokChart"></canvas></div>
  <div class="grid2">
    <div class="card"><h2>Estimated cost over time</h2><canvas id="costChart"></canvas></div>
    <div class="card"><h2>Tool calls over time</h2><canvas id="toolChart"></canvas></div>
  </div>
  <div class="grid2">
    <div class="card"><h2>By model</h2><div id="modelTable"></div></div>
    <div class="card"><h2>By agent</h2><div id="agentTable"></div></div>
  </div>
  <div class="card"><h2>By tool</h2><div id="toolTable"></div></div>
</main>
<script>
// Theme presets: same ids/palettes as the main web UI so the choice feels continuous.
const themes = [
  { id:'zinc', name:'Zinc', dot:'#09090b', vars:{ '--bg':'#09090b','--bg-card':'rgba(24,24,27,.6)','--bg-input':'rgba(39,39,42,.5)','--border':'rgba(63,63,70,.4)','--text':'#fafafa','--muted':'#a1a1aa','--accent':'#64B4DC','--accent-rgb':'100,180,220','--green':'#10b981','--blue':'#3b82f6','--amber':'#f59e0b','--red':'#ef4444','--agent':'#8b5cf6' } },
  { id:'light', name:'Light', dot:'#f4f4f5', vars:{ '--bg':'#f4f4f5','--bg-card':'rgba(255,255,255,.8)','--bg-input':'rgba(255,255,255,.9)','--border':'rgba(228,228,231,.8)','--text':'#18181b','--muted':'#71717a','--accent':'#0284c7','--accent-rgb':'2,132,199','--green':'#059669','--blue':'#2563eb','--amber':'#d97706','--red':'#dc2626','--agent':'#7c3aed' } },
  { id:'ocean', name:'Ocean', dot:'#0f172a', vars:{ '--bg':'#0f172a','--bg-card':'rgba(30,41,59,.6)','--bg-input':'rgba(51,65,85,.5)','--border':'rgba(71,85,105,.4)','--text':'#f8fafc','--muted':'#94a3b8','--accent':'#2dd4bf','--accent-rgb':'45,212,191','--green':'#34d399','--blue':'#38bdf8','--amber':'#fbbf24','--red':'#f87171','--agent':'#a78bfa' } },
  { id:'matrix', name:'Matrix', dot:'#000000', vars:{ '--bg':'#000000','--bg-card':'rgba(0,20,0,.6)','--bg-input':'rgba(0,40,0,.5)','--border':'rgba(0,80,0,.4)','--text':'#4ade80','--muted':'#22c55e','--accent':'#22c55e','--accent-rgb':'34,197,94','--green':'#86efac','--blue':'#4ade80','--amber':'#facc15','--red':'#ef4444','--agent':'#4ade80' } },
  { id:'mocha', name:'Mocha', dot:'#1e1e2e', vars:{ '--bg':'#1e1e2e','--bg-card':'rgba(49,50,68,.6)','--bg-input':'rgba(69,71,90,.5)','--border':'rgba(88,91,112,.4)','--text':'#cdd6f4','--muted':'#a6adc8','--accent':'#cba6f7','--accent-rgb':'203,166,247','--green':'#a6e3a1','--blue':'#89b4fa','--amber':'#f9e2af','--red':'#f38ba8','--agent':'#cba6f7' } },
];
let themeId = localStorage.getItem('muxTelemetryTheme') || 'zinc';
function applyTheme() {
  const t = themes.find(x => x.id === themeId) || themes[0];
  for (const [k, v] of Object.entries(t.vars)) document.documentElement.style.setProperty(k, v);
  document.querySelectorAll('.theme-preset-btn').forEach(b => b.classList.toggle('active', b.dataset.id === themeId));
  draw();
}
const presetsEl = document.getElementById('themePresets');
for (const t of themes) {
  const btn = document.createElement('button');
  btn.className = 'theme-preset-btn'; btn.dataset.id = t.id;
  btn.innerHTML = `<span class="dot" style="background:${t.dot}"></span>${t.name}`;
  btn.onclick = () => { themeId = t.id; localStorage.setItem('muxTelemetryTheme', themeId); applyTheme(); };
  presetsEl.appendChild(btn);
}
document.getElementById('settingsBtn').onclick = () => document.getElementById('settingsPop').classList.toggle('open');
document.addEventListener('click', e => {
  const pop = document.getElementById('settingsPop');
  if (pop.classList.contains('open') && !pop.contains(e.target) && e.target.closest('#settingsBtn') === null)
    pop.classList.remove('open');
});

let range = 'all';
let series = [], summary = null, models = [], agents = [], tools = [];
document.getElementById('ranges').addEventListener('click', e => {
  const btn = e.target.closest('.range-btn'); if (!btn) return;
  range = btn.dataset.range;
  document.querySelectorAll('.range-btn').forEach(b => b.classList.toggle('active', b === btn));
  refresh();
});

const fmt = n => n >= 1e9 ? (n/1e9).toFixed(2)+'B' : n >= 1e6 ? (n/1e6).toFixed(2)+'M' : n >= 1e3 ? (n/1e3).toFixed(1)+'k' : String(n);
const money = n => '$' + (n >= 100 ? n.toFixed(0) : n >= 1 ? n.toFixed(2) : n.toFixed(4));

async function refresh() {
  const q = range === 'all' ? '' : ('?range=' + range);
  try {
    [summary, series, modelsResp, tools] = await Promise.all([
      fetch('/api/telemetry/summary' + q).then(r => r.json()),
      fetch('/api/telemetry/series' + q).then(r => r.json()),
      fetch('/api/telemetry/models' + q).then(r => r.json()),
      fetch('/api/telemetry/tools' + q).then(r => r.json()),
    ]);
    models = modelsResp.models || []; agents = modelsResp.agents || []; tools = tools || [];
  } catch { return; }
  renderTiles(); renderTables(); draw();
}

function renderTiles() {
  if (!summary) return;
  const t = document.getElementById('tiles');
  const total = (summary.totalIn|0) + (summary.totalOut|0);
  t.innerHTML = `
    <div class="tile"><div class="label">Total tokens</div><div class="value">${fmt(total)}</div><div class="sub">${fmt(summary.totalIn)} in - ${fmt(summary.totalOut)} out</div></div>
    <div class="tile"><div class="label">Cached</div><div class="value">${fmt(summary.totalCached)}</div><div class="sub">prompt-cache reads</div></div>
    <div class="tile"><div class="label">Reasoning</div><div class="value">${fmt(summary.totalReason)}</div><div class="sub">thinking tokens</div></div>
    <div class="tile"><div class="label">Est. cost</div><div class="value">${money(summary.totalCost)}</div><div class="sub">API list prices; subscriptions bill separately</div></div>
    <div class="tile"><div class="label">Tool calls</div><div class="value">${fmt(summary.toolCalls)}</div><div class="sub">${summary.toolErrors ? `<span style="color:var(--red)">${fmt(summary.toolErrors)} errors</span> - ` : ''}${fmt(summary.compactions)} compactions${summary.tokensSaved ? ` (${fmt(summary.tokensSaved)} tok saved)` : ''}</div></div>
    <div class="tile"><div class="label">Turns</div><div class="value">${fmt(summary.turns || 0)}</div><div class="sub">${summary.turns ? 'avg ' + (summary.avgTurnMs/1000).toFixed(1) + 's per turn' : 'agent responses'}</div></div>
    <div class="tile"><div class="label">Delegations</div><div class="value">${fmt(summary.delegations)}</div><div class="sub">since ${summary.firstEvent ? new Date(summary.firstEvent).toLocaleDateString() : '-'}</div></div>`;
}

function renderTables() {
  const mt = document.getElementById('modelTable');
  mt.innerHTML = models.length === 0 ? '<div class="empty">No usage recorded yet.</div>'
    : '<table><tr><th>Model</th><th>In</th><th>Out</th><th>Cached</th><th>Cost</th><th>Tools</th></tr>'
      + models.map(m => `<tr><td class="name">${m.model}</td><td>${fmt(m.in)}</td><td>${fmt(m.out)}</td><td>${fmt(m.cached)}</td><td>${money(m.cost)}</td><td>${fmt(m.tools)}</td></tr>`).join('')
      + '</table>';
  const at = document.getElementById('agentTable');
  at.innerHTML = agents.length === 0 ? '<div class="empty">No agent activity recorded yet.</div>'
    : '<table><tr><th>Agent</th><th>Tokens</th><th>Cost</th><th>Turns</th><th>Deleg.</th><th>Time</th></tr>'
      + agents.map(a => `<tr><td class="name">${a.agent}</td><td>${fmt((a.in|0)+(a.out|0))}</td><td>${money(a.cost||0)}</td><td>${fmt(a.turns||0)}</td><td>${fmt(a.delegations)}</td><td>${(a.totalMs/1000).toFixed(1)}s</td></tr>`).join('')
      + '</table>';
  const tt = document.getElementById('toolTable');
  tt.innerHTML = tools.length === 0 ? '<div class="empty">No tool calls recorded yet.</div>'
    : '<table><tr><th>Tool</th><th>Calls</th><th>Errors</th></tr>'
      + tools.map(t => `<tr><td class="name">${t.tool}</td><td>${fmt(t.calls)}</td><td>${t.errors ? `<span style="color:var(--red)">${fmt(t.errors)}</span>` : '0'}</td></tr>`).join('')
      + '</table>';
}

function css(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

function drawChart(id, keys, colors) {
  const cv = document.getElementById(id), ctx = cv.getContext('2d');
  const w = cv.width = cv.clientWidth * devicePixelRatio, h = cv.height = 220 * devicePixelRatio;
  ctx.clearRect(0, 0, w, h);
  if (!series.length) {
    ctx.fillStyle = css('--muted'); ctx.font = `${12*devicePixelRatio}px ${css('--font-sans')}`;
    ctx.fillText('No data in range.', 12*devicePixelRatio, 24*devicePixelRatio);
    return;
  }
  const pad = 34 * devicePixelRatio, innerW = w - pad*1.5, innerH = h - pad*1.6;
  let max = 0;
  for (const p of series) for (const k of keys) max = Math.max(max, p[k] || 0);
  if (max === 0) max = 1;
  ctx.strokeStyle = css('--border'); ctx.lineWidth = 1;
  ctx.font = `${10*devicePixelRatio}px ${css('--font-mono')}`;
  for (let g = 0; g <= 3; g++) {
    const y = pad*0.4 + innerH * g/3;
    ctx.beginPath(); ctx.moveTo(pad, y); ctx.lineTo(pad + innerW, y); ctx.stroke();
    ctx.fillStyle = css('--muted');
    ctx.fillText(fmt(Math.round(max*(1 - g/3))), 4*devicePixelRatio, y + 3*devicePixelRatio);
  }
  keys.forEach((k, ki) => {
    ctx.strokeStyle = css(colors[ki]); ctx.lineWidth = 2 * devicePixelRatio;
    ctx.beginPath();
    series.forEach((p, i) => {
      const x = pad + innerW * (series.length === 1 ? 0.5 : i/(series.length - 1));
      const y = pad*0.4 + innerH * (1 - (p[k] || 0)/max);
      i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
    });
    ctx.stroke();
  });
  ctx.fillStyle = css('--muted');
  const first = series[0].bucket, last = series[series.length - 1].bucket;
  ctx.fillText(first, pad, h - 8*devicePixelRatio);
  const lw = ctx.measureText(last).width;
  ctx.fillText(last, pad + innerW - lw, h - 8*devicePixelRatio);
}

function draw() {
  drawChart('tokChart', ['in', 'out'], ['--accent', '--agent']);
  drawChart('costChart', ['cost'], ['--green']);
  drawChart('toolChart', ['tools'], ['--amber']);
}

window.addEventListener('resize', draw);
applyTheme();
refresh();
setInterval(refresh, 15000);
</script>
</body>
</html>
""";
}
