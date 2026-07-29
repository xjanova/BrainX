/* BrainX Universe HUD — the dashboard as a heads-up display over the galaxy.
 *
 * Self-contained on purpose: it attaches its OWN listener to the WebView2
 * message channel rather than threading cases through app.js's dispatcher, so
 * the galaxy renderer and the HUD can be changed without touching each other.
 * Unknown message types are ignored by both sides.
 *
 * Activation is opt-in via ?hud=1 (the main Universe view passes it). The
 * wallpaper and dashboard-embed modes load this same page and must stay
 * exactly as they were, so everything here is gated on body.hud-active.
 */

const QS = new URLSearchParams(location.search);
const HUD_ON = QS.get('hud') === '1';

/* ?hudDemo=1 fills every panel with representative data and no host.
 * Design work on a HUD needs the HUD to be FULL — empty panels hide exactly
 * the problems worth catching (a number that overflows, a feed line that wraps,
 * a label that collides). It also means the layout can be reviewed in a plain
 * browser instead of a WPF build + app restart per iteration. Never fires
 * unless explicitly asked for. */
const HUD_DEMO = QS.get('hudDemo') === '1';

/* Boot steps. Each one is a section of the old dashboard; the bar fills as the
 * host delivers them, so a slow section is visible as a slow section instead of
 * the whole window looking frozen — which is what the WPF dashboard did. */
const STEPS = [
    { id: 'galaxy',    label: 'Rendering galaxy' },
    { id: 'stats',     label: 'Reading brain index' },
    { id: 'expertise', label: 'Mapping expertise' },
    { id: 'activity',  label: 'Attaching activity feed' },
    { id: 'agents',    label: 'Locating agents' },
    { id: 'network',   label: 'Joining mesh' },
    { id: 'system',    label: 'Polling system' },
    { id: 'usage',     label: 'Tallying usage' },
];

/* A section that never arrives must not hold the boot screen hostage. After
 * this long we mark the stragglers "skipped" and show the HUD anyway — a HUD
 * missing one panel beats a black screen with a stuck progress bar. */
const BOOT_DEADLINE_MS = 9000;

const state = { done: new Set(), started: performance.now(), finished: false };

/** The Agent Bus solar system. Loaded lazily so a HUD-less page (wallpaper,
 *  dashboard embed) never pays for three.js twice. */
let bus = null;

const $ = (id) => document.getElementById(id);
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const num = (n) => (typeof n === 'number' ? n : parseFloat(n) || 0).toLocaleString('en-US');

/** 1234567 → "1.2M" — HUD panels are narrow and exact digits past a few
 *  thousand are noise; the exact value stays available on hover. */
function compact(n) {
    n = Number(n) || 0;
    if (n >= 1e9) return (n / 1e9).toFixed(1).replace(/\.0$/, '') + 'B';
    if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M';
    if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, '') + 'K';
    return String(n);
}

// ── Boot sequence ────────────────────────────────────────────────

function renderBootSteps() {
    const host = $('hud-boot-steps');
    if (!host) return;
    host.innerHTML = STEPS.map(s =>
        `<div class="hud-boot-step" data-step="${s.id}">
            <span class="mark">·</span><span>${esc(s.label)}</span><span class="ms"></span>
        </div>`).join('');
    const first = host.querySelector('.hud-boot-step');
    if (first) first.classList.add('active');
}

function markStep(id, status = 'ok') {
    if (state.done.has(id)) return;
    state.done.add(id);

    const el = document.querySelector(`.hud-boot-step[data-step="${id}"]`);
    if (el) {
        el.classList.remove('active');
        el.classList.add(status);
        el.querySelector('.mark').textContent = status === 'ok' ? '✓' : '–';
        el.querySelector('.ms').textContent = Math.round(performance.now() - state.started) + 'ms';
    }
    // Light up the next unfinished step so the list reads as a sequence.
    const next = STEPS.find(s => !state.done.has(s.id));
    if (next) document.querySelector(`.hud-boot-step[data-step="${next.id}"]`)?.classList.add('active');

    const fill = $('hud-boot-fill');
    if (fill) fill.style.width = Math.round((state.done.size / STEPS.length) * 100) + '%';

    if (state.done.size >= STEPS.length) finishBoot();
}

