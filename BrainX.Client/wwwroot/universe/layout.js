// BrainX Universe — layout + palette.
//
// Pure math: given a brain-export payload, deterministically place every
// note in 3D space and assign it a color. No three.js imports here so this
// module is unit-testable from a plain HTML page.
//
// Layout model:
//   • Each PrimaryCategory becomes a "galaxy" — a flat-ish spiral disk.
//   • Galaxies sit on a Fibonacci sphere so they spread evenly in 3D.
//   • Within a galaxy, notes are placed by a 2-arm log-spiral with
//     gaussian thickness, seeded by the node id so layout is stable
//     across reloads even when the source array order shuffles.

import { gatherGroups, groupNotes, recordGroupReach } from './nodegroups.js';

const GALAXY_PALETTE = {
    // hand-picked HSL colors that read well against deep space + bloom.
    Programming:         { hex: 0x6cf0ff, label: 'Programming'         }, // cyan
    DataScience:         { hex: 0xb084ff, label: 'Data Science'        }, // violet
    Design_Art:          { hex: 0xff7ac6, label: 'Design / Art'        }, // rose
    Engineering:         { hex: 0xffb86c, label: 'Engineering'         }, // amber
    Blockchain_Web3:     { hex: 0xff9248, label: 'Blockchain / Web3'   }, // orange
    Business_Finance:    { hex: 0x6fe7a3, label: 'Business / Finance'  }, // emerald
    AI_MachineLearning:  { hex: 0x7aa8ff, label: 'AI / ML'             }, // electric blue
    Web_Development:     { hex: 0x9b7cff, label: 'Web Development'     }, // indigo
    Security_Crypto:     { hex: 0xff6b6b, label: 'Security / Crypto'   }, // red
    Health_Wellness:     { hex: 0x9bff7a, label: 'Health / Wellness'   }, // lime
    Productivity:        { hex: 0x7adfff, label: 'Productivity'        }, // sky
    Education:           { hex: 0xffd86c, label: 'Education'           }, // gold
    Lifestyle:           { hex: 0xff9bd5, label: 'Lifestyle'           }, // soft pink
    Science:             { hex: 0x6cffd5, label: 'Science'             }, // mint
    Other:               { hex: 0xc0c0d8, label: 'Other'               }  // silver
};

// Deterministic hash → uint32 for seeded RNG. Quick + good enough; not crypto.
export function hashStr(s) {
    let h = 2166136261 >>> 0;
    for (let i = 0; i < s.length; i++) {
        h ^= s.charCodeAt(i);
        h = Math.imul(h, 16777619) >>> 0;
    }
    return h >>> 0;
}

// mulberry32 — small, fast, decent distribution.
export function rng(seed) {
    let t = seed >>> 0;
    return () => {
        t = (t + 0x6D2B79F5) >>> 0;
        let r = Math.imul(t ^ (t >>> 15), 1 | t);
        r = (r + Math.imul(r ^ (r >>> 7), 61 | r)) ^ r;
        return ((r ^ (r >>> 14)) >>> 0) / 4294967296;
    };
}

// Box-Muller; uses the rng above.
export function gauss(rand) {
    const u = Math.max(rand(), 1e-9);
    const v = rand();
    return Math.sqrt(-2 * Math.log(u)) * Math.cos(2 * Math.PI * v);
}

// Fibonacci-sphere point #i out of n.
function fibSphere(i, n, radius) {
    const phi = Math.acos(1 - 2 * (i + 0.5) / n);
    const theta = Math.PI * (1 + Math.sqrt(5)) * (i + 0.5);
    return {
        x: radius * Math.cos(theta) * Math.sin(phi),
        y: radius * Math.cos(phi),
        z: radius * Math.sin(theta) * Math.sin(phi)
    };
}

