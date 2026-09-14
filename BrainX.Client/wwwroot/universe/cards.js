// Which HUD cards a WALLPAPER shows — the live wallpaper pages only.
//
// The main view keeps its own card switches (hud.js, obsidianx.hud.cards.v1)
// and nothing here touches them. A wallpaper's cards belong to the wallpaper
// prefs instead, and to a SCREEN: in Separate mode every monitor carries its
// own set. app.js owns that persistence (it already owns the wallpaper prefs
// and knows which monitor is being configured); hud.js owns the panels. They
// meet here, the same way theme.js joins them for the theme.
//
// A wallpaper shows NO cards unless asked — that is what a wallpaper looked
// like before cards existed, and nobody's desktop should change on update.

/** Card ids, in the order they are offered: the HUD's grid areas, with the
 *  labels a wallpaper uses. `tc` is the action bar + network readout on the
 *  main view; a wallpaper takes no clicks, so there it is the network card. */
export const WALLPAPER_CARDS = [
    { id: 'tl', label: 'Brain' },
    { id: 'tc', label: 'Network' },
    { id: 'tr', label: 'Activity' },
    { id: 'ml', label: 'Expertise' },
    { id: 'mr', label: 'System' },
    { id: 'br', label: 'MCP' },
    { id: 'bl', label: 'Agent Bus' },
    { id: 'bc', label: 'Agent Chat' },
];
const IDS = new Set(WALLPAPER_CARDS.map(c => c.id));

let current = {};
const listeners = new Set();

/** Only known ids, only `true` — a stored blob can never switch on a card
 *  that does not exist or smuggle anything else through. */
export function normalizeCards(c) {
    const out = {};
    if (c && typeof c === 'object') for (const id of IDS) if (c[id] === true) out[id] = true;
    return out;
}

export const anyCard = (c) => Object.values(c || {}).some(v => v === true);

export function getWallpaperCards() { return { ...current }; }

/** @param {string} source  who changed it — 'hud' when a chip was clicked, so
 *  app.js can tell a user's edit (persist it) from its own push (don't). */
export function setWallpaperCards(c, source = 'app') {
    current = normalizeCards(c);
    for (const fn of listeners) {
        try { fn({ ...current }, source); } catch (e) { console.warn('[cards] listener failed', e); }
    }
}

export function onWallpaperCardsChange(fn) {
    listeners.add(fn);
    return () => listeners.delete(fn);
}