function finishBoot() {
    if (state.finished) return;
    state.finished = true;
    // Let the bar visibly reach 100% before the curtain lifts; snapping both at
    // once reads as a glitch rather than a completion.
    setTimeout(() => $('hud-boot')?.classList.add('done'), 260);
}

// ── Panel renderers ──────────────────────────────────────────────

function renderStats(d = {}) {
    const cells = [
        ['Notes', compact(d.notes), `${num(d.notes)} indexed`],
        ['Words', compact(d.words), 'across the vault'],
        ['Links', compact(d.links), `${num(d.wiki ?? 0)} wiki`],
        ['Galaxies', num(d.galaxies ?? 0), 'clusters'],
    ];
    // The dashboard's two KPI tiles, folded in rather than given their own
    // corner — they are the same class of fact as the four above.
    if (d.connections != null) cells.push(['Connections', compact(d.connections), `${num(d.autoLinks ?? 0)} auto`]);
    if (d.expertiseAreas != null) cells.push(['Expertise', `${d.expertiseAreas}/${d.expertiseTotal ?? 24}`, `${d.expertiseStrong ?? 0} above 80%`]);
    setHTML('hud-stats-body', cells.map(([l, v, f]) =>
        `<div><div class="hud-stat-label">${esc(l)}</div>
              <div class="hud-stat-value" title="${esc(f)}">${esc(v)}</div>
              <div class="hud-stat-foot">${esc(f)}</div></div>`).join(''));
    if (d.brainName) setText('hud-brand-name', d.brainName);
    if (d.address) setText('hud-brand-addr', d.address);
    markStep('stats');
}

function renderExpertise(list = []) {
    setHTML('hud-expertise-body', list.slice(0, 6).map(e => {
        const pct = Math.max(0, Math.min(100, Number(e.percent) || 0));
        return `<div class="hud-row">
                  <span class="hud-row-name">
                    <i class="hud-swatch" style="background:${esc(e.color || '#6cf0ff')}"></i>${esc(e.name)}
                  </span>
                  <span class="hud-row-val">${pct.toFixed(1)}%</span>
                  <span class="hud-bar"><i style="width:${pct}%"></i></span>
                </div>`;
    }).join('') || emptyRow('no expertise data'));
    markStep('expertise');
}

function renderActivity(list = []) {
    setHTML('hud-activity-body', list.slice(0, 8).map(a =>
        `<div class="hud-feed-line">
           <span class="hud-feed-time">${esc(a.time || '')}</span>
           <span class="hud-feed-tag">${esc(a.tag || 'MCP')}</span>
           <span class="hud-feed-text" title="${esc(a.text)}">${esc(a.text)}</span>
         </div>`).join('') || emptyRow('waiting for activity'));
    markStep('activity');
}

function renderAgents(d = {}) {
    const list = d.agents || [];
    bus?.setAgents(list);
    // Traffic the host reports since the last poll, replayed as motes falling
    // into the star. Capped so a burst reads as busy rather than as confetti.
    (d.traffic || []).slice(0, 4).forEach((t, i) =>
        setTimeout(() => { bus?.fireTraffic(t.agent, t.inbound !== false); }, i * 160));
    setHTML('hud-agents-body', list.map(a =>
        `<div class="hud-row">
           <span class="hud-row-name">
             <i class="hud-swatch" style="background:${esc(a.color || '#8e9aa6')};
                box-shadow:${a.online ? `0 0 8px ${esc(a.color || '#8e9aa6')}` : 'none'}"></i>${esc(a.name)}
           </span>
           <span class="hud-row-val">${a.online ? esc(a.detail || 'online') : 'offline'}</span>
         </div>`).join('') || emptyRow('no agents connected'));
    setText('hud-agents-count', `${list.filter(a => a.online).length} online`);
    markStep('agents');
}

