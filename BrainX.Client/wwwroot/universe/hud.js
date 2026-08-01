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
 * missing one panel beats a black screen with a stuck progress bar.
 *
 * The clock RESTARTS whenever the host says it is still working (hudBootBusy),
 * because the host now loads the page BEFORE it finishes reading the vault:
 * on a big brain that read is longer than this deadline, and expiring mid-read
 * would lift the curtain on a HUD full of zeroes. */
const BOOT_DEADLINE_MS = 9000;

const state = { done: new Set(), started: performance.now(), finished: false };
let bootDeadline = null;

/* The host's own work, as a row in the same list.
 *
 * Reading the vault is ~15 of the ~18 seconds of a cold start, and it was the
 * one part of the boot with no line of its own: six sections cannot even begin
 * until it lands, so the bar sat at 2/8 for fifteen seconds and then jumped to
 * 8/8. Counting it makes the bar describe the wait instead of hiding it.
 * Added only when the host says it is busy — a fast start never sees it. */
const hostStep = { id: 'host', label: 'Reading the vault', added: false };
const totalSteps = () => STEPS.length + (hostStep.added ? 1 : 0);

function armBootDeadline() {
    clearTimeout(bootDeadline);
    bootDeadline = setTimeout(() => {
        STEPS.forEach(s => { if (!state.done.has(s.id)) markStep(s.id, 'skip'); });
        if (hostStep.added) markStep(hostStep.id, 'skip');
    }, BOOT_DEADLINE_MS);
}

/** Host heartbeat during a long read: keep the curtain, say why, and put the
 *  wait on the board. `done` arrives once when the host is finally ready. */
function noteHostBusy(label, done) {
    if (state.finished) return;
    if (label) setText('hud-boot-busy', label);

    if (!hostStep.added) {
        hostStep.added = true;
        if (label) hostStep.label = label.replace(/[.…]+$/, '');
        const host = $('hud-boot-steps');
        if (host) {
            const row = document.createElement('div');
            row.className = 'hud-boot-step pending';
            row.dataset.step = hostStep.id;
            row.innerHTML = `<span class="mark">·</span><span>${esc(hostStep.label)}</span><span class="ms"></span>`;
            host.insertBefore(row, firstPendingRow(host));
        }
        updateBootProgress();
    }

    if (done) markStep(hostStep.id);
    armBootDeadline();
}

/** Where the pending block starts — i.e. just after the settled ones.
 *  `ignore` is the row being moved: by the time markStep calls this it is
 *  already marked done, so counting it would make it its own anchor and the
 *  move would be a no-op. */
function firstPendingRow(host, ignore) {
    const settled = [...host.querySelectorAll('.hud-boot-step.ok, .hud-boot-step.skip')]
        .filter(n => n !== ignore);
    const last = settled[settled.length - 1];
    return last ? last.nextSibling : host.firstChild;
}

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

/** Every section starts at once, so every row starts in flight.
 *
 *  The old list lit ONE row at a time and moved the light down in order, which
 *  described a queue. There is no queue: the host pushes these on a tiered
 *  timer and they land in whatever order they finish — the agent roster is
 *  usually first and the Claude meters last, no matter where they sit here. */
function renderBootSteps() {
    const host = $('hud-boot-steps');
    if (!host) return;
    host.innerHTML = STEPS.map(s =>
        `<div class="hud-boot-step pending" data-step="${s.id}">
            <span class="mark">·</span><span>${esc(s.label)}</span><span class="ms"></span>
        </div>`).join('');
    updateBootProgress();
}

/** Bar and count come from the same number, so they can never disagree. */
function updateBootProgress() {
    const total = totalSteps();
    const fill = $('hud-boot-fill');
    if (fill) fill.style.width = Math.round((state.done.size / total) * 100) + '%';
    setText('hud-boot-count', `${state.done.size}/${total}`);
}

