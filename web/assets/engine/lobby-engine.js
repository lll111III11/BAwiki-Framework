'use strict';

function openWebViewer(url){
  let m = $('#unity-viewer');
  if(!m){
    m = document.createElement('div');
    m.id = 'unity-viewer';
    m.className = 'unity-viewer';
    m.innerHTML = `<div class="uv-box"><div class="uv-head">Unity 3D 实时查看器<button id="uv-close" title="关闭">✕</button></div><iframe id="uv-frame" src="about:blank" allowfullscreen></iframe></div>`;
    document.body.appendChild(m);
    $('#uv-close').onclick = ()=>m.classList.add('hidden');
    m.onclick = e=>{ if(e.target===m) m.classList.add('hidden'); };
  }
  $('#uv-frame').src = url;
  m.classList.remove('hidden');
  window.__uvUrl = url;
}

async function renderLobby(d, base, panel){
  panel.innerHTML = '<div class="lobby-wrap"><div class="loading">正在加载记忆大厅……</div></div>';

  let lb = null;
  if(d && d.lobby){ lb = { image: d.lobby.image||'', spines: (d.lobby.spines||[]).map(n=>({name:n, base:'/spines/'+n+'/'})) }; }
  if(!lb){
    try{
      const r = await fetch('/api/lobby?id='+encodeURIComponent(d.curVariant||d.id)+'&t='+BUILD);
      if(r.ok) lb = await r.json();
    }catch(e){ lb = null; }
  }

  if(!window.spine && lb && lb.spines) lb.spines = [];
  if(!lb || (!lb.image && (!lb.spines||!lb.spines.length))){
    panel.innerHTML = '<div class="empty">该学生暂无记忆大厅资源</div>'; return;
  }

  if(lb.spines && lb.spines.length && typeof lb.spines[0] === 'string'){
    lb.spines = lb.spines.map(n=>({name:n, base:'/spines/'+n+'/'}));
  }
  const hasSpine = (lb.spines||[]).length > 0;
  const hasImg = !!lb.image;

  let engine = 'web';
  try{ const er = await fetch('/api/lobby-engine?t='+BUILD); const ej = await er.json(); engine = (ej&&ej.engine==='unity')?'unity':'web'; }catch(e){}
  if(engine === 'unity'){
    const activeId = String(d.curVariant) || String(d.id);
    panel.innerHTML = `
    <div class="lobby-wrap">
      <div class="lobby-stage ${hasSpine?'has-spine':''}">
        ${hasImg?`<img class="lobby-bg-img" src="${esc(lb.image)}">`:''}
      </div>
      <div class="lobby-bar">
        ${hasSpine?`
        <label class="lobby-ctl">场景
          <select id="u-lobby-sel">${(lb.spines||[]).map(s=>`<option value="${esc(s.name)}">${esc(s.name)}</option>`).join('')}</select>
        </label>`:''}
        <button class="lobby-btn" id="u-open">🌀 打开 Unity 3D 查看器</button>
        <span class="lobby-tip">当前引擎：Unity WebGL（服务器控制台 LE web 可切回自研）</span>
      </div>
      <div class="lobby-bar lobby-music-bar">
        <label class="lobby-ctl">🎵 记忆大厅音乐
          <select id="lobby-music"><option>正在读取曲目……</option></select>
        </label>
        <button class="lobby-btn" id="lobby-music-play">▶ 播放</button>
        <label class="lobby-ctl">音量
          <input type="range" id="lobby-music-vol" min="0" max="100" value="50" style="width:100px">
        </label>
        <span class="lobby-tip" id="lobby-music-tip"></span>
      </div>
    </div>`;
    const sel = $('#u-lobby-sel');
    $('#u-open').onclick = ()=>{
      const nn = (sel&&sel.value) || (lb.spines&&lb.spines[0]&&lb.spines[0].name) || '';
      openWebViewer('/web/viewer/index.html?id=' + encodeURIComponent(activeId) + '&mode=lobby&lobby=' + encodeURIComponent(nn));
    };
    setupLobbyMusic(d);
    return;
  }

  panel.innerHTML = `
  <div class="lobby-wrap">
    <div class="lobby-stage ${hasSpine?'has-spine':''}" id="lobby-stage"><div class="lobby-tint" id="lobby-tint"></div></div>
    <div class="lobby-bar">
      ${hasSpine?`
      ${(lb.spines||[]).length>1?`
      <label class="lobby-ctl">场景
        <select id="lobby-scene">${(lb.spines||[]).map(s=>`<option value="${esc(s.name)}">${esc(s.name)}</option>`).join('')}</select>
      </label>`:''}
      <label class="lobby-ctl">动画
        <select id="lobby-anim"></select>
      </label>
      <label class="lobby-ctl">显示方式
        <select id="lobby-fit">
          <option value="adapt" ${window.__lobbyFit==='adapt'?'selected':''}>适应（完整）</option>
          <option value="fill" ${window.__lobbyFit!=='adapt'?'selected':''}>填充（铺满）</option>
        </select>
      </label>`:''}
      <label class="lobby-ctl">背景颜色
        <input type="color" id="lobby-bg" value="#0d1424">
      </label>
      <label class="lobby-ctl">透明
        <input type="range" id="lobby-alpha" min="0" max="100" value="0" style="width:90px">
      </label>
      <button class="lobby-btn" id="lobby-save-png">⬇ 导出图片</button>
      <button class="lobby-btn" id="lobby-save-video" ${hasSpine?'':'disabled'}>🎞 导出视频</button>
      <label class="lobby-ctl">语音
        <select id="lobby-lang">
          <option value="国服">中配</option>
          <option value="日服">日配</option>
          <option value="韩服">韩配</option>
        </select>
      </label>
      <button class="lobby-btn" id="lobby-pic">🎨 画面</button>
      <span class="lobby-tip">${hasSpine?'点击人物 / 摸头 / 戳眼睛有互动':'静态记忆大厅'}${hasImg?' · 带原版场景图':''}</span>
      <span class="lobby-tip" id="lobby-voice-tip" style="display:none;margin-left:0;color:#c97"></span>
    </div>
    <div class="lobby-pic-panel hidden" id="lobby-pic-panel"></div>
    <div class="lobby-bar lobby-music-bar">
      <label class="lobby-ctl">🎵 记忆大厅音乐
        <select id="lobby-music"><option>正在读取曲目……</option></select>
      </label>
      <button class="lobby-btn" id="lobby-music-play">▶ 播放</button>
      <label class="lobby-ctl">音量
        <input type="range" id="lobby-music-vol" min="0" max="100" value="50" style="width:100px">
      </label>
      <span class="lobby-tip" id="lobby-music-tip"></span>
    </div>
  </div>`;
  const stage = $('#lobby-stage');
  const animSel = $('#lobby-anim');
  const fitSel = $('#lobby-fit');
  const sceneSel = $('#lobby-scene');
  const bgInp = $('#lobby-bg');
  const alphaInp = $('#lobby-alpha');
  const btnPng = $('#lobby-save-png');
  const btnVideo = $('#lobby-save-video');
  if(fitSel){
    fitSel.onchange = ()=>{ window.__lobbyFit = fitSel.value; if(window.__lobbyApplyFit) window.__lobbyApplyFit(); };
  }
  if(sceneSel){
    sceneSel.onchange = ()=>{
      const selName = sceneSel.value;

      const src = (d.lobby && d.lobby.spines) ? d.lobby.spines : (lb.spines||[]).map(s=>s.name);
      const ordered = [selName].concat(src.filter(n=>n!==selName));
      if(d && d.lobby) d.lobby.spines = ordered;
      else lb.spines = ordered.map(n=>({name:n, base:'/spines/'+n+'/'}));
      renderLobby(d, base, panel);
    };
  }

  let bgColor = 'rgba(13,20,36,1)';
  function applyBg(){
    stage.style.background = bgColor;
    const already = !!stage.querySelector('img.lobby-bg-img');
    if(hasImg && !hasSpine && !already){
      const im = document.createElement('img');
      im.className = 'lobby-bg-img';
      im.src = lb.image + (lb.image.includes('?')?'&':'?') + 't=' + Date.now();
      im.onload = ()=>{ im.classList.add('loaded'); };
      im.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:cover;z-index:0;';
      stage.appendChild(im);
    }
  }
  applyBg();
  function updateBgFromControls(){
    const alpha = (parseInt(alphaInp.value)||0)/100;
    bgColor = `rgba(13,20,36,${1-alpha})`;
    const im = stage.querySelector('img.lobby-bg-img');
    if(im) im.style.opacity = String(1-alpha);
    applyBg();
  }
  bgInp.oninput = ()=>{ const im = stage.querySelector('img.lobby-bg-img'); if(im) im.style.opacity = '0'; bgColor = bgInp.value; applyBg(); };
  alphaInp.oninput = updateBgFromControls;

  function exportPng(){
    const im = stage.querySelector('img.lobby-bg-img');
    const cv = stage.querySelector('canvas');
    if(!im && !cv){ toast('当前无可导出的画面'); return; }
    const W = 1280, H = Math.round(W * (stage.clientHeight/stage.clientWidth));
    const c = document.createElement('canvas');
    c.width = W; c.height = H;
    const g = c.getContext('2d');
    const drawAll = ()=>{
      g.fillStyle = bgColor; g.fillRect(0,0,W,H);
      if(im && im.complete){ const r = (W-4)/im.naturalWidth; const h = im.naturalHeight*r; g.drawImage(im,0,(H-h)/2,W,h); }
    };
    if(!cv){ drawAll(); }
    else {
      try{

        const k = Math.max(1, window.__lobbyScaleK || 1);
        const dw = Math.round(W * k), dh = Math.round(H * k);
        g.drawImage(cv, Math.round((W-dw)/2), Math.round((H-dh)/2), dw, dh);
      }catch(e){ drawAll(); }
    }
    c.toBlob(b=>{
      if(!b){ toast('导出失败'); return; }
      const a = document.createElement('a');
      a.href = URL.createObjectURL(b);
      a.download = (d.cn||'student')+'_记忆大厅_'+(animSel?animSel.value:'')+'.png';
      a.click();
      setTimeout(()=>URL.revokeObjectURL(a.href), 4000);
      toast('✅ 图片已导出');
    }, 'image/png');
  }
  btnPng.onclick = exportPng;

  let player = null;
  if(hasSpine && window.spine && spine.SpinePlayer){
    const skelInfo = lb.spines[0];
    const skelUrl = skelInfo.base + skelInfo.name + '.skel';
    const baseUrl = skelUrl.replace(/\.skel$/, '');
    try{

      player = new spine.SpinePlayer('lobby-stage', {
        skelUrl, atlasUrl: baseUrl + '.atlas',
        backgroundColor: '#00000000',
        showControls: false,
        alpha: true,
        premultipliedAlpha: false,
        animation: 'Idle_01',
        fullScreenBackgroundColor: '#00000000',
        viewport: { padLeft:'0%', padRight:'0%', padTop:'0%', padBottom:'0%', transitionTime: 0 },

        updateWorldTransform: function(p){
          try{ if(p.__lobbyGaze) p.__lobbyGaze(); }catch(e){}
          p.skeleton.updateWorldTransform(2);
        }
      });
    }catch(e){ player = null; }
    if(!player){
      if(animSel) animSel.innerHTML = '<option>— 加载失败 —</option>';
      return;
    }
    window.__lobbySpine = player;
    const lobbyMeta = await loadLobbyMeta(skelInfo.name);
    const voicePool = await buildVoicePool(d);
    const setAnimOptions = ()=>{
      animSel.innerHTML = '';
      const names = [];
      let sd = null;
      try{ sd = player.skeleton && player.skeleton.data ? player.skeleton.data : null; }catch(e){}
      if(sd && sd.animations){
        sd.animations.forEach(a=>{ if(!names.includes(a.name)) names.push(a.name); });
      }
      (names.length?names:['Idle_01']).forEach(n=>{
        const o = document.createElement('option');
        o.value = n; o.textContent = n; animSel.appendChild(o);
      });
    };

    function applyFit(){
      const cv = stage.querySelector('canvas');
      if(!cv) return;

      if(window.__lobbyFit === 'adapt'){ cv.style.transform = ''; window.__lobbyScaleK = 1; return; }
      const cw = stage.clientWidth || 1, ch = stage.clientHeight || 1;
      let sw = 0, sh = 0;
      try{ sw = player.skeleton.data.width; sh = player.skeleton.data.height; }catch(e){}
      if(!sw || !sh){ cv.style.transform = ''; return; }
      const contain = Math.min(cw/sw, ch/sh);
      const cover = Math.max(cw/sw, ch/sh);
      const k = contain > 0 ? cover / contain : 1;
      window.__lobbyScaleK = k > 1 ? k : 1;
      cv.style.transformOrigin = 'center center';
      cv.style.transform = k > 1 ? 'scale(' + k.toFixed(3) + ')' : '';
    }
    window.__lobbyApplyFit = applyFit;
    if(!window.__lobbyFitResizeBound){
      window.__lobbyFitResizeBound = true;
      window.addEventListener('resize', ()=>{ try{ window.__lobbyApplyFit && window.__lobbyApplyFit(); }catch(e){} });
    }

    let pollCount = 0;
    (function pollAnim(){
      try{
        if(player.skeleton && player.skeleton.data && animSel.options.length === 0){
          setAnimOptions();

          const want = ['Idle_01','idle','Idle'].find(n => { for(let i=0;i<animSel.options.length;i++){ if(animSel.options[i].value===n) return true; } return false; });
          if(want){
            animSel.value = want;
            try{ player.setAnimation(want, true); player.play(); }catch(e){}
          } else {
            try{ player.setAnimation(animSel.value, true); player.play(); }catch(e){}
          }
          applyFit();
          if(!player.__interactReady){
            player.__interactReady = true;
            setupLobbyInteraction(player, stage, d, lobbyMeta, voicePool);
          }
          return;
        }

        pollCount++;
        if(pollCount > 20 && !(player.skeleton && player.skeleton.data)){
          lobbyBrokenFallback(stage, lb, hasImg);
          return;
        }
      }catch(e){}
      setTimeout(pollAnim, 600);
    })();
    animSel.onchange = ()=>{ try{ player.setAnimation(animSel.value, true); player.play(); }catch(e){} };
    btnVideo.onclick = ()=>exportLobbyVideo(player, stage, d, bgColor);

  } else {
    if(btnVideo) btnVideo.title = '该学生无 Spine 动画，仅支持图片导出';
    if(animSel) animSel.innerHTML = '<option>— 静态图 —</option>';
  }
  setupLobbyMusic(d);
  setupLobbyPicture(stage, player);
}

