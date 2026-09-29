#!/usr/bin/env node
'use strict';
/**
 * kilee-vl-admin v2
 * DGX Spark vLLM 多设备公网网关 + 控制台
 *
 *   /vl/            控制台静态资源（nginx 直出）
 *   /vl/api/*       控制台 API（账号会话鉴权）
 *   /api/vl/*       对外推理入口（API key → 按绑定的设备路由）
 *
 * 推理链路：本服务 → 127.0.0.1:<device.tunnelPort> (SSH 反向隧道) → DGX vLLM
 *
 * v2 要点：
 *   - 多设备：每台 DGX 一个反向隧道回环端口
 *   - API key 绑定设备（key.deviceId），key 决定路由，对外 base URL 不变
 *   - 账号：owner（沿用 admin.json 密码）+ 自助注册 tenant；owner 管理全部
 *   - 配对：/vl/api/devices/pair/start（会话生成码）→ /vl/api/devices/pair（拿码换设备）
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const CFG = {
  host: process.env.VL_HOST || '127.0.0.1',
  port: Number(process.env.VL_PORT || 8811),
  dataDir: process.env.VL_DATA_DIR || '/var/lib/kilee-vl-admin',
  pubDir: path.join(__dirname, 'public'),
  maxBody: 52 * 1024 * 1024,
  model: process.env.VL_MODEL || 'qwen3-vl',
  sessionTtlMs: 12 * 3600 * 1000,
  loginMaxFails: 8,
  loginWindowMs: 10 * 60 * 1000,
  loginBlockMs: 10 * 60 * 1000,
  serverHost: process.env.VL_SERVER_HOST || 'kilee.cn',
  sshPort: Number(process.env.VL_SSH_PORT || 22),
  tunnelUser: process.env.VL_TUNNEL_USER || 'kilee-tunnel',
  portMin: Number(process.env.VL_TUNNEL_PORT_MIN || 8820),
  portMax: Number(process.env.VL_TUNNEL_PORT_MAX || 8899),
  reservedPorts: (process.env.VL_RESERVED_PORTS || '8801,8811').split(',').map(Number),
  ownerUser: process.env.VL_OWNER_USER || 'owner',
  allowRegister: process.env.VL_ALLOW_REGISTER !== '0',
  inviteCode: process.env.VL_INVITE_CODE || '',
  pairTtlMs: 10 * 60 * 1000,
  defaultUpstreamPort: Number(process.env.VL_DEFAULT_UPSTREAM_PORT || 8001),
  maxDevicesPerAccount: Number(process.env.VL_MAX_DEVICES_PER_ACCOUNT || 5),
};

/* ══════════════════════════ 存储 ══════════════════════════ */

const KEYS_FILE = path.join(CFG.dataDir, 'keys.json');
const ADMIN_FILE = path.join(CFG.dataDir, 'admin.json');

const emptyPlayground = () => ({ requests: 0, ok: 0, errors: 0, promptTokens: 0, completionTokens: 0, lastUsedAt: null });
const emptyStore = () => ({ version: 2, accounts: [], devices: [], keys: [], pairings: [], playground: emptyPlayground() });

let store = emptyStore();
let admin = { passwordHash: '', sessionSecret: '', passwordChangedAt: 0 };

function readJson(file, fallback) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return fallback; }
}

function atomicWrite(file, text, mode) {
  const tmp = `${file}.tmp.${process.pid}`;
  fs.writeFileSync(tmp, text, { mode: mode || 0o600 });
  fs.renameSync(tmp, file);
}

let saveTimer = null;
function saveStore() {
  if (saveTimer) return;
  saveTimer = setTimeout(() => {
    saveTimer = null;
    try { atomicWrite(KEYS_FILE, JSON.stringify(store, null, 2)); }
    catch (e) { console.error('[save] keys.json:', e.message); }
  }, 1200);
}
function saveStoreNow() {
  if (saveTimer) { clearTimeout(saveTimer); saveTimer = null; }
  try { atomicWrite(KEYS_FILE, JSON.stringify(store, null, 2)); }
  catch (e) { console.error('[save] keys.json:', e.message); }
}

function loadAll() {
  fs.mkdirSync(CFG.dataDir, { recursive: true });
  const s = readJson(KEYS_FILE, null);
  store = (s && Array.isArray(s.keys)) ? Object.assign(emptyStore(), s) : emptyStore();
  if (!Array.isArray(store.accounts)) store.accounts = [];
  if (!Array.isArray(store.devices)) store.devices = [];
  if (!Array.isArray(store.pairings)) store.pairings = [];
  if (!store.playground || typeof store.playground !== 'object') store.playground = emptyPlayground();

  admin = readJson(ADMIN_FILE, null) || admin;
  if (!admin.passwordHash || !admin.sessionSecret) {
    console.error('FATAL: admin.json 缺失或损坏，请先执行 init-admin.js');
    process.exit(1);
  }
  if (migrate()) saveStoreNow();
}

/* ══════════════════════════ 密码 / 会话 ══════════════════════════ */

function hashPassword(pw) {
  const salt = crypto.randomBytes(16);
  const dk = crypto.scryptSync(pw, salt, 32, { N: 16384, r: 8, p: 1 });
  return ['scrypt', 16384, 8, 1, salt.toString('base64'), dk.toString('base64')].join('$');
}

function verifyPassword(pw, stored) {
  try {
    const [alg, N, r, p, saltB64, hashB64] = String(stored).split('$');
    if (alg !== 'scrypt') return false;
    const dk = crypto.scryptSync(pw, Buffer.from(saltB64, 'base64'), 32, { N: +N, r: +r, p: +p });
    const exp = Buffer.from(hashB64, 'base64');
    return dk.length === exp.length && crypto.timingSafeEqual(dk, exp);
  } catch { return false; }
}

function hmac(body) {
  return crypto.createHmac('sha256', admin.sessionSecret).update(body).digest('base64url');
}

function signSession(sub) {
  const body = Buffer.from(JSON.stringify({ exp: Date.now() + CFG.sessionTtlMs, sub })).toString('base64url');
  return `${body}.${hmac(body)}`;
}