function renderSystem(d = {}) {
    const rows = [];
    if (d.gpu != null) rows.push(['GPU', `${Math.round(d.gpu)}%`, d.gpu]);
    if (d.cpu != null) rows.push(['CPU', `${Math.round(d.cpu)}%`, d.cpu]);
    // The old SYSTEM HEALTH card, verbatim — these are the lines the owner
    // actually checks when something looks wrong.
    if (d.vault) rows.push(['Vault', d.vault, null]);
    if (d.db) rows.push(['DB', d.db, null]);
    if (d.index) rows.push(['Index', d.index, null]);
    if (d.ai) rows.push(['AI', d.ai, null]);
    if (d.mesh) rows.push(['Mesh', d.mesh, null]);
    if (d.version) rows.push(['Version', d.version, null]);
    dotClass('hud-health-dot', d.healthy === false ? 'warn' : '');

    setHTML('hud-system-body', rows.map(([l, v, bar]) =>
        `<div class="hud-row">
           <span class="hud-row-name">${esc(l)}</span>
           <span class="hud-row-val">${esc(v)}</span>
           ${bar != null ? `<span class="hud-bar"><i style="width:${Math.max(0, Math.min(100, bar))}%"></i></span>` : ''}
         </div>`).join('') || emptyRow('no telemetry'));
    markStep('system');
}

function renderRecent(list = []) {
    setHTML('hud-recent-body', list.slice(0, 5).map(n =>
        `<div class="hud-feed-line">
           <span class="hud-feed-time">${esc(n.when || '')}</span>
           <span class="hud-feed-tag">${esc(n.category || 'NOTE')}</span>
           <span class="hud-feed-text" title="${esc(n.title)}">${esc(n.title)}</span>
         </div>`).join('') || emptyRow('nothing edited yet'));
    setText('hud-recent-count', list.length ? `${num(list.totalCount ?? list.length)} notes` : '—');
}

function renderNetwork(d = {}) {
    const rows = [
        ['Status', d.connected ? 'Connected' : 'Offline'],
        ['Peers', num(d.peers ?? 0)],
    ];
    if (d.address) rows.push(['Node', d.address]);
    setHTML('hud-network-body', rows.map(([l, v]) =>
        `<div class="hud-row"><span class="hud-row-name">${esc(l)}</span>
           <span class="hud-row-val">${esc(v)}</span></div>`).join(''));
    setText('hud-net-state', d.connected ? `${num(d.peers ?? 0)} peer${d.peers === 1 ? '' : 's'}` : 'offline');
    dotClass('hud-net-dot', d.connected ? '' : 'off');
    markStep('network');
}

function renderMcp(d = {}) {
    const delta = Number(d.delta ?? 0);
    setHTML('hud-mcp-body',
        `<div class="hud-headline">
           <span class="n">${num(d.calls ?? 0)}</span><span class="u">calls</span>
           ${delta ? `<span class="d ${delta > 0 ? 'up' : 'down'}">${delta > 0 ? '+' : ''}${num(delta)} vs prev</span>` : ''}
         </div>
         ${sparkline(d.buckets || [])}
         <div class="hud-row"><span class="hud-row-name">top tool</span>
           <span class="hud-row-val">${esc(d.topTool || '—')}${d.topToolCount ? ` · ${num(d.topToolCount)}×` : ''}</span></div>`);
    setText('hud-mcp-window', d.window || '24 h');
}