function lobbyBrokenFallback(stage, lb, hasImg){
  if(!stage) return;
  try{
    const cv = stage.querySelector('canvas');
    if(cv) cv.style.display = 'none';
    if(hasImg && lb && lb.image && !stage.querySelector('img.lobby-bg-img')){
      const im = document.createElement('img');
      im.className = 'lobby-bg-img loaded';
      im.src = lb.image + (lb.image.indexOf('?') >= 0 ? '&' : '?') + 't=' + Date.now();
      im.style.cssText = 'position:absolute;inset:0;width:100%;height:100%;object-fit:cover;z-index:1;';
      stage.appendChild(im);
    }
    const tip = document.querySelector('#tab-panel .lobby-bar .lobby-tip');
    if(tip) tip.textContent = hasImg ? '该大厅 Spine 数据损坏，已退回静态图' : '该大厅 Spine 数据损坏，无法播放';
    const anim = document.getElementById('lobby-anim');
    if(anim) anim.innerHTML = '<option>— 数据损坏 —</option>';
    const btnVideo = document.getElementById('lobby-save-video');
    if(btnVideo){ btnVideo.disabled = true; btnVideo.title = 'Spine 数据损坏，无法录制'; }
  }catch(e){}
}

let lobbyMetaCache = null;
async function loadLobbyMeta(spineName){
  if(!lobbyMetaCache){
    try{
      const r = await fetch('assets/lobby-interactions.json');
      lobbyMetaCache = (await r.json()).spines || {};
    }catch(e){ lobbyMetaCache = {}; }
  }
  return lobbyMetaCache[spineName] || null;
}