function verifySession(token) {
  if (!token || typeof token !== 'string') return null;
  const i = token.lastIndexOf('.');
  if (i <= 0) return null;
  const body = token.slice(0, i);
  const sig = token.slice(i + 1);
  const expect = hmac(body);
  if (sig.length !== expect.length) return null;
  if (!crypto.timingSafeEqual(Buffer.from(sig), Buffer.from(expect))) return null;
  try {
    const p = JSON.parse(Buffer.from(body, 'base64url').toString('utf8'));
    return (p && p.exp && Date.now() < p.exp && p.sub) ? p : null;
  } catch { return null; }
}

/* ══════════════════════════ 工具 ══════════════════════════ */

const KEY_ALPHABET = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
const PAIR_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';

function randStr(alphabet, n) {
  let s = '';
  for (let i = 0; i < n; i++) s += alphabet[crypto.randomInt(alphabet.length)];
  return s;
}
const newApiKey = () => `vl_${randStr(KEY_ALPHABET, 43)}`;
const newPairCode = () => `${randStr(PAIR_ALPHABET, 4)}-${randStr(PAIR_ALPHABET, 4)}`;
const newToken = () => randStr(KEY_ALPHABET, 43);
const maskKey = (k) => (k.length > 14 ? `${k.slice(0, 7)}…${k.slice(-4)}` : k);

function parseCookies(req) {
  const out = {};
  const raw = req.headers.cookie;
  if (!raw) return out;
  for (const part of raw.split(';')) {
    const eq = part.indexOf('=');
    if (eq < 0) continue;
    out[part.slice(0, eq).trim()] = decodeURIComponent(part.slice(eq + 1).trim());
  }
  return out;
}

function clientIp(req) {
  const xr = req.headers['x-real-ip'];
  if (xr) return String(xr).split(',')[0].trim();
  return (req.socket.remoteAddress || '').replace(/^::ffff:/, '');
}

function sendJson(res, code, obj, extraHeaders) {
  const body = Buffer.from(JSON.stringify(obj));
  res.writeHead(code, Object.assign({
    'content-type': 'application/json; charset=utf-8',
    'content-length': body.length,
    'cache-control': 'no-store',
  }, extraHeaders || {}));
  res.end(body);
}

