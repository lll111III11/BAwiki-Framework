'use strict';

const EXTERNAL = !!window.BAWIKI_EXTERNAL;

const THEME_KEY = 'bwiki_theme';
(function applyThemeOnLoad(){
  const t = localStorage.getItem(THEME_KEY) || 'day';
  document.body.classList.toggle('night', t === 'night');
})();
function currentNight(){ return document.body.classList.contains('night'); }
function setNight(on){
  document.body.classList.toggle('night', on);
  localStorage.setItem(THEME_KEY, on ? 'night' : 'day');
  const sw = document.getElementById('theme-switch');
  if(sw) sw.classList.toggle('on', on);
}

function buildStarLayer(){
  const layer = document.createElement('div');
  layer.id = 'night-stars';
  for(let i = 0; i < 90; i++){
    const s = document.createElement('i');
    s.style.left = (Math.random()*100).toFixed(2) + '%';
    s.style.top  = (Math.random()*100).toFixed(2) + '%';
    const d = (Math.random()*1.6 + 0.8);
    s.style.width = d.toFixed(2) + 'px';
    s.style.height = s.style.width;
    s.style.animationDelay = (Math.random()*5).toFixed(1) + 's';
    s.style.animationDuration = (Math.random()*3.5 + 2.2).toFixed(1) + 's';
    layer.appendChild(s);
  }
  document.body.appendChild(layer);
}
buildStarLayer();

const SETTINGS = { ui_animation:true, ui_transition:true, fast_view:true, show_download:true, personal:false, me:'', role:'guest', perms:[] };
async function loadSiteSettings(){
  try{
    const r = await fetch('/api/settings?t='+Date.now());
    if(r.ok){ const j = await r.json(); Object.assign(SETTINGS, j); }
  }catch(e){}
  applyUiClass();
}
function applyUiClass(){
  document.body.classList.toggle('no-fx', !(SETTINGS.ui_animation && SETTINGS.ui_transition));
  document.body.classList.toggle('no-dl', !SETTINGS.show_download);
}
loadSiteSettings();
function fastKey(type, key){
  return (SETTINGS.fast_view && (type==='gallery'||type==='music'||type==='students')) ? 'fast' : type;
}

const PERM_ROUTE = { students:'students', gallery:'gallery', music:'music', videos:'videos', bilibili:'bili', online:'online' };
const PERM_CN = { students:'学生图鉴', gallery:'CG画廊', music:'留声机', videos:'官方PV', bili:'B站视频', online:'在线更新', memories:'回忆大厅', models:'3D模型' };
function isAdminRole(){ return SETTINGS.role==='super' || SETTINGS.role==='admin'; }
async function loadMe(){
  try{
    const r = await fetch('/api/me?t='+Date.now());
    if(r.ok){ const j = await r.json(); if(j.ok){ SETTINGS.me = j.name||''; SETTINGS.role = j.role||'user'; SETTINGS.perms = j.perms||[]; SETTINGS.hasUi = !!j.uis; } }
  }catch(e){}
  applyPermUI();
}
function hasPerm(route){
  const p = PERM_ROUTE[route];
  if(!p) return true;
  if(!SETTINGS.perms || !SETTINGS.perms.length) return true;
  return SETTINGS.perms.includes(p);
}
function applyPermUI(){
  const nav = document.querySelector('.nav-links');
  if(!nav) return;

  document.querySelectorAll('.nav-links a[data-route]').forEach(a=>{
    const r = a.dataset.route;
    a.classList.remove('perm-locked');
    a.onclick = null;
    if(EXTERNAL){
      if(r==='admin'){ a.style.display = isAdminRole() ? '' : 'none'; return; }
      if(r==='home' || r==='me'){ a.style.display = ''; return; }
      a.style.display = hasPerm(r) ? '' : 'none';
    }else{
      a.style.display = '';
    }
  });

  if(EXTERNAL && !nav.querySelector('a[data-act=logout]')){
    const lo = document.createElement('a');
    lo.href = '#';
    lo.dataset.act = 'logout';
    lo.textContent = '↪ 退出登录';
    lo.style.cssText = 'padding:8px 14px;border-radius:999px;font-size:14px;font-weight:600;color:#55607a;white-space:nowrap';
    lo.onclick = e=>{ e.preventDefault(); location.href = '/__logout'; };
    nav.appendChild(lo);
  }
}

const DISC_KEY = 'bwiki_disc_ok';
function showDiscGate(){ showFx($('#disc-gate')); }
function acceptDisclaimer(){
  try{ localStorage.setItem(DISC_KEY, '1'); }catch(e){}
  hideFx($('#disc-gate'));
  initApp();
}
function declineDisclaimer(){
  const err = $('#disc-err');
  err.textContent = '不同意上述说明，则无法进入本站。';
  err.classList.remove('hidden');
}

document.getElementById('disc-ok').onclick = acceptDisclaimer;
document.getElementById('disc-no').onclick = declineDisclaimer;

function showLoginGate(){ showFx($('#login-gate')); }
function hideLoginGate(){ hideFx($('#login-gate')); }
async function submitLogin(e){
  e.preventDefault();
  const err = $('#lg-err'); err.classList.add('hidden');
  const btn = $('#lg-btn');
  btn.disabled = true; btn.textContent = '登录中…';
  try{
    const name = $('#lg-user').value.trim();
    const pass = $('#lg-pass').value;
    if(!name){ err.textContent = '请输入账号'; err.classList.remove('hidden'); return; }
    const r = await fetch('/api/login', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({name, pass})});
    const j = await r.json().catch(()=>({}));
    if(j && j.ok){ location.reload(); return; }
    err.textContent = (j && j.error) || '登录失败，请检查账号或密码';
    err.classList.remove('hidden');
  }catch(err2){
    $('#lg-err').textContent = '网络错误，请重试';
    $('#lg-err').classList.remove('hidden');
  }finally{
    btn.disabled = false; btn.textContent = '登 录';
  }
}
async function initApp(){

  if(!EXTERNAL){ applyPermUI(); router(); return; }

  if(localStorage.getItem(DISC_KEY) !== '1'){ showDiscGate(); return; }

  let loggedIn = false;
  try{
    const r = await fetch('/api/me?t='+Date.now());
    if(r.status===401){ loggedIn = false; }
    else{
      const j = await r.json();
      if(j && j.ok){ SETTINGS.me=j.name||''; SETTINGS.role=j.role||'user'; SETTINGS.perms=j.perms||[]; loggedIn = true; }
    }
  }catch(err){}
  applyPermUI();
  if(loggedIn){ hideLoginGate(); router(); }
  else { showLoginGate(); }
}

function sleep(ms){ return new Promise(r=>setTimeout(r,ms)); }
async function pageSwitch(fn, arg){
  const canFx = SETTINGS.ui_transition && !document.body.classList.contains('no-fx');
  const wrap = $('#app');

  if(canFx){
    wrap.classList.remove('fx-in');
    wrap.classList.add('fx-out');
    await sleep(170);
  }
  try{
    await fn(arg);
  }finally{
    if(canFx){
      wrap.classList.remove('fx-out');
      void wrap.offsetWidth;
      wrap.classList.add('fx-in');
      applyStagger(wrap);
      setTimeout(()=>wrap.classList.remove('fx-in'), 480);
    }
  }
}

async function switchFx(el, fn){
  const canFx = SETTINGS.ui_animation && SETTINGS.ui_transition && !document.body.classList.contains('no-fx');
  if(!el || !canFx){ await fn(); return; }
  const tok = (el._swTok = (el._swTok || 0) + 1);
  el.classList.remove('sw-in');
  el.classList.add('sw-out');
  await sleep(140);
  if(el._swTok !== tok) return;
  try{
    await fn();
  }finally{

    el.classList.remove('sw-out');
    el.classList.add('sw-in');
    applyStagger(el);
    setTimeout(()=>{ if(el._swTok === tok) el.classList.remove('sw-in'); }, 480);
  }
  if(el._swTok !== tok) return;
}

function swapFx(el){
  if(!el) return;
  if(!(SETTINGS.ui_animation && SETTINGS.ui_transition) || document.body.classList.contains('no-fx')) return;
  el.classList.remove('swap');
  void el.offsetWidth;
  el.classList.add('swap');
  setTimeout(()=>el.classList.remove('swap'), 620);
}

const $ = s => document.querySelector(s);
const app = $('#app');

function showFx(el){
  if(!el) return;
  el._fxTok = (el._fxTok || 0) + 1;
  el.classList.remove('fx-closing');
  el.classList.remove('hidden');
}

function hideFx(el){
  if(!el || el.classList.contains('hidden')) return;
  const canFx = SETTINGS.ui_transition && !document.body.classList.contains('no-fx');
  if(!canFx){ el.classList.add('hidden'); return; }
  const tok = (el._fxTok = (el._fxTok || 0) + 1);
  el.classList.add('fx-closing');
  setTimeout(()=>{
    if(el._fxTok !== tok) return;
    el.classList.remove('fx-closing');
    el.classList.add('hidden');
  }, 190);
}

function applyStagger(scope){
  if(!scope || document.body.classList.contains('no-fx')) return;
  const els = scope.querySelectorAll('.quick-card,.stat-card,.stu-card,.video-card,.img-cell,.music-row');
  const n = Math.min(els.length, 14);
  for(let i = 0; i < n; i++){
    els[i].style.animationDelay = (i * 34) + 'ms';
    els[i].classList.add('fx-stagger');
  }
}

const encoder = new TextEncoder();
function toHex(str){
  const bytes = encoder.encode(str);
  let s = '';
  for(let i=0;i<bytes.length;i++) s += bytes[i].toString(16).padStart(2,'0');
  return s;
}
function mediaUrl(rel){ return '/media?p=' + toHex(rel); }
function thumbUrl(type, key){ return '/thumb?type=' + type + '&p=' + toHex(key); }
function esc(s){ return String(s==null?'':s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c])); }

const apiCache = {};
const BUILD = '20260906a';
async function api(name){
  if(apiCache[name]) return apiCache[name];
  const r = await fetch('/api/'+name+'?t='+BUILD);
  const j = await r.json();
  apiCache[name] = j;
  return j;
}

function toast(msg){
  const t = $('#toast'); t.textContent = msg; t.classList.remove('hidden');
  clearTimeout(t._timer); t._timer = setTimeout(()=>hideFx(t), 2200);
}

function debounce(fn, ms){
  let t;
  return function(...args){ clearTimeout(t); t = setTimeout(()=>fn.apply(this, args), ms); };
}

function showMsg(text, title){
  $('#msg-title').textContent = title || '老师，稍等一下！';
  $('#msg-text').innerHTML = text || '这份资料还没有下载到本地，当前无法浏览。<br>离线版仅提供<b>已收录</b>的内容。';
  showFx($('#msg-modal'));
}
$('#msg-ok').onclick = ()=>hideFx($('#msg-modal'));
$('#msg-modal').onclick = e=>{ if(e.target.id==='msg-modal') hideFx($('#msg-modal')); };

const FAV_KEY = 'bwiki_favs';
const FAV = {
  set: new Set(),
  load(){ try{ const a = JSON.parse(localStorage.getItem(FAV_KEY) || '[]'); this.set = new Set(Array.isArray(a) ? a : []); }catch(e){ this.set = new Set(); } return this.set; },
  save(){ try{ localStorage.setItem(FAV_KEY, JSON.stringify([...this.set])); }catch(e){} window.dispatchEvent(new CustomEvent('fav-changed')); },
  has(k){ return this.set.has(k); },
  toggle(k){ if(this.set.has(k)) this.set.delete(k); else this.set.add(k); this.save(); return this.set.has(k); },
  of(kind){ const p = kind + ':'; return [...this.set].filter(k=>k.startsWith(p)); },
  count(kind){ return this.of(kind).length; }
};
FAV.load();

let favRefresh = null;

function favBtn(k){
  const on = FAV.has(k);
  return `<button class="fav-btn${on?' on':''}" data-fav="${esc(k)}" title="${on?'取消喜欢':'喜欢'}" `
       + `aria-label="${on?'取消喜欢':'喜欢'}" onclick="event.stopPropagation();window.FAVToggle(this)">${on?'\u2665':'\u2661'}</button>`;
}
window.FAVToggle = function(btn){
  const k = btn.dataset.fav; if(!k) return;
  const on = FAV.toggle(k);
  btn.classList.toggle('on', on);
  btn.textContent = on ? '\u2665' : '\u2661';
  const tip = on ? '取消喜欢' : '喜欢';
  btn.title = tip; btn.setAttribute('aria-label', tip);

  if(!document.body.classList.contains('no-fx')){ btn.classList.remove('pop'); void btn.offsetWidth; btn.classList.add('pop'); }

  document.querySelectorAll('.fav-n').forEach(el=>{ el.textContent = FAV.count(el.dataset.kind); });
  if(typeof favRefresh === 'function') favRefresh();
  if(k.startsWith('mus:')) syncMusicTagRow(k.slice(4));
};

function favTabBtn(kind, active){ return `<button class="sub-tab fav-tab${active?' active':''}" data-fav-tab="${kind}">\u2665 我喜欢的 <small class="fav-n" data-kind="${kind}">${FAV.count(kind)}</small></button>`; }
function favEmpty(){
  return '<div class="empty fav-empty"><div class="big">\u2661</div>还没有收藏，去列表里点一下 \u2665 就能存到这里。</div>';
}

const MTAG_KEY = 'bwiki_music_tags';
const MUSIC_TAGS = {
  map: {},
  load(){ try{ const o = JSON.parse(localStorage.getItem(MTAG_KEY) || '{}'); this.map = (o && typeof o === 'object' && !Array.isArray(o)) ? o : {}; }catch(e){ this.map = {}; } return this.map; },
  save(){ try{ localStorage.setItem(MTAG_KEY, JSON.stringify(this.map)); }catch(e){} },
  get(dir){ const a = this.map[dir]; return Array.isArray(a) ? a : []; },
  add(dir, tag){
    tag = String(tag == null ? '' : tag).trim().slice(0, 12);
    if(!tag) return this.get(dir);
    const a = this.get(dir).slice();
    if(!a.includes(tag)){ a.push(tag); this.map[dir] = a; this.save(); }
    return a;
  },
  del(dir, tag){
    const a = this.get(dir).filter(x => x !== tag);
    if(a.length) this.map[dir] = a; else delete this.map[dir];
    this.save(); return a;
  }
};
MUSIC_TAGS.load();
const TAG_PRESETS = ['循环', '睡前', '战斗', '剧情', '泪目', '燃', '轻快'];

function tagChipsHtml(dir){
  const tags = MUSIC_TAGS.get(dir);
  return tags.map(t => `<span class="music-tag">${esc(t)}<b data-tag-del="${esc(t)}" title="删除标签" onclick="event.stopPropagation()">×</b></span>`).join('')
       + `<button class="music-tag-add" data-tag-edit="${esc(dir)}" onclick="event.stopPropagation()">${tags.length ? '＋ 标签' : '＋ 加标签'}</button>`;
}
function musicTagsHtml(t){
  const on = FAV.has('mus:' + t.dir);
  return `<div class="music-tags" data-tagrow="${esc(t.dir)}"${on ? '' : ' style="display:none"'}>${tagChipsHtml(t.dir)}</div>`;
}
function syncMusicTagRow(dir){
  document.querySelectorAll('[data-tagrow]').forEach(box => {
    if(box.dataset.tagrow !== dir) return;
    box.innerHTML = tagChipsHtml(dir);
    box.style.display = FAV.has('mus:' + dir) ? '' : 'none';
  });
}
let tagEditorEl = null, tagEditorDir = null;
function closeTagEditor(){ if(tagEditorEl){ tagEditorEl.remove(); tagEditorEl = null; tagEditorDir = null; } }
function openTagEditor(dir, anchor){
  if(tagEditorEl && tagEditorDir === dir){ closeTagEditor(); return; }
  closeTagEditor();
  tagEditorDir = dir;
  const el = document.createElement('div');
  el.className = 'tag-editor';
  el.innerHTML = `<div class="tag-editor-title">🏷 曲目标签</div>
    <div class="tag-editor-list" id="tag-ed-list"></div>
    <input class="tag-editor-input" id="tag-ed-input" maxlength="12" placeholder="输入自定义标签，回车添加">
    <div class="tag-editor-presets">${TAG_PRESETS.map(x => `<button type="button" data-tag-preset="${esc(x)}">${esc(x)}</button>`).join('')}</div>`;
  document.body.appendChild(el);
  tagEditorEl = el;
  const r = anchor.getBoundingClientRect();
  const w = 268;
  el.style.left = Math.max(8, Math.min(window.innerWidth - w - 8, r.left)) + 'px';
  el.style.top = (r.bottom + 14 + el.offsetHeight > window.innerHeight
      ? Math.max(8, r.top - el.offsetHeight - 10)
      : r.bottom + 8) + 'px';
  const redraw = () => {
    const list = el.querySelector('#tag-ed-list');
    const tags = MUSIC_TAGS.get(dir);
    list.innerHTML = tags.length
      ? tags.map(t => `<span class="music-tag">${esc(t)}<b data-tag-ed-del="${esc(t)}" title="删除">×</b></span>`).join('')
      : '<span class="tag-ed-none">还没有标签</span>';
  };
  redraw();
  const input = el.querySelector('#tag-ed-input');
  input.addEventListener('keydown', e => {
    if(e.key === 'Enter'){ e.preventDefault(); MUSIC_TAGS.add(dir, input.value); input.value = ''; redraw(); syncMusicTagRow(dir); }
    else if(e.key === 'Escape'){ closeTagEditor(); }
  });
  el.addEventListener('click', e => {
    e.stopPropagation();
    const preset = e.target.dataset.tagPreset;
    if(preset){ MUSIC_TAGS.add(dir, preset); redraw(); syncMusicTagRow(dir); return; }
    const del = e.target.dataset.tagEdDel;
    if(del){ MUSIC_TAGS.del(dir, del); redraw(); syncMusicTagRow(dir); }
  });
  setTimeout(() => { try{ input.focus(); }catch(e){} }, 30);
}
document.addEventListener('click', e => {
  if(!tagEditorEl) return;
  if(tagEditorEl.contains(e.target) || e.target.closest('[data-tag-edit]')) return;
  closeTagEditor();
});
window.addEventListener('scroll', () => { if(tagEditorEl) closeTagEditor(); }, true);

function thumbImg(type, key, mediaRel){
  const t = fastKey(type, key);
  return `<img loading="lazy" decoding="async" src="${thumbUrl(t,key)}" data-rel="${esc(mediaRel)}"
    onload="this.classList.add('loaded')"
    onerror="if(this.dataset.f){var p=this.parentNode;p.classList.add('missing');this.remove()}else{this.dataset.f=1;this.src='${mediaUrl(mediaRel)}'}">`;
}

function origImg(rel){
  return `<img loading="lazy" decoding="async" src="${mediaUrl(rel)}" data-rel="${esc(rel)}"
    onload="this.classList.add('loaded')"
    onerror="var p=this.parentNode;if(p){p.classList.add('missing');this.remove()}">`;
}

