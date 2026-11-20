function json(obj, status) {
  return new Response(JSON.stringify(obj), {
    status: status || 200,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' }
  });
}

const enc = new TextEncoder();

function b64url(bytes) {
  const u = new Uint8Array(bytes);
  let s = '';
  for (let i = 0; i < u.length; i++) s += String.fromCharCode(u[i]);
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function fromB64url(str) {
  let s = String(str || '').replace(/-/g, '+').replace(/_/g, '/');
  while (s.length % 4) s += '=';
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function concat(a, b) {
  const out = new Uint8Array(a.length + b.length);
  out.set(a, 0);
  out.set(b, a.length);
  return out;
}

async function hmacSign(secret, msg) {
  const key = await crypto.subtle.importKey('raw', enc.encode(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return b64url(await crypto.subtle.sign('HMAC', key, enc.encode(msg)));
}

function safeEqual(a, b) {
  const x = String(a || ''), y = String(b || '');
  if (x.length !== y.length) return false;
  let r = 0;
  for (let i = 0; i < x.length; i++) r |= x.charCodeAt(i) ^ y.charCodeAt(i);
  return r === 0;
}

async function checkAdmin(request, env) {
  const given = request.headers.get('x-admin-secret') || '';
  return !!env.BA_ADMIN_SECRET && safeEqual(given, env.BA_ADMIN_SECRET);
}

async function parseToken(token, env) {
  const p = String(token || '').trim().split('.');
  if (p.length !== 4 || p[0] !== 'CFK1') throw new Error('bad_token');
  const exp = parseInt(p[2], 10);
  if (!exp) throw new Error('bad_expiry');
  const sig = await hmacSign(env.BA_TOKEN_SECRET, p[1] + '.' + p[2]);
  if (!safeEqual(sig, p[3])) throw new Error('bad_signature');
  if (Math.floor(Date.now() / 1000) > exp) throw new Error('expired');
  if (env.LDTE_KV) {
    const hit = await env.LDTE_KV.get('revoked:' + p[1]);
    if (hit) throw new Error('revoked');
  }
  return { id: p[1], exp };
}

async function decryptClip(bytes, master) {
  if (bytes.length < 101) throw new Error('too_short');
  const magic = String.fromCharCode(bytes[0], bytes[1], bytes[2], bytes[3], bytes[4]);
  if (magic !== 'LDTC1') throw new Error('bad_magic');
  if (bytes[5] !== 1) throw new Error('bad_version');
  const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let off = 6;
  const nonce = bytes.slice(off, off + 12); off += 12;
  const tag = bytes.slice(off, off + 16); off += 16;
  const wnonce = bytes.slice(off, off + 12); off += 12;
  const wtag = bytes.slice(off, off + 16); off += 16;
  const wkey = bytes.slice(off, off + 32); off += 32;
  const len = Number(dv.getBigInt64(off, true)); off += 8;
  if (len <= 0 || off + len > bytes.length) throw new Error('bad_length');
  const ct = bytes.slice(off, off + len);

  const mk = await crypto.subtle.importKey('raw', master, 'AES-GCM', false, ['decrypt']);
  const dkRaw = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: wnonce, tagLength: 128 }, mk, concat(wkey, wtag));
  const dk = await crypto.subtle.importKey('raw', dkRaw, 'AES-GCM', false, ['decrypt']);
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce, tagLength: 128 }, dk, concat(ct, tag));
  return new Uint8Array(plain);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const path = url.pathname.replace(/\/+$/, '') || '/';

    if (request.method === 'OPTIONS') {
      return new Response(null, {
        status: 204,
        headers: {
          'access-control-allow-origin': '*',
          'access-control-allow-headers': 'content-type,x-admin-secret,x-ldte-token',
          'access-control-allow-methods': 'GET,POST,OPTIONS'
        }
      });
    }

    if (path === '/' || path === '/health') {
      return json({ ok: true, service: 'ba-ldte-gate', ts: Date.now() });
    }

    if (path === '/issue' && request.method === 'POST') {
      if (!(await checkAdmin(request, env))) return json({ ok: false, error: 'unauthorized' }, 401);
      let hours = 24;
      try {
        const b = await request.json();
        if (b && b.hours) hours = Math.max(1, Math.min(24, parseInt(b.hours, 10) || 24));
      } catch (e) {}
      const exp = Math.floor(Date.now() / 1000) + hours * 3600;
      const id = crypto.randomUUID().replace(/-/g, '').slice(0, 12);
      const sig = await hmacSign(env.BA_TOKEN_SECRET, id + '.' + exp);
      return json({
        ok: true,
        id,
        hours,
        expires: exp,
        expiresAt: new Date(exp * 1000).toISOString(),
        token: 'CFK1.' + id + '.' + exp + '.' + sig
      });
    }

    if (path === '/verify') {
      try {
        const t = await parseToken(url.searchParams.get('token'), env);
        return json({ ok: true, id: t.id, expires: t.exp, expiresAt: new Date(t.exp * 1000).toISOString() });
      } catch (e) {
        return json({ ok: false, error: String(e && e.message || e) }, 401);
      }
    }

    if (path === '/revoke' && request.method === 'POST') {
      if (!(await checkAdmin(request, env))) return json({ ok: false, error: 'unauthorized' }, 401);
      let id = '';
      try { const b = await request.json(); id = String(b && b.id || ''); } catch (e) {}
      if (!id) return json({ ok: false, error: 'missing_id' }, 400);
      if (env.LDTE_KV) await env.LDTE_KV.put('revoked:' + id, '1', { expirationTtl: 172800 });
      return json({ ok: true, id, persisted: !!env.LDTE_KV });
    }

    if (path === '/decrypt' && request.method === 'POST') {
      try {
        const token = request.headers.get('x-ldte-token') || url.searchParams.get('token');
        const t = await parseToken(token, env);
        const master = fromB64url(env.LDTE_MASTER_KEY);
        if (master.length !== 32) return json({ ok: false, error: 'worker_key_missing' }, 500);
        const buf = await request.arrayBuffer();
        if (buf.byteLength > 32 * 1024 * 1024) return json({ ok: false, error: 'too_large' }, 413);
        const wav = await decryptClip(new Uint8Array(buf), master);
        return new Response(wav, {
          status: 200,
          headers: {
            'content-type': 'audio/wav',
            'cache-control': 'no-store',
            'x-ldte-id': t.id,
            'access-control-allow-origin': '*'
          }
        });
      } catch (e) {
        const msg = String(e && e.message || e);
        const code = msg === 'expired' || msg === 'revoked' || msg === 'bad_signature' || msg === 'bad_token' ? 401 : 400;
        return json({ ok: false, error: msg }, code);
      }
    }

    return json({ ok: false, error: 'not_found' }, 404);
  }
};