// Orthonormal basis from one normal. Used to orient each galaxy's disk.
function basisFromNormal(n) {
    const up = Math.abs(n.y) < 0.95 ? { x: 0, y: 1, z: 0 } : { x: 1, y: 0, z: 0 };
    // u = up × n  (right-hand)
    const ux = up.y * n.z - up.z * n.y;
    const uy = up.z * n.x - up.x * n.z;
    const uz = up.x * n.y - up.y * n.x;
    const ul = Math.hypot(ux, uy, uz) || 1;
    const u = { x: ux / ul, y: uy / ul, z: uz / ul };
    // v = n × u
    const v = {
        x: n.y * u.z - n.z * u.y,
        y: n.z * u.x - n.x * u.z,
        z: n.x * u.y - n.y * u.x
    };
    return { u, v };
}

/**
 * Given the brain payload, return:
 *   {
 *     nodes:    Array<{ id, title, category, color, position:{x,y,z}, size, brightness, raw }>,
 *     edges:    Array<{ a, b }>            // indices into nodes
 *     galaxies: Array<{ category, color, label, center:{x,y,z}, count, radius }>,
 *   }
 */
export function buildUniverse(brain) {
    const rawNodes = brain.Nodes ?? brain.nodes ?? [];
    if (!rawNodes.length) {
        const nodes = [], edges = [], galaxies = [];
        addCodeLayer(brain.CodeOverlay ?? brain.codeOverlay, nodes, edges, galaxies, new Map());
        return { nodes, edges, galaxies };
    }

    // 1) Group nodes by primary category.
    const byCat = new Map();
    for (const n of rawNodes) {
        const cat = n.PrimaryCategory ?? n.primaryCategory ?? 'Other';
        if (!byCat.has(cat)) byCat.set(cat, []);
        byCat.get(cat).push(n);
    }

    // Sort categories by node count desc so the legend lists big ones first
    // and the layout puts the chunkiest galaxies at deterministic positions.
    const sortedCats = [...byCat.entries()].sort((a, b) => b[1].length - a[1].length);

    const expertiseByCat = expertiseByCategory(brain);

    // 2) Place each galaxy on a Fibonacci sphere. The sphere radius scales
    //    with the LARGEST disk so big galaxies (Programming can hit r≈60)
    //    get real space between them instead of overlapping into one blob —
    //    the eye should read distinct star systems, not a wireframe hairball.
    //    (Camera fit + focusGalaxy derive from these centers, so they adapt.)
    const maxDiskR = Math.max(...sortedCats.map(([, list]) => 12 + Math.sqrt(list.length) * 2.4));
    const galaxyR = Math.max(80, maxDiskR * 2.1);
    const galaxies = sortedCats.map(([category, list], i) => {
        const pal = paletteFor(category);
        const expertise = expertiseByCat.get(category) ?? { score: 0, noteCount: list.length, totalWords: 0, growthRate: 0 };
        const center = sortedCats.length === 1
            ? { x: 0, y: 0, z: 0 }
            : fibSphere(i, sortedCats.length, galaxyR);
        // Disk basis: normal = outward radial; (u, v) span the disk plane.
        // Stored on the galaxy so scene.js can project local↔world during
        // physics integration without re-deriving the math.
        const isOrigin = center.x === 0 && center.y === 0 && center.z === 0;
        const normal = isOrigin ? { x: 0, y: 1, z: 0 } : unit(center);
        const basis = basisFromNormal(normal);
        return {
            category,
            label: pal.label,
            color: pal.hex,
            center,
            normal,
            basisU: basis.u,
            basisV: basis.v,
            count: list.length,
            score: expertise.score,         // 0..1, drives legend progress bar
            totalWords: expertise.totalWords,
            growthRate: expertise.growthRate,
            radius: 12 + Math.sqrt(list.length) * 2.4   // bigger groups = bigger disks
        };
    });

    // 3) Place each node inside its galaxy disk. Two-arm log-spiral with
    //    gaussian noise; thickness scales with disk radius / 6 so big
    //    galaxies feel fluffy and tiny ones stay tight.
    //
    //    These are *initial* positions; per-galaxy d3-force in scene.js
    //    will then settle them based on link density + repulsion.
    const nodes = [];
    const idIndex = new Map();
    let galaxyIdx = -1;
    for (const g of galaxies) {
        galaxyIdx++;
        const list = byCat.get(g.category);
        const normal = g.normal;
        const basis = { u: g.basisU, v: g.basisV };

        // Importance drives radial bias: top-importance notes pulled toward the
        // galactic core, dim ones drift to the rim. This makes "what matters
        // most" naturally cluster as the bright center the eye lands on.
        const importanceMax = Math.max(...list.map(n => n.Importance ?? n.importance ?? 1), 1);

        list.forEach((n, idx) => {
            const id = n.Id ?? n.id ?? `${g.category}-${idx}`;
            const seed = hashStr(id);
            const r = rng(seed);

            const importance = (n.Importance ?? n.importance ?? 1) / importanceMax; // 0..1
            // radial position: importance shrinks radius (core pull); +noise
            const rim = g.radius * (0.15 + (1 - importance) * 0.75 + r() * 0.15);
            const arm = (idx % 2) * Math.PI;                          // two arms
            const swirl = Math.log(1 + rim) * 1.6;                    // log spiral
            const theta = arm + swirl + r() * 0.6;

            const localX = rim * Math.cos(theta);
            const localY = rim * Math.sin(theta);
            const localZ = gauss(r) * (g.radius / 7.5);               // disk thickness

            // local (u, v, n) → world
            const pos = {
                x: g.center.x + basis.u.x * localX + basis.v.x * localY + normal.x * localZ,
                y: g.center.y + basis.u.y * localX + basis.v.y * localY + normal.y * localZ,
                z: g.center.z + basis.u.z * localX + basis.v.z * localY + normal.z * localZ
            };

            // Star size: log(word count) keeps massive notes from dwarfing the
            // scene. 0.5 floor so a stub still casts a visible point.
            const wc = Math.max(1, n.WordCount ?? n.wordCount ?? 1);
            const size = 0.55 + Math.log10(wc) * 0.7;                 // 0.55 .. ~3.5

            // Brightness: importance + tiny boost for fresh notes.
            const brightness = 0.45 + importance * 0.55;

            const node = {
                id,
                sourceKind: 'note',
                title: n.Title ?? n.title ?? '(untitled)',
                scope: n.Scope ?? n.scope ?? '',
                category: g.category,
                galaxyIdx,
                categoryLabel: g.label,
                color: g.color,
                position: pos,
                // local (u, v, n) coords inside the galaxy disk. Scene.js
                // feeds these to d3-force; the simulation mutates u/v in
                // place and we project back to world per frame.
                local: { u: localX, v: localY, n: localZ },
                size,
                brightness,
                wordCount: wc,
                tags: n.Tags ?? n.tags ?? [],
                preview: (n.Preview ?? n.preview ?? '').slice(0, 480),
                modifiedAt: n.ModifiedAt ?? n.modifiedAt ?? null,
                linkedIds: drawnLinks(n)
            };

            idIndex.set(id, nodes.length);
            nodes.push(node);
        });
    }

    groupNotes(nodes);
    gatherGroups(nodes, n => {
        const g = galaxies[n.galaxyIdx];
        n.position.x = g.center.x + g.basisU.x * n.local.u + g.basisV.x * n.local.v + g.normal.x * n.local.n;
        n.position.y = g.center.y + g.basisU.y * n.local.u + g.basisV.y * n.local.v + g.normal.y * n.local.n;
        n.position.z = g.center.z + g.basisU.z * n.local.u + g.basisV.z * n.local.v + g.normal.z * n.local.n;
    });

    const edges = linkEdges(nodes, idIndex);
    addCodeLayer(brain.CodeOverlay ?? brain.codeOverlay, nodes, edges, galaxies, idIndex);
    return { nodes, edges, galaxies };
}