function ensureCellActions(cell, rel){
  let panel = cell.querySelector(':scope > .img-actions');
  if(!panel){
    panel = document.createElement('div');
    panel.className = 'img-actions';
    if(EXTERNAL){

      panel.innerHTML =
        `<a class="ia-btn" data-act="dl" title="下载原图" href="${mediaUrl(rel)}" download="${esc(rel.split('/').pop())}">⬇<span>下载</span></a>`;
    }else{
      panel.innerHTML =
        '<button data-act="copy" title="复制图片文件到剪贴板">📋<span>复制</span></button>' +
        `<a class="ia-btn" data-act="dl" title="下载原图" href="${mediaUrl(rel)}" download="${esc(rel.split('/').pop())}">⬇<span>下载</span></a>` +
        '<button data-act="open" title="在资源管理器中定位并选中该图片">📁<span>打开文件夹</span></button>';
    }
    panel.addEventListener('click', async e=>{
      const t = e.target.closest('[data-act]');
      if(!t) return;
      e.stopPropagation();
      const act = t.dataset.act;
      if(act==='dl') return;
      const rel = panel.dataset.rel;
      if(!rel) return;
      if(act==='copy'){
        toast('正在复制……');
        try{ const r = await fetch('/api/local/copy?p='+toHex(rel)); const j = await r.json();
          toast(j.ok?'📋 已复制到剪贴板':('复制失败：'+(j.error||'')));
        }catch(err){ toast('复制失败'); }
      }else if(act==='open'){
        try{ const r = await fetch('/api/local/open-folder?p='+toHex(rel)); const j = await r.json();
          if(!j.ok) toast('打开失败：'+(j.error||''));
          else toast('📁 已在资源管理器中定位该图片');
        }catch(err){ toast('打开失败'); }
      }
      panel.classList.remove('show');
    });
    cell.appendChild(panel);
  }else{

    const dl = panel.querySelector('[data-act=dl]');
    if(dl){ dl.href = mediaUrl(rel); dl.setAttribute('download', rel.split('/').pop()); }
  }
  panel.dataset.rel = rel;
  return panel;
}
function hideCellActions(cell){
  const panel = cell.querySelector(':scope > .img-actions');
  if(panel) panel.classList.remove('show');
}

document.addEventListener('mouseover', e=>{
  const img = e.target.closest('img[data-rel]');
  if(!img) return;
  const cell = img.closest('.img-cell') || img.closest('.video-thumb') || (img.id==='lb-img' ? $('#lightbox') : null);
  if(!cell) return;
  const panel = ensureCellActions(cell, img.dataset.rel);
  requestAnimationFrame(()=>panel.classList.add('show'));
});
document.addEventListener('mouseout', e=>{

  const t = e.target;
  const cell = t && t.closest ? (t.closest('.img-cell') || t.closest('.video-thumb') || (t.id==='lb-img' ? $('#lightbox') : null)) : null;
  if(!cell) return;
  const rt = e.relatedTarget;
  if(rt && rt.nodeType === 1 && cell.contains(rt)) return;
  hideCellActions(cell);
});

async function openFolder(rel){
  if(EXTERNAL){ toast('对外访客无权访问本地文件'); return; }
  if(!rel){ toast('路径无效'); return; }
  try{
    const r = await fetch('/api/local/open-folder?p='+toHex(rel));
    const j = await r.json();
    if(!j.ok) toast('打开失败：'+(j.error||''));
    else toast('📁 已打开所在文件夹');
  }catch(e){ toast('打开失败'); }
}

function paginated(container, items, renderItem, pageSize){
  let shown = 0;

  const bar = document.createElement('div');
  bar.className = 'load-more-bar';
  bar.style.cssText = 'grid-column:1/-1;';

  const moreBtn = document.createElement('button');
  moreBtn.className = 'load-more';
  moreBtn.textContent = '加载更多';

  const allBtn = document.createElement('button');
  allBtn.className = 'load-more load-all';
  allBtn.textContent = '全部加载';

  const hint = document.createElement('div');
  hint.className = 'load-all-hint';
  hint.textContent = '（可能对设备及流量有一定限制，可能加载不全或出现空白，属于 Beta 型。）';

  bar.appendChild(moreBtn);
  bar.appendChild(allBtn);
  bar.appendChild(hint);

  function appendRange(end){
    const frag = document.createDocumentFragment();
    const nodes = [];
    for(let i=shown;i<end;i++){ const n = renderItem(items[i], i); if(n){ nodes.push(n); frag.appendChild(n); } }
    container.insertBefore(frag, bar);

    if(SETTINGS.ui_animation && SETTINGS.ui_transition && !document.body.classList.contains('no-fx')){
      const k = Math.min(nodes.length, 14);
      for(let j=0;j<k;j++){ nodes[j].style.animationDelay = (j*28)+'ms'; nodes[j].classList.add('fx-stagger'); }
    }
    shown = end;
  }
  function updateBar(){
    const rest = items.length - shown;
    bar.style.display = rest > 0 ? '' : 'none';
    moreBtn.textContent = '加载更多（还有 ' + rest + ' 项）';
    allBtn.textContent = '全部加载（还有 ' + rest + ' 项）';
  }
  function renderBatch(){
    appendRange(Math.min(shown + pageSize, items.length));
    updateBar();
    if(items.length === 0){
      container.innerHTML = '<div class="empty"><div class="big">📭</div>暂无内容（可能索引尚未生成完毕，或缩略图仍在后台制作中）</div>';
    }
  }

  function renderAll(){
    const step = ()=>{
      appendRange(Math.min(shown + 200, items.length));
      updateBar();
      if(shown < items.length) setTimeout(step, 0);
    };
    step();
  }
  moreBtn.onclick = renderBatch;
  allBtn.onclick = renderAll;

  container._locate = (idx)=>{
    if(idx < 0 || idx >= items.length) return null;
    while(shown <= idx) appendRange(Math.min(shown + 200, items.length));
    updateBar();
    const el = container.children[idx] || null;
    if(!el) return null;
    el.scrollIntoView({block:'center'});
    el.classList.add('loc-hit');

    setTimeout(()=>el.scrollIntoView({block:'center', behavior:'smooth'}), 240);
    setTimeout(()=>el.classList.remove('loc-hit'), 2600);
    return el;
  };
  container.appendChild(bar);
  renderBatch();
}

function renderList(container, items, renderItem, pageSize, after){
  return switchFx(container, ()=>{
    container.innerHTML = '';
    paginated(container, items, renderItem, pageSize);
    if(after) after();
  });
}

function jumpInput(inputId, list, container, find){
  const el = document.getElementById(inputId);
  if(!el) return;
  el.onkeydown = e=>{
    if(e.key !== 'Enter') return;
    e.preventDefault();
    const raw = (el.value || '').trim();
    if(!raw || !list.length) return;
    let idx = find ? find(raw) : -1;
    if(idx < 0){
      const n = parseInt(raw, 10);
      if(n > 0 && n <= list.length) idx = n - 1;
    }
    if(idx < 0){ toast('没找到「'+raw+'」· 这里可输入 1 ~ '+list.length+' 的序号'); return; }
    if(container._locate(idx)) toast('📍 已定位到第 '+(idx+1)+' 项');
  };
}

const routes = {
  home: renderHome, students: renderStudents, student: renderStudent,
  gallery: renderGallery, music: renderMusic, videos: renderVideos, bilibili: renderBilibili,
  online: renderOnline, me: renderMe
};

const UI_LS = 'ba.ui.v1';
const uiState = { sticky: true, remember: true, smoothRate: true, scroll: {} };
try { Object.assign(uiState, JSON.parse(localStorage.getItem(UI_LS) || '{}')); } catch(e) {}
function uiSave(){ try{ localStorage.setItem(UI_LS, JSON.stringify(uiState)); }catch(e){} }
function applyUiPrefs(){
  document.body.classList.toggle('nav-static', uiState.sticky === false);
}
let lastRouteKey = '';
function restoreScroll(y){
  if(!y) return;
  let tries = 0;
  const run = ()=>{
    tries++;
    window.scrollTo(0, y);
    const ok = Math.abs(window.scrollY - y) <= 4;
    if(!ok && tries < 16) setTimeout(run, tries < 5 ? 120 : 280);
  };
  requestAnimationFrame(run);
}
applyUiPrefs();
let scrollSaveTimer = 0;
window.addEventListener('scroll', ()=>{
  if(!uiState.remember || !lastRouteKey) return;
  clearTimeout(scrollSaveTimer);
  scrollSaveTimer = setTimeout(()=>{ uiState.scroll[lastRouteKey] = window.scrollY; uiSave(); }, 250);
}, { passive: true });

