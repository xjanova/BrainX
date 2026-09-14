// BrainX Universe — entry point.
//
// Wires the WebView2 host bridge (C# ↔ JS) to the three.js scene module.
// All DOM panels (info card, legend, status) live here; scene.js stays
// agnostic of the surrounding chrome.

import { createScene } from './scene.js';
import { publishTheme } from './theme.js';
import { WALLPAPER_CARDS, normalizeCards, setWallpaperCards, onWallpaperCardsChange } from './cards.js';

const $status   = document.getElementById('status');
const $stats    = document.getElementById('stats');
const $size     = document.getElementById('size');
const $canvas   = document.getElementById('universe-canvas');
const $legend   = document.getElementById('legend');
const $legendRows = document.getElementById('legend-rows');
const $info     = document.getElementById('info-card');
const $infoCat  = document.getElementById('info-category');
const $infoTitle = document.getElementById('info-title');
const $infoMeta = document.getElementById('info-meta');
const $infoPrev = document.getElementById('info-preview');
const $infoTags = document.getElementById('info-tags');
const $infoEdit   = document.getElementById('info-edit');
const $infoWalk   = document.getElementById('info-walk');
const $infoSave   = document.getElementById('info-save');
const $infoCancel = document.getElementById('info-cancel');
const $infoOpen   = document.getElementById('info-open');
const $infoClose  = document.getElementById('info-close');
const $infoFade   = document.getElementById('info-fade-bar');
const $infoEditor = document.getElementById('info-editor');
const $infoStatus = document.getElementById('info-status');
const $settingsToggle = document.getElementById('settings-toggle');
const $settingsPanel  = document.getElementById('settings-panel');
const $setGlow   = document.getElementById('set-glow');
const $setStars  = document.getElementById('set-stars');
const $setSky    = document.getElementById('set-sky');
const $setSize   = document.getElementById('set-size');
const $setEdges  = document.getElementById('set-edges');
const $setDrift  = document.getElementById('set-drift');
const $setMotion = document.getElementById('set-motion');
const $setLightning      = document.getElementById('set-lightning');
const $setLightningSpeed = document.getElementById('set-lightning-speed');
const $setGlowV   = document.getElementById('set-glow-val');
const $setStarsV  = document.getElementById('set-stars-val');
const $setSkyV    = document.getElementById('set-sky-val');
const $setSizeV   = document.getElementById('set-size-val');
const $setEdgesV  = document.getElementById('set-edges-val');
const $setDriftV  = document.getElementById('set-drift-val');
const $setMotionV = document.getElementById('set-motion-val');
const $setLightningV      = document.getElementById('set-lightning-val');
const $setLightningSpeedV = document.getElementById('set-lightning-speed-val');
const $setReset      = document.getElementById('set-reset');
const $setResettle   = document.getElementById('set-resettle');
const $setFit        = document.getElementById('set-fit');
const $setFullscreen = document.getElementById('set-fullscreen');
const $setShowCase   = document.getElementById('set-showcase');
const $setWallpaper  = document.getElementById('set-wallpaper');
const $setIslands    = document.getElementById('set-islands');
const $tokenChip     = document.getElementById('token-chip');
const $tokenText     = document.getElementById('token-text');
const $wpSetupBar    = document.getElementById('wp-setup-bar');
const $wpIcons       = document.getElementById('wp-icons');
const $wpApply       = document.getElementById('wp-apply');
const $wpCancel      = document.getElementById('wp-cancel');
const $wpExitHint    = document.getElementById('wp-exit-hint');
const $wpLayoutSpan     = document.getElementById('wp-layout-span');
const $wpLayoutMirror   = document.getElementById('wp-layout-mirror');
const $wpLayoutSeparate = document.getElementById('wp-layout-separate');
const $wpResourceWarn      = document.getElementById('wp-resource-warn');
const $wpResourceWarnText  = document.getElementById('wp-resource-warn-text');
const $wpMonitorBar        = document.getElementById('wp-monitor-bar');
const $wpMonitorChips      = document.getElementById('wp-monitor-chips');
const $wpScreensBar        = document.getElementById('wp-screens-bar');
const $wpScreenChips       = document.getElementById('wp-screen-chips');
const $wpLookLabel         = document.getElementById('wp-look-label');
const $wpCardChips         = document.getElementById('wp-card-chips');
const $wpCardsLabel        = document.getElementById('wp-cards-label');
const $wpCardsOn           = document.getElementById('wp-cards-on');
const $wpCardsScreenChips  = document.getElementById('wp-cards-screen-chips');

// localStorage key for the saved wallpaper preferences. v2 adds:
//   - layout: 'span' | 'mirror' | 'separate'
//   - monitors: { 0: {settings, camera}, 1: {settings, camera}, ... }
//     populated only in 'separate' mode; each monitor remembers its own
//     setup so the user can have e.g. galaxy-centered view on monitor 1
//     and a wide-angle shot on monitor 2.
// v1 saves auto-upgrade: missing layout → 'span'; missing monitors → seeded
// from top-level settings/camera so the first switch into separate mode
// starts with sensible defaults instead of empty slots.
const WALLPAPER_PREFS_KEY = 'obsidianx.wallpaper.prefs.v2';
const WALLPAPER_PREFS_KEY_V1 = 'obsidianx.wallpaper.prefs.v1';
const WALLPAPER_LAYOUT_DEFAULT = 'span';
let _wpLayout = WALLPAPER_LAYOUT_DEFAULT;
// Number of physical monitors as reported by the host. Set on `monitorCount`
// message during wallpaper-setup boot. Default = 1 so single-monitor users
// don't see a monitor selector pop up.
let _wpMonitorCount = 1;
// Index of the monitor currently being edited in Separate mode. 0 = primary.
let _wpEditingMonitor = 0;
// Per-monitor saved prefs in Separate mode. Shape:
//   { 0: {settings, camera, cards, device}, ... }
// `device` (\\.\DISPLAY2) is what survives a monitor being re-ordered or
// unplugged; the index key is only where it sat when it was saved.
let _wpMonitorPrefs = {};
// The host's monitor list: [{index, device, width, height, left, top, primary}],
// primary first. Empty until the `monitors` message lands (single screen, or
// a host older than this page).
let _wpMonitors = [];
// Screens switched OFF, by device: { "\\\\.\\DISPLAY2": false }. Absent = on,
// so a newly plugged monitor shows the wallpaper like every screen used to.
let _wpScreens = {};
// Span / Mirror: the cards every screen shows (Separate keeps them per slot).
let _wpCards = {};
// Span: the screen the cards sit on (a device name; null = the primary).
let _wpCardsScreen = null;

function loadWallpaperPrefs() {
    try {
        // Try v2 first; fall back to v1 for migration.
        let raw = localStorage.getItem(WALLPAPER_PREFS_KEY);
        if (!raw) raw = localStorage.getItem(WALLPAPER_PREFS_KEY_V1);
        return raw ? JSON.parse(raw) : null;
    } catch { return null; }
}
function saveWallpaperPrefs(prefs) {
    try { localStorage.setItem(WALLPAPER_PREFS_KEY, JSON.stringify(prefs)); } catch {}
}

// v3 of the settings schema — adds lightning/lightningSpeed for the
// MCP-pulse flash effect. Keys missing from older payloads fall back to
// defaults on load (see loadSettings below) so older v2 saves migrate
// transparently.
const SETTINGS_KEY = 'obsidianx.universe.settings.v3';
const DEFAULT_SETTINGS = {
    glow: 0.55, stars: 0.85, motion: 1.0,
    sky: 1.0,               // Milky Way dome brightness; 0 = off, 1 = as tuned
    size: 1.0, edges: 1.0, drift: 0.0,
    lightning: 1.0,         // 0 = disable pulse flash, 1 = default, 2 = blinding
    lightningSpeed: 1.0,    // 0.5 = slow majestic strike, 2 = frantic flicker
    background: 'nebula',   // 'nebula' | 'black'
    lockSelected: true,     // true = clicked star sticks to screen centre
    legendVisible: true,    // true = show galaxy/expertise legend on the right
    cameraMode: 'free',     // 'free' | 'orbit' | 'follow' | 'random'
    // Added without a schema bump: a save that predates them simply has no
    // key, and loadSettings falls back to these — so nobody's picture changes
    // on update until they choose the brain themselves.
    theme: 'universe',      // 'universe' | 'brain'
    fiberColor: 'category'  // brain theme fibres: 'category' | 'dti'
};

/* Every Display value belongs to the theme it was set in — a glow that suits
 * a sky of stars burns a brain's stacked fibres to white, and tuning one must
 * not retune the other. The flat fields of a settings object are the theme on
 * screen, so everything that reads `currentSettings.glow` is unchanged;
 * `looks` holds each theme's set as it was when that theme was left. (The
 * entry for the theme on screen is only read after switching away from it,
 * which rewrites it first.) Theme itself and the cards are not per theme. */
const LOOK_KEYS = ['glow', 'stars', 'sky', 'size', 'edges', 'drift', 'motion',
    'lightning', 'lightningSpeed', 'background', 'lockSelected', 'legendVisible',
    'cameraMode', 'fiberColor'];

function lookOf(s) {
    const o = {};
    for (const k of LOOK_KEYS) o[k] = s[k];
    return o;
}

/** `s` showing theme `t`: the outgoing theme's values go into `looks`, `t`'s
 *  come out. A theme never tuned starts from the one being left, so the first
 *  switch after this update changes nothing on screen. */
function switchLook(s, t) {
    if (s.theme === t) return s;
    const looks = { ...(s.looks || {}), [s.theme]: lookOf(s) };
    return { ...s, ...(looks[t] || lookOf(s)), theme: t, looks };
}

/** A saved slot or a host payload laid over `base`. Its `looks` replace
 *  base's instead of merging — another screen's brain is not this screen's. */
function adoptSettings(base, incoming) {
    return { ...base, ...incoming, looks: { ...(incoming?.looks || {}) } };
}

/** One theme's Display values from storage, each checked; anything missing
 *  or malformed falls back to `def`. */
function readLook(p, def) {
    const num = (v, d) => typeof v === 'number' ? v : d;
    const bool = (v, d) => typeof v === 'boolean' ? v : d;
    return {
        glow:   num(p.glow,   def.glow),
        stars:  num(p.stars,  def.stars),
        sky:    num(p.sky,    def.sky),
        motion: num(p.motion, def.motion),
        size:   num(p.size,   def.size),
        edges:  num(p.edges,  def.edges),
        drift:  num(p.drift,  def.drift),
        lightning:      num(p.lightning,      def.lightning),
        lightningSpeed: num(p.lightningSpeed, def.lightningSpeed),
        background: (p.background === 'black' || p.background === 'nebula')
            ? p.background : def.background,
        lockSelected:  bool(p.lockSelected,  def.lockSelected),
        legendVisible: bool(p.legendVisible, def.legendVisible),
        cameraMode: (['free', 'orbit', 'follow', 'random'].includes(p.cameraMode))
            ? p.cameraMode : def.cameraMode,
        fiberColor: (p.fiberColor === 'dti' || p.fiberColor === 'category')
            ? p.fiberColor : def.fiberColor,
    };
}

function setStatus(text, isError = false) {
    if (!$status) return;
    $status.textContent = text;
    $status.classList.toggle('error', isError);
}