// The code core's radius for a repo of this many files. Loose on purpose: the
// edges and the additive glow are what would melt a dense ball, not the points.
const CORE_RADIUS = (files) => 14 + 6 * Math.cbrt(files);
// The core may fill this much of the space inside the note galaxies.
const CORE_SHARE = 0.42;
// Note galaxies nearer the origin than this leave no room for a core. The sphere
// they sit on is never under 80, so in practice this is the one-galaxy vault.
const HOLLOW_MIN = 60;
// Spacing between the symbols of one file, in ball-local units.
const SYMBOL_PITCH = 2.2;

/** The drawn symbols, per repo, in layer order. Ids a note already holds are skipped. */
export function codeRepos(layer, idIndex) {
    const byRepo = new Map();
    if (!Array.isArray(layer?.nodes)) return byRepo;
    for (const raw of layer.nodes) {
        if (!raw?.id || idIndex.has(raw.id)) continue;
        const repo = raw.repo || 'code';
        if (!byRepo.has(repo)) byRepo.set(repo, []);
        byRepo.get(repo).push(raw);
    }
    return byRepo;
}

/** How many links touch each symbol: a hub is what the most other points reach for. */
export function codeDegree(layer) {
    const degree = new Map();
    const bump = id => degree.set(id, (degree.get(id) ?? 0) + 1);
    for (const e of layer?.edges ?? []) { bump(e.from); bump(e.to); }
    for (const m of layer?.mentions ?? []) bump(m.symbolId);
    return degree;
}