async function router(){
  const hash = location.hash.replace(/^#\/?/, '') || 'home';
  const [page, enArg] = hash.split('/');

  let arg = enArg;
  if (arg){ try { arg = decodeURIComponent(arg); } catch(e){ arg = enArg; } }

  if(!hasPerm(page) && page !== 'me'){ toast('🔒 当前账号没有访问该板块的权限'); location.hash = '#/home'; return; }
  if(uiState.remember && lastRouteKey){
    uiState.scroll[lastRouteKey] = window.scrollY;
    uiSave();
  }
  const fn = routes[page] || renderHome;
  favRefresh = null;
  if(window.LobbyAudio) LobbyAudio.pause();
  if(window.__setRenderScale) window.__setRenderScale(1);
  window.scrollTo(0,0);
  document.querySelectorAll('.nav-links a').forEach(a=>{
    a.classList.toggle('active', a.dataset.route === page);
  });
  await pageSwitch(fn, arg);
  lastRouteKey = hash;
  if(uiState.remember) restoreScroll(uiState.scroll[hash] || 0);

  if(window.Sfx && page !== 'student') Sfx.scene(page);
}
window.addEventListener('hashchange', router);

async function renderHome(){
  app.innerHTML = '<div class="wrap"><div class="loading">正在加载本地索引……</div></div>';
  const [stu, gal, mus, vid] = await Promise.all([api('students'), api('gallery'), api('music'), api('videos')]);
  let galImgs = 0;
  gal.cats.forEach(c => c.subs.forEach(s => galImgs += s.files.length));
  const biliCount = vid.videos.filter(v=>v.category==='B站官方视频').length;

  const cards = [
    {icon:'👤', name:'学生图鉴', desc:'学生资料、立绘、皮肤、画廊、语音与技能，SD 小人 3D 模型已融入学生档案', url:'#/students', count:stu.students.length+' 名角色', perm:'students'},
    {icon:'🖼️', name:'CG画廊', desc:'主线剧情 CG、活动图、设定素材、PV 逐帧截图', url:'#/gallery', count:gal.cats.length+' 个分类 · '+galImgs+' 张图片', perm:'gallery'},
    {icon:'🎵', name:'留声机', desc:'游戏内全量 OST 与主题曲 MV，另按学生收录角色歌与相关曲目', url:'#/music', count:mus.tracks.filter(t=>t.kind!=='mv').length+' 首 OST · '+mus.tracks.filter(t=>t.kind==='mv').length+' 支 MV', perm:'music'},
    {icon:'🎬', name:'官方PV', desc:'国服/日服/活动/联动 PV，本地高清双轨视频，支持拖放进度', url:'#/videos', count:vid.videos.filter(v=>v.category!=='B站官方视频').length+' 个视频', perm:'videos'},
    {icon:'📺', name:'B站视频分区', desc:'蔚蓝档案官方 B 站账号视频本地镜像（持续补充）', url:'#/bilibili', count:biliCount+' 个已收录', perm:'bili'}
  ];

  app.innerHTML = `
  <div class="wrap">
    <div class="hero">
      <img class="hero-bg" src="assets/banner.jpg" alt="">
      <button id="theme-switch" class="ts-track ${currentNight()?'on':''}" title="切换白天 / 夜晚模式" aria-pressed="${currentNight()}">
        <span class="ts-ico ts-sun">☀</span>
        <span class="ts-thumb"></span>
        <span class="ts-ico ts-moon">☾</span>
      </button>
      <div class="hero-content">
        <div class="hero-badges">
          <span class="hero-badge">📦 完全离线</span>
          <span class="hero-badge">⚡ 本地加速 · 缩略图预载</span>
          <span class="hero-badge">🎮 蔚蓝档案全资源</span>
        </div>
        <h1>基沃托斯古书馆</h1>
        <p>本地离线版 · 复刻 kivo.wiki 界面与内容分区。<br>学生图鉴、CG 画廊、游戏留声机、官方 PV 与 B 站视频，全部资源均存放于本地，秒开不等待。</p>
        <a class="btn" href="#/students">📖 开始翻阅</a>
      </div>
    </div>
    <div class="quick-grid">
      ${cards.map(c=>{
        const locked = !hasPerm(c.perm);
        return `
        <a class="quick-card ${locked?'locked':''}" href="${c.url}" data-locked="${locked?'1':'0'}">
          <div class="quick-icon">${c.icon}</div>
          <div class="quick-name">${c.name}</div>
          <div class="quick-desc">${c.desc}</div>
          <div class="quick-count">${locked?'🔒 当前账号无权访问':c.count}</div>
        </a>`;
      }).join('')}
    </div>
    <div class="stats-row">
      <div class="stat-card"><div class="stat-num">${stu.students.length}</div><div class="stat-label">学生档案</div></div>
      <div class="stat-card"><div class="stat-num">${galImgs.toLocaleString()}</div><div class="stat-label">画廊图片</div></div>
      <div class="stat-card"><div class="stat-num">${mus.tracks.length}</div><div class="stat-label">音乐曲目</div></div>
      <div class="stat-card"><div class="stat-num">${vid.videos.length}</div><div class="stat-label">视频资源</div></div>
    </div>
  </div>`;

  document.querySelectorAll('.quick-card[data-locked="1"]').forEach(a=>{
    a.onclick = e=>{ e.preventDefault(); toast('🔒 当前账号没有访问该板块的权限'); };
  });

  const tsw = document.getElementById('theme-switch');
  if(tsw){
    tsw.onclick = ()=>{ setNight(!currentNight()); };
  }
}

let stuFilter = {q:'', school:'', rarity:''};
let stuView = 'all';
async function renderStudents(){
  const data = await api('students');

  const expanded = [];
  data.students.forEach(s=>{
    const vs = (s.variants && s.variants.length) ? s.variants : [{ key:s.id, dir:s.dir, skin:'原皮' }];
    vs.forEach(v=>{
      expanded.push({
        id:s.id, key:v.key||v.id, dir:v.dir, skin:v.skin||'原皮',
        cn:s.cn, jp:s.jp, school:s.school, rarity:s.rarity
      });
    });
  });
  const schools = [...new Set(data.students.map(s=>s.school))].sort();
  app.innerHTML = `
  <div class="wrap">
    <div class="page-title">👤 学生图鉴</div>
    <div class="page-sub">共 ${data.students.length} 名学生 · ${expanded.length} 个服装版本（同名角色多期立绘/皮肤均已展开）</div>
    <div class="filter-bar">
      <input type="text" id="f-q" placeholder="🔍 搜索中文名 / 日文名 / ID" value="${esc(stuFilter.q)}">
      <label>学校</label>
      <select id="f-school"><option value="">全部</option>${schools.map(s=>`<option ${s===stuFilter.school?'selected':''}>${esc(s)}</option>`).join('')}</select>
      <label>稀有度</label>
      <select id="f-rarity">
        <option value="">全部</option>
        <option value="1" ${stuFilter.rarity==='1'?'selected':''}>★</option>
        <option value="2" ${stuFilter.rarity==='2'?'selected':''}>★★</option>
        <option value="3" ${stuFilter.rarity==='3'?'selected':''}>★★★</option>
      </select>
      <span class="sp"></span>
      <input type="text" id="f-jump" class="jump-box" placeholder="📍 定位：序号 / 学生ID" title="输入序号或学生 ID 后回车，会自动展开「加载更多」并滚到那一项">
      <span id="f-count" style="font-size:13px;color:var(--text-sub)"></span>
    </div>
    <div class="sub-tabs" id="stu-subs">
      <button class="sub-tab ${stuView==='all'?'active':''}" data-v="all">全部</button>
      ${favTabBtn('stu', stuView==='fav')}
    </div>
    <div class="stu-grid" id="stu-grid"></div>
  </div>`;

  $('#stu-subs').querySelectorAll('.sub-tab').forEach(b=>b.onclick=()=>{

    stuView = b.dataset.favTab ? 'fav' : (b.dataset.v || 'all');
    $('#stu-subs').querySelectorAll('.sub-tab').forEach(x=>x.classList.toggle('active', x===b));
    apply();
  });

  const grid = $('#stu-grid');
  function apply(){
    const q = $('#f-q').value.trim().toLowerCase();
    stuFilter = {q, school:$('#f-school').value, rarity:$('#f-rarity').value};
    let list = expanded.filter(s=>{
      if(stuFilter.school && s.school !== stuFilter.school) return false;
      if(stuFilter.rarity && String(s.rarity||'') !== stuFilter.rarity) return false;
      if(q){
        const hay = (s.cn+' '+s.jp+' '+s.id+' '+s.skin).toLowerCase();
        if(!hay.includes(q)) return false;
      }
      return true;
    });

    if(stuView==='fav') list = list.filter(s=>FAV.has('stu:'+s.dir));
    $('#f-count').textContent = stuView==='fav' ? ('我喜欢的 '+list.length+' 个') : ('显示 '+list.length+' / '+expanded.length);

    favRefresh = stuView==='fav' ? apply : null;
    if(!list.length){
      switchFx(grid, ()=>{ grid.innerHTML = stuView==='fav' ? favEmpty() : '<div class="empty">没有匹配的学生</div>'; });
      return;
    }

    renderList(grid, list, s=>{
      const card = document.createElement('div');
      card.className = 'stu-card';
      card.dataset.key = s.key;
      const stars = s.rarity ? '★'.repeat(Math.min(3, parseInt(s.rarity)||1)) : '';
      const fallback = mediaUrl('01_学生/'+s.dir+'/立绘/头像.png');
      const skinTag = (s.skin && s.skin!=='原皮') ? `<span class="stu-skin">${esc(s.skin)}</span>` : '';
      card.innerHTML = `
        <div class="stu-avatar-wrap">
          ${favBtn('stu:'+s.dir)}
          <img class="stu-avatar" loading="lazy" decoding="async" src="${thumbUrl('students', s.dir+'/avatar')}" alt="${esc(s.cn)}"
               onload="this.classList.add('loaded')"
               onerror="if(this.dataset.a){this.onerror=null;this.src='assets/arona.png';this.classList.add('loaded')}else{this.dataset.a=1;this.src='${fallback}'}">
        </div>
        <div class="stu-body">
          <div class="stu-cn">${esc(s.cn)}${skinTag}</div>
          <div class="stu-jp">${esc(s.jp)}</div>
          <div class="stu-meta">${esc(s.school)}${stars?` · <span class="stu-stars">${stars}</span>`:''}</div>
        </div>`;
      card.onclick = ()=> location.hash = '#/student/'+card.dataset.key;
      return card;
    }, 60, ()=>{ jumpInput('f-jump', list, grid, raw=>list.findIndex(s=>String(s.id) === raw)); });
  }
  $('#f-q').oninput = debounce(apply, 220);
  $('#f-school').onchange = apply;
  $('#f-rarity').onchange = apply;
  apply();
}

let detailTab = 'info';

const PF_ATTR = { Piercing:'贯通', Mystic:'神秘', Explosive:'爆发', Vibration:'振动', Sonic:'音波' };
const PF_DEF  = { Light:'轻装甲', Heavy:'重装甲', Special:'特殊装甲', Elastic:'弹性装甲' };
const PF_TYPE = { Dealer:'输出', Healer:'治疗', Tanker:'防御', Support:'辅助', 'T.S':'战术支援' };
const PF_TEAM = { FRONT:'前排', MIDDLE:'中排', BACK:'后排' };
const PF_FIELD = { STRIKER:'突击', SPECIAL:'特殊' };
const PF_STATS = {
  max_hp:'生命', attack:'攻击', defense:'防御', healing:'治疗', accuracy:'命中', evasion:'闪避',
  crit:'暴击值', crit_res:'暴击抵抗', crit_dmg:'暴击伤害', crit_dmg_res:'暴伤抵抗',
  stability:'稳定', range:'射程', cc_power:'控制力', cc_res:'控制抵抗',
  recovery_boost:'回复率', mag_count:'弹匣'
};
const PF_STAT_NUM = { max_hp:1, attack:1, defense:1, healing:1, accuracy:1, evasion:1, crit:1, crit_res:1, stability:1, range:1, cc_power:1, cc_res:1, mag_count:1 };
function pfAttr(v){ return PF_ATTR[v] || v || ''; }
function pfRich(s){ return esc(String(s||'')).replace(/\r/g,'').replace(/\n/g,'<br>'); }
function pfSkillIcon(d, base, s){
  if(s.iconLocal) return mediaUrl(base + s.iconLocal);
  return s.icon || '';
}

function playStudentVoice(d, base){
  if(!window.Sfx || !d) return;
  const groups = d.voices || {};
  let pool = [];
  Object.keys(groups).forEach(s=>{
    (groups[s]||[]).forEach(v=>{ if(v && v.file) pool.push(v); });
  });
  if(!pool.length){
    const vs = d.variants || [];
    const orig = vs.find(v=>String(v.skin||'').indexOf('原皮') === 0);
    if(orig && orig.dir && orig.dir !== d.dir){
      const fb = '01_学生/' + orig.dir + '/';
      const key = orig.key || orig.id;
      fetch('/api/student?id=' + encodeURIComponent(key) + '&t=' + BUILD)
        .then(r => r.ok ? r.json() : null)
        .then(j => { if(j) pickAndPlayVoice(j.voices || {}, fb); })
        .catch(()=>{});
    }
    return;
  }
  pickAndPlayVoice(groups, base);
}

function pickAndPlayVoice(groups, base){
  let pool = [];
  Object.keys(groups||{}).forEach(s=>{
    (groups[s]||[]).forEach(v=>{ if(v && v.file) pool.push(v); });
  });
  if(!pool.length) return;

  const notLong = pool.filter(v=>!/exweapon|relationship|season|anniversary|memoriallobby/i.test(v.file||''));
  const shortPool = notLong.length >= 3 ? notLong : pool;

  const brief = shortPool.filter(v=>/战斗|编队|大厅|咖啡厅|登录|login|lobby|cafe|formation|battle|skill|tactic|title/i.test(v.file||''));
  const pickFrom = brief.length >= 3 ? brief : shortPool;

  const cands = [];
  for(let i=0;i<6 && pickFrom.length;i++){
    const v = pickFrom[Math.floor(Math.random()*pickFrom.length)];
    if(v && v.file) cands.push(mediaUrl(base + v.file));
  }
  if(!cands.length) return;
  Sfx.voice(cands);
}

async function renderStudent(id){
  app.innerHTML = '<div class="wrap"><div class="loading">正在加载学生档案……</div></div>';
  let d;
  try{
    const r = await fetch('/api/student?id='+encodeURIComponent(id)+'&t='+BUILD);
    if(!r.ok) throw 0;
    d = await r.json();
  }catch(e){ app.innerHTML = '<div class="wrap"><div class="empty"><div class="big">❓</div>未找到该学生</div></div>'; return; }

  const base = '01_学生/'+d.dir+'/';
  favRefresh = null;

  if(window.Sfx) playStudentVoice(d, base);
  const illusMain = (d.illus||[]).find(f=>f.includes('初始立绘')) || (d.illus||[])[0] || '';
  const prof = d.profile || null;
  const pv = k => (prof && prof[k]) || '';
  const atkAttr = d.attr || (prof && prof.combat && prof.combat.attackAttr) || '';
  const chips = [
    (pv('school')||d.school) && ('🏫 '+(pv('school')||d.school)),
    (pv('grade')||d.grade) && ('🎓 '+(pv('grade')||d.grade)),
    (pv('birthday')||d.birthday) && ('🎂 '+(pv('birthday')||d.birthday)),
    (pv('height')||d.height) && ('📏 '+(pv('height')||d.height)+'cm'),
    (pv('weaponType')||d.weapon) && ('🔫 '+(pv('weaponType')||d.weapon)),
    atkAttr && ('💥 '+pfAttr(atkAttr)),
    pv('hobby') && ('💗 '+pv('hobby')),
    (pv('illustrator')||d.illustrator) && ('🎨 画师 '+(pv('illustrator')||d.illustrator)),
    (pv('voice')||d.voice) && ('🎙️ 声优 '+(pv('voice')||d.voice))
  ].filter(Boolean);

  const tabs = [
    {k:'info', n:'📋 资料'},
    {k:'combat', n:'⚔️ 战斗'},
    {k:'illus', n:'🖼️ 立绘', n2:(d.illus||[]).length + ((d.skins||[]).length?(' (+'+d.skins.length+'皮肤)'):'')},
    {k:'gallery', n:'🎞️ 画廊', n2:((d.gallery||[]).reduce((a,g)=>a+g.files.length,0))},
    {k:'lobby', n:'🏛️ 记忆大厅'},
    {k:'voice', n:'🔊 语音', n2:Object.keys(d.voices||{}).length+'服'},
    {k:'skill', n:'⚡ 技能', n2:(prof&&prof.skills&&prof.skills.length)||(d.skills||[]).length}
  ];

  const variants = d.variants||[];
  const skinBar = variants.length>1 ? `
    <div class="skin-bar">
      <span class="skin-bar-label">👗 服装版本：</span>
      ${variants.map(v=>`<button class="skin-chip ${String(v.key||v.id)===String(d.curVariant)?'active':''}" data-vid="${esc(v.key||v.id)}">${esc(v.skin)}</button>`).join('')}
    </div>` : (d.curSkin&&d.curSkin!=='原皮'?`<div class="skin-bar"><span class="skin-bar-label">👗 当前服装：<b>${esc(d.curSkin)}</b></span></div>`:'');

  app.innerHTML = `
  <div class="wrap">
    <a class="back-link" href="#/students">← 返回学生图鉴</a>
    <div class="detail-hero">
      ${illusMain?`<img class="detail-bg" src="${mediaUrl(base+illusMain)}" alt="" onerror="this.style.display='none'">`:''}
      <div class="detail-info">
        <img class="detail-avatar" src="${mediaUrl(base+'立绘/头像.png')}" alt="" onerror="this.style.visibility='hidden'">
        <div>
          <div class="detail-name">${esc(d.cn)}${d.curSkin&&d.curSkin!=='原皮'?` <span class="cur-skin-tag">${esc(d.curSkin)}</span>`:''}${favBtn('stu:'+d.dir)}</div>
          <div class="detail-jp">${esc(d.jp)} · ID ${esc(d.id)}</div>
          <div class="detail-chips">${chips.map(c=>`<span class="chip">${esc(c)}</span>`).join('')}</div>
        </div>
      </div>
    </div>
    ${skinBar}
    <div class="more-bar">
      <a class="more-btn" href="/web/modelview/index.html?kw=${encodeURIComponent(d.cn||'')}&from=${encodeURIComponent('/web/index.html' + location.hash)}">🧩 3D 模型库</a>
      <span class="more-tip">GLB 骨骼 / 动画 / 贴图内嵌，离线本地加载（SD 小人）</span>
    </div>
    <div class="tabs">
      ${tabs.map(t=>`<button class="tab ${detailTab===t.k?'active':''}" data-tab="${t.k}">${t.n}${t.n2?` <small>(${t.n2})</small>`:''}</button>`).join('')}
    </div>
    <div class="tab-panel" id="tab-panel"></div>
  </div>`;

  document.querySelectorAll('.skin-chip').forEach(b=>b.onclick=()=>{
    if(b.dataset.vid === String(d.curVariant)) return;
    location.hash = '#/student/'+b.dataset.vid;
  });
  document.querySelectorAll('.tab').forEach(b=>b.onclick=()=>{
    if(b.dataset.tab === detailTab) return;
    detailTab = b.dataset.tab;
    if(detailTab !== 'lobby' && window.LobbyAudio) LobbyAudio.pause();

    if(detailTab !== 'lobby' && window.__setRenderScale) window.__setRenderScale(1);
    document.querySelectorAll('.tab').forEach(x=>x.classList.toggle('active', x.dataset.tab===detailTab));
    switchFx($('#tab-panel'), ()=>renderStudentTab(d, base));
  });
  renderStudentTab(d, base);
}

function lightboxOpen(list, idx){
  const lb = $('#lightbox');
  let cur = idx;
  function show(){
    const item = list[cur];
    const img = $('#lb-img');
    const origSrc = mediaUrl(item.full || item);
    const onOrigFail = ()=>{ showMsg('这张图片还没有下载到本地（<b>'+esc(item.name||'')+'</b>），<br>离线版仅能打开已收录的文件。','图片未收录'); };
    img.dataset.rel = item.full || item;
    $('#lb-name').textContent = item.name || (typeof item==='string'?item.split('/').pop():'');
    refreshLbRefs(item.full || (typeof item==='string'? item : ''), item);
    hideCellActions($('#lightbox'));
    if(SETTINGS.fast_view && item.full){

      img.onerror = ()=>{ img.onerror = onOrigFail; img.onload = null; img.src = origSrc; };
      img.onload = ()=>{ img.onerror = onOrigFail; img.onload = null; img.src = origSrc; };
      img.src = thumbUrl('fast', item.full);
    }else{
      img.onerror = onOrigFail;
      img.onload = ()=>{ img.onerror = null; };
      img.src = origSrc;
    }
  }
  $('#lb-close').onclick = ()=>hideFx(lb);

  $('#lb-prev').onclick = ()=>{ cur=(cur-1+list.length)%list.length; show(); swapFx($('#lb-img')); swapFx($('#lb-name')); };
  $('#lb-next').onclick = ()=>{ cur=(cur+1)%list.length; show(); swapFx($('#lb-img')); swapFx($('#lb-name')); };
  lb.onclick = e=>{ if(e.target===lb) hideFx(lb); };
  document.onkeydown = e=>{
    if(lb.classList.contains('hidden')) return;
    if(e.key==='Escape') hideFx(lb);
    if(e.key==='ArrowLeft'){ cur=(cur-1+list.length)%list.length; show(); }
    if(e.key==='ArrowRight'){ cur=(cur+1)%list.length; show(); }
  };
  showFx(lb);
  show();
}

function renderStudentTab(d, base){
  const panel = $('#tab-panel');
  if(detailTab==='info'){
    const p = d.profile || null;
    const atkAttr = d.attr || (p && p.combat && p.combat.attackAttr) || '';
    const rows = p ? [
      ['学校', p.school||d.school], ['年级', p.grade||d.grade], ['年龄', p.age],
      ['生日', p.birthday||d.birthday], ['身高', (p.height||d.height)?((p.height||d.height)+' cm'):''],
      ['爱好', p.hobby], ['武器类型', p.weaponType||d.weapon],
      ['攻击属性', atkAttr?pfAttr(atkAttr):''], ['稀有度', d.rarity?'★'.repeat(parseInt(d.rarity)||1):''],
      ['画师', p.illustrator||d.illustrator], ['设计', p.designer],
      ['声优', p.voice||d.voice], ['国服配音', p.voiceCn],
      ['日服实装', p.release], ['国服实装', p.releaseCn], ['国际服实装', p.releaseGlobal]
    ].filter(x=>x[1]) : [
      ['学校', d.school], ['年级', d.grade], ['生日', d.birthday], ['身高', d.height?d.height+' cm':''],
      ['武器类型', d.weapon], ['攻击属性', atkAttr?pfAttr(atkAttr):''], ['战队定位', d.pos],
      ['稀有度', d.rarity?'★'.repeat(parseInt(d.rarity)||1):''],
      ['画师', d.illustrator], ['声优', d.voice]
    ].filter(x=>x[1]);

    const alias = p ? [
      p.nameJp && ['日文名', p.nameJp],
      p.nameEn && ['英文名', p.nameEn],
      p.nameTw && ['繁中名', p.nameTw],
      p.nick && ['昵称', p.nick]
    ].filter(Boolean) : [];

    const intro = p && (p.introCn || p.intro);
    panel.innerHTML = `
      <div class="pf-block">
        <h4>📋 基本资料</h4>
        <div class="info-grid">${rows.map(([k,v])=>`<div class="info-item"><div class="k">${k}</div><div class="v">${esc(v)}</div></div>`).join('')}</div>
        ${alias.length?`<div class="info-grid" style="margin-top:12px">${alias.map(([k,v])=>`<div class="info-item"><div class="k">${k}</div><div class="v">${esc(v)}</div></div>`).join('')}</div>`:''}
      </div>
      ${p && p.signature ? `<div class="pf-block"><h4>💬 MomoTalk 签名</h4><div class="pf-text">${pfRich(p.signature)}</div></div>` : ''}
      <div class="pf-block"><h4>📖 简介</h4>
        ${intro ? `<div class="pf-text">${pfRich(intro)}</div>`
                : `<div class="empty" style="border:none">本地暂无该角色的完整档案与简介。${p?'':'<br><b>可到「在线更新 → 学生资料全量同步」补充。</b>'}</div>`}
      </div>
      ${p && p.updatedAt ? `<div class="pf-src">档案来源：kivo.wiki · 同步于 ${esc(p.updatedAt)}</div>` : ''}`;
  }
  else if(detailTab==='combat'){
    const p = d.profile || null;
    const c = (p && p.combat) || null;
    const st = (p && p.stats) || null;
    if(!c || !Object.keys(c).length){
      panel.innerHTML = '<div class="empty"><div class="big">⚔️</div>暂无战斗档案，请先到「在线更新 → 学生资料全量同步」同步数据。</div>';
      return;
    }
    const rows = [
      ['定位类型', PF_TYPE[c.type]||c.type], ['攻击属性', pfAttr(c.attackAttr)],
      ['防御属性', PF_DEF[c.defenseAttr]||c.defenseAttr],
      ['队伍站位', PF_TEAM[c.teamPos]||c.teamPos], ['战场位置', PF_FIELD[c.fieldPos]||c.fieldPos],
      ['稀有度', c.rarity?('★'.repeat(Math.min(3,parseInt(c.rarity)||1))):''], ['获取方式', c.limited?'限定':'常驻'],
      ['群体控制', c.crowdControl?'有':'无']
    ].filter(x=>x[1]);
    const terrains = [['室外', c.outdoor], ['室内', c.indoor], ['街道', c.street]].filter(x=>x[1]);
    const statKeys = Object.keys(PF_STATS).filter(k=>st && st[k]!==undefined && st[k]!==null && st[k]!=='');
    const eqs = (c.equipment||[]).filter(Boolean);
    const w = (p && p.weapon) || null;
    panel.innerHTML = `
      <div class="pf-block"><h4>⚔️ 战斗定位</h4>
        <div class="info-grid">${rows.map(([k,v])=>`<div class="info-item"><div class="k">${k}</div><div class="v">${esc(v)}</div></div>`).join('')}</div>
      </div>
      ${terrains.length?`<div class="pf-block"><h4>🌤️ 地形适性</h4>
        <div class="terrain-row">${terrains.map(([k,v])=>`<span class="terrain-chip t-${esc(String(v).toLowerCase())}">${k} <b>${esc(v)}</b></span>`).join('')}</div>
      </div>`:''}
      ${statKeys.length?`<div class="pf-block"><h4>📊 基础数值</h4>
        <div class="stat-grid">${statKeys.map(k=>`<div class="stat-cell"><span>${PF_STATS[k]}</span><b>${esc(st[k])}</b></div>`).join('')}</div></div>`:''}
      ${(w && (w.name||w.nameCn))?`<div class="pf-block"><h4>🔫 专属武器</h4>
        <div class="weapon-card">
          ${w.icon?`<img src="${esc(w.icon)}" alt="" onerror="this.style.display='none'">`:''}
          <div class="weapon-body">
            <div class="weapon-name">${esc(w.nameCn||w.name)}${(w.nameCn&&w.name&&w.nameCn!==w.name)?` <small>${esc(w.name)}</small>`:''}</div>
            <div class="pf-text">${pfRich(w.descCn||w.desc)}</div>
          </div>
        </div></div>`:''}
      ${eqs.length?`<div class="pf-block"><h4>🎒 装备（点击查看升级）</h4>
        <div class="equip-row">${eqs.map(e=>{
          const o = (typeof e === 'string') ? {name:e} : e;
          return `<button class="equip-card" data-eq="${esc(o.id||'')}" data-name="${esc(o.name||'')}">
            ${o.iconLocal?`<img src="${mediaUrl('09_物品图标/'+o.iconLocal)}" alt="" loading="lazy" onerror="this.style.display='none'">`:'<span class="equip-ph">🎒</span>'}
            <span>${esc(o.name||'装备')}</span>${o.favorite?'<i class="eq-fav">★</i>':''}
          </button>`;
        }).join('')}</div>
        <div id="eq-detail" class="eq-detail hidden"></div>
      </div>`:''}
      ${(p.gifts||[]).length?`<div class="pf-block"><h4>🎁 喜好礼物</h4>
        <div class="gift-row">${(p.gifts||[]).map(g=>`<div class="gift-chip">
          ${g.iconLocal?`<img src="${mediaUrl('09_物品图标/'+g.iconLocal)}" alt="" loading="lazy" onerror="this.style.display='none'">`:'<span class="gift-ph">🎁</span>'}
          <div class="gift-body"><div>${esc(g.name||('礼物 #'+g.id))}</div>${g.favor?`<small>好感 +${esc(g.favor)}</small>`:''}</div>
        </div>`).join('')}</div></div>`:''}`;
    panel.querySelectorAll('.equip-card').forEach(btn=>{
      btn.onclick = async ()=>{
        const id = btn.dataset.eq;
        const box = $('#eq-detail');
        if(!box) return;
        if(!id){ toast('该装备暂无升级数据'); return; }
        box.classList.remove('hidden');
        box.innerHTML = '<div class="eq-loading">加载升级数据…</div>';
        try{
          const r = await fetch('/api/equipment?id=' + encodeURIComponent(id)).then(r=>r.json());
          if(!r.ok || !r.data){ box.innerHTML = '<div class="empty" style="border:none">未找到该装备</div>'; return; }
          const d = r.data;
          box.innerHTML = `<div class="eq-head"><b>${esc(d.name)}</b><span>${esc(d.nameJp||'')}</span><button class="eq-close">收起 ✕</button></div>
            <div class="eq-tiers">${(d.tiers||[]).map(t=>`<div class="eq-tier">
              ${t.iconLocal?`<img src="${mediaUrl('09_物品图标/'+t.iconLocal)}" alt="" loading="lazy" onerror="this.style.display='none'">`:''}
              <div class="eq-tier-body"><div class="eq-tier-title">T${esc(t.tier)} · ${esc(t.title)}</div><div class="eq-tier-desc">${pfRich(t.desc)}</div></div>
            </div>`).join('')}</div>`;
          box.querySelector('.eq-close').onclick = ()=>box.classList.add('hidden');
          box.scrollIntoView({behavior:'smooth', block:'nearest'});
        }catch(e){ box.innerHTML = '<div class="empty" style="border:none">加载失败：'+esc(e.message)+'</div>'; }
      };
    });
  }
  else if(detailTab==='illus'){
    const groups = [{name:'立绘', files:(d.illus||[]).map(f=>({full:base+f, name:f.split('/').pop()}))}];
    (d.skins||[]).forEach(s=>groups.push({name:'皮肤 · '+s.name, files:s.files.map(f=>({full:base+f, name:f.split('/').pop()}))}));
    panel.innerHTML = groups.map((g,i)=>`
      <div class="voice-group"><h4>${esc(g.name)}（${g.files.length}）</h4>
        <div class="img-grid" data-g="${i}"></div></div>`).join('');
    groups.forEach((g,i)=>{
      const host = panel.querySelector(`.img-grid[data-g="${i}"]`);
      paginated(host, g.files, (f,idx)=>{
        const cell = document.createElement('div');
        cell.className = 'img-cell';

        cell.innerHTML = `${thumbImg('students', f.full, f.full)}<div class="cap">${esc(f.name)}</div>`;
        cell.onclick = ()=>lightboxOpen(g.files, idx);
        return cell;
      }, 40);
    });
  }
  else if(detailTab==='gallery'){
    const groups = (d.gallery||[]).map(g=>({name:g.name, files:g.files.map(f=>({full:base+f, name:f.split('/').pop()}))}));
    if(!groups.length){ panel.innerHTML = '<div class="empty">该学生暂无本地画廊</div>'; return; }
    panel.innerHTML = groups.map((g,i)=>`
      <div class="voice-group"><h4>${esc(g.name)}（${g.files.length}）</h4>
        <div class="img-grid" data-g="${i}"></div></div>`).join('');
    groups.forEach((g,i)=>{
      const host = panel.querySelector(`.img-grid[data-g="${i}"]`);
      paginated(host, g.files, (f,idx)=>{
        const cell = document.createElement('div');
        cell.className = 'img-cell';

        cell.innerHTML = `${thumbImg('students', f.full, f.full)}<div class="cap">${esc(f.name)}</div>`;
        cell.onclick = ()=>lightboxOpen(g.files, idx);
        return cell;
      }, 60);
    });
  }
  else if(detailTab==='voice'){
    const servers = Object.keys(d.voices||{});
    if(!servers.length){ panel.innerHTML = '<div class="empty">该学生暂无本地语音</div>'; return; }
    panel.innerHTML = `<div class="sub-tabs">${servers.map((s,i)=>`<button class="sub-tab ${i===0?'active':''}" data-s="${esc(s)}">${esc(s)}（${d.voices[s].length}）</button>`).join('')}</div><div id="voice-host"></div>`;
    function showServer(s){
      const host = $('#voice-host');
      host.innerHTML = '';
      paginated(host, d.voices[s], v=>{
        const row = document.createElement('div');
        row.className = 'voice-item';
        const vd = voiceDisplay(v.name || v.file);
        row.innerHTML = `<span class="vn">${esc(vd.cn)}<small class="vn-orig">${esc(vd.orig)}</small></span><audio controls preload="none" src="${mediaUrl(base+v.file)}" onerror="showMsg('这条语音还没有下载到本地（<b>${esc(vd.cn)}</b>）。','语音未收录')"></audio>`;
        return row;
      }, 40);
    }
    panel.querySelectorAll('.sub-tab').forEach(b=>b.onclick=()=>{
      if(b.classList.contains('active')) return;
      panel.querySelectorAll('.sub-tab').forEach(x=>x.classList.remove('active'));
      b.classList.add('active');

      switchFx($('#voice-host'), ()=>showServer(b.dataset.s));
    });
    showServer(servers[0]);
  }
  else if(detailTab==='skill'){
    const p = d.profile || null;
    const pskills = (p && p.skills) || [];
    if(!pskills.length){

      if(!(d.skills||[]).length){ panel.innerHTML = '<div class="empty">该学生暂无本地技能数据</div>'; return; }
      panel.innerHTML = '<div class="img-grid" id="skill-grid"></div>';
      const host = $('#skill-grid');
      paginated(host, d.skills, (f)=>{
        const cell = document.createElement('div');
        cell.className = 'img-cell';
        cell.style.maxWidth = '140px';
        cell.innerHTML = `${thumbImg('students', base+f, base+f)}<div class="cap">${esc(f.split('/').pop())}</div>`;
        cell.onclick = ()=>lightboxOpen(d.skills.map(x=>({full:base+x, name:x.split('/').pop()})), d.skills.indexOf(f));
        return cell;
      }, 60);
      return;
    }
    panel.innerHTML = `<div class="skill-list">${pskills.map((s,i)=>{
      const icon = pfSkillIcon(d, base, s);
      const name = esc(s.nameCn||s.name||'未命名技能');
      const sub = (s.name && s.nameCn && s.name!==s.nameCn) ? ` <small>${esc(s.name)}</small>` : '';
      const lv1 = (s.levels&&s.levels[0]&&s.levels[0].desc)||'';
      const hasLv = s.levels && s.levels.length>1;
      return `<div class="skill-card" data-i="${i}">
        <div class="skill-head">
          ${icon?`<img class="skill-ico" src="${esc(icon)}" alt="" loading="lazy" onerror="this.style.display='none'">`:''}
          <div class="skill-main">
            <div class="skill-kind k-${esc(s.kind||'')}">${esc(s.kindName||'技能')}</div>
            <div class="skill-name">${name}${sub}</div>
            <div class="skill-meta">${s.cost?('COST '+esc(s.cost)):''}${s.maxLevel?(' · 最高 '+esc(s.maxLevel)+' 级'):''}</div>
            <div class="skill-desc">${pfRich(lv1)}</div>
          </div>
          ${hasLv?`<button class="skill-toggle">等级表 ▾</button>`:''}
        </div>
        ${hasLv?`<div class="skill-levels hidden">${(s.levels||[]).map(l=>`<div class="skill-lv"><span class="lv">Lv.${esc(l.lv)}</span><span class="lv-cost">${l.cost?('COST '+esc(l.cost)):''}</span><span class="lv-desc">${pfRich(l.desc)}</span></div>`).join('')}</div>`:''}
      </div>`;
    }).join('')}</div>`;
    panel.querySelectorAll('.skill-toggle').forEach(btn=>{
      btn.onclick = ()=>{
        const box = btn.closest('.skill-card').querySelector('.skill-levels');
        const hidden = box.classList.toggle('hidden');
        btn.textContent = hidden ? '等级表 ▾' : '收起等级表 ▴';
      };
    });
  }
  else if(detailTab==='lobby'){
    renderLobby(d, base, panel);
  }
}

let galState = {cat:0, sub:0, fav:false};
try { Object.assign(galState, JSON.parse(localStorage.getItem('ba.gal.v1') || '{}')); } catch(e) {}
function galSave(){ try { localStorage.setItem('ba.gal.v1', JSON.stringify(galState)); } catch(e) {} }

function relParts(rel){
  const p = String(rel||'').split('/');
  const file = p.pop() || '';
  const sub  = p.pop() || '';
  const catRaw = p.pop() || '';
  let cat = catRaw.replace(/^\d+_/, '').replace(/^[A-Z]\d+(?:\.\d+)*\./, '').replace(/^_+/, '');
  if(!cat) cat = catRaw;
  return { cat, sub, file, digits: (file.match(/\d+/) || [''])[0] };
}

function relLabel(rel, ref){
  const { cat, sub, file, digits } = relParts(rel);
  const n = (ref && ref.n) || digits;
  const bits = [];
  if(cat && cat !== sub) bits.push(cat);
  if(sub && sub !== 'Default') bits.push(sub);
  if(n) bits.push('第 ' + n + ' 张');
  return bits.join(' · ') || file;
}
let galNames = null;
let galRefs = null, galRefsPromise = null;
function galRef(rel){ return (galRefs && galRefs[rel]) || null; }
async function loadGalNames(){
  if(galNames) return galNames;
  galNames = {};
  try{
    const r = await fetch('assets/gallery-names.json?v=20261015s');
    if(r.ok){ const j = await r.json(); if(j && typeof j === 'object') galNames = j; }
  }catch(e){}
  return galNames;
}

function ensureRefs(){
  if(galRefs) return Promise.resolve(galRefs);
  if(galRefsPromise) return galRefsPromise;
  galRefsPromise = fetch('assets/gallery-refs.json?v=20261015s')
    .then(r => r.ok ? r.json() : {})
    .then(j => { galRefs = (j && typeof j === 'object') ? j : {}; return galRefs; })
    .catch(()=>{ galRefs = {}; return galRefs; });
  return galRefsPromise;
}
function galleryName(rel){ return (galNames && galNames[rel]) || relLabel(rel, galRef(rel)); }

let lbRefTok = 0;
function fillLbRefs(rel, item){
  const box = $('#lb-refs');
  if(!box) return;
  if(!rel){ box.innerHTML = ''; return; }
  const ref = galRef(rel);
  const nm = galNames && galNames[rel];
  const rows = [];

  rows.push(`<div class="lbr-line"><span class="lbr-k">📍 出处</span><span class="lbr-v">${esc(relLabel(rel, ref))}</span></div>`);
  if(nm) rows.push(`<div class="lbr-line"><span class="lbr-k">🏷 名称</span><span class="lbr-v">${esc(nm)}</span></div>`);
  if(ref && ref.t) rows.push(`<div class="lbr-line"><span class="lbr-k">📝 说明</span><span class="lbr-v">${esc(ref.t)}</span></div>`);
  if(ref && ref.src) rows.push(`<div class="lbr-line"><span class="lbr-k">🔗 原始</span><span class="lbr-v"><a href="${esc(ref.src)}" target="_blank" rel="noopener">${esc(ref.src.replace(/^https?:\/\//,''))} ↗</a></span></div>`);
  if(ref && ref.also && ref.also.length){
    const items = ref.also.map(a=>`<li>${esc(relLabel(a, galRef(a)))}</li>`).join('');
    rows.push(`<div class="lbr-line lbr-also"><span class="lbr-k">🧩 同图另见</span><span class="lbr-v"><ul>${items}</ul></span></div>`);
  }else if(!galRefs){
    rows.push(`<div class="lbr-line"><span class="lbr-k">🧩 同图另见</span><span class="lbr-v lbr-dim">查询中…</span></div>`);
  }
  box.innerHTML = rows.join('');
}
function refreshLbRefs(rel, item){
  const tok = ++lbRefTok;
  fillLbRefs(rel, item);
  ensureRefs().then(()=>{ if(tok === lbRefTok) fillLbRefs(rel, item); });
}
async function renderGallery(){
  const data = await api('gallery');
  await loadGalNames();
  if(!data.cats || !data.cats.length){ app.innerHTML = '<div class="wrap"><div class="empty">暂无画廊内容</div></div>'; return; }
  if(galState.cat >= data.cats.length) galState.cat = 0;
  const gc0 = data.cats[galState.cat];
  if(gc0 && gc0.subs && galState.sub >= gc0.subs.length) galState.sub = 0;
  app.innerHTML = `
  <div class="wrap">
    <div class="page-title">🖼️ CG画廊</div>
    <div class="page-sub">重现你的回忆 · 主线 CG / 活动图像 / 设定素材 / PV 截图</div>
    <div class="gallery-layout">
      <div class="gallery-tree" id="gal-tree"></div>
      <div>
        <div class="filter-bar">
          <input type="text" id="gal-q" placeholder="🔍 搜索图片文件名">
          <span class="sp"></span>
          <input type="text" id="gal-jump" class="jump-box" placeholder="📍 定位：第几张" title="输入 1 ~ 当前分类张数 的序号后回车，会自动展开「加载更多」并滚到那张图">
          <span id="gal-count" style="font-size:13px;color:var(--text-sub)"></span>
        </div>
        <div class="sub-tabs" id="gal-subs"></div>
        <div class="img-grid" id="gal-grid"></div>
      </div>
    </div>
  </div>`;

  const tree = $('#gal-tree');
  tree.innerHTML = '<div class="g-tree-title">📁 画廊分类</div>' + data.cats.map((c,i)=>
    `<div class="g-tree-item ${i===galState.cat?'active':''}" data-c="${i}" title="${esc(c.name)}">${esc(c.name)}</div>`
  ).join('');

  function renderSubs(){
    const cat = data.cats[galState.cat];
    const subsEl = $('#gal-subs');
    const grid = $('#gal-grid');
    if(!cat){ subsEl.innerHTML=''; grid.innerHTML=''; return; }
    subsEl.innerHTML = favTabBtn('img', galState.fav) + cat.subs.map((s,i)=>`
      <button class="sub-tab ${(!galState.fav && i===galState.sub)?'active':''}" data-i="${i}" title="${esc(s.name)}">${esc(s.name)} <small>${s.files.length}</small></button>
    `).join('');
    subsEl.querySelectorAll('.sub-tab').forEach(b=>b.onclick=()=>{
      if(b.dataset.favTab){ galState.fav = true; }
      else { galState.fav = false; galState.sub = parseInt(b.dataset.i); }
      galSave();
      renderSubs();
    });
    showImages();
  }
  function showImages(){
    const cat = data.cats[galState.cat];
    const grid = $('#gal-grid');
    if(!cat){ grid.innerHTML=''; return; }
    const q = ($('#gal-q').value||'').trim().toLowerCase();
    let files;
    if(galState.fav){

      files = cat.subs.reduce((a,s)=>a.concat(s.files.filter(f=>FAV.has('img:'+f))), []);
      if(q) files = files.filter(f=>f.toLowerCase().includes(q));
      $('#gal-count').textContent = '我喜欢的 ' + files.length + ' 张';
    }else{
      const sub = cat.subs[galState.sub];
      if(!sub){ grid.innerHTML='<div class="empty">暂无内容</div>'; return; }
      files = q ? sub.files.filter(f=>f.toLowerCase().includes(q)) : sub.files;
      $('#gal-count').textContent = files.length + ' / ' + sub.files.length + ' 张';
    }

    favRefresh = galState.fav ? showImages : null;
    if(!files.length){
      switchFx(grid, ()=>{ grid.innerHTML = galState.fav ? favEmpty() : '<div class="empty">没有匹配的图片</div>'; });
      return;
    }
    const items = files.map(f=>({full:f, name:galleryName(f)}));

    renderList(grid, items, (it, idx)=>{
      const cell = document.createElement('div');
      cell.className = 'img-cell';
      cell.innerHTML = `${thumbImg('gallery', it.full, it.full)}${favBtn('img:'+it.full)}<div class="cap">${esc(it.name)}</div>`;
      cell.onclick = ()=>lightboxOpen(items, items.indexOf(it));
      return cell;
    }, 80, ()=>{ jumpInput('gal-jump', items, grid, null); });
  }
  tree.querySelectorAll('.g-tree-item').forEach(el=>el.onclick=()=>{
    galState.cat = parseInt(el.dataset.c); galState.sub = 0; galState.fav = false;
    galSave();
    tree.querySelectorAll('.g-tree-item').forEach(x=>x.classList.remove('active'));
    el.classList.add('active'); renderSubs();
  });
  $('#gal-q').oninput = debounce(showImages, 220);
  renderSubs();
}

const player = { list: [], idx: -1, audio: $('#audio'), errGuard: false };

function trackCover(t){
  if(t.coverUrl) return t.coverUrl;
  if(t.cover) return thumbUrl('music', t.dir+'/cover');
  return '';
}

const VOICE_CN = {
  title:'启动', login:'登录', lobby:'大厅', memoriallobby:'纪念大厅', oriallobby:'原设大厅',
  eventlobby:'活动大厅', minieventlobby:'活动大厅', raidlobby:'总力战大厅', uibattlepasslobby:'战令大厅',
  cafe:'咖啡厅', monolog:'独白', act:'互动',
  battle:'战斗', tactic:'战术', formation:'编队',
  exskill:'EX技能', commonskill:'普通技能', skill:'技能',
  growup:'成长', gachaget:'招募获得', relationship:'羁绊', up:'提升', exweapon:'专属武器',
  buffed:'获得增益', buffself:'自我强化', covered:'掩护队友', defense:'防御', move:'移动',
  recovery:'恢复', retire:'撤退', damage:'受击', shout:'呐喊', victory:'胜利', defeat:'失败',
  in:'出击', select:'选中', get:'获得', level:'升级',
  season:'节日问候', newyear:'新年', xmas:'圣诞节', halloween:'万圣节', birthday:'生日',
  chinesenewyear:'春节', midautumn:'中秋节', player:'老师', swimsuit:'泳装',
  versus:'战术大赛', raid:'总力战', worldraid:'世界总力战', worldraidmap:'世界总力战地图',
  minigame:'迷你游戏', minievent:'迷你活动', event:'活动',
  work:'勤务', shop:'商店', mission:'任务', operation:'作战', omikuji:'御神签', omikujipickup:'御神签抽签',
  weekdungeon:'悬赏通缉', schooldungeon:'学园交流会', dungeon:'地下城',
  consumable:'消耗道具', pilots:'机甲驾驶员', quick:'快捷', commands:'通讯指令',
  battlepass:'战令', uibattlepass:'战令',
  tileclear:'区域压制', tileerosion:'区域侵蚀', tileoccupy:'区域占领', tileupgrade:'区域升级',
  tilereward:'区域奖励', zoneopen:'区域开放', stageopen:'关卡开放',
  multifloorraid:'多重楼层挑战', floor:'楼层', idle:'待机', enter:'进入',
  mainscenario:'主线剧情', formchange:'形态切换', phase:'阶段', warning:'警告', win:'获胜',
  location:'场景', talk:'对话', sit:'就座', sleep:'休息', watch:'观景', umbrella:'撑伞',
  watering:'浇水', watchpot:'照看花盆', cabinet:'橱柜', exit:'离开',
  aronasleeppeek:'偷看阿洛娜', aronasleepsit:'阿洛娜睡颜',
  planasitpeek:'偷看普拉娜', planawatchsky:'普拉娜观星',
  minieventboxshop:'活动宝箱商店', minieventcardpickup:'活动卡片抽取', minieventshop:'活动商店',
  minieventlogin:'活动登录', findtreasure:'寻找宝藏', gettreasure:'获得宝藏', roundrenewal:'回合刷新',
  spbuy:'特殊购买', buy:'购买', box:'宝箱', daycomplete:'每日完成', dayend:'每日结束',
  dailyend:'每日结束', complete:'完成', fail:'失败', failed:'失败', success:'成功',
  drawturn:'抽牌', losebattle:'战斗失败', winturn:'回合胜利', loseturn:'回合失败',
  winturnspecial:'特殊回合胜利', loseturnspecial:'特殊回合失败', map:'地图',
  start:'开始', touch:'触碰', train:'训练', awesome:'喝彩',
  winbattle:'战斗胜利', hit:'命中', airborne:'空降', atpos:'就位', destroy:'击破',
  engage:'交战', land:'着陆', ready:'准备完毕', tangodown:'目标击破', attack:'攻击',
  aye:'收到', back:'撤退', caramba:'糟糕', enemy:'敌方', ally:'己方', base:'基地', building:'建筑',
  planes:'飞机', point:'据点', ship:'舰船', game:'比赛', luck:'祝好运',
  need:'请求', aa:'防空', scouting:'侦察', smoke:'烟雾', support:'支援', way:'执行',
  thank:'感谢', you:'你', destroyed:'被击破',
  laser:'激光', laserbeam:'激光束', beam:'光束', roar:'咆哮', walk:'行走',
  explosion:'爆炸', splash:'溅射', op:'开战', ex:'EX技能',
  roll:'滚动', small:'小型', retreat:'撤退', summon:'召唤', stop:'停止', launch:'发射',
  nurse:'护士装'
};
function voiceDisplay(fileOrName){
  let raw = String(fileOrName||'').split('/').pop();
  raw = raw.replace(/\.(ogg|mp3|m4a|wav|flac)$/i,'').replace(/\.ogg$/i,'');
  let tag = '';
  const tm = raw.match(/^(.*?)[（(]([^）)]*)[）)]\s*$/);
  if(tm){ raw = tm[1]; tag = tm[2]; }
  let toks = raw.split(/[_\s]+/).filter(Boolean);
  while(toks.length && /^\d+$/.test(toks[0])) toks.shift();
  const isCode = t => /^(s\d+|r\d+|np\d+|[a-z]+\d*)$/i.test(t) && !(t.toLowerCase() in VOICE_CN);
  while(toks.length && isCode(toks[0])) toks.shift();
  const orig = toks.join(' ').replace(/\s+/g,' ').trim();
  let cn = '';
  toks.forEach(t=>{
    const k = t.toLowerCase();
    let out = /^\d+$/.test(t) ? t : (VOICE_CN[k] || t);
    if(!cn){ cn = out; return; }
    if(/^\d+$/.test(out)){ cn += ' '+out; return; }
    if(/[一-鿿]/.test(out) && /[一-鿿]$/.test(cn)) cn += out;
    else cn += ' '+out;
  });
  if(tag) cn += '（'+tag+'）';
  return {cn: cn || raw, orig: orig || raw};
}