function getViewport() {
    return {
        w: window.innerWidth,
        h: window.innerHeight,
        dpr: window.devicePixelRatio || 1
    };
}

function applyCanvasSize() {
    if (!$canvas) return;
    const { w, h, dpr } = getViewport();
    $canvas.style.width = w + 'px';
    $canvas.style.height = h + 'px';
    $canvas.width = Math.round(w * dpr);
    $canvas.height = Math.round(h * dpr);
}

// ── overlay rendering ────────────────────────────────────────────────
function renderStats(brain) {
    if (!$stats) return;
    const notes = brain.totalNotes ?? brain.TotalNotes ?? 0;
    const words = brain.totalWords ?? brain.TotalWords ?? 0;
    const edges = brain.totalEdges ?? brain.TotalEdges ?? 0;
    const expertise = brain.expertise ?? brain.Expertise ?? [];
    const address = brain.brainAddress ?? brain.BrainAddress ?? 'unknown';
    const display = brain.displayName ?? brain.DisplayName ?? '';

    setStatus(`Connected · ${display} · ${address}`);

    $stats.innerHTML = `
        <div>Notes</div><div class="num">${notes.toLocaleString()}</div>
        <div>Words</div><div class="num">${words.toLocaleString()}</div>
        <div>Wiki-links</div><div class="num">${edges.toLocaleString()}</div>
        <div>Galaxies</div><div class="num">${expertise.length}</div>
    `;
}

function renderLegend(galaxies) {
    if (!$legend || !$legendRows) return;
    _legendHasRows = galaxies.length > 0;
    if (!galaxies.length || !currentSettings.legendVisible) {
        // Either nothing to show, or user toggled it off in settings.
        $legend.hidden = true;
        // Still populate rows below so the next "Show" toggle has content
        // without waiting for the brain payload to re-arrive.
        if (!galaxies.length) return;
    } else {
        $legend.hidden = false;
        $legend.classList.add('fade-in');
    }
    $legendRows.innerHTML = '';
    for (const g of galaxies) {
        const row = document.createElement('div');
        row.className = 'legend-row expertise-row';
        row.dataset.category = g.category;
        const hex = '#' + g.color.toString(16).padStart(6, '0');
        // Expertise pct comes from brain.Expertise[].Score (0..1); fallback
        // to a count-derived heuristic if the brain didn't ship one.
        const pct = Math.round((g.score ?? 0) * 100);
        const wordsK = g.totalWords ? (g.totalWords / 1000).toFixed(0) + 'k' : '';
        row.innerHTML = `
            <div class="expertise-head">
                <span class="legend-swatch" style="background:${hex};color:${hex};"></span>
                <span class="legend-label" title="${escapeHtml(g.label)}">${escapeHtml(g.label)}</span>
                <span class="legend-count">${g.count}</span>
            </div>
            <div class="expertise-bar">
                <div class="expertise-fill" style="width:${pct}%;background:${hex};"></div>
            </div>
            <div class="expertise-meta">
                <span class="expertise-score">${pct}%</span>
                ${wordsK ? `<span class="expertise-words">${wordsK} words</span>` : ''}
                ${g.regionLabel ? `<span class="expertise-region">${escapeHtml(g.regionLabel)}</span>` : ''}
            </div>
        `;
        row.addEventListener('click', () => scene.focusGalaxy(g.category));
        $legendRows.appendChild(row);
    }
}

// Merge fresh expertise scores into the already-mounted galaxies and
// re-render just the legend rows. Cheap DOM work — never touches three.js.
// No-op until a full mount has produced _lastGalaxies (the next mount
// carries correct data anyway). Node COUNT changes still need a reload to
// reshape the galaxies; this only keeps the score/word figures live.
function updateExpertise(list) {
    if (!Array.isArray(list) || !_lastGalaxies) return;
    const byCat = new Map();
    for (const e of list) {
        const k = e.Category ?? e.category;
        if (k) byCat.set(k, {
            score: e.Score ?? e.score ?? 0,
            totalWords: e.TotalWords ?? e.totalWords ?? 0
        });
    }
    let changed = false;
    for (const g of _lastGalaxies) {
        const e = byCat.get(g.category);
        if (!e) continue;
        if (g.score !== e.score || g.totalWords !== e.totalWords) {
            g.score = e.score;
            g.totalWords = e.totalWords;
            changed = true;
        }
    }
    if (changed) renderLegend(_lastGalaxies);
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    }[c]));
}

function fmtDate(iso) {
    if (!iso) return '—';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toISOString().slice(0, 10);
}

// Info card auto-fade. 10 s no-hover → close. Mouse-enter cancels the
// timer; mouse-leave restarts it. The fade-bar at the bottom of the card
// is a visual countdown so the user sees how long they have.
let _infoFadeTimer = null;
let _infoCurrentNode = null;

function startInfoFadeTimer() {
    cancelInfoFadeTimer();
    if (!$infoFade) return;
    // restart the CSS countdown animation by removing + re-adding the class
    $infoFade.classList.remove('tick');
    void $infoFade.offsetWidth;  // force reflow so the animation re-runs
    $infoFade.classList.add('tick');
    _infoFadeTimer = setTimeout(() => {
        scene?.focusNode?.(-1);   // deselect → showInfo(null) → hide card
        _infoCurrentNode = null;
    }, 10000);
}

function cancelInfoFadeTimer() {
    if (_infoFadeTimer) clearTimeout(_infoFadeTimer);
    _infoFadeTimer = null;
    if ($infoFade) $infoFade.classList.remove('tick');
}

function showInfo(payload) {
    if (!$info) return;
    if (!payload) {
        $info.hidden = true;
        $info.classList.remove('fade-in');
        cancelInfoFadeTimer();
        _infoCurrentNode = null;
        return;
    }
    const { node, related } = payload;
    _infoCurrentNode = node;
    const hex = '#' + node.color.toString(16).padStart(6, '0');
    $info.hidden = false;
    $info.classList.add('fade-in');
    $infoCat.style.color = hex;
    $infoCat.textContent = node.categoryLabel ?? node.category ?? '';
    $infoTitle.textContent = node.title;
    $infoMeta.innerHTML = `
        <span><span class="meta-key">words</span>${node.wordCount.toLocaleString()}</span>
        <span><span class="meta-key">links</span>${(related?.length ?? 0)}</span>
        <span><span class="meta-key">modified</span>${fmtDate(node.modifiedAt)}</span>
    `;
    $infoPrev.textContent = node.preview || '(no preview available)';
    $infoTags.innerHTML = '';
    const tags = (node.tags || []).slice(0, 8);
    for (const t of tags) {
        const chip = document.createElement('span');
        chip.className = 'tag-chip';
        chip.textContent = '#' + t;
        $infoTags.appendChild(chip);
    }
    startInfoFadeTimer();
}

// ── Inline note edit/save state machine ──────────────────────────────
// view  → click ✎ Edit  → request content from host → wait → enter edit
// edit  → click 💾 Save → post content → wait → ok status → back to view
//        click Cancel   → back to view (preview restored)
//        click ↗ Open   → open in WPF Markdown editor (full screen) — leaves Universe
let _infoMode = 'view';     // 'view' | 'loading' | 'edit' | 'saving'

function setInfoStatus(text, kind) {
    if (!$infoStatus) return;
    if (!text) { $infoStatus.hidden = true; return; }
    $infoStatus.hidden = false;
    $infoStatus.className = 'info-status ' + (kind || '');
    $infoStatus.textContent = text;
}

function enterEditUI() {
    _infoMode = 'edit';
    $infoPrev.hidden = true;
    $infoEditor.hidden = false;
    $infoEdit.hidden = true;
    $infoSave.hidden = false;
    $infoCancel.hidden = false;
    setInfoStatus(null);
    cancelInfoFadeTimer();   // don't auto-close while user is typing
    $infoEditor.focus();
}

function exitEditUI() {
    _infoMode = 'view';
    $infoPrev.hidden = false;
    $infoEditor.hidden = true;
    $infoEditor.value = '';
    $infoEdit.hidden = false;
    $infoSave.hidden = true;
    $infoCancel.hidden = true;
    setInfoStatus(null);
    if (!$info.hidden) startInfoFadeTimer();
}

