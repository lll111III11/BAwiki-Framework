(function () {
  'use strict';

  var LS_KEY = 'ba.sfx.v3';
  var SCENE_GAIN = 0.30;
  var VOICE_VOL = 0.70;
  var MAX_VOICE_SEC = 6;
  var ctx = null, master = null, voiceGain = null;
  var voiceSrc = null;
  var lobbyEl = null;
  var state = { scene: true, voice: true, lobby: true, vol: 0.6, lobbyVol: 0.5 };

  try {
    var raw = localStorage.getItem(LS_KEY) || localStorage.getItem('ba.sfx.v2');
    if (raw) {
      var s = JSON.parse(raw);
      if (typeof s.scene === 'boolean') state.scene = s.scene;
      if (typeof s.voice === 'boolean') state.voice = s.voice;
      if (typeof s.lobby === 'boolean') state.lobby = s.lobby;
      if (typeof s.vol === 'number') state.vol = Math.max(0, Math.min(1, s.vol));
      if (typeof s.lobbyVol === 'number') state.lobbyVol = Math.max(0, Math.min(1, s.lobbyVol));
    }
  } catch (e) {}

  function save() {
    try { localStorage.setItem(LS_KEY, JSON.stringify(state)); } catch (e) {}
  }

  function build() {
    if (ctx) return ctx;
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return null;
    try { ctx = new AC(); } catch (e) { return null; }
    master = ctx.createGain();
    master.gain.value = SCENE_GAIN * state.vol;
    master.connect(ctx.destination);
    voiceGain = ctx.createGain();
    voiceGain.gain.value = VOICE_VOL;
    voiceGain.connect(ctx.destination);
    return ctx;
  }

  function unlock() {
    if (!state.scene && !state.voice) return;
    var c = build();
    if (c && c.state === 'suspended') { try { c.resume(); } catch (e) {} }
  }
  try {
    document.addEventListener('pointerdown', unlock, true);
    document.addEventListener('keydown', unlock, true);
  } catch (e) {}

  function ready() {
    if (!state.scene && !state.voice) return null;
    var c = build();
    if (!c) return null;
    if (c.state === 'suspended') { try { c.resume(); } catch (e) {} }
    return c;
  }

  function tone(freq, dur, type, gain, delay) {
    var c = ready();
    if (!c) return;
    var t0 = c.currentTime + (delay || 0);
    var o = c.createOscillator();
    var g = c.createGain();
    o.type = type || 'sine';
    o.frequency.setValueAtTime(freq, t0);
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(Math.max(0.0002, gain || 0.2), t0 + 0.005);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    o.connect(g); g.connect(master);
    o.start(t0);
    o.stop(t0 + dur + 0.03);
  }

  var SCENE = {
    home:     function () { tone(587, 0.05, 'sine', 0.20); tone(880, 0.07, 'sine', 0.14, 0.05); },
    students: function () { tone(784, 0.045, 'sine', 0.18); tone(1046, 0.06, 'sine', 0.12, 0.045); },
    gallery:  function () { tone(523, 0.045, 'sine', 0.17); tone(784, 0.065, 'sine', 0.12, 0.045); },
    music:    function () { tone(659, 0.05, 'triangle', 0.18); tone(988, 0.07, 'triangle', 0.12, 0.05); },
    videos:   function () { tone(392, 0.055, 'sine', 0.18); tone(587, 0.065, 'sine', 0.11, 0.055); },
    bilibili: function () { tone(740, 0.045, 'sine', 0.17); tone(1109, 0.055, 'sine', 0.10, 0.045); },
    online:   function () { tone(880, 0.04, 'sine', 0.16); tone(1175, 0.05, 'sine', 0.10, 0.04); },
    me:       function () { tone(1046, 0.04, 'sine', 0.16); tone(1318, 0.05, 'sine', 0.10, 0.04); }
  };

  var api = {

    scene: function (name) {
      if (!state.scene) return false;
      var f = SCENE[name];
      if (!f) return false;
      f();
      return true;
    },

    voice: function (url) {
      if (!state.voice || !url) return false;
      var c = ready();
      if (!c) return false;
      var list = Array.isArray(url) ? url.slice(0, 6) : [url];
      var idx = 0, fallback = null;
      function tryNext() {
        if(idx >= list.length){
          if(fallback) play(fallback);
          return;
        }
        var u = list[idx++];
        fetch(u).then(function (r) { return r.arrayBuffer(); })
          .then(function (ab) { return c.decodeAudioData(ab); })
          .then(function (buf) {
            if(!state.voice) return;
            if(!fallback || buf.duration < fallback.duration) fallback = {buf: buf, duration: buf.duration};
            if(buf.duration <= MAX_VOICE_SEC){ play(buf); return; }
            tryNext();
          }).catch(function () { tryNext(); });
      }
      function play(buf) {
        if(!state.voice) return;
        if (voiceSrc) { try { voiceSrc.stop(); } catch (e) {} }
        voiceSrc = c.createBufferSource();
        voiceSrc.buffer = buf;
        voiceSrc.connect(voiceGain);
        voiceSrc.onended = function () { voiceSrc = null; };
        voiceSrc.start();
      }
      try { tryNext(); } catch (e) {}
      return true;
    },

    stopVoice: function () { if (voiceSrc) { try { voiceSrc.stop(); } catch (e) {} voiceSrc = null; } },

    lobbyPlay: function (url) {
      if (!state.lobby || !url) return false;
      try {
        if (!lobbyEl) {
          lobbyEl = new Audio();
          lobbyEl.preload = 'auto';
          lobbyEl.loop = true;
        }
        if (lobbyEl.src !== url) { lobbyEl.src = url; }
        lobbyEl.volume = state.lobbyVol;
        var p = lobbyEl.play();
        if (p && p.catch) p.catch(function () {});
        return true;
      } catch (e) { return false; }
    },
    lobbyPause: function () { if (lobbyEl) { try { lobbyEl.pause(); } catch (e) {} } },
    lobbyStop: function () {
      if (lobbyEl) { try { lobbyEl.pause(); lobbyEl.removeAttribute('src'); } catch (e) {} }
    },
    get lobbyPlaying() { return !!(lobbyEl && !lobbyEl.paused); },
    setLobbyVol: function (v) {
      state.lobbyVol = Math.max(0, Math.min(1, +v || 0));
      if (lobbyEl) lobbyEl.volume = state.lobbyVol;
      save();
      return state.lobbyVol;
    },
    get lobbyVol() { return state.lobbyVol; },
    setLobby: function (v) {
      state.lobby = !!v;
      if (!state.lobby) api.lobbyPause();
      save();
      return state.lobby;
    },
    get lobbyOn() { return state.lobby; },

    setScene: function (v) {
      state.scene = !!v;

      if (!state.scene && !state.voice && ctx) { try { ctx.suspend(); } catch (e) {} }
      else unlock();
      save();
      return state.scene;
    },
    setVoice: function (v) {
      state.voice = !!v;
      if (!state.voice) api.stopVoice();
      save();
      return state.voice;
    },
    get sceneOn() { return state.scene; },
    get voiceOn() { return state.voice; },
    setVolume: function (v) {
      state.vol = Math.max(0, Math.min(1, +v || 0));
      if (master) master.gain.value = SCENE_GAIN * state.vol;
      save();
      return state.vol;
    },
    get volume() { return state.vol; },

    preview: function () { if (state.scene) SCENE.music(); }
  };

  window.Sfx = api;
})();