function startPlay(list, i){
  if(!list || !list.length) return;
  if(window.LobbyAudio) LobbyAudio.pause();
  player.list = list; player.idx = i;
  const t = list[i];
  if(!t || !t.audio){ showMsg('这份音频还没有下载到本地，暂时无法播放。<br>离线版仅能播放<b>已收录</b>的内容。','音频未收录'); return; }

  window.__baNowPlaying = { key: 'mus:' + t.dir, dir: t.dir, name: t.name, artist: t.artist || '' };

  const vv = $('#vm-video');
  if(vv){ vv.pause(); }
  hideFx($('#video-modal'));
  player.errGuard = false;
  const au = player.audio, url = mediaUrl(t.audio);
  if (window.AudioLab) AudioLab.attachMedia(au);
  cancelScrub();

  if(au.getAttribute('src') !== url) au.src = url;
  au.play().catch(err=>{ if(err && err.name !== 'AbortError') console.warn('[player] 播放失败', err.name, t.name); });

  if(window.AudioLab && AudioLab.routeTrack) AudioLab.routeTrack(url, t, au);
  $('#pl-title').textContent = t.name;
  $('#pl-artist').textContent = t.artist || 'Blue Archive';
  const cv = trackCover(t);
  const pc = document.getElementById('pl-cover');
  if(cv){

    pc.style.visibility = '';
    pc.onload = ()=>{ pc.onerror = null; };
    pc.onerror = ()=>{ pc.style.visibility = 'hidden'; pc.removeAttribute('src'); };

    if(pc.getAttribute('src') !== cv) pc.src = cv;
  }else{
    pc.style.visibility = 'hidden';
    pc.removeAttribute('src');
  }
  $('#pl-play').textContent = '⏸';
  document.querySelectorAll('.music-row.playing').forEach(r=>r.classList.remove('playing'));
  const row = document.querySelector('.music-row[data-pidx="'+i+'"]');
  if(row) row.classList.add('playing');

  swapFx(document.querySelector('.pl-cover-wrap'));
  swapFx(document.getElementById('pl-title'));
  swapFx(document.getElementById('pl-artist'));
  if(row){ row.classList.remove('play-hit'); void row.offsetWidth; row.classList.add('play-hit'); }
  syncLosslessButtons();
}

