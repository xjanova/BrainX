// Which picture this page is showing: the universe or the neural brain.
//
// Two modules need that one fact and they must never disagree. app.js OWNS
// the setting — it persists it with every other display setting, routed to
// the wallpaper prefs or the app's own exactly as the rest are — and publishes
// it here. hud.js only listens, to swap its agent-bus picture: a solar system
// beside the universe, an anatomical body beside the brain.
//
// A module rather than a DOM event because ES modules are singletons: a late
// subscriber can still ask what the theme IS, where an event it missed would
// simply be gone.

let current = 'universe';
const listeners = new Set();

export function getTheme() { return current; }

export function publishTheme(t) {
    const next = t === 'brain' ? 'brain' : 'universe';
    // On the root element too, so CSS can key off it without a class dance.
    document.documentElement.dataset.theme = next;
    if (next === current) return;
    current = next;
    for (const fn of listeners) {
        try { fn(next); } catch (e) { console.warn('[theme] listener failed', e); }
    }
}

export function onThemeChange(fn) {
    listeners.add(fn);
    return () => listeners.delete(fn);
}