function renderClaude(d = {}) {
    const rows = (d.meters || []).map(m => {
        const pct = Math.max(0, Math.min(100, Number(m.percent) || 0));
        return `<div class="hud-row">
                  <span class="hud-row-name">${esc(m.name)}</span>
                  <span class="hud-row-val">${m.percent == null ? '—' : pct + '% used'}</span>
                  <span class="hud-bar"><i style="width:${pct}%"></i></span>
                </div>`;
    });
    if (d.tally) rows.push(`<div class="hud-row"><span class="hud-row-name">local</span>
        <span class="hud-row-val">${esc(d.tally)}</span></div>`);
    setHTML('hud-claude-body', rows.join('') || emptyRow('sign in to track'));
    setText('hud-claude-plan', d.plan || '—');
    dotClass('hud-claude-dot', d.signedIn ? '' : 'warn');
    markStep('usage');
}

/** Inline sparkline. SVG rather than canvas: a handful of points, and it
 *  inherits the HUD's CSS glow for free. */
function sparkline(vals) {
    if (!vals.length) return '';
    const w = 100, h = 30, max = Math.max(1, ...vals);
    const pts = vals.map((v, i) =>
        `${(i / Math.max(1, vals.length - 1)) * w},${h - (v / max) * (h - 2) - 1}`).join(' ');
    return `<svg class="hud-spark" viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">
              <defs><linearGradient id="hud-spark-grad" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0" stop-color="#6cf0ff" stop-opacity="0.6"/>
                <stop offset="1" stop-color="#6cf0ff" stop-opacity="0"/>
              </linearGradient></defs>
              <polygon points="0,${h} ${pts} ${w},${h}"/>
              <polyline points="${pts}"/>
            </svg>`;
}

function dotClass(id, cls) {
    const el = $(id);
    if (el) el.className = 'hud-dot' + (cls ? ' ' + cls : '');
}

const emptyRow = (t) => `<div class="hud-row"><span class="hud-row-name" style="opacity:.55">${esc(t)}</span></div>`;

function setHTML(id, html) { const el = $(id); if (el) el.innerHTML = html; }
function setText(id, txt) { const el = $(id); if (el) el.textContent = txt; }

// ── Host channel ─────────────────────────────────────────────────

function post(msg) {
    try { window.chrome?.webview?.postMessage(msg); } catch { /* not hosted */ }
}

function onHudMessage(evt) {
    const m = evt.data;
    if (!m || typeof m !== 'object') return;
    switch (m.type) {
        case 'hudStats':     renderStats(m.payload); break;
        case 'hudExpertise': renderExpertise(m.payload); break;
        case 'hudActivity':  renderActivity(m.payload); break;
        case 'hudAgents':    renderAgents(m.payload); break;
        case 'hudSystem':    renderSystem(m.payload); break;
        case 'hudRecent':    renderRecent(m.payload); break;
        case 'hudNetwork':   renderNetwork(m.payload); break;
        case 'hudMcp':       renderMcp(m.payload); break;
        case 'hudClaude':    renderClaude(m.payload); break;
        // The galaxy payload already flows for the renderer; piggyback on it so
        // the first two boot steps complete without the host doing anything new.
        case 'brain':
            markStep('galaxy');
            if (m.payload?.nodes) {
                renderStats({
                    notes: m.payload.nodes.length,
                    words: m.payload.totalWords,
                    links: m.payload.edges?.length,
                    galaxies: m.payload.galaxies?.length,
                    brainName: m.payload.displayName,
                    address: m.payload.brainAddress,
                });
            }
            break;
    }
}

/** Spin up the solar system, and keep it honest about cost: the loop stops
 *  whenever the canvas is off-screen or the window is hidden, so a HUD left
 *  open behind another view never burns frames. */