function syncLosslessButtons(){
  const box = $('#imp-lossless');
  if(!box) return;
  const sameList = !!(player.list && player.list.length && player.list[0] && player.list[0].kind === 'lossless');
  box.querySelectorAll('[data-lplay]').forEach(b => {
    const on = sameList && +b.dataset.lplay === player.idx;
    b.textContent = on ? '⏸ 播放中' : '▶ 播放';
    b.classList.toggle('on', on);
  });
}

let musTab = 'ost';
async function renderMusic(){
  $('#player').classList.remove('hidden');
  if(window.AudioLab) AudioLab.init();
  const data = await api('music');
  app.innerHTML = `
  <div class="wrap">
    <div class="page-title" style="display:flex;align-items:center;gap:12px;flex-wrap:wrap">🎵 留声机
      <button type="button" onclick="AudioLab.open()"
        style="margin-left:auto;padding:8px 18px;border-radius:999px;border:1px solid rgba(31,111,235,.45);background:rgba(31,111,235,.10);color:#1f6feb;font-weight:700;font-size:13px;cursor:pointer">
        🎚 高级音频
      </button>
    </div>
    <div class="page-sub">游戏内 OST 与主题曲 MV · 按编号编排 · 点击播放，自动连播 · 另有「角色与剧情音乐」</div>
    <div class="sub-tabs" id="mus-tabs">
      <button class="sub-tab" data-tab="ost">🎵 OST</button>
      <button class="sub-tab" data-tab="mv">🎞 主题曲 MV</button>
      <button class="sub-tab" data-tab="voice">🎵 角色与剧情音乐</button>
      ${favTabBtn('mus', musTab==='fav')}
      <button class="sub-tab" data-tab="import">📥 本地导入</button>
    </div>
    <div class="filter-bar" id="mus-filter">
      <input type="text" id="mus-q" placeholder="🔍 搜索曲名 / 编号 / 用途 / 作曲">
      <span class="sp"></span>
      <input type="text" id="mus-jump" class="jump-box" placeholder="📍 定位：序号 / 曲目编号" title="输入序号或曲目编号后回车，会自动展开「加载更多」并滚到那一首">
      <span id="mus-count" style="font-size:13px;color:var(--text-sub)"></span>
    </div>
    <div class="music-list" id="mus-list"></div>
  </div>`;

  const listEl = $('#mus-list');

  listEl.addEventListener('click', e => {
    const edit = e.target.closest('[data-tag-edit]');
    if(edit){ e.preventDefault(); e.stopPropagation(); openTagEditor(edit.dataset.tagEdit, edit); return; }
    const del = e.target.closest('[data-tag-del]');
    if(del){
      e.preventDefault(); e.stopPropagation();
      const box = del.closest('[data-tagrow]');
      if(box){
        const dir = box.dataset.tagrow;
        MUSIC_TAGS.del(dir, del.dataset.tagDel);
        syncMusicTagRow(dir);
        if(tagEditorDir === dir) closeTagEditor();
      }
    }
  }, true);
  function apply(){
    if(musTab==='import'){
      favRefresh = null;
      if($('#mus-filter')) $('#mus-filter').style.display='none';
      renderImportPanel(listEl);
      return;
    }
    if($('#mus-filter')) $('#mus-filter').style.display='';
    if(musTab==='voice'){ openVoiceModal(); return; }
    const q = ($('#mus-q').value||'').trim().toLowerCase();
    const isFav = musTab==='fav';

    let tracks = isFav ? data.tracks.filter(t=>FAV.has('mus:'+t.dir))
             : data.tracks.filter(t=> musTab==='mv' ? t.kind==='mv' : t.kind==='ost');
    if(q) tracks = tracks.filter(t=>(t.name+' '+(t.artist||'')+' '+(t.use||'')+' '+(t.album||'')+' '+(t.ost||'')+' '+(t.num||'')).toLowerCase().includes(q));
    $('#mus-count').textContent = (isFav?'我喜欢的 ':'') + tracks.length+' 首';
    favRefresh = isFav ? apply : null;
    if(!tracks.length){
      switchFx(listEl, ()=>{ listEl.innerHTML = isFav ? favEmpty() : '<div class="empty">没有匹配的曲目</div>'; });
      return;
    }
    renderList(listEl, tracks, (t,idx)=>{
      const mvRow = isFav ? (t.kind==='mv') : (musTab==='mv');
      const row = document.createElement('div');
      row.className = 'music-row';
      row.dataset.pidx = idx;

      const ostNum = t.ost?String(t.ost).padStart(3,'0'):'';
      const subParts = [];
      if(t.artist) subParts.push(t.artist);
      if(t.use) subParts.push(t.use);
      if(mvRow && t.album && t.album!==t.name && t.album!=='游戏内曲目') subParts.push(t.album);
      const sub = subParts.join(' · ') || (mvRow?'主题曲 MV · 点击播放视频':'Blue Archive OST');
      const noVideo = mvRow && !t.video;
      const mediaRel = mvRow ? (t.video||t.audio) : t.audio;
      const dlFile = mediaRel ? mediaRel.split('/').pop() : '';
      const hq = t.lossless
        ? `<span class="music-hires">${esc(t.fmt||'LOSSLESS')}${t.bits?' '+t.bits+'bit':''}${t.hz?' / '+Math.round(t.hz/1000)+'kHz':''}</span>`
        : '';
      const durTxt = t.dur ? Math.floor(t.dur/60)+':'+String(Math.round(t.dur%60)).padStart(2,'0') : '';
      row.innerHTML = `
        <div class="music-num">${ostNum?esc(ostNum):(mvRow?'🎞':(t.lossless?'💎':'·'))}</div>
        <div class="music-cover-wrap">${t.cover
          ? `<img class="music-cover" loading="lazy" decoding="async" src="${thumbUrl('music', t.dir+'/cover')}"
               onload="this.classList.add('loaded')" onerror="this.parentNode.classList.add('no-cover');this.remove()">`
          : '<div class="music-cover ph">🎵</div>'}</div>
        <div class="music-main">
          <div class="music-name">${esc(t.name)}${hq}</div>
          <div class="music-artist">${esc(sub)}</div>
          ${musicTagsHtml(t)}
        </div>
        <div class="music-dur">${mvRow?(noVideo?'<span class="tag-missing">视频未下载</span>':'视频 ▶'):(durTxt||'本地音频')}</div>
        ${favBtn('mus:'+t.dir)}
        ${mediaRel?`<a class="dl-btn" href="${mediaUrl(mediaRel)}" download="${esc(dlFile)}" title="下载文件"
           onclick="event.stopPropagation()" ondblclick="event.stopPropagation()">⬇ 下载</a>`:''}`;

      row.onclick = ()=>{
        if(row._ct) return;
        row._ct = setTimeout(()=>{
          row._ct = null;
          if(mvRow){
            if(!t.video){ showMsg('这支 MV 的视频还没有下载到本地，目前只有音频。<br>可运行 <b>下载MV视频.bat</b> 自动补下载。','MV 视频未收录'); return; }
            openVideo({file:t.video, title:t.name});
          } else startPlay(tracks, idx);
        }, 260);
      };
      row.ondblclick = (e)=>{
        if(e.target.closest('.music-tags')) return;
        if(row._ct){ clearTimeout(row._ct); row._ct = null; }
        e.stopPropagation();
        if(mediaRel) openFolder(mediaRel);
      };
      if(idx===player.idx) row.classList.add('playing');
      return row;
    }, 100, ()=>{

      jumpInput('mus-jump', tracks, listEl, raw=>{
        let i = tracks.findIndex(t=>t.ost && String(t.ost) === raw);
        if(i < 0) i = tracks.findIndex(t=>t.num && String(t.num) === raw);
        return i;
      });
    });
  }
  $('#mus-tabs').querySelectorAll('.sub-tab').forEach(b=>b.onclick=()=>{
    if(b.dataset.favTab){
      musTab = 'fav';
    }else if(b.dataset.tab==='voice'){
      $('#mus-tabs').querySelectorAll('.sub-tab').forEach(x=>x.classList.toggle('active', x.dataset.tab===musTab));
      openVoiceModal();
      return;
    }else{
      musTab = b.dataset.tab;
    }
    $('#mus-tabs').querySelectorAll('.sub-tab').forEach(x=>x.classList.toggle('active', x===b));
    apply();
  });

  $('#mus-tabs').querySelectorAll('.sub-tab').forEach(x=>x.classList.toggle('active', x.dataset.favTab ? (musTab==='fav') : (x.dataset.tab===musTab)));
  $('#mus-q').oninput = debounce(apply, 220);
  apply();
}

function renderImportPanel(box){
  if(!box) return;
  box.innerHTML = `
    <div class="imp-hero">我们自研的整套系统，专门应对几乎所有格式，哪怕是 B 站客户端经过加密的音频文件，哪怕是网易云里边的保护音频。</div>
    <div id="imp-drop" class="imp-drop">
      <div class="imp-ico">⬇</div>
      <div class="imp-title">拖到这里，或点击选择文件</div>
      <div class="imp-sub">可以一次拖入视频 M4S + 音频 M4S（自动配对合成 MP4），也可以只拖音频，单独归入音频分区</div>
      <input type="file" id="imp-file" multiple hidden>
    </div>
    <div id="imp-status" class="imp-status"></div>
    <div class="imp-section"><h3>🎬 视频分区</h3><div id="imp-videos" class="imp-list"></div></div>
    <div class="imp-section"><h3>🎵 音频分区</h3><div id="imp-audios" class="imp-list"></div></div>
    <div class="imp-section"><h3>💎 无损音乐</h3><div id="imp-lossless" class="imp-list"></div></div>`;

  const drop = $('#imp-drop'), input = $('#imp-file');
  drop.onclick = () => input.click();
  input.onchange = () => { const fs = [...(input.files || [])]; input.value = ''; if(fs.length) importFiles(fs); };
  ['dragenter','dragover'].forEach(k => drop.addEventListener(k, e => { e.preventDefault(); drop.classList.add('over'); }));
  ['dragleave','drop'].forEach(k => drop.addEventListener(k, e => { e.preventDefault(); drop.classList.remove('over'); }));
  drop.addEventListener('drop', e => { const fs = [...((e.dataTransfer && e.dataTransfer.files) || [])]; if(fs.length) importFiles(fs); });
  loadImportList();
  loadLosslessList();
}

async function loadLosslessList(){
  const box = $('#imp-lossless');
  if(!box) return;
  try{
    const d = await (await fetch('/api/music?t=' + Date.now())).json();
    const list = (d.tracks || []).filter(t => t.kind === 'lossless');
    if(!list.length){
      box.innerHTML = '<div class="empty">还没有无损曲目。把 FLAC 放进资源库的 10_无损音乐 目录，几秒内会自动出现。</div>';
      return;
    }
    box.innerHTML = list.map((t, i) => `<div class="imp-item" data-lsi="${i}">
        <div class="imp-meta"><div class="imp-name">${esc(t.name)}<span class="music-hires">${esc(t.fmt || 'LOSSLESS')}${t.bits ? ' ' + t.bits + 'bit' : ''}${t.hz ? ' / ' + Math.round(t.hz / 1000) + 'kHz' : ''}</span></div>
          <div class="imp-info">${esc(t.artist || '未知艺术家')}${t.dur ? ' · ' + fmtDur(t.dur) : ''} · ${fmtMB(t.bytes || 0)}</div></div>
        <button class="imp-btn" data-lplay="${i}">▶ 播放</button>
      </div>`).join('');
    box.querySelectorAll('[data-lplay]').forEach(btn => {
      btn.onclick = e => {
        e.stopPropagation();
        startPlay(list, +btn.dataset.lplay);
      };
    });
    syncLosslessButtons();
  }catch(e){ box.innerHTML = '<div class="empty">读取无损列表失败</div>'; }
}

function uploadImportFile(file, token, onProgress){
  return new Promise((res, rej) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/import/upload?token=' + encodeURIComponent(token) + '&name=' + encodeURIComponent(file.name));
    xhr.upload.onprogress = e => { if(e.lengthComputable && onProgress) onProgress(e.loaded / e.total); };
    xhr.onload = () => {
      try { const j = JSON.parse(xhr.responseText || '{}'); j.ok ? res(j) : rej(new Error(j.error || '上传失败')); }
      catch(e) { rej(new Error('上传失败')); }
    };
    xhr.onerror = () => rej(new Error('网络错误'));
    xhr.send(file);
  });
}
function fmtMB(n){ n = n || 0; return n < 1048576 ? (n / 1024).toFixed(0) + ' KB' : (n / 1048576).toFixed(1) + ' MB'; }
function fmtDur(s){ s = Math.round(s || 0); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); }

let __importBusy = false;
async function importFiles(files){
  const st = $('#imp-status');
  if(!st) return;
  if(__importBusy){
    st.innerHTML = '<span class="imp-warn">上一批还在处理，完成后再拖新的文件。</span>';
    return;
  }
  __importBusy = true;
  const dropEl = $('#imp-drop');
  if(dropEl) dropEl.classList.add('busy');
  if(!window.ffmpegReadyHinted){
    window.ffmpegReadyHinted = true;
    st.innerHTML = '正在上传，处理在本地服务端用 ffmpeg 完成，不会上传到外网。';
  }
  const token = ('imp' + Date.now() + Math.random().toString(36).slice(2)).replace(/[^a-z0-9]/gi, '').slice(0, 24);
  let done = 0;
  try{
    for(const f of files){
      st.innerHTML = '正在上传 ' + esc(f.name) + '…（' + done + '/' + files.length + '）';
      await uploadImportFile(f, token, p => {
        st.innerHTML = '正在上传 ' + esc(f.name) + '… ' + Math.round(p * 100) + '%（' + done + '/' + files.length + '）';
      });
      done++;
    }
    st.innerHTML = '已上传，开始处理…';
    const r = await fetch('/api/import/process', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token })
    });
    const j = await r.json();
    if(!j.ok){
      st.innerHTML = '<span class="imp-err">处理失败：' + esc(j.error || '未知错误') +
        (j.detail ? '<br><small>' + esc(j.detail) + '</small>' : '') + '</span>';
      return;
    }
    const res = await pollImportJob(j.jobId);
    st.innerHTML = '<span class="imp-ok">✅ ' + esc(res.message || '处理完成') + '：' +
      res.items.map(x => esc(x.title)).join('、') + '</span>';
    loadImportList();
  }catch(e){
    st.innerHTML = '<span class="imp-err">处理失败：' + esc(e && e.message || e) + '</span>';
  } finally {
    __importBusy = false;
    const dp = $('#imp-drop');
    if(dp) dp.classList.remove('busy');
  }
}

async function pollImportJob(jobId){
  const st = $('#imp-status');
  for(;;){
    await new Promise(r => setTimeout(r, 700));
    let d;
    try{
      d = await (await fetch('/api/import/status?job=' + encodeURIComponent(jobId) + '&t=' + Date.now())).json();
    }catch(e){ throw new Error('无法读取任务状态'); }
    if(!d.ok) throw new Error(d.error || '任务已过期');
    if(d.message) st.innerHTML = esc(d.message) + (d.total ? '（' + d.done + '/' + d.total + '）' : '');
    if(d.state === 'done') return d;
    if(d.state === 'failed') throw new Error(d.error || '处理失败');
  }
}

async function loadImportList(){
  const boxV = $('#imp-videos'), boxA = $('#imp-audios');
  if(!boxV || !boxA) return;
  try{
    const d = await (await fetch('/api/import/list?t=' + Date.now())).json();
    const row = it => `<div class="imp-item" data-id="${esc(it.id)}">
        <div class="imp-meta"><div class="imp-name">${esc(it.title)}</div>
          <div class="imp-info">${fmtMB(it.size || 0)} · ${esc(it.added || '')}${it.duration ? ' · ' + fmtDur(it.duration) : ''}${it.lufs ? ' · ' + Number(it.lufs).toFixed(1) + ' LUFS' : ''}${it.truePeak ? ' · TP ' + Number(it.truePeak).toFixed(1) + ' dB' : ''}</div></div>
        <button class="imp-btn" data-play="${esc(it.id)}">▶ 播放</button>
        <button class="imp-btn ghost" data-del="${esc(it.id)}">删除</button>
      </div>`;
    boxV.innerHTML = (d.video || []).map(row).join('') || '<div class="empty">还没有导入视频</div>';
    boxA.innerHTML = (d.audio || []).map(row).join('') || '<div class="empty">还没有导入音频</div>';
    const map = {};
    [...(d.video || []), ...(d.audio || [])].forEach(x => { map[x.id] = x; });
    document.querySelectorAll('.imp-item').forEach(el => {
      const it = map[el.dataset.id];
      if(!it) return;
      el.querySelector('[data-play]').onclick = () => playImport(it, el);
      el.querySelector('[data-del]').onclick = async () => {
        if(!confirm('删除「' + it.title + '」及其本地文件？')) return;
        const r = await fetch('/api/import/delete', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: it.id })
        });
        const j = await r.json();
        if(j.ok) loadImportList(); else alert(j.error || '删除失败');
      };
    });
  }catch(e){ boxV.innerHTML = '<div class="empty">读取列表失败</div>'; }
}

