/* The code dashboard: what the graphs of this brain's repositories hold, and the
 * way to reach any symbol of them. #hud-top and #legend are hidden under the HUD,
 * so without this the only trace of a graph is a galaxy with no explanation.
 *
 * The sky draws a bounded slice of each graph (MainWindow.CodeUniverse.cs); this
 * panel asks the host about the whole of it, one question at a time, and the host
 * holds the graphs only while the panel is open. A flight into the sky closes the
 * panel, because the panel covers what the flight went to look at, and leaves a
 * pill to come back by.
 *
 * Every string from a repository or a note goes in as text, never as markup. */

const QS = new URLSearchParams(location.search);
const DEMO = QS.get('hudDemo') === '1';

const PROVENANCE = {
    EXTRACTED: { tag: 'EXT', say: 'Read from the compiler or syntax tree' },
    INFERRED:  { tag: 'INF', say: 'Placed by a name rule' },
    AMBIGUOUS: { tag: 'AMB', say: 'More than one candidate' },
};
const STATE_TEXT = {
    current: 'Up to date',
    behind: 'The checkout has moved past this graph',
    unknown: 'Commit unknown',
    gone: 'Folder not found',
};
const BUILD_DONE_NOTICE_MS = 6000;
const SEARCH_WAIT_MS = 180;

const S = {
    open: false,
    repos: [],
    selected: null,
    selectedKey: null,    // what the panel's content was read from
    overview: null,       // { repo, ...answer } | { repo, error }
    query: '',
    hits: null,
    detail: null,
    trail: [],            // symbols behind the one showing, for Back
    build: null,          // { repo, since }
};

let $root, $sum, $add, $notice, $repos, $search, $searchRow, $view, $back;
let _askSeq = 0, _noticeTimer = null, _searchTimer = null, _elapsedTimer = null;
const _waiting = new Map();
const _latest = {};       // slot → the ask whose answer still counts

function post(msg) {
    if (DEMO) { demoHost(msg); return; }
    try { window.chrome?.webview?.postMessage(msg); } catch { /* not hosted */ }
}

/** Ask the host. Resolves null when a newer ask took this slot. */
function ask(slot, repo, op, extra) {
    const id = ++_askSeq;
    _latest[slot] = id;
    return new Promise(resolve => {
        _waiting.set(id, answer => resolve(_latest[slot] === id ? answer : null));
        post({ type: 'codeQuery', id, repo, op, ...extra });
    });
}

function h(tag, attrs, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(attrs ?? {})) {
        if (v == null || v === false) continue;
        if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
        else if (k === 'class') el.className = v;
        else if (k === 'vars') for (const [name, val] of Object.entries(v)) el.style.setProperty(name, val);
        else el.setAttribute(k, v === true ? '' : v);
    }
    for (const kid of kids.flat()) if (kid != null && kid !== false) el.append(kid);
    return el;
}

const num = n => (n ?? 0).toLocaleString();
const sum = obj => Object.values(obj ?? {}).reduce((a, b) => a + b, 0);
const place = (file, line) => file ? (line ? `${file}:${line}` : file) : '';

function ago(iso) {
    const ms = Date.now() - new Date(iso).getTime();
    if (!Number.isFinite(ms)) return '';
    const min = Math.round(ms / 60000);
    if (min < 1) return 'just now';
    if (min < 60) return `${min} min ago`;
    const hours = Math.round(min / 60);
    if (hours < 48) return `${hours} h ago`;
    return `${Math.round(hours / 24)} d ago`;
}

function cardOf(repo) { return S.repos.find(r => r.repo === repo) ?? null; }
function keyOf(card) { return card ? `${card.builtAt}|${card.symbols}|${card.edges}` : null; }

// ── Open and close ──