async function initBus() {
    const canvas = $('hud-bus-canvas');
    if (!canvas) return;
    try {
        const { createAgentBus3D } = await import('./agentbus3d.js');
        bus = createAgentBus3D(canvas);
        // Demo mode only: a console handle for checking orbits/traffic without
        // a screenshot. Never exposed in the shipped HUD.
        if (HUD_DEMO) window.__hudBus = bus;
    } catch (e) {
        // A missing WebGL context must not take the rest of the HUD with it —
        // the roster below the canvas still carries every fact.
        console.warn('[hud] agent bus 3D unavailable:', e?.message || e);
        canvas.style.display = 'none';
        return;
    }
    new ResizeObserver(() => bus?.resize()).observe(canvas);
    new IntersectionObserver(
        ([e]) => (e.isIntersecting && !document.hidden ? bus?.start() : bus?.stop()),
        { threshold: 0.01 }).observe(canvas);
    document.addEventListener('visibilitychange', () =>
        document.hidden ? bus?.stop() : bus?.start());
}

/** Let the wheel scroll a clipped readout instead of zooming the galaxy.
 *
 * The canvas listens for wheel events on the window, so without this a scroll
 * over a panel both scrolls the panel AND flies the camera — which reads as
 * the HUD fighting the user. Only swallow the event when the panel can
 * actually move in that direction; at the ends the wheel belongs to the
 * galaxy again, so the camera never feels stuck near a panel. */
function wireWheelScroll() {
    document.querySelectorAll('.hud-panel').forEach(panel => {
        panel.addEventListener('wheel', (e) => {
            const canScroll = panel.scrollHeight - panel.clientHeight > 1;
            if (!canScroll) return;
            const atTop = panel.scrollTop <= 0;
            const atEnd = panel.scrollTop + panel.clientHeight >= panel.scrollHeight - 1;
            if ((e.deltaY < 0 && atTop) || (e.deltaY > 0 && atEnd)) return;
            e.stopPropagation();
            e.preventDefault();
            panel.scrollTop += e.deltaY;
        }, { passive: false });
    });
}

/** Mark panels whose content is clipped, so the fade only appears when there
 *  is genuinely more to see — a permanent fade would be decoration that lies. */
function markScrollable() {
    document.querySelectorAll('.hud-panel').forEach(p => {
        p.classList.toggle('is-scrollable', p.scrollHeight - p.clientHeight > 1);
    });
}

function wireActions() {
    document.querySelectorAll('.hud-action').forEach(btn => {
        btn.addEventListener('click', () => post({ type: 'hudAction', action: btn.dataset.action }));
    });
}

// ── Init ─────────────────────────────────────────────────────────

export function initHud() {
    if (!HUD_ON) return;
    document.body.classList.add('hud-active');
    renderBootSteps();
    wireActions();
    wireWheelScroll();
    initBus();
    // Content arrives in stages, so re-evaluate what is clipped as it lands
    // and whenever the window changes shape.
    const ro = new ResizeObserver(markScrollable);
    document.querySelectorAll('.hud-panel').forEach(p => ro.observe(p));
    addEventListener('resize', markScrollable);

    try { window.chrome?.webview?.addEventListener('message', onHudMessage); } catch { /* not hosted */ }

    // The canvas is up as soon as this module runs; the galaxy step completes
    // for real when the brain payload lands, but mark a floor here so the bar
    // moves immediately and the window never looks dead.
    setTimeout(() => markStep('galaxy'), 400);

    // Safety net: never let a missing section strand the boot screen.
    setTimeout(() => {
        STEPS.forEach(s => { if (!state.done.has(s.id)) markStep(s.id, 'skip'); });
    }, BOOT_DEADLINE_MS);

    post({ type: 'hudReady' });
    if (HUD_DEMO) runDemo();
}

/** Representative sample data — shapes and magnitudes match the live vault so
 *  the layout is judged against real content, not lorem ipsum. */