/**
 * Where each symbol of a repo sits in a ball of `radius`, in ball-local (u, v, n).
 * One point per file group: the most connected file at the middle, the least at
 * the rim, its symbols gathered around it. Writes straight into the result; the
 * caller projects it into whichever frame the theme lives in.
 */
export function placeCode(list, degree, radius) {
    const files = new Map();
    for (const raw of list) {
        const key = raw.file ?? raw.id;
        if (!files.has(key)) files.set(key, []);
        files.get(key).push(raw);
    }
    const reach = symbols => symbols.reduce((sum, s) => sum + (degree.get(s.id) ?? 0), 0);
    const ranked = [...files.entries()]
        .map(([file, symbols]) => ({ file, symbols, reach: reach(symbols) }))
        .sort((a, b) => (b.reach - a.reach) || (a.file < b.file ? -1 : 1));
    const out = [];
    ranked.forEach(({ file, symbols }, rank) => {
        const nearness = 1 - rank / Math.max(1, ranked.length - 1);
        const home = fibSphere(rank, ranked.length, radius * Math.cbrt((rank + 0.5) / ranked.length));
        const head = symbols.find(s => s.kind === 'file')
            ?? symbols.reduce((best, s) => ((degree.get(s.id) ?? 0) > (degree.get(best.id) ?? 0) ? s : best));
        const members = symbols.filter(s => s !== head);
        out.push({ raw: head, local: { u: home.x, v: home.z, n: home.y }, nearness, file, count: symbols.length, head: true });
        members.forEach((raw, k) => {
            const off = fibSphere(k, members.length, SYMBOL_PITCH * Math.cbrt(k + 2));
            out.push({ raw, local: { u: home.x + off.x, v: home.z + off.z, n: home.y + off.y }, nearness, file, count: symbols.length, head: false });
        });
    });
    return out;
}

/** The sky's node for one placed symbol. position is the caller's to fill. */
export function codeNode(slot, repo, galaxyIdx, position, source) {
    const { raw, local, nearness } = slot;
    const isFile = raw.kind === 'file';
    return { id: raw.id, title: raw.title || raw.id, category: `code:${repo}`, galaxyIdx,
        categoryLabel: `Code · ${repo} (${source})`, sourceKind: 'code', codeKind: raw.kind,
        codeSource: source, codeRepo: repo, codeFile: raw.file, codeLine: raw.line,
        color: isFile ? 0x8ef5ff : 0x30ddea,
        position, local, size: (isFile ? 1.6 : 1.0) + nearness * 0.8,
        brightness: 0.35 + nearness * 0.6, wordCount: 0,
        tags: [raw.kind || 'symbol', source], preview: raw.signature || raw.file || '',
        modifiedAt: null, linkedIds: [] };
}