function build() {
    $sum = h('span', { class: 'cd-sum' });
    $add = h('button', { class: 'cd-btn cd-primary', type: 'button', onclick: addRepo,
        title: 'Choose a folder. BrainX reads its code, and maps symbols, calls and imports.' }, '+ Add repo');
    $notice = h('div', { class: 'cd-notice', role: 'status', hidden: true });
    $repos = h('nav', { class: 'cd-repos', 'aria-label': 'Repositories' });
    $search = h('input', { class: 'cd-search', type: 'search', spellcheck: 'false', autocomplete: 'off',
        placeholder: 'Search symbols and files', 'aria-label': 'Search symbols and files', oninput: onSearchInput,
        onkeydown: e => { if (e.key === 'Enter') { clearTimeout(_searchTimer); runSearch(); } } });
    $searchRow = h('div', { class: 'cd-searchrow' }, $search);
    $view = h('div', { class: 'cd-view' });
    $root = h('aside', { id: 'code-dash', role: 'dialog', 'aria-label': 'Code graphs', hidden: true },
        h('header', { class: 'cd-head' },
            h('h2', {}, 'Code graphs'), $sum, h('span', { class: 'cd-grow' }), $add,
            h('button', { class: 'cd-btn cd-x', type: 'button', title: 'Close (Esc)', 'aria-label': 'Close', onclick: () => close() }, '×')),
        $notice,
        h('div', { class: 'cd-body' }, $repos, h('main', { class: 'cd-main' }, $searchRow, $view)));
    $back = h('div', { id: 'code-dash-back', hidden: true },
        h('button', { class: 'cd-back-open', type: 'button', onclick: () => { hideBack(); open(); } }),
        h('button', { class: 'cd-back-x', type: 'button', 'aria-label': 'Dismiss', onclick: hideBack }, '×'));
    document.body.append($root, $back);
    document.getElementById('code-dash-open')?.addEventListener('click', () => S.open ? close() : open());
    // Capture, ahead of the scene's own Escape, which would also throw the camera home.
    window.addEventListener('keydown', onKey, true);
}

function onKey(e) {
    if (!S.open || e.key !== 'Escape') return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (S.query) { $search.value = ''; setQuery(''); }
    else close();
}

function open() {
    hideBack();
    S.open = true;
    $root.hidden = false;
    post({ type: 'codeDashOpen' });
    if (!S.selected && S.repos.length) S.selected = S.repos[0].repo;
    render();
    reread();
    if (!$searchRow.hidden) $search.focus();
    syncElapsed();
}

function close() {
    S.open = false;
    $root.hidden = true;
    post({ type: 'codeDashClose' });
    syncElapsed();
}

function hideBack() { $back.hidden = true; }

function fly(target, label) {
    const went = window.UniverseHost?.flyToCode?.(target) ?? 'none';
    if (went === 'none') {
        notice(target.id && !target.category
            ? 'This note is not in the current view.'
            : 'Not drawn: the sky holds only what notes name and its near neighbours.', 'warn');
    } else {
        close();
        $back.firstChild.textContent = went === 'galaxy'
            ? `Not drawn on its own, showing ${target.category.slice(5)} · Back to Code`
            : `‹ Back to ${label}`;
        $back.hidden = false;
    }
}

// ── Asking ──

function reread() {
    const card = cardOf(S.selected);
    if (!card || card.error) return;
    S.selectedKey = keyOf(card);
    if (S.detail) loadSymbol(S.detail.symbol.id, true);
    else if (S.query) runSearch();
    else loadOverview();
}

async function loadOverview() {
    const repo = S.selected;
    if (S.overview?.repo !== repo) { S.overview = null; renderView(); }
    const a = await ask('view', repo, 'overview');
    if (!a || S.selected !== repo) return;
    S.overview = { ...a, repo };
    renderView();
}