function playImport(it, el){
  const old = el.querySelector('.imp-player');
  if(old){ old.remove(); return; }
  const box = document.createElement('div');
  box.className = 'imp-player';
  const src = '/import-media?p=' + toHex(it.file);
  box.innerHTML = it.type === 'video'
    ? `<video controls autoplay src="${src}" style="width:100%;max-height:380px;border-radius:10px;background:#000"></video>`
    : `<audio controls autoplay src="${src}" style="width:100%"></audio>`;
  el.appendChild(box);
}

const PL_LS = 'ba.player.v1';
const plState = { mode:'order', vol:1, muted:false, rate:1 };
try{ Object.assign(plState, JSON.parse(localStorage.getItem(PL_LS)||'{}')); }catch(e){}
plState.rate = +plState.rate || 1;
plState.vol = plState.vol == null ? 1 : +plState.vol;
function plSave(){ try{ localStorage.setItem(PL_LS, JSON.stringify(plState)); }catch(e){} }
function fmtTime(s){ if(!s || !isFinite(s) || s < 0) return '0:00'; s = Math.floor(s); const m = Math.floor(s/60), x = s%60; return m+':'+(x<10?'0':'')+x; }
function plVolApply(){
  player.audio.volume = plState.vol;
  player.audio.muted = plState.muted;
  $('#pl-vol').value = plState.vol;
  $('#pl-mute').textContent = (plState.muted || plState.vol === 0) ? '🔇' : '🔊';
}
function plModeApply(){
  const map = { order:['➡','顺序连播（播完即停）'], list:['🔁','列表循环'], repeat:['🔂','单曲循环'], shuffle:['🔀','随机播放'] };
  const m = map[plState.mode] || map.order;
  $('#pl-mode').textContent = m[0];
  $('#pl-mode').title = '播放模式：' + m[1] + '（点击切换）';
}
plVolApply();
plModeApply();
try {
  player.audio.preservesPitch = true;
  player.audio.webkitPreservesPitch = true;
  player.audio.mozPreservesPitch = true;
  player.audio.msPreservesPitch = true;
} catch(e) {}
let rateAnim = 0;
function applyRate(target, instant){
  target = Math.max(0.5, Math.min(2, +target || 1));
  cancelAnimationFrame(rateAnim);
  const a = player.audio;
  if(instant === true || uiState.smoothRate === false || a.paused || !a.duration){
    a.playbackRate = target;
    return;
  }
  const from = a.playbackRate || 1;
  if(Math.abs(from - target) < 0.01){ a.playbackRate = target; return; }
  const t0 = performance.now(), dur = 320;
  const tick = now=>{
    const k = Math.min(1, (now - t0) / dur);
    const e = k < 0.5 ? 2*k*k : 1 - Math.pow(-2*k + 2, 2) / 2;
    a.playbackRate = from + (target - from) * e;
    if(k < 1) rateAnim = requestAnimationFrame(tick);
  };
  rateAnim = requestAnimationFrame(tick);
}
applyRate(plState.rate, true);
$('#pl-rate').value = String(plState.rate);
$('#pl-vol').oninput = e=>{ plState.vol = +e.target.value; plState.muted = false; plVolApply(); plSave(); };
$('#pl-mute').onclick = ()=>{ plState.muted = !plState.muted; plVolApply(); plSave(); };
$('#pl-rate').onchange = e=>{ plState.rate = +e.target.value; applyRate(plState.rate); plSave(); };
$('#pl-mode').onclick = ()=>{
  const order = ['order','list','repeat','shuffle'];
  const i = order.indexOf(plState.mode);
  plState.mode = order[(i < 0 ? 0 : i + 1) % order.length];
  plModeApply(); plSave();
};
['loadedmetadata','durationchange'].forEach(ev=>player.audio.addEventListener(ev, ()=>{
  $('#pl-tdur').textContent = fmtTime(player.audio.duration);
}));

(function(){
  const host = $('#pl-spec');
  if(!host) return;
  const clipEl = $('#pl-clip');
  let clipHoldAt = 0;
  for(let i=0;i<14;i++) host.appendChild(document.createElement('i'));
  let raf = 0;
  function tick(){
    raf = 0;
    const on = !!(window.AudioLab && AudioLab.state.on && AudioLab.hasAnalyser());
    const lv = on ? AudioLab.getLevels(host.children.length) : null;
    host.classList.toggle('on', !!lv);
    if(lv){ for(let i=0;i<host.children.length;i++) host.children[i].style.height = Math.max(2, Math.round(lv[i]*26)) + 'px'; }

    if(clipEl){
      const st = (window.AudioLab && AudioLab.state.on && AudioLab.getClipState) ? AudioLab.getClipState() : null;
      const bad = !!(st && (st.level === 'clip' || st.level === 'over'));
      if(bad) clipHoldAt = performance.now();
      const show = bad || (clipHoldAt && performance.now() - clipHoldAt < 800);
      clipEl.classList.toggle('on', !!show);
      if(st) clipEl.title = '峰值 ' + st.db + ' dBFS · 素材基线 ' + (st.baseDb == null ? '学习中' : st.baseDb + ' dBFS') +
        (st.rel == null ? '' : ' · 相对 ' + (st.rel > 0 ? '+' : '') + st.rel + ' dB') + ' · 削顶样本 ' + st.hot + '%';
    }
    if(!player.audio.paused) raf = requestAnimationFrame(tick);
  }
  player.audio.addEventListener('play', ()=>{ if(!raf) raf = requestAnimationFrame(tick); });
  player.audio.addEventListener('pause', ()=>{
    if(raf){ cancelAnimationFrame(raf); raf = 0; }
    host.classList.remove('on');
    if(clipEl) clipEl.classList.remove('on');
    for(let i=0;i<host.children.length;i++) host.children[i].style.height = '2px';
  });
})();

player.audio.addEventListener('ended', ()=>{
  if(player.idx < 0 || !player.list.length) return;
  if(plState.mode === 'repeat'){ player.audio.currentTime = 0; player.audio.play().catch(()=>{}); return; }
  if(plState.mode === 'shuffle'){
    if(player.list.length === 1){ player.audio.currentTime = 0; player.audio.play().catch(()=>{}); return; }
    let j = Math.floor(Math.random()*(player.list.length-1));
    if(j >= player.idx) j++;
    startPlay(player.list, j);
    return;
  }
  if(plState.mode === 'list'){

    if(player.idx < player.list.length-1) startPlay(player.list, player.idx+1);
    else startPlay(player.list, 0);
    return;
  }
  if(player.idx < player.list.length-1) startPlay(player.list, player.idx+1);
});
player.audio.addEventListener('play', ()=>$('#pl-play').textContent='⏸');
player.audio.addEventListener('pause', ()=>$('#pl-play').textContent='▶');
player.audio.addEventListener('error', ()=>{
  if(!player.errGuard && player.idx >= 0){
    player.errGuard = true;
    showMsg('这份音频还没有下载到本地，暂时无法播放。<br>离线版仅能播放<b>已收录</b>的内容。','音频未收录');
  }
});
player.audio.addEventListener('timeupdate', ()=>{
  $('#pl-tcur').textContent = fmtTime(player.audio.currentTime);
  if(scrubbing) return;
  const d = player.audio.duration;
  if(d && isFinite(d)) $('#pl-prog').style.width = (player.audio.currentTime/d*100)+'%';
});
$('#pl-play').onclick = ()=>{
  if(player.idx < 0){ const list=(apiCache.music||{tracks:[]}).tracks.filter(t=>t.kind!=='mv'); if(list.length) startPlay(list,0); return; }
  player.audio.paused ? player.audio.play() : player.audio.pause();
};
$('#pl-next').onclick = ()=>{ if(player.idx < player.list.length-1) startPlay(player.list, player.idx+1); };
$('#pl-prev').onclick = ()=>{ if(player.idx > 0) startPlay(player.list, player.idx-1); };

(function(){
  const au = $('#audio'), pl = $('#player');
  if(!au || !pl) return;
  au.addEventListener('play',  ()=> pl.classList.add('playing'));
  au.addEventListener('pause', ()=> pl.classList.remove('playing'));
  au.addEventListener('ended', ()=> pl.classList.remove('playing'));
})();

const plBar = $('.pl-bar');
let scrubbing = false;
function barRatio(e){
  const r = plBar.getBoundingClientRect();
  if(!r.width) return null;
  let x = (e.clientX - r.left) / r.width;
  return Math.max(0, Math.min(1, x));
}
function barSeek(e, commit){
  const x = barRatio(e);
  if(x == null) return;
  $('#pl-prog').style.width = (x*100)+'%';
  const d = player.audio.duration;
  if(commit && d && isFinite(d)) player.audio.currentTime = x * d;
}
function scrubEnd(e){
  if(!scrubbing) return;
  scrubbing = false;
  plBar.classList.remove('scrubbing');
  barSeek(e, true);
  try{ plBar.releasePointerCapture(e.pointerId); }catch(_){}
}

function cancelScrub(){
  if(!scrubbing) return;
  scrubbing = false;
  plBar.classList.remove('scrubbing');
}
plBar.addEventListener('pointerdown', e=>{
  const d = player.audio.duration;
  if(!d || !isFinite(d)) return;
  scrubbing = true;
  plBar.classList.add('scrubbing');
  try{ plBar.setPointerCapture(e.pointerId); }catch(_){}
  barSeek(e, false);
  e.preventDefault();
});
plBar.addEventListener('pointermove', e=>{ if(scrubbing) barSeek(e, false); });
plBar.addEventListener('pointerup', scrubEnd);
plBar.addEventListener('pointercancel', scrubEnd);

plBar.addEventListener('lostpointercapture', ()=>{ if(scrubbing){ scrubbing=false; plBar.classList.remove('scrubbing'); } });

$('#vcm-close').onclick = ()=>hideFx($('#voice-modal'));
$('#voice-modal').onclick = e=>{ if(e.target.id==='voice-modal') hideFx($('#voice-modal')); };

let roleMusicCache = null;
async function loadRoleMusic(){
  if(roleMusicCache) return roleMusicCache;
  const r = await fetch('assets/role-music.json?v=20261015f');
  const j = await r.json();
  roleMusicCache = {
    topics: (j.topics || []).filter(x => x.tracks && x.tracks.length),
    roles: (j.roles || []).filter(x => x.tracks && x.tracks.length)
  };
  return roleMusicCache;
}

async function openVoiceModal(){
  showFx($('#voice-modal'));
  const stuHost = $('#vcm-stu');
  const lineHost = $('#vcm-lines');
  lineHost.innerHTML = '<div class="loading">正在读取角色音乐数据……</div>';
  let data = { topics: [], roles: [] };
  try{ data = await loadRoleMusic(); }catch(e){}
  const list = data.topics.concat(data.roles);
  if(!list.length){
    stuHost.innerHTML = '';
    lineHost.innerHTML = '<div class="empty" style="border:none">暂时没有可用的角色音乐数据</div>';
    return;
  }

  function listStu(){
    const q = ($('#vcm-q').value||'').trim().toLowerCase();
    const arr = list.filter(x => !q || (x.cn+' '+(x.jp||'')+' '+(x.en||'')+' '+(x.desc||'')).toLowerCase().includes(q));
    stuHost.innerHTML = '';
    if(!arr.length){ stuHost.innerHTML = '<div class="empty" style="border:none;padding:20px">没有匹配的学生</div>'; return; }
    arr.forEach(s=>{
      const b = document.createElement('button');
      b.className = 'vcm-stu-item' + (s.topic ? ' is-topic' : '');
      const head = s.topic
        ? `<span class="vcm-topic-ico">${esc(s.icon || String.fromCodePoint(0x1F3B5))}</span>`
        : `<img loading="lazy" src="${thumbUrl('students', s.dir+'/avatar')}" onerror="this.src='assets/arona.png'">`;
      b.innerHTML = `${head}<span>${esc(s.cn)}<small class="vn-orig"> ${s.tracks.length} 首</small></span>`;
      b.dataset.sid = s.id;
      b.onclick = ()=>{ markActive(s.id); showTracks(s); };
      stuHost.appendChild(b);
    });
  }

  const VCM_PAGE = 60;
  function showTracks(s){

    const queue = s.tracks.filter(t=>t.local && t.local.audio).map(t=>({
      name: s.cn+' · '+t.title,
      audio: t.local.audio,
      coverUrl: s.topic ? '' : thumbUrl('students', s.dir+'/avatar'),
      artist: t.type || '角色相关音乐'
    }));
    let qi = 0;
    const rows = s.tracks.map((t, i)=>{
      const ok = !!(t.local && t.local.audio);
      const gi = ok ? qi++ : -1;
      return `<div class="vcm-line${ok?'':' no-local'}" data-gi="${gi}" title="${esc(ok ? '点击播放' : '这首还没有下载到本地')}">`
           + `<span class="vcm-no">${String(i+1).padStart(3,'0')}</span>`
           + `<span class="vcm-ico">${ok?'🔊':'—'}</span>`
           + `<span>${esc(t.title)}<small class="vn-orig">${esc(t.cn||'')}${t.type?' · '+esc(t.type):''}${ok?'':' · 未收录'}</small></span>`
           + `</div>`;
    });
    let shown = VCM_PAGE;
    function paint(){
      const part = rows.slice(0, shown).join('');
      const more = shown < rows.length
        ? `<button class="load-more" id="vcm-more">还有 ${rows.length - shown} 首 · 点此继续展开</button>` : '';
      lineHost.innerHTML = `<div class="vcm-lines-title">🎵 ${esc(s.cn)} 的相关音乐（共 ${s.tracks.length} 首 · 本地可播 ${queue.length} 首）</div>${part}${more}`;
      lineHost.querySelectorAll('.vcm-line').forEach(el=>el.onclick=()=>{
        const gi = parseInt(el.dataset.gi, 10);
        if(gi < 0){ showMsg('这首还没有下载到本地，暂时无法播放。<br>离线版仅能播放<b>已收录</b>的内容。','音乐未收录'); return; }
        lineHost.querySelectorAll('.vcm-line').forEach(x=>x.classList.remove('playing'));
        el.classList.add('playing');
        startPlay(queue, gi);
      });
      const mb = lineHost.querySelector('#vcm-more');
      if(mb) mb.onclick = ()=>{ shown += VCM_PAGE; paint(); };
    }
    paint();
  }

  function markActive(id){
    stuHost.querySelectorAll('.vcm-stu-item').forEach(x=>x.classList.toggle('active', x.dataset.sid === id));
  }

  $('#vcm-q').value = '';
  $('#vcm-q').oninput = debounce(listStu, 180);
  listStu();

  if(list.length){ markActive(list[0].id); showTracks(list[0]); }
}

$('#vm-video').addEventListener('error', function(){
  if(this.getAttribute('src')) showMsg('这个视频还没有下载到本地，或文件正在索引中。<br>离线版仅能播放<b>已收录</b>的视频。','视频不可用');
});
function openVideo(v){

  try{ player.audio.pause(); }catch(e){}
  const src = mediaUrl(v.file);
  $('#vm-video').src = src;
  $('#vm-title').textContent = v.title + (v.bv?('  ['+v.bv+']'):'');
  showFx($('#video-modal'));
}
$('#vm-close').onclick = ()=>{

  const vv = $('#vm-video');
  vv.pause(); vv.removeAttribute('src'); vv.load();
  hideFx($('#video-modal'));
};
$('#video-modal').onclick = e=>{ if(e.target.id==='video-modal'){ $('#vm-close').click(); } };

document.addEventListener('keydown', e=>{
  if(e.key!=='Escape') return;
  if(!$('#video-modal').classList.contains('hidden')) $('#vm-close').click();
  else if(!$('#voice-modal').classList.contains('hidden')) hideFx($('#voice-modal'));
  else if(!$('#lightbox').classList.contains('hidden')) $('#lb-close') && $('#lb-close').click();
});

let vidView = 'all';
let biliView = 'all';

function videoCard(v){
  const card = document.createElement('div');
  card.className = 'video-card';
  const poster = thumbUrl('video', v.posterKey || v.file);
  const dlFile = String(v.file||'').split('/').pop();

  const favKey = (v.category === 'B站官方视频' ? 'bili:' : 'vid:') + v.file;
  card.innerHTML = `
    <div class="video-thumb">
      <img loading="lazy" decoding="async" src="${poster}" data-rel="${esc(v.file)}" onload="this.classList.add('loaded')" onerror="this.remove()">
      ${favBtn(favKey)}
      <span class="video-cat">${esc(v.category)}</span>
      <span class="play-ico">▶</span>
    </div>
    <div class="video-body">
      <div class="video-title2">${esc(v.title)}</div>
      <div class="video-dl-row">
        <a class="dl-btn" href="${mediaUrl(v.file)}" download="${esc(dlFile)}" title="下载视频"
           onclick="event.stopPropagation()" ondblclick="event.stopPropagation()">⬇ 下载</a>
      </div>
    </div>`;
  card.onclick = ()=>openVideo(v);

  card.ondblclick = (e)=>{ e.stopPropagation(); if(v.file) openFolder(v.file); };
  return card;
}

async function renderVideos(){
  $('#player').classList.add('hidden');
  const data = await api('videos');
  const vids = data.videos.filter(v=>v.category!=='B站官方视频');
  const cats = [...new Set(vids.map(v=>v.category))];
  app.innerHTML = `
  <div class="wrap">
    <div class="page-title">🎬 官方PV</div>
    <div class="page-sub">本地高清视频 · 支持进度拖放（本地流式传输）</div>
    <div class="filter-bar">
      <input type="text" id="vid-q" placeholder="🔍 搜索视频标题 / BV号">
      <label>分类</label>
      <select id="vid-cat"><option value="">全部</option>${cats.map(c=>`<option>${esc(c)}</option>`).join('')}</select>
      <span class="sp"></span>
      <input type="text" id="vid-jump" class="jump-box" placeholder="📍 定位：序号 / BV号" title="输入序号或 BV 号后回车，会自动展开「加载更多」并滚到那个视频">
      <span id="vid-count" style="font-size:13px;color:var(--text-sub)"></span>
    </div>
    <div class="sub-tabs" id="vid-subs">
      <button class="sub-tab ${vidView==='all'?'active':''}" data-v="all">全部</button>
      ${favTabBtn('vid', vidView==='fav')}
    </div>
    <div class="video-grid" id="vid-grid"></div>
  </div>`;
  const grid = $('#vid-grid');
  function apply(){
    const q = ($('#vid-q').value||'').trim().toLowerCase();
    const cat = $('#vid-cat').value;
    let list = vids.filter(v=>(!cat||v.category===cat) && (!q || (v.title+' '+(v.bv||'')).toLowerCase().includes(q)));
    if(vidView==='fav') list = list.filter(v=>FAV.has('vid:'+v.file));
    $('#vid-count').textContent = (vidView==='fav'?'我喜欢的 ':'') + list.length+' 个';
    favRefresh = vidView==='fav' ? apply : null;
    if(!list.length){
      switchFx(grid, ()=>{ grid.innerHTML = vidView==='fav' ? favEmpty() : '<div class="empty">没有匹配的视频</div>'; });
      return;
    }
    renderList(grid, list, videoCard, 30, ()=>{ jumpInput('vid-jump', list, grid, raw=>list.findIndex(v=>v.bv && v.bv === raw)); });
  }
  $('#vid-subs').querySelectorAll('.sub-tab').forEach(b=>b.onclick=()=>{
    vidView = b.dataset.v || 'fav';
    $('#vid-subs').querySelectorAll('.sub-tab').forEach(x=>x.classList.toggle('active', x===b));
    apply();
  });
  $('#vid-q').oninput = debounce(apply, 220);
  $('#vid-cat').onchange = apply;
  apply();
}