async function buildVoicePool(d){
  const pool = { 国服: [], 日服: [], 韩服: [] };
  const push = (set, voices, prefer) => {
    Object.keys(voices || {}).forEach(lang => {
      const dst = set[lang] || (set[lang] = []);
      (voices[lang] || []).forEach(v => {
        if(!v || !v.file) return;
        if(dst.some(x => x.file === v.file)) return;
        if(prefer) dst.unshift(v); else dst.push(v);
      });
    });
  };
  push(pool, d.voices, true);

  const vs = d.variants || [];
  const orig = vs.find(v => String(v.skin || '').indexOf('原皮') === 0);
  if(orig && orig.dir && orig.dir !== d.dir){
    try{
      const r = await fetch('/api/student?id=' + encodeURIComponent(orig.key || orig.id) + '&t=' + BUILD);
      if(r.ok) push(pool, (await r.json()).voices, false);
    }catch(e){}
  }
  return pool;
}

let lobbyVoiceEl = null;

function setupLobbyInteraction(player, stage, d, meta, voicePool){
  const st = player.animationState;
  const sk = player.skeleton;
  if(!st || !sk) return;

  const LANG_KEY = 'ba.lobby.lang';
  const langSel = document.getElementById('lobby-lang');
  let lang = '国服';
  try{ lang = localStorage.getItem(LANG_KEY) || '国服'; }catch(e){}
  if(langSel){
    if([].some.call(langSel.options, o=>o.value===lang)) langSel.value = lang;
    langSel.onchange = ()=>{
      lang = langSel.value;
      try{ localStorage.setItem(LANG_KEY, lang); }catch(e){}
    };
  }

  let lastTalk = -1;
  let patTimer = 0;
  let patHold = false, patStarted = false, patLastX = 0, patLastTrigger = 0;

  const idleName = (()=>{
    try{
      const names = (sk.data.animations || []).map(a => a.name);
      return names.find(n => /^Idle_01$/i.test(n))
          || names.find(n => /^Idle/i.test(n))
          || names.find(n => /idle/i.test(n))
          || '';
    }catch(e){ return ''; }
  })();

  const has = n => { try{ return !!sk.data.findAnimation(n); }catch(e){ return false; } };

  const extra = (meta && meta.extra) || [];
  const canPat  = meta ? !!meta.pat  : has('Pat_01_M');
  const canLook = meta ? !!meta.look : has('Look_01_M');

  const baseOf = n => String(n).replace(/_(M|A)$/, '');
  const extraBases = extra.map(baseOf);
  const pinchMain = extraBases.find(n => /^Pinch_0\d$/.test(n));
  const pinchEnd  = extraBases.find(n => /^PinchEnd_0\d$/.test(n));
  const touchMain = extraBases.find(n => /^Touch_0\d$/.test(n));
  const touchEnd  = extraBases.find(n => /^(TouchEnd|Touch_End)_0\d$/.test(n));
  const canExtra = !!(pinchMain || touchMain);

  const patBases = extraBases.filter(n => /^Pat_0\d$/.test(n));
  if(!patBases.length && has('Pat_01_M')) patBases.push('Pat_01');
  let curPatBase = patBases[0] || 'Pat_01';
  let lastPatIdx = -1;
  function pickPat(){
    if(patBases.length <= 1) return patBases[0] || 'Pat_01';
    let i;
    do { i = Math.floor(Math.random() * patBases.length); } while(i === lastPatIdx);
    lastPatIdx = i;
    return patBases[i];
  }

  const talkBases = (meta && meta.talk && meta.talk.length)
    ? meta.talk
    : ['Talk_01','Talk_02','Talk_03','Talk_04','Talk_05'].filter(b => has(b + '_M'));

  const boneBy = (...names) => {
    for(let i = 0; i < names.length; i++){
      const b = sk.findBone(names[i]);
      if(b) return b;
    }
    return null;
  };
  const bonesMatching = (re, deny) => {
    let list = [];
    try{ list = Array.from(sk.bones || []); }catch(e){ list = []; }
    return list.filter(b => b && b.data && b.data.name && re.test(b.data.name) && (!deny || !deny.test(b.data.name)));
  };
  const headBone = boneBy('head_Rot','Head_Rot','head','Head','Head2','Face')
    || (bonesMatching(/head/i, /(hair|back|halo|con|suprise|ik)/i)[0] || null);
  const eyeBone = boneBy('Touch_Eye','L_Eye_idle_01');
  let bodyBones = ['L_Chest_01','R_Chest_01','L_UpperArm_01','R_UpperArm_01',
                   'L_ForeArm_01','R_ForeArm_01','Face','Head_Rot','L_Eye_idle_01']
                   .map(n => boneBy(n)).filter(Boolean);

  {
    const deny = /(hair|halo|ribbon|flower|string|background|layer|ik|con|suprise|shadow|effect)/i;
    const more = bonesMatching(/(torso|chest|arm|hand|hip|body|neck|shoulder|waist|face|head)/i, deny);
    more.forEach(b => { if(bodyBones.indexOf(b) < 0) bodyBones.push(b); });
    if(!bodyBones.length) bodyBones = bonesMatching(/./, /(background|root|layer|bg)/i);
    if(headBone && bodyBones.indexOf(headBone) < 0) bodyBones.push(headBone);
    if(eyeBone && bodyBones.indexOf(eyeBone) < 0) bodyBones.push(eyeBone);
  }
  window.__lobbyBones = { head: headBone, eye: eyeBone, body: bodyBones };

  const EYE_DENY = /brow|blow|ash|lash|lid|blink|highlight|light|shadow|touch|key|closed|open|mask|face|intro/i;
  const EYE_LIKE = /eye|pupil|iris|hitomi/i;

  const EYE_WHITE_LIKE = /eye[_\s-]?white|white[_\s-]?eye|eyewhite/i;

  function bindEyeBones(){
    const H0 = sk.data.height || 1;
    const picked = [];
    try{
      const look = sk.data.findAnimation('Look_01_M') || sk.data.findAnimation('Look_01');
      if(look){
        for(const t of look.timelines || []){
          if(!Number.isInteger(t.boneIndex) || t.boneIndex < 0) continue;
          const b = sk.bones[t.boneIndex];
          if(!b || !b.data || !EYE_LIKE.test(b.data.name) || EYE_DENY.test(b.data.name)) continue;
          if(picked.indexOf(b) < 0) picked.push(b);
        }
      }
    }catch(e){}
    if(!picked.length){
      const eyeAll = Array.from(sk.bones || []).filter(b => b && b.data && b.data.name && EYE_LIKE.test(b.data.name) && !EYE_DENY.test(b.data.name));
      eyeAll.forEach(b => {
        let p = b.parent;
        while(p){ if(eyeAll.indexOf(p) >= 0) return; p = p.parent; }
        picked.push(b);
      });
    }

    try{
      const promoted = [];
      for(const b of picked){
        let root = b;
        let p = root.parent;
        while(p && p.data && p.data.name && EYE_LIKE.test(p.data.name) && !EYE_DENY.test(p.data.name)){ root = p; p = p.parent; }
        if(promoted.indexOf(root) < 0) promoted.push(root);
      }
      picked.length = 0;
      for(const b of promoted) picked.push(b);
    }catch(e){}

    try{
      const whites = Array.from(sk.bones || []).filter(b => b && b.data && b.data.name && EYE_WHITE_LIKE.test(b.data.name));
      for(const w of whites){
        if(!headBone) continue;
        if(Math.abs(w.worldX - headBone.worldX) > H0 * 0.20 || Math.abs(w.worldY - headBone.worldY) > H0 * 0.26) continue;
        if(picked.indexOf(w) < 0) picked.push(w);
      }
    }catch(e){}

    let roots = picked.filter(b => { let p = b.parent; while(p){ if(picked.indexOf(p) >= 0) return false; p = p.parent; } return true; });

    roots = roots.filter(b => !headBone || (Math.abs(b.worldX - headBone.worldX) <= H0 * 0.18
                                         && Math.abs(b.worldY - headBone.worldY) <= H0 * 0.25));
    return roots;
  }
  let eyeRoots = [];
  try{
    sk.updateWorldTransform(2);
    eyeRoots = bindEyeBones();
  }catch(e){ eyeRoots = []; }
  window.__lobbyBones.eyes = eyeRoots;

  let gazeTX = 0, gazeTY = 0, gazeX = 0, gazeY = 0, gazeMaxX = 0, gazeMaxY = 0;
  let patLean = 0, patSwayAmp = 0;

  function eyeWhiteSize(){
    let w = 0, h = 0;
    try{
      for(const s of sk.slots || []){
        const n = s.data && s.data.name;
        if(!n || !/eye/i.test(n) || !/white/i.test(n)) continue;
        const at = s.getAttachment ? s.getAttachment() : null;
        if(at && at.width > 0){ w = Math.max(w, at.width); h = Math.max(h, at.height || 0); }
      }
    }catch(e){}
    return { w, h };
  }

  function worldDirToLocal(b, dx, dy){
    const p = b.parent;
    if(!p) return { x: dx, y: dy };
    const a = p.a, bb = p.b, c = p.c, d = p.d;
    const det = a * d - bb * c;
    if(!det) return { x: dx, y: dy };
    return { x: (d * dx - bb * dy) / det, y: (-c * dx + a * dy) / det };
  }

  function applyGaze(){
    if(!eyeRoots.length) return;
    const H = sk.data.height || 1;
    if(!gazeMaxX){
      let cx = 0, cy = 0;
      for(const b of eyeRoots){ cx += b.worldX; cy += b.worldY; }
      cx /= eyeRoots.length; cy /= eyeRoots.length;
      let span = 0;
      for(const b of eyeRoots) span = Math.max(span, Math.hypot(b.worldX - cx, b.worldY - cy));
      const white = eyeWhiteSize();
      const byWhite = white.w > 0 ? white.w * 0.26 : 0;
      gazeMaxX = Math.max(H * 0.005, Math.min(H * 0.011, byWhite || span * 0.15));
      gazeMaxY = Math.max(H * 0.003, Math.min(H * 0.008, (white.h > 0 ? white.h * 0.2 : gazeMaxX * 0.6)));
    }
    gazeX += (gazeTX - gazeX) * 0.2;
    gazeY += (gazeTY - gazeY) * 0.2;
    const rest = Math.abs(gazeX) < 0.01 && Math.abs(gazeY) < 0.01
      && Math.abs(gazeTX) < 0.001 && Math.abs(gazeTY) < 0.001;
    const wx = rest ? 0 : gazeX * gazeMaxX;
    const wy = rest ? 0 : gazeY * gazeMaxY;
    for(const b of eyeRoots){

      if(b.__gazeOutX === undefined || b.x !== b.__gazeOutX || b.y !== b.__gazeOutY){
        b.__gazeBaseX = b.x; b.__gazeBaseY = b.y;
      }
      const loc = worldDirToLocal(b, wx, wy);
      b.x = b.__gazeBaseX + loc.x;
      b.y = b.__gazeBaseY + loc.y;
      b.__gazeOutX = b.x; b.__gazeOutY = b.y;
    }
  }

  let patSwayClock = 0, patSwayLast = 0;
  function applyPatSway(now){
    if(!headBone) return;
    const dt = patSwayLast ? Math.min(0.05, (now - patSwayLast) / 1000) : 0.016;
    patSwayLast = now;
    patSwayAmp += ((patHold ? 1 : 0) - patSwayAmp) * Math.min(1, dt * 5);
    if(patSwayAmp < 0.002 && !headBone.__swayDirty) return;
    patSwayClock += dt;

    const deg = patSwayAmp < 0.002 ? 0
      : (patLean * 1.9 + Math.sin(patSwayClock * 1.35) * 0.35) * patSwayAmp;

    if(headBone.__swayOut === undefined || headBone.rotation !== headBone.__swayOut){
      headBone.__swayBase = headBone.rotation;
    }
    headBone.rotation = headBone.__swayBase + deg;
    headBone.__swayOut = headBone.rotation;
    headBone.__swayDirty = patSwayAmp >= 0.002;
    if(!headBone.__swayDirty){
      headBone.rotation = headBone.__swayBase;
      headBone.__swayOut = undefined;
    }
  }

  player.__lobbyGaze = function(){
    applyGaze();
    applyPatSway(performance.now());
  };
  window.__lobbyGazeDebug = function(){
    return {
      eyes: eyeRoots.map(b => b.data.name),
      gazeMaxX: +gazeMaxX.toFixed(2),
      gazeMaxY: +gazeMaxY.toFixed(2),
      white: eyeWhiteSize(),
      tx: +gazeTX.toFixed(2), ty: +gazeTY.toFixed(2),
      active: gazeActive,
      patBases: patBases.slice(),
      curPat: curPatBase,
      scaleX: eyeRoots[0] && eyeRoots[0].parent ? +Math.hypot(eyeRoots[0].parent.a, eyeRoots[0].parent.c).toFixed(3) : 0
    };
  };

  function backToIdle(){
    try{

      st.setEmptyAnimation(1, 0.28);
      st.setEmptyAnimation(2, 0.28);
      const cur = st.getCurrent(0);
      if(idleName && (!cur || !cur.animation || cur.animation.name !== idleName)){
        st.setAnimation(0, idleName, true);
      }
    }catch(e){}
  }

  function playVoiceByKey(key){
    if(!key) return;
    if(!window.Sfx || !Sfx.voiceOn) return;
    const list = (voicePool && voicePool[lang] && voicePool[lang].length)
      ? voicePool[lang]
      : ((d.voices && d.voices[lang]) || []);
    const k = key.toLowerCase().replace(/[-_]/g, '');
    const norm = f => String(f || '').toLowerCase().replace(/[-_]/g, '');
    const hit = list.find(v => v.file && norm(v.file).indexOf(k) >= 0);
    if(!hit){
      const tip = document.getElementById('lobby-voice-tip');
      if(tip){
        tip.textContent = '该句本地未收录（' + lang + ' · ' + key + '）';
        tip.style.display = '';
      }
      return;
    }
    try{
      if(!lobbyVoiceEl) lobbyVoiceEl = new Audio();
      lobbyVoiceEl.pause();
      lobbyVoiceEl.src = mediaUrl(hit.file);
      lobbyVoiceEl.volume = 0.85;
      const p = lobbyVoiceEl.play();
      if(p && p.catch) p.catch(()=>{});
    }catch(e){}
  }

  const TRACK_MAIN = 1;
  const TRACK_FACE = 2;
  function playDual(base, loop){
    const main = has(base + '_M') ? base + '_M' : (has(base) ? base : null);
    if(main){
      try{ st.setAnimation(TRACK_MAIN, main, !!loop); }
      catch(e){ try{ player.setAnimation(main, !!loop); }catch(e2){} }
    }
    if(has(base + '_A')) st.setAnimation(TRACK_FACE, base + '_A', !!loop);
  }

  function playTalk(){
    if(patTimer){ clearTimeout(patTimer); patTimer = 0; }
    const bases = talkBases;
    if(!bases.length){

      const touches = extra.filter(n => /^Idle_0\d_Touch_M$/.test(n));
      if(touches.length){
        const t = touches[Math.floor(Math.random() * touches.length)].replace(/_M$/, '');
        playDual(t, false);
        return true;
      }
      return false;
    }
    let i;
    do { i = Math.floor(Math.random() * bases.length); }
    while(bases.length > 1 && i === lastTalk);
    lastTalk = i;
    playDual(bases[i], false);
    return true;
  }

  function playExtra(main, end){
    if(!main) return false;
    playDual(main.replace(/_M$/, ''), false);
    if(end){
      const eb = end.replace(/_M$/, '');
      if(patTimer) clearTimeout(patTimer);
      patTimer = setTimeout(()=>{
        patTimer = 0;
        if(has(eb + '_M')) playDual(eb, false);
        else backToIdle();
      }, 620);
    }
    return true;
  }

  function playPat(){
    if(!canPat || !patBases.length) return playTalk();
    curPatBase = pickPat();
    playDual(curPatBase, false);
    if(patTimer) clearTimeout(patTimer);
    patTimer = setTimeout(()=>{
      patTimer = 0;
      if(has('PatEnd_01_M')) playDual('PatEnd_01', false);
      else backToIdle();
    }, 620);
    return true;
  }

  function pointerToWorld(e){
    const cv = player.canvas;
    const cam = player.sceneRenderer && player.sceneRenderer.camera;
    if(!cv || !cam) return null;
    const sr = stage.getBoundingClientRect();
    const k = Math.max(1, window.__lobbyScaleK || 1);
    const sx = cv.width / Math.max(1, stage.clientWidth);
    const sy = cv.height / Math.max(1, stage.clientHeight);
    const px = (((e.clientX - (sr.left + sr.width / 2)) / k) + stage.clientWidth / 2) * sx;
    const py = (((e.clientY - (sr.top + sr.height / 2)) / k) + stage.clientHeight / 2) * sy;
    const vw = cam.viewportWidth || cv.width;
    const vh = cam.viewportHeight || cv.height;
    return {
      x: cam.position.x + (px - vw / 2) * cam.zoom,
      y: cam.position.y + (vh / 2 - py) * cam.zoom
    };
  }

  function headTopY(){
    if(!headBone) return 0;
    const H = sk.data.height || 1;
    let top = headBone.worldY + H * 0.02;
    try{
      const bones = sk.bones || [];
      for(let i = 0; i < bones.length; i++){
        const b = bones[i];
        const n = b && b.data && b.data.name;
        if(!n || !/(hair|head|halo|hat|cap|ribbon)/i.test(n)) continue;
        if(/(back|shadow|tree)/i.test(n)) continue;
        if(Math.abs(b.worldX - headBone.worldX) > H * 0.34) continue;
        if(b.worldY > top && b.worldY < headBone.worldY + H * 0.45) top = b.worldY;
      }
    }catch(e){}
    return top;
  }

  function headBottomY(){
    if(!headBone) return 0;
    const H = sk.data.height || 1;
    let bottom = headBone.worldY;
    try{
      const bones = sk.bones || [];
      for(let i = 0; i < bones.length; i++){
        const b = bones[i];
        const n = b && b.data && b.data.name;

        if(!n || !/(^|[_\s])(head|face|nose|eye|mouth|mouse|ear|brow|jaw|chin)([_\d]|$)/i.test(n)) continue;
        if(/(hair|touch|key|halo|back|shadow|highlight|white)/i.test(n)) continue;
        if(Math.abs(b.worldX - headBone.worldX) > H * 0.2) continue;
        if(b.worldY < bottom && b.worldY > headBone.worldY - H * 0.22) bottom = b.worldY;
      }
    }catch(e){}
    return bottom;
  }

  function headHit(wx, wy){
    if(!headBone) return false;
    const H = sk.data.height || 1;
    const rx = H * 0.16;
    const bottom = headBottomY() - H * 0.03;
    const top = headTopY() + H * 0.04;
    return wx > headBone.worldX - rx && wx < headBone.worldX + rx
        && wy > bottom && wy < top;
  }

  function eyeHit(wx, wy){
    if(!eyeRoots.length) return false;
    const H = sk.data.height || 1;
    let cx = 0, cy = 0;
    for(const b of eyeRoots){ cx += b.worldX; cy += b.worldY; }
    cx /= eyeRoots.length; cy /= eyeRoots.length;
    let span = 0;
    for(const b of eyeRoots) span = Math.max(span, Math.hypot(b.worldX - cx, b.worldY - cy));
    return Math.hypot(wx - cx, wy - cy) <= Math.max(span * 1.1, H * 0.035);
  }

  function hitTest(e){
    const p = pointerToWorld(e);
    if(!p) return null;
    const wx = p.x, wy = p.y;

    if(canExtra && eyeHit(wx, wy)) return 'eye';
    if(headHit(wx, wy)) return 'head';

    const H = sk.data.height || 1;
    if(bodyBones.length){
      let d = Infinity, minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity;
      for(let i = 0; i < bodyBones.length; i++){
        const b = bodyBones[i];
        const dd = Math.hypot(wx - b.worldX, wy - b.worldY);
        if(dd < d) d = dd;
        if(b.worldX < minX) minX = b.worldX;
        if(b.worldX > maxX) maxX = b.worldX;
        if(b.worldY < minY) minY = b.worldY;
        if(b.worldY > maxY) maxY = b.worldY;
      }

      if(d <= H * 0.24 &&
         wx > minX - H * 0.12 && wx < maxX + H * 0.12 &&
         wy > minY - H * 0.12 && wy < maxY + H * 0.12) return 'body';
    }
    return null;
  }

  let lastTap = 0;
  window.__lobbyHitTest = hitTest;

  let gazeActive = false;

  stage.addEventListener('pointerdown', (e)=>{
    if(e.button !== 0) return;
    const hit = hitTest(e);
    if(!hit){

      gazeActive = true;
      try{ stage.classList.add('gaze-on'); }catch(e2){}
      return;
    }
    if(hit !== 'head' || !canPat || !patBases.length) return;
    patHold = true; patStarted = true;
    patLastX = e.clientX; patLastTrigger = performance.now();
    const p0 = pointerToWorld(e);
    if(p0 && headBone){
      const H0 = sk.data.height || 1;
      patLean = clamp(-1, 1, (p0.x - headBone.worldX) / (H0 * 0.3));
    }
    curPatBase = pickPat();
    playDual(curPatBase, false);
  });

  stage.addEventListener('pointermove', (e)=>{
    if(patHold && Math.abs(e.clientX - patLastX) >= 30 && performance.now() - patLastTrigger >= 420){
      patLastX = e.clientX;
      patLastTrigger = performance.now();
      playDual(curPatBase, false);
    }
    const p = pointerToWorld(e);
    if(!p || !headBone) return;
    const H = sk.data.height || 1;
    patLean = clamp(-1, 1, (p.x - headBone.worldX) / (H * 0.30));
    if(!gazeActive) return;

    gazeTX = clamp(-1, 1, (p.x - headBone.worldX) / (H * 0.15));
    gazeTY = clamp(-1, 1, (p.y - headBone.worldY) / (H * 0.20));
  }, { passive: true });

  stage.addEventListener('pointerleave', ()=>{ gazeTX = 0; gazeTY = 0; if(!patHold) patLean = 0; });

  function endPat(){
    if(!patHold) return;
    patHold = false;
    if(patTimer) clearTimeout(patTimer);
    patTimer = setTimeout(()=>{
      patTimer = 0;
      if(has('PatEnd_01_M')) playDual('PatEnd_01', false);
      else backToIdle();
    }, 220);
  }
  stage.addEventListener('pointerup', endPat);
  stage.addEventListener('pointercancel', endPat);

  window.addEventListener('pointerup', endPat);

  stage.addEventListener('pointerup', (e)=>{
    if(e.button !== 0) return;
    if(patStarted){ patStarted = false; return; }
    const hit = hitTest(e);
    if(!hit){

      gazeActive = true;
      try{ stage.classList.add('gaze-on'); }catch(e2){}
      return;
    }
    if(performance.now() - lastTap < 260) return;
    lastTap = performance.now();

    gazeActive = false;
    try{ stage.classList.remove('gaze-on'); }catch(e2){}
    if(hit === 'eye') playExtra(pinchMain || touchMain, pinchMain ? pinchEnd : touchEnd);
    else if(hit === 'head') playPat();
    else playTalk();
  });

  st.addListener({
    event: function(entry, ev){
      const n = ev && ev.data && ev.data.name;
      if(!n || n.indexOf('sound/') !== 0) return;
      const key = n.slice(6).replace(/^[A-Za-z0-9]+_/, '');
      playVoiceByKey(key);
    },
    complete: function(entry){
      if(!entry || entry.trackIndex !== TRACK_MAIN) return;
      if(!entry.animation) return;
      const name = entry.animation.name;

      if(name === idleName) return;

      if(patHold && /^Pat_0\d_M$/.test(name)){ playDual(curPatBase, false); return; }
      if(patTimer) return;
      backToIdle();
    }
  });

  try{
    const tip = document.querySelector('#tab-panel .lobby-bar .lobby-tip');
    if(tip){
      const parts = [];
      if(talkBases.length) parts.push('点击对话 ×' + talkBases.length);
      if(canPat) parts.push('摸头');
      if(canExtra) parts.push(pinchMain ? '捏脸' : '戳');
      if(canLook) parts.push('视线');
      if(parts.length) tip.textContent = parts.join(' · ');
      else if(extra.some(n => /^Idle_0\d_Touch_M$/.test(n))) tip.textContent = '点击人物有反应（该大厅无对白）';
      else tip.textContent = '该大厅没有互动动画';
    }
  }catch(e){}
}