function markStep(id, status = 'ok') {
    if (state.done.has(id)) return;
    state.done.add(id);

    // Relay to the host: the WPF splash covers the whole boot now, and these
    // are the second half of the sequence it shows. Harmless when unhosted.
    post({
        type: 'hudBootStep', id,
        label: STEPS.find(s => s.id === id)?.label || (id === hostStep.id ? hostStep.label : id),
        done: state.done.size, total: totalSteps(),
        skipped: status !== 'ok',
    });

    const host = $('hud-boot-steps');
    const el = host?.querySelector(`.hud-boot-step[data-step="${id}"]`);
    if (el && host) {
        el.classList.remove('pending');
        el.classList.add(status === 'ok' ? 'ok' : 'skip', 'just-landed');
        el.querySelector('.mark').textContent = status === 'ok' ? '✓' : '–';
        el.querySelector('.ms').textContent = Math.round(performance.now() - state.started) + 'ms';

        // Finished sections rise into a block at the top, in the order they
        // ACTUALLY finished; whatever is still working stays below. The list
        // then reads as what it is — several things loading at once, settling
        // as they land — instead of a queue that appears to stall on whichever
        // row happens to be next in a fixed order.
        host.insertBefore(el, firstPendingRow(host, el));
    }

    updateBootProgress();

    if (state.done.size >= totalSteps()) finishBoot();
}

function finishBoot() {
    if (state.finished) return;
    state.finished = true;
    post({ type: 'hudBootDone' });      // releases the splash covering all of this
    // Let the bar visibly reach 100% before the curtain lifts; snapping both at
    // once reads as a glitch rather than a completion.
    setTimeout(() => $('hud-boot')?.classList.add('done'), 260);
}

// ── Panel renderers ──────────────────────────────────────────────

function renderStats(d = {}) {
    const cells = [
        ['Notes', compact(d.notes), `${num(d.notes)} indexed`],
        ['Words', compact(d.words), 'across the vault'],
        // The dashboard called this CONNECTIONS in one card and Wiki-links in
        // another, both showing the same total. One cell, and the split that
        // the two labels were reaching for: what was written vs what was
        // inferred by the auto-linker.
        ['Links', compact(d.links), d.auto != null
            ? `${num(d.wiki ?? 0)} wiki · ${num(d.auto)} auto`
            : `${num(d.wiki ?? 0)} wiki`],
        ['Galaxies', num(d.galaxies ?? 0), 'clusters'],
    ];
    // The dashboard's mastery KPI, folded in rather than given its own corner
    // — it is the same class of fact as the counts beside it.
    if (d.expertiseAreas != null) {
        cells.push(['Mastery', `${d.expertiseStrong ?? 0}/${d.expertiseAreas}`,
            `areas above 80% of ${d.expertiseTotal ?? 24}`]);
    }
    setHTML('hud-stats-body', cells.map(([l, v, f]) =>
        `<div><div class="hud-stat-label">${esc(l)}</div>
              <div class="hud-stat-value" title="${esc(f)}">${esc(v)}</div>
              <div class="hud-stat-foot">${esc(f)}</div></div>`).join(''));
    if (d.brainName) setText('hud-brand-name', d.brainName);
    if (d.address) setText('hud-brand-addr', d.address);
    markStep('stats');
}

/** Every category, not a top-six. The settings row that used to reveal the
 *  full legend is gone, so this panel IS the complete picture — it scrolls
 *  under the wheel and fades at the tail when there is more below. */