async function renderBilibili(){
  $('#player').classList.add('hidden');
  const data = await api('videos');
  let vids = data.videos.filter(v=>v.category==='B站官方视频');
  app.innerHTML = `
  <div class="wrap">
    <div class="page-title">📺 B站视频分区</div>
    <div class="page-sub">蔚蓝档案官方哔哩哔哩账号视频的本地镜像 · 独立分区存放于 bilibili 文件夹</div>
    <div class="filter-bar">
      <span class="sp"></span>
      <input type="text" id="vid-jump" class="jump-box" placeholder="📍 定位：序号 / BV号" title="输入序号或 BV 号后回车，会自动展开「加载更多」并滚到那个视频">
    </div>
    <div class="sub-tabs" id="vid-subs">
      <button class="sub-tab ${biliView==='all'?'active':''}" data-v="all">全部</button>
      ${favTabBtn('bili', biliView==='fav')}
    </div>
    <div class="video-grid" id="vid-grid"></div>
    ${vids.length===0?`
    <div class="tab-panel" style="margin-top:22px">
      <h3 style="color:var(--primary);margin-bottom:10px">📥 如何添加视频</h3>
      <p style="font-size:14px;line-height:2;color:#55607a">
        1. 将 B 站下载工具 <b>BBDown</b>（或任意工具）下载的视频（MP4）放入文件夹：<br>
        &nbsp;&nbsp;&nbsp;<code style="background:#eef2fb;padding:2px 8px;border-radius:6px">E:\\BAwiki Web\\bilibili\\</code><br>
        2. 文件名即视频标题，支持按子文件夹归类。<br>
        3. 重启 <b>BAwikiWeb.exe</b>（或等待重新索引），视频即会出现在本页。<br><br>
        官方账号空间：<a href="https://space.bilibili.com/1933702210" target="_blank" style="color:var(--accent);font-weight:700">蔚蓝档案 官方B站空间 →</a>
      </p>
    </div>`:''}
  </div>`;
  const grid = $('#vid-grid');
  function apply(){
    const list = biliView==='fav' ? vids.filter(v=>FAV.has('bili:'+v.file)) : vids;
    favRefresh = biliView==='fav' ? apply : null;
    if(!list.length){
      switchFx(grid, ()=>{ grid.innerHTML = biliView==='fav' ? favEmpty() : '<div class="empty">暂无视频</div>'; });
      return;
    }
    renderList(grid, list, videoCard, 30, ()=>{ jumpInput('vid-jump', list, grid, raw=>list.findIndex(v=>v.bv && v.bv === raw)); });
  }
  $('#vid-subs').querySelectorAll('.sub-tab').forEach(b=>b.onclick=()=>{
    biliView = b.dataset.v || 'fav';
    $('#vid-subs').querySelectorAll('.sub-tab').forEach(x=>x.classList.toggle('active', x===b));
    apply();
  });
  apply();
}

async function renderMe(){
  app.innerHTML = '<div class="wrap"><div class="loading">正在读取设置……</div></div>';

  try{ const r = await fetch('/api/me?t='+Date.now()); if(r.ok){ const j = await r.json(); if(j.ok){ SETTINGS.me=j.name||''; SETTINGS.role=j.role||'user'; SETTINGS.perms=j.perms||[]; } } }catch(e){}
  try{ const r = await fetch('/api/settings?t='+Date.now()); if(r.ok){ Object.assign(SETTINGS, await r.json()); } }catch(e){}
  const roleCn = SETTINGS.role==='super'?'超级管理员（本地安全后台）':SETTINGS.role==='admin'?'管理员':'普通用户';
  const permsList = SETTINGS.perms && SETTINGS.perms.length ? SETTINGS.perms.map(p=>`<span class="chip">${esc(PERM_CN[p]||p)}</span>`).join('')
    : (EXTERNAL?'<span class="chip off">未分配任何资源权限</span>':'<span class="chip">本地管理员 · 全部资源</span>');
  app.innerHTML = `
  <div class="wrap">
    <div class="page-title">⚙️ 我的设置</div>
    <div class="page-sub">当前身份：<b>${esc(SETTINGS.me||'未登录')}</b> · ${roleCn}</div>
    <div class="tab-panel" style="margin-bottom:18px">
      <h3 style="color:var(--primary);margin-bottom:14px">👀 UI 设置（按当前账户保存，下次登录仍有效）</h3>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">动态滑动效果 / UI 动效</div><div class="t-desc">悬停滑出按钮、卡片动效、骨架屏动画</div></div>
        <label class="switch"><input type="checkbox" id="me-anim" ${SETTINGS.ui_animation?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">画面切换过渡</div><div class="t-desc">页面/图片切换过渡动画（关闭可降低损耗）</div></div>
        <label class="switch"><input type="checkbox" id="me-trans" ${SETTINGS.ui_transition?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">高速阅览</div><div class="t-desc">图片用粗略小图加载（128px），点开灯箱再看原图</div></div>
        <label class="switch"><input type="checkbox" id="me-fast" ${SETTINGS.fast_view?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">显示下载按钮</div><div class="t-desc">图片/音乐/视频旁的「⬇ 下载」按钮</div></div>
        <label class="switch"><input type="checkbox" id="me-dl" ${SETTINGS.show_download?'checked':''}><span class="sl"></span></label></div>
      <div class="row" style="margin-top:16px">
        <button class="btn" id="me-save">💾 保存我的设置</button>
        <span class="lbl" id="me-tip"></span>
      </div>
    </div>
    <div class="tab-panel" style="margin-bottom:18px">
      <h3 style="color:var(--primary);margin-bottom:14px">🎧 高级音频选项</h3>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">训练数据播放</div><div class="t-desc">参考 LDTE 训练档案修正音色、音量与限幅；仅在 EQ / 音量 / 声场 / 限幅被实际调整后生效，全部默认时完全不改动音频</div></div>
        <label class="switch"><input type="checkbox" id="me-ldte" ${localStorage.getItem('ba.ldte.use')==='1'?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">变速平滑过渡</div><div class="t-desc">切换倍速时用约 0.3 秒平滑过渡，避免瞬间变速的僵硬感；保持音高不变</div></div>
        <label class="switch"><input type="checkbox" id="me-smooth" ${uiState.smoothRate!==false?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">吸顶导航栏</div><div class="t-desc">向下浏览资源时顶部导航一直挂载，随时可切换板块</div></div>
        <label class="switch"><input type="checkbox" id="me-sticky" ${uiState.sticky!==false?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">记忆浏览位置</div><div class="t-desc">切走再切回时恢复原来的滚动位置与画廊分类</div></div>
        <label class="switch"><input type="checkbox" id="me-mem" ${uiState.remember!==false?'checked':''}><span class="sl"></span></label></div>
      <div class="row" style="margin-top:14px;align-items:center">
        <button class="btn ghost" id="me-audio-reset">恢复音频默认值</button>
        <span class="lbl" id="me-audio-tip">训练数据不会在默认设置下改动音频</span>
      </div>
    </div>
    <div class="tab-panel" style="margin-bottom:18px">
      <h3 style="color:var(--primary);margin-bottom:14px">🤖 AI 助手</h3>
      <p style="font-size:13.5px;color:var(--text-sub);line-height:1.9;margin-bottom:12px">
        运维、资料与音频问答，带会话记忆与工具调用。网页端默认中等思考，每账户会话上限 0.5 MB，长期记忆跨会话保留。
      </p>
      <button class="btn" id="me-ai">打开 AI 助手</button>
    </div>
    <div class="tab-panel" style="margin-bottom:18px">
      <h3 style="color:var(--primary);margin-bottom:14px">🔔 场景音效（保存在本机浏览器）</h3>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">板块提示音</div><div class="t-desc">进入留声机 / CG画廊 / 学生图鉴等板块时的一声短促提示音</div></div>
        <label class="switch"><input type="checkbox" id="me-sfx" ${(window.Sfx&&Sfx.sceneOn)?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">学生语音</div><div class="t-desc">点入某个学生时随机播放该学生的一条本地语音</div></div>
        <label class="switch"><input type="checkbox" id="me-sfxvoice" ${(window.Sfx&&Sfx.voiceOn)?'checked':''}><span class="sl"></span></label></div>
      <div class="toggle-row"><div style="flex:1"><div class="t-name">记忆大厅音乐</div><div class="t-desc">进入学生「记忆大厅」时播放该角色相关曲目（默认循环，可切换曲子）</div></div>
        <label class="switch"><input type="checkbox" id="me-sfxlobby" ${(window.Sfx&&Sfx.lobbyOn)?'checked':''}><span class="sl"></span></label></div>
      <div class="row" style="margin-top:12px;align-items:center">
        <span style="font-size:13.5px;color:var(--text-sub)">提示音音量</span>
        <input type="range" id="me-sfxvol" min="0" max="100" value="${window.Sfx?Math.round(Sfx.volume*100):60}" style="flex:1;max-width:260px">
        <span class="lbl" id="me-sfxvol-lbl">${window.Sfx?Math.round(Sfx.volume*100):60}%</span>
        <button class="btn" id="me-sfxtest" style="padding:6px 14px;font-size:13px">▶ 试听</button>
      </div>
      <div style="font-size:12.5px;color:var(--text-sub);margin-top:10px">音量刻意压得很低，只是"哒"一声，不会盖住音乐；嫌吵可以直接关掉。</div>
    </div>
    <div class="tab-panel">
      <h3 style="color:var(--primary);margin-bottom:10px">🔑 我的可访问权限（由管理员分配）</h3>
      <div style="font-size:13.5px;line-height:2">${permsList}</div>
    </div>
  </div>`;
  $('#me-save').onclick = async ()=>{
    const body = { anim:$('#me-anim').checked, trans:$('#me-trans').checked, fast:$('#me-fast').checked, dl:$('#me-dl').checked };
    try{
      const r = await fetch('/api/me/ui', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body)});
      const j = await r.json();
      if(j.ok){ toast('✅ 已保存到当前账户'); $('#me-tip').textContent = '已保存 · '+new Date().toLocaleTimeString(); await loadSiteSettings(); }
      else toast('保存失败：'+(j.error||''));
    }catch(e){ toast('保存失败'); }
  };

  if(window.Sfx){    $('#me-sfx').onchange = e=> Sfx.setScene(e.target.checked);
    $('#me-sfxvoice').onchange = e=> Sfx.setVoice(e.target.checked);
    $('#me-sfxlobby').onchange = e=> Sfx.setLobby(e.target.checked);
    $('#me-sfxvol').oninput = e=>{ const v = Sfx.setVolume(+e.target.value/100); $('#me-sfxvol-lbl').textContent = Math.round(v*100)+'%'; };
    $('#me-sfxtest').onclick = ()=> Sfx.preview();
  }
  $('#me-ldte').onchange = e=>{
    const on = e.target.checked;
    try{ localStorage.setItem('ba.ldte.use', on ? '1' : '0'); }catch(err){}
    if(window.AudioLab) AudioLab.setUseLdte(on);
    $('#me-audio-tip').textContent = on ? '训练数据已启用：只在调整过设置时参与修正' : '训练数据已关闭，不参与任何处理';
  };
  $('#me-smooth').onchange = e=>{ uiState.smoothRate = e.target.checked; uiSave(); };
  $('#me-sticky').onchange = e=>{ uiState.sticky = e.target.checked; uiSave(); applyUiPrefs(); };
  $('#me-mem').onchange = e=>{
    uiState.remember = e.target.checked;
    if(!uiState.remember) uiState.scroll = {};
    uiSave();
  };
  $('#me-audio-reset').onclick = ()=>{
    if(window.AudioLab){ AudioLab.resetDefaults(); $('#me-audio-tip').textContent = '已恢复默认值：训练数据不再改动音频'; toast('✅ 音频参数已恢复默认'); }
  };
  const aiBtn = $('#me-ai');
  if(aiBtn) aiBtn.onclick = ()=>{ if(window.openAiPanel) window.openAiPanel(); else toast('AI 助手未加载'); };
}