// ── Wallpaper setup bar ────────────────────────────────────────────
// Shown when ?mode=wallpaper-setup. User configures interactively then
// clicks Apply → posts wallpaperApply to C# which reparents to WorkerW.
//
// Three things are chosen here, per screen when there are several:
//   WHERE — which monitors show the wallpaper at all (_wpScreens);
//   WHAT  — universe or neural brain (the ordinary `theme` setting);
//   WHICH — the HUD cards printed on it (cards.js; none by default).
// Span and Mirror make one choice for every screen — Mirror by definition
// shows one picture, and Span IS one picture. Separate makes it per screen,
// through the same "Configuring" chips that already carried each screen's
// camera and settings. The preview shows the result live: its theme is the
// live theme and its HUD shows exactly the cards that will be printed.
function wireWallpaperSetup() {
    if (!$wpSetupBar) return;
    $wpSetupBar.hidden = false;

    // (Removed Random camera quick-toggle UI sync — cameraMode is now set
    // via the main settings panel's cam-btn picker, which reads/writes the
    // wallpaper-specific config through saveSettings's routing.)

    // Restore last saved camera angle + settings if available — so user
    // doesn't lose their previous setup when they reopen Wallpaper.
    const saved = loadWallpaperPrefs();
    if (saved) {
        if (saved.settings) {
            currentSettings = adoptSettings(currentSettings, saved.settings);
            applySettingsToUI(currentSettings);
            applySettingsToScene(currentSettings);
            saveSettings(currentSettings);
        }
        if (saved.camera && scene?.restoreCamera) {
            // Defer to next frame so scene/mount is ready.
            requestAnimationFrame(() => scene.restoreCamera(saved.camera));
        }
        if (saved.layout === 'span' || saved.layout === 'mirror' || saved.layout === 'separate') {
            _wpLayout = saved.layout;
        }
        if (saved.monitors && typeof saved.monitors === 'object') {
            _wpMonitorPrefs = saved.monitors;
        }
        if (saved.screens && typeof saved.screens === 'object') _wpScreens = { ...saved.screens };
        _wpCards = normalizeCards(saved.cards);
        if (typeof saved.cardsScreen === 'string') _wpCardsScreen = saved.cardsScreen;
    }
    applyLayoutToUI(_wpLayout);
    showPreviewCards();

    // Layout segmented toggle (Span / Mirror / Separate).
    //   • Span     = 1 WebView2, stretched across virtual screen.
    //   • Mirror   = N WebView2, every monitor renders the SAME view
    //                (sync'd via host master→slave broadcast).
    //   • Separate = N WebView2, each monitor has its OWN setup
    //                (different camera + settings, remembered per monitor).
    function applyLayoutToUI(mode) {
        $wpLayoutSpan?.classList.toggle('active',     mode === 'span');
        $wpLayoutMirror?.classList.toggle('active',   mode === 'mirror');
        $wpLayoutSeparate?.classList.toggle('active', mode === 'separate');
        // Resource warning row — only shown for heavy modes. Span = 1 process,
        // free of charge; Mirror/Separate spawn N. Update text with current
        // monitor count so the user sees "Heavy: 3×~250 MB" not just "N×".
        if ($wpResourceWarn) {
            // Counted over the screens that will SHOW it: a screen switched
            // off costs nothing, and quoting it would overstate the price.
            const procs = enabledIndices().length;
            const heavy = (mode === 'mirror' || mode === 'separate') && procs > 1;
            $wpResourceWarn.hidden = !heavy;
            if (heavy && $wpResourceWarnText) {
                $wpResourceWarnText.textContent =
                    `Heavy: ${procs}×~250 MB RAM + ${procs} GPU contexts. Span uses only 1.`;
            }
        }
        // Per-monitor selector — Separate mode only, and only once there is
        // more than one SHOWING screen to choose between.
        const showMonitorBar = (mode === 'separate' && enabledIndices().length > 1);
        if ($wpMonitorBar) $wpMonitorBar.hidden = !showMonitorBar;
        if (showMonitorBar) renderMonitorChips();
        renderScreens();
        renderLook();
        renderCards();
    }

    // Wire all three layout chips. Switching mode rebuilds the per-monitor
    // chip bar visibility but does NOT mutate per-monitor saved prefs.
    $wpLayoutSpan?.addEventListener('click', () => {
        if (_wpLayout === 'separate') flushEditingMonitorPrefs();
        _wpLayout = 'span';
        applyLayoutToUI(_wpLayout);
        showPreviewCards();
    });
    $wpLayoutMirror?.addEventListener('click', () => {
        if (_wpLayout === 'separate') flushEditingMonitorPrefs();
        _wpLayout = 'mirror';
        applyLayoutToUI(_wpLayout);
        showPreviewCards();
    });
    $wpLayoutSeparate?.addEventListener('click', () => {
        _wpLayout = 'separate';
        ensureEditingEnabled();
        // Entering Separate: ensure the currently-edited monitor's slot has
        // SOMETHING in it (the current live settings/camera) so the chip
        // looks "filled". Other slots seed from live values lazily on click.
        // Before the render, so the card row draws the slot, not an empty one.
        flushEditingMonitorPrefs();
        applyLayoutToUI(_wpLayout);
        showPreviewCards();
    });

    // Host reports its monitors after wallpaper-setup boots. Slots saved
    // against a device follow that device to wherever it now sits in the
    // list, the screen being edited must be one that is switched on, and then
    // every row re-renders against the real count.
    window.addEventListener('wpMonitorCountChanged', () => {
        remapSlotsByDevice();
        ensureEditingEnabled();
        applyLayoutToUI(_wpLayout);
        showPreviewCards();
    });

    // Persist the currently-shown live settings/camera into the slot for
    // the monitor currently being edited (Separate mode only). Called
    // when the user switches monitors so edits don't get lost. The slot's
    // cards are kept — they are edited on their own row, not captured live.
    function flushEditingMonitorPrefs() {
        if (_wpLayout !== 'separate') return;
        const cam = scene?.snapshotCamera?.() ?? null;
        // A slot born here starts from the shared card set, the same way its
        // settings start from what is on screen — cards picked in Span or
        // Mirror a moment ago should not vanish on switching to Separate.
        const prev = _wpMonitorPrefs[_wpEditingMonitor] || { cards: normalizeCards(_wpCards) };
        _wpMonitorPrefs[_wpEditingMonitor] = {
            ...prev,
            settings: { ...currentSettings },
            camera:   cam,
            device:   _wpMonitors[_wpEditingMonitor]?.device ?? prev.device ?? null,
        };
    }

    /** "2 · 1920×1080" — or just "2" before the host has said anything. */
    function screenLabel(i) {
        const m = _wpMonitors[i];
        const n = String(i + 1);
        if (!m) return i === 0 ? `${n} (primary)` : n;
        return `${n}${m.primary ? ' ★' : ''} · ${m.width}×${m.height}`;
    }

    // Build the per-monitor chip row. One chip per SHOWING monitor. The
    // active chip is the one currently being edited in the preview window.
    function renderMonitorChips() {
        if (!$wpMonitorChips) return;
        $wpMonitorChips.innerHTML = '';
        for (const i of enabledIndices()) {
            const chip = document.createElement('button');
            chip.type = 'button';
            chip.className = 'wp-mon-chip' + (i === _wpEditingMonitor ? ' active' : '');
            chip.dataset.idx = String(i);
            chip.textContent = screenLabel(i);
            chip.title = `Edit screen ${i + 1}'s theme, camera and cards`;
            chip.addEventListener('click', () => switchEditingMonitor(i));
            $wpMonitorChips.appendChild(chip);
        }
    }

    // ── WHERE: screens on / off ──
    function screenCount() { return Math.max(1, _wpMonitors.length || _wpMonitorCount); }
    function screenEnabled(i) {
        const d = _wpMonitors[i]?.device;
        return d ? _wpScreens[d] !== false : true;
    }
    function enabledIndices() {
        const out = [];
        for (let i = 0; i < screenCount(); i++) if (screenEnabled(i)) out.push(i);
        return out.length ? out : [0];
    }
    function ensureEditingEnabled() {
        const on = enabledIndices();
        if (!on.includes(_wpEditingMonitor)) {
            if (_wpLayout === 'separate') switchEditingMonitor(on[0]);
            else _wpEditingMonitor = on[0];
        }
    }
    /**
     * A monitor list that moved between sessions takes its slots with it.
     * Slots that name a device go wherever that device now sits; a slot whose
     * device is not plugged in is PARKED under `d:<device>` — kept, not given
     * to whichever monitor now holds its old index, so it comes back intact
     * with its screen. Only slots from before devices were recorded fall back
     * to their index, and only onto a position nothing else claimed.
     */
    function remapSlotsByDevice() {
        if (!_wpMonitors.length) return;
        const at = new Map(_wpMonitors.map(m => [m.device, m.index]));
        const next = {};
        const legacy = [];
        for (const [k, slot] of Object.entries(_wpMonitorPrefs)) {
            if (!slot) continue;
            if (!slot.device) { legacy.push([k, slot]); continue; }
            if (at.has(slot.device)) next[at.get(slot.device)] = slot;
            else next['d:' + slot.device] = slot;
        }
        for (const [k, slot] of legacy) {
            const idx = Number(k);
            if (Number.isInteger(idx) && next[idx] === undefined) next[idx] = slot;
        }
        _wpMonitorPrefs = next;
    }
    function renderScreens() {
        if (!$wpScreensBar || !$wpScreenChips) return;
        const multi = _wpMonitors.length > 1;
        $wpScreensBar.hidden = !multi;
        if (!multi) return;
        $wpScreenChips.innerHTML = '';
        for (const m of _wpMonitors) {
            const on = screenEnabled(m.index);
            const chip = document.createElement('button');
            chip.type = 'button';
            chip.className = 'wp-mon-chip' + (on ? ' active' : ' off');
            chip.setAttribute('aria-pressed', String(on));
            chip.textContent = screenLabel(m.index);
            chip.title = on
                ? `Screen ${m.index + 1} shows the BrainX wallpaper — click to leave its own wallpaper there instead`
                : `Screen ${m.index + 1} keeps its own wallpaper — click to show BrainX there`;
            chip.addEventListener('click', () => toggleScreen(m.index));
            $wpScreenChips.appendChild(chip);
        }
    }
    function toggleScreen(i) {
        const d = _wpMonitors[i]?.device;
        if (!d) return;
        if (screenEnabled(i)) {
            // Nothing showing is not a wallpaper, it is a Cancel with extra
            // steps — and Cancel already has a button.
            if (enabledIndices().length <= 1) {
                setStatus('At least one screen has to show the wallpaper.');
                return;
            }
            if (_wpLayout === 'separate' && i === _wpEditingMonitor) flushEditingMonitorPrefs();
            _wpScreens[d] = false;
        } else {
            delete _wpScreens[d];
        }
        ensureEditingEnabled();
        applyLayoutToUI(_wpLayout);
        showPreviewCards();
    }

    // ── WHAT: universe or neural brain ──
    // The segmented control is a second face of the ordinary theme setting —
    // chooseTheme saves it through the wallpaper routing, and in Separate
    // mode flushEditingMonitorPrefs carries it into the screen's slot.
    document.querySelectorAll('[data-wp-theme]').forEach(btn =>
        btn.addEventListener('click', () => chooseTheme(btn.dataset.wpTheme)));
    function renderLook() {
        if (!$wpLookLabel) return;
        const separate = _wpLayout === 'separate' && enabledIndices().length > 1;
        const scope = separate ? `Screen ${_wpEditingMonitor + 1}`
                    : (_wpMonitors.length > 1 ? 'All screens' : '');
        $wpLookLabel.textContent = scope ? `${scope} · theme:` : 'Theme:';
        if ($wpCardsLabel) $wpCardsLabel.textContent = scope ? `${scope} · cards:` : 'Cards:';
    }

    // ── WHICH: the cards printed on it ──
    function cardsForEditing() {
        return _wpLayout === 'separate'
            ? normalizeCards(_wpMonitorPrefs[_wpEditingMonitor]?.cards)
            : normalizeCards(_wpCards);
    }
    function storeCards(c) {
        const cards = normalizeCards(c);
        if (_wpLayout === 'separate') {
            if (!_wpMonitorPrefs[_wpEditingMonitor]) flushEditingMonitorPrefs();
            const prev = _wpMonitorPrefs[_wpEditingMonitor];
            _wpMonitorPrefs[_wpEditingMonitor] = { ...prev, cards };
        } else {
            _wpCards = cards;
        }
    }
    function showPreviewCards() { setWallpaperCards(cardsForEditing(), 'setup'); }
    function renderCards() {
        if ($wpCardChips) {
            const cards = cardsForEditing();
            $wpCardChips.innerHTML = '';
            for (const c of WALLPAPER_CARDS) {
                const on = cards[c.id] === true;
                const chip = document.createElement('button');
                chip.type = 'button';
                chip.className = 'wp-mon-chip' + (on ? ' active' : '');
                chip.setAttribute('aria-pressed', String(on));
                chip.textContent = c.label;
                chip.addEventListener('click', () => {
                    const next = cardsForEditing();
                    next[c.id] = !next[c.id];
                    storeCards(next);
                    showPreviewCards();
                    renderCards();
                });
                $wpCardChips.appendChild(chip);
            }
        }
        // Span is ONE window across the screens: its cards go on a screen of
        // their own choosing, or the centre column would sit on a bezel.
        const spanScreens = _wpLayout === 'span' ? enabledIndices() : [];
        if ($wpCardsOn) $wpCardsOn.hidden = spanScreens.length < 2;
        if ($wpCardsScreenChips && spanScreens.length >= 2) {
            const current = cardsScreenIndex();
            $wpCardsScreenChips.innerHTML = '';
            for (const i of spanScreens) {
                const chip = document.createElement('button');
                chip.type = 'button';
                chip.className = 'wp-mon-chip' + (i === current ? ' active' : '');
                chip.textContent = String(i + 1);
                chip.title = `Put the cards on screen ${i + 1}`;
                chip.addEventListener('click', () => {
                    _wpCardsScreen = _wpMonitors[i]?.device ?? null;
                    renderCards();
                });
                $wpCardsScreenChips.appendChild(chip);
            }
        }
    }
    /** The span screen the cards go on: the chosen one if it is still
     *  showing, else the primary, else the first screen that is on. */
    function cardsScreenIndex() {
        const on = enabledIndices();
        const chosen = _wpMonitors.findIndex(m => m.device === _wpCardsScreen);
        if (chosen >= 0 && on.includes(chosen)) return chosen;
        const primary = _wpMonitors.findIndex(m => m.primary);
        return (primary >= 0 && on.includes(primary)) ? primary : on[0];
    }
    // A chip in the preview's own settings panel changes the same cards.
    onWallpaperCardsChange((cards, source) => {
        if (source !== 'hud') return;
        storeCards(cards);
        renderCards();
    });

    // Switch which monitor is being edited. Saves current live state into
    // the OLD monitor's slot, then loads the NEW monitor's slot into the
    // live preview (settings + camera). If the new slot is empty, the
    // current live state becomes the seed for that monitor.
    function switchEditingMonitor(newIdx) {
        if (newIdx === _wpEditingMonitor) return;
        // Save current edits to the OLD slot.
        flushEditingMonitorPrefs();
        _wpEditingMonitor = newIdx;
        const slot = _wpMonitorPrefs[newIdx];
        if (slot) {
            if (slot.settings) {
                currentSettings = adoptSettings(currentSettings, slot.settings);
                applySettingsToUI(currentSettings);
                applySettingsToScene(currentSettings);
                saveSettings(currentSettings);
            }
            if (slot.camera && scene?.restoreCamera) {
                requestAnimationFrame(() => scene.restoreCamera(slot.camera));
            }
        } else {
            // No saved slot — seed it from current live values so the
            // chip "remembers" what was on screen the moment we landed.
            flushEditingMonitorPrefs();
        }
        renderMonitorChips();
        renderLook();
        renderCards();
        showPreviewCards();
    }

    // (Random camera quick-toggle handler removed — Free/Orbit/Follow/Random
    // is now picked via the settings panel's standard .cam-btn[data-mode]
    // group, which already calls scene.setCameraMode and saveSettings —
    // and saveSettings now routes through the wallpaper prefs object when
    // the URL is ?mode=wallpaper-setup, so the wallpaper's camera-mode
    // choice is fully decoupled from the main app's settings.)

    // Hide/Show desktop icons — C# handles SHELLDLL_DefView ShowWindow.
    // Button label flips between "Hide" / "Show" so the action is clear.
    $wpIcons?.addEventListener('click', () => {
        const hidden = $wpIcons.dataset.hidden === 'true';
        const nextHidden = !hidden;
        $wpIcons.dataset.hidden = String(nextHidden);
        $wpIcons.innerHTML = nextHidden
            ? '\u{1F4F1} Show desktop icons'
            : '\u{1F4F1} Hide desktop icons';
        postToHost({ type: 'wallpaperToggleIcons', hide: nextHidden });
    });

    $wpApply?.addEventListener('click', () => {
        // Save current edits one last time before Apply so the active
        // monitor's slot is up-to-date.
        if (_wpLayout === 'separate') flushEditingMonitorPrefs();

        const cam = scene?.snapshotCamera?.() ?? null;
        // Separate: a showing screen nobody opened gets what is on screen
        // now and the shared cards — the same seed its chip would have given
        // it on its first click — rather than the host guessing.
        if (_wpLayout === 'separate') {
            for (const i of enabledIndices()) {
                if (_wpMonitorPrefs[i]) continue;
                _wpMonitorPrefs[i] = {
                    settings: { ...currentSettings }, camera: cam, cards: normalizeCards(_wpCards),
                    device: _wpMonitors[i]?.device ?? null,
                };
            }
        }
        const monitors = {};
        for (const [k, slot] of Object.entries(_wpMonitorPrefs)) {
            if (!slot) continue;
            monitors[k] = {
                settings: slot.settings ?? null,
                camera: slot.camera ?? null,
                cards: normalizeCards(slot.cards),
                device: slot.device ?? _wpMonitors[k]?.device ?? null,
            };
        }
        const spanOn = cardsScreenIndex();
        const prefs = {
            settings: currentSettings,
            camera: cam,
            layout: _wpLayout,
            monitors: _wpMonitorPrefs,
            screens: _wpScreens,
            cards: _wpCards,
            cardsScreen: _wpCardsScreen,
        };
        saveWallpaperPrefs(prefs);
        setStatus('Applying wallpaper…');
        postToHost({
            type: 'wallpaperApply',
            layout: _wpLayout,
            // Which screens show it. Null from a single-monitor host (or one
            // that never listed its monitors): every screen, as before.
            screens: _wpMonitors.length
                ? _wpMonitors.map(m => ({ index: m.index, device: m.device, enabled: screenEnabled(m.index) }))
                : null,
            // Span / Mirror: one look for every screen.
            global: {
                settings: currentSettings,
                camera: cam,
                cards: normalizeCards(_wpCards),
                cardsScreen: _wpMonitors[spanOn]?.device ?? null,
            },
            // Separate: the host pushes each screen its own slot after that
            // screen's WebView2 fires 'ready' — matched by device first.
            monitors: (_wpLayout === 'separate') ? monitors : null,
        });
    });

    $wpCancel?.addEventListener('click', () => {
        setStatus('Cancelling…');
        postToHost({ type: 'wallpaperCancel' });
    });
}

