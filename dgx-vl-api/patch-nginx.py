#!/usr/bin/env python3
"""把 nginx 从「硬编码 token 校验」切到「反代到 kilee-vl-admin」，并挂上 /vl/ 控制台。"""
import shutil
import time

TS = time.strftime('%Y%m%d-%H%M%S')

NEW_BLOCK = '''    # ── DGX 本地 vLLM Qwen3-VL 推理入口 ──────────────────────────────
    # API key 校验与用量统计在 127.0.0.1:8811 (kilee-vl-admin) 内完成
    location ^~ /api/vl/ {
        limit_req zone=vlapi burst=20 nodelay;
        limit_conn conn 5;
        client_max_body_size 50m;
        proxy_pass http://127.0.0.1:8811;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header Connection "";
        proxy_buffering off;
        proxy_cache off;
        proxy_request_buffering off;
        proxy_read_timeout 300s;
        proxy_send_timeout 300s;
        chunked_transfer_encoding on;
    }

    # ── 模型对接控制台（管理密码登录） ───────────────────────────────
    location /vl/ {
        alias /var/www/kilee.cn/vl/;
        index index.html;
        try_files $uri $uri/ =404;
    }

    # 控制台 API（会话鉴权；会话 cookie Path=/vl，与静态资源同源）
    location ^~ /vl/api/ {
        limit_req zone=vladmin burst=60 nodelay;
        limit_conn conn 10;
        client_max_body_size 50m;
        proxy_pass http://127.0.0.1:8811;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header Connection "";
        proxy_buffering off;
        proxy_cache off;
        proxy_request_buffering off;
        proxy_read_timeout 300s;
        proxy_send_timeout 300s;
        chunked_transfer_encoding on;
    }
'''

# ───────── 1. nginx.conf: 增加两个限流 zone ─────────
CONF = '/etc/nginx/nginx.conf'
conf = open(CONF, encoding='utf-8').read()
if 'zone=vlapi' in conf:
    print('[nginx.conf] zone 已存在，跳过')
else:
    anchor = 'limit_conn_zone $binary_remote_addr zone=conn:10m;'
    assert anchor in conf, 'nginx.conf 里找不到 zone 锚点'
    conf = conf.replace(
        anchor,
        anchor
        + '\n\tlimit_req_zone $binary_remote_addr zone=vlapi:10m rate=60r/m;'
        + '\n\tlimit_req_zone $binary_remote_addr zone=vladmin:10m rate=120r/m;',
        1,
    )
    shutil.copy2(CONF, f'{CONF}.bak.{TS}')
    open(CONF, 'w', encoding='utf-8').write(conf)
    print(f'[nginx.conf] 已加 vlapi/vladmin zone（备份 {CONF}.bak.{TS}）')

# ───────── 2. 站点配置 ─────────
SITE = '/etc/nginx/sites-enabled/kilee.cn'
t = open(SITE, encoding='utf-8').read()
shutil.copy2(SITE, f'/root/nginx-kilee.cn.bak.{TS}')
print(f'[site] 备份 /root/nginx-kilee.cn.bak.{TS}')

# 2a. 让 /vl/ 也绕过扫描器 UA 封锁
ua_old = '''    if ($uri ~ "^/api/") {
        set $block_ua 0;
    }'''
if 'if ($uri ~ "^/vl/")' in t:
    print('[site] UA 豁免已存在')
else:
    assert ua_old in t, 'UA 豁免锚点找不到'
    t = t.replace(ua_old, ua_old + '''
    if ($uri ~ "^/vl/") {
        set $block_ua 0;
    }''', 1)
    print('[site] 已给 /vl/ 加 UA 豁免')

# 2b. 替换旧的硬编码 token 块
marker = 'Qwen3-VL 反向代理'
i = t.find(marker)
assert i >= 0, '找不到旧的 vl 注释块'
line_start = t.rfind('\n', 0, i) + 1
assert t[line_start:line_start + 5] == '    #', f'行首异常: {t[line_start:line_start + 12]!r}'

j = t.find('location ^~ /api/vl/', i)
assert j >= 0, '找不到旧 location'
k = t.find('{', j)
depth, end = 0, -1
for idx in range(k, len(t)):
    if t[idx] == '{':
        depth += 1
    elif t[idx] == '}':
        depth -= 1
        if depth == 0:
            end = idx
            break
assert end > 0, '旧 location 括号不闭合'

tail = t[end + 1:].lstrip('\n')
t = t[:line_start] + NEW_BLOCK + '\n' + tail
open(SITE, 'w', encoding='utf-8').write(t)
print('[site] 已替换为反代 8811 + /vl/ 控制台')