export const codeSourceLabel = list => (list[0]?.source === 'graphify' ? 'Graphify' : 'BrainX');

/** The head of each placed file group, as a node index, then the groups stamped. */
export function joinCodeGroups(nodes, firstIdx, slots) {
    const heads = new Map();
    slots.forEach((slot, k) => { if (slot.head) heads.set(slot.file, firstIdx + k); });
    slots.forEach((slot, k) => {
        if (slot.count < 2) return;
        const n = nodes[firstIdx + k];
        n.groupHead = heads.get(slot.file);
        n.groupCount = slot.count;
        n.groupLabel = (slot.file.split('/').pop() || slot.file);
    });
}

/** Code and mention edges, once per pair of points and kind. */
export function linkCode(layer, edges, idIndex) {
    const seen = new Set();
    function link(aId, bId, kind, relation) {
        const a = idIndex.get(aId), b = idIndex.get(bId);
        if (a == null || b == null || a === b) return;
        const key = `${Math.min(a, b)}:${Math.max(a, b)}:${kind}`;
        if (seen.has(key)) return;
        seen.add(key);
        edges.push({ a, b, kind, relation });
    }
    for (const e of layer?.edges ?? []) link(e.from, e.to, 'code', e.relation);
    for (const m of layer?.mentions ?? []) link(m.noteId, m.symbolId, 'mention', 'mentions');
}

/**
 * Code is the universe's loose core: every note galaxy sits around it, so any of
 * them can reach it. Each repo is a ball of its own inside that core. Note stars
 * keep their original ids and homes.
 */
export function addCodeLayer(layer, nodes, edges, galaxies, idIndex) {
    const repos = [...codeRepos(layer, idIndex).entries()];
    if (!repos.length) return;
    const degree = codeDegree(layer);
    const radii = repos.map(([, list]) => CORE_RADIUS(new Set(list.map(s => s.file ?? s.id)).size));
    const widest = Math.max(...radii);
    const ring = repos.length > 1 ? widest * 1.25 : 0;
    // The disks lie tangent to their sphere, so a galaxy's centre is also its
    // nearest point to the origin. A vault with one category has its only
    // galaxy AT the origin: there the core goes beside it, as far out as it reaches.
    const free = Math.min(...galaxies.map(g => Math.hypot(g.center.x, g.center.y, g.center.z)));
    const hollow = free >= HOLLOW_MIN;
    const fit = hollow ? Math.min(1, CORE_SHARE * free / (ring + widest)) : 1;
    const base = hollow ? { x: 0, y: 0, z: 0 }
        : { x: Math.max(...galaxies.map(g => g.radius)) + 35 + (ring + widest), y: 0, z: 0 };

    const normal = { x: 0, y: 1, z: 0 };
    const basis = basisFromNormal(normal);
    repos.forEach(([repo, list], i) => {
        const radius = radii[i] * fit;
        const offset = fibSphere(i, repos.length, ring * fit);
        const center = { x: base.x + offset.x, y: base.y + offset.y, z: base.z + offset.z };
        const galaxyIdx = galaxies.length;
        const source = codeSourceLabel(list);
        const label = `Code · ${repo} (${source})`;
        galaxies.push({ category: `code:${repo}`, label, color: 0x30ddea,
            center, normal, basisU: basis.u, basisV: basis.v, count: list.length,
            score: 0, totalWords: 0, radius, isCode: true });
        const firstIdx = nodes.length;
        const slots = placeCode(list, degree, radius);
        for (const slot of slots) {
            const { u, v, n } = slot.local;
            const position = {
                x: center.x + basis.u.x * u + basis.v.x * v + normal.x * n,
                y: center.y + basis.u.y * u + basis.v.y * v + normal.y * n,
                z: center.z + basis.u.z * u + basis.v.z * v + normal.z * n
            };
            const node = codeNode(slot, repo, galaxyIdx, position, source);
            idIndex.set(node.id, nodes.length);
            nodes.push(node);
        }
        joinCodeGroups(nodes, firstIdx, slots);
    });
    recordGroupReach(nodes);
    linkCode(layer, edges, idIndex);
}