let onlineCheck = null;
let pfSyncTimer = null;
async function renderOnline(){
  $('#player').classList.add('hidden');
  app.innerHTML = `
  <div class="wrap">
    <div class="page-title">⬇️ 在线更新</div>
    <div class="page-sub">从 kivo.wiki / B 站拉取更多资源到本地 · 离线版仅提供已收录内容</div>

    <div class="tab-panel" style="margin-top:16px">
      <h3 style="color:var(--primary);margin-bottom:10px">🚀 一键全部资源更新（AR）</h3>
      <p style="font-size:14px;color:var(--text-sub);margin-bottom:12px">
        自动从 kivo.wiki 获取最新学生列表，逐个抓取缺失的角色资料（立绘/画廊/语音），清理孤儿目录，最后重建索引。<br>
        也可在服务器控制台输入 <code>AR</code> 触发。
      </p>
      <div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:14px">
        <button class="btn" id="ol-ar">🚀 启动全部资源更新</button>
        <span id="ol-ar-status" style="font-size:14px;color:var(--text-sub)"></span>
      </div>
      <div id="ol-ar-log" style="font-size:13px;color:var(--text-sub);white-space:pre-wrap;max-height:200px;overflow:auto"></div>
    </div>

    <div class="tab-panel" style="margin-top:22px">
      <h3 style="color:var(--primary);margin-bottom:10px">📇 学生资料全量同步（技能 / 属性 / 数值）</h3>
      <p style="font-size:14px;color:var(--text-sub);margin-bottom:12px">
        从 kivo.wiki 逐个拉取完整学生档案：技能（EX / 普通 / 被动 / 副技能，含每级说明）、攻击与防御属性、地形适性、基础数值、专属武器、装备、喜好礼物、生日、爱好、画师、声优、各服实装日期与简介。
        档案写入本地索引，离线也能查看；技能图标会一并下载到对应学生目录。
      </p>
      <div class="pf-sync-bar">
        <button class="btn" id="ol-pf-sync">📇 全量同步学生资料</button>
        <span id="ol-pf-status" style="font-size:14px;color:var(--text-sub)"></span>
      </div>
      <div class="pf-sync-progress"><i id="ol-pf-bar" style="width:0%"></i></div>
      <div id="ol-pf-log" style="font-size:13px;color:var(--text-sub);white-space:pre-wrap;max-height:200px;overflow:auto"></div>
    </div>

    <div class="tab-panel" style="margin-top:22px">
      <h3 style="color:var(--primary);margin-bottom:10px">🩺 完整性体检</h3>
      <p style="font-size:14px;color:var(--text-sub);margin-bottom:12px">
        逐个学生检查：目录、信息、立绘、画廊、语音、记忆大厅图、Spine。只补缺失部分，已存在的文件不会重复下载。<br>
        上游 kivo.wiki 本来就没有的资源（例如部分 NPC 没有语音）不会算作缺失，体检结果 7 天内有效。
      </p>
      <div style="display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin-bottom:14px">
        <button class="btn" id="ol-audit">🔍 开始体检</button>
        <button class="btn ghost" id="ol-repair" disabled>🛠 修复选中</button>
        <button class="btn ghost" id="ol-repair-all" disabled>🛠 一键修复全部</button>
        <span id="ol-audit-status" style="font-size:14px;color:var(--text-sub)"></span>
      </div>
      <div id="ol-audit-list" class="music-list" style="max-height:420px;overflow:auto"></div>
      <div id="ol-repair-log" style="margin-top:12px;font-size:13px;color:var(--text-sub);white-space:pre-wrap"></div>
    </div>

    <div class="tab-panel" style="margin-top:22px">
      <h3 style="color:var(--primary);margin-bottom:10px">🎞️ 主题曲 MV 补全</h3>
      <p style="font-size:14px;color:var(--text-sub);margin-bottom:12px">
        扫描 <b>03_游戏音乐</b> 目录，找出带 BV 号但尚未下载 MV 视频的曲目，调用 <b>BBDown.exe</b> 自动下载。
      </p>
      <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:14px">
        <button class="btn" id="ol-check">🔍 扫描可下载 MV</button>
        <button class="btn" id="ol-dl-selected" disabled>⬇️ 下载选中项</button>
        <button class="btn" id="ol-dl-all" disabled>⬇️ 全部下载</button>
      </div>
      <div id="ol-list" class="music-list" style="max-height:400px;overflow:auto"></div>
      <div id="ol-progress" style="margin-top:12px;font-size:14px;color:var(--text-sub)"></div>
    </div>

    <div class="tab-panel" style="margin-top:22px">
      <h3 style="color:var(--primary);margin-bottom:10px">📥 从 kivo.wiki 拉取新数据</h3>
      <p style="font-size:14px;color:var(--text-sub);margin-bottom:12px">
        从 kivo.wiki 抓取最新角色列表，与本地对比，列出尚未收录的角色。可选择抓取其立绘、画廊、语音到本地。
      </p>
      <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:14px">
        <button class="btn" id="ol-kivo-check">🔍 检查 kivo.wiki 新角色</button>
        <button class="btn" id="ol-kivo-fetch" disabled>⬇️ 抓取选中角色资料</button>
      </div>
      <div id="ol-kivo-list" class="music-list" style="max-height:400px;overflow:auto"></div>
      <div id="ol-kivo-log" style="margin-top:12px;font-size:13px;color:var(--text-sub);white-space:pre-wrap"></div>
    </div>

    <div class="tab-panel" style="margin-top:22px">
      <h3 style="color:var(--primary);margin-bottom:10px">🧹 kivo.wiki 学生清理</h3>
      <p style="font-size:14px;color:var(--text-sub);margin-bottom:12px">
        从 kivo.wiki 获取最新学生列表，找出本地有但 kivo.wiki 已经没有的孤儿目录（可能是已删除/重编号的角色）。
        可勾选删除以保持本地数据与 kivo.wiki 一致。删除后请重建索引。
      </p>
      <div style="display:flex;gap:8px;flex-wrap:wrap;margin-bottom:14px">
        <button class="btn" id="ol-cleanup-check">🔍 检查孤儿目录</button>
        <button class="btn" id="ol-cleanup-delete" disabled>🗑️ 删除选中项</button>
      </div>
      <div id="ol-cleanup-list" class="music-list" style="max-height:400px;overflow:auto"></div>
      <div id="ol-cleanup-log" style="margin-top:12px;font-size:13px;color:var(--text-sub);white-space:pre-wrap"></div>
    </div>

    <div class="tab-panel" style="margin-top:22px">
      <h3 style="color:var(--primary);margin-bottom:10px">🔄 重建本地索引</h3>
      <p style="font-size:14px;color:var(--text-sub);margin-bottom:12px">
        下载新资源后，重建索引让新内容出现在各页面。
      </p>
      <button class="btn" id="ol-reindex">🔄 重建索引（后台）</button>
    </div>
  </div>`;

  if(EXTERNAL && !isAdminRole()){
    app.querySelectorAll('#app .tab-panel').forEach(p=>p.classList.add('hidden'));
    const note = document.createElement('div');
    note.className = 'tab-panel';
    note.innerHTML = '<h3 style="color:var(--primary);margin-bottom:8px">🔒 只读查看</h3>' +
      '<p style="font-size:14px;line-height:1.9;color:var(--text-sub)">您的账号是普通用户，在线更新的下载 / 抓取 / 清理等操作仅限管理员使用。<br>如需更新资源，请联系服务器管理员操作。</p>';
    app.querySelector('#app .wrap').appendChild(note);
  }

  $('#ol-check').onclick = async ()=>{
    $('#ol-list').innerHTML = '<div class="loading">扫描中……</div>';
    try{
      const d = await fetch('/api/online/check?t='+BUILD).then(r=>r.json());
      onlineCheck = d;
      if(!d.items || d.items.length===0){
        $('#ol-list').innerHTML = '<div class="empty"><div class="big">✅</div>所有带 BV 号的曲目均已下载 MV，或没有可下载的项目。</div>';
        $('#ol-dl-selected').disabled = true; $('#ol-dl-all').disabled = true; return;
      }
      const items = [...d.items].sort((a,b)=>b.score-a.score);
      $('#ol-list').innerHTML = items.map((it,i)=>`
        <div class="music-row" style="cursor:pointer">
          <input type="checkbox" class="ol-cb" data-dir="${esc(it.dir)}" checked>
          <div class="music-main" style="flex:1">
            <div class="music-name">${esc(it.title||it.dir)}</div>
            <div class="music-artist">${esc(it.use||'')}${it.bv?' · BV:'+esc(it.bv):''}</div>
          </div>
          <div class="music-dur">${it.isTheme?'<span style="color:var(--accent)">主题曲</span>':'曲目'}</div>
        </div>`).join('');
      $('#ol-dl-selected').disabled = false;
      $('#ol-dl-all').disabled = false;
    }catch(e){ $('#ol-list').innerHTML = '<div class="empty">扫描失败：'+esc(e.message)+'</div>'; }
  };

  async function doDownload(dirs){
    $('#ol-progress').textContent = '正在启动下载任务……';
    $('#ol-dl-selected').disabled = true; $('#ol-dl-all').disabled = true;
    try{
      const r = await fetch('/api/online/download?t='+BUILD, {
        method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({dirs})
      }).then(r=>r.json());
      if(r.error){ $('#ol-progress').textContent = '错误：'+r.error; $('#ol-dl-selected').disabled=false; $('#ol-dl-all').disabled=false; return; }
      $('#ol-progress').textContent = '下载任务已启动，共 '+r.total+' 项。正在轮询进度……';
      pollStatus();
    }catch(e){ $('#ol-progress').textContent = '请求失败：'+e.message; }
  }
  $('#ol-dl-selected').onclick = ()=>{
    const dirs = [...document.querySelectorAll('.ol-cb:checked')].map(c=>c.dataset.dir);
    if(!dirs.length){ toast('请先选择要下载的项目'); return; }
    doDownload(dirs);
  };
  $('#ol-dl-all').onclick = ()=>{
    const dirs = [...document.querySelectorAll('.ol-cb')].map(c=>c.dataset.dir);
    doDownload(dirs);
  };

  function pollStatus(){
    fetch('/api/online/status?t='+BUILD).then(r=>r.json()).then(d=>{
      const s = d.status;
      if(s==='running'){
        $('#ol-progress').textContent = `下载中…… ${d.done}/${d.total} 完成，${d.fail} 失败 · 当前：${d.current||''}`;
        setTimeout(pollStatus, 1500);
      } else if(s==='done'){
        $('#ol-progress').textContent = `✅ 下载完成：成功 ${d.done}，失败 ${d.fail}`;
        $('#ol-dl-selected').disabled = false; $('#ol-dl-all').disabled = false;
      } else {
        setTimeout(pollStatus, 1500);
      }
    }).catch(()=>setTimeout(pollStatus, 2000));
  }

  let kivoList = [];
  $('#ol-kivo-check').onclick = async ()=>{
    $('#ol-kivo-list').innerHTML = '<div class="loading">正在从 kivo.wiki 获取角色列表……</div>';
    try{
      const d = await fetch('/api/online/kivo-characters?t='+BUILD).then(r=>r.json());
      if(d.error){ $('#ol-kivo-list').innerHTML='<div class="empty">'+esc(d.error)+'</div>'; return; }
      kivoList = d.items || [];
      const newOnes = kivoList.filter(x=>!x.local);
      if(newOnes.length===0){
        $('#ol-kivo-list').innerHTML = '<div class="empty"><div class="big">✅</div>本地角色已是最新，kivo.wiki 上没有新角色。</div>';
        return;
      }
      $('#ol-kivo-list').innerHTML = newOnes.map((s,i)=>`
        <div class="music-row">
          <input type="checkbox" class="kivo-cb" data-id="${s.id}" checked>
          <div class="music-main" style="flex:1">
            <div class="music-name">${esc(s.cn)}${s.jp?' · '+esc(s.jp):''}</div>
            <div class="music-artist">ID ${s.id} · kivo.wiki</div>
          </div>
        </div>`).join('');
      $('#ol-kivo-fetch').disabled = false;
    }catch(e){ $('#ol-kivo-list').innerHTML='<div class="empty">获取失败：'+esc(e.message)+'</div>'; }
  };

  $('#ol-kivo-fetch').onclick = async ()=>{
    const ids = [...document.querySelectorAll('.kivo-cb:checked')].map(c=>c.dataset.id);
    if(!ids.length){ toast('请先选择要抓取的角色'); return; }
    $('#ol-kivo-fetch').disabled = true;
    $('#ol-kivo-log').textContent = '开始抓取 '+ids.length+' 个角色……\n';
    for(const id of ids){
      try{
        const r = await fetch('/api/online/kivo-fetch?id='+encodeURIComponent(id)+'&t='+BUILD, {method:'POST'}).then(r=>r.json());
        if(r.error) $('#ol-kivo-log').textContent += `[${id}] 失败：${r.error}\n`;
        else $('#ol-kivo-log').textContent += `[${id}] ${r.cn||id} 完成：立绘 ${r.illus||0}，画廊 ${r.gallery||0}，语音 ${r.voices||0}\n`;
      }catch(e){ $('#ol-kivo-log').textContent += `[${id}] 异常：${e.message}\n`; }
    }
    $('#ol-kivo-log').textContent += '\n全部完成。建议点击下方「重建索引」让新内容生效。';
    $('#ol-kivo-fetch').disabled = false;
  };

  function fmtSize(b){ if(b<1024) return b+' B'; if(b<1048576) return (b/1024).toFixed(1)+' KB'; if(b<1073741824) return (b/1048576).toFixed(1)+' MB'; return (b/1073741824).toFixed(2)+' GB'; }
  $('#ol-cleanup-check').onclick = async ()=>{
    $('#ol-cleanup-list').innerHTML = '<div class="loading">正在从 kivo.wiki 获取学生列表并对比本地……</div>';
    try{
      const d = await fetch('/api/online/kivo-cleanup?t='+BUILD).then(r=>r.json());
      if(d.error){ $('#ol-cleanup-list').innerHTML='<div class="empty">'+esc(d.error)+'</div>'; return; }
      if(!d.items || d.items.length===0){
        $('#ol-cleanup-list').innerHTML = '<div class="empty"><div class="big">✅</div>本地学生目录与 kivo.wiki 一致，没有孤儿目录。<br>kivo.wiki 共 '+d.remoteCount+' 名角色，本地 '+d.localCount+' 个目录。</div>';
        $('#ol-cleanup-delete').disabled = true;
        return;
      }
      $('#ol-cleanup-list').innerHTML = d.items.map(it=>`
        <div class="music-row" style="cursor:pointer">
          <input type="checkbox" class="cleanup-cb" data-dir="${esc(it.dir)}" checked>
          <div class="music-main" style="flex:1">
            <div class="music-name">${esc(it.cn||it.dir)} <small style="color:var(--text-sub)">ID ${it.id}</small></div>
            <div class="music-artist">目录：${esc(it.dir)} · ${it.fileCount} 个文件 · ${fmtSize(it.size)}</div>
          </div>
          <div class="music-dur">kivo.wiki 无此ID</div>
        </div>`).join('');
      $('#ol-cleanup-log').textContent = 'kivo.wiki 共 '+d.remoteCount+' 名角色，本地 '+d.localCount+' 个目录，其中 '+d.items.length+' 个孤儿目录待清理。';
      $('#ol-cleanup-delete').disabled = false;
    }catch(e){ $('#ol-cleanup-list').innerHTML='<div class="empty">检查失败：'+esc(e.message)+'</div>'; }
  };
  $('#ol-cleanup-delete').onclick = async ()=>{
    const dirs = [...document.querySelectorAll('.cleanup-cb:checked')].map(c=>c.dataset.dir);
    if(!dirs.length){ toast('请先勾选要删除的目录'); return; }
    if(!confirm('确定删除 '+dirs.length+' 个学生目录吗？\n此操作不可恢复，将永久删除本地文件。')) return;
    $('#ol-cleanup-delete').disabled = true;
    $('#ol-cleanup-log').textContent = '正在删除 '+dirs.length+' 个目录……\n';
    try{
      const r = await fetch('/api/online/kivo-cleanup?t='+BUILD, {
        method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({dirs})
      }).then(r=>r.json());
      if(r.error){ $('#ol-cleanup-log').textContent += '删除失败：'+r.error+'\n'; }
      else{
        $('#ol-cleanup-log').textContent += '已删除 '+r.deleted+' 个目录，释放 '+fmtSize(r.freedBytes)+' 空间。\n';
        if(r.errors && r.errors.length) r.errors.forEach(e=> $('#ol-cleanup-log').textContent += '  [错误] '+e.dir+': '+e.error+'\n');
        $('#ol-cleanup-log').textContent += '\n请点击下方「重建索引」让变更生效。';
        toast('已删除 '+r.deleted+' 个目录');
      }
    }catch(e){ $('#ol-cleanup-log').textContent += '异常：'+e.message+'\n'; }
    $('#ol-cleanup-delete').disabled = false;
  };

  $('#ol-reindex').onclick = async ()=>{
    try{
      const r = await fetch('/api/reindex?t='+BUILD, {method:'POST'}).then(r=>r.json());
      toast(r.msg || r.message || '已触发重建');
    }catch(e){ toast('重建失败：'+e.message); }
  };

  let arTimer = null;
  $('#ol-ar').onclick = async ()=>{
    if($('#ol-ar').disabled) return;
    if(!confirm('确定启动全部资源更新吗？\n将从 kivo.wiki 抓取所有缺失角色并重建索引，可能需要较长时间。')) return;
    $('#ol-ar').disabled = true;
    $('#ol-ar').textContent = '更新中…';
    $('#ol-ar-log').textContent = '正在启动…\n';
    try{
      await fetch('/api/online/auto-update?t='+BUILD).then(r=>r.json());
    }catch(e){}

    if(arTimer) clearInterval(arTimer);
    arTimer = setInterval(async ()=>{
      try{
        const d = await fetch('/api/online/auto-update?t='+BUILD).then(r=>r.json());
        $('#ol-ar-status').textContent = d.running ? (d.status || '运行中') + (d.total?` (${d.done||0}/${d.total})`:'') : '空闲';
        if(d.running && d.status) $('#ol-ar-log').textContent = d.status + '\n' + $('#ol-ar-log').textContent.split('\n').slice(0,30).join('\n');
        if(!d.running){
          clearInterval(arTimer); arTimer = null;
          $('#ol-ar').disabled = false;
          $('#ol-ar').textContent = '🚀 启动全部资源更新';
          toast('全部资源更新已完成');
        }
      }catch(e){}
    }, 2000);
  };

  let auditItems = [];
  const LACK = { info:'信息', illus:'立绘', gallery:'画廊', voice:'语音', lobby:'大厅图', spine:'Spine' };
  function lackOf(it){
    if(!it.exists) return ['目录'];
    const out = [];
    Object.keys(LACK).forEach(k => { if(!it[k]) out.push(LACK[k]); });
    return out;
  }
  function renderAudit(d){
    auditItems = (d.items || []).filter(it => lackOf(it).length > 0);
    $('#ol-audit-status').textContent = `远端 ${d.total} · 本地 ${d.localCount} · 完整 ${d.completeCount} · 待补全 ${d.incompleteCount}`;
    if(!auditItems.length){
      $('#ol-audit-list').innerHTML = '<div class="empty"><div class="big">✅</div>全部学生资源完整，没有需要修复的项目。</div>';
      $('#ol-repair').disabled = true; $('#ol-repair-all').disabled = true;
      return;
    }
    $('#ol-audit-list').innerHTML = auditItems.map(it => {
      const lack = lackOf(it);
      return `<div class="music-row">
        <input type="checkbox" class="rep-cb" data-id="${it.id}" checked>
        <div class="music-main" style="flex:1">
          <div class="music-name">${esc(it.name || it.dir || ('ID ' + it.id))} <small style="color:var(--text-sub)">ID ${it.id}</small></div>
          <div class="music-artist" style="color:var(--accent)">缺：${esc(lack.join('、'))}</div>
        </div>
        <div class="music-dur">${it.exists ? '补全' : '新建'}</div>
      </div>`;
    }).join('');
    $('#ol-repair').disabled = false;
    $('#ol-repair-all').disabled = false;
  }
  $('#ol-audit').onclick = async ()=>{
    $('#ol-audit').disabled = true;
    $('#ol-audit-list').innerHTML = '<div class="loading">正在逐项体检，大约需要 10 秒……</div>';
    $('#ol-audit-status').textContent = '';
    try{
      const d = await fetch('/api/online/audit?t=' + Date.now()).then(r => r.json());
      if(d.error){ $('#ol-audit-list').innerHTML = '<div class="empty">体检失败：' + esc(d.error) + '</div>'; return; }
      renderAudit(d);
    }catch(e){ $('#ol-audit-list').innerHTML = '<div class="empty">体检失败：' + esc(e.message) + '</div>'; }
    $('#ol-audit').disabled = false;
  };
  async function startRepair(body){
    $('#ol-repair').disabled = true; $('#ol-repair-all').disabled = true; $('#ol-audit').disabled = true;
    $('#ol-repair-log').textContent = '正在启动修复…\n';
    try{
      const r = await fetch('/api/online/repair', {
        method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body)
      }).then(r => r.json());
      if(r.error){ $('#ol-repair-log').textContent += '启动失败：' + r.error + '\n'; return; }
      pollRepair();
    }catch(e){ $('#ol-repair-log').textContent += '请求失败：' + e.message + '\n'; }
  }
  $('#ol-repair').onclick = ()=>{
    const ids = [...document.querySelectorAll('.rep-cb:checked')].map(c => parseInt(c.dataset.id));
    if(!ids.length){ toast('请先勾选要修复的学生'); return; }
    startRepair({ ids });
  };
  $('#ol-repair-all').onclick = ()=>{
    const n = auditItems.length;
    if(!confirm('修复全部 ' + n + ' 个待补全学生？\n将下载缺失的语音、大厅图与 Spine，已存在的文件会跳过。')) return;
    startRepair({ all: true });
  };
  function pollRepair(){
    fetch('/api/online/repair-status?t=' + Date.now()).then(r => r.json()).then(d => {
      if(d.running){
        $('#ol-repair-log').textContent = (d.status || '运行中') + (d.total ? `（${d.done}/${d.total}）` : '') + '\n' + $('#ol-repair-log').textContent.split('\n').slice(0, 40).join('\n');
        setTimeout(pollRepair, 1500);
      } else {
        $('#ol-repair-log').textContent = (d.status || '修复结束') + '\n' + $('#ol-repair-log').textContent.split('\n').slice(0, 20).join('\n');
        $('#ol-audit').disabled = false; $('#ol-repair').disabled = false; $('#ol-repair-all').disabled = false;
        toast('修复结束，正在刷新索引');
      }
    }).catch(() => setTimeout(pollRepair, 2500));
  }

  if(pfSyncTimer){ clearInterval(pfSyncTimer); pfSyncTimer = null; }
  function pfPoll(){
    const bar = $('#ol-pf-bar');
    if(!bar) return;
    fetch('/api/online/profile-status?t=' + Date.now()).then(r => r.json()).then(d => {
      const pct = d.total ? Math.round(d.done / d.total * 100) : (d.localCount ? 100 : 0);
      bar.style.width = pct + '%';
      const st = $('#ol-pf-status'), lg = $('#ol-pf-log');
      if(st) st.textContent = (d.status || '') + (d.total ? `（${d.done}/${d.total}，失败 ${d.fail}）` : '');
      if(lg && d.log && d.log.length) lg.textContent = d.log.slice(-80).join('\n');
      if(!d.running && pfSyncTimer){
        clearInterval(pfSyncTimer); pfSyncTimer = null;
        const b = $('#ol-pf-sync'); if(b) b.disabled = false;
      }
    }).catch(()=>{});
  }
  pfPoll();
  const pfBtn = $('#ol-pf-sync');
  if(pfBtn) pfBtn.onclick = async ()=>{
    pfBtn.disabled = true;
    try{
      const r = await fetch('/api/online/profile-sync', {
        method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({all:true})
      }).then(r => r.json());
      if(r.error){ toast(r.error); pfBtn.disabled = false; return; }
      if(!pfSyncTimer) pfSyncTimer = setInterval(pfPoll, 1500);
      pfPoll();
    }catch(e){ toast('启动失败：' + e.message); pfBtn.disabled = false; }
  };
}

(function initLoginGate(){
  const f = $('#lg-form');
  if(f) f.addEventListener('submit', submitLogin);
})();
initApp();

(function initAiWidget(){
  const panel = document.createElement('div');
  panel.id = 'ai-panel'; panel.className = 'ai-hide';
  panel.innerHTML = '<div class="ai-hd"><b>Koyuki</b><span>运维 / 资料 / 音频</span><button id="ai-close">×</button></div>' +
    '<div id="ai-msgs"></div>' +
    '<div class="ai-in"><input id="ai-text" placeholder="问点什么…"><button id="ai-go">发送</button></div>';
  document.body.appendChild(panel);

  const msgs = panel.querySelector('#ai-msgs');
  const input = panel.querySelector('#ai-text');
  const go = panel.querySelector('#ai-go');
  let busy = false, histLoaded = false;

  function bubble(cls, text){
    const el = document.createElement('div');
    el.className = 'm ' + cls;
    el.textContent = text;
    msgs.appendChild(el);
    msgs.scrollTop = msgs.scrollHeight;
    return el;
  }

  async function restoreHistory(){
    if(histLoaded) return;
    histLoaded = true;
    try{
      const who = (typeof SETTINGS !== 'undefined' && SETTINGS.me) ? SETTINGS.me : '';
      if(!who) return;
      const r = await fetch('/api/ai/state?user=' + encodeURIComponent(who) + '&t=' + Date.now());
      const d = await r.json();
      if(!d || !d.ok || !Array.isArray(d.messages) || !d.messages.length) return;
      const tip = bubble('t', '↺ 已恢复上次对话（' + d.messages.length + ' 条）');
      tip.style.opacity = '.7';
      d.messages.forEach(m => bubble(m.role === 'user' ? 'u' : '', m.content));
    }catch(e){}
  }

  window.openAiPanel = () => {
    panel.classList.remove('ai-hide');
    restoreHistory();
    setTimeout(() => input.focus(), 60);
  };
  panel.querySelector('#ai-close').onclick = () => panel.classList.add('ai-hide');

  async function send(){
    const text = input.value.trim();
    if(!text || busy) return;
    input.value = '';
    bubble('u', text);
    const tip = bubble('t', '思考中…');
    busy = true; go.disabled = true;
    try{
      const user = (typeof SETTINGS !== 'undefined' && SETTINGS.me) ? SETTINGS.me : 'web';
      const r = await fetch('/api/ai/chat', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ user, source: 'web', message: text })
      });
      const d = await r.json();
      tip.remove();
      if(!d.ok) bubble('e', d.error || '请求失败');
      else bubble('', d.reply || '（空回复）');
    }catch(e){
      tip.remove(); bubble('e', '请求失败：' + e.message);
    }
    busy = false; go.disabled = false;
  }
  go.onclick = send;
  input.addEventListener('keydown', e => { if(e.key === 'Enter'){ e.preventDefault(); send(); } });
})();