(function initRenderScale(){
  const base = (window.devicePixelRatio || 1);
  let scale = 1;
  try{
    Object.defineProperty(window, 'devicePixelRatio', {
      configurable: true,
      get(){ return base * scale; }
    });
  }catch(e){}
  window.__setRenderScale = function(s){ scale = Math.max(1, Math.min(3, +s || 1)); };
  window.__getRenderScale = function(){ return scale; };
})();

const PIC_KEY = 'ba.lobby.pic';
const PIC_DEFAULT = { quality: 1.5, temp: 0, sat: 100, con: 100, bri: 100, mode: 'simple', open: false };

function loadLobbyPic(){
  let v = {};
  try{ v = JSON.parse(localStorage.getItem(PIC_KEY) || '{}') || {}; }catch(e){}
  return Object.assign({}, PIC_DEFAULT, v);
}

function setupLobbyPicture(stage, player){
  const pic = loadLobbyPic();
  const panel = document.getElementById('lobby-pic-panel');
  const btn = document.getElementById('lobby-pic');
  if(!panel || !btn) return;

  function save(){ try{ localStorage.setItem(PIC_KEY, JSON.stringify(pic)); }catch(e){} }

  function apply(){

    if(window.__setRenderScale) window.__setRenderScale(pic.quality);

    const cv = stage.querySelector('canvas');
    if(cv){
      cv.style.filter = (pic.sat !== 100 || pic.con !== 100 || pic.bri !== 100)
        ? `saturate(${pic.sat}%) contrast(${pic.con}%) brightness(${pic.bri}%)`
        : '';
    }
    const im = stage.querySelector('img.lobby-bg-img');
    if(im){
      im.style.filter = (pic.sat !== 100 || pic.con !== 100 || pic.bri !== 100)
        ? `saturate(${pic.sat}%) contrast(${pic.con}%) brightness(${pic.bri}%)`
        : '';
    }

    const tint = document.getElementById('lobby-tint');
    if(tint){
      const v = clamp(-100, 100, pic.temp);
      const a = Math.abs(v) / 100 * 0.42;
      tint.style.background = v === 0 ? 'transparent'
        : (v > 0 ? `rgba(255,176,96,${a.toFixed(3)})` : `rgba(96,150,255,${a.toFixed(3)})`);
    }
  }

  function row(label, id, min, max, val, unit){
    return `<div class="lobby-pic-row"><label>${label}</label>`
      + `<input type="range" id="${id}" min="${min}" max="${max}" value="${val}">`
      + `<span class="val" id="${id}-v">${val}${unit || ''}</span></div>`;
  }

  function render(){
    const simple = `
      <div class="lobby-pic-row">
        <label>画质档位</label>
        <select id="pic-q" class="al-sel" style="padding:6px 8px;border-radius:8px">
          <option value="1" ${pic.quality === 1 ? 'selected' : ''}>标准（1× 渲染）</option>
          <option value="1.5" ${pic.quality === 1.5 ? 'selected' : ''}>高清（1.5× 渲染）</option>
          <option value="2" ${pic.quality === 2 ? 'selected' : ''}>超清（2× 渲染）</option>
          <option value="3" ${pic.quality === 3 ? 'selected' : ''}>极致（3× 渲染）</option>
        </select>
        <span class="val" id="pic-q-tip">当前 ${(stage.querySelector('canvas') || {}).width || '—'} px</span>
      </div>
      <div class="lobby-pic-note">
        渲染倍数越高越锐利，但受素材原始分辨率限制（Spine 图集本身有上限），超过素材分辨率不会更清晰，只是更耗显存。<br>
        想要更饱满的画面，用上面的「显示方式 · 填充」；想要整幅完整入镜，切「适应」。
      </div>`;

    const adv = `
      ${row('色温', 'pic-temp', -100, 100, pic.temp, '')}
      ${row('饱和度', 'pic-sat', 50, 160, pic.sat, '%')}
      ${row('对比度', 'pic-con', 70, 140, pic.con, '%')}
      ${row('亮度', 'pic-bri', 70, 140, pic.bri, '%')}
      <div class="lobby-pic-row">
        <button class="lobby-btn" id="pic-reset" style="padding:6px 14px">复位调色</button>
        <span class="lobby-pic-note" style="border:0;padding:0;margin:0">色温负值偏冷、正值偏暖；0 为原色。</span>
      </div>
      <div class="lobby-pic-note">
        关于「杜比视界 / 8K」：网页与 WebGL 无法输出 Dolby Vision 动态元数据，能否看到 HDR 效果取决于你的显示器与系统；
        本站能做到的是提高渲染分辨率与色彩调校。上列参数只作用于记忆大厅画面，不影响其它页面。
      </div>`;

    panel.innerHTML = `
      <div class="lobby-pic-tabs">
        <button data-m="simple" class="${pic.mode === 'simple' ? 'on' : ''}">简单模式</button>
        <button data-m="advanced" class="${pic.mode === 'advanced' ? 'on' : ''}">高级模式</button>
      </div>
      ${pic.mode === 'simple' ? simple : adv}`;

    panel.querySelectorAll('.lobby-pic-tabs button').forEach(b=>{
      b.onclick = ()=>{ pic.mode = b.dataset.m; save(); render(); };
    });

    const q = panel.querySelector('#pic-q');
    if(q){
      q.onchange = ()=>{
        pic.quality = +q.value; save(); apply(); render();
        setTimeout(()=>{
          const tip = panel.querySelector('#pic-q-tip');
          const cv = stage.querySelector('canvas');
          if(tip && cv) tip.textContent = '当前 ' + cv.width + ' px';
        }, 900);
      };
    }

    const bind = (id, key, unit)=>{
      const el = panel.querySelector('#' + id);
      const v = panel.querySelector('#' + id + '-v');
      if(!el) return;
      el.oninput = ()=>{
        pic[key] = +el.value;
        if(v) v.textContent = el.value + (unit || '');
        apply(); save();
      };
    };
    bind('pic-temp', 'temp', '');
    bind('pic-sat', 'sat', '%');
    bind('pic-con', 'con', '%');
    bind('pic-bri', 'bri', '%');

    const rs = panel.querySelector('#pic-reset');
    if(rs) rs.onclick = ()=>{ pic.temp = 0; pic.sat = 100; pic.con = 100; pic.bri = 100; save(); apply(); render(); };

    const cv = stage.querySelector('canvas');
    const tip = panel.querySelector('#pic-q-tip');
    if(tip && cv) tip.textContent = '当前 ' + cv.width + ' px';
  }

  btn.onclick = ()=>{
    pic.open = !pic.open;
    panel.classList.toggle('hidden', !pic.open);
    btn.classList.toggle('on', pic.open);
    if(pic.open) render();
  };
  if(pic.open){
    panel.classList.remove('hidden');
    btn.classList.add('on');
    render();
  }
  apply();
}

