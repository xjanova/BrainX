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
    { id: 'system',    label: 'Polling system' },
];

/* A section that never arrives must not hold the boot screen hostage. After
 * this long we mark the stragglers "skipped" and show the HUD anyway — a HUD
 * missing one panel beats a black screen with a stuck progress bar. */
const BOOT_DEADLINE_MS = 9000;

const state = { done: new Set(), started: performance.now(), finished: false };

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
    if (d.mcpCalls != null) rows.push(['MCP 24h', num(d.mcpCalls), null]);
    if (d.mesh) rows.push(['Mesh', esc(d.mesh), null]);
    if (d.version) rows.push(['Version', esc(d.version), null]);

    setHTML('hud-system-body', rows.map(([l, v, bar]) =>
        `<div class="hud-row">
           <span class="hud-row-name">${esc(l)}</span>
           <span class="hud-row-val">${esc(v)}</span>
           ${bar != null ? `<span class="hud-bar"><i style="width:${Math.max(0, Math.min(100, bar))}%"></i></span>` : ''}
         </div>`).join('') || emptyRow('no telemetry'));
    markStep('system');
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
        brainName: "xman's Brain", address: '0xBRAIN-e10c-6760-f9cc-707a',
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
        { name: 'Claude',  online: true,  color: '#e8825a', detail: 'brain_search' },
        { name: 'Codex',   online: true,  color: '#19a385', detail: 'agent_peers' },
        { name: 'CluadeX', online: true,  color: '#8b7cf6', detail: 'idle' },
        { name: 'Local agent', online: false, color: '#8e9aa6' },
    ] }));
    step(2700, () => renderSystem({
        gpu: 48, cpu: 33, mcpCalls: 415, mesh: '1 peer', version: 'v2.0.166 · mcp 2.9.171',
    }));
}

if (HUD_ON) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initHud);
    else initHud();
}