function runDemo() {
    const step = (ms, fn) => setTimeout(fn, ms);
    step(300, () => renderStats({
        notes: 1204, words: 1523809, links: 8087, wiki: 969, galaxies: 24,
        connections: 8087, autoLinks: 2704, expertiseAreas: 16, expertiseTotal: 24, expertiseStrong: 4,
        brainName: "xman's Brain", address: '0xBRAIN-e10c-6760-f9cc-707a',
    }));
    step(1100, () => renderNetwork({ connected: true, peers: 1, address: '0xBRAIN-e10c…' }));
    step(1800, () => renderRecent([
        { when: '2 min',  category: 'Programming', title: 'CluadeX froze for 11 minutes — event under a lock' },
        { when: '18 min', category: 'Notes',       title: 'Semantic search was never running' },
        { when: '1 h',    category: 'Notes',       title: 'Brain-first coverage audit — every connected agent' },
        { when: '3 h',    category: 'Programming', title: 'BrainX Agent Bus v2.9 — Claude ⇄ Codex' },
        { when: '5 h',    category: 'Design_Art',  title: 'Dashboard floating overlays — NETWORK card' },
    ]));
    step(3100, () => renderMcp({
        calls: 415, delta: 27, window: '24 h', topTool: 'brain_search', topToolCount: 237,
        buckets: [3, 8, 5, 12, 22, 14, 9, 31, 18, 7, 11, 26, 42, 19, 8, 5, 13, 29, 37, 21, 9, 6, 15, 24],
    }));
    step(3500, () => renderClaude({
        plan: 'Max (5x)', signedIn: true, tally: '251.7M tokens · 5 h · 701 msg',
        meters: [
            { name: 'Current session', percent: 53 },
            { name: 'Weekly · all models', percent: 82 },
            { name: 'Model only', percent: null },
            { name: 'Usage credits', percent: 0 },
        ],
    }));
    step(900, () => renderExpertise([
        { name: 'Programming',      percent: 35.6, color: '#6cf0ff' },
        { name: 'AI / Machine Learning', percent: 18.1, color: '#a68bff' },
        { name: 'Design / Art',     percent: 10.9, color: '#ff6ec7' },
        { name: 'DataScience',      percent: 10.7, color: '#5ce1a0' },
        { name: 'Blockchain / Web3', percent: 6.9, color: '#ffb86c' },
        { name: 'Business / Finance', percent: 5.8, color: '#ffd86c' },
    ]));
    step(1500, () => renderActivity([
        { time: '21:57:24', tag: 'MCP',   text: 'ssh_ok "cd /home/admin/domains/netwix.online"' },
        { time: '21:57:41', tag: 'MCP',   text: 'search "publish client Velopack release"' },
        { time: '21:58:02', tag: 'NOTE',  text: 'BrainX Agent Bus v2.9 — Claude ⇄ Codex' },
        { time: '21:58:19', tag: 'BUS',   text: 'claude → codex · review-authcontroller' },
        { time: '21:58:44', tag: 'MCP',   text: 'brain_semantic_search "dashboard layout"' },
        { time: '21:59:03', tag: 'NOTE',  text: 'Semantic search was never running' },
    ]));
    step(2100, () => renderAgents({ agents: [
        { name: 'claude',  online: true,  color: '#e8825a', detail: 'brain_search' },
        { name: 'codex',   online: true,  color: '#19a385', detail: 'agent_peers' },
        { name: 'cluadex', online: true,  color: '#8b7cf6', detail: 'idle' },
        { name: 'local-agent', online: false, everSeen: true, color: '#8e9aa6' },
    ] }));
    // Keep the system busy so the orbit + traffic animation can be judged.
    setInterval(() => {
        const who = ['claude', 'codex', 'cluadex'][Math.floor(Math.random() * 3)];
        bus?.fireTraffic(who, true);
        setTimeout(() => bus?.fireTraffic(who, false), 700);
    }, 1800);
    step(2700, () => renderSystem({
        gpu: 48, cpu: 33, healthy: true,
        vault: 'G:\\Obsidian · 1.2 MB', db: 'SQLite · 1.5 GB',
        index: '1,204 nodes · 8,087 links', ai: 'Ollama · bge-m3',
        mesh: '1 peer · :5142', version: 'v2.0.166 · mcp 2.9.171',
    }));
}

if (HUD_ON) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initHud);
    else initHud();
}