function renderExpertise(list = []) {
    setHTML('hud-expertise-body', list.map(e => {
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

/** Highest activity id already on screen. The host stamps a monotonic id per
 *  event, which is the only reliable identity here — the log genuinely
 *  contains repeated lines with the same second AND the same text, so a
 *  content hash would silently swallow real events. */
let _feedSeen = 0;
/** Lines kept in the DOM. Deep enough to scroll back through, bounded so a
 *  HUD left open for a day does not accumulate a thousand nodes. */
const FEED_KEEP = 60;

const feedLine = (a) =>
    `<div class="hud-feed-line">
       <span class="hud-feed-time">${esc(a.time || '')}</span>
       <span class="hud-feed-tag">${esc(a.tag || 'MCP')}</span>
       <span class="hud-feed-text" title="${esc(a.text)}">${esc(a.text)}</span>
     </div>`;

/** The feed GROWS; it is not redrawn.
 *
 *  The host resends its newest N every couple of seconds, so rewriting the
 *  list would replay every line's entrance animation on every poll and throw
 *  away wherever the user had scrolled to. Only genuinely new events are
 *  prepended, so only they animate — and if the user is reading history, the
 *  scroll position is corrected by exactly the height that arrived above it,
 *  which keeps the text they are looking at under their eyes. */
function renderActivity(list = []) {
    const host = $('hud-activity-body');
    if (!host) { markStep('activity'); return; }

    const fresh = list.filter(a => Number(a.id ?? 0) > _feedSeen);
    if (!fresh.length) {
        if (!host.childElementCount) host.innerHTML = emptyRow('waiting for activity');
        markStep('activity');
        return;
    }
    _feedSeen = Math.max(_feedSeen, ...list.map(a => Number(a.id ?? 0)));

    // Drop the placeholder before the first real line lands on top of it.
    if (host.querySelector('.hud-row')) host.innerHTML = '';

    const anchored = host.scrollTop > 2;
    const heightBefore = host.scrollHeight;

    const frag = document.createRange().createContextualFragment(
        fresh.map(feedLine).join(''));      // fresh is newest-first, so is the DOM
    host.insertBefore(frag, host.firstChild);

    while (host.childElementCount > FEED_KEEP) host.lastElementChild.remove();
    if (anchored) host.scrollTop += host.scrollHeight - heightBefore;

    markScrollable();
    markStep('activity');
}

function renderAgents(d = {}) {
    const list = d.agents || [];
    bus?.setAgents(list);
    // Traffic the host reports since the last poll, replayed as motes falling
    // into the star. Capped so a burst reads as busy rather than as confetti.
    (d.traffic || []).slice(0, 4).forEach((t, i) =>
        setTimeout(() => { bus?.fireTraffic(t.agent, t.inbound !== false); }, i * 160));
    // No text roster under the map. It repeated, in words, what the orbits
    // already say in colour and motion — and the name is one wheel-scroll away
    // on the planet itself. The count stays in the panel title, because
    // "how many are up" is the one thing the picture doesn't state outright.
    setText('hud-agents-count', `${list.filter(a => a.online).length} online`);
    markStep('agents');
}

/** Load history, so the panel can show the SHAPE of the last two minutes and
 *  not just this instant — a 90 % spike that has already passed is the thing
 *  worth seeing, and a bare percentage never shows it. The host samples every
 *  2 s, so 60 points ≈ 2 minutes. */
const LOAD_KEEP = 60;
const _load = { gpu: [], cpu: [], temp: [], ram: [], vram: [] };
function pushLoad(series, v) {
    if (v == null) return;
    series.push(Math.max(0, Math.min(100, Number(v) || 0)));
    if (series.length > LOAD_KEEP) series.shift();
}

function renderSystem(d = {}) {
    pushLoad(_load.gpu, d.gpu);
    pushLoad(_load.cpu, d.cpu);
    pushLoad(_load.temp, d.gpuTemp);
    pushLoad(_load.ram, d.ram);
    pushLoad(_load.vram, d.vram);

    // Every live metric is a percentage of a real ceiling, so each one gets a
    // real instrument: segmented bargraph, redline, and the history trace
    // underneath. `redline` is where the reading stops being comfortable —
    // 85 °C for a GPU, 90% for memory you can actually exhaust, none for load
    // that is supposed to sit at 100% while work happens.
    const gauges = [];
    const G = (label, value, text, color, redline, spark) => {
        if (value == null) return;
        const pct = Math.max(0, Math.min(100, Number(value) || 0));
        const hot = redline != null && pct >= redline;
        // History sits BEHIND the readout, not under it — one instrument per
        // metric rather than a readout and a chart competing for the same
        // glance. It also buys back the vertical space five stacked graphs
        // were spending.
        gauges.push(
            `<div class="hud-gauge${hot ? ' is-hot' : ''}" style="--g:${color}">
               <span class="hud-gauge-bg">${spark}</span>
               <div class="hud-gauge-head">
                 <span class="hud-gauge-label">${esc(label)}</span>
                 <span class="hud-gauge-val">${text}</span>
               </div>
               <div class="hud-gauge-track">
                 <i class="hud-gauge-fill" style="width:${pct}%"></i>
                 ${redline != null ? `<b class="hud-gauge-redline" style="left:${redline}%"></b>` : ''}
               </div>
             </div>`);
    };

    G('GPU', d.gpu, `${Math.round(d.gpu ?? 0)}<small>%</small>`, '#6cf0ff', null,
        sparkline(_load.gpu, { id: 'spark-gpu', color: '#6cf0ff', max: 100, height: 14 }));
    G('CPU', d.cpu, `${Math.round(d.cpu ?? 0)}<small>%</small>`, '#a68bff', null,
        sparkline(_load.cpu, { id: 'spark-cpu', color: '#a68bff', max: 100, height: 14 }));
    // Temperature is the one that says whether the load above is SUSTAINABLE.
    // Drawn against a fixed 100 °C ceiling so the bar means the same thing
    // every time you glance at it — autoscaling would make 45 °C idle look
    // identical to 85 °C under load.
    G('GPU temp', d.gpuTemp, `${Math.round(d.gpuTemp ?? 0)}<small>°C</small>`, '#ff9f4a', 85,
        sparkline(_load.temp, { id: 'spark-temp', color: '#ff9f4a', max: 100, height: 14 }));
    // VRAM before RAM: on this box the card is 8 GB and it is what runs out
    // first when a local model loads.
    G('VRAM', d.vram, esc(d.vramLabel || `${Math.round(d.vram ?? 0)}%`), '#5ce0a0', 90,
        sparkline(_load.vram, { id: 'spark-vram', color: '#5ce0a0', max: 100, height: 14 }));
    G('RAM', d.ram, esc(d.ramLabel || `${Math.round(d.ram ?? 0)}%`), '#f07de0', 90,
        sparkline(_load.ram, { id: 'spark-ram', color: '#f07de0', max: 100, height: 14 }));

    // The old SYSTEM HEALTH card, verbatim — these are the lines the owner
    // actually checks when something looks wrong. Text, not instruments:
    // they have no ceiling to read against.
    const rows = [];
    if (d.vault) rows.push(['Vault', d.vault]);
    if (d.db) rows.push(['DB', d.db]);
    if (d.index) rows.push(['Index', d.index]);
    if (d.ai) rows.push(['AI', d.ai]);
    if (d.mesh) rows.push(['Mesh', d.mesh]);
    dotClass('hud-health-dot', d.healthy === false ? 'warn' : '');

    const rowHtml = rows.map(([l, v]) =>
        `<div class="hud-row">
           <span class="hud-row-name">${esc(l)}</span>
           <span class="hud-row-val">${esc(v)}</span>
         </div>`).join('');

    setHTML('hud-system-body', (gauges.join('') + rowHtml) || emptyRow('no telemetry'));
    markStep('system');
}

/** Accepts either a bare array or {items, totalCount}. The host sends the
 *  latter because the panel's heading is the size of the WHOLE vault, which
 *  five rows can't tell you. */
function renderRecent(d = []) {
    const list = Array.isArray(d) ? d : (d.items || []);
    const total = Array.isArray(d) ? d.length : (d.totalCount ?? list.length);
    setHTML('hud-recent-body', list.slice(0, 5).map(n =>
        `<div class="hud-feed-line">
           <span class="hud-feed-time">${esc(n.when || '')}</span>
           <span class="hud-feed-tag">${esc(n.category || 'NOTE')}</span>
           <span class="hud-feed-text" title="${esc(n.title)}">${esc(n.title)}</span>
         </div>`).join('') || emptyRow('nothing edited yet'));
    setText('hud-recent-count', list.length ? `${num(total)} notes` : '—');
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
         ${sparkline(d.buckets || [], { id: 'spark-mcp', height: 24 })}
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
 *  inherits the HUD's CSS glow for free.
 *
 *  `max` is explicit for percentage series — auto-scaling a load graph makes
 *  an idle 3 % CPU draw the same mountain as a pegged one, which is a chart
 *  that lies. Counts (MCP calls) still auto-scale, because there is no
 *  meaningful ceiling to draw them against. The gradient id is per-instance:
 *  several sparklines share this page and duplicate ids make the fill of one
 *  silently depend on another. */
function sparkline(vals, opts = {}) {
    if (!vals.length) return '';
    const color = opts.color || '#6cf0ff';
    const gid = opts.id || 'hud-spark-grad';
    const h = opts.height ?? 30;
    const w = 100;
    const max = opts.max ?? Math.max(1, ...vals);
    const pts = vals.map((v, i) =>
        `${(i / Math.max(1, vals.length - 1)) * w},${h - (Math.min(v, max) / max) * (h - 2) - 1}`).join(' ');
    return `<svg class="hud-spark" style="--spark:${esc(color)};height:${h}px"
                 viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">
              <defs><linearGradient id="${esc(gid)}" x1="0" y1="0" x2="0" y2="1">
                <stop offset="0" stop-color="${esc(color)}" stop-opacity="0.6"/>
                <stop offset="1" stop-color="${esc(color)}" stop-opacity="0"/>
              </linearGradient></defs>
              <polygon fill="url(#${esc(gid)})" points="0,${h} ${pts} ${w},${h}"/>
              <polyline points="${pts}"/>
            </svg>`;
}

/** The one thing on this HUD allowed to interrupt.
 *
 *  An empty payload clears it — the host sends that as soon as the condition
 *  is gone, so a notice can never outlive the problem it describes. Dismissing
 *  hides it until the host raises a DIFFERENT one: the user saying "not now"
 *  should not mean "never tell me again" for a fact that is still true, but it
 *  also should not re-open every poll. */
let _noticeDismissed = '';
function renderNotice(d = {}) {
    const bar = $('hud-notice');
    if (!bar) return;
    const text = d.text || '';
    if (!text) { hideNotice(); _noticeDismissed = ''; return; }
    if (_noticeDismissed === text) { hideNotice(); return; }

    setText('hud-notice-text', text);
    setText('hud-notice-detail', d.detail || '');
    const btn = $('hud-notice-btn');
    if (btn) {
        btn.hidden = !d.action;
        btn.textContent = d.actionLabel || 'Fix';
        btn.dataset.action = d.action || '';
    }
    // Second action, for the notices that are counting down to something:
    // the thing is going to happen, and "Cancel" has to be as reachable as
    // "do it now".
    const alt = $('hud-notice-alt');
    if (alt) {
        alt.hidden = !d.alt;
        alt.textContent = d.altLabel || 'Cancel';
        alt.dataset.action = d.alt || '';
    }
    bar.hidden = false;
    // The readouts move DOWN for it rather than being covered by it. A bar
    // that hides the identity block and the action buttons — including the
    // ones it is telling you to use — is worse than the problem it reports.
    // Measured, because the text can wrap on a narrow window.
    document.body.classList.add('has-notice');
    document.documentElement.style.setProperty('--hud-notice-h', bar.offsetHeight + 'px');
    markScrollable();
}

function hideNotice() {
    const bar = $('hud-notice');
    if (bar) bar.hidden = true;
    document.body.classList.remove('has-notice');
    markScrollable();
}

function wireNotice() {
    ['hud-notice-btn', 'hud-notice-alt'].forEach(id => $(id)?.addEventListener('click', (e) => {
        const action = e.currentTarget.dataset.action;
        if (action) post({ type: 'hudAction', action });
    }));
    $('hud-notice-close')?.addEventListener('click', () => {
        _noticeDismissed = $('hud-notice-text')?.textContent || '';
        hideNotice();
    });
}

function dotClass(id, cls) {
    const el = $(id);
    if (el) el.className = 'hud-dot' + (cls ? ' ' + cls : '');
}

const emptyRow = (t) => `<div class="hud-row"><span class="hud-row-name" style="opacity:.55">${esc(t)}</span></div>`;

/** Write only when the markup actually changed.
 *
 *  The host re-sends every panel on a timer, so an unguarded innerHTML would
 *  rebuild identical DOM every couple of seconds — and rebuilt nodes replay
 *  their entrance animation, which turned the activity feed into a permanent
 *  twitch. It also throws away the meters' width transitions and any text the
 *  user was mid-selection on. */
const _lastHTML = new Map();
function setHTML(id, html) {
    const el = $(id);
    if (!el || _lastHTML.get(id) === html) return;
    _lastHTML.set(id, html);
    el.innerHTML = html;
}
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
        // The host is still reading the vault. Show what it is doing and push
        // the "a section never arrived" deadline out — that deadline exists to
        // rescue a HUD whose host went quiet, not to overrule a host that is
        // telling us, right now, that it is still working.
        case 'hudBootBusy':  noteHostBusy(m.payload?.label, m.payload?.done); break;
        case 'hudNotice':    renderNotice(m.payload); break;
        // The galaxy payload already flows for the renderer; piggyback on it so
        // the first two boot steps complete without waiting on the host's own
        // hudStats. brain-export.json is PascalCase (the C# model serialised
        // as-is) — read both casings rather than assuming, because guessing
        // wrong here shows the user a confident, wrong "0 notes".
        case 'brain': {
            markStep('galaxy');
            const b = m.payload;
            if (b && (b.TotalNotes != null || b.totalNotes != null || b.Nodes || b.nodes)) {
                const pick = (...keys) => keys.map(k => b[k]).find(v => v != null);
                renderStats({
                    notes: pick('TotalNotes', 'totalNotes') ?? (b.Nodes ?? b.nodes ?? []).length,
                    words: pick('TotalWords', 'totalWords'),
                    links: pick('TotalEdges', 'totalEdges'),
                    galaxies: (pick('Expertise', 'expertise') ?? []).length,
                    brainName: pick('DisplayName', 'displayName'),
                    address: pick('BrainAddress', 'brainAddress'),
                });
            }
            break;
        }
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
            // The nearest scrollable box UNDER THE CURSOR, not the panel:
            // the activity feed is its own scroller inside .hud-tr so that
            // "recently edited" below it stays on screen while the user reads
            // back through history. Handling only the panel would scroll the
            // wrong box — and preventDefault on the way past would stop the
            // inner one scrolling natively too.
            let box = e.target;
            while (box && box !== panel.parentNode) {
                if (box.scrollHeight - box.clientHeight > 1 &&
                    getComputedStyle(box).overflowY !== 'visible') break;
                box = box.parentElement;
            }
            if (!box || box === panel.parentNode) return;
            const atTop = box.scrollTop <= 0;
            const atEnd = box.scrollTop + box.clientHeight >= box.scrollHeight - 1;
            // At either end the wheel belongs to the galaxy again, so the
            // camera never feels stuck near a readout.
            if ((e.deltaY < 0 && atTop) || (e.deltaY > 0 && atEnd)) return;
            e.stopPropagation();
            e.preventDefault();
            box.scrollTop += e.deltaY;
        }, { passive: false });
    });
}