function wireInfoCard() {
    if (!$info) return;
    // Hover cancels the auto-fade; leave restarts it.
    $info.addEventListener('mouseenter', cancelInfoFadeTimer);
    $info.addEventListener('mouseleave', () => {
        // Don't restart fade while editing — user could lose unsaved typing.
        if (!$info.hidden && _infoMode === 'view') startInfoFadeTimer();
    });

    $infoClose?.addEventListener('click', () => {
        if (_infoMode === 'edit' && $infoEditor.value && !confirm('Discard changes?')) return;
        exitEditUI();
        scene?.focusNode?.(-1);
    });

    // Inline edit: fetch full note content from C#, then swap into textarea.
    $infoEdit?.addEventListener('click', () => {
        if (!_infoCurrentNode) return;
        _infoMode = 'loading';
        cancelInfoFadeTimer();
        $infoEditor.hidden = false;
        $infoEditor.value = '';
        $infoEditor.placeholder = 'Loading note content…';
        $infoPrev.hidden = true;
        $infoEdit.hidden = true;
        setInfoStatus('Loading…', 'loading');
        postToHost({ type: 'requestNoteContent', noteId: _infoCurrentNode.id });
    });

    $infoSave?.addEventListener('click', () => {
        if (!_infoCurrentNode || _infoMode !== 'edit') return;
        _infoMode = 'saving';
        setInfoStatus('Saving…', 'loading');
        postToHost({
            type: 'saveNote',
            noteId: _infoCurrentNode.id,
            content: $infoEditor.value
        });
    });

    $infoCancel?.addEventListener('click', () => {
        if ($infoEditor.value && !confirm('Discard changes?')) return;
        exitEditUI();
    });

    // ⚡ Walk this concept — sequenced lightning across the 2-hop graph
    // neighbourhood of the currently-selected note. Same effect as a
    // right-click on the canvas, just discoverable from the info card.
    $infoWalk?.addEventListener('click', () => {
        if (!_infoCurrentNode) return;
        scene?.walkFromHere?.(_infoCurrentNode.id, 2);
    });

    // ↗ Open in full WPF editor — leaves Universe entirely.
    $infoOpen?.addEventListener('click', () => {
        if (!_infoCurrentNode) return;
        postToHost({ type: 'editNote', noteId: _infoCurrentNode.id });
    });

    // Ctrl+S inside the textarea = Save
    $infoEditor?.addEventListener('keydown', e => {
        if ((e.ctrlKey || e.metaKey) && e.key === 's') {
            e.preventDefault();
            $infoSave?.click();
        }
        if (e.key === 'Escape') {
            e.preventDefault();
            $infoCancel?.click();
        }
    });
}

// ── bridge wiring ────────────────────────────────────────────────────
function postToHost(msg) {
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.postMessage(msg);
    } else {
        console.warn('[Universe] Not running inside WebView2');
    }
}

let scene = null;
let pendingBrain = null;
// Last galaxy list handed to renderLegend(). Kept so a lightweight
// 'expertise' message can refresh the legend %s in place WITHOUT a
// scene.mount() geometry rebuild (that full rebuild on every re-index
// was what made the universe stutter).
let _lastGalaxies = null;

/**
 * Did this touch CHANGE the brain, or only read it?
 *
 * `write` FIRST, because it is the one that actually happens. The access log's
 * whole vocabulary is eight short verbs, not tool names — search, recall,
 * semantic_search and get_note read; `write` is the only one that changes a
 * note, and it covers both an MCP write and an editor save, which reach here
 * as the same string (BumpNodeActivityByPath passes "write" too). The first
 * cut of this matched tool-name verbs like `create` and `append` and therefore
 * matched NOTHING this log emits: the galaxies would never have re-settled and
 * nothing would have said why. Checked against every op in 8,900 logged calls.
 *
 * The tool-name verbs stay because the client documents two access-log shapes
 * (`mcp.<tool>` and bare `<tool>`), so an op named after a tool is a thing this
 * has to survive rather than a thing it can assume away. None of them appears
 * inside a read verb, so the superset costs nothing.
 */
const WRITE_VERBS = ['write', 'save',
                     'create', 'append', 'remember', 'import', 'delete', 'remove',
                     'apply_audit_fix', 'mark_verified', 'synthesize', 'dream'];
function isWriteOp(op) {
    if (!op) return false;
    const s = String(op).toLowerCase();
    return WRITE_VERBS.some(v => s.includes(v));
}