async function loadSymbol(id, keepTrail) {
    const repo = S.selected;
    const from = S.detail;
    const a = await ask('view', repo, 'symbol', { symbol: id });
    if (!a || S.selected !== repo) return;
    if (a.error) { notice(a.error, 'warn'); if (keepTrail) { S.detail = null; render(); reread(); } return; }
    if (!keepTrail && from && from.symbol.id !== id) S.trail.push(from.symbol.id);
    S.detail = a;
    renderView();
    $view.scrollTop = 0;
}

function back() {
    const id = S.trail.pop();
    if (id) loadSymbol(id, true);
    else { S.detail = null; renderView(); if (S.query) runSearch(); else loadOverview(); }
}

function onSearchInput() {
    clearTimeout(_searchTimer);
    _searchTimer = setTimeout(() => setQuery($search.value.trim()), SEARCH_WAIT_MS);
}

function setQuery(q) {
    S.query = q;
    if (!q) { S.hits = null; _latest.view = 0; renderView(); if (!S.detail) loadOverview(); return; }
    runSearch();
}

async function runSearch() {
    S.query = $search.value.trim();
    if (!S.query) return;
    const repo = S.selected, q = S.query;
    S.detail = null;
    S.trail = [];
    const a = await ask('view', repo, 'search', { q });
    if (!a || S.selected !== repo) return;
    S.hits = a;
    renderView();
}

function select(repo) {
    if (repo === S.selected) { S.detail = null; S.trail = []; renderView(); reread(); return; }
    S.selected = repo;
    S.detail = null; S.trail = []; S.hits = null; S.query = '';
    $search.value = '';
    render();
    reread();
}

function addRepo() { post({ type: 'codeAddRepo' }); }

// ── Rendering ──

function notice(text, tone) {
    clearTimeout(_noticeTimer);
    $notice.textContent = text;
    $notice.className = `cd-notice ${tone ?? ''}`;
    $notice.hidden = false;
    if (tone === 'ok') _noticeTimer = setTimeout(() => { $notice.hidden = true; }, BUILD_DONE_NOTICE_MS);
}

function render() {
    if (!$root) return;
    const good = S.repos.filter(r => !r.error);
    $sum.textContent = S.repos.length
        ? `${S.repos.length} ${S.repos.length === 1 ? 'repo' : 'repos'} · ${num(good.reduce((n, r) => n + r.symbols, 0))} symbols · ${num(good.reduce((n, r) => n + r.edges, 0))} edges · ${num(good.reduce((n, r) => n + r.links, 0))} note links`
        : '';
    $add.disabled = !!S.build;
    renderRepos();
    renderView();
}

function renderRepos() {
    const ghost = S.build && !cardOf(S.build.repo) ? [{ repo: S.build.repo, fresh: true }] : [];
    const all = [...ghost, ...S.repos];
    $repos.hidden = all.length === 0;
    $repos.replaceChildren(...all.map(repoCard));
    syncElapsed();
}