function clamp(a, b, v){ return Math.min(b, Math.max(a, v)); }

window.LobbyAudio = {
  pause: function(){ try{ if(window.Sfx) Sfx.lobbyPause(); }catch(e){} },
  stop: function(){ try{ if(window.Sfx) Sfx.lobbyStop(); }catch(e){} },
  get playing(){ try{ return !!(window.Sfx && Sfx.lobbyPlaying); }catch(e){ return false; } }
};

async function setupLobbyMusic(d){
  const sel = document.getElementById('lobby-music');
  const btn = document.getElementById('lobby-music-play');
  const vol = document.getElementById('lobby-music-vol');
  const tip = document.getElementById('lobby-music-tip');
  if(!sel || !btn) return;
  const S = window.Sfx;
  if(vol && S) vol.value = Math.round(S.lobbyVol * 100);

  let mine = [], generic = [];
  try{
    const j = await fetch('assets/role-music.json?v=20261015f').then(r=>r.json());
    const seen = {};
    const role = (j.roles||[]).find(x=>x.dir === (d.dir||'')) || (j.roles||[]).find(x=>String(x.id) === String(d.id));
    if(role) (role.tracks||[]).forEach(t=>{
      const a = t.local && t.local.audio;
      if(a && !seen[a]){ seen[a] = 1; mine.push({ name: (t.title||'') + (t.type?' · '+t.type:''), audio: a }); }
    });
    const topic = (j.topics||[]).find(x=>x.id === 'topic:lobby');
    if(topic) (topic.tracks||[]).forEach(t=>{
      const a = t.local && t.local.audio;
      if(a && !seen[a]){ seen[a] = 1; generic.push({ name: (t.title||''), audio: a }); }
    });
  }catch(e){}

  if(!(mine.length + generic.length)){
    sel.innerHTML = '<option>暂无本地音乐</option>';
    btn.disabled = true;
    if(tip) tip.textContent = '本地没有与该角色或回忆大厅相关的音乐';
    return;
  }

  sel.innerHTML =
    (mine.length ? `<optgroup label="该角色相关（${mine.length}）">${mine.map((t,i)=>`<option value="m${i}">${esc(t.name)}</option>`).join('')}</optgroup>` : '') +
    (generic.length ? `<optgroup label="回忆大厅曲目（${generic.length}）">${generic.map((t,i)=>`<option value="g${i}">${esc(t.name)}</option>`).join('')}</optgroup>` : '');

  const keep = window.__lobbyMusicSel && window.__lobbyMusicSel[d.dir];
  if(keep){ try{ if([].some.call(sel.options, o=>o.value===keep)) sel.value = keep; }catch(e){} }

  function cur(){
    const v = sel.value || '';
    const i = parseInt(v.slice(1), 10);
    if(v.charAt(0) === 'm' && mine[i]) return mine[i];
    if(v.charAt(0) === 'g' && generic[i]) return generic[i];
    return mine[0] || generic[0];
  }
  function refresh(){
    btn.textContent = (S && S.lobbyPlaying) ? '⏸ 暂停' : '▶ 播放';
  }
  function play(){
    if(!S) return;
    if(!S.lobbyOn){
      if(tip) tip.textContent = '记忆大厅音乐已在「我的设置」里关闭';
      return;
    }
    const t = cur();
    if(!t) return;

    try{ const au = document.getElementById('audio'); if(au) au.pause(); }catch(e){}
    S.lobbyPlay(mediaUrl(t.audio));
    window.__lobbyMusicAuto = true;
    setTimeout(refresh, 120);
  }

  btn.onclick = ()=>{ if(S && S.lobbyPlaying) { S.lobbyPause(); window.__lobbyMusicAuto = false; refresh(); } else play(); };
  sel.onchange = ()=>{
    window.__lobbyMusicSel = window.__lobbyMusicSel || {};
    window.__lobbyMusicSel[d.dir] = sel.value;
    if(S && S.lobbyPlaying) play();
    refresh();
  };
  if(vol) vol.oninput = ()=>{ if(S) S.setLobbyVol(+vol.value / 100); };

  refresh();

  if(window.__lobbyMusicAuto !== false) play();

  const tmr = setInterval(()=>{
    if(!document.getElementById('lobby-music')){ clearInterval(tmr); return; }
    refresh();
  }, 800);
}

function exportLobbyVideo(player, stage, d, bgColor){
  if(!player){ toast('当前学生没有可录制的动画'); return; }
  toast('🎬 正在录制动画（3 秒）……');
  const W = 1280, H = Math.round(W * (stage.clientHeight/Math.max(1,stage.clientWidth)));
  const cv = stage.querySelector('canvas');
  if(!cv || typeof cv.captureStream !== 'function'){ toast('浏览器不支持视频录制'); return; }
  const stream = cv.captureStream(30);
  const rec = new MediaRecorder(stream, { mimeType: (MediaRecorder.isTypeSupported('video/webm;codecs=vp9')?'video/webm;codecs=vp9':'video/webm') });
  const chunks = [];
  rec.ondataavailable = e=>{ if(e.data && e.data.size) chunks.push(e.data); };
  rec.onstop = ()=>{
    const blob = new Blob(chunks, { type: 'video/webm' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = (d.cn||'student')+'_记忆大厅_'+(Date.now())+'.webm';
    a.click();
    setTimeout(()=>URL.revokeObjectURL(a.href), 4000);
    toast('✅ 视频已导出（WebM）');
  };
  rec.start();
  setTimeout(()=>{ try{ rec.stop(); }catch(e){} }, 3000);
}
