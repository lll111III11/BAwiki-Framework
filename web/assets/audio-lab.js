(function () {
  'use strict';

  const LS_KEY = 'ba.audiolab.v1';
  const BANDS = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

  const PRESETS = {
    flat:    { name: '原声（平直）', eq: [0,0,0,0,0,0,0,0,0,0], gain: 1, comp: false },
    vocal:   { name: '人声强化',   eq: [-2,-1,0,2,4,4,3,1,0,-1], gain: 1, comp: true },
    music:   { name: '器乐解析',   eq: [1,2,1,0,-1,0,1,2,2,1], gain: 1, comp: false },
    night:   { name: '夜间轻听',   eq: [-4,-3,-2,-1,0,0,-1,-2,-4,-6], gain: 0.85, comp: true },
    cinema:  { name: '影院增强',   eq: [3,4,2,0,-1,1,3,4,3,1], gain: 1, comp: true }
  };

  const MODES = [
    { k: 'stereo',   n: '直通立体声',     d: '不做空间处理，最保真' },
    { k: 'wide',     n: '宽立体声',       d: 'Mid/Side 增强，左右分离感更强' },
    { k: 'surround', n: '虚拟环绕（HRTF）', d: '左右声道分置 ±60°，双耳定位' },
    { k: 'rear',     n: '虚拟后置环绕',   d: '延迟+低通模拟后方反射，影院感' },
    { k: 'dolby',    n: '杜比环绕模拟',   d: '6 声道虚拟上混 + HRTF 耳机环绕 + 瞬态收紧' }
  ];

  const state = {
    on: false,
    preset: 'flat',
    eq: PRESETS.flat.eq.slice(),
    gain: 1,
    pan: 0,
    comp: false,
    mode: 'stereo',
    sinkId: '',
    ready: false,
    error: '',
    autoMsg: '',
    autoOk: null,
    caps: null,
    dolby: null,
    sys: null,
    codecs: null,
    rec: false,
    anticlip: false,
    anticlipK: 2,
    anticlipMode: 'auto',
    toneComp: 1,
    learnOn: true,
    ldte: null,
    useLdte: false,
    fullRecord: false,
    clips: [],
    clipMsg: '',
    learnedInfo: null,
    clipCount: 0,
    baseline: null,
    abOff: false,
    bridge: { checked: false, ok: false, info: null, error: '', msg: '', dolby: 'off', checking: false,
              scheme: (function(){ var v = localStorage.getItem('ba.audio.scheme'); return (v === 'fidelity' || v === 'sim' || v === 'attempt') ? v : 'attempt'; })(),
              rate: +(localStorage.getItem('ba.audio.rate') || 0),
              deviceId: localStorage.getItem('ba.audio.dev') === null ? -1 : +localStorage.getItem('ba.audio.dev'),
              routeOn: localStorage.getItem('ba.audio.route') !== '0',
              routeMsg: '' },
    devices: { outputs: [], inputs: [] },
    net: null,
    exportMsg: ''
  };

  let ctx = null, srcNode = null, chain = null, el = null;
  let analyser = null, postAnalyser = null, recDest = null, recorder = null, recChunks = [];
  let makeupGain = 1, agcTimer = null, clipTimer = null;
  let learnSession = null, learnTimer = null, learnAcc = null;
  let probeNode = null, probeLoading = null, probeFailed = false;
  const LDTE_FRAME_MS = 16;
  const LDTE_CHANNELS = 12;
  let panel = null, tab = 'cap';

  function save() {
    try {
      localStorage.setItem(LS_KEY, JSON.stringify({
        on: state.on, preset: state.preset, eq: state.eq, gain: state.gain,
        pan: state.pan, comp: state.comp, mode: state.mode, sinkId: state.sinkId,
        anticlip: state.anticlip, anticlipK: state.anticlipK,
        anticlipMode: state.anticlipMode, toneComp: state.toneComp
      }));
    } catch (e) {}
  }
  function load() {
    try {
      const o = JSON.parse(localStorage.getItem(LS_KEY) || '{}');
      Object.keys(o).forEach(k => { if (k in state && o[k] != null) state[k] = o[k]; });

      if (state.mode === 'wasapi') state.mode = 'stereo';
    } catch (e) {}
  }

  const FORMATS = [
    { key: 'mp3',  mime: 'audio/mpeg' },
    { key: 'ogg/vorbis', mime: 'audio/ogg; codecs="vorbis"' },
    { key: 'ogg/opus',   mime: 'audio/ogg; codecs="opus"' },
    { key: 'flac', mime: 'audio/flac' },
    { key: 'wav',  mime: 'audio/wav' },
    { key: 'm4a/aac', mime: 'audio/mp4; codecs="mp4a.40.2"' },
    { key: 'webm/opus', mime: 'audio/webm; codecs="opus"' },
    { key: 'aac',  mime: 'audio/aac' }
  ];

  function probeFormats() {
    const a = document.createElement('audio');
    return FORMATS.map(f => {
      let v = '';
      try { v = a.canPlayType(f.mime) || ''; } catch (e) {}
      return { key: f.key, mime: f.mime, support: v === 'probably' ? '确定支持' : (v === 'maybe' ? '可能支持' : '不支持'), raw: v };
    });
  }

  async function probeDecodeReal() {
    if (!ctx) return null;
    try {
      const sr = 44100, n = Math.round(sr * 0.2);
      const buf = new Float32Array(n);
      for (let i = 0; i < n; i++) buf[i] = Math.sin(2 * Math.PI * 440 * i / sr) * 0.2;
      const wav = encodeWav(buf, sr);
      const t0 = performance.now();
      const decoded = await ctx.decodeAudioData(wav.slice(0));
      return {
        ok: true, ms: Math.round(performance.now() - t0),
        duration: +decoded.duration.toFixed(3), channels: decoded.numberOfChannels, sampleRate: decoded.sampleRate
      };
    } catch (e) {
      return { ok: false, error: String(e && e.message || e) };
    }
  }

  function encodeWav(samples, sampleRate) {
    const n = samples.length;
    const buf = new ArrayBuffer(44 + n * 2);
    const v = new DataView(buf);
    const ws = (o, s) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
    ws(0, 'RIFF'); v.setUint32(4, 36 + n * 2, true); ws(8, 'WAVE');
    ws(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
    v.setUint32(24, sampleRate, true); v.setUint32(28, sampleRate * 2, true);
    v.setUint16(32, 2, true); v.setUint16(34, 16, true);
    ws(36, 'data'); v.setUint32(40, n * 2, true);
    let o = 44;
    for (let i = 0; i < n; i++, o += 2) {
      const s = Math.max(-1, Math.min(1, samples[i]));
      v.setInt16(o, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
    }
    return buf;
  }

  async function probeContext() {
    const dup = !ctx;
    if (!ctx) {
      try { ctx = new (window.AudioContext || window.webkitAudioContext)(); }
      catch (e) { return { ok: false, error: 'AudioContext 不可用：' + e.message }; }
    }
    const out = {
      ok: true,
      sampleRate: ctx.sampleRate,
      state: ctx.state,
      baseLatency: ctx.baseLatency != null ? +(ctx.baseLatency * 1000).toFixed(2) : null,
      outputLatency: ctx.outputLatency != null ? +(ctx.outputLatency * 1000).toFixed(2) : null,
      maxChannels: ctx.destination.maxChannelCount || 2,
      channelCountMode: ctx.destination.channelCountMode,
      setSinkId: typeof (el && el.setSinkId) === 'function' || typeof AudioContext.prototype.setSinkId === 'function'
    };
    return out;
  }

  async function probeDevices(requestPerm) {
    if (!navigator.mediaDevices || !navigator.mediaDevices.enumerateDevices) {
      return { outputs: [], inputs: [], error: '浏览器不提供设备枚举' };
    }
    let mic = null;
    try {
      if (requestPerm) mic = await navigator.mediaDevices.getUserMedia({ audio: true });
    } catch (e) {   }
    let list = [];
    try { list = await navigator.mediaDevices.enumerateDevices(); } catch (e) { list = []; }
    if (mic) { mic.getTracks().forEach(t => t.stop()); }
    const outputs = [], inputs = [];
    list.forEach(d => {
      const isDefault = d.deviceId === '' || d.deviceId === 'default';
      const label = d.label || (isDefault
        ? (d.kind === 'audiooutput' ? '系统默认输出' : '系统默认输入')
        : (d.kind === 'audiooutput' ? '输出设备 ' : '输入设备 ') + d.deviceId.slice(0, 6));
      const item = { id: d.deviceId, label, kind: d.kind, groupId: d.groupId };
      if (d.kind === 'audiooutput') {
        if (isDefault) return;
        outputs.push(item);
      }
      if (d.kind === 'audioinput') inputs.push(item);
    });

    return { outputs, inputs };
  }

  function probeNet() {
    const c = navigator.connection || navigator.mozConnection || navigator.webkitConnection;
    const r = { online: navigator.onLine, type: c && c.type || '', effectiveType: c && c.effectiveType || '',
                downlink: c && c.downlink != null ? c.downlink : null, rtt: c && c.rtt != null ? c.rtt : null,
                saveData: !!(c && c.saveData), cores: navigator.hardwareConcurrency || null, mem: navigator.deviceMemory || null };
    return r;
  }

  async function sniffRemote(rel) {
    try {
      const r = await fetch(mediaUrl(rel), { headers: { Range: 'bytes=0-1023' } });
      if (!r.ok) return { ok: false, error: 'HTTP ' + r.status };
      const b = new Uint8Array(await r.arrayBuffer());
      const magic = arr => arr.join(' ');
      const head = b.slice(0, 12);
      let fmt = '未知';
      if (head[0] === 0x4F && head[1] === 0x67 && head[2] === 0x67 && head[3] === 0x53) fmt = 'OGG';
      else if (head[0] === 0x66 && head[1] === 0x4C && head[2] === 0x61 && head[3] === 0x43) fmt = 'FLAC';
      else if (head[0] === 0x52 && head[1] === 0x49 && head[2] === 0x46 && head[3] === 0x46) fmt = 'WAV';
      else if ((head[0] === 0xFF && (head[1] & 0xE0) === 0xE0)) fmt = 'MP3';
      else if (head[4] === 0x66 && head[5] === 0x74 && head[6] === 0x79 && head[7] === 0x70) fmt = 'M4A/MP4';
      return { ok: true, format: fmt, first: magic(head), size: r.headers.get('Content-Range') };
    } catch (e) {
      return { ok: false, error: String(e && e.message || e) };
    }
  }

  function probeSystem() {
    const ua = navigator.userAgent;
    const isEdge = /Edg\//.test(ua);
    const isChrome = /Chrome\//.test(ua) && !isEdge;
    const isFF = /Firefox\//.test(ua);
    const isSafari = /Safari\//.test(ua) && !isChrome && !isEdge;
    const isWindows = /Windows/.test(ua);
    const engine = isEdge ? 'Edge' : isChrome ? 'Chrome' : isFF ? 'Firefox' : isSafari ? 'Safari' : '其他';
    const platform = isWindows ? 'Windows'
      : /Android/.test(ua) ? 'Android'
      : /iPhone|iPad|iPod/.test(ua) ? 'iOS'
      : /Mac OS X/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : '未知';
    const mse = ct => { try { return !!(window.MediaSource && MediaSource.isTypeSupported(ct)); } catch (e) { return false; } };
    const maxCh = (state.caps && state.caps.ctx && state.caps.ctx.maxChannels) || 2;
    return {
      engine, platform, isWindows, isEdge, isChrome,
      mseAc3: mse('audio/mp4; codecs="ac-3"'),
      mseEac3: mse('audio/mp4; codecs="ec-3"'),
      maxChannels: maxCh,
      multiOut: maxCh > 2,
      osBuild: platform === 'Windows' ? 'Windows（24H2 起系统已移除内置 AC-3 解码器）' : platform
    };
  }

  function openDolbyStore() {
    const web = 'https://apps.microsoft.com/detail/9n0866fs04w8';
    try { location.href = 'ms-windows-store://pdp/?productid=9N0866FS04W8'; } catch (e) {}
    setTimeout(() => { try { window.open(web, '_blank', 'noopener'); } catch (e) {} }, 900);
    return { ok: true, store: web };
  }

  function openInEdge() {
    const u = location.href;
    try { location.href = 'microsoft-edge:' + u; return { ok: true }; }
    catch (e) { return { ok: false, error: '当前系统没有注册 Edge 协议' }; }
  }

  const BRIDGE = 'http://127.0.0.1:8236';

  const BRIDGE_DOLBY = [
    { k: 'off',    n: '杜比：关闭（按声道直出）' },
    { k: 'dd',     n: '杜比数字 5.1 直解' },
    { k: 'dsu',    n: 'Dolby Surround 上混（立体声→5.1）' },
    { k: 'cinema', n: '杜比影院增强（对白+低频+压缩）' },
    { k: 'night',  n: '杜比夜间模式（响度均衡+压缩）' }
  ];

  async function bridgeCheck() {
    if (state.bridge.checking) return state.bridge;
    state.bridge.checking = true;
    refreshPanel();
    try {
      const r = await fetch(BRIDGE + '/health?t=' + Date.now(), { cache: 'no-store' });
      if (!r.ok) throw new Error('HTTP ' + r.status);
      const j = await r.json();
      state.bridge = Object.assign({}, state.bridge, { checked: true, ok: true, info: j, error: '', checking: false });
    } catch (e) {
      state.bridge = Object.assign({}, state.bridge, { checked: true, ok: false, info: null, error: '未检测到本地引擎（先下载并双击运行 BAudioBridge.exe）', checking: false });
    }
    refreshPanel();
    return state.bridge;
  }

  async function bridgePlay() {
    const cur = el || document.getElementById('audio');
    if (!cur || !cur.currentSrc) {
      state.bridge.msg = '当前没有在播放的曲目，先点一首';
      refreshPanel();
      return { ok: false, error: 'no-track' };
    }
    const scheme = state.bridge.scheme || 'attempt';
    const ch = scheme === 'fidelity' ? 6 : (state.bridge.info && state.bridge.info.multichannel ? 6 : 2);
    const dolby = state.bridge.dolby || 'off';
    try {
      const r = await fetch(BRIDGE + '/play', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: cur.currentSrc, channels: ch, dolby: dolby, scheme: scheme, rate: state.bridge.rate || 0, device: state.bridge.deviceId })
      });
      const j = await r.json();
      if (j.ok) {
        try { cur.pause(); } catch (e) {}
        state.bridge.msg = '已交给本地引擎 · ' + j.channels + ' 声道 · ' + j.rate + ' Hz · ' + (j.dolbyName || '杜比关闭') + (j.note ? ' · ' + j.note : '') + '（网页播放已暂停）';
        state.bridge.checked = true;
        state.bridge.ok = true;
      } else {
        state.bridge.msg = '引擎拒绝：' + (j.error || '未知');
      }
      return j;
    } catch (e) {
      state.bridge.msg = '本地引擎未运行，先双击 BAudioBridge.exe';
      state.bridge.checked = true;
      state.bridge.ok = false;
      return { ok: false, error: String(e && e.message || e) };
    } finally { refreshPanel(); }
  }

  async function bridgeStop() {
    try {
      await fetch(BRIDGE + '/stop', { method: 'POST' });
      state.bridge.msg = '已停止本地播放';
    } catch (e) {
      state.bridge.msg = '本地引擎未运行';
    }
    refreshPanel();
    return { ok: true };
  }

  function bridgeOpen() {
    try { window.open(BRIDGE + '/', '_blank', 'noopener'); return { ok: true }; }
    catch (e) { return { ok: false, error: '打不开引擎界面' }; }
  }

  async function openEngineUi() {
    await bridgeCheck();
    if (!state.bridge.ok) {
      state.bridge.msg = '引擎还没启动：先下载并双击运行 BAudioBridge.exe（首次运行 Windows 会问一次是否允许）';
      refreshPanel();
      return { ok: false, error: '引擎未启动' };
    }
    try {
      const w = window.open(BRIDGE + '/', 'ba-engine', 'width=1100,height=820');
      if (!w) {
        state.bridge.msg = '窗口被浏览器拦截：请允许本站弹窗后重试';
        refreshPanel();
        return { ok: false, error: '弹窗被拦截' };
      }
      state.bridge.msg = '已打开引擎界面 · 波形在页面顶部实时滚动';
      refreshPanel();
      return { ok: true };
    } catch (e) {
      return { ok: false, error: e.message };
    }
  }

  function bridgeDolby(v) {
    state.bridge.dolby = v || 'off';
    refreshPanel();
    return { ok: true, dolby: state.bridge.dolby };
  }

  async function probeDolby() {
    const cases = [
      { name: 'AC-3 · 杜比数字 5.1', ct: 'audio/mp4; codecs="ac-3"', ch: 6 },
      { name: 'E-AC-3 · 杜比数字+ 5.1', ct: 'audio/mp4; codecs="ec-3"', ch: 6 },
      { name: 'AC-4 · 杜比 AC-4', ct: 'audio/mp4; codecs="ac-4"', ch: 2 },
      { name: 'AAC 5.1 · 多声道 AAC', ct: 'audio/mp4; codecs="mp4a.40.2"', ch: 6 },
      { name: 'Opus 5.1', ct: 'audio/webm; codecs="opus"', ch: 6 },
      { name: 'FLAC 5.1', ct: 'audio/ogg; codecs="flac"', ch: 6 }
    ];
    const mc = navigator.mediaCapabilities;
    const rows = [];
    for (const c of cases) {
      let canPlay = '';
      try { canPlay = document.createElement('audio').canPlayType(c.ct) || ''; } catch (e) {}
      let info = null;
      if (mc && mc.decodingInfo) {
        try {
          const r = await mc.decodingInfo({ type: 'file', audio: { contentType: c.ct, channels: c.ch, bitrate: 640000, samplerate: 48000 } });
          info = { supported: !!r.supported, smooth: !!r.smooth, power: !!r.powerEfficient };
        } catch (e) { info = { error: String(e && e.message || e) }; }
      }
      rows.push({ name: c.name, ct: c.ct, canPlay, info });
    }
    const anyDolby = rows.slice(0, 2).some(r => (r.info && r.info.supported) || r.canPlay === 'probably' || r.canPlay === 'maybe');
    return { rows, anyDolby, hasApi: !!(mc && mc.decodingInfo) };
  }

  async function probeCodecs() {
    const mr = [];
    if (window.MediaRecorder && MediaRecorder.isTypeSupported) {
      ['audio/webm;codecs=opus', 'audio/ogg;codecs=opus', 'audio/mp4;codecs=mp4a.40.2', 'audio/wav'].forEach(t => {
        let ok = false;
        try { ok = MediaRecorder.isTypeSupported(t); } catch (e) {}
        mr.push({ t, ok });
      });
    }
    const we = [];
    if (window.AudioEncoder && AudioEncoder.isConfigSupported) {
      const cfgs = [
        { codec: 'opus', sampleRate: 48000, numberOfChannels: 2, bitrate: 128000 },
        { codec: 'mp4a.40.2', sampleRate: 48000, numberOfChannels: 2, bitrate: 128000 },
        { codec: 'flac', sampleRate: 48000, numberOfChannels: 2, bitrate: 0 },
        { codec: 'pcm-s16', sampleRate: 48000, numberOfChannels: 2, bitrate: 0 }
      ];
      for (const c of cfgs) {
        try { const r = await AudioEncoder.isConfigSupported(c); we.push({ codec: c.codec, ok: !!r.supported }); }
        catch (e) { we.push({ codec: c.codec, ok: false, error: String(e && e.message || e) }); }
      }
    }
    return { mr, we, hasMR: !!window.MediaRecorder, hasEncoder: !!window.AudioEncoder };
  }

  async function fullProbe() {
    const caps = {
      formats: probeFormats(),
      ctx: await probeContext(),
      decode: await probeDecodeReal(),
      devices: await probeDevices(false),
      net: probeNet(),
      ua: navigator.userAgent
    };
    state.caps = caps;
    state.devices = caps.devices;
    state.net = caps.net;
    state.dolby = await probeDolby();
    state.sys = probeSystem();
    state.codecs = await probeCodecs();
    return caps;
  }

  function buildChain() {
    if (!ctx || !srcNode) return false;
    try {
      return buildChainInner();
    } catch (e) {
      state.ready = false;
      state.error = '处理链构建失败：' + (e && e.name ? e.name + ' ' : '') + (e && e.message || e);
      return false;
    }
  }

  function buildChainInner() {
    if (chain && chain.nodes) {
      chain.nodes.forEach(n => { try { n.disconnect(); } catch (e) {} });
    }
    const nodes = [];
    const preGain = ctx.createGain();
    preGain.gain.value = 1;
    nodes.push(preGain);

    const filters = BANDS.map((f, i) => {
      const bq = ctx.createBiquadFilter();
      bq.type = 'peaking';
      bq.frequency.value = f;
      bq.Q.value = 1.0;
      bq.gain.value = (state.eq[i] || 0) + ldteEqNudge(i);
      nodes.push(bq);
      return bq;
    });

    let panner = null;
    if (ctx.createStereoPanner) {
      panner = ctx.createStereoPanner();
      panner.pan.value = state.pan;
      nodes.push(panner);
    }

    let splitter = null, merger = null;
    const extra = [];
    if ((state.mode === 'wide' || state.mode === 'surround' || state.mode === 'rear' || state.mode === 'dolby') && ctx.createChannelSplitter) {
      splitter = ctx.createChannelSplitter(2);
      merger = ctx.createChannelMerger(2);
      extra.push(splitter, merger);
    }

    const comp = (state.comp || state.mode === 'dolby') ? ctx.createDynamicsCompressor() : null;
    if (comp) nodes.push(comp);
    const outGain = ctx.createGain();
    outGain.gain.value = state.gain * ldteGainMul();
    nodes.push(outGain);

    let head = preGain;
    filters.forEach(f => { head.connect(f); head = f; });
    if (panner) { head.connect(panner); head = panner; }

    const tail = comp || outGain;

    if (state.mode === 'wide' && splitter) {
      head.connect(splitter);
      const k = 0.55;
      const kkPos = ctx.createGain(); kkPos.gain.value = k;
      const kkNeg = ctx.createGain(); kkNeg.gain.value = -k;
      splitter.connect(merger, 0, 0);
      splitter.connect(merger, 1, 1);
      splitter.connect(kkPos, 0, 0);
      splitter.connect(kkNeg, 1, 0);
      kkPos.connect(merger, 0, 0);
      kkNeg.connect(merger, 0, 0);
      const kkPos2 = ctx.createGain(); kkPos2.gain.value = k;
      const kkNeg2 = ctx.createGain(); kkNeg2.gain.value = -k;
      splitter.connect(kkPos2, 1, 0);
      splitter.connect(kkNeg2, 0, 0);
      kkPos2.connect(merger, 0, 1);
      kkNeg2.connect(merger, 0, 1);
      merger.connect(tail);
      extra.push(kkPos, kkNeg, kkPos2, kkNeg2);
    } else if (state.mode === 'surround' && splitter && ctx.createPanner) {
      head.connect(splitter);
      const pl = ctx.createPanner(), pr = ctx.createPanner();
      [[pl, -1.6], [pr, 1.6]].forEach(([p, x]) => {
        p.panningModel = 'HRTF';
        p.distanceModel = 'inverse';
        p.refDistance = 1.2;
        if (p.positionX) { p.positionX.value = x; p.positionY.value = 0; p.positionZ.value = 0.6; }
        else p.setPosition(x, 0, 0.6);
      });
      splitter.connect(pl, 0, 0);
      splitter.connect(pr, 1, 0);
      pl.connect(merger, 0, 0);
      pr.connect(merger, 0, 1);
      merger.connect(tail);
      extra.push(pl, pr);
    } else if (state.mode === 'dolby' && splitter && ctx.createPanner) {

      const hp = ctx.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = 30; hp.Q.value = 0.6;
      const mud = ctx.createBiquadFilter(); mud.type = 'peaking'; mud.frequency.value = 330; mud.Q.value = 0.9; mud.gain.value = -2.2;
      const air = ctx.createBiquadFilter(); air.type = 'highshelf'; air.frequency.value = 6400; air.gain.value = 1.8;
      head.connect(hp); hp.connect(mud); mud.connect(air); air.connect(splitter);

      const bus = ctx.createGain(); bus.gain.value = 0.58;
      bus.connect(tail);
      extra.push(hp, mud, air, bus);

      const speaker = (x, z, gain, src) => {
        const p = ctx.createPanner();
        p.panningModel = 'HRTF';
        p.distanceModel = 'inverse';
        p.refDistance = 1.8;
        p.maxDistance = 20;
        p.rolloffFactor = 0.35;
        if (p.positionX) { p.positionX.value = x; p.positionY.value = 0; p.positionZ.value = z; }
        else p.setPosition(x, 0, z);
        const g = ctx.createGain(); g.gain.value = gain;
        src.connect(p); p.connect(g); g.connect(bus);
        extra.push(p, g);
      };

      const gl = ctx.createGain(); gl.gain.value = 0.92;
      const gr = ctx.createGain(); gr.gain.value = 0.92;
      splitter.connect(gl, 0, 0);
      splitter.connect(gr, 1, 0);
      speaker(-1.75, 0.85, 0.95, gl);
      speaker(1.75, 0.85, 0.95, gr);

      const gc = ctx.createGain(); gc.gain.value = 0.40;
      splitter.connect(gc, 0, 0);
      splitter.connect(gc, 1, 0);
      speaker(0, 1.55, 0.46, gc);

      const invL = ctx.createGain(); invL.gain.value = -1;
      const invR = ctx.createGain(); invR.gain.value = -1;
      splitter.connect(invL, 0, 0);
      splitter.connect(invR, 1, 0);
      const sl = ctx.createGain(); sl.gain.value = 0.50;
      const sr = ctx.createGain(); sr.gain.value = 0.50;
      splitter.connect(sl, 0, 0);
      invR.connect(sl, 0, 0);
      splitter.connect(sr, 1, 0);
      invL.connect(sr, 0, 0);
      const dl = ctx.createDelay(0.08); dl.delayTime.value = 0.014;
      const dr = ctx.createDelay(0.08); dr.delayTime.value = 0.016;
      const ll = ctx.createBiquadFilter(); ll.type = 'lowpass'; ll.frequency.value = 6800;
      const lr = ctx.createBiquadFilter(); lr.type = 'lowpass'; lr.frequency.value = 6800;
      sl.connect(dl); dl.connect(ll);
      sr.connect(dr); dr.connect(lr);
      speaker(-1.55, -1.05, 0.38, ll);
      speaker(1.55, -1.05, 0.38, lr);
      extra.push(invL, invR, gl, gr, gc, sl, sr, dl, dr, ll, lr);

      const lf = ctx.createBiquadFilter(); lf.type = 'lowpass'; lf.frequency.value = 95; lf.Q.value = 0.7;
      const lg = ctx.createGain(); lg.gain.value = 0.32;
      splitter.connect(lf, 0, 0);
      splitter.connect(lf, 1, 0);
      lf.connect(lg); lg.connect(bus);
      extra.push(lf, lg);

      if (comp) { comp.threshold.value = -18; comp.knee.value = 6; comp.ratio.value = 2.2; comp.attack.value = 0.004; comp.release.value = 0.16; }
    } else if (state.mode === 'rear' && splitter) {
      head.connect(splitter);
      splitter.connect(merger, 0, 0);
      splitter.connect(merger, 1, 1);
      const dl = ctx.createDelay(0.2); dl.delayTime.value = 0.018;
      const lp = ctx.createBiquadFilter(); lp.type = 'lowpass'; lp.frequency.value = 3200;
      const rg = ctx.createGain(); rg.gain.value = 0.34;
      splitter.connect(dl, 0, 0);
      splitter.connect(dl, 1, 0);
      dl.connect(lp); lp.connect(rg);
      rg.connect(merger, 0, 0);
      rg.connect(merger, 0, 1);
      merger.connect(tail);
      extra.push(dl, lp, rg);
    } else {
      head.connect(tail);
    }

    nodes.push(...extra);
    if (comp) comp.connect(outGain);

    let tail2 = outGain, anti = null;
    const antiOn = state.anticlip && !state.abOff && state.anticlipMode !== 'monitor';
    if (antiOn) {
      const effK = Math.min(2.6, Math.max(1, state.anticlipK));
      const ws = ctx.createWaveShaper();
      ws.curve = state.anticlipMode === 'hard' ? hardCurve(antiLimit()) : softCurve(effK, antiLimit());
      ws.oversample = '4x';
      const hp = ctx.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = 24;
      const low = ctx.createBiquadFilter(); low.type = 'lowshelf'; low.frequency.value = 95; low.gain.value = 0.6 * state.toneComp;
      const pres = ctx.createBiquadFilter(); pres.type = 'peaking'; pres.frequency.value = 1600; pres.Q.value = 0.7; pres.gain.value = 0.8 * state.toneComp;
      const mk = ctx.createGain(); mk.gain.value = Math.min(1, makeupGain);

      const safe = ctx.createWaveShaper();
      safe.curve = hardCurve(Math.min(0.985, antiLimit() + 0.02));
      safe.oversample = 'none';
      outGain.connect(ws); ws.connect(hp); hp.connect(low); low.connect(pres); pres.connect(mk); mk.connect(safe);
      tail2 = safe;
      anti = { ws, hp, low, pres, mk, safe };
      nodes.push(ws, hp, low, pres, mk, safe);
    } else {

    }
    if (state.on) startAgc(); else stopAgc();

    if (state.learnOn && probeNode) {
      try { probeNode.disconnect(); } catch (e) {}
      tail2.connect(probeNode);
      probeNode.connect(ctx.destination);
    } else {
      tail2.connect(ctx.destination);
    }

    if (!analyser && ctx.createAnalyser) {
      analyser = ctx.createAnalyser();
      analyser.fftSize = 2048;
      analyser.smoothingTimeConstant = 0.82;
    }
    if (analyser) { try { analyser.disconnect(); } catch (e) {} outGain.connect(analyser); }

    if (!postAnalyser && ctx.createAnalyser) {
      postAnalyser = ctx.createAnalyser();
      postAnalyser.fftSize = 1024;
    }
    if (postAnalyser) tail2.connect(postAnalyser);

    if (recDest) tail2.connect(recDest);

    try { srcNode.disconnect(); } catch (e) {}
    srcNode.connect(preGain);

    chain = { nodes, preGain, filters, panner, outGain, comp, anti, tail2 };
    state.ready = true;
    state.error = '';
    if (state.learnOn) startLearnTimer();
    return true;
  }

  function softCurve(k, lim) {
    const n = 2049, c = new Float32Array(n);
    const kk = Math.max(0.6, Math.min(3.2, k || 2));
    const top = Math.max(0.80, Math.min(0.995, lim || 0.94));
    const norm = Math.tanh(kk);
    for (let i = 0; i < n; i++) {
      const x = (i / (n - 1)) * 2 - 1;
      c[i] = (Math.tanh(kk * x) / norm) * top;
    }
    return c;
  }

  function hardCurve(lim) {
    const n = 2049, c = new Float32Array(n);
    const top = Math.max(0.80, Math.min(0.995, lim || 0.94));
    for (let i = 0; i < n; i++) {
      const x = (i / (n - 1)) * 2 - 1;
      c[i] = Math.max(-top, Math.min(top, x));
    }
    return c;
  }

  function antiLimit() {
    const ldte = ldteSafeLimit();
    if (ldte != null) return ldte;
    if (state.anticlipMode === 'auto') {
      const b = state.baseline;
      if (b && b.learned && b.peak > 0.2) return Math.min(0.99, Math.max(0.90, b.peak * 0.995));
    }
    return 0.94;
  }

  function hasUserTuning() {
    const flat = (PRESETS.flat && PRESETS.flat.eq) || [];
    for (let i = 0; i < BANDS.length; i++) {
      if (Math.abs((+state.eq[i] || 0) - (+flat[i] || 0)) > 0.05) return true;
    }
    return Math.abs(state.gain - 1) > 0.01 || Math.abs(state.pan) > 0.01 ||
      !!state.comp || state.mode !== 'stereo' ||
      Math.abs(state.toneComp - 1) > 0.01 || !!state.anticlip;
  }

  function ldteActive() {
    return !!(state.useLdte && state.ldte && hasUserTuning());
  }

  function ldteTimbre() {
    const p = state.ldte && state.ldte.profiles;
    return (p && p.timbre) || null;
  }

  function ldteEqNudge(i) {
    if (!ldteActive()) return 0;
    const t = ldteTimbre();
    if (!t) return 0;
    const lo = +t.lowRatio, mid = +t.midRatio, hi = +t.highRatio;
    if (!isFinite(lo) || !isFinite(mid) || !isFinite(hi)) return 0;
    const f = BANDS[i];
    if (f <= 125) return Math.max(-2.5, Math.min(2.5, (0.38 - lo) * 5));
    if (f <= 2000) return Math.max(-1.5, Math.min(1.5, (0.30 - mid) * 3));
    return Math.max(-2.5, Math.min(2.5, (0.10 - hi) * 7));
  }

  function ldteGainMul() {
    if (!ldteActive()) return 1;
    const c = state.ldte.profiles && state.ldte.profiles.calibration;
    let db = c ? +c.suggestGainDb : 0;
    if (!isFinite(db)) db = 0;
    db = Math.max(-2, Math.min(2, db));
    return Math.pow(10, (db * 0.5) / 20);
  }

  function ldteSafeLimit() {
    if (!ldteActive()) return null;
    const c = +state.ldte.limit.ceiling;
    if (!isFinite(c)) return null;
    return Math.min(0.97, Math.max(0.86, Math.min(c, 0.94 + 0.015)));
  }

  function refreshLdteGains() {
    if (!chain) return;
    try {
      chain.filters.forEach((f, i) => { f.gain.value = (state.eq[i] || 0) + ldteEqNudge(i); });
      chain.outGain.gain.value = state.gain * ldteGainMul();
    } catch (e) {}
  }

  function getClip() {
    if (!analyser) return null;
    const f = new Float32Array(analyser.fftSize);
    analyser.getFloatTimeDomainData(f);
    let peak = 0, hot = 0;
    for (let i = 0; i < f.length; i++) {
      const a = f[i] < 0 ? -f[i] : f[i];
      if (a > peak) peak = a;
      if (a >= 0.995) hot++;
    }
    const db = 20 * Math.log10(peak || 1e-6);
    return { peak: +peak.toFixed(3), db: +db.toFixed(1), hot: +(hot / f.length * 100).toFixed(1) };
  }

  function getPost() {
    if (!postAnalyser) return null;
    const f = new Float32Array(postAnalyser.fftSize);
    postAnalyser.getFloatTimeDomainData(f);
    let peak = 0, sum = 0;
    for (let i = 0; i < f.length; i++) {
      const a = f[i] < 0 ? -f[i] : f[i];
      if (a > peak) peak = a;
      sum += f[i] * f[i];
    }
    return { peak: +peak.toFixed(3), rms: +Math.sqrt(sum / f.length).toFixed(4) };
  }

  function rmsOf(node) {
    if (!node) return 0;
    const f = new Float32Array(node.fftSize);
    node.getFloatTimeDomainData(f);
    let sum = 0;
    for (let i = 0; i < f.length; i++) sum += f[i] * f[i];
    return Math.sqrt(sum / f.length);
  }
  function startAgc() {
    if (agcTimer) return;
    agcTimer = setInterval(agcTick, 80);
  }
  function stopAgc() {
    if (agcTimer) { clearInterval(agcTimer); agcTimer = null; }
    makeupGain = 1;
  }

  function resetBaseline() {
    if (learnSession && learnSession.samples) postLearn(true);
    learnSession = null;
    state.baseline = { peak: 0, rmsSum: 0, samples: 0, loud: 0, learned: false, nativeLoud: false, startAt: performance.now() };
  }
  function learnBaseline() {
    let b = state.baseline;
    if (!b) { b = state.baseline = { peak: 0, rmsSum: 0, samples: 0, loud: 0, learned: false, nativeLoud: false, startAt: performance.now() }; }
    const c = getClip();
    if (!c) return;
    b.samples++;

    if (c.peak >= 0.98) b.loud++;
    if (b.loud >= 8) b.nativeLoud = true;

    if (b.learned) return;
    if (c.peak > b.peak) b.peak = c.peak;
    b.rmsSum += rmsOf(analyser);
    if (performance.now() - b.startAt > 8000 && b.samples > 40) {
      b.rms = b.rmsSum / b.samples;
      b.learned = true;

      if (state.on && state.anticlip && state.anticlipMode === 'auto') buildChain();
    }
  }

  function getClipState() {
    const c = getClip();
    if (!c) return null;
    const b = state.baseline;
    let rel = null, baseDb = null;
    if (b && b.learned && b.peak > 0.02) {
      rel = 20 * Math.log10(Math.max(c.peak, 1e-6) / b.peak);
      baseDb = 20 * Math.log10(b.peak);
    }
    const absClip = c.peak >= 0.999 || c.hot > 0.3;
    const relWarn = rel != null && rel > 6;
    const nativeLoud = !!(b && (b.nativeLoud || b.peak > 0.95));
    let level = 'ok';
    if (absClip && (relWarn || !nativeLoud)) level = 'clip';
    else if (relWarn) level = 'over';
    else if (c.peak >= 0.98 && !nativeLoud) level = 'near';
    return {
      peak: c.peak, db: c.db, hot: c.hot, level: level,
      rel: rel == null ? null : +rel.toFixed(1),
      baseDb: baseDb == null ? null : +baseDb.toFixed(1),
      learned: !!(b && b.learned),
      limit: +(antiLimit() * 100).toFixed(1)
    };
  }

  function agcTick() {
    learnBaseline();
    if (!state.anticlip || state.anticlipMode === 'monitor') return;
    if (!postAnalyser || !chain || !chain.anti) return;
    const preRms = rmsOf(analyser);
    const postRms = rmsOf(postAnalyser);
    if (preRms < 0.012 || postRms < 0.002) return;

    const postPeak = (getPost() || {}).peak || 0;
    const cap = state.anticlipK >= 2 ? 1.02 : 1.12;
    let want = Math.min(cap, Math.max(1.0, preRms / postRms));
    if (postPeak > 0.985) want = Math.min(want, 1.0);
    makeupGain = makeupGain * 0.85 + want * 0.15;
    makeupGain = Math.max(0.7, Math.min(cap, makeupGain));
    try { chain.anti.mk.gain.value = makeupGain; } catch (e) {}
  }

  function toggleAntiClip() {
    state.anticlip = !state.anticlip;
    save();
    if (state.on) buildChain();
    refreshPanel();
    return { ok: true, on: state.anticlip };
  }

  function setAntiClipK(v) {
    state.anticlipK = Math.max(1, Math.min(3, +v || 2));
    save();
    if (chain && chain.anti && state.anticlipMode !== 'monitor') {
      const k = Math.min(2.6, Math.max(1, state.anticlipK));
      try { chain.anti.ws.curve = state.anticlipMode === 'hard' ? hardCurve(antiLimit()) : softCurve(k, antiLimit()); } catch (e) {}
    }
    const lab = document.getElementById('al-clip-k');
    if (lab) lab.textContent = state.anticlipK.toFixed(1);
    return state.anticlipK;
  }

  function setAntiMode(m) {
    if (m !== 'auto' && m !== 'soft' && m !== 'hard' && m !== 'monitor') return state.anticlipMode;
    state.anticlipMode = m;
    save();
    if (state.on) buildChain();
    refreshPanel();
    return m;
  }

  function setToneComp(v) {
    state.toneComp = Math.max(0, Math.min(2, +v || 0));
    save();
    if (chain && chain.anti) {
      try {
        chain.anti.low.gain.value = 0.6 * state.toneComp;
        chain.anti.pres.gain.value = 0.8 * state.toneComp;
      } catch (e) {}
    }
    const lab = document.getElementById('al-tc');
    if (lab) lab.textContent = state.toneComp.toFixed(1);
    return state.toneComp;
  }

  function abBypass(on) {
    if (state.abOff === on) return;
    state.abOff = !!on;
    if (state.on) buildChain();
  }

  try { window.addEventListener('blur', function () { if (state.abOff) abBypass(false); }); } catch (e) {}

  function resetClipCount() {
    state.clipCount = 0;
    holdPeak = 0;
    holdHot = 0;
    const el2 = document.getElementById('al-clip-cnt');
    if (el2) el2.textContent = '0 次';
    return { ok: true };
  }

  let holdPeak = 0, holdHot = 0;
  function updateClipReadout() {
    if (!panel) return;
    const dbEl = document.getElementById('al-clip-db');
    if (!dbEl) return;
    const stEl = document.getElementById('al-clip-st');
    const hotEl = document.getElementById('al-clip-hot');
    const baseEl = document.getElementById('al-clip-base');
    const c = state.on ? getClip() : null;
    if (!c) {
      holdPeak = 0; holdHot = 0;
      dbEl.textContent = '—';
      if (hotEl) hotEl.textContent = '—';
      if (baseEl) baseEl.textContent = '—';
      if (stEl) stEl.innerHTML = '<span class="al-tip">尚未接管播放</span>';
      return;
    }

    holdPeak = Math.max(c.peak, holdPeak * 0.94);
    holdHot = Math.max(c.hot, holdHot * 0.85);
    const db = 20 * Math.log10(holdPeak || 1e-6);
    const b = state.baseline;
    let baseDb = null, rel = null, nativeLoud = false;
    if (b && b.learned && b.peak > 0.02) {
      baseDb = 20 * Math.log10(b.peak);
      rel = 20 * Math.log10(Math.max(holdPeak, 1e-6) / b.peak);
      nativeLoud = !!(b.nativeLoud || b.peak > 0.95);
    }
    const absClip = holdPeak >= 0.999 || holdHot > 0.3;
    const relWarn = rel != null && rel > 6;
    let level = 'ok';
    if (absClip && (relWarn || !nativeLoud)) level = 'clip';
    else if (relWarn) level = 'over';
    else if (holdPeak >= 0.98 && !nativeLoud) level = 'near';
    if (level === 'clip' || level === 'over') state.clipCount++;

    dbEl.innerHTML = `<span class="${level === 'ok' ? 'al-ok' : 'al-bad'}">${db.toFixed(1)} dBFS</span>`;
    if (hotEl) hotEl.textContent = holdHot.toFixed(1) + ' %';
    if (baseEl) {
      baseEl.innerHTML = b && b.learned
        ? `${baseDb.toFixed(1)} dBFS${nativeLoud ? ' · 素材本身高振幅' : ''}`
        : `<span class="al-tip">学习中…（还需 ${Math.max(0, Math.ceil(8 - (performance.now() - (b ? b.startAt : performance.now())) / 1000))} 秒）</span>`;
    }
    const mkEl = document.getElementById('al-clip-mk');
    if (mkEl) mkEl.textContent = makeupGain.toFixed(2) + 'x' + (makeupGain > 1.001 ? '（仅还原）' : '（不放大）');
    const cntEl = document.getElementById('al-clip-cnt');
    if (cntEl) cntEl.textContent = state.clipCount + ' 次';
    if (stEl) {
      if (level === 'clip') {
        stEl.innerHTML = `<span class="al-bad">⚠ 真实削顶${rel != null ? '（高于素材基线 +' + rel.toFixed(1) + ' dB）' : ''}${state.anticlip ? (state.abOff ? ' · 对比中已旁路' : ' · 已缓释') : ''}</span>`;
      } else if (level === 'over') {
        stEl.innerHTML = `<span class="al-warn">⚠ 高于素材基线 +${rel.toFixed(1)} dB（疑似增益/均衡抬升）${state.anticlip && !state.abOff ? ' · 已缓释' : ''}</span>`;
      } else if (level === 'near') {
        stEl.innerHTML = `<span class="al-warn">接近满量程，但素材基线不高（${nativeLoud ? '素材本身高振幅' : '可开启缓释'}）</span>`;
      } else {
        stEl.innerHTML = `<span class="al-ok">波形正常${nativeLoud ? '（素材本身就到 0dBFS，按基线判定不报警）' : ''}</span>`;
      }
    }
  }

  function ensureGraph() {
    if (!el) el = document.getElementById('audio');
    if (!el) { state.error = '页面上找不到 audio 元素'; return false; }
    const Ctx = window.AudioContext || window.webkitAudioContext;
    if (!Ctx) { state.error = '此浏览器不支持 Web Audio'; return false; }
    if (!ctx) {
      try { ctx = new Ctx(); }
      catch (e) { state.error = 'AudioContext 创建失败：' + (e && e.message || e); return false; }
    }
    if (!srcNode) {
      try { srcNode = ctx.createMediaElementSource(el); }
      catch (e) {
        state.error = '接入音频元素失败：' + (e && e.name ? e.name + ' ' : '') + (e && e.message || e);
        return false;
      }
    }
    ensureProbe();
    return true;
  }

  function ensureProbe() {
    if (probeNode || probeFailed || !ctx) return !!probeNode;
    if (!ctx.audioWorklet || !window.AudioWorkletNode) { probeFailed = true; return false; }
    if (probeLoading) return false;
    let base = 'assets/audio-lab-worklet.js?v=20261113h';
    try { base = new URL('assets/audio-lab-worklet.js?v=20261113h', location.href).href; } catch (e) {}
    probeLoading = ctx.audioWorklet.addModule(base)
      .then(() => {
        probeNode = new AudioWorkletNode(ctx, 'bawl-probe', {
          numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2],
          channelCount: 2, channelCountMode: 'explicit', channelInterpretation: 'speakers'
        });
        probeNode.port.onmessage = (ev) => {
          if (ev.data && ev.data.f) pushLearnFrame(ev.data.f);
        };

        if (state.on && chain && !chain.learnOnly) buildChain();
        else if (chain && chain.learnOnly) buildLearnGraph();
        return true;
      })
      .catch((e) => {
        probeFailed = true;
        probeLoading = null;
        state.error = '16ms 训练探针加载失败：' + ((e && e.message) || e) + '（已退化为 100ms 采样）';
        return false;
      });
    return false;
  }

  function pushLearnFrame(f) {
    if (!state.learnOn) return;
    if (!el || el.paused || el.ended) return;
    const np = window.__baNowPlaying;
    if (!np || !np.key) return;
    if (!learnSession || learnSession.key !== np.key) startLearnSession(np);
    const s = learnSession;
    s.samples++;
    const peak = f[0], rms = f[1];
    if (peak > s.peak) s.peak = peak;
    s.rmsSum += rms;
    const hot = f[11] * 100;
    if (hot > s.hot) s.hot = hot;
    if ((el.currentTime || 0) > s.seconds) s.seconds = el.currentTime || 0;

    const row = new Array(LDTE_CHANNELS);
    for (let c = 0; c < LDTE_CHANNELS; c++) row[c] = +f[c].toFixed(4);
    s.frames.push(row);
  }

  function attachMedia(audioEl) {
    if (audioEl) el = audioEl;

    if (!window.__alUnloadHooked) {
      window.__alUnloadHooked = true;
      window.addEventListener('pagehide', () => { if (learnSession) postLearn(true); });
    }
    if (el && !el.__alBaseline) {
      el.__alBaseline = true;
      el.addEventListener('loadedmetadata', resetBaseline);
      el.addEventListener('emptied', resetBaseline);
      el.addEventListener('ended', () => { if (learnSession) { postLearn(true); learnSession = null; } });

      el.addEventListener('pause', () => {
        if (learnSession && learnSession.frames.length) { postLearn(true); learnSession = null; }
      });
      el.addEventListener('seeking', () => {
        if (learnSession && learnSession.frames.length) { postLearn(true); learnSession = null; }
      });

      el.addEventListener('timeupdate', learnSample);
    }
    if (!state.on) {
      if (!state.learnOn) return true;
      if (!ensureGraph()) return false;
      if (!state.ready || !chain || !chain.learnOnly) return buildLearnGraph();
      startLearnTimer();
      return true;
    }
    if (!ensureGraph()) return false;

    if (ctx.state === 'suspended') { try { ctx.resume(); } catch (e) {} }
    if (!state.ready || !chain) return buildChain();
    return true;
  }

  function detachMedia() {
    if (!srcNode || !ctx) return;
    try { srcNode.disconnect(); } catch (e) {}
    if (state.learnOn) { buildLearnGraph(); return; }
    try { srcNode.connect(ctx.destination); } catch (e) {}
    state.ready = false;
  }

  function buildLearnGraph() {
    if (!srcNode || !ctx) return false;
    try { srcNode.disconnect(); } catch (e) {}
    try { if (analyser) analyser.disconnect(); } catch (e) {}
    const g = ctx.createGain();
    g.gain.value = 1;
    if (!analyser && ctx.createAnalyser) {
      analyser = ctx.createAnalyser();
      analyser.fftSize = 2048;
      analyser.smoothingTimeConstant = 0.82;
    }
    srcNode.connect(g);
    let tailNode = g;
    if (state.learnOn && probeNode) {
      try { probeNode.disconnect(); } catch (e) {}
      g.connect(probeNode);
      tailNode = probeNode;
    }
    if (analyser) { tailNode.connect(analyser); analyser.connect(ctx.destination); }
    else { tailNode.connect(ctx.destination); }
    chain = { learnOnly: true, head: g, nodes: [g] };
    state.ready = true;
    startLearnTimer();
    return true;
  }

  function startLearnTimer() {
    if (learnTimer) return;
    learnTimer = setInterval(learnTick, 120);
  }
  function stopLearnTimer() {
    if (learnTimer) { clearInterval(learnTimer); learnTimer = null; }
  }
  function startLearnSession(np) {
    learnSession = {
      key: np.key, name: np.name || '', peak: 0, rmsSum: 0, samples: 0,
      hot: 0, seconds: 0, first: true, lastPost: performance.now(),
      frames: [], lastRms: 0, lastPeak: 1e-4,

      startFrame: Math.max(0, Math.round(((el && el.currentTime) || 0) * 1000 / LDTE_FRAME_MS)),
      frameMs: probeNode ? LDTE_FRAME_MS : 100
    };
    learnAcc = null;
    loadLearnedProfile(np.key);
    if (state.fullRecord) ldteFullCapture(np);
  }

  function learnSample() {
    if (probeNode) return;
    if (!state.learnOn || !analyser || !el || el.paused || el.ended) return;
    const np = window.__baNowPlaying;
    if (!np || !np.key) return;
    if (!learnSession || learnSession.key !== np.key) startLearnSession(np);
    const f = new Float32Array(analyser.fftSize);
    analyser.getFloatTimeDomainData(f);
    let peak = 0, sum = 0, hot = 0, zc = 0, prev = 0;
    for (let i = 0; i < f.length; i++) {
      const v = f[i], a = v < 0 ? -v : v;
      if (a > peak) peak = a;
      if (a >= 0.995) hot++;
      sum += v * v;
      if ((prev < 0 && v >= 0) || (prev >= 0 && v < 0)) zc++;
      prev = v;
    }
    const rms = Math.sqrt(sum / f.length);
    const s = learnSession;
    s.samples++;
    if (peak > s.peak) s.peak = peak;
    s.rmsSum += rms;
    const hp = hot / f.length * 100;
    if (hp > s.hot) s.hot = hp;
    if ((el.currentTime || 0) > s.seconds) s.seconds = el.currentTime || 0;

    s.frames.push([
      +peak.toFixed(4), +rms.toFixed(5), +(zc / f.length).toFixed(4),
      +Math.abs(rms - s.lastRms).toFixed(4), 0, 0, 0, 0, 0, 1,
      +Math.min(8, peak / Math.max(1e-4, s.lastPeak)).toFixed(3),
      +(hot / f.length).toFixed(4)
    ]);
    s.lastRms = rms;
    s.lastPeak = Math.max(peak, 1e-4);
  }
  function learnTick() {
    learnSample();
    const s = learnSession;
    if (!s) return;
    if (performance.now() - s.lastPost > 15000) postLearn(false);
  }
  function postLearn(final) {
    const s = learnSession;
    if (!s || !s.samples || !s.key) return;
    const rms = s.samples ? s.rmsSum / s.samples : 0;
    s.first = false;
    s.lastPost = performance.now();

    if (s.frames.length) {
      const nf = s.frames.length;
      const cols = [];
      for (let c = 0; c < LDTE_CHANNELS; c++) {
        const a = new Array(nf);
        for (let i = 0; i < nf; i++) a[i] = s.frames[i][c] || 0;
        cols.push(a);
      }
      try {
        const payload = JSON.stringify({
          key: s.key, name: s.name, seconds: Math.round(s.seconds),
          frameMs: s.frameMs || LDTE_FRAME_MS, startFrame: s.startFrame || 0,
          sampleRate: (ctx && ctx.sampleRate) || 48000,
          channels: cols
        });
        const opt = { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload };

        if (payload.length < 60000) opt.keepalive = true;
        fetch('/api/ldte/train', opt).then(r => r.json()).then(p => {
          if (p && p.ok) {
            s.sentAt = performance.now();
            loadLdteSystem(false);
          } else if (p && p.error) {
            state.error = '训练数据写入失败：' + p.error;
          }
        }).catch(e => {
          state.error = '训练数据写入失败：' + ((e && e.message) || e);
        });
        if (final) s.frames = [];
      } catch (e) {
        state.error = '训练数据序列化失败：' + ((e && e.message) || e);
      }
    }
  }

  function loadLdteSystem(ask) {
    try {
      fetch('/api/ldte/system')
        .then(r => r.json())
        .then(sys => {
          if (!sys) return;
          state.fullRecord = !!sys.fullRecord;
          if (!sys.learned || !sys.limit) return;
          state.ldte = sys;
          let choice = '';
          try { choice = localStorage.getItem('ba.ldte.use') || ''; } catch (e) {}
          if (choice === '1') { state.useLdte = true; return; }
          if (choice === '0') { state.useLdte = false; return; }
          if (ask === false) return;
          const n = sys.trackCount || 0;
          const fr = (sys.stats && sys.stats.frames) || 0;
          const ok = window.confirm('检测到全音频训练数据（' + n + ' 首 / ' + fr + ' 帧）。\n是否使用训练数据来获得更好的动态表现（不破坏音频的软限幅）？\n\n选「取消」后不再询问，可在音频实验室重置。');
          state.useLdte = ok;
          try { localStorage.setItem('ba.ldte.use', ok ? '1' : '0'); } catch (e) {}
          if (ok && state.on && chain && !chain.learnOnly) buildChain();
        })
        .catch(() => {});
    } catch (e) {}
  }

  function loadLearnedProfile(key) {
    try {

      state.learnedInfo = null;
      fetch('/api/ldte/get?key=' + encodeURIComponent(key))
        .then(r => r.json())
        .then(p => {
          if (!p || !p.learned) return;
          const peak = (p.stats && +p.stats.maxPeak) || 0;
          const plays = +p.passes || 0;
          if (!plays && !peak) return;
          state.learnedInfo = { plays: plays, peak: peak, rms: (p.stats && +p.stats.avgRms) || 0 };
          if (peak > 0) {
            if (!state.baseline) state.baseline = { peak: 0, samples: 0, learned: false };
            state.baseline.peak = Math.max(state.baseline.peak || 0, peak);
            state.baseline.learned = true;
            state.baseline.nativeLoud = peak >= 0.95;
          }
          if (state.on && state.anticlip && state.anticlipMode === 'auto' && chain && !chain.learnOnly) buildChain();
        })
        .catch(() => {});
      loadLdteSystem(true);
    } catch (e) {}
  }
  function getLevels(n) {
    if (!analyser) return null;
    n = n || 12;
    const arr = new Uint8Array(analyser.frequencyBinCount);
    analyser.getByteFrequencyData(arr);
    const top = Math.floor(arr.length * 0.55);
    const out = [];
    for (let i = 0; i < n; i++) {
      const a = Math.floor(Math.pow(i / n, 1.6) * top);
      const b = Math.max(a + 1, Math.floor(Math.pow((i + 1) / n, 1.6) * top));
      let m = 0;
      for (let j = a; j < b && j < arr.length; j++) if (arr[j] > m) m = arr[j];
      out.push(+(m / 255).toFixed(3));
    }
    return out;
  }

  function hasAnalyser() { return !!analyser; }

  function wavFromBuffer(buf) {
    const ch = buf.numberOfChannels, n = buf.length, sr = buf.sampleRate;
    const ab = new ArrayBuffer(44 + n * ch * 2);
    const v = new DataView(ab);
    const ws = (o, s) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
    ws(0, 'RIFF'); v.setUint32(4, 36 + n * ch * 2, true); ws(8, 'WAVE');
    ws(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, ch, true);
    v.setUint32(24, sr, true); v.setUint32(28, sr * ch * 2, true);
    v.setUint16(32, ch * 2, true); v.setUint16(34, 16, true);
    ws(36, 'data'); v.setUint32(40, n * ch * 2, true);
    const chans = [];
    for (let c = 0; c < ch; c++) chans.push(buf.getChannelData(c));
    let o = 44;
    for (let i = 0; i < n; i++) {
      for (let c = 0; c < ch; c++) {
        const s = Math.max(-1, Math.min(1, chans[c][i]));
        v.setInt16(o, s < 0 ? s * 0x8000 : s * 0x7FFF, true); o += 2;
      }
    }
    return ab;
  }

  function download(blob, name) {
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { try { URL.revokeObjectURL(a.href); a.remove(); } catch (e) {} }, 5000);
  }

  function trackTitle() {
    const t = (document.getElementById('pl-title') || {}).textContent || '';
    return (t && t !== '未播放' ? t : 'track').replace(/[\\/:*?"<>|]/g, '_');
  }

  async function exportWav(seconds) {
    const cur = el || document.getElementById('audio');
    if (!cur || !cur.currentSrc) return { ok: false, error: '当前没有在下载的音频' };
    const sec = Math.max(1, Math.min(600, +seconds || 30));
    const OAC = window.OfflineAudioContext || window.webkitOfflineAudioContext;
    if (!OAC) return { ok: false, error: '浏览器不支持 OfflineAudioContext' };
    try {
      state.exportMsg = '正在下载音频…'; refreshPanel();
      const ab = await (await fetch(cur.currentSrc)).arrayBuffer();
      state.exportMsg = '正在解码…'; refreshPanel();
      const tmp = new OAC(1, 1, 48000);
      const buf = await tmp.decodeAudioData(ab.slice(0));
      const dur = Math.min(sec, buf.duration);
      const sr = buf.sampleRate;
      const oc = new OAC(2, Math.round(dur * sr), sr);
      const src = oc.createBufferSource(); src.buffer = buf;
      let head = src;
      BANDS.forEach((f, i) => {
        const bq = oc.createBiquadFilter();
        bq.type = 'peaking'; bq.frequency.value = f; bq.Q.value = 1; bq.gain.value = state.eq[i] || 0;
        head.connect(bq); head = bq;
      });
      if (state.comp || state.mode === 'dolby') {
        const c = oc.createDynamicsCompressor(); head.connect(c); head = c;
      }

      if (state.anticlip && state.anticlipMode !== 'monitor' && !state.abOff) {
        const ws = oc.createWaveShaper();
        ws.curve = state.anticlipMode === 'hard' ? hardCurve(0.94) : softCurve(state.anticlipK, 0.94);
        ws.oversample = '4x';
        const hp = oc.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = 24;
        const low = oc.createBiquadFilter(); low.type = 'lowshelf'; low.frequency.value = 95; low.gain.value = 0.6 * state.toneComp;
        const pres = oc.createBiquadFilter(); pres.type = 'peaking'; pres.frequency.value = 1600; pres.Q.value = 0.7; pres.gain.value = 0.8 * state.toneComp;
        head.connect(ws); ws.connect(hp); hp.connect(low); low.connect(pres);
        const safe = oc.createWaveShaper();
        safe.curve = hardCurve(0.96);
        safe.oversample = 'none';
        pres.connect(safe);
        head = safe;
      }
      const g = oc.createGain(); g.gain.value = state.gain;
      head.connect(g); g.connect(oc.destination);
      src.start(0, 0, dur);
      state.exportMsg = '正在渲染 ' + dur.toFixed(1) + ' 秒…'; refreshPanel();
      const rendered = await oc.startRendering();
      const wav = wavFromBuffer(rendered);
      const file = trackTitle() + '（' + Math.round(dur) + 's）.wav';
      state.exportMsg = '导出完成：' + (wav.byteLength / 1048576).toFixed(1) + ' MB · ' + fileNameSafe(file);
      download(new Blob([wav], { type: 'audio/wav' }), file);
      return { ok: true, bytes: wav.byteLength, seconds: dur, name: file };
    } catch (e) {
      state.exportMsg = '导出失败：' + (e && e.message || e);
      return { ok: false, error: String(e && e.message || e) };
    } finally { refreshPanel(); }
  }

  function fileNameSafe(s) { return String(s).replace(/[\\/:*?"<>|]/g, '_'); }

  function recordStart() {
    if (!state.on) return { ok: false, error: '请先启用接管播放，否则没有可录的信号' };
    if (!ensureGraph()) return { ok: false, error: state.error };
    if (!ctx.createMediaStreamDestination) return { ok: false, error: '浏览器不支持 MediaStreamDestination' };
    if (!window.MediaRecorder) return { ok: false, error: '浏览器不支持 MediaRecorder' };
    const mime = MediaRecorder.isTypeSupported('audio/webm;codecs=opus') ? 'audio/webm;codecs=opus'
      : (MediaRecorder.isTypeSupported('audio/ogg;codecs=opus') ? 'audio/ogg;codecs=opus' : '');
    if (!mime) return { ok: false, error: '没有可用的录制编码格式' };
    const fresh = !chain || !state.ready;
    if (!recDest) recDest = ctx.createMediaStreamDestination();
    if (fresh) { if (!buildChain()) return { ok: false, error: state.error }; }
    else { try { chain.outGain.connect(recDest); } catch (e) {} }
    recChunks = [];
    recorder = new MediaRecorder(recDest.stream, { mimeType: mime, audioBitsPerSecond: 128000 });
    recorder.ondataavailable = e => { if (e.data && e.data.size) recChunks.push(e.data); };
    recorder.onstop = () => {
      const blob = new Blob(recChunks, { type: mime });
      const ext = mime.indexOf('ogg') >= 0 ? 'ogg' : 'webm';
      download(blob, trackTitle() + '-录制.' + ext);
      state.rec = false;
      state.exportMsg = '录制完成：' + (blob.size / 1024).toFixed(0) + ' KB';
      refreshPanel();
    };
    recorder.start(1000);
    state.rec = true;
    state.exportMsg = '录制中…（' + mime + '）';
    refreshPanel();
    return { ok: true, mime };
  }

  function recordStop() {
    if (recorder && recorder.state !== 'inactive') recorder.stop();
    return { ok: true };
  }

  function clipCtx() {
    const np = window.__baNowPlaying;
    const cur = el || document.getElementById('audio');
    return { np, cur };
  }

  async function clipRefresh() {
    try {
      const d = await (await fetch('/api/ldte/clip/list?t=' + Date.now())).json();
      state.clips = (d.items || []).slice(-40).reverse();
      state.clipMsg = '已归档 ' + (d.count || 0) + ' 段（AES-256-GCM 独立加密）';
    } catch (e) { state.clipMsg = '读取归档列表失败'; }
    refreshPanel();
  }

  async function clipDelete(id) {
    try {
      await fetch('/api/ldte/clip/delete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id }) });
    } catch (e) {}
    clipRefresh();
  }

  async function clipSave() {
    const { np, cur } = clipCtx();
    if (!np || !np.key) { state.clipMsg = '当前没有播放中的曲目'; refreshPanel(); return { ok: false, error: 'no track' }; }
    if (!cur || !cur.currentSrc) { state.clipMsg = '当前没有音频源'; refreshPanel(); return { ok: false, error: 'no src' }; }
    const sIn = document.getElementById('al-clip-start');
    const eIn = document.getElementById('al-clip-end');
    let s = Math.max(0, +((sIn || {}).value) || 0);
    let e = +((eIn || {}).value) || (s + 30);
    if (e <= s) e = s + 10;
    const OAC = window.OfflineAudioContext || window.webkitOfflineAudioContext;
    if (!OAC) { state.clipMsg = '浏览器不支持 OfflineAudioContext'; refreshPanel(); return { ok: false, error: 'no OAC' }; }
    state.clipMsg = '正在渲染选中段…'; refreshPanel();
    try {
      const ab = await (await fetch(cur.currentSrc)).arrayBuffer();
      const tmp = new OAC(1, 1, 48000);
      const buf = await tmp.decodeAudioData(ab.slice(0));
      e = Math.min(e, buf.duration);
      if (e - s < 0.5) { state.clipMsg = '选区太短'; refreshPanel(); return { ok: false, error: 'too short' }; }
      const sr = buf.sampleRate;
      const oc = new OAC(buf.numberOfChannels, Math.round((e - s) * sr), sr);
      const src = oc.createBufferSource(); src.buffer = buf;
      src.connect(oc.destination);
      src.start(0, s, e - s);
      const rendered = await oc.startRendering();
      const wav = wavFromBuffer(rendered);
      const url = '/api/ldte/clip/save?key=' + encodeURIComponent(np.key)
        + '&name=' + encodeURIComponent(np.name || trackTitle())
        + '&start=' + s.toFixed(2) + '&end=' + e.toFixed(2);
      const r = await (await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/octet-stream' }, body: wav })).json();
      if (!r || !r.ok) { state.clipMsg = '归档失败：' + ((r && r.error) || '未知错误'); refreshPanel(); return r || { ok: false }; }
      state.clipMsg = '已归档 ' + (wav.byteLength / 1048576).toFixed(1) + ' MB（独立加密）';
      clipRefresh();
      return r;
    } catch (err) {
      state.clipMsg = '归档失败：' + ((err && err.message) || err);
      refreshPanel();
      return { ok: false, error: String((err && err.message) || err) };
    }
  }

  async function ldteFullCapture(np) {
    if (!state.fullRecord || !np || !np.key) return;
    state.fullDone = state.fullDone || {};
    if (state.fullDone[np.key]) return;
    state.fullDone[np.key] = true;
    const cur = el || document.getElementById('audio');
    if (!cur || !cur.currentSrc) return;
    try {
      const d = await (await fetch('/api/ldte/clip/list?key=' + encodeURIComponent(np.key) + '&t=' + Date.now())).json();
      if ((d.items || []).some(x => x.full)) return;
      const ab = await (await fetch(cur.currentSrc)).arrayBuffer();
      const OAC = window.OfflineAudioContext || window.webkitOfflineAudioContext;
      if (!OAC) return;
      const tmp = new OAC(1, 1, 48000);
      const buf = await tmp.decodeAudioData(ab.slice(0));
      const CHUNK = 120;
      for (let off = 0; off < buf.duration; off += CHUNK) {
        const dur = Math.min(CHUNK, buf.duration - off);
        const sr = buf.sampleRate;
        const oc = new OAC(buf.numberOfChannels, Math.round(dur * sr), sr);
        const src = oc.createBufferSource(); src.buffer = buf;
        src.connect(oc.destination);
        src.start(0, off, dur);
        const rendered = await oc.startRendering();
        const wav = wavFromBuffer(rendered);
        const url = '/api/ldte/clip/save?key=' + encodeURIComponent(np.key)
          + '&name=' + encodeURIComponent(np.name || np.key)
          + '&start=' + off.toFixed(2) + '&end=' + (off + dur).toFixed(2) + '&full=1';
        await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/octet-stream' }, body: wav });
      }
      state.clipMsg = '全录制完成：' + (np.name || np.key);
    } catch (e) {
      state.clipMsg = '全录制失败：' + ((e && e.message) || e);
    }
  }

  function clipPlay(id) {
    try {
      const a = new Audio('/api/ldte/clip/get?id=' + encodeURIComponent(id));
      a.play();
    } catch (e) {}
  }

  function applyPreset(name) {
    const p = PRESETS[name];
    if (!p) return;
    state.preset = name;
    state.eq = p.eq.slice();
    state.gain = p.gain;
    state.comp = p.comp;
    if (chain) {
      refreshLdteGains();
      if (p.comp !== !!chain.comp && state.on) buildChain();
    }
    save();
    refreshPanel();
  }

  function setBand(i, v) {
    state.eq[i] = v;
    refreshLdteGains();
    state.preset = 'custom';
    save();
  }

  function setGain(v) {
    state.gain = v;
    refreshLdteGains();
    save();
  }

  function setPan(v) {
    state.pan = v;
    if (chain && chain.panner) chain.panner.pan.value = v;
    save();
  }

  function setMode(m) {
    state.mode = m;
    save();
    if (state.on) buildChain();
    refreshPanel();
  }

  async function setSink(id) {
    state.sinkId = id || '';
    save();
    try {

      if (ctx && srcNode && typeof ctx.setSinkId === 'function') await ctx.setSinkId(state.sinkId);
      else if (el && typeof el.setSinkId === 'function') await el.setSinkId(state.sinkId);
      else return { ok: false, error: '此浏览器不支持指定输出设备（需要 Chrome/Edge 110+）' };
      return { ok: true };
    } catch (e) {
      return { ok: false, error: (e && e.name ? e.name + ' ' : '') + (e && e.message || e) };
    }
  }

  async function toggle(on) {
    state.on = on;
    save();
    if (on) {
      if (!attachMedia(el || document.getElementById('audio'))) {
        state.on = false; save();
        return { ok: false, error: state.error || '接管失败' };
      }
      if (ctx && ctx.state === 'suspended') { try { await ctx.resume(); } catch (e) {} }
      return { ok: true };
    }
    detachMedia();
    return { ok: true };
  }

  const CSS = `
  .al-mask{position:fixed;inset:0;background:rgba(4,8,18,.66);backdrop-filter:blur(6px);z-index:80;display:flex;align-items:center;justify-content:center}
  .al-box{width:min(920px,94vw);max-height:88vh;overflow:auto;background:linear-gradient(160deg,#101728,#0b1120);border:1px solid rgba(255,255,255,.12);border-radius:18px;box-shadow:0 24px 80px rgba(0,0,0,.6);color:#e6ecf7;font:13px/1.6 "Microsoft YaHei",system-ui,sans-serif}
  .al-hd{display:flex;align-items:center;gap:12px;padding:16px 20px;border-bottom:1px solid rgba(255,255,255,.08);position:sticky;top:0;background:rgba(16,23,40,.96);z-index:2}
  .al-hd h3{margin:0;font-size:16px}
  .al-hd .x{margin-left:auto;background:none;border:0;color:#8fa0c0;font-size:20px;cursor:pointer}
  .al-tabs{display:flex;gap:6px;padding:10px 20px 0}
  .al-tab{padding:6px 14px;border-radius:999px;border:1px solid rgba(255,255,255,.12);background:rgba(255,255,255,.05);color:#a9b7d0;font-size:12.5px;cursor:pointer}
  .al-tab.on{background:#4a6cf7;border-color:#4a6cf7;color:#fff;font-weight:700}
  .al-body{padding:14px 20px 22px}
  .al-sec{background:rgba(255,255,255,.04);border:1px solid rgba(255,255,255,.08);border-radius:12px;padding:12px 14px;margin-bottom:12px}
  .al-sec h4{margin:0 0 8px;font-size:13px;color:#cfe0ff}
  .al-row{display:flex;align-items:center;gap:10px;margin:8px 0;flex-wrap:wrap}
  .al-row label{color:#8fa0c0;min-width:78px}
  .al-kv{display:grid;grid-template-columns:auto 1fr;gap:4px 12px;font-size:12.5px}
  .al-kv b{color:#8fa0c0;font-weight:400}
  .al-ok{color:#5fd39a}.al-warn{color:#ffcf6b}.al-bad{color:#ff7b7b}
  .al-btn{padding:6px 14px;border-radius:999px;border:1px solid rgba(255,255,255,.16);background:rgba(255,255,255,.08);color:#e6ecf7;cursor:pointer;font-size:12.5px}
  .al-btn:hover{background:rgba(74,108,247,.45)}
  .al-btn.on{background:#4a6cf7;border-color:#4a6cf7}
  .al-btn:disabled{opacity:.42;cursor:not-allowed}
  .al-btn:disabled:hover{background:rgba(255,255,255,.08)}
  .al-sel{background:#0e1626;color:#e6ecf7;border:1px solid rgba(255,255,255,.14);border-radius:8px;padding:6px 8px;font-size:12.5px;min-width:220px}
  .al-eq{display:grid;grid-template-columns:repeat(10,1fr);gap:6px;margin-top:8px}
  .al-eq .c{text-align:center}
  .al-eq input[type=range]{writing-mode:vertical-lr;direction:rtl;width:18px;height:120px;accent-color:#4a6cf7}
  .al-eq .f{font-size:11px;color:#8fa0c0}
  .al-eq .v{font-size:11px;color:#cfe0ff}
  .al-tip{font-size:11.5px;color:#7b8bab;line-height:1.7;margin-top:6px}
  .al-chip{display:inline-block;padding:2px 9px;border-radius:999px;font-size:11.5px;margin:2px 4px 2px 0;border:1px solid rgba(255,255,255,.12);background:rgba(255,255,255,.06)}
  `;

  function injectCss() {
    if (document.getElementById('al-css')) return;
    const s = document.createElement('style');
    s.id = 'al-css';
    s.textContent = CSS;
    document.head.appendChild(s);
  }

  function fmt(v, unit) { return v == null ? '—' : v + (unit || ''); }

  function renderProbe() {
    const c = state.caps;
    if (!c) return '<div class="al-tip">点「开始检测」读取浏览器与设备能力。</div>';
    const fm = c.formats.map(f => {
      const cls = f.support === '确定支持' ? 'al-ok' : (f.support === '可能支持' ? 'al-warn' : 'al-bad');
      return `<span class="al-chip ${cls}">${f.key}：${f.support}</span>`;
    }).join('');
    const d = c.decode || {};
    const out = state.devices.outputs || [], inn = state.devices.inputs || [];
    return `
      <div class="al-sec">
        <h4>① 音频解码能力</h4>
        <div>${fm}</div>
        <div class="al-kv" style="margin-top:8px">
          <b>实测解码</b><span class="${d.ok ? 'al-ok' : 'al-bad'}">${d.ok ? '通过（' + d.ms + 'ms · ' + d.duration + 's · ' + d.channels + ' 声道 · ' + d.sampleRate + 'Hz）' : '失败：' + (d.error || '未知')}</span>
          <b>AudioContext</b><span>${fmt(c.ctx.sampleRate, ' Hz')} · 最大声道 ${fmt(c.ctx.maxChannels)} · 状态 ${c.ctx.state}</span>
          <b>输出延迟</b><span>base ${fmt(c.ctx.baseLatency, ' ms')} · output ${fmt(c.ctx.outputLatency, ' ms')}</span>
        </div>
        <div class="al-tip">「确定支持」= 浏览器声明可解码；「实测解码」= 真的跑了 decodeAudioData。两者都过才说明这条链路可用。</div>
        <div class="al-row" style="margin-top:10px">
          <button class="al-btn on" onclick="AudioLab.autoDetect()">一键自动检测并接管</button>
          <span class="al-tip">解码 → 设备 → 网络三项全部通过后，自动接管播放并使用默认输出；任一项不合格则保持原生播放。</span>
        </div>
        ${state.autoMsg ? `<div class="al-tip ${state.autoOk ? 'al-ok' : 'al-bad'}">${escHtml(state.autoMsg)}</div>` : ''}
      </div>
      <div class="al-sec">
        <h4>② 接入设备</h4>
        <div class="al-kv">
          <b>输出设备</b><span><span class="al-chip al-ok">系统默认输出</span>${out.map(o => `<span class="al-chip">${escHtml(o.label)}</span>`).join('')}${out.length ? '' : '<span class="al-tip">其余设备名需授权后可见</span>'}</span>
          <b>输入设备</b><span>${inn.length ? inn.map(o => `<span class="al-chip">${escHtml(o.label)}</span>`).join('') : '<span class="al-tip">无 / 未授权</span>'}</span>
          <b>切换输出</b><span>${c.ctx.setSinkId ? '<span class="al-ok">支持（可在「输出」页指定设备）</span>' : '<span class="al-warn">不支持（需 Chrome/Edge 110+）</span>'}</span>
        </div>
        <div class="al-row"><button class="al-btn" onclick="AudioLab.grantDevices()">授权后重新读取设备名</button>
        <span class="al-tip">浏览器默认隐藏设备名称，需要一次麦克风授权才会显示真实名称（用完立即释放）。</span></div>
      </div>
      <div class="al-sec">
        <h4>③ 网络 / 网卡</h4>
        <div class="al-kv">
          <b>在线状态</b><span>${c.net.online ? '<span class="al-ok">在线</span>' : '<span class="al-bad">离线</span>'}</span>
          <b>连接类型</b><span>${fmt(c.net.type) || '—'} ${c.net.effectiveType ? '· ' + c.net.effectiveType : ''}</span>
          <b>下行/延迟</b><span>${fmt(c.net.downlink, ' Mb/s')} · ${fmt(c.net.rtt, ' ms')} ${c.net.saveData ? '<span class="al-warn">· 省流模式</span>' : ''}</span>
          <b>CPU / 内存</b><span>${fmt(c.net.cores, ' 核')} · ${fmt(c.net.mem, ' GB')}</span>
        </div>
        <div class="al-tip">浏览器出于隐私不暴露真实网卡型号与链路速率，只能拿到「有效连接类型」。局域网直连本地服务器时，上面这些数值仅供参考，实际带宽远高于此。</div>
      </div>
      ${renderDolby()}
      ${renderBridge()}`;
  }

  function renderDolby() {
    const d = state.dolby, s = state.sys;
    if (!d || !s) return '';
    const rows = d.rows.map(r => {
      const mc = r.info;
      const ok = mc && mc.supported;
      const cls = ok ? 'al-ok' : (r.canPlay ? 'al-warn' : 'al-bad');
      const txt = ok ? '可解码' : (mc && mc.error ? '探测失败' : (r.canPlay === 'maybe' ? '可能可解码' : '不支持'));
      return `<span class="al-chip ${cls}" title="${escHtml(r.ct)}">${escHtml(r.name)}：${txt}${ok ? (mc.smooth ? ' · 流畅' : ' · 不保证流畅') : ''}</span>`;
    }).join('');
    const verdict = d.anyDolby
      ? { cls: 'al-ok', t: '本机可解码杜比音轨：真实 5.1 码流能解出来，能否听出环绕还看第 ③ 层' }
      : (s.isEdge && s.isWindows)
        ? { cls: 'al-warn', t: '内核支持但系统缺解码器：装 Dolby Access 后重开 Edge 再检测' }
        : s.isChrome
          ? { cls: 'al-bad', t: 'Chrome 内核不解析 AC-3 / E-AC-3：装任何组件都不会生效，只能换 Edge' }
          : { cls: 'al-bad', t: '当前浏览器 / 系统不支持杜比音轨解码' };
    return `
      <div class="al-sec">
        <h4>④ 杜比通路（三层，缺一层就听不到）</h4>
        <div class="al-kv">
          <b>① 解码器层</b><span>${s.osBuild}；杜比组件由 Microsoft Store 分发（无官方 exe）</span>
          <b>② 内核层</b><span>${escHtml(s.engine)} · MSE AC-3 <span class="${s.mseAc3 ? 'al-ok' : 'al-bad'}">${s.mseAc3 ? '支持' : '不支持'}</span> / E-AC-3 <span class="${s.mseEac3 ? 'al-ok' : 'al-bad'}">${s.mseEac3 ? '支持' : '不支持'}</span></span>
          <b>③ 物理层</b><span>AudioContext 最大 ${s.maxChannels} 声道 → ${s.multiOut ? '<span class="al-ok">可走多声道输出</span>' : '<span class="al-warn">只能双声道下混：3.5mm 耳机口 / 普通耳机都是 2 声道，要 5.1 得走 HDMI 或 USB 多声道声卡</span>'}</span>
        </div>
        <div class="al-tip" style="margin-top:8px"><b>结论：</b><span class="${verdict.cls}">${verdict.t}</span></div>
        <div>${rows}</div>
        <div class="al-row" style="margin-top:10px">
          <button class="al-btn" onclick="AudioLab.openDolbyStore()">获取杜比组件（Microsoft Store）</button>
          <button class="al-btn" onclick="AudioLab.openInEdge()">在 Edge 中打开本站</button>
          <button class="al-btn" onclick="AudioLab.setMode('dolby'); AudioLab.setTab('mode')">用杜比环绕模拟代替</button>
        </div>
        <div class="al-tip">装组件只补齐第 ① 层；第 ② 层是浏览器内核自己的事（Chrome 永远不解），第 ③ 层是声卡与接口的物理能力。所以「支持就是支持，不支持就是不支持」这句话在这里成立：缺哪层都别指望一个安装包能救。</div>
        <div class="al-tip">想把杜比码流经网络送到真正支持它的设备（Chromecast / AV 功放）：浏览器页面既发现不了局域网里的接收端，也不能把裸码流交给它，这一步得靠 Cast / DLNA 客户端，本页只能做「本机解码」或「本页模拟」。</div>
      </div>`;
  }

  function renderOutput() {
    const outs = state.devices.outputs || [];
    const opt = outs.map(o => `<option value="${escHtml(o.id)}" ${o.id === state.sinkId ? 'selected' : ''}>${escHtml(o.label)}</option>`).join('');
    return `
      <div class="al-sec">
        <h4>输出路由</h4>
        <div class="al-row">
          <label>输出设备</label>
          <select class="al-sel" id="al-sink">
            <option value="" ${state.sinkId === '' ? 'selected' : ''}>自动（系统默认）</option>
            ${opt}
          </select>
          <button class="al-btn" onclick="AudioLab.applySink()">应用</button>
          <button class="al-btn" onclick="AudioLab.refreshDevices(1)">刷新</button>
        </div>
        <div class="al-tip" id="al-sink-msg">选择后会把当前音频切到该设备；不支持 setSinkId 的浏览器只能跟系统默认走。</div>
      </div>
      <div class="al-sec">
        <h4>处理链开关</h4>
        <div class="al-row">
          <button class="al-btn ${state.on ? 'on' : ''}" onclick="AudioLab.toggleFromUi()">${state.on ? '● EQ 处理链已接管' : '○ EQ 处理链未接管'}</button>
          <span class="al-tip">WebAudio 处理链：EQ / 声像 / 环绕 / 防过曝；关闭则退回浏览器原生播放，音质最稳。</span>
        </div>
      </div>`;
  }

  function renderTune() {
    const presetBtns = Object.keys(PRESETS).map(k =>
      `<button class="al-btn ${state.preset === k ? 'on' : ''}" onclick="AudioLab.applyPreset('${k}')">${PRESETS[k].name}</button>`).join('');
    const cols = BANDS.map((f, i) => `
      <div class="c">
        <div class="v" id="al-eqv${i}">${(state.eq[i] > 0 ? '+' : '') + state.eq[i]}</div>
        <input type="range" min="-12" max="12" step="1" value="${state.eq[i]}" oninput="AudioLab.setBand(${i}, this.value)">
        <div class="f">${f >= 1000 ? (f / 1000) + 'k' : f}</div>
      </div>`).join('');
    return `
      <div class="al-sec">
        <h4>音效校准 · 预设</h4>
        <div class="al-row">${presetBtns}</div>
      </div>
      <div class="al-sec">
        <h4>10 段均衡（dB）</h4>
        <div class="al-eq">${cols}</div>
      </div>
      <div class="al-sec">
        <h4>增益 / 声像 / 动态</h4>
        <div class="al-row"><label>主增益</label>
          <input type="range" min="0.2" max="2" step="0.05" value="${state.gain}" style="flex:1;max-width:320px" oninput="AudioLab.setGain(+this.value)">
          <span id="al-gainv">${Math.round(state.gain * 100)}%</span></div>
        <div class="al-row"><label>声道平衡</label>
          <input type="range" min="-1" max="1" step="0.05" value="${state.pan}" style="flex:1;max-width:320px" oninput="AudioLab.setPan(+this.value)">
          <span id="al-panv">${state.pan === 0 ? '居中' : (state.pan < 0 ? '左 ' + Math.abs(Math.round(state.pan * 100)) + '%' : '右 ' + Math.round(state.pan * 100) + '%')}</span></div>
        <div class="al-row"><label>动态压缩</label>
          <button class="al-btn ${state.comp ? 'on' : ''}" onclick="AudioLab.toggleComp()">${state.comp ? '已开启' : '已关闭'}</button>
          <span class="al-tip">压低大动态峰值，避免爆音；夜间听建议开。</span></div>
      </div>
      <div class="al-tip">校准提示：先播放一首熟悉的曲子，再把 EQ 拉到平直（预设「原声」），确认无声染；随后按耳机/音箱特性微调。改动实时生效。</div>
      <div class="al-sec">
        <h4>防过曝 · 实时波形（在引擎窗口里显示）</h4>
        <div class="al-tip">波形改由本地引擎渲染：新波形从右侧进入，历史向左滚动，填满 8 秒后整体左移。网页这边只负责挑歌、把音频交给引擎，不再自己画波形。</div>
        <div class="al-row">
          <a class="al-btn" href="/web/download/BAudioBridge.exe" download style="text-decoration:none">下载引擎（30 KB）</a>
          <button class="al-btn ${state.bridge.ok ? 'on' : ''}" onclick="AudioLab.openEngineUi()">${state.bridge.ok ? '● 打开引擎波形界面' : '打开引擎波形界面'}</button>
        </div>
        <div class="al-tip">首次运行会触发一次系统询问「是否允许此应用对你的设备进行更改」（SmartScreen / UAC），选「仍要运行」；之后引擎页面顶部就会实时滚动波形。</div>
        <div class="al-kv" style="margin-top:10px">
          <b>实时峰值</b><span id="al-clip-db">—</span>
          <b>削顶样本</b><span id="al-clip-hot">—</span>
          <b>素材基线</b><span id="al-clip-base">—</span>
          <b>状态</b><span id="al-clip-st"><span class="al-tip">尚未接管播放</span></span>
          <b>电平还原</b><span id="al-clip-mk">1.00x（不放大）</span>
          <b>累计报警</b><span id="al-clip-cnt">0 次</span>
          <b>学习记忆</b><span id="al-learn-st">${state.learnedInfo
            ? ('已训练 ' + (state.learnedInfo.plays || 0) + ' 次 · 峰值 ' + (+(state.learnedInfo.peak || 0)).toFixed(3))
            : (state.anticlip ? '暂无训练档案，使用本次基线' : '暂无训练档案（播放后自动采集）')}</span>
          <b>LDTE 记录</b><span>${state.ldte
            ? (state.ldte.frameMs || 16) + 'ms / 帧 · ' + (state.ldte.frames || 0) + ' 帧 · '
              + ((state.ldte.trackCount || 0)) + ' 首 · '
              + Math.max(1, Math.round((state.ldte.frames || 0) * (state.ldte.channelCount || 12) * 4 / 1024)) + ' KB'
            : '暂无训练数据'}</span>
        </div>
        <div class="al-row" style="margin-top:8px">
          <button class="al-btn ${state.useLdte ? 'on' : ''}" onclick="AudioLab.setUseLdte(!AudioLab.state.useLdte)">${state.useLdte ? '● 训练数据播放已开启' : '○ 训练数据播放未开启'}</button>
          <span class="al-tip">开启后，只有 EQ / 音量 / 声场 / 限幅被实际调整过才会参考训练数据修正音色与限幅；全部默认值时完全不改动音频。</span>
        </div>
        <div class="al-row">
          <button class="al-btn ${state.anticlip ? 'on' : ''}" onclick="AudioLab.toggleAntiClip()">${state.anticlip ? '● 自动缓释已开启' : '○ 开启自动缓释'}</button>
          <label>模式</label>
          ${[['auto', '自适应'], ['soft', '软限幅'], ['hard', '硬限幅'], ['monitor', '仅监测']].map(([k, n]) =>
            `<button class="al-btn ${state.anticlipMode === k ? 'on' : ''}" onclick="AudioLab.setAntiMode('${k}')">${n}</button>`).join('')}
        </div>
        <div class="al-row">
          <label>限幅强度</label>
          <input type="range" min="1" max="3" step="0.1" value="${state.anticlipK}" style="flex:1;max-width:180px" oninput="AudioLab.setAntiClipK(+this.value)">
          <span id="al-clip-k">${state.anticlipK.toFixed(1)}</span>
          <label style="min-width:auto">音色补偿</label>
          <input type="range" min="0" max="2" step="0.1" value="${state.toneComp}" style="flex:1;max-width:180px" oninput="AudioLab.setToneComp(+this.value)">
          <span id="al-tc">${state.toneComp.toFixed(1)}</span>
        </div>
        <div class="al-row">
          <button class="al-btn" onpointerdown="AudioLab.abBypass(true)" onpointerup="AudioLab.abBypass(false)" onpointercancel="AudioLab.abBypass(false)" onpointerleave="AudioLab.abBypass(false)">按住对比原声</button>
          <button class="al-btn" onclick="AudioLab.resetClipCount()">重置计数</button>
          <span class="al-tip">按住时临时旁路限幅，松手恢复，方便 A/B。</span>
        </div>
        <div class="al-tip">无论防过曝是否开启，系统都会在后台按 16ms 一帧持续采样当前曲目，写入 LDTE 全音频训练档案（程序目录 LDTE训练）。开启防过曝时会读取该曲目的历史峰值作为基线，越听越准；清空训练档案后「学习记忆」会回到「暂无」。</div>
        <div class="al-tip">判断用的是「素材基线」而不是固定刻度：每首歌开始后先学 8 秒，取自己的峰值作为基线。本身就到 0dBFS 的母带不会一直报警；只有出现真实数字削顶、或比基线再高 6dB 才判定异常。自适应模式还会把限幅拐点抬到基线附近，所以天生就很响的素材不会被无端压平。</div>
        <div class="al-tip">缓释链：tanh 软膝曲线 → 24Hz 高通去直流 → 95Hz 低频 + 1.6k 中频按「音色补偿」补回 → 电平还原。电平还原只看 preRms/postRms 的比值，且强度越高上限越低；一旦后级峰值逼近满量程就停止补偿，<b>不会靠继续拉高音量制造新的过曝</b>。</div>
      </div>`;
  }

  function renderMode() {
    const items = MODES.map(m => `
      <div class="al-row">
        <button class="al-btn ${state.mode === m.k ? 'on' : ''}" onclick="AudioLab.setMode('${m.k}')">${state.mode === m.k ? '● ' : '○ '}${m.n}</button>
        <span class="al-tip">${m.d}</span>
      </div>`).join('');
    return `
      <div class="al-sec"><h4>多声道模拟</h4>${items}
        <div class="al-tip">浏览器只能物理输出双声道（除非系统提供多声道声卡且浏览器开放），所以这里做的是「虚拟多声道」：用 HRTF 与延迟把立体声渲染出方位感。真 5.1/7.1 需要桌面播放器接管。</div>
      </div>
      <div class="al-sec"><h4>当前链路</h4>
        <div class="al-kv">
          <b>接管状态</b><span>${state.on ? '<span class="al-ok">EQ 处理链已接管</span>' : '未接管（原生播放）'}</span>
          <b>处理模式</b><span>${(MODES.find(m => m.k === state.mode) || MODES[0]).n}</span>
          <b>预设</b><span>${state.preset === 'custom' ? '自定义 EQ' : (PRESETS[state.preset] || {}).name || state.preset}</span>
          <b>输出</b><span>${state.sinkId ? escHtml((state.devices.outputs.find(o => o.id === state.sinkId) || {}).label || state.sinkId) : '系统默认'}</span>
          <b>防过曝</b><span>${state.anticlip ? '<span class="al-ok">已开启（' + ({ auto: '自适应', soft: '软限幅', hard: '硬限幅', monitor: '仅监测' }[state.anticlipMode] || '') + ' · 强度 ' + state.anticlipK.toFixed(1) + '）</span>' : '未开启'}</span>
        </div>
      </div>`;
  }

  function renderBridge() {
    const b = state.bridge || {};
    const st = !b.checked
      ? '<span class="al-tip">未检测</span>'
      : b.ok
        ? `<span class="al-ok">在线 · 解码 AC-3 ${b.info.decoders.ac3 ? '✓' : '✗'} / E-AC-3 ${b.info.decoders.eac3 ? '✓' : '✗'} · ${b.info.multichannel ? '6 声道可直出' : '仅 2 声道'}</span>`
        : `<span class="al-warn">${escHtml(b.error)}</span>`;
    return `
      <div class="al-sec">
        <h4>⑤ 本地音频引擎（本网站专属组件）</h4>
        <div class="al-kv">
          <b>引擎状态</b><span>${st}</span>
          <b>工作方式</b><span>网页只负责挑歌与发地址；真正的解码、声道与输出交给本地程序（127.0.0.1:8236）</span>
        </div>
        <div class="al-row">
          <label>杜比音效</label>
          <select class="al-sel" id="al-dolby" onchange="AudioLab.bridgeDolby(this.value)">
            ${BRIDGE_DOLBY.map(d => `<option value="${d.k}" ${(b.dolby || 'off') === d.k ? 'selected' : ''}>${escHtml(d.n)}</option>`).join('')}
          </select>
          <span class="al-tip">选中后点「用引擎播放当前曲目」生效；上混 / 影院 / 夜间会强制 6 声道输出。</span>
        </div>
        <div class="al-row">
          <a class="al-btn" href="/web/download/BAudioBridge.exe" download style="text-decoration:none">下载 BAudioBridge.exe（30 KB · 免安装）</a>
          <button class="al-btn" onclick="AudioLab.bridgeCheck()" ${b.checking ? 'disabled' : ''}>${b.checking ? '检测中…' : '检测引擎'}</button>
          <button class="al-btn" onclick="AudioLab.bridgePlay()" ${b.ok ? '' : 'disabled'}>用引擎播放当前曲目</button>
          <button class="al-btn" onclick="AudioLab.bridgeStop()" ${b.ok ? '' : 'disabled'}>停止本地播放</button>
          <button class="al-btn" onclick="AudioLab.bridgeOpen()" ${b.ok ? '' : 'disabled'}>打开引擎界面</button>
        </div>
        ${b.checked && !b.ok ? '<div class="al-tip al-warn">引擎未运行：先下载并双击 exe，再点「检测引擎」；离线时后面三个按钮不可用。</div>' : ''}
        ${b.msg ? `<div class="al-tip al-ok">${escHtml(b.msg)}</div>` : ''}
        <div class="al-tip">双击 exe 会弹出控制台窗口并自动打开 http://127.0.0.1:8236/ ，关掉窗口即停止。它用本站目录里的 ffmpeg.exe 解码（自动向上查找），所以 AC-3 / E-AC-3 / DTS / TrueHD 都能解，不受 Windows 是否移除内置解码器影响；能否真的听到环绕仍取决于声卡与接口。</div>
      </div>`;
  }

  function renderCodec() {
    const c = state.codecs;
    const mr = c ? c.mr.map(r => `<span class="al-chip ${r.ok ? 'al-ok' : 'al-bad'}">${escHtml(r.t)}：${r.ok ? '可用' : '不支持'}</span>`).join('') : '';
    const we = c && c.we.length
      ? c.we.map(r => `<span class="al-chip ${r.ok ? 'al-ok' : 'al-bad'}">${escHtml(r.codec)}：${r.ok ? '可编码' : '不支持'}</span>`).join('')
      : '<span class="al-tip">此浏览器没有 WebCodecs AudioEncoder</span>';
    return `
      <div class="al-sec">
        <h4>① 编码能力检测</h4>
        <div class="al-kv">
          <b>MediaRecorder</b><span>${mr || '<span class="al-tip">不支持</span>'}</span>
          <b>WebCodecs</b><span>${we}</span>
        </div>
        <div class="al-tip">MediaRecorder 产出的是带封装的文件（webm/ogg，Opus 编码），可直接播放；WebCodecs 只给裸码流，需要自己封装（mux），所以这里只做能力检测。</div>
      </div>
      <div class="al-sec">
        <h4>② 导出 WAV（离线渲染，套用当前 EQ / 增益）</h4>
        <div class="al-row">
          <label>导出时长</label>
          <input class="al-sel" id="al-exp-sec" type="number" min="5" max="600" value="30" style="min-width:90px">
          <span class="al-tip">秒</span>
          <button class="al-btn" onclick="AudioLab.exportWavUi()">导出 WAV</button>
        </div>
        <div class="al-tip" id="al-export-msg">${escHtml(state.exportMsg || '导出会重新下载并解码当前音轨，不打断正在播放的声音。')}</div>
      </div>
      <div class="al-sec">
        <h4>③ 录制当前播放</h4>
        <div class="al-row">
          <button class="al-btn ${state.rec ? 'on' : ''}" onclick="AudioLab.recordToggle()">${state.rec ? '■ 停止并保存' : '● 开始录制'}</button>
          <span class="al-tip">录的是接管后的最终效果（EQ + 环绕 + 杜比模拟）；需先启用接管。</span>
        </div>
        <div class="al-tip">录制走 MediaRecorder，实时抓取处理后信号，因此切模式、切歌都会一并录进去。</div>
      </div>
      <div class="al-sec">
        <h4>④ LDTE 片段归档 <span class="al-tip">${state.fullRecord ? '全录制已开启' : '按段归档'}</span></h4>
        <div class="al-row">
          <label>起点</label><input class="al-sel" id="al-clip-start" type="number" min="0" step="0.5" value="${Math.max(0, Math.floor((el && el.currentTime) || 0))}" style="min-width:90px">
          <label>终点</label><input class="al-sel" id="al-clip-end" type="number" min="0" step="0.5" value="${Math.max(5, Math.ceil(((el && el.currentTime) || 0) + 30))}" style="min-width:90px">
          <button class="al-btn" onclick="AudioLab.clipSave()">归档这一段</button>
          <button class="al-btn ghost" onclick="AudioLab.clipRefresh()">刷新</button>
        </div>
        <div class="al-tip" id="al-clip-msg">${escHtml(state.clipMsg || '保留选中段的原始 WAV，服务端逐段独立 AES-256-GCM 加密；全录制开启后会自动归档整首。')}</div>
        ${state.clips && state.clips.length ? `<div class="al-tip">${state.clips.map(c => `<div class="al-row" style="justify-content:space-between"><span>${escHtml(c.name || c.key || '')} ${c.start}–${c.end}s${c.full ? ' · 全录制' : ''}</span><span style="display:flex;gap:6px"><button class="al-btn ghost" onclick="AudioLab.clipPlay('${escHtml(c.id)}')">▶</button><button class="al-btn ghost" onclick="AudioLab.clipDelete('${escHtml(c.id)}')">删除</button></span></div>`).join('')}</div>` : ''}
      </div>`;
  }

  function escHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function saveBridgePref(){
    try{
      localStorage.setItem('ba.audio.scheme', state.bridge.scheme);
      localStorage.setItem('ba.audio.rate', String(state.bridge.rate || 0));
      localStorage.setItem('ba.audio.dev', String(state.bridge.deviceId));
      localStorage.setItem('ba.audio.route', state.bridge.routeOn ? '1' : '0');
    }catch(e){}
  }
  function setScheme(k){ state.bridge.scheme = k; saveBridgePref(); refreshPanel(); return { ok: true, scheme: k }; }
  function setRate(v){ state.bridge.rate = +v || 0; saveBridgePref(); refreshPanel(); return { ok: true, rate: state.bridge.rate }; }
  function setDevice(v){ state.bridge.deviceId = +v; saveBridgePref(); refreshPanel(); return { ok: true, device: state.bridge.deviceId }; }
  function setRoute(on){ state.bridge.routeOn = !!on; saveBridgePref(); refreshPanel(); return { ok: true, on: state.bridge.routeOn }; }

  function renderCap(){
    const b = state.bridge;
    const info = b.info;
    const scheme = b.scheme || 'attempt';
    const schemes = (info && info.schemes) || [
      { k: 'fidelity', name: '绝对保真', ok: false, why: '本地引擎未连接，无法确认多声道通路' },
      { k: 'sim', name: '明确告知的模拟', ok: true, why: '只做模拟环绕，界面会标注「非杜比授权效果」' },
      { k: 'attempt', name: '直接尝试', ok: true, why: '先试保真，失败自动退回模拟' }
    ];
    const cur = schemes.find(s => s.k === scheme) || schemes[schemes.length - 1] || schemes[0];
    const dec = info && info.decoders ? info.decoders : null;
    const devs = (info && info.devices) || [];
    const sel = b.deviceId == null ? -1 : b.deviceId;
    const devOpts = `<option value="-1" ${sel < 0 ? 'selected' : ''}>系统默认</option>` +
      devs.map(d => `<option value="${d.id}" ${sel === d.id ? 'selected' : ''}>${escHtml(d.name)}${d.bluetooth ? '（蓝牙）' : ''}</option>`).join('');
    const devRows = devs.map(d => {
      const ok6 = (d.formats || []).some(f => f.ch === 6 && f.ok);
      const rates = Array.from(new Set((d.formats || []).filter(f => f.ok && f.ch === 2).map(f => f.rate))).sort((a, b) => a - b);
      return `<div class="al-kv">
          <b>${escHtml(d.name)}${d.bluetooth ? ' <span class="al-chip al-warn">蓝牙</span>' : ''}</b>
          <span>驱动声明 ${d.channels} 声道 · ${ok6 ? '<span class="al-ok">引擎可开 6 声道</span>' : '<span class="al-warn">引擎只能开 2 声道</span>'} · ${rates.length ? rates.map(r => (r / 1000) + 'k').join(' / ') : '采样率未知'}</span>
        </div>`;
    }).join('');
    const st = !b.checked ? '<span class="al-tip">正在检测本地引擎……</span>'
      : b.ok ? '<span class="al-ok">在线 · 127.0.0.1:8236</span>'
      : `<span class="al-warn">${escHtml(b.error)}</span>`;
    const yn = v => v ? '<span class="al-ok">✓</span>' : '<span class="al-bad">✗</span>';
    return `
      <div class="al-sec">
        <div class="al-row" style="margin-bottom:10px">
          <button class="al-btn ${state.useLdte ? 'on' : ''}" onclick="AudioLab.setUseLdte(!AudioLab.state.useLdte)">${state.useLdte ? '● 训练数据播放已开启' : '○ 训练数据播放未开启'}</button>
          <span class="al-tip">也可在「我的设置 → 高级音频选项」调整；全部默认值时不改动音频。</span>
        </div>
        <h4>① 本机能力（支持什么才显示什么）</h4>
        <div class="al-kv">
          <b>本地引擎</b><span>${st}</span>
          <b>输出设备</b><span>${devs.length} 个${info && info.bluetooth ? ' · <span class="al-warn">检测到蓝牙端点</span>' : ''}</span>
          <b>解码器</b><span>AC-3 ${dec ? yn(dec.ac3) : '—'} · E-AC-3 ${dec ? yn(dec.eac3) : '—'} · DTS ${dec ? yn(dec.dca) : '—'} · TrueHD ${dec ? yn(dec.truehd) : '—'}</span>
        </div>
        <div class="al-row">
          <label>输出设备</label>
          <select class="al-sel" onchange="AudioLab.setDevice(this.value)">${devOpts}</select>
          <button class="al-btn" onclick="AudioLab.bridgeCheck()">重新检测</button>
        </div>
        <div class="al-tip">设备能力来自 WinMM 实测：能「打开」不代表硬件原生支持，驱动可能自动重采样；<b>蓝牙端点通常被限制在 48 kHz / 2 声道</b>，并会再编码一次，保真度低于有线。</div>
        ${devRows}
      </div>

      <div class="al-sec">
        <h4>② 杜比：三款方案，由你选</h4>
        <div class="al-row">
          ${schemes.map(s => `<button class="al-btn ${scheme === s.k ? 'on' : ''}" ${s.ok ? '' : 'disabled'} onclick="AudioLab.setScheme('${s.k}')">${scheme === s.k ? '● ' : '○ '}${escHtml(s.name)}</button>`).join('')}
        </div>
        <div class="al-tip ${cur.ok ? 'al-ok' : 'al-bad'}">${escHtml(cur.why || '')}</div>
        <div class="al-tip">① 绝对保真：只解码原样输出，不加任何 DSP；② 明确告知：只做模拟环绕，界面标注「非杜比授权效果」；③ 直接尝试：先走保真，设备拿不到 5.1 时自动退回模拟并写明原因。</div>
      </div>

      <div class="al-sec">
        <h4>③ 播放通路</h4>
        <div class="al-row">
          <button class="al-btn ${b.routeOn ? 'on' : ''}" onclick="AudioLab.setRoute(${b.routeOn ? 'false' : 'true'})">${b.routeOn ? '● 自动选路已开' : '○ 自动选路已关'}</button>
          <label>目标采样率</label>
          <select class="al-sel" onchange="AudioLab.setRate(this.value)">
            <option value="0" ${!b.rate ? 'selected' : ''}>自动（48 kHz）</option>
            ${[44100, 48000, 96000, 192000].map(r => `<option value="${r}" ${b.rate === r ? 'selected' : ''}>${r / 1000} kHz</option>`).join('')}
          </select>
          <button class="al-btn" onclick="AudioLab.bridgePlay()">把当前曲目交给本地引擎</button>
          <button class="al-btn" onclick="AudioLab.bridgeStop()">停止本地播放</button>
        </div>
        <div class="al-tip">${b.routeMsg ? escHtml(b.routeMsg) : (b.routeOn ? '开启后，留声机播放会自动判断：本地引擎在线走本地，离线自动退回网页，不会出现无声。' : '自动选路已关闭：全部由网页播放。')}</div>
        ${b.msg ? `<div class="al-tip ${b.ok ? 'al-ok' : 'al-warn'}">${escHtml(b.msg)}</div>` : ''}
      </div>`;
  }

  async function routeTrack(url, track, au){
    if(!state.bridge.routeOn) return false;
    if(state.bridge.checked && !state.bridge.ok) return false;
    try{
      const abs = new URL(url, location.origin).href;
      const scheme = state.bridge.scheme || 'attempt';
      const ch = scheme === 'fidelity' ? 6 : (state.bridge.info && state.bridge.info.multichannel ? 6 : 2);
      const r = await fetch(BRIDGE + '/play', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: abs, channels: ch, dolby: state.bridge.dolby || 'off', scheme: scheme, rate: state.bridge.rate || 0, device: state.bridge.deviceId })
      });
      const j = await r.json();
      if(j && j.ok){
        try{ if(au) au.pause(); }catch(e){}
        state.bridge.ok = true; state.bridge.checked = true;
        state.bridge.routeMsg = '当前：本地引擎 · ' + j.channels + ' 声道 · ' + j.rate + ' Hz' + (j.note ? ' · ' + j.note : '');
        refreshPanel();
        return true;
      }
      state.bridge.routeMsg = '当前：网页播放（引擎拒绝：' + ((j && j.error) || '未知') + '）';
    }catch(e){
      state.bridge.checked = true;
      state.bridge.ok = false;
      state.bridge.routeMsg = '当前：网页播放（本地引擎未连接）';
    }
    return false;
  }

  function render() {
    const tabs = [['cap', '能力'], ['probe', '自检'], ['out', '输出'], ['tune', '校准'], ['mode', '多声道'], ['codec', '编码器']];
    const body =
      tab === 'cap' ? renderCap() :
      tab === 'probe' ? renderProbe() :
      tab === 'out' ? renderOutput() :
      tab === 'tune' ? renderTune() :
      tab === 'codec' ? renderCodec() : renderMode();
    return `
      <div class="al-mask" onclick="if(event.target===this)AudioLab.close()">
        <div class="al-box" onclick="event.stopPropagation()">
          <div class="al-hd">
            <h3>🎚 高级音频处理</h3>
            <span class="al-tip">留声机专用 · 能力 → 校准 → 多声道 → 编码</span>
            <button class="x" onclick="AudioLab.close()">✕</button>
          </div>
          <div class="al-tabs">
            ${tabs.map(([k, n]) => `<button class="al-tab ${tab === k ? 'on' : ''}" onclick="AudioLab.setTab('${k}')">${n}</button>`).join('')}
          </div>
          <div class="al-body">${body}</div>
        </div>
      </div>`;
  }

  function refreshPanel() {
    if (!panel) return;
    const oldBox = panel.querySelector('.al-box');
    const top = oldBox ? oldBox.scrollTop : 0;
    try {
      panel.innerHTML = render();
    } catch (e) {

      panel.innerHTML = '<div class="al-mask"><div class="al-box"><div class="al-hd"><h3>🎚 高级音频处理</h3>'
        + '<button class="x" onclick="AudioLab.close()">✕</button></div><div class="al-body"><div class="al-sec">'
        + '<h4>面板渲染出错</h4><div class="al-tip al-bad">' + escHtml((e && e.message) || String(e)) + '</div>'
        + '<div class="al-tip">播放与处理链未受影响；可关闭面板后重新打开。</div></div></div></div></div>';
    }
    const box = panel.querySelector('.al-box');
    if (box) box.scrollTop = top;
  }

  function open() {
    injectCss();
    if (!panel) {
      panel = document.createElement('div');
      panel.id = 'al-panel';
      document.body.appendChild(panel);
    }
    panel.style.display = '';
    refreshPanel();
    if (!state.caps) runProbe();
    if (!state.bridge.checked) bridgeCheck();
    if (!clipTimer) clipTimer = setInterval(updateClipReadout, 500);
  }

  function close() {
    if (panel) panel.style.display = 'none';
    if (clipTimer) { clearInterval(clipTimer); clipTimer = null; }
  }

  async function runProbe() {
    const c = await fullProbe();
    refreshPanel();
    return c;
  }

  async function autoDetect() {
    state.autoMsg = '检测中…';
    state.autoOk = null;
    refreshPanel();
    const c = await fullProbe();
    const bad = [];
    if (!c.decode || !c.decode.ok) bad.push('解码链路');
    if (!c.ctx || !c.ctx.ok) bad.push('AudioContext');
    if (!c.net.online) bad.push('网络');
    let r;
    if (bad.length) {
      if (state.on) await toggle(false);
      r = { ok: false, error: '自检未通过：' + bad.join(' / ') + '，已保持原生播放' };
    } else if (state.on && state.ready) {
      r = { ok: true, already: true };
    } else {
      r = await toggle(true);
      if (!r.ok) r = { ok: false, error: '接管失败：' + r.error };
      else if (state.sinkId) await setSink(state.sinkId);
    }
    state.autoMsg = r.ok
      ? '✓ 自检通过，已自动接管播放 · ' + (MODES.find(m => m.k === state.mode) || {}).n
      : r.error;
    state.autoOk = !!r.ok;
    refreshPanel();
    return r;
  }

  const AudioLab = {
    state, PRESETS, BANDS,
    init() { load(); injectCss(); },
    open, close,
    setTab(t) { tab = t; refreshPanel(); },
    runProbe,
    autoDetect,
    get caps() { return state.caps; },
    stats() {
      return {
        on: state.on, ready: state.ready, error: state.error, mode: state.mode, preset: state.preset,
        ctx: ctx ? ctx.state : 'none', sampleRate: ctx ? ctx.sampleRate : null,
        nodes: chain && chain.nodes ? chain.nodes.length : 0,
        hasSource: !!srcNode, paused: el ? el.paused : null,
        currentTime: el ? +el.currentTime.toFixed(2) : null, sinkId: state.sinkId,
        anticlip: state.anticlip, antiK: state.anticlipK, makeup: +makeupGain.toFixed(2),
        clip: getClip(), post: getPost()
      };
    },
    attachMedia,
    toggle,
    async toggleFromUi() {
      const r = await toggle(!state.on);
      if (!r.ok) { const m = document.getElementById('al-sink-msg'); if (m) m.textContent = r.error; }
      refreshPanel();
      return r;
    },
    async grantDevices() {
      state.devices = await probeDevices(true);
      if (state.caps) state.caps.devices = state.devices;
      refreshPanel();
    },
    async refreshDevices() {
      state.devices = await probeDevices(true);
      if (state.caps) state.caps.devices = state.devices;
      refreshPanel();
    },
    async applySink() {
      const sel = document.getElementById('al-sink');
      const r = await setSink(sel ? sel.value : '');
      const m = document.getElementById('al-sink-msg');
      if (m) m.textContent = r.ok ? '已切换到所选输出设备。' : ('切换失败：' + r.error);
    },
    applyPreset, setBand, setGain, setPan, setMode, setSink,
    setUseLdte(v) {
      state.useLdte = !!v;
      try { localStorage.setItem('ba.ldte.use', state.useLdte ? '1' : '0'); } catch (e) {}
      if (state.on) buildChain();
      refreshPanel();
      return state.useLdte;
    },
    hasUserTuning() { return hasUserTuning(); },
    resetDefaults() {
      state.preset = 'flat';
      state.eq = PRESETS.flat.eq.slice();
      state.gain = 1;
      state.pan = 0;
      state.comp = false;
      state.mode = 'stereo';
      state.toneComp = 1;
      state.anticlip = false;
      state.anticlipMode = 'auto';
      state.anticlipK = 2;
      makeupGain = 1;
      save();
      if (state.on) buildChain();
      refreshPanel();
      return true;
    },
    toggleComp() { state.comp = !state.comp; save(); if (state.on) buildChain(); refreshPanel(); },
    sniffRemote,
    get ctx() { return ctx; },
    getLevels, hasAnalyser,
    getClip, getClipState, getPost, toggleAntiClip, setAntiClipK,
    setAntiMode, setToneComp, abBypass, resetClipCount, resetBaseline, openEngineUi,
    exportWav,
    async exportWavUi() {
      const inp = document.getElementById('al-exp-sec');
      const r = await exportWav(inp ? +inp.value : 30);
      return r;
    },
    recordStart, recordStop,
    recordToggle() {
      const r = state.rec ? recordStop() : recordStart();
      if (!r.ok) { state.exportMsg = r.error; refreshPanel(); }
      else refreshPanel();
      return r;
    },
    probeDolby, probeCodecs, probeSystem,
    clipSave, clipRefresh, clipDelete, clipPlay,
    openDolbyStore, openInEdge,
    bridgeCheck, bridgePlay, bridgeStop, bridgeOpen, bridgeDolby, setScheme, setRate, setDevice, setRoute, routeTrack
  };

  window.AudioLab = AudioLab;
})();