function repoCard(r) {
    const busy = S.build?.repo === r.repo;
    const langs = Object.entries(r.languages ?? {}).sort((a, b) => b[1] - a[1]).slice(0, 3).map(([l]) => l).join(' · ');
    const stateName = r.error ? 'failed' : r.state ?? 'unknown';
    const imported = r.source === 'graphify';
    return h('article', { class: `cd-repo${r.repo === S.selected ? ' on' : ''}` },
        h('button', { class: 'cd-repo-pick', type: 'button', onclick: () => r.fresh ? null : select(r.repo) },
            h('span', { class: 'cd-repo-name' },
                h('span', { class: `cd-state ${stateName}`, title: r.error ?? STATE_TEXT[stateName], 'aria-hidden': 'true' }),
                r.repo),
            r.fresh ? h('span', { class: 'cd-repo-meta' }, 'First build')
                : r.error ? h('span', { class: 'cd-repo-meta' }, 'Could not be read')
                : [
                    h('span', { class: 'cd-repo-meta' }, `${num(r.symbols)} symbols · ${num(r.edges)} edges`),
                    h('span', { class: 'cd-repo-meta' }, [
                        r.commit ? r.commit.slice(0, 8) : null, r.builtAt ? ago(r.builtAt) : null, imported ? 'imported' : null,
                    ].filter(Boolean).join(' · ')),
                    langs ? h('span', { class: 'cd-repo-meta dim' }, langs) : null,
                ]),
        busy ? h('div', { class: 'cd-building' },
            h('div', { class: 'cd-indet' }, h('i')),
            h('div', { class: 'cd-buildrow' },
                h('span', {}, 'Building ', h('span', { class: 'cd-elapsed' }, '')),
                h('button', { class: 'cd-btn', type: 'button', onclick: () => post({ type: 'codeBuildStop' }),
                    title: 'Stop this build. The last saved graph stays.' }, '■ Stop')))
            : (!r.error && !imported && !r.fresh) ? h('div', { class: 'cd-repo-acts' },
                h('button', { class: 'cd-btn', type: 'button', disabled: !!S.build || r.state === 'gone',
                    onclick: () => post({ type: 'codeBuild', repo: r.repo, full: false }),
                    title: 'Read the files that changed since the last build.' }, 'Refresh'),
                h('button', { class: 'cd-btn', type: 'button', disabled: !!S.build || r.state === 'gone',
                    onclick: () => post({ type: 'codeBuild', repo: r.repo, full: true }),
                    title: 'Read every file again.' }, 'Full rebuild'))
            : null);
}

function syncElapsed() {
    const live = S.open && !!S.build;
    if (live && !_elapsedTimer) _elapsedTimer = setInterval(tickElapsed, 1000);
    if (!live && _elapsedTimer) { clearInterval(_elapsedTimer); _elapsedTimer = null; }
    if (live) tickElapsed();
}