function onHostMessage(evt) {
    const msg = evt.data;
    if (!msg || typeof msg !== 'object') return;
    switch (msg.type) {
        case 'brain':
            handleBrain(msg.payload);
            break;
        case 'pulse':
            // C# forwards every MCP read/write touch as {noteId, op}. When
            // noteId is present we flash that exact star; when it's blank
            // (node-less MCP call — brain_stats / list / create) we flash a
            // random star so every MCP call stays visibly "alive"
            // (user spec: ทุก MCP call ต้องกระพริบ).
            if (scene) {
                if (msg.noteId) scene.firePulse(msg.noteId, msg.op);
                else scene.firePulseRandom?.(msg.op);
                // A READ and a WRITE are not the same event and must not look
                // the same. Both flash the star — something touched this note.
                // Only a write also lets that galaxy re-settle, because only a
                // write changed what the layout is a picture OF.
                if (msg.noteId && isWriteOp(msg.op)) scene.reorganize?.([msg.noteId]);
            }
            break;
        case 'semantic':
            // Embedding-derived affinities. They arrive once, after the client
            // finishes computing them, which is usually well after the brain
            // payload — the scene applies them whenever they land.
            scene?.applySemanticSprings?.(msg.springs);
            break;
        case 'brainwork':
            // A long job over the whole brain: the gardener re-baking bundles
            // and auditing, or a re-index re-scanning the vault. Both are
            // literally "the brain is being organised", and the picture used
            // to sit perfectly still through either. Sent as a state per job
            // rather than as edges, so a message that arrives twice, or one
            // that is missed, still leaves the scene agreeing with the app.
            scene?.setBrainWork?.(msg.job, !!msg.running);
            break;
        case 'peerJoined':
            // Join Brain demo / live: C# forwards every PeerJoined hub
            // event as {address, displayName}. Scene drops a colored halo
            // on the orbit ring + plays the scale-in pop.
            if (scene && msg.address) {
                scene.addPeer({ address: msg.address, displayName: msg.displayName });
            }
            break;
        case 'peerLeft':
            // Peer disconnected — scene fades the halo out over ~450 ms
            // then disposes its GPU resources.
            if (scene && msg.address) scene.removePeer(msg.address);
            break;
        case 'peerActivity':
            // Future hook: hub forwards share-allow / share-deny / scope-set
            // events so the halo flashes (color override available for
            // denied = red). Currently only triggered from the demo button.
            if (scene && msg.address) scene.pulsePeerActivity(msg.address, msg.color);
            break;
        case 'tokenStats':
            // C# forwards BrainCostTracker.Compute output as
            // { text: "🧠 12.3k tok/24h", tooltip: "...multi-line..." }.
            // This is the brain's measured COST, not a savings figure — the
            // old "+12.3k saved" came from invented per-op constants.
            if ($tokenChip && $tokenText && msg.text) {
                $tokenText.textContent = msg.text;
                $tokenChip.title = msg.tooltip || '';
                $tokenChip.hidden = false;
            }
            break;
        case 'dashStats':
            // Brain growth numbers for the top-left dashboard chip.
            // { nodes: 746, words: 1111607 } → {NODES 746 · WORDS 1.1M}.
            handleDashStats(msg);
            break;
        case 'dashLoad':
            // GPU + CPU samples for the bottom-left sparkline chip.
            // { gpu: 33, cpu: 37 } → push into 60-sample ring + redraw.
            handleDashLoad(msg);
            break;
        case 'expertise':
            // Live expertise refresh after a re-index. Updates ONLY the
            // legend %s/word counts — no scene.mount(), so the galaxy
            // doesn't rebuild (that was the stutter). msg.payload is the
            // small expertise array from _graph.ExpertiseMap.
            updateExpertise(msg.payload);
            break;
        case 'finalizeWallpaper':
            // C# has just reparented us to WorkerW. Transition this page
            // from setup → final wallpaper without reload: drop the setup
            // chrome, hide HUD, disable mouse.
            document.body.classList.remove('wallpaper-setup');
            document.body.classList.add('wallpaper-mode');
            if ($wpSetupBar) $wpSetupBar.hidden = true;
            // Flash the exit hint briefly so the user remembers how to leave.
            if ($wpExitHint) {
                $wpExitHint.hidden = false;
                setTimeout(() => { if ($wpExitHint) $wpExitHint.hidden = true; }, 6000);
            }
            break;
        case 'wallpaperStatus':
            // C# wallpaper steps streamed back as a status update. The WPF
            // status bar at the bottom is easy to miss; surface it in the
            // Universe HUD too.
            if (msg.text) setStatus('Wallpaper: ' + msg.text);
            break;
        case 'monitorCount':
            // Host reports how many physical monitors it sees. Drives the
            // resource warning text + the per-monitor chip count in
            // Separate mode. Re-render the wallpaper-setup UI if it's
            // already visible.
            if (typeof msg.count === 'number' && msg.count >= 1) {
                _wpMonitorCount = msg.count;
                // If the layout-toggle hook captured an apply function,
                // re-run it so the warning + chips reflect the new count.
                window.dispatchEvent(new CustomEvent('wpMonitorCountChanged'));
            }
            break;
        case 'monitors':
            // The monitors themselves — device names, sizes, which is
            // primary — so the setup bar can name each screen and remember
            // choices by device rather than by a position that can move.
            if (Array.isArray(msg.list) && msg.list.length) {
                _wpMonitors = msg.list
                    .filter(m => m && typeof m.device === 'string')
                    .map((m, i) => ({
                        index: i, device: m.device,
                        width: m.width | 0, height: m.height | 0,
                        left: m.left | 0, top: m.top | 0, primary: !!m.primary,
                    }));
                _wpMonitorCount = Math.max(1, _wpMonitors.length);
                window.dispatchEvent(new CustomEvent('wpMonitorCountChanged'));
            }
            break;
        case 'mirrorState':
            // Slave-side: master WebView2 broadcasts camera state to all
            // mirror-mode siblings via the host. Apply directly (no lerp)
            // so every monitor shows the same view within one frame.
            if (scene && msg.target && msg.position) {
                scene.applyMirrorState?.(msg.target, msg.position);
            }
            break;
        case 'applyMonitorSettings':
            // Per-surface bootstrap: the host sends every wallpaper surface
            // what it was configured to show — theme and sliders, camera,
            // cards — after its 'ready' handshake (and to the promoted setup
            // window straight after Apply, which never says 'ready' again).
            // Apply only, never save: N surfaces writing the one shared
            // settings object would leave it holding whichever booted last.
            if (msg.settings) {
                currentSettings = adoptSettings(currentSettings, msg.settings);
                applySettingsToUI?.(currentSettings);
                applySettingsToScene?.(currentSettings);
            }
            if (msg.camera && scene?.restoreCamera) {
                requestAnimationFrame(() => scene.restoreCamera(msg.camera));
            }
            if (msg.cards !== undefined) setWallpaperCards(msg.cards, 'host');
            applyHudRect(msg.hudRect);
            // If host marks us as a mirror master, start broadcasting
            // camera state to siblings at ~10 fps.
            if (msg.mirrorRole === 'master' && scene?.startMirrorBroadcast) {
                scene.startMirrorBroadcast((state) => {
                    postToHost({ type: 'mirrorState', target: state.target, position: state.position });
                });
            }
            break;
        case 'pauseRender':
            // C# detected our wallpaper is fully covered (fullscreen game,
            // browser maximized over us, RDP, etc.) → stop the rAF loop to
            // give the foreground app the GPU. scene.pauseAnimation cancels
            // the in-flight rAF + sets a flag so tick() stops re-scheduling.
            if (scene) scene.pauseAnimation?.();
            break;
        case 'resumeRender':
            // C# detected the wallpaper is visible again → restart rAF.
            // scene.resumeAnimation resets THREE.Clock so the first frame
            // doesn't attribute the full pause duration as elapsed dt
            // (which would jump-rotate the universe by hours of motion).
            if (scene) scene.resumeAnimation?.();
            break;
        case 'viewState':
            // C# notifies us when the WPF floating toggle switched the view.
            // Re-sync our segmented control so it doesn't lie about state.
            document.querySelectorAll('.cam-btn[data-view]').forEach(b => {
                b.classList.toggle('active', b.dataset.view === msg.view);
            });
            break;
        case 'setTheme':
            // The WPF pill over the view (Universe · Brain). Same path as the
            // settings row, so the choice is saved and the pill is told back.
            chooseTheme(msg.theme);
            break;
        case 'noteContent':
            // C# returned the full Markdown body of the note we asked for.
            // Ignore if user clicked another star meanwhile (id mismatch).
            if (_infoMode === 'loading' && _infoCurrentNode?.id === msg.noteId) {
                $infoEditor.value = msg.content ?? '';
                $infoEditor.placeholder = '';
                enterEditUI();
            }
            break;
        case 'noteContentError':
            if (_infoMode === 'loading') {
                setInfoStatus(msg.error || 'Failed to load', 'error');
                _infoMode = 'view';
                $infoEditor.hidden = true;
                $infoPrev.hidden = false;
                $infoEdit.hidden = false;
            }
            break;
        case 'noteSaved':
            if (_infoCurrentNode?.id === msg.noteId) {
                setInfoStatus('Saved ✓', 'ok');
                // brief flash, then back to view mode with the new preview.
                if (_infoCurrentNode) _infoCurrentNode.preview = ($infoEditor.value || '').slice(0, 480);
                $infoPrev.textContent = _infoCurrentNode?.preview || '';
                setTimeout(exitEditUI, 700);
            }
            break;
        case 'noteSaveError':
            setInfoStatus(msg.error || 'Save failed', 'error');
            _infoMode = 'edit';
            break;
        default:
            console.log('[Universe] unknown message', msg);
    }
}

function handleBrain(brain) {
    renderStats(brain);
    pendingBrain = brain;
    if (scene) mountScene(brain);
}

/** The status line after a mount, in the vocabulary of the picture on screen. */
function renderedStatus(universe) {
    if (!universe) return '';
    const n = universe.nodes.length.toLocaleString();
    const e = universe.edges.length.toLocaleString();
    return scene?.getTheme?.() === 'brain'
        ? `Neural brain rendered · ${n} neurons · ${e} fibres · ${universe.galaxies.length} regions`
        : `Universe rendered · ${n} stars · ${e} wiki-links · ${universe.galaxies.length} galaxies`;
}
let _lastUniverse = null;

function mountScene(brain) {
    try {
        const universe = scene.mount(brain);
        _lastUniverse = universe;
        // Same as a theme switch: a re-mount must not silently drop an
        // islands highlight whose button is still lit.
        if ($setIslands?.classList.contains('active')) scene.toggleIslands?.(true);
        if (universe.nodes.length === 0) {
            setStatus('Brain has 0 notes yet — open BrainX and add some.');
        } else {
            setStatus(renderedStatus(universe));
        }
        // Dashboard preview: galaxy must always sit nicely in frame +
        // camera should auto-fly to MCP-pulsed stars (user spec
        // "ออโต้จัดให้พอดีตลอด และเป็น follow ด้วย"). Re-snap fit + ensure
        // follow mode is active right after mount, and again after physics
        // settles in case the centroid moved.
        if (IS_WALLPAPER_ACTIVE && universe.nodes.length > 0) {
            requestAnimationFrame(() => {
                scene.fitToScreen?.({ duration: 0, keepDirection: false });
                scene.setCameraMode?.('follow');   // captures the just-fitted pose as home
            });
            // Settle pass: physics still pushes nodes for ~3s after mount.
            // Re-fit and refresh follow-home so the resting pose matches
            // the final layout, not the chaotic initial one.
            setTimeout(() => {
                if (!scene) return;
                scene.setCameraMode?.('free');
                scene.fitToScreen?.({ duration: 0.8, keepDirection: false });
                setTimeout(() => scene.setCameraMode?.('follow'), 900);
            }, 3500);
            // Periodic gentle re-fit so drift (when settings.drift > 0)
            // never strands the galaxy half-off-screen.
            _startDashAutoFitLoop();
        }
    } catch (err) {
        console.error('[Universe] mount failed', err);
        setStatus('Render failed — see DevTools (F12)', true);
    }
}

// ── Dashboard auto-fit loop ──────────────────────────────────────────
// Only used in wallpaper-active (dashboard) mode. Fires every ~18 s
// and quietly re-frames the galaxy without disturbing an in-flight
// pulse-follow (the brief mode toggle still respects FOLLOW_IDLE_RETURN
// so a fresh pulse interrupts the fit). Idempotent — re-calling
// _startDashAutoFitLoop() just resets the interval.
let _dashAutoFitTimer = null;
function _startDashAutoFitLoop() {
    if (_dashAutoFitTimer) clearInterval(_dashAutoFitTimer);
    _dashAutoFitTimer = setInterval(() => {
        if (!scene) return;
        if (!IS_WALLPAPER_ACTIVE) { clearInterval(_dashAutoFitTimer); _dashAutoFitTimer = null; return; }
        // Exit follow → fit → re-enter follow. The mode toggle is what
        // refreshes the follow "home" pose so idle-return after the next
        // MCP pulse lands at the new fitted view rather than a stale one.
        scene.setCameraMode?.('free');
        scene.fitToScreen?.({ duration: 0.9, keepDirection: false });
        setTimeout(() => scene?.setCameraMode?.('follow'), 1000);
    }, 18000);
}

