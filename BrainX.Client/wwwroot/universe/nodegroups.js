// BrainX Universe — which points are one point.
//
// Notes on the same subject and symbols in the same file gather side by side,
// and read as a single point from far away (the shader in scene.js does that
// part). Pure math like layout.js: runs from a plain node check.
//
//   a note joins a group by   its title, the same title once dates and hashes
//                             are gone, inside one project
//                             else the rarest tag it shares, inside one category
//   a symbol joins a group by its file (see placeCode in layout.js)

const DATE_TAG = /^\d{4}(-\d{2}){0,2}$/;
// How a note came to be, not what it is about. A tag naming a status gathers
// notes that have nothing else in common.
const STATUS_TAGS = new Set([
    'session-handoff', 'session', 'handoff', 'task-handoff', 'imported', 'docs', 'auto-digest', 'session-auto-digest',
    'digest', 'gotcha', 'deployed', 'shipped', 'merged', 'uncommitted', 'completed', 'code-complete', 'blocked',
    'pending', 'wip', 'todo', 'done', 'audit', 'incident', 'diagnosis', 'bug-fix', 'bug', 'fix', 'silent-failure',
    'reusable', 'playbook', 'note', 'notes', 'workflow', 'pr',
]);
const TAG_GROUP_MIN = 3;
// More notes than this under one tag is a project, not a topic.
const TAG_GROUP_MAX = 24;
// Distance between neighbours in a gathered group, in layout units. Just past
// what forceCollide keeps apart, so the sim has nothing to undo.
const MEMBER_PITCH = 2.6;
// A member keeps this much of its depth in the disk: a group at full depth
// would stand as a column through the galaxy.
const MEMBER_DEPTH = 0.15;
const GOLDEN = Math.PI * (3 - Math.sqrt(5));

const stripIds = (title) => title
    .replace(/\b\d{4}-\d{2}-\d{2}\b/g, '')
    .replace(/\b[0-9a-f]{8,}\b/gi, '')
    .replace(/\s+/g, ' ')
    .trim();

const outranks = (a, b) => (a.brightness - b.brightness) || (a.wordCount - b.wordCount) || (a.id < b.id ? 1 : -1);

/** Stamp a group on its members. The strongest note is the head they gather around. */
export function joinGroup(nodes, members, label) {
    const head = members.reduce((best, i) => (outranks(nodes[i], nodes[best]) > 0 ? i : best));
    for (const i of members) {
        nodes[i].groupHead = head;
        nodes[i].groupCount = members.length;
        nodes[i].groupLabel = label;
    }
}

/** Group the notes of one sky. Code nodes are grouped by placeCode and left alone here. */
export function groupNotes(nodes) {
    const byCategory = new Map();
    nodes.forEach((n, i) => {
        if (n.sourceKind === 'code') return;
        if (!byCategory.has(n.category)) byCategory.set(n.category, []);
        byCategory.get(n.category).push(i);
    });
    for (const indices of byCategory.values()) {
        const free = new Set(indices);

        const byTitle = new Map();
        for (const i of indices) {
            const title = stripIds(nodes[i].title ?? '').toLowerCase();
            if (!title) continue;
            const key = `${nodes[i].scope ?? ''}|${title}`;
            if (!byTitle.has(key)) byTitle.set(key, []);
            byTitle.get(key).push(i);
        }
        for (const members of byTitle.values()) {
            if (members.length < 2) continue;
            joinGroup(nodes, members, stripIds(nodes[members[0]].title));
            for (const i of members) free.delete(i);
        }

        const byTag = new Map();
        for (const i of free) {
            for (const raw of new Set((nodes[i].tags ?? []).map(t => String(t).toLowerCase()))) {
                if (DATE_TAG.test(raw) || STATUS_TAGS.has(raw)) continue;
                if (!byTag.has(raw)) byTag.set(raw, []);
                byTag.get(raw).push(i);
            }
        }
        // Rarest first: the narrower the tag, the closer the notes under it.
        const tags = [...byTag.entries()]
            .filter(([, list]) => list.length <= TAG_GROUP_MAX)
            .sort((a, b) => (a[1].length - b[1].length) || (a[0] < b[0] ? -1 : 1));
        for (const [tag, list] of tags) {
            const members = list.filter(i => free.has(i));
            if (members.length < TAG_GROUP_MIN) continue;
            joinGroup(nodes, members, `#${tag}`);
            for (const i of members) free.delete(i);
        }
    }
}

/**
 * Bring every member beside its head, then record how far the group reaches.
 * `project(node)` turns node.local into node.position, in whichever frame the
 * theme lives. Heads and ungrouped notes stay where the layout put them.
 */
export function gatherGroups(nodes, project) {
    const groups = new Map();
    nodes.forEach((n, i) => {
        if (n.groupHead == null || n.sourceKind === 'code') return;
        if (!groups.has(n.groupHead)) groups.set(n.groupHead, []);
        if (n.groupHead !== i) groups.get(n.groupHead).push(n);
    });
    for (const [headIdx, members] of groups) {
        const head = nodes[headIdx];
        members.forEach((n, k) => {
            const r = MEMBER_PITCH * Math.sqrt(k + 1);
            const a = (k + 1) * GOLDEN;
            n.local.u = head.local.u + r * Math.cos(a);
            n.local.v = head.local.v + r * Math.sin(a);
            n.local.n = head.local.n + (n.local.n - head.local.n) * MEMBER_DEPTH;
            project(n);
        });
    }
    recordGroupReach(nodes);
}

/** groupR: how far the farthest member sits from its head, in world units. */
export function recordGroupReach(nodes) {
    for (const n of nodes) {
        if (n.groupHead == null) continue;
        const h = nodes[n.groupHead];
        const d = Math.hypot(n.position.x - h.position.x, n.position.y - h.position.y, n.position.z - h.position.z);
        h.groupR = Math.max(h.groupR ?? 0, d);
    }
    for (const n of nodes) if (n.groupHead != null) n.groupR = nodes[n.groupHead].groupR;
}