/**
 * Expertise lookup: brain-export ships { Category, Score, NoteCount, ... }
 * for each top-level category. Joined by category name so the legend can
 * render a progress bar reflecting the brain's actual depth, not just node
 * count. Missing = score 0 (rare — only in toy brains).
 *
 * Exported with paletteFor and linkEdges because the brain theme
 * (brainlayout.js) builds the same { nodes, edges, galaxies } shape from the
 * same payload — two copies of the join would be two answers to one question.
 */
export function expertiseByCategory(brain) {
    const expertiseByCat = new Map();
    for (const e of (brain.Expertise ?? brain.expertise ?? [])) {
        const k = e.Category ?? e.category;
        if (k) expertiseByCat.set(k, {
            score: e.Score ?? e.score ?? 0,
            noteCount: e.NoteCount ?? e.noteCount ?? 0,
            totalWords: e.TotalWords ?? e.totalWords ?? 0,
            growthRate: e.GrowthRate ?? e.growthRate ?? 0
        });
    }
    return expertiseByCat;
}

/** Colour + display label for a category, with a stable fallback for one the
 *  palette has never heard of. */
export function paletteFor(category) {
    return GALAXY_PALETTE[category] ?? { hex: hueFromCategory(category), label: prettifyCategory(category) };
}

/**
 * Every link a note draws: the written ones and the auto-linker's guesses.
 * Since 2026-09-23 the export keeps them apart (AutoLinkedNodeIds) so the
 * brain's tools stop counting guesses as links — but the universe was always
 * shaped by both, and dropping the ~80% that are guesses would change how the
 * brain looks for a reason that has nothing to do with how it looks.
 */
export function drawnLinks(n) {
    const written = n.LinkedNodeIds ?? n.linkedNodeIds ?? [];
    const guessed = n.AutoLinkedNodeIds ?? n.autoLinkedNodeIds ?? [];
    return guessed.length === 0 ? written : [...new Set([...written, ...guessed])];
}

/**
 * Wiki-link edges. Skip self-loops + edges to nodes we never indexed (orphan
 * ids occur when an import is partial).
 *
 * LinkedNodeIds is DIRECTIONAL (outgoing links only), so dedupe must
 * normalize the pair — the old `if (i < j)` shortcut silently dropped every
 * one-way link that happened to point from a higher node index to a lower one
 * (roughly half of all single-direction wiki-links).
 */
export function linkEdges(nodes, idIndex) {
    const edges = [];
    const seenPairs = new Set();
    for (let i = 0; i < nodes.length; i++) {
        for (const tgtId of nodes[i].linkedIds) {
            const j = idIndex.get(tgtId);
            if (j == null || j === i) continue;
            const key = i < j ? i * nodes.length + j : j * nodes.length + i;
            if (seenPairs.has(key)) continue;
            seenPairs.add(key);
            edges.push(i < j ? { a: i, b: j } : { a: j, b: i });
        }
    }
    return edges;
}

function unit(v) {
    const l = Math.hypot(v.x, v.y, v.z) || 1;
    return { x: v.x / l, y: v.y / l, z: v.z / l };
}

// Stable hue for an unknown category — keeps the legend honest if the brain
// ever introduces a new category we forgot to put in the palette.
function hueFromCategory(category) {
    const h = (hashStr(category) % 360) / 360;
    // HSL → hex via a quick conversion
    return hslToHex(h, 0.55, 0.62);
}

function hslToHex(h, s, l) {
    const a = s * Math.min(l, 1 - l);
    const f = n => {
        const k = (n + h * 12) % 12;
        const c = l - a * Math.max(-1, Math.min(k - 3, Math.min(9 - k, 1)));
        return Math.round(c * 255);
    };
    return (f(0) << 16) | (f(8) << 8) | f(4);
}

function prettifyCategory(c) {
    return c.replace(/[_-]+/g, ' / ').replace(/\b\w/g, ch => ch.toUpperCase());
}