// ── settings (Glow / Stars / Motion) ─────────────────────────────────
function loadSettings() {
    try {
        // When running in any wallpaper-related context (the setup window,
        // a finalized wallpaper clone, OR the dashboard preview tagged
        // ?mode=wallpaper-active), pull settings from the SAME wallpaper
        // prefs that the wallpaper itself writes to. Without this the
        // dashboard read SETTINGS_KEY (main-app settings) and looked
        // disconnected from the wallpaper — even though saveSettings was
        // already routing writes here. This makes the dashboard "look like
        // the wallpaper" per user spec.
        let raw = null;
        if (window.location.search.includes('mode=wallpaper')) {
            const prefs = loadWallpaperPrefs();
            if (prefs && prefs.settings) raw = JSON.stringify(prefs.settings);
        }
        if (!raw) raw = localStorage.getItem(SETTINGS_KEY);
        if (!raw) return { ...DEFAULT_SETTINGS, looks: {} };
        const parsed = JSON.parse(raw);
        const live = readLook(parsed, DEFAULT_SETTINGS);
        const looks = {};
        if (parsed.looks && typeof parsed.looks === 'object') {
            for (const t of ['universe', 'brain']) {
                const l = parsed.looks[t];
                if (l && typeof l === 'object') looks[t] = readLook(l, live);
            }
        }
        return {
            ...live,
            theme: (parsed.theme === 'brain' || parsed.theme === 'universe')
                ? parsed.theme : DEFAULT_SETTINGS.theme,
            looks,
        };
    } catch {
        return { ...DEFAULT_SETTINGS, looks: {} };
    }
}

function saveSettings(s) {
    try {
        // CRITICAL: wallpaper-setup and finalized wallpaper instances live
        // in their OWN WebView2 contexts with the same JS module. Writing
        // to the main app's SETTINGS_KEY (obsidianx.universe.settings.v3)
        // from a wallpaper context CLOBBERS the user's main-app settings
        // every time they tweak a slider or pick a camera mode during
        // setup. That's the "wallpaper config keeps overwriting my main
        // app config" complaint.
        //
        // Fix: in any wallpaper-related URL (`?mode=wallpaper-setup` for
        // the interactive setup, `?mode=wallpaper` for finalized clones),
        // route settings writes into the wallpaper prefs object
        // (obsidianx.wallpaper.prefs.v2) instead. This keeps the two
        // configurations fully independent — wallpaper's camera mode /
        // sliders / background never bleed into the main app's UI and
        // vice-versa.
        if (window.location.search.includes('mode=wallpaper')) {
            const existing = loadWallpaperPrefs() || {};
            existing.settings = s;
            saveWallpaperPrefs(existing);
        } else {
            localStorage.setItem(SETTINGS_KEY, JSON.stringify(s));
        }
    } catch { /* private mode / quota — silently drop */ }
}

function applySettingsToUI(s) {
    if ($setGlow)   { $setGlow.value   = s.glow;   $setGlowV.textContent   = s.glow.toFixed(2); }
    if ($setStars)  { $setStars.value  = s.stars;  $setStarsV.textContent  = s.stars.toFixed(2); }
    if ($setSky)    { $setSky.value    = s.sky;    $setSkyV.textContent    = s.sky.toFixed(2); }
    if ($setSize)   { $setSize.value   = s.size;   $setSizeV.textContent   = s.size.toFixed(2); }
    if ($setEdges)  { $setEdges.value  = s.edges;  $setEdgesV.textContent  = s.edges.toFixed(2); }
    if ($setDrift)  { $setDrift.value  = s.drift;  $setDriftV.textContent  = s.drift.toFixed(2); }
    if ($setMotion) { $setMotion.value = s.motion; $setMotionV.textContent = s.motion.toFixed(2); }
    if ($setLightning)      { $setLightning.value      = s.lightning;      $setLightningV.textContent      = s.lightning.toFixed(2); }
    if ($setLightningSpeed) { $setLightningSpeed.value = s.lightningSpeed; $setLightningSpeedV.textContent = s.lightningSpeed.toFixed(2); }
    document.querySelectorAll('.cam-btn[data-bg]').forEach(b =>
        b.classList.toggle('active', b.dataset.bg === s.background));
    document.querySelectorAll('.cam-btn[data-lock]').forEach(b =>
        b.classList.toggle('active', (b.dataset.lock === 'on') === s.lockSelected));
    document.querySelectorAll('.cam-btn[data-legend]').forEach(b =>
        b.classList.toggle('active', (b.dataset.legend === 'on') === s.legendVisible));
    document.querySelectorAll('.cam-btn[data-mode]').forEach(b =>
        b.classList.toggle('active', b.dataset.mode === s.cameraMode));
    applyThemeToUI(s);
    applyLegendVisibility(s.legendVisible);
}

/**
 * The words on the panel follow the picture. Anything carrying
 * `data-label-brain` swaps to it in the brain theme and back to its original
 * text (remembered on first swap) in the universe — "Milky Way" is not a
 * thing a brain has, and a slider still named after one would be a slider
 * nobody can guess the meaning of.
 */
function applyThemeToUI(s) {
    const brain = s.theme === 'brain';
    document.querySelectorAll('.cam-btn[data-theme]').forEach(b =>
        b.classList.toggle('active', b.dataset.theme === s.theme));
    // The wallpaper setup bar's copy of the same choice.
    document.querySelectorAll('[data-wp-theme]').forEach(b =>
        b.classList.toggle('active', b.dataset.wpTheme === s.theme));
    document.querySelectorAll('.cam-btn[data-fiber]').forEach(b =>
        b.classList.toggle('active', b.dataset.fiber === s.fiberColor));
    const fiberRow = document.querySelector('.fiber-row');
    if (fiberRow) fiberRow.hidden = !brain;
    document.querySelectorAll('[data-label-brain]').forEach(el => {
        if (el.dataset.labelUniverse === undefined) el.dataset.labelUniverse = el.textContent;
        el.textContent = brain ? el.dataset.labelBrain : el.dataset.labelUniverse;
    });
    document.body.classList.toggle('theme-brain', brain);
}

// Track whether the legend has galaxies to display. The "Show" toggle is a
// no-op until renderLegend() has actually been called with a non-empty list.
let _legendHasRows = false;

function applyLegendVisibility(visible) {
    if (!$legend) return;
    // Only un-hide if there's something to show — otherwise we'd flash an
    // empty glass panel during the brain-fetch race.
    $legend.hidden = !visible || !_legendHasRows;
    if (visible && _legendHasRows) $legend.classList.add('fade-in');
}

function applyBackground(which) {
    // Drive BOTH the DOM body (so HUD glass + body bg behind canvas match)
    // AND the three.js renderer.setClearColor / nebula / starfield via scene.
    document.body.classList.toggle('bg-black',  which === 'black');
    document.body.classList.toggle('bg-nebula', which !== 'black');
    scene?.setBackground?.(which);
}

function applySettingsToScene(s) {
    // The HUD swaps its agent-bus picture off this, scene or no scene.
    publishTheme(s.theme);
    if (!scene) return;
    // Theme FIRST: switching rebuilds the scene from its last payload, and
    // every setter below then lands on the rebuilt materials.
    const switched = scene.setTheme?.(s.theme);
    scene.setFiberColor?.(s.fiberColor);
    scene.setGlow(s.glow);
    scene.setStars(s.stars);
    scene.setSky?.(s.sky);
    scene.setStarSize?.(s.size);
    scene.setEdgeAlpha?.(s.edges);
    scene.setDrift?.(s.drift);
    scene.setMotion(s.motion);
    // The dashboard (mode=wallpaper-active) inherits the desktop wallpaper's
    // prefs, but its MCP-activity pulse is a FUNCTIONAL signal ("Claude is
    // reading your brain") — it must stay visible even when the wallpaper's
    // DECORATIVE Lightning is dialed to 0. So floor it on the dashboard only;
    // the real desktop wallpaper keeps the user's chosen value. This is the
    // root-cause fix for "เอฟเฟค universe ที่กระพริบตอนทำงานหาย" — the dashboard
    // was inheriting wallpaper lightning=0 and silently swallowing every pulse.
    const _isDash = window.location.search.includes('mode=wallpaper-active');
    const _lightning = _isDash ? Math.max(s.lightning, 1.0) : s.lightning;
    scene.setLightning?.(_lightning, s.lightningSpeed > 0 ? s.lightningSpeed : 1.0);
    scene.setLockSelected?.(s.lockSelected);
    scene.setCameraMode?.(s.cameraMode);
    applyBackground(s.background);
    if (switched) {
        postThemeState(s.theme);
        if (_lastGalaxies?.length) setStatus(renderedStatus(_lastUniverse));
        // The rebuild drops the islands highlight with everything else, but
        // the button is still lit — carry the owner's choice across instead of
        // leaving a control that says ON over a picture that is not.
        if ($setIslands?.classList.contains('active')) scene.toggleIslands?.(true);
    }
}

/**
 * Span wallpaper: pin the HUD to the one screen its cards were given, inside
 * a window that covers several. `r` is that screen's rect in PHYSICAL pixels
 * relative to the window — what the host measures — so it is divided back to
 * CSS pixels here. Null returns the HUD to the whole window.
 */
function applyHudRect(r) {
    const layer = document.getElementById('hud-layer');
    if (!layer) return;
    const props = ['left', 'top', 'width', 'height'];
    if (!r || !(r.width > 0) || !(r.height > 0)) {
        layer.classList.remove('hud-layer-pinned');
        props.forEach(p => layer.style.removeProperty(p));
        return;
    }
    const dpr = window.devicePixelRatio || 1;
    layer.classList.add('hud-layer-pinned');
    for (const p of props) layer.style.setProperty(p, (Number(r[p]) || 0) / dpr + 'px');
}

/** Tell the host which theme is showing, so the WPF pill over the view can
 *  light the right half. Sent on every switch and once at start-up. */
function postThemeState(theme) {
    postToHost({ type: 'themeState', theme: theme === 'brain' ? 'brain' : 'universe' });
}

/** One setter for every way a theme can be chosen — the settings row, the WPF
 *  pill, a URL — so all of them persist, redraw and report identically. */
function chooseTheme(theme) {
    const t = theme === 'brain' ? 'brain' : 'universe';
    if (t === currentSettings.theme) { postThemeState(t); return; }
    currentSettings = switchLook(currentSettings, t);
    // A camera the URL pinned stays pinned whichever theme's look comes in.
    if (_URL_CAMERA_OVERRIDE) currentSettings = { ...currentSettings, cameraMode: _URL_CAMERA_OVERRIDE };
    applySettingsToUI(currentSettings);
    applySettingsToScene(currentSettings);
    saveSettings(currentSettings);
}

let currentSettings = loadSettings();

