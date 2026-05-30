// BrainX Universe — entry point.
//
// Wires the WebView2 host bridge (C# ↔ JS) to the three.js scene module.
// All DOM panels (info card, legend, status) live here; scene.js stays
// agnostic of the surrounding chrome.

import { createScene } from './scene.js';

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
const $setSize   = document.getElementById('set-size');
const $setEdges  = document.getElementById('set-edges');
const $setDrift  = document.getElementById('set-drift');
const $setMotion = document.getElementById('set-motion');
const $setLightning      = document.getElementById('set-lightning');
const $setLightningSpeed = document.getElementById('set-lightning-speed');
const $setGlowV   = document.getElementById('set-glow-val');
const $setStarsV  = document.getElementById('set-stars-val');
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
// Per-monitor saved prefs in Separate mode. Shape: { 0: {settings, camera}, ... }
// Persisted as part of the prefs object on every edit.
let _wpMonitorPrefs = {};

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
    size: 1.0, edges: 1.0, drift: 0.0,
    lightning: 1.0,         // 0 = disable pulse flash, 1 = default, 2 = blinding
    lightningSpeed: 1.0,    // 0.5 = slow majestic strike, 2 = frantic flicker
    background: 'nebula',   // 'nebula' | 'black'
    lockSelected: true,     // true = clicked star sticks to screen centre
    legendVisible: true,    // true = show galaxy/expertise legend on the right
    cameraMode: 'free'      // 'free' | 'orbit' | 'follow' | 'random'
};

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
            </div>
        `;
        row.addEventListener('click', () => scene.focusGalaxy(g.category));
        $legendRows.appendChild(row);
    }
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
            currentSettings = { ...currentSettings, ...saved.settings };
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
    }
    applyLayoutToUI(_wpLayout);

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
            const heavy = (mode === 'mirror' || mode === 'separate') && _wpMonitorCount > 1;
            $wpResourceWarn.hidden = !heavy;
            if (heavy && $wpResourceWarnText) {
                const procs = _wpMonitorCount;
                $wpResourceWarnText.textContent =
                    `Heavy: ${procs}×~250 MB RAM + ${procs} GPU contexts. Span uses only 1.`;
            }
        }
        // Per-monitor selector — Separate mode only, multi-monitor only.
        const showMonitorBar = (mode === 'separate' && _wpMonitorCount > 1);
        if ($wpMonitorBar) $wpMonitorBar.hidden = !showMonitorBar;
        if (showMonitorBar) renderMonitorChips();
    }

    // Wire all three layout chips. Switching mode rebuilds the per-monitor
    // chip bar visibility but does NOT mutate per-monitor saved prefs.
    $wpLayoutSpan?.addEventListener('click', () => {
        if (_wpLayout === 'separate') flushEditingMonitorPrefs();
        _wpLayout = 'span';
        applyLayoutToUI(_wpLayout);
    });
    $wpLayoutMirror?.addEventListener('click', () => {
        if (_wpLayout === 'separate') flushEditingMonitorPrefs();
        _wpLayout = 'mirror';
        applyLayoutToUI(_wpLayout);
    });
    $wpLayoutSeparate?.addEventListener('click', () => {
        _wpLayout = 'separate';
        applyLayoutToUI(_wpLayout);
        // Entering Separate: ensure the currently-edited monitor's slot has
        // SOMETHING in it (the current live settings/camera) so the chip
        // looks "filled". Other slots seed from live values lazily on click.
        flushEditingMonitorPrefs();
    });

    // Host reports monitor count after wallpaper-setup boots. Re-run
    // applyLayoutToUI so the warning + chip row react to the actual count.
    window.addEventListener('wpMonitorCountChanged', () => applyLayoutToUI(_wpLayout));

    // Persist the currently-shown live settings/camera into the slot for
    // the monitor currently being edited (Separate mode only). Called
    // when the user switches monitors so edits don't get lost.
    function flushEditingMonitorPrefs() {
        if (_wpLayout !== 'separate') return;
        const cam = scene?.snapshotCamera?.() ?? null;
        _wpMonitorPrefs[_wpEditingMonitor] = {
            settings: { ...currentSettings },
            camera:   cam,
        };
    }

    // Build the per-monitor chip row. One chip per detected monitor,
    // labelled "1 (primary)", "2", "3", ... The active chip is the one
    // currently being edited in the preview window.
    function renderMonitorChips() {
        if (!$wpMonitorChips) return;
        $wpMonitorChips.innerHTML = '';
        for (let i = 0; i < _wpMonitorCount; i++) {
            const chip = document.createElement('button');
            chip.type = 'button';
            chip.className = 'wp-mon-chip' + (i === _wpEditingMonitor ? ' active' : '');
            chip.dataset.idx = String(i);
            chip.textContent = (i === 0) ? `${i + 1} (primary)` : String(i + 1);
            chip.title = `Edit monitor ${i + 1}'s wallpaper setup`;
            chip.addEventListener('click', () => switchEditingMonitor(i));
            $wpMonitorChips.appendChild(chip);
        }
    }

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
                currentSettings = { ...currentSettings, ...slot.settings };
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

        // Bundle live settings/camera (= primary monitor's view for
        // Span/Mirror, or the currently-edited monitor for Separate)
        // PLUS the per-monitor prefs map (Separate only) for the host
        // to fan out to each clone.
        const cam = scene?.snapshotCamera?.() ?? null;
        const prefs = {
            settings: currentSettings,
            camera: cam,
            layout: _wpLayout,
            monitors: _wpMonitorPrefs,
        };
        saveWallpaperPrefs(prefs);
        setStatus('Applying wallpaper…');
        postToHost({
            type: 'wallpaperApply',
            layout: _wpLayout,
            // Host uses this map to push per-monitor settings to each
            // clone's WebView2 after they fire 'ready'. Empty in Span/Mirror.
            monitors: (_wpLayout === 'separate') ? _wpMonitorPrefs : null,
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
            }
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
            // C# forwards TokenSavingsTracker.Compute output as
            // { text: "💰 +12.3k tok", tooltip: "...multi-line..." }.
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
        case 'mirrorState':
            // Slave-side: master WebView2 broadcasts camera state to all
            // mirror-mode siblings via the host. Apply directly (no lerp)
            // so every monitor shows the same view within one frame.
            if (scene && msg.target && msg.position) {
                scene.applyMirrorState?.(msg.target, msg.position);
            }
            break;
        case 'applyMonitorSettings':
            // Per-monitor bootstrap (Separate mode only): host sends each
            // clone its monitor-specific settings + camera right after the
            // 'ready' handshake. Single-shot — just apply and forget.
            if (msg.settings) {
                currentSettings = { ...currentSettings, ...msg.settings };
                applySettingsToUI?.(currentSettings);
                applySettingsToScene?.(currentSettings);
                saveSettings?.(currentSettings);
            }
            if (msg.camera && scene?.restoreCamera) {
                requestAnimationFrame(() => scene.restoreCamera(msg.camera));
            }
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

function mountScene(brain) {
    try {
        const universe = scene.mount(brain);
        if (universe.nodes.length === 0) {
            setStatus('Brain has 0 notes yet — open BrainX and add some.');
        } else {
            setStatus(`Universe rendered · ${universe.nodes.length.toLocaleString()} stars · ${universe.edges.length.toLocaleString()} wiki-links · ${universe.galaxies.length} galaxies`);
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
        if (!raw) return { ...DEFAULT_SETTINGS };
        const parsed = JSON.parse(raw);
        const num = (v, def) => typeof v === 'number' ? v : def;
        return {
            glow:   num(parsed.glow,   DEFAULT_SETTINGS.glow),
            stars:  num(parsed.stars,  DEFAULT_SETTINGS.stars),
            motion: num(parsed.motion, DEFAULT_SETTINGS.motion),
            size:   num(parsed.size,   DEFAULT_SETTINGS.size),
            edges:  num(parsed.edges,  DEFAULT_SETTINGS.edges),
            drift:  num(parsed.drift,  DEFAULT_SETTINGS.drift),
            lightning:      num(parsed.lightning,      DEFAULT_SETTINGS.lightning),
            lightningSpeed: num(parsed.lightningSpeed, DEFAULT_SETTINGS.lightningSpeed),
            background: (parsed.background === 'black' || parsed.background === 'nebula')
                ? parsed.background : DEFAULT_SETTINGS.background,
            lockSelected: typeof parsed.lockSelected === 'boolean'
                ? parsed.lockSelected : DEFAULT_SETTINGS.lockSelected,
            legendVisible: typeof parsed.legendVisible === 'boolean'
                ? parsed.legendVisible : DEFAULT_SETTINGS.legendVisible,
            cameraMode: (['free','orbit','follow','random'].includes(parsed.cameraMode))
                ? parsed.cameraMode : DEFAULT_SETTINGS.cameraMode
        };
    } catch {
        return { ...DEFAULT_SETTINGS };
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
    applyLegendVisibility(s.legendVisible);
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
    if (!scene) return;
    scene.setGlow(s.glow);
    scene.setStars(s.stars);
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
        currentSettings = {
            ...currentSettings,
            ...prefs.settings,
            cameraMode: _URL_CAMERA_OVERRIDE || currentSettings.cameraMode
        };
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
    $setSize  ?.addEventListener('input', onSlide('size',   $setSize,   $setSizeV));
    $setEdges ?.addEventListener('input', onSlide('edges',  $setEdges,  $setEdgesV));
    $setDrift ?.addEventListener('input', onSlide('drift',  $setDrift,  $setDriftV));
    $setMotion?.addEventListener('input', onSlide('motion', $setMotion, $setMotionV));
    $setLightning?.addEventListener('input',
        onSlide('lightning', $setLightning, $setLightningV));
    $setLightningSpeed?.addEventListener('input',
        onSlide('lightningSpeed', $setLightningSpeed, $setLightningSpeedV));

    $setReset?.addEventListener('click', () => {
        currentSettings = { ...DEFAULT_SETTINGS };
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

    // View toggle: 3D Universe (WebView2 default) ↔ 2D Graph (WPF Graph2DRenderer).
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
        setStatus(`three.js failed to load: ${err.message}. Check internet connectivity (CDN: unpkg.com).`, true);
        return;
    }

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
