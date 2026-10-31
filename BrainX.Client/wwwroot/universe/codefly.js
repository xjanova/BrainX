// Where a code dashboard flight lands. Both themes draw code now: the Universe at
// its core, the brain in the thalamus. Kept apart from app.js, which needs a
// browser to import, so a plain node check can hold the answers still.

/** Fly to a symbol, or to its repo when the symbol is not drawn. Says where it went:
 *  'node' · 'galaxy' · 'none'. */
export function flyToCode(scene, { id, category }) {
    if (!scene) return 'none';
    if (id && scene.focusNodeId?.(id)) return 'node';
    return category && scene.focusGalaxy(category) ? 'galaxy' : 'none';
}
