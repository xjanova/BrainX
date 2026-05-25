// ObsidianX Universe — layout + palette.
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
function hashStr(s) {
    let h = 2166136261 >>> 0;
    for (let i = 0; i < s.length; i++) {
        h ^= s.charCodeAt(i);
        h = Math.imul(h, 16777619) >>> 0;
    }
    return h >>> 0;
}

// mulberry32 — small, fast, decent distribution.
function rng(seed) {
    let t = seed >>> 0;
    return () => {
        t = (t + 0x6D2B79F5) >>> 0;
        let r = Math.imul(t ^ (t >>> 15), 1 | t);
        r = (r + Math.imul(r ^ (r >>> 7), 61 | r)) ^ r;
        return ((r ^ (r >>> 14)) >>> 0) / 4294967296;
    };
}

// Box-Muller; uses the rng above.
function gauss(rand) {
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
        return { nodes: [], edges: [], galaxies: [] };
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

    // Expertise lookup: brain-export ships { Category, Score, NoteCount, ... }
    // for each top-level category. We join by category name so the legend
    // can render a progress bar reflecting the brain's actual depth, not
    // just node count. Missing = score 0 (rare — only in toy brains).
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

    // 2) Place each galaxy on a Fibonacci sphere of radius 80.
    //    Single-galaxy edge case: keep it centered.
    const galaxyR = 80;
    const galaxies = sortedCats.map(([category, list], i) => {
        const pal = GALAXY_PALETTE[category] ?? { hex: hueFromCategory(category), label: prettifyCategory(category) };
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
                title: n.Title ?? n.title ?? '(untitled)',
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
                linkedIds: n.LinkedNodeIds ?? n.linkedNodeIds ?? []
            };

            idIndex.set(id, nodes.length);
            nodes.push(node);
        });
    }

    // 4) Wiki-link edges. Skip self-loops + edges to nodes we never indexed
    //    (orphan ids occur when an import is partial).
    const edges = [];
    for (let i = 0; i < nodes.length; i++) {
        for (const tgtId of nodes[i].linkedIds) {
            const j = idIndex.get(tgtId);
            if (j == null || j === i) continue;
            // dedupe: only emit (a,b) where a < b — otherwise we draw the
            // same line twice and waste pixels (and bloom intensity).
            if (i < j) edges.push({ a: i, b: j });
        }
    }

    return { nodes, edges, galaxies };
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