// URL override: ?cameraMode=orbit (or free/follow/random) pins the camera
// regardless of saved/inherited settings. The dashboard preview uses
// `?cameraMode=orbit&mode=wallpaper-active` to inherit the wallpaper's
// APPEARANCE (glow/lightning/background/sliders) while keeping its own
// independent orbiting camera per user spec ("Same look, independent
// camera"). Without honoring the URL override, the dashboard would also
// inherit the wallpaper's camera mode (e.g. follow/random) and the user
// couldn't pin it.
const _URL_CAMERA_OVERRIDE = (() => {
    const m = new URLSearchParams(location.search).get('cameraMode');
    return (m && ['free','orbit','follow','random'].includes(m)) ? m : null;
})();
if (_URL_CAMERA_OVERRIDE) {
    currentSettings = { ...currentSettings, cameraMode: _URL_CAMERA_OVERRIDE };
}

// ?theme=brain / ?theme=universe pins the picture the same way — for a host
// embed that wants one look regardless of what was saved, and for opening the
// page standalone straight into either theme.
const _URL_THEME_OVERRIDE = (() => {
    const t = new URLSearchParams(location.search).get('theme');
    return (t === 'brain' || t === 'universe') ? t : null;
})();
if (_URL_THEME_OVERRIDE) {
    // With that theme's own look — then the camera pin above, again, since
    // the look just brought its own camera mode.
    currentSettings = switchLook(currentSettings, _URL_THEME_OVERRIDE);
    if (_URL_CAMERA_OVERRIDE) currentSettings = { ...currentSettings, cameraMode: _URL_CAMERA_OVERRIDE };
}
// Published at module load, before hud.js initialises, so the agent bus is
// built as the right picture the first time rather than swapped a beat later.
publishTheme(currentSettings.theme);

// Cross-WebView live sync: when the wallpaper (setup window OR finalized
// clone) saves new appearance settings, WebView2 instances sharing the
// same UserDataFolder + origin fire a `storage` event in every OTHER
// browsing context. The dashboard preview rides this to re-apply settings
// live — no polling, no host round-trip — so wallpaper tweaks show up in
// the dashboard immediately.
//
// We preserve `cameraMode` from the URL override (if any) so the
// dashboard's independent camera doesn't get yanked back to whatever the
// wallpaper just saved.
if (window.location.search.includes('mode=wallpaper-active')) {
    window.addEventListener('storage', (ev) => {
        if (ev.key !== WALLPAPER_PREFS_KEY) return;
        const prefs = loadWallpaperPrefs();
        if (!prefs || !prefs.settings) return;
        const keepCamera = _URL_CAMERA_OVERRIDE || currentSettings.cameraMode;
        let next = adoptSettings(currentSettings, prefs.settings);
        // A pinned theme takes the wallpaper's tuning OF THAT THEME, not the
        // flat values of whichever theme the wallpaper happens to show.
        if (_URL_THEME_OVERRIDE) next = switchLook(next, _URL_THEME_OVERRIDE);
        currentSettings = { ...next, cameraMode: keepCamera };
        try { applySettingsToUI(currentSettings); } catch {}
        try { applySettingsToScene(currentSettings); } catch {}
    });
}

function wireSettingsPanel() {
    if (!$settingsToggle || !$settingsPanel) return;
    $settingsToggle.addEventListener('click', () => {
        $settingsPanel.hidden = !$settingsPanel.hidden;
        if (!$settingsPanel.hidden) $settingsPanel.classList.add('fade-in');
    });

    const onSlide = (which, slider, valEl) => () => {
        const v = parseFloat(slider.value);
        valEl.textContent = v.toFixed(2);
        currentSettings = { ...currentSettings, [which]: v };
        applySettingsToScene(currentSettings);
        saveSettings(currentSettings);
    };
    $setGlow  ?.addEventListener('input', onSlide('glow',   $setGlow,   $setGlowV));
    $setStars ?.addEventListener('input', onSlide('stars',  $setStars,  $setStarsV));
    $setSky   ?.addEventListener('input', onSlide('sky',    $setSky,    $setSkyV));
    $setSize  ?.addEventListener('input', onSlide('size',   $setSize,   $setSizeV));
    $setEdges ?.addEventListener('input', onSlide('edges',  $setEdges,  $setEdgesV));
    $setDrift ?.addEventListener('input', onSlide('drift',  $setDrift,  $setDriftV));
    $setMotion?.addEventListener('input', onSlide('motion', $setMotion, $setMotionV));
    $setLightning?.addEventListener('input',
        onSlide('lightning', $setLightning, $setLightningV));
    $setLightningSpeed?.addEventListener('input',
        onSlide('lightningSpeed', $setLightningSpeed, $setLightningSpeedV));

    $setReset?.addEventListener('click', () => {
        // Reset is "put the sliders back", not "change what I am looking at":
        // the theme and its fibre colouring survive it, and so does the
        // other theme's tuning — Reset belongs to the theme on screen.
        currentSettings = { ...currentSettings, ...lookOf(DEFAULT_SETTINGS), fiberColor: currentSettings.fiberColor };
        applySettingsToUI(currentSettings);
        applySettingsToScene(currentSettings);
        saveSettings(currentSettings);
    });

    // Replays the per-galaxy d3-force assembly animation. Pumps alpha
    // back to 1 so the simulations heat up and settle again over ~3.5 s.
    // After ~4 s (sim is settled), auto-fit to reframe the new layout.
    $setResettle?.addEventListener('click', () => {
        scene?.resettle?.();
        setTimeout(() => scene?.fitToScreen?.(), 4000);
    });

    // Fit: zoom + recentre so every star sits inside the viewport.
    // Uses live (post-physics) positions and the current aspect ratio.
    $setFit?.addEventListener('click', () => {
        scene?.fitToScreen?.();
        setStatus('Fit · reframed to current node positions');
    });

    // (Import Obsidian moved out of Universe Settings to the main app's
    // "Import / Scan" nav tab — see MainWindow.xaml's ImportView. The
    // host-side `importObsidianVault` message handler is still wired in
    // case any other code path posts it, but the in-Universe button is
    // gone.)

    // Toggle WPF host fullscreen (covers the Windows taskbar). The C# side
    // owns window bounds; we just nudge it via the existing message bridge.
    $setFullscreen?.addEventListener('click', () => {
        postToHost({ type: 'toggleFullscreen' });
    });

    // Show Case = hide all chrome (sidebar, title, status) — Universe only.
    $setShowCase?.addEventListener('click', () => {
        postToHost({ type: 'toggleShowCase' });
    });

    // Wallpaper = pin window behind desktop icons via WorkerW (multi-monitor).
    // Console log + visible status so the user sees the click landed even if
    // the WorkerW reparenting itself silently fails on some Windows builds.
    $setWallpaper?.addEventListener('click', () => {
        console.log('[Universe] wallpaper button clicked — posting toggleWallpaper');
        setStatus('Wallpaper toggle sent to host…');
        postToHost({ type: 'toggleWallpaper' });
    });

    // Islands = dim main galaxy, brighten orphans/small clusters. Pure
    // visual diagnostic — answers "which notes haven't been linked yet?".
    // Toggles the .active class so the user can see whether highlight is on.
    let islandsState = false;
    $setIslands?.addEventListener('click', () => {
        islandsState = !islandsState;
        $setIslands.classList.toggle('active', islandsState);
        const stats = scene?.toggleIslands?.(islandsState);
        if (!stats) return;
        if (islandsState) {
            const detached = stats.totalComponents - 1;
            setStatus(
                `Islands ON · ${stats.totalComponents} components · ` +
                `main=${stats.mainSize} stars · ${stats.islandCount} small clusters · ` +
                `${stats.loneCount} lone stars`
            );
        } else {
            setStatus(`Islands OFF · main galaxy restored (${stats.mainSize} stars)`);
        }
    });

    // Camera-mode picker (Free / Orbit / Follow / Random). Persisted so a
    // user who left wallpaper on Random doesn't lose the mode across
    // restarts. scene.js owns the auto-drive timer.
    document.querySelectorAll('.cam-btn[data-mode]').forEach(btn => {
        btn.addEventListener('click', () => {
            const mode = btn.dataset.mode;
            document.querySelectorAll('.cam-btn[data-mode]').forEach(b =>
                b.classList.toggle('active', b === btn));
            currentSettings = { ...currentSettings, cameraMode: mode };
            scene?.setCameraMode?.(mode);
            saveSettings(currentSettings);
        });
    });

    // Theme: universe ↔ neural brain. Both are this same scene, so unlike the
    // 2D graph below nothing leaves the WebView — scene.setTheme rebuilds the
    // picture from the payload it already holds.
    document.querySelectorAll('.cam-btn[data-theme]').forEach(btn => {
        btn.addEventListener('click', () => chooseTheme(btn.dataset.theme));
    });

    // Brain fibres: category colours or tractography (DTI) direction colours.
    document.querySelectorAll('.cam-btn[data-fiber]').forEach(btn => {
        btn.addEventListener('click', () => {
            currentSettings = { ...currentSettings, fiberColor: btn.dataset.fiber === 'dti' ? 'dti' : 'category' };
            applyThemeToUI(currentSettings);
            scene?.setFiberColor?.(currentSettings.fiberColor);
            saveSettings(currentSettings);
        });
    });

    // View toggle: 3D Universe (WebView2 default) ↔ 2D Graph (WPF Graph2DRenderer).
    // RETIRED from the panel (its row is `hidden` in index.html) — the brain
    // theme replaced it as the second way to see the graph. Still wired, so
    // un-hiding the row is the whole rollback.
    // The 2D renderer lives on the WPF side, so JS just posts; C# flips
    // visibility of the WebView vs the embedded Graph2DRenderer.
    document.querySelectorAll('.cam-btn[data-view]').forEach(btn => {
        btn.addEventListener('click', () => {
            const view = btn.dataset.view;
            document.querySelectorAll('.cam-btn[data-view]').forEach(b =>
                b.classList.toggle('active', b === btn));
            postToHost({ type: 'switchView', view });
        });
    });

    // Background picker: nebula gradient (default) vs pure black.
    document.querySelectorAll('.cam-btn[data-bg]').forEach(btn => {
        btn.addEventListener('click', () => {
            const bg = btn.dataset.bg;
            document.querySelectorAll('.cam-btn[data-bg]').forEach(b =>
                b.classList.toggle('active', b === btn));
            currentSettings = { ...currentSettings, background: bg };
            applyBackground(bg);
            saveSettings(currentSettings);
        });
    });

    // Expertise legend toggle: show/hide the right-side galaxy panel.
    // Persisted in settings so it survives reload. Doesn't re-render the
    // rows on toggle — just flips $legend.hidden via applyLegendVisibility.
    document.querySelectorAll('.cam-btn[data-legend]').forEach(btn => {
        btn.addEventListener('click', () => {
            const visible = btn.dataset.legend === 'on';
            document.querySelectorAll('.cam-btn[data-legend]').forEach(b =>
                b.classList.toggle('active', b === btn));
            currentSettings = { ...currentSettings, legendVisible: visible };
            applyLegendVisibility(visible);
            saveSettings(currentSettings);
        });
    });

    // Lock toggle: keep the selected star at screen centre even as the
    // universe rotates / drifts. Default ON.
    document.querySelectorAll('.cam-btn[data-lock]').forEach(btn => {
        btn.addEventListener('click', () => {
            const on = btn.dataset.lock === 'on';
            document.querySelectorAll('.cam-btn[data-lock]').forEach(b =>
                b.classList.toggle('active', b === btn));
            currentSettings = { ...currentSettings, lockSelected: on };
            scene?.setLockSelected?.(on);
            saveSettings(currentSettings);
        });
    });
}

