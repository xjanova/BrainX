// chat.js — talk to her. Typed, or spoken.
//
// The panel posts to the WPF host over the existing WebMessage channel and
// gets the answer back through window.brainxChat.reply(). The host does the
// thinking (AiHubService, which already injects brain context) and the
// speaking (brainx-mcp speak); this file is the mouth and ears only.
//
// MIC. Web Speech API, which in this WebView2 resolves to Edge's recogniser —
// the runtime component is present on this machine, and the page is served
// from https://universe.local, a real origin. getUserMedia and SpeechRecognition
// are both blocked outright on file://, which is the single reason this works
// at all. Thai is requested explicitly; the recogniser falls back to whatever
// it has if th-TH is unavailable, and that is reported rather than hidden.

const el = (id) => document.getElementById(id);

let recog = null;
let listening = false;
let busy = false;

function post(msg) {
    try { window.chrome?.webview?.postMessage(msg); }
    catch { /* running outside the host, e.g. the test harness */ }
}

function bubble(who, text, pending = false) {
    const log = el('mind-chat-log');
    if (!log) return null;
    const d = document.createElement('div');
    d.className = `mind-msg mind-msg-${who}` + (pending ? ' mind-msg-pending' : '');
    d.textContent = text;
    log.appendChild(d);
    log.scrollTop = log.scrollHeight;
    return d;
}

let pendingEl = null;

function send(text) {
    text = (text || '').trim();
    if (!text || busy) return;
    busy = true;
    bubble('me', text);
    const input = el('mind-chat-input');
    if (input) input.value = '';
    pendingEl = bubble('her', '…', true);
    // 'thinking' while she works: the face is the only progress indicator this
    // panel has, and a silent unmoving head during a 10 s local model call
    // reads as broken.
    window.brainxAssistant?.mood('thinking');
    post({ type: 'mind.ask', text });
}

// ── mic ──────────────────────────────────────────────────────────────────
function initRecognition() {
    const SR = window.SpeechRecognition || window.webkitSpeechRecognition;
    if (!SR) return null;
    const r = new SR();
    r.lang = 'th-TH';
    r.continuous = false;
    r.interimResults = true;
    r.maxAlternatives = 1;

    r.onresult = (ev) => {
        let finalText = '', interim = '';
        for (let i = ev.resultIndex; i < ev.results.length; i++) {
            const t = ev.results[i][0].transcript;
            if (ev.results[i].isFinal) finalText += t; else interim += t;
        }
        const input = el('mind-chat-input');
        if (input) input.value = finalText || interim;
        // Send only on a FINAL result. Sending interim text means she answers
        // half a sentence, which is worse than waiting.
        if (finalText) { stopListening(); send(finalText); }
    };
    r.onerror = (ev) => {
        stopListening();
        const why = ev.error === 'not-allowed' ? 'microphone blocked'
                  : ev.error === 'no-speech'   ? 'heard nothing'
                  : ev.error;
        setStatus(`mic: ${why}`);
    };
    r.onend = () => stopListening();
    return r;
}

function setStatus(s) { const n = el('mind-chat-status'); if (n) n.textContent = s || ''; }

function startListening() {
    recog = recog || initRecognition();
    if (!recog) { setStatus('mic: not supported here'); return; }
    try { recog.start(); } catch { return; }   // start() throws if already running
    listening = true;
    el('mind-mic-btn')?.classList.add('listening');
    setStatus('listening…');
}

function stopListening() {
    listening = false;
    el('mind-mic-btn')?.classList.remove('listening');
    if (setStatus) setStatus('');
    try { recog?.stop(); } catch {}
}

// ── host → page ──────────────────────────────────────────────────────────
window.brainxChat = {
    /** The host calls this with her answer. */
    reply(text, ok = true) {
        busy = false;
        if (pendingEl) { pendingEl.textContent = text; pendingEl.classList.remove('mind-msg-pending'); pendingEl = null; }
        else bubble('her', text);
        if (!ok) window.brainxAssistant?.mood('sorry');
        const log = el('mind-chat-log');
        if (log) log.scrollTop = log.scrollHeight;
        return true;
    },
    open() { el('mind-chat')?.removeAttribute('hidden'); el('mind-chat-input')?.focus(); return true; },
    close() { el('mind-chat')?.setAttribute('hidden', ''); return true; },
    toggle() {
        const p = el('mind-chat');
        if (!p) return false;
        if (p.hidden) this.open(); else this.close();
        return !p.hidden;
    },
    status: setStatus,
    get busy() { return busy; },
};

// ── wiring ───────────────────────────────────────────────────────────────
window.addEventListener('DOMContentLoaded', () => {
    el('mind-chat-send')?.addEventListener('click', () => send(el('mind-chat-input')?.value));
    el('mind-chat-input')?.addEventListener('keydown', (e) => {
        // Enter sends, Shift+Enter is a newline — the convention every chat box
        // has, and getting it backwards is instantly infuriating.
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(e.target.value); }
    });
    el('mind-mic-btn')?.addEventListener('click', () => listening ? stopListening() : startListening());
    el('mind-chat-close')?.addEventListener('click', () => window.brainxChat.close());
});