/** Mark panels whose content is clipped, so the fade only appears when there
 *  is genuinely more to see — a permanent fade would be decoration that lies. */
function markScrollable() {
    document.querySelectorAll('.hud-panel, .hud-feed').forEach(p => {
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
    wireNotice();
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

    // Safety net: never let a missing section strand the boot screen. Rearmed
    // by every hudBootBusy heartbeat, so a slow host extends it and a silent
    // host does not.
    armBootDeadline();

    post({ type: 'hudReady' });
    if (HUD_DEMO) runDemo();
}

/** Representative sample data — shapes and magnitudes match the live vault so
 *  the layout is judged against real content, not lorem ipsum. */
function runDemo() {
    const step = (ms, fn) => setTimeout(fn, ms);
    step(300, () => renderStats({
        notes: 1204, words: 1523809, links: 8087, wiki: 5383, auto: 2704, galaxies: 24,
        expertiseAreas: 16, expertiseTotal: 25, expertiseStrong: 4,
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
    // All sixteen the live vault actually has, not a top-six: this panel is
    // the complete expertise picture now and the demo has to exercise the
    // scroll that makes that possible.
    step(900, () => renderExpertise([
        { name: 'Programming',      percent: 35.6, color: '#00f0ff' },
        { name: 'AI MachineLearning', percent: 18.1, color: '#8b5cf6' },
        { name: 'Design Art',       percent: 10.9, color: '#ff006e' },
        { name: 'DataScience',      percent: 10.7, color: '#4ecdc4' },
        { name: 'Blockchain Web3',  percent: 6.9, color: '#ffb800' },
        { name: 'Business Finance', percent: 5.8, color: '#ffd86c' },
        { name: 'Engineering',      percent: 4.2, color: '#00ff88' },
        { name: 'Web Development',  percent: 3.1, color: '#6cf0ff' },
        { name: 'Security Crypto',  percent: 1.6, color: '#ff6b6b' },
        { name: 'Health Medicine',  percent: 1.2, color: '#a8e6cf' },
        { name: 'DevOps Cloud',     percent: 0.9, color: '#4ecdc4' },
        { name: 'Science',          percent: 0.7, color: '#00ff88' },
        { name: 'GameDev',          percent: 0.4, color: '#ffb86c' },
        { name: 'Mathematics',      percent: 0.2, color: '#a68bff' },
        { name: 'Philosophy',       percent: 0.1, color: '#ff6ec7' },
        { name: 'Other',            percent: 0.1, color: '#8e9aa6' },
    ]));
    // Ids matter: the feed only accepts events it has not seen, so demo data
    // without them would be silently ignored — exactly as it would be if the
    // host ever forgot to stamp them.
    step(1500, () => renderActivity([
        { id: 6, time: '21:59:03', tag: 'NOTE',  text: 'Semantic search was never running' },
        { id: 5, time: '21:58:44', tag: 'MCP',   text: 'brain_semantic_search "dashboard layout"' },
        { id: 4, time: '21:58:19', tag: 'BUS',   text: 'claude → codex · review-authcontroller' },
        { id: 3, time: '21:58:02', tag: 'NOTE',  text: 'BrainX Agent Bus v2.9 — Claude ⇄ Codex' },
        { id: 2, time: '21:57:41', tag: 'MCP',   text: 'search "publish client Velopack release"' },
        { id: 1, time: '21:57:24', tag: 'MCP',   text: 'ssh_ok "cd /home/admin/domains/netwix.online"' },
    ]));
    // A trickle of new events, so the prepend + scroll-anchor behaviour can be
    // watched rather than reasoned about.
    let demoId = 6;
    setInterval(() => {
        demoId++;
        renderActivity([{ id: demoId, time: new Date().toTimeString().slice(0, 8), tag: 'MCP',
                          text: `brain_search "live feed sample ${demoId}"` }]);
    }, 2600);
    // Mirrors the host allowlist (BusWellKnownAgents in MainWindow.AgentBus.cs):
    // exactly these five, never a stray. unreal stands in for a bridge that has
    // connected before, unity for one that never has.
    step(2100, () => renderAgents({ agents: [
        { name: 'claude',  online: true,  color: '#e8825a', detail: 'brain_search' },
        { name: 'codex',   online: true,  color: '#19a385', detail: 'agent_peers' },
        { name: 'cluadex', online: true,  color: '#8b7cf6', detail: 'idle' },
        { name: 'unreal',  online: false, everSeen: true,  color: '#4fb3e8' },
        { name: 'unity',   online: false, everSeen: false, color: '#c9cfd6' },
    ] }));
    // Keep the system busy so the orbit + traffic animation can be judged.
    setInterval(() => {
        const who = ['claude', 'codex', 'cluadex'][Math.floor(Math.random() * 3)];
        bus?.fireTraffic(who, true);
        setTimeout(() => bus?.fireTraffic(who, false), 700);
    }, 1800);
    const demoSystem = (gpu, cpu) => renderSystem({
        gpu, cpu, healthy: true,
        // Temp trails load rather than tracking it instantly — a card does not
        // cool down the moment the work stops, and the demo should not imply
        // it does.
        gpuTemp: 44 + gpu * 0.42,
        vram: 38 + gpu * 0.25, vramLabel: `${(3.1 + gpu * 0.02).toFixed(1)}/8.0 GB`,
        ram: 52 + cpu * 0.2, ramLabel: `${(16.6 + cpu * 0.06).toFixed(1)}/32.0 GB`,
        vault: 'G:\\Obsidian · 1.2 MB', db: 'SQLite · 1.5 GB',
        index: '1,222 nodes · 8,263 links', ai: 'Ollama · bge-m3',
        mesh: '1 peer · :5142',
    });
    step(2700, () => {
        // Seed a plausible two minutes of history so the load graphs can be
        // judged as graphs. A single sample only ever draws a flat line.
        [12, 18, 44, 61, 38, 22, 27, 55, 78, 64, 41, 30, 26, 35, 52, 48]
            .forEach((g, i) => demoSystem(g, 12 + (i * 7) % 46));
        setInterval(() => demoSystem(20 + Math.random() * 70, 8 + Math.random() * 50), 2000);
    });
}

if (HUD_ON) {
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initHud);
    else initHud();
}