// ── boot ─────────────────────────────────────────────────────────────
// Wallpaper has TWO modes:
//   setup    — fullscreen child window, user configures camera + settings
//              interactively then clicks Apply. HUD visible, mouse enabled.
//   wallpaper — final state. SetParent'd to WorkerW, HUD hidden, mouse disabled.
//
// C# spawns in 'setup' first; when user clicks Apply, JS posts wallpaperApply
// → C# reparents + JS adds wallpaper-mode class. Cancel → C# closes window.
const URL_MODE = new URLSearchParams(location.search).get('mode');
const IS_WALLPAPER_SETUP  = URL_MODE === 'wallpaper-setup';
const IS_WALLPAPER_MODE   = URL_MODE === 'wallpaper';
// `wallpaper-active` is the dashboard preview: visually identical to
// wallpaper-mode (no HUD, no settings panel, no info-card on hover) but
// keeps mouse interaction so the user can still drag/zoom/orbit. Settings
// can't be edited (settings panel never wires up) — matches user spec
// "ไม่มีตัวหนังสือบัง ตั้งค่าก็ไม่ได้ แต่ลากเลื่อนได้ด้วยเม้า".
const IS_WALLPAPER_ACTIVE = URL_MODE === 'wallpaper-active';
if (IS_WALLPAPER_SETUP)  document.body.classList.add('wallpaper-setup');
if (IS_WALLPAPER_MODE)   document.body.classList.add('wallpaper-mode');
if (IS_WALLPAPER_ACTIVE) document.body.classList.add('wallpaper-active');

async function init() {
    applyCanvasSize();
    handleResize();
    window.addEventListener('resize', handleResize);

    setStatus('Building scene…');

    try {
        scene = createScene($canvas, {
            onHover: payload => {
                $canvas.classList.toggle('hover-star', payload != null);
            },
            onSelect: payload => {
                showInfo(payload);
            },
            onGalaxies: galaxies => {
                _lastGalaxies = galaxies;
                renderLegend(galaxies);
            },
            onWalk: stats => {
                if (!stats) return;
                // perHop[0] is the seed itself; per-layer counts read more
                // naturally as "1-hop:N · 2-hop:M".
                const layers = stats.perHop.slice(1)
                    .map((n, i) => `${i + 1}-hop:${n}`).join(' · ');
                setStatus(
                    `Walking from "${stats.startTitle}" · ${stats.totalReached} stars · ${layers || '(isolated — no neighbours)'}`
                );
            }
        });
    } catch (err) {
        console.error('[Universe] scene init failed', err);
        setStatus(`three.js failed to load: ${err.message}. Vendored files missing under universe/vendor/ — reinstall BrainX.`, true);
        return;
    }

    // Demo-only console handle, same idea as hud.js's __hudBus: the picture can
    // be checked by numbers from a plain browser. Never exposed in the host.
    if (new URLSearchParams(location.search).get('hudDemo') === '1') window.__scene = scene;

    // restore persisted UI + push to scene so the user's last preference
    // (e.g. low glow) survives a reload.
    applySettingsToUI(currentSettings);
    applySettingsToScene(currentSettings);
    // Wallpaper mode (final state)   = no HUD, no input → skip both.
    // Wallpaper-active (dash preview) = no HUD but mouse drag still works
    //   → skip wireSettingsPanel + wireInfoCard so no text overlays/tooltips,
    //   but OrbitControls in scene.js still handle pointer events directly.
    // Wallpaper setup = full interactivity + extra Apply/Cancel/Randomize bar.
    if (!IS_WALLPAPER_MODE && !IS_WALLPAPER_ACTIVE) {
        wireSettingsPanel();
        wireInfoCard();
    }
    if (IS_WALLPAPER_SETUP) {
        wireWallpaperSetup();
    }

    // pointerdown/up flip the canvas cursor so grab/grabbing reads correctly
    // through OrbitControls (which doesn't manage cursor itself).
    $canvas.addEventListener('pointerdown', () => $canvas.classList.add('dragging'));
    window.addEventListener('pointerup', () => $canvas.classList.remove('dragging'));

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', onHostMessage);
        setStatus('Waiting for brain snapshot…');
        postToHost({ type: 'ready' });
        // The pill over the view starts out knowing which half to light.
        postThemeState(currentSettings.theme);
    } else {
        // Standalone preview path: try fetching the JSON directly so devs can
        // open index.html from a static server (e.g. `npx serve`).
        await tryStandaloneFetch();
    }
}

async function tryStandaloneFetch() {
    setStatus('Standalone mode — fetching brain-export.json…');
    try {
        const res = await fetch('../../../.obsidianx/brain-export.json');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const brain = await res.json();
        handleBrain(brain);
    } catch (err) {
        setStatus('Open this page from inside BrainX (WebView2 host required).', true);
        console.warn('[Universe] standalone fetch failed:', err);
    }
}

// Debounce auto-fit so dragging the window edge doesn't fire a flyTo on
// every pixel. 250 ms = long enough that the user has stopped resizing,
// short enough that the cluster snaps into frame before they let go.
let _autoFitTimer = null;
function scheduleAutoFit() {
    if (_autoFitTimer) clearTimeout(_autoFitTimer);
    _autoFitTimer = setTimeout(() => {
        _autoFitTimer = null;
        // Wallpaper-mode is read-only — no camera moves while it's the
        // desktop background. Also skip if the user is actively using
        // the wallpaper-setup window (they're framing their own shot).
        if (IS_WALLPAPER_MODE) return;
        scene?.fitToScreen?.({ duration: 0.45, keepDirection: true });
    }, 250);
}

function handleResize() {
    applyCanvasSize();
    if ($size) {
        const { w, h, dpr } = getViewport();
        $size.textContent = `${w} × ${h} @ ${dpr}×`;
    }
    if (scene) scene.setSize(window.innerWidth, window.innerHeight);
    // Keep the universe framed across window-size changes — otherwise a
    // wider window leaves dead space and a narrower one crops galaxies.
    scheduleAutoFit();
}

// kick off — DOMContentLoaded guard for the rare case the script lands
// before the DOM is parsed (importmap can shuffle ordering).
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
} else {
    init();
}

// Expose a tiny host API for future Phase 2 messages from C#.
window.UniverseHost = {
    viewport: getViewport,
    canvas: () => $canvas,
    refresh: () => pendingBrain && handleBrain(pendingBrain)
};

// ─── Dashboard floating chips ─────────────────────────────────────
// Lives in the DOM (not WPF Border) so the chips share the WebView2
// DComp surface — no HwndHost airspace bleed-through, no Popup z-order
// hacks. C# pushes values via PostWebMessageAsJson:
//   { type: "dashStats", nodes: N,    words: M }     once per index
//   { type: "dashLoad",  gpu:   33.4, cpu:   37.2 }  every 1.5 s
const DASH_SPARK_CAP = 60;
const _dashGpuRing = [];
const _dashCpuRing = [];

function fmtBig(n) {
    if (n == null) return '0';
    n = +n;
    if (n >= 1e9) return (n / 1e9).toFixed(1) + 'B';
    if (n >= 1e6) return (n / 1e6).toFixed(1) + 'M';
    if (n >= 1e4) return (n / 1e3).toFixed(0) + 'k';
    return n.toLocaleString('en-US');
}

function handleDashStats(msg) {
    const nodes = document.getElementById('dash-nodes-value');
    const words = document.getElementById('dash-words-value');
    if (nodes && typeof msg.nodes === 'number') nodes.textContent = msg.nodes.toLocaleString('en-US');
    if (words && typeof msg.words === 'number') words.textContent = fmtBig(msg.words);
}

function handleDashLoad(msg) {
    const gpu = typeof msg.gpu === 'number' ? Math.max(0, Math.min(100, msg.gpu)) : null;
    const cpu = typeof msg.cpu === 'number' ? Math.max(0, Math.min(100, msg.cpu)) : null;
    if (gpu != null) {
        _dashGpuRing.push(gpu);
        if (_dashGpuRing.length > DASH_SPARK_CAP) _dashGpuRing.shift();
        const v = document.getElementById('dash-gpu-value');
        if (v) v.textContent = Math.round(gpu) + '%';
        drawDashSpark('gpu', _dashGpuRing);
    }
    if (cpu != null) {
        _dashCpuRing.push(cpu);
        if (_dashCpuRing.length > DASH_SPARK_CAP) _dashCpuRing.shift();
        const v = document.getElementById('dash-cpu-value');
        if (v) v.textContent = Math.round(cpu) + '%';
        drawDashSpark('cpu', _dashCpuRing);
    }
    // "Hot" dot when either is >85 %.
    const dot = document.getElementById('dash-load-dot');
    if (dot) {
        const hot = (gpu != null && gpu > 85) || (cpu != null && cpu > 85);
        dot.classList.toggle('hot', hot);
    }
}

// Catmull-Rom → cubic Bezier, tension 0.5. Produces SVG path "d"
// strings for the stroke layer + closed fill layer. ViewBox is 240×32
// with `preserveAspectRatio="none"`, so the path stretches to whatever
// width the chip ends up rendering at.
function drawDashSpark(which, samples) {
    const w = 240, h = 32;
    const stroke = document.getElementById('dash-spark-stroke-path-' + which);
    const fill   = document.getElementById('dash-spark-fill-path-'   + which);
    const halo   = document.getElementById('dash-spark-halo-' + which);
    const dot    = document.getElementById('dash-spark-dot-'  + which);
    if (!stroke || !fill) return;

    const n = samples.length;
    if (n === 0) { stroke.setAttribute('d', ''); fill.setAttribute('d', ''); return; }

    const pts = new Array(n);
    const slot = w / Math.max(1, DASH_SPARK_CAP - 1);
    for (let i = 0; i < n; i++) {
        const x = i * slot;
        const y = h - 2 - (samples[i] / 100) * (h - 3);
        pts[i] = { x, y };
    }

    // Build the smooth path.
    let d = `M ${pts[0].x.toFixed(2)} ${pts[0].y.toFixed(2)}`;
    for (let i = 0; i < n - 1; i++) {
        const p0 = pts[Math.max(0, i - 1)];
        const p1 = pts[i];
        const p2 = pts[i + 1];
        const p3 = pts[Math.min(n - 1, i + 2)];
        const c1x = p1.x + (p2.x - p0.x) / 6;
        const c1y = p1.y + (p2.y - p0.y) / 6;
        const c2x = p2.x - (p3.x - p1.x) / 6;
        const c2y = p2.y - (p3.y - p1.y) / 6;
        d += ` C ${c1x.toFixed(2)} ${c1y.toFixed(2)}, ${c2x.toFixed(2)} ${c2y.toFixed(2)}, ${p2.x.toFixed(2)} ${p2.y.toFixed(2)}`;
    }
    stroke.setAttribute('d', d);
    fill.setAttribute('d', d + ` L ${pts[n - 1].x.toFixed(2)} ${h} L 0 ${h} Z`);

    // End-cap halo + dot.
    if (halo && dot) {
        const last = pts[n - 1];
        halo.setAttribute('cx', last.x.toFixed(2));
        halo.setAttribute('cy', last.y.toFixed(2));
        dot.setAttribute('cx', last.x.toFixed(2));
        dot.setAttribute('cy', last.y.toFixed(2));
    }
}
