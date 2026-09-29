#!/usr/bin/env node
'use strict';
/**
 * 一次性初始化：生成管理密码 + 会话密钥，迁移旧的隧道 token 为第一个 API key。
 * 用法： node init-admin.js <dataDir> [migrateToken]
 * 只把「密码」打到 stdout，其余走 stderr —— 方便脚本捕获，且不落日志。
 */
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const dir = process.argv[2] || '/var/lib/kilee-vl-admin';
const migrateToken = process.argv[3] || '';

fs.mkdirSync(dir, { recursive: true, mode: 0o750 });

const adminFile = path.join(dir, 'admin.json');
const keysFile = path.join(dir, 'keys.json');

if (fs.existsSync(adminFile)) {
  process.stderr.write('admin.json 已存在，拒绝覆盖（如需重置请先手动备份删除）\n');
  process.exit(1);
}

/* ── 生成易读但高熵的密码：4 组 5 位，避开易混字符 ── */
const AB = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789';
const group = (n) => { let s = ''; for (let i = 0; i < n; i++) s += AB[crypto.randomInt(AB.length)]; return s; };
const password = [group(5), group(5), group(5), group(5)].join('-');

const salt = crypto.randomBytes(16);
const dk = crypto.scryptSync(password, salt, 32, { N: 16384, r: 8, p: 1 });
const passwordHash = ['scrypt', 16384, 8, 1, salt.toString('base64'), dk.toString('base64')].join('$');

const admin = {
  passwordHash,
  sessionSecret: crypto.randomBytes(32).toString('base64url'),
  passwordChangedAt: Date.now(),
  createdAt: new Date().toISOString(),
};

const tmp = `${adminFile}.tmp.${process.pid}`;
fs.writeFileSync(tmp, JSON.stringify(admin, null, 2), { mode: 0o600 });
fs.renameSync(tmp, adminFile);

if (!fs.existsSync(keysFile)) {
  const keys = [];
  if (migrateToken) {
    keys.push({
      id: crypto.randomUUID(),
      label: '默认 key（迁移自反向隧道 token）',
      key: migrateToken,
      createdAt: Date.now(),
      expiresAt: null,
      revokedAt: null,
      lastUsedAt: null,
      requests: 0, ok: 0, errors: 0, promptTokens: 0, completionTokens: 0,
    });
  }
  const store = {
    version: 1,
    keys,
    playground: { requests: 0, ok: 0, errors: 0, promptTokens: 0, completionTokens: 0, lastUsedAt: null },
  };
  const ktmp = `${keysFile}.tmp.${process.pid}`;
  fs.writeFileSync(ktmp, JSON.stringify(store, null, 2), { mode: 0o600 });
  fs.renameSync(ktmp, keysFile);
  process.stderr.write(`keys.json 已创建，迁移 ${keys.length} 个 key\n`);
}

process.stderr.write(`admin.json 已写入 ${adminFile}\n`);
process.stdout.write(password);