function tickElapsed() {
    const s = Math.floor((Date.now() - (S.build?.since ?? Date.now())) / 1000);
    $repos.querySelector('.cd-elapsed')?.replaceChildren(s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${s % 60} s`);
}

function renderView() {
    const card = cardOf(S.selected);
    $searchRow.hidden = !card || !!card.error;
    if (!S.repos.length && !S.build) return $view.replaceChildren(emptyState());
    if (!card) return $view.replaceChildren(h('p', { class: 'cd-quiet' }, 'The first graph will show here when it is built.'));
    if (card.error) return $view.replaceChildren(h('p', { class: 'cd-quiet' }, card.error));
    if (S.detail) return $view.replaceChildren(...detailView(S.detail, card));
    if (S.query) return $view.replaceChildren(...hitsView());
    return $view.replaceChildren(...overviewView(card));
}

function emptyState() {
    return h('div', { class: 'cd-empty' },
        h('h3', {}, 'No code graphs yet'),
        h('p', {}, 'Add a repository folder and BrainX will map its symbols, calls, inheritance and imports, then link them to the notes that name them. Agents can then ask what calls what instead of reading files to find out.'),
        h('p', { class: 'dim' }, 'C#, JavaScript, TypeScript, Python and PHP. The first build of a large repo takes a few minutes.'),
        h('button', { class: 'cd-btn cd-primary', type: 'button', onclick: addRepo, disabled: !!S.build }, '+ Add repo'));
}

function section(title, ...kids) {
    return h('section', { class: 'cd-section' }, h('h3', {}, title), ...kids);
}

function overviewView(card) {
    const o = S.overview;
    const head = h('div', { class: 'cd-title' },
        h('h3', {}, card.repo),
        h('span', { class: `cd-statetext ${card.state ?? 'unknown'}` }, STATE_TEXT[card.state] ?? STATE_TEXT.unknown),
        card.root ? h('span', { class: 'cd-path' }, card.root) : null);
    const tiles = h('div', { class: 'cd-tiles' },
        tile(num(card.symbols), 'Symbols'), tile(num(card.edges), 'Edges'),
        tile(num(card.links), 'Note links'), tile(num(card.visible), 'Drawn in the sky',
            card.symbols ? { onclick: () => fly({ category: `code:${card.repo}` }, card.repo), title: 'Fly to this repo in the Universe' } : null));
    if (!o || o.repo !== card.repo) return [head, tiles, h('p', { class: 'cd-quiet' }, 'Reading graph…')];
    if (o.error) return [head, tiles, h('p', { class: 'cd-quiet' }, o.error)];

    const coverage = [
        ['Unresolved references', sum(o.unresolved), 'References no rule could place. Counted, never guessed.'],
        ['External references', sum(o.external), 'Framework and package code, outside this repo.'],
        ['Files changed since build', (o.files?.changed ?? 0) + (o.files?.missing ?? 0), (o.files?.samples ?? []).join('\n')],
        ['Files with syntax errors', o.syntaxErrorFiles, 'What parsed was kept.'],
        ['Files skipped', o.skippedFiles, 'Listed but not read.'],
    ];
    const langs = Object.entries(o.languages ?? {}).sort((a, b) => b[1] - a[1]);
    return [head, tiles,
        section('How sure each edge is', provenanceBar(o.provenance)),
        h('div', { class: 'cd-cols' },
            section('Relations', relationBars(o.relations)),
            section('Coverage', h('dl', { class: 'cd-rows' }, coverage.flatMap(([k, v, why]) => [
                h('dt', { title: why }, k), h('dd', {}, num(v)),
            ])))),
        (o.warnings?.length ? section('Warnings', h('ul', { class: 'cd-warn' }, o.warnings.map(w => h('li', {}, w)))) : null),
        langs.length ? section('Languages', h('div', { class: 'cd-chips' }, langs.map(([l, n]) => h('span', { class: 'cd-chip' }, `${l} ${num(n)}`)))) : null,
        o.hubs?.length ? section('Most connected', h('div', { class: 'cd-list' }, o.hubs.map(x =>
            symbolRow(x, h('span', { class: 'cd-count', title: 'Edges in · out' }, `${num(x.in)} in · ${num(x.out)} out`))))) : null,
    ].filter(Boolean);
}

function tile(value, label, action) {
    const kids = [h('b', {}, value), h('span', {}, label)];
    return action ? h('button', { class: 'cd-tile link', type: 'button', ...action }, ...kids) : h('div', { class: 'cd-tile' }, ...kids);
}

function provenanceBar(counts) {
    const rows = Object.entries(counts ?? {});
    const total = rows.reduce((n, [, v]) => n + v, 0);
    if (!total) return h('p', { class: 'cd-quiet' }, 'No edges yet.');
    const order = ['EXTRACTED', 'INFERRED', 'AMBIGUOUS'];
    rows.sort((a, b) => (order.indexOf(a[0]) + 1 || 9) - (order.indexOf(b[0]) + 1 || 9));
    const pct = v => `${(v / total * 100).toFixed(v / total < 0.1 ? 1 : 0)}%`;
    const toneOf = name => PROVENANCE[name] ? name.toLowerCase() : 'unknown';
    return h('div', {},
        h('div', { class: 'cd-bar', role: 'img', 'aria-label': rows.map(([k, v]) => `${k} ${pct(v)}`).join(', ') },
            rows.map(([k, v]) => h('i', { class: toneOf(k), vars: { '--share': v }, title: `${k}: ${num(v)} (${pct(v)})` }))),
        h('div', { class: 'cd-legend' }, rows.map(([k, v]) => h('div', {},
            h('span', { class: `cd-swatch ${toneOf(k)}`, 'aria-hidden': 'true' }),
            h('b', {}, k), ` ${num(v)} · ${pct(v)}`,
            PROVENANCE[k] ? h('span', { class: 'dim' }, PROVENANCE[k].say) : null))));
}

function relationBars(counts) {
    const rows = Object.entries(counts ?? {});
    const top = Math.max(1, ...rows.map(([, v]) => v));
    return h('div', { class: 'cd-rel' }, rows.map(([k, v]) => [
        h('span', {}, k),
        h('span', { class: 'cd-rel-track' }, h('i', { vars: { '--w': `${Math.max(1, v / top * 100)}%` }, title: `${k}: ${num(v)}` })),
        h('span', { class: 'cd-count' }, num(v)),
    ]).flat());
}

function symbolRow(x, trailing) {
    return h('button', { class: 'cd-row', type: 'button', title: place(x.file, x.line), onclick: () => loadSymbol(x.id) },
        h('span', { class: 'cd-kind' }, x.kind),
        h('span', { class: 'cd-name' }, x.name),
        h('span', { class: 'cd-file' }, place(x.file, x.line)),
        trailing);
}

function hitsView() {
    if (!S.hits) return [h('p', { class: 'cd-quiet' }, 'Searching…')];
    if (S.hits.error) return [h('p', { class: 'cd-quiet' }, S.hits.error)];
    if (!S.hits.total) return [h('p', { class: 'cd-quiet' }, `Nothing in this graph matches “${S.query}”.`)];
    return [
        h('p', { class: 'cd-quiet' }, S.hits.total > S.hits.hits.length
            ? `First ${S.hits.hits.length} of ${num(S.hits.total)} matches. Type more to narrow them.`
            : `${num(S.hits.total)} ${S.hits.total === 1 ? 'match' : 'matches'}.`),
        h('div', { class: 'cd-list' }, S.hits.hits.map(x => symbolRow(x))),
    ];
}

function detailView(d, card) {
    const s = d.symbol;
    return [
        h('button', { class: 'cd-link', type: 'button', onclick: back }, S.trail.length ? '‹ Back' : S.query ? '‹ Results' : '‹ Overview'),
        h('div', { class: 'cd-title' },
            h('h3', {}, s.qualified || s.name),
            h('span', { class: 'cd-kind' }, s.kind),
            s.language ? h('span', { class: 'cd-kind' }, s.language) : null),
        s.file ? h('div', { class: 'cd-where' },
            h('span', { class: 'cd-path' }, place(s.file, s.line)),
            h('button', { class: 'cd-btn', type: 'button', onclick: () => post({ type: 'codeReveal', repo: card.repo, file: s.file }),
                title: 'Show this file in File Explorer' }, 'Reveal file')) : null,
        d.freshness === 'changed' || d.freshness === 'missing'
            ? h('p', { class: 'cd-stale' }, d.freshness === 'changed'
                ? 'This file changed after the graph was built. Lines and links may be off until you refresh.'
                : 'This file is gone from the checkout. Refresh the graph.') : null,
        s.signature ? h('pre', { class: 'cd-sig' }, s.signature) : null,
        h('button', { class: 'cd-btn', type: 'button',
            onclick: () => fly({ id: s.id, category: `code:${card.repo}` }, s.name) }, 'Show in the Universe'),
        section(`Notes that name it (${num(d.notesTotal)})`, d.notes.length
            ? h('div', { class: 'cd-list' }, d.notes.map(n => h('button', { class: 'cd-row', type: 'button',
                onclick: () => fly({ id: n.noteId }, s.name) },
                h('span', { class: 'cd-name' }, n.path),
                h('span', { class: 'cd-count' }, `${n.count}×`))))
            : h('p', { class: 'cd-quiet' }, 'No note names this yet.')),
        ...d.groups.map(g => section(`${g.label} (${num(g.total)})`,
            h('div', { class: 'cd-list' }, g.items.map(x => symbolRow(x, provenanceTag(x)))),
            g.total > g.items.length ? h('p', { class: 'cd-quiet' }, `First ${g.items.length} of ${num(g.total)}.`) : null)),
        d.groups.length ? null : h('p', { class: 'cd-quiet' }, 'Nothing in this graph connects to it.'),
    ].filter(Boolean);
}

function provenanceTag(x) {
    const p = PROVENANCE[x.provenance];
    return h('span', { class: `cd-prov ${p ? x.provenance.toLowerCase() : 'unknown'}`,
        title: `${x.provenance}${p ? `: ${p.say}` : ''}${x.sites > 1 ? ` · ${x.sites} call sites` : ''}` }, p?.tag ?? '?');
}

// ── Host ──

function onHost(evt) {
    const m = evt.data;
    if (!m || typeof m !== 'object') return;
    switch (m.type) {
        case 'codeGraph': onRepos(m.payload?.totals ?? []); break;
        case 'codeAnswer': {
            const done = _waiting.get(m.id);
            _waiting.delete(m.id);
            done?.(m);
            break;
        }
        case 'codeBuildState': onBuildState(m); break;
        case 'codeGraphFailed': if (S.open) notice(m.message, 'warn'); break;
        case 'codeNotice': notice(m.message, 'warn'); break;
    }
}

function onRepos(repos) {
    S.repos = repos;
    if (!cardOf(S.selected)) {
        S.selected = S.build && cardOf(S.build.repo) ? S.build.repo : repos[0]?.repo ?? null;
        S.detail = null; S.trail = []; S.hits = null; S.query = ''; S.overview = null;
        if ($search) $search.value = '';
    }
    if (!S.open) return;
    render();
    // A background refresh keeps what is on screen until the new answer lands.
    if (keyOf(cardOf(S.selected)) !== S.selectedKey) reread();
}

function onBuildState(m) {
    const wasRunning = !!S.build;
    S.build = m.running ? { repo: m.repo, since: S.build?.since ?? Date.now() } : null;
    if (m.message) notice(m.message, m.state === 'done' ? 'ok' : 'warn');
    else if (m.running && !wasRunning) $notice.hidden = true;
    // A repo just added is the one the owner wants to look at when it lands.
    if (m.state === 'done' && m.repo && cardOf(m.repo) == null) S.selected = m.repo;
    if (S.open) render();
}

// ── ?hudDemo=1: the panel, full, with no host ──

const DEMO_SYMBOL = {
    id: 'brainx:Program.cs#Handle', name: 'Handle', qualified: 'BrainX.Mcp.Program.Handle', kind: 'method',
    file: 'BrainX.Mcp/Program.cs', line: 412, endLine: 498, language: 'csharp',
    signature: 'internal static string Handle(string line)',
};
const demoRow = (name, kind, file, line, provenance, sites = 1) => ({ id: `brainx:${name}`, name, kind, file, line, provenance, sites });
let _demoRepos = [
    { repo: 'brainx', source: 'brainx', root: 'D:\\BrainX', commit: '774548bc9d21', builtAt: new Date(Date.now() - 7.2e6).toISOString(), symbols: 48213, edges: 183402, visible: 342, links: 1208, languages: { csharp: 1102, javascript: 94, typescript: 8 }, state: 'current' },
    { repo: 'tpix-trade', source: 'brainx', root: 'D:\\Works\\tpix-trade', commit: '4d2e91aa0031', builtAt: new Date(Date.now() - 3.1e8).toISOString(), symbols: 21764, edges: 61230, visible: 188, links: 96, languages: { php: 1450, javascript: 212 }, state: 'behind' },
    { repo: 'old-import', source: 'graphify', root: 'D:\\Archive\\old', commit: null, builtAt: new Date(Date.now() - 2.2e9).toISOString(), symbols: 3120, edges: 8440, visible: 80, links: 0, languages: {}, state: 'gone' },
    { repo: 'broken', error: 'This graph could not be read. Add the folder again to rebuild it.' },
];

function demoHost(m) {
    const reply = a => setTimeout(() => onHost({ data: { ...a, type: 'codeAnswer', id: m.id } }), 120);
    if (m.type === 'codeDashOpen') {
        const repos = QS.get('codeEmpty') === '1' ? [] : _demoRepos;
        setTimeout(() => onHost({ data: { type: 'codeGraph', payload: { totals: repos } } }), 60);
    } else if (m.type === 'codeBuild' || m.type === 'codeAddRepo') {
        const repo = m.repo ?? 'new-project';
        onHost({ data: { type: 'codeBuildState', running: true, repo } });
        window.__demoBuild = setTimeout(() => onHost({ data: { type: 'codeBuildState', running: false, repo, state: 'done', message: '12,408 symbols, 40,112 edges in 18.2 s.' } }), 3000);
    } else if (m.type === 'codeBuildStop') {
        clearTimeout(window.__demoBuild);
        onHost({ data: { type: 'codeBuildState', running: false, state: 'stopped', message: 'Build stopped. The last saved graph is unchanged.' } });
    } else if (m.type === 'codeQuery' && m.op === 'overview') {
        reply({
            provenance: { EXTRACTED: 141220, INFERRED: 39874, AMBIGUOUS: 2308 },
            relations: { contains: 61240, calls: 88120, uses: 21840, imports: 7310, inherits: 3120, implements: 1772 },
            unresolved: { calls: 3120, inherits: 42 }, external: { calls: 40112, uses: 9120 },
            languages: { csharp: 1102, javascript: 94, typescript: 8 }, skippedFiles: 12, syntaxErrorFiles: 3, warnings: [],
            files: { unchanged: 1180, changed: 21, missing: 3, unverifiable: 0, tracked: 1204, samples: ['BrainX.Mcp/Program.cs', 'BrainX.Core/Services/CodeGraph.cs'] },
            notes: 640, linkedSymbols: 901, hubs: [
                { id: DEMO_SYMBOL.id, name: DEMO_SYMBOL.qualified, kind: 'method', file: DEMO_SYMBOL.file, in: 412, out: 96 },
                { id: 'brainx:Log', name: 'BrainX.Mcp.Program.Log', kind: 'method', file: 'BrainX.Mcp/Program.cs', in: 388, out: 2 },
                { id: 'brainx:NoteStore', name: 'BrainX.Core.Services.NoteStore', kind: 'class', file: 'BrainX.Core/Services/NoteStore.cs', in: 240, out: 61 },
            ],
        });
    } else if (m.type === 'codeQuery' && m.op === 'search') {
        reply({ total: 57, hits: [
            { id: DEMO_SYMBOL.id, name: DEMO_SYMBOL.qualified, kind: 'method', file: DEMO_SYMBOL.file, line: 412 },
            { id: 'brainx:HandleCodeMessage', name: 'BrainX.Client.MainWindow.HandleCodeMessage', kind: 'method', file: 'BrainX.Client/MainWindow.CodeUniverse.cs', line: 168 },
        ] });
    } else if (m.type === 'codeQuery' && m.op === 'symbol') {
        reply({
            symbol: DEMO_SYMBOL, freshness: 'changed', notesTotal: 2,
            notes: [{ noteId: 'n1', path: 'Notes/MCP request loop.md', count: 7 }, { noteId: 'n2', path: 'Notes/Long polls off the gate.md', count: 2 }],
            groups: [
                { label: 'Called by', total: 2, items: [demoRow('BrainX.Mcp.Program.ServeCodeBuild', 'method', 'BrainX.Mcp/Program.CodeGraph.cs', 79, 'EXTRACTED', 2), demoRow('BrainX.Mcp.Program.Main', 'method', 'BrainX.Mcp/Program.cs', 88, 'INFERRED')] },
                { label: 'Calls', total: 120, items: [demoRow('BrainX.Mcp.Program.BuildError', 'method', 'BrainX.Mcp/Program.cs', 40, 'AMBIGUOUS')] },
            ],
        });
    }
}

function init() {
    build();
    if (DEMO) { window.__codeDash = S; return; }
    try { window.chrome?.webview?.addEventListener('message', onHost); } catch { /* not hosted */ }
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();