function readBody(req, limit) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (c) => {
      size += c.length;
      if (size > limit) { reject(Object.assign(new Error('payload too large'), { code: 413 })); req.destroy(); return; }
      chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

async function readJsonBody(req, limit) {
  const buf = await readBody(req, limit || 64 * 1024);
  return JSON.parse(buf.toString('utf8') || '{}');
}

function parseBearer(req) {
  const h = req.headers.authorization || '';
  const m = /^Bearer\s+(.+)$/i.exec(h.trim());
  return m ? m[1].trim() : null;
}

function keyStatus(k) {
  if (k.revokedAt) return 'revoked';
  if (k.expiresAt && Date.now() > k.expiresAt) return 'expired';
  return 'active';
}

function findKey(value) {
  if (!value) return null;
  return store.keys.find((k) => k.key === value && keyStatus(k) === 'active') || null;
}

/* ══════════════════════════ 迁移 v1 → v2 ══════════════════════════ */

function migrate() {
  let changed = false;

  // 旧版单上游链路：把现有 key 挂到一台默认设备（8801）
  if (store.devices.length === 0 && store.keys.length > 0) {
    store.devices.push({
      id: crypto.randomUUID(),
      accountId: 'owner',
      name: 'DGX Spark (本机)',
      tunnelPort: 8801,
      upstreamPort: CFG.defaultUpstreamPort,
      pubkey: '',
      pubkeyFp: '',
      revokedAt: null,
      createdAt: Date.now(),
      lastSeenAt: null,
      models: [],
    });
    changed = true;
  }
  const fallbackDeviceId = store.devices[0] ? store.devices[0].id : null;

  for (const k of store.keys) {
    if (k.accountId === undefined) { k.accountId = 'owner'; changed = true; }
    if (k.deviceId === undefined) { k.deviceId = fallbackDeviceId; changed = true; }
    if (!k.quota || typeof k.quota !== 'object') { k.quota = { maxRequestsPerDay: 0, maxTokensPerDay: 0 }; changed = true; }
  }
  if (store.version !== 2) { store.version = 2; changed = true; }
  return changed;
}

/* ══════════════════════════ 账号 ══════════════════════════ */

function findAccountByUsername(u) {
  const name = String(u || '').trim().toLowerCase();
  if (!name) return null;
  if (name === CFG.ownerUser.toLowerCase()) return { id: 'owner', username: CFG.ownerUser, role: 'owner', passwordHash: admin.passwordHash };
  const a = store.accounts.find((x) => x.username === name);
  return a || null;
}

function publicAccountView(a) {
  return { id: a.id, username: a.username, role: a.role || 'tenant', createdAt: a.createdAt, disabled: !!a.disabled };
}

/** 解析会话主体：owner 或已启用的 tenant 账号 */
function principalOf(sess) {
  if (!sess || !sess.sub) return null;
  if (sess.sub === 'owner') return { id: 'owner', username: CFG.ownerUser, role: 'owner' };
  const a = store.accounts.find((x) => x.id === sess.sub);
  if (!a || a.disabled) return null;
  return { id: a.id, username: a.username, role: a.role || 'tenant' };
}

const isOwner = (p) => !!p && p.role === 'owner';

/* ══════════════════════════ 设备 ══════════════════════════ */

function deviceList(principal) {
  const all = store.devices.slice().sort((a, b) => b.createdAt - a.createdAt);
  return isOwner(principal) ? all : all.filter((d) => d.accountId === principal.id);
}

function accountDevices(accountId) {
  return store.devices.filter((d) => d.accountId === accountId);
}

function findDevice(id) {
  return store.devices.find((d) => d.id === id) || null;
}

function allocTunnelPort() {
  const used = new Set(store.devices.map((d) => d.tunnelPort));
  CFG.reservedPorts.forEach((p) => used.add(p));
  for (let p = CFG.portMin; p <= CFG.portMax; p++) if (!used.has(p)) return p;
  return null;
}

/** 疑似僵尸设备：从未上线超过 1 小时，或最后在线已是 24 小时前 */
const DEVICE_STALE_NEVER_MS = 60 * 60 * 1000;
const DEVICE_STALE_OFFLINE_MS = 24 * 60 * 60 * 1000;
function deviceIsStale(d, online) {
  if (online || d.revokedAt) return false;
  const ref = d.lastSeenAt || d.createdAt || 0;
  if (!ref) return false;
  const limit = d.lastSeenAt ? DEVICE_STALE_OFFLINE_MS : DEVICE_STALE_NEVER_MS;
  return Date.now() - ref > limit;
}

function publicDeviceView(d, health) {
  const online = !!(health && health.healthy);
  return {
    id: d.id,
    name: d.name,
    accountId: d.accountId,
    tunnelPort: d.tunnelPort,
    upstreamPort: d.upstreamPort,
    revoked: !!d.revokedAt,
    online,
    stale: deviceIsStale(d, online),
    models: (health && health.models) || (d.models || []),
    pubkeyFp: d.pubkeyFp || null,
    createdAt: d.createdAt,
    lastSeenAt: d.lastSeenAt || null,
  };
}

/* ══════════════════════════ 配对 ══════════════════════════ */

function createPairing(accountId) {
  const now = Date.now();
  store.pairings = store.pairings.filter((p) => !p.usedAt && p.expiresAt > now);
  const rec = { code: newPairCode(), accountId, createdAt: now, expiresAt: now + CFG.pairTtlMs, usedAt: null };
  store.pairings.push(rec);
  saveStore();
  return rec;
}

function findPairing(code) {
  const now = Date.now();
  const c = String(code || '').trim().toUpperCase();
  return store.pairings.find((p) => p.code === c && !p.usedAt && p.expiresAt > now) || null;
}

function sshFingerprint(pubkey) {
  try {
    const parts = String(pubkey).trim().split(/\s+/);
    if (parts.length < 2) return '';
    const raw = Buffer.from(parts[1], 'base64');
    return 'SHA256:' + crypto.createHash('sha256').update(raw).digest('base64').replace(/=+$/, '');
  } catch { return ''; }
}

function validPubkey(pubkey) {
  const s = String(pubkey || '').trim();
  return /^(ssh-ed25519|ecdsa-sha2-nistp256|ssh-rsa)\s+[A-Za-z0-9+/=]+/.test(s) && s.length < 2048;
}

/* ══════════════════════════ 上游健康检查（按设备） ══════════════════════════ */

const healthCache = new Map(); // deviceId -> { at, data }

function probeDevice(device, force) {
  const cached = healthCache.get(device.id);
  if (!force && cached && Date.now() - cached.at < 10000) return Promise.resolve(cached.data);
  return new Promise((resolve) => {
    const done = (data) => { healthCache.set(device.id, { at: Date.now(), data }); resolve(data); };
    if (!device || device.revokedAt) { done({ healthy: false, models: [] }); return; }
    const req = http.request({
      host: '127.0.0.1', port: device.tunnelPort, path: '/v1/models', method: 'GET', timeout: 4000,
    }, (r) => {
      let b = '';
      r.on('data', (c) => { b += c; });
      r.on('end', () => {
        let models = [];
        try { models = (JSON.parse(b).data || []).map((m) => m.id); } catch { /* ignore */ }
        const healthy = r.statusCode === 200;
        if (healthy) { device.lastSeenAt = Date.now(); device.models = models; }
        done({ healthy, models });
      });
    });
    req.on('error', () => done({ healthy: false, models: [] }));
    req.on('timeout', () => { req.destroy(); done({ healthy: false, models: [] }); });
    req.end();
  });
}

/* ══════════════════════════ 上游转发 ══════════════════════════ */

const HOP_HEADERS = new Set(['connection', 'keep-alive', 'transfer-encoding', 'content-length', 'upgrade', 'trailer', 'proxy-connection']);

function pickUpstreamResponseHeaders(h) {
  const out = {};
  for (const [k, v] of Object.entries(h)) {
    if (HOP_HEADERS.has(k.toLowerCase())) continue;
    out[k] = v;
  }
  return out;
}

function extractSseUsage(text) {
  let idx = text.lastIndexOf('"usage":');
  while (idx >= 0) {
    const brace = text.indexOf('{', idx);
    if (brace >= 0) {
      let depth = 0, end = -1;
      for (let i = brace; i < text.length; i++) {
        if (text[i] === '{') depth++;
        else if (text[i] === '}') { depth--; if (depth === 0) { end = i; break; } }
      }
      if (end > 0) {
        try {
          const obj = JSON.parse(text.slice(brace, end + 1));
          if (obj && typeof obj.total_tokens === 'number') return obj;
        } catch { /* ignore */ }
      }
    }
    idx = text.lastIndexOf('"usage":', idx - 1);
  }
  return null;
}

/**
 * 把请求转发到指定设备的上游 vLLM，并把响应（含 SSE 流）原样回吐。
 * onResult(status, usage) 在上游响应结束后回调，用于统计。
 */
function forwardUpstream(opts) {
  const { res, method, upstreamPort, upstreamPath, body, contentType, onResult } = opts;
  let finished = false;

  const headers = {
    host: `127.0.0.1:${upstreamPort}`,
    accept: opts.accept || 'application/json',
    'user-agent': 'kilee-vl-admin',
  };
  if (body && body.length) {
    headers['content-type'] = contentType || 'application/json';
    headers['content-length'] = body.length;
  }

  const up = http.request({
    host: '127.0.0.1', port: upstreamPort, method, path: upstreamPath, headers,
  }, (upRes) => {
    const status = upRes.statusCode || 502;
    const ct = upRes.headers['content-type'] || '';
    const isSse = /text\/event-stream/i.test(ct);

    res.writeHead(status, pickUpstreamResponseHeaders(upRes.headers));
    const finish = (usage) => {
      if (finished) return;
      finished = true;
      if (onResult) { try { onResult(status, usage); } catch (e) { console.error('[stats]', e.message); } }
    };

    if (isSse) {
      let tail = '';
      upRes.on('data', (chunk) => {
        if (!res.writableEnded) res.write(chunk);
        tail = (tail + chunk.toString('utf8')).slice(-16384);
      });
      upRes.on('end', () => { if (!res.writableEnded) res.end(); finish(extractSseUsage(tail)); });
      upRes.on('error', () => { if (!res.writableEnded) res.end(); finish(null); });
    } else {
      const chunks = [];
      upRes.on('data', (c) => chunks.push(c));
      upRes.on('end', () => {
        const buf = Buffer.concat(chunks);
        if (!res.writableEnded) res.end(buf);
        let usage = null;
        try { usage = JSON.parse(buf.toString('utf8')).usage || null; } catch { /* ignore */ }
        finish(usage);
      });
      upRes.on('error', () => { if (!res.writableEnded) res.end(); finish(null); });
    }
  });

  up.setTimeout(600000, () => up.destroy(new Error('upstream timeout')));
  up.on('error', (e) => {
    console.error('[upstream]', e.message);
    if (!res.headersSent) sendJson(res, 503, { error: { message: `上游不可达（设备隧道是否在线？）: ${e.message}`, type: 'upstream_unavailable', code: 'upstream_unavailable' } });
    else if (!res.writableEnded) res.end();
    if (onResult) { try { onResult(503, null); } catch { /* ignore */ } }
  });

  res.on('close', () => { if (!res.writableEnded) up.destroy(); });

  if (body && body.length) up.write(body);
  up.end();
}

function recordKeyUsage(key, status, usage) {
  key.requests += 1;
  key.lastUsedAt = Date.now();
  if (status >= 200 && status < 400) key.ok += 1; else key.errors += 1;
  if (usage) {
    if (typeof usage.prompt_tokens === 'number') key.promptTokens += usage.prompt_tokens;
    if (typeof usage.completion_tokens === 'number') key.completionTokens += usage.completion_tokens;
  }
  saveStore();
}

function recordPlaygroundUsage(status, usage) {
  const p = store.playground;
  p.requests += 1;
  p.lastUsedAt = Date.now();
  if (status >= 200 && status < 400) p.ok += 1; else p.errors += 1;
  if (usage) {
    if (typeof usage.prompt_tokens === 'number') p.promptTokens += usage.prompt_tokens;
    if (typeof usage.completion_tokens === 'number') p.completionTokens += usage.completion_tokens;
  }
  saveStore();
}

/* ══════════════════════════ 推理入口 /api/vl/* ══════════════════════════ */

const INFERENCE_ROUTES = {
  'POST /api/vl/chat/completions': '/v1/chat/completions',
  'GET /api/vl/models': '/v1/models',
};

const CORS = {
  'access-control-allow-origin': '*',
  'access-control-allow-headers': 'Authorization, Content-Type',
  'access-control-allow-methods': 'GET, POST, OPTIONS',
  'access-control-max-age': '86400',
};

function quotaExceeded(key) {
  const q = key.quota || {};
  if (!q.maxRequestsPerDay && !q.maxTokensPerDay) return false;
  const day = new Date().toISOString().slice(0, 10);
  if (!key.dayStats || key.dayStats.day !== day) key.dayStats = { day, requests: 0, tokens: 0 };
  const s = key.dayStats;
  if (q.maxRequestsPerDay && s.requests >= q.maxRequestsPerDay) return 'requests';
  if (q.maxTokensPerDay && s.tokens >= q.maxTokensPerDay) return 'tokens';
  return false;
}

function bumpDayStats(key, usage) {
  const day = new Date().toISOString().slice(0, 10);
  if (!key.dayStats || key.dayStats.day !== day) key.dayStats = { day, requests: 0, tokens: 0 };
  key.dayStats.requests += 1;
  if (usage) key.dayStats.tokens += (usage.total_tokens || ((usage.prompt_tokens || 0) + (usage.completion_tokens || 0)));
}

function handleInference(req, res, pathname) {
  if (req.method === 'OPTIONS') { res.writeHead(204, CORS); res.end(); return; }

  const key = findKey(parseBearer(req));
  if (!key) {
    sendJson(res, 401, { error: { message: '缺少或无效的 API key（Authorization: Bearer <key>）', type: 'invalid_request_error', code: 'invalid_api_key' } }, CORS);
    return;
  }

  // 归属账号被停用则 key 立即失效（"下线一家"）
  if (key.accountId && key.accountId !== 'owner') {
    const owner = store.accounts.find((a) => a.id === key.accountId);
    if (!owner || owner.disabled) {
      sendJson(res, 403, { error: { message: '该 key 所属账号已停用', type: 'invalid_request_error', code: 'account_disabled' } }, CORS);
      return;
    }
  }

  const device = findDevice(key.deviceId);
  if (!device || device.revokedAt) {
    sendJson(res, 503, { error: { message: '该 key 绑定的设备不存在或已下线（设备暂时不可用）', type: 'upstream_unavailable', code: 'upstream_unavailable' } }, CORS);
    return;
  }

  const over = quotaExceeded(key);
  if (over) {
    sendJson(res, 429, { error: { message: `超出每日配额（${over}）`, type: 'rate_limit_error' } }, CORS);
    return;
  }

  const upstreamPath = INFERENCE_ROUTES[`${req.method} ${pathname}`];
  if (!upstreamPath) {
    sendJson(res, 404, { error: { message: `不支持的路径: ${pathname}`, type: 'invalid_request_error' } }, CORS);
    return;
  }

  const prepare = (buf) => {
    let text = buf.toString('utf8');
    try {
      const obj = JSON.parse(text);
      if (pathname === '/api/vl/chat/completions' && obj && obj.stream === true) {
        if (!obj.stream_options || typeof obj.stream_options !== 'object') obj.stream_options = {};
        if (obj.stream_options.include_usage === undefined) obj.stream_options.include_usage = true;
      }
      text = JSON.stringify(obj);
    } catch { /* 非 JSON 原样透传 */ }
    const body = req.method === 'GET' ? null : Buffer.from(text);
    forwardUpstream({
      res, method: req.method, upstreamPort: device.tunnelPort, upstreamPath, body,
      contentType: req.headers['content-type'] || 'application/json',
      accept: req.headers.accept || '*/*',
      onResult: (status, usage) => { recordKeyUsage(key, status, usage); bumpDayStats(key, usage); },
    });
  };

  if (req.method === 'GET') { prepare(Buffer.alloc(0)); return; }
  readBody(req, CFG.maxBody).then(prepare).catch((e) => {
    sendJson(res, e.code || 400, { error: { message: e.message, type: 'invalid_request_error' } }, CORS);
  });
}

/* ══════════════════════════ 管理 API /vl/api/* ══════════════════════════ */

const loginFails = new Map();

function loginBlocked(ip) {
  const rec = loginFails.get(ip);
  if (!rec) return 0;
  if (rec.blockedUntil && Date.now() < rec.blockedUntil) return Math.ceil((rec.blockedUntil - Date.now()) / 1000);
  if (rec.blockedUntil && Date.now() >= rec.blockedUntil) { loginFails.delete(ip); return 0; }
  return 0;
}

function noteLoginFail(ip) {
  const now = Date.now();
  let rec = loginFails.get(ip);
  if (!rec || now - rec.first > CFG.loginWindowMs) rec = { count: 0, first: now, blockedUntil: 0 };
  rec.count += 1;
  if (rec.count >= CFG.loginMaxFails) rec.blockedUntil = now + CFG.loginBlockMs;
  loginFails.set(ip, rec);
}

const sessionCookie = (token, maxAgeSec) =>
  `vl_sess=${token}; Path=/vl; HttpOnly; Secure; SameSite=Lax; Max-Age=${maxAgeSec}`;

function requireSession(req, res) {
  const sess = verifySession(parseCookies(req).vl_sess);
  const principal = sess ? principalOf(sess) : null;
  if (!principal) { sendJson(res, 401, { error: 'unauthorized' }); return null; }
  if (req.method !== 'GET' && req.headers['x-requested-with'] !== 'vl') {
    sendJson(res, 400, { error: 'missing X-Requested-With header' });
    return null;
  }
  return principal;
}

/** 未鉴权路由：注册 / 登录 / 登出 / 会话查询。返回 true 表示已处理。 */
async function handleAuthRoutes(req, res, pathname) {
  if (pathname === '/vl/api/register' && req.method === 'POST') {
    if (!CFG.allowRegister) { sendJson(res, 403, { error: '本站未开放注册' }); return true; }
    let p;
    try { p = await readJsonBody(req); } catch { sendJson(res, 400, { error: 'bad json' }); return true; }
    if (CFG.inviteCode && String(p.invite || '') !== CFG.inviteCode) { sendJson(res, 403, { error: '邀请码错误' }); return true; }
    const username = String(p.username || '').trim().toLowerCase();
    const password = String(p.password || '');
    if (!/^[a-z0-9_-]{3,32}$/.test(username)) { sendJson(res, 400, { error: '用户名需 3-32 位，仅小写字母/数字/-/_' }); return true; }
    if (username === CFG.ownerUser.toLowerCase()) { sendJson(res, 409, { error: '该用户名不可用' }); return true; }
    if (password.length < 10) { sendJson(res, 400, { error: '密码至少 10 位' }); return true; }
    if (store.accounts.some((a) => a.username === username)) { sendJson(res, 409, { error: '用户名已存在' }); return true; }
    const acc = {
      id: crypto.randomUUID(), username, passwordHash: hashPassword(password),
      role: 'tenant', createdAt: Date.now(), disabled: false,
    };
    store.accounts.push(acc);
    saveStore();
    console.log('[account] registered', username);
    sendJson(res, 201, { ok: true, account: publicAccountView(acc) });
    return true;
  }

  if (pathname === '/vl/api/login' && req.method === 'POST') {
    const ip = clientIp(req);
    const blocked = loginBlocked(ip);
    if (blocked) { sendJson(res, 429, { error: `尝试次数过多，请 ${blocked} 秒后再试` }); return true; }
    let p;
    try { p = await readJsonBody(req); } catch { sendJson(res, 400, { error: 'bad json' }); return true; }
    const acc = findAccountByUsername(p.username);
    const ok = acc && !acc.disabled && verifyPassword(String(p.password || ''), acc.passwordHash);
    if (!ok) {
      noteLoginFail(ip);
      console.warn('[login] fail', p.username || '', 'from', ip);
      sendJson(res, 401, { error: '用户名或密码错误' });
      return true;
    }
    loginFails.delete(ip);
    const token = signSession(acc.id);
    sendJson(res, 200, { ok: true, me: { username: acc.username, role: acc.role || 'tenant' } },
      { 'set-cookie': sessionCookie(token, Math.floor(CFG.sessionTtlMs / 1000)) });
    return true;
  }

  if (pathname === '/vl/api/logout' && req.method === 'POST') {
    sendJson(res, 200, { ok: true }, { 'set-cookie': sessionCookie('', 0) });
    return true;
  }

  if (pathname === '/vl/api/session' && req.method === 'GET') {
    const sess = verifySession(parseCookies(req).vl_sess);
    const principal = sess ? principalOf(sess) : null;
    sendJson(res, principal ? 200 : 401, {
      authenticated: !!principal,
      me: principal ? { username: principal.username, role: principal.role } : null,
      registerEnabled: CFG.allowRegister,
      inviteRequired: !!CFG.inviteCode,
    });
    return true;
  }

  return false;
}

async function handleAdminApi(req, res, pathname, query, host) {
  if (await handleAuthRoutes(req, res, pathname)) return;
  if (await handlePairRegister(req, res, pathname)) return;

  const principal = requireSession(req, res);
  if (!principal) return;

  if (await handleDeviceRoutes(req, res, pathname, principal, host)) return;
  if (await handleKeyRoutes(req, res, pathname, principal)) return;
  if (await handleMiscRoutes(req, res, pathname, principal, host)) return;

  sendJson(res, 404, { error: `未知管理接口: ${pathname}` });
}

/* ══════════════════════════ 服务器 SSH 主机密钥指纹 ══════════════════════════ */

const HOST_KEY_FILES = ['/etc/ssh/ssh_host_ed25519_key.pub', '/etc/ssh/ssh_host_ecdsa_key.pub', '/etc/ssh/ssh_host_rsa_key.pub'];
let hostKeyCache = null;
function serverHostKey() {
  if (hostKeyCache) return hostKeyCache;
  const lines = [];
  for (const f of HOST_KEY_FILES) {
    try {
      const t = fs.readFileSync(f, 'utf8').trim();
      if (t) lines.push(`${CFG.serverHost} ${t}`);
    } catch { /* ignore */ }
  }
  hostKeyCache = {
    lines,                                       // 写入 known_hosts 的多行（各密钥类型）
    fp: process.env.VL_HOST_KEY_FP || (lines[0] ? sshFingerprint(lines[0]) : ''),
  };
  return hostKeyCache;
}

/* ══════════════════════════ 设备配对注册（未鉴权，配对码即凭据） ══════════════════════════ */

async function handlePairRegister(req, res, pathname) {
  if (pathname !== '/vl/api/devices/pair' || req.method !== 'POST') return false;
  let p;
  try { p = await readJsonBody(req); } catch { sendJson(res, 400, { error: 'bad json' }); return true; }

  const pairing = findPairing(p.code);
  if (!pairing) { sendJson(res, 404, { error: '配对码无效或已过期' }); return true; }
  if (!validPubkey(p.pubkey)) { sendJson(res, 400, { error: '公钥格式不正确' }); return true; }

  const accountId = pairing.accountId;
  const fp = sshFingerprint(p.pubkey);
  const upstreamPort = Number(p.upstreamPort) || CFG.defaultUpstreamPort;
  let dev = store.devices.find((d) => d.pubkeyFp === fp && d.accountId === accountId);

  // 同一把公钥只能属于一个账号：否则 authorized_keys 会出现重复行，
  // 而 sshd 对同一把公钥只认第一条匹配行 —— 后配的设备端口永远连不上（表现为「一直离线」）。
  if (!dev) {
    const clash = store.devices.find((d) => d.pubkeyFp === fp && d.accountId !== accountId);
    if (clash) {
      const acc = clash.accountId === 'owner'
        ? CFG.ownerUser
        : ((store.accounts.find((a) => a.id === clash.accountId) || {}).username || '其它账号');
      sendJson(res, 409, {
        error: `这台机器的公钥已经绑定到账号「${acc}」${clash.name ? `（设备：${clash.name}）` : ''}，同一台 DGX Spark 只能属于一个账号。`
          + `如需改绑，请先登录「${acc}」在控制台删除该设备，再重新配对。`,
        code: 'pubkey_in_use',
      });
      return true;
    }
  }

  if (dev) {
    dev.name = String(p.hostname || dev.name || 'DGX Spark').slice(0, 64) || dev.name;
    dev.upstreamPort = upstreamPort;
    dev.revokedAt = null;
    dev.pubkey = String(p.pubkey).trim();
    if (Array.isArray(p.models)) dev.models = p.models.slice(0, 16).map(String);
  } else {
    // 每账号设备数上限：防止同一台机器反复配对（重装/丢密钥）堆积出永远离线的僵尸设备，白占隧道端口
    const owned = store.devices.filter((d) => d.accountId === accountId).length;
    if (owned >= CFG.maxDevicesPerAccount) {
      sendJson(res, 409, {
        error: `该账号的设备数已达上限（${CFG.maxDevicesPerAccount} 台）。请先在控制台删除不再使用的设备（例如长期离线的那台），再重新配对。`,
        code: 'device_limit',
      });
      return true;
    }
    const port = allocTunnelPort();
    if (!port) { sendJson(res, 507, { error: '服务器隧道端口已用尽，请联系管理员' }); return true; }
    dev = {
      id: crypto.randomUUID(), accountId,
      name: String(p.hostname || 'DGX Spark').slice(0, 64) || 'DGX Spark',
      tunnelPort: port, upstreamPort,
      pubkey: String(p.pubkey).trim(), pubkeyFp: fp,
      models: Array.isArray(p.models) ? p.models.slice(0, 16).map(String) : [],
      revokedAt: null, createdAt: Date.now(), lastSeenAt: null,
    };
    store.devices.push(dev);
  }
  pairing.usedAt = Date.now();
  saveStoreNow(); // 立即落盘，供 provisioner 消费
  console.log('[pair] device', dev.id, dev.name, 'tunnel', dev.tunnelPort);

  sendJson(res, 200, {
    deviceId: dev.id,
    deviceName: dev.name,
    serverHost: CFG.serverHost,
    serverUser: CFG.tunnelUser,
    sshPort: CFG.sshPort,
    tunnelPort: dev.tunnelPort,
    upstreamPort: dev.upstreamPort,
    hostKeyFp: serverHostKey().fp,
    hostKeyLine: serverHostKey().lines.join('\n'),
  });
  return true;
}

/* ══════════════════════════ 设备管理（需会话） ══════════════════════════ */

function canTouchDevice(principal, d) {
  return isOwner(principal) || d.accountId === principal.id;
}

async function handleDeviceRoutes(req, res, pathname, principal, host) {
  if (pathname === '/vl/api/devices' && req.method === 'GET') {
    const list = deviceList(principal);
    const health = await Promise.all(list.map((d) => probeDevice(d)));
    sendJson(res, 200, {
      devices: list.map((d, i) => publicDeviceView(d, health[i])),
      serverHost: CFG.serverHost,
      tunnelUser: CFG.tunnelUser,
      defaultUpstreamPort: CFG.defaultUpstreamPort,
      maxDevicesPerAccount: CFG.maxDevicesPerAccount,
    });
    return true;
  }

  if (pathname === '/vl/api/devices/pair/start' && req.method === 'POST') {
    const rec = createPairing(principal.id);
    const cmd = `curl -fsSL https://${host}/vl/agent.sh | sudo bash -s -- --pair ${rec.code} --upstream ${CFG.defaultUpstreamPort}`;
    sendJson(res, 201, {
      code: rec.code, expiresAt: rec.expiresAt, command: cmd,
      serverHost: CFG.serverHost, serverUser: CFG.tunnelUser, sshPort: CFG.sshPort,
    });
    return true;
  }

  const m = /^\/vl\/api\/devices\/([0-9a-f-]{36})\/(revoke|restore|delete|rename)$/.exec(pathname);
  if (m && req.method === 'POST') {
    const d = findDevice(m[1]);
    if (!d || !canTouchDevice(principal, d)) { sendJson(res, 404, { error: '设备不存在' }); return true; }
    const action = m[2];
    if (action === 'revoke') { d.revokedAt = Date.now(); }
    else if (action === 'restore') { d.revokedAt = null; }
    else if (action === 'delete') {
      store.devices = store.devices.filter((x) => x.id !== d.id);
      store.keys.forEach((k) => { if (k.deviceId === d.id) k.deviceId = null; });
    } else if (action === 'rename') {
      let p;
      try { p = await readJsonBody(req); } catch { p = {}; }
      d.name = String(p.name || d.name).slice(0, 64) || d.name;
    }
    saveStoreNow();
    console.log('[device]', action, d.id);
    sendJson(res, 200, { ok: true });
    return true;
  }

  return false;
}

/* ══════════════════════════ API Key（需会话） ══════════════════════════ */

function publicKeyView(k) {
  const dev = findDevice(k.deviceId);
  const today = new Date().toISOString().slice(0, 10);
  return {
    id: k.id, label: k.label, masked: maskKey(k.key), status: keyStatus(k),
    deviceId: k.deviceId || null,
    deviceName: dev ? dev.name : null,
    accountId: k.accountId,
    createdAt: k.createdAt, expiresAt: k.expiresAt, revokedAt: k.revokedAt,
    lastUsedAt: k.lastUsedAt, requests: k.requests, ok: k.ok, errors: k.errors,
    promptTokens: k.promptTokens, completionTokens: k.completionTokens,
    totalTokens: k.promptTokens + k.completionTokens,
    quota: k.quota || { maxRequestsPerDay: 0, maxTokensPerDay: 0 },
    today: (k.dayStats && k.dayStats.day === today) ? k.dayStats : { requests: 0, tokens: 0 },
  };
}

function visibleKeys(principal) {
  return isOwner(principal) ? store.keys : store.keys.filter((k) => k.accountId === principal.id);
}

function resolveDeviceForNewKey(principal, deviceId) {
  if (deviceId) {
    const d = findDevice(deviceId);
    return (d && canTouchDevice(principal, d) && !d.revokedAt) ? d : null;
  }
  const mine = isOwner(principal) ? store.devices : accountDevices(principal.id);
  const active = mine.filter((d) => !d.revokedAt);
  return active[0] || null;
}

async function handleKeyRoutes(req, res, pathname, principal) {
  const host = (req.headers.host || CFG.serverHost).replace(/:\d+$/, '');
  const baseUrl = `https://${host}/api/vl`;

  if (pathname === '/vl/api/keys' && req.method === 'GET') {
    const list = visibleKeys(principal).slice().sort((a, b) => b.createdAt - a.createdAt).map(publicKeyView);
    sendJson(res, 200, { keys: list, baseUrl });
    return true;
  }

  if (pathname === '/vl/api/keys' && req.method === 'POST') {
    let p;
    try { p = await readJsonBody(req); } catch { sendJson(res, 400, { error: 'bad json' }); return true; }
    const dev = resolveDeviceForNewKey(principal, p.deviceId);
    if (!dev) { sendJson(res, 400, { error: '请先添加一台设备（DGX Spark）再创建 key' }); return true; }
    const label = String(p.label || '').trim().slice(0, 64) || '未命名 key';
    const days = Number(p.expiresInDays);
    const now = Date.now();
    const k = {
      id: crypto.randomUUID(), accountId: principal.id, deviceId: dev.id,
      label, key: newApiKey(),
      createdAt: now, expiresAt: Number.isFinite(days) && days > 0 ? now + days * 86400000 : null,
      revokedAt: null, lastUsedAt: null,
      requests: 0, ok: 0, errors: 0, promptTokens: 0, completionTokens: 0,
      quota: { maxRequestsPerDay: 0, maxTokensPerDay: 0 },
    };
    store.keys.push(k);
    saveStore();
    console.log('[key] created', k.id, label, '→ device', dev.name);
    sendJson(res, 201, { key: Object.assign(publicKeyView(k), { plaintext: k.key }) });
    return true;
  }

  const m = /^\/vl\/api\/keys\/([0-9a-f-]{36})(?:\/(revoke|restore|rotate|delete|reset-stats|quota))?$/.exec(pathname);
  if (m && req.method === 'POST') {
    const k = store.keys.find((x) => x.id === m[1]);
    if (!k || (k.accountId !== principal.id && !isOwner(principal))) { sendJson(res, 404, { error: 'key 不存在' }); return true; }
    const action = m[2] || 'noop';
    if (action === 'revoke') { k.revokedAt = Date.now(); }
    else if (action === 'restore') { k.revokedAt = null; if (k.expiresAt && Date.now() > k.expiresAt) k.expiresAt = null; }
    else if (action === 'rotate') { k.key = newApiKey(); k.revokedAt = null; }
    else if (action === 'reset-stats') {
      k.requests = k.ok = k.errors = k.promptTokens = k.completionTokens = 0; k.lastUsedAt = null;
      k.dayStats = { day: new Date().toISOString().slice(0, 10), requests: 0, tokens: 0 };
    } else if (action === 'quota') {
      let p; try { p = await readJsonBody(req); } catch { p = {}; }
      k.quota = {
        maxRequestsPerDay: Math.max(0, Number(p.maxRequestsPerDay) || 0),
        maxTokensPerDay: Math.max(0, Number(p.maxTokensPerDay) || 0),
      };
    } else if (action === 'delete') { store.keys = store.keys.filter((x) => x.id !== k.id); }
    else { sendJson(res, 400, { error: 'unknown action' }); return true; }
    saveStore();
    console.log('[key]', action, k.id);
    sendJson(res, 200, action === 'delete'
      ? { ok: true, deleted: true }
      : { key: Object.assign(publicKeyView(k), action === 'rotate' ? { plaintext: k.key } : {}) });
    return true;
  }

  return false;
}

/* ══════════════════════════ 概览 / 测试台 / 密码 / 账号管理（需会话） ══════════════════════════ */

async function handleMiscRoutes(req, res, pathname, principal, host) {
  if (pathname === '/vl/api/overview' && req.method === 'GET') {
    const list = deviceList(principal);
    const health = await Promise.all(list.map((d) => probeDevice(d)));
    const keys = visibleKeys(principal);
    const active = keys.filter((k) => keyStatus(k) === 'active');
    const sum = (f) => keys.reduce((a, k) => a + (k[f] || 0), 0);
    const models = [...new Set(health.flatMap((h) => h.models || []))];
    sendJson(res, 200, {
      baseUrl: `https://${host}/api/vl`,
      model: models[0] || CFG.model,
      models,
      me: { username: principal.username, role: principal.role },
      devices: list.map((d, i) => publicDeviceView(d, health[i])),
      maxDevicesPerAccount: CFG.maxDevicesPerAccount,
      upstream: { healthy: health.some((h) => h.healthy), models, online: health.filter((h) => h.healthy).length, total: list.length },
      keys: { total: keys.length, active: active.length, revoked: keys.length - active.length },
      totals: { requests: sum('requests'), promptTokens: sum('promptTokens'), completionTokens: sum('completionTokens') },
      playground: store.playground,
      serverTime: Date.now(),
    });
    return true;
  }

  if (pathname === '/vl/api/play' && req.method === 'POST') {
    let buf;
    try { buf = await readBody(req, CFG.maxBody); } catch (e) { sendJson(res, e.code || 400, { error: e.message }); return true; }
    let text = buf.toString('utf8');
    let wantDevice = null;
    try {
      const obj = JSON.parse(text);
      if (obj && obj.stream === true) {
        if (!obj.stream_options || typeof obj.stream_options !== 'object') obj.stream_options = {};
        if (obj.stream_options.include_usage === undefined) obj.stream_options.include_usage = true;
      }
      if (obj && !obj.model) obj.model = CFG.model;
      if (obj) { wantDevice = obj.deviceId; delete obj.deviceId; }
      text = JSON.stringify(obj);
    } catch { /* ignore */ }
    const dev = resolveDeviceForNewKey(principal, wantDevice);
    if (!dev) { sendJson(res, 503, { error: { message: '还没有可用设备（先在控制台添加一台 DGX Spark）', type: 'upstream_unavailable', code: 'upstream_unavailable' } }); return true; }
    forwardUpstream({
      res, method: 'POST', upstreamPort: dev.tunnelPort, upstreamPath: '/v1/chat/completions',
      body: Buffer.from(text), contentType: 'application/json',
      onResult: (status, usage) => recordPlaygroundUsage(status, usage),
    });
    return true;
  }

  if (pathname === '/vl/api/password' && req.method === 'POST') {
    let p;
    try { p = await readJsonBody(req); } catch { sendJson(res, 400, { error: 'bad json' }); return true; }
    const acc = isOwner(principal) ? null : store.accounts.find((a) => a.id === principal.id);
    const stored = isOwner(principal) ? admin.passwordHash : (acc ? acc.passwordHash : '');
    if (!verifyPassword(String(p.current || ''), stored)) { sendJson(res, 401, { error: '当前密码错误' }); return true; }
    const next = String(p.next || '');
    if (next.length < 10) { sendJson(res, 400, { error: '新密码至少 10 位' }); return true; }
    if (isOwner(principal)) {
      admin.passwordHash = hashPassword(next);
      admin.sessionSecret = crypto.randomBytes(32).toString('base64url');
      admin.passwordChangedAt = Date.now();
      atomicWrite(ADMIN_FILE, JSON.stringify(admin, null, 2), 0o600);
    } else {
      acc.passwordHash = hashPassword(next);
      saveStoreNow();
    }
    const token = signSession(principal.id);
    sendJson(res, 200, { ok: true }, { 'set-cookie': sessionCookie(token, Math.floor(CFG.sessionTtlMs / 1000)) });
    console.log('[account] password changed', principal.username);
    return true;
  }

  if (isOwner(principal) && pathname === '/vl/api/accounts' && req.method === 'GET') {
    const accounts = store.accounts.map((a) => Object.assign(publicAccountView(a), {
      devices: accountDevices(a.id).length,
      keys: store.keys.filter((k) => k.accountId === a.id).length,
    }));
    sendJson(res, 200, { accounts });
    return true;
  }

  const am = /^\/vl\/api\/accounts\/([0-9a-f-]{36})\/(disable|enable|delete)$/.exec(pathname);
  if (isOwner(principal) && am && req.method === 'POST') {
    const a = store.accounts.find((x) => x.id === am[1]);
    if (!a) { sendJson(res, 404, { error: '账号不存在' }); return true; }
    if (am[2] === 'disable') a.disabled = true;
    else if (am[2] === 'enable') a.disabled = false;
    else if (am[2] === 'delete') {
      store.accounts = store.accounts.filter((x) => x.id !== a.id);
      store.devices = store.devices.filter((d) => d.accountId !== a.id);
      store.keys = store.keys.filter((k) => k.accountId !== a.id);
    }
    saveStoreNow();
    console.log('[account]', am[2], a.username);
    sendJson(res, 200, { ok: true });
    return true;
  }

  return false;
}

/* ══════════════════════════ HTTP 服务 ══════════════════════════ */

const server = http.createServer((req, res) => {
  const started = Date.now();
  let url;
  try { url = new URL(req.url, 'http://internal'); }
  catch { sendJson(res, 400, { error: 'bad url' }); return; }
  const p = url.pathname;
  const host = (req.headers.host || CFG.serverHost).replace(/:\d+$/, '');

  res.on('finish', () => {
    if (p === '/healthz') return;
    console.log(`${new Date().toISOString()} ${clientIp(req)} ${req.method} ${p} ${res.statusCode} ${Date.now() - started}ms`);
  });

  if (p === '/healthz') { sendJson(res, 200, { ok: true }); return; }

  if (p === '/api/vl' || p.startsWith('/api/vl/')) { handleInference(req, res, p); return; }

  if (p === '/vl') { res.writeHead(301, { location: '/vl/' }); res.end(); return; }

  if (p === '/vl/api' || p.startsWith('/vl/api/')) {
    handleAdminApi(req, res, p, url.searchParams, host).catch((e) => {
      console.error('[admin]', (e && e.stack) || e);
      if (!res.headersSent) sendJson(res, 500, { error: 'internal error' });
    });
    return;
  }

  sendJson(res, 404, { error: 'not found' });
});

loadAll();
server.listen(CFG.port, CFG.host, () => {
  console.log(`kilee-vl-admin v2 listening on http://${CFG.host}:${CFG.port}`);
  console.log(`  data dir : ${CFG.dataDir}`);
  console.log(`  accounts : ${store.accounts.length}  devices: ${store.devices.length}  keys: ${store.keys.length}`);
  console.log(`  tunnel   : user=${CFG.tunnelUser} host=${CFG.serverHost} sshPort=${CFG.sshPort} ports=${CFG.portMin}-${CFG.portMax}`);
});

process.on('SIGTERM', () => {
  try { if (saveTimer) { clearTimeout(saveTimer); atomicWrite(KEYS_FILE, JSON.stringify(store, null, 2)); } } catch { /* ignore */ }
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 2000).unref();
});
