// assistant.js — mounts the assistant into the Universe HUD and exposes the
// one surface the WPF host drives her through.
//
// The host talks to this via CoreWebView2.ExecuteScriptAsync, which is the
// existing pattern in this app and needs no new plumbing:
//
//   window.brainxAssistant.configure({name:'มายด์', female:true})
//   window.brainxAssistant.say('https://voice.local/abc.mp3', {mood:'pleased'})
//   window.brainxAssistant.mood('concerned')
//
// She stays hidden until the first say(): an idle face staring out of the
// corner of a dashboard is a distraction, and the Universe is already busy.

import { AssistantFace, MOODS } from './face.js';

const wrap  = document.getElementById('assistant-face');
const stage = document.getElementById('assistant-face-stage');
const label = document.getElementById('assistant-face-name');

let face = null;
let cfg = { name: '', female: true };

function ensure() {
    if (!face && stage) face = new AssistantFace(stage, cfg.female);
    return face;
}

function show() {
    if (wrap?.hidden) wrap.hidden = false;
    // Unhiding a zero-size element leaves the renderer at its boot dimensions,
    // so the canvas has to be re-measured once it is actually laid out.
    requestAnimationFrame(() => {
        if (face && stage?.clientWidth) face.resize(stage.clientWidth, stage.clientHeight);
    });
}

const api = {
    /** name + which head to draw. The head follows the VOICE's gender. */
    configure({ name, female } = {}) {
        if (typeof name === 'string') {
            cfg.name = name;
            if (label) label.textContent = name;
            // The chat panel is a conversation with HER, so it carries her
            // name rather than the word "CHAT". Set from the same place the
            // face label is, so the two can never show different names.
            const title = document.getElementById('mind-chat-title');
            if (title) title.textContent = name;
        }
        if (typeof female === 'boolean') {
            cfg.female = female;
            if (face) face.setFemale(female);
        }
        ensure();
        return true;
    },

    /**
     * Speak an audio URL, optionally with a mood. Resolves when playback ends.
     * Rejects only on a genuine playback failure — a browser that blocks
     * autoplay throws here, and the caller needs to know that rather than
     * watching a silent face.
     */
    async say(url, { mood = null, keepMood = false } = {}) {
        ensure(); show();
        if (mood) face.setMood(mood);
        try {
            await face.speak(url);
        } finally {
            // Settle back to neutral so the last thing she said does not leave
            // her permanently worried.
            if (!keepMood) setTimeout(() => face.setMood('neutral'), 1200);
        }
        return true;
    },

    mood(m) { ensure(); show(); face.setMood(m); return true; },
    moods() { return Object.keys(MOODS); },
    stop()  { face?.stop(); return true; },
    hide()  { face?.stop(); if (wrap) wrap.hidden = true; return true; },
    get ready() { return !!face; },
    /** The live AssistantFace. Exposed for the host and for diagnostics —
     *  verifying that she actually DRAWS means forcing a frame and reading
     *  the pixels back, which needs the instance, not just this facade. */
    get face() { return face; },
};

window.brainxAssistant = api;

window.addEventListener('resize', () => {
    if (face && stage?.clientWidth) face.resize(stage.clientWidth, stage.clientHeight);
});
