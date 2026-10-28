#!/usr/bin/env node
// DSH Unity Bridge 的客户端 CLI —— DSH 侧唯一入口，零依赖（只用 Node 内置模块）。
//
// 用法：
//   node tools/unity-bridge/bridge.mjs ping
//   node tools/unity-bridge/bridge.mjs scene
//   node tools/unity-bridge/bridge.mjs hierarchy --depth 4
//   node tools/unity-bridge/bridge.mjs create --name Foo --sprite Assets/Art/x.png
//   node tools/unity-bridge/bridge.mjs set --path Player --component Rigidbody2D --field gravityScale --value 2.5
//   node tools/unity-bridge/bridge.mjs screenshot --view scene
//
// 连接参数优先级：命令行 > 环境变量 > 自动发现（从 cwd 向上找 UnityProject/UserSettings/UnityBridge.json）
//   --project  <Unity 工程根目录>
//   --url      http://127.0.0.1:8090/rpc
//   --token    xxx
//   DSH_UNITY_URL / DSH_UNITY_TOKEN / DSH_UNITY_PROJECT
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

const OPS = {
  ping: [],
  scene: [],
  'open-scene': ['path', 'additive'],
  assets: ['filter', 'type', 'folder', 'limit'],
  hierarchy: ['depth', 'budget', 'components'],
  find: ['query', 'type'],
  create: ['name', 'parent', 'position', 'localPosition', 'scale', 'layer', 'tag', 'sprite', 'order', 'sortingLayer'],
  delete: ['path', 'name'],
  rename: ['path', 'to'],
  'set-active': ['path', 'active'],
  transform: ['path', 'position', 'localPosition', 'scale', 'eulerAngles'],
  'add-component': ['path', 'type'],
  'remove-component': ['path', 'type'],
  set: ['path', 'component', 'field', 'value'],
  save: ['all'],
  screenshot: ['view', 'width', 'height', 'path'],
  play: ['action'],
  menu: ['path'],
  select: ['path'],
  invoke: ['type', 'method'],
  log: ['lines'],
  quit: ['code']
};

// op 名 -> 请求体里的 op；命令行用短横线，Unity 侧用下划线
const OP_ALIAS = {
  'open-scene': 'open_scene',
  'set-active': 'set_active',
  'add-component': 'add_component',
  'remove-component': 'remove_component'
};

function parseArgs(argv) {
  const out = { _: [], flags: {} };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) out.flags[key] = true;
      else { out.flags[key] = next; i++; }
    } else out._.push(a);
  }
  return out;
}

function findProject(start) {
  let dir = path.resolve(start || process.cwd());
  for (let i = 0; i < 8; i++) {
    if (fs.existsSync(path.join(dir, 'UserSettings', 'UnityBridge.json'))) return dir;
    if (fs.existsSync(path.join(dir, 'Assets')) && fs.existsSync(path.join(dir, 'ProjectSettings'))) return dir;
    // 仓库根目录的情况：工程在 UnityProject/ 下
    const nested = path.join(dir, 'UnityProject');
    if (fs.existsSync(path.join(nested, 'Assets')) && fs.existsSync(path.join(nested, 'ProjectSettings'))) return nested;
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}

function readSettings(projectDir) {
  if (!projectDir) return null;
  const file = path.join(projectDir, 'UserSettings', 'UnityBridge.json');
  if (!fs.existsSync(file)) return null;
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return null;
  }
}

function resolveConnection(flags) {
  const projectDir = flags.project ? path.resolve(flags.project) : findProject(process.env.DSH_UNITY_PROJECT || process.cwd());
  const settings = readSettings(projectDir);
  const port = flags.port ? Number(flags.port) : (settings && settings.Port ? settings.Port : 8090);
  const url = flags.url || process.env.DSH_UNITY_URL || `http://127.0.0.1:${port}/rpc`;
  const token = flags.token || process.env.DSH_UNITY_TOKEN || (settings && settings.Token) || '';
  return { projectDir, url, token };
}

function coerce(key, raw) {
  if (['depth', 'budget', 'order', 'width', 'height', 'lines', 'port'].includes(key)) return Number(raw);
  if (['components', 'active', 'all'].includes(key)) return raw === 'true' || raw === true;
  if (['position', 'localPosition', 'scale', 'eulerAngles'].includes(key)) {
    if (typeof raw === 'string') {
      const parts = raw.split(',').map(s => Number(s.trim()));
      return parts.length === 1 ? Number(raw) : parts;
    }
  }
  if (key === 'value') {
    if (raw === 'true' || raw === 'false') return raw === 'true';
    const n = Number(raw);
    return Number.isFinite(n) && raw !== '' ? n : raw;
  }
  return raw;
}

function postJson(url, body, token) {
  return new Promise((resolve, reject) => {
    const payload = Buffer.from(JSON.stringify(body), 'utf8');
    const u = new URL(url);
    const req = http.request(
      {
        hostname: u.hostname,
        port: u.port,
        path: u.pathname + u.search,
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Content-Length': payload.length,
          Authorization: `Bearer ${token}`
        },
        timeout: 60000
      },
      res => {
        const chunks = [];
        res.on('data', c => chunks.push(c));
        res.on('end', () => resolve({ status: res.statusCode, text: Buffer.concat(chunks).toString('utf8') }));
      }
    );
    req.on('timeout', () => req.destroy(new Error('请求超时（Unity 主线程可能被阻塞）')));
    req.on('error', reject);
    req.write(payload);
    req.end();
  });
}

function getHealth(url) {
  return new Promise((resolve, reject) => {
    const u = new URL('/health', url);
    const req = http.request({ hostname: u.hostname, port: u.port, path: '/health', method: 'GET', timeout: 5000 }, res => {
      const chunks = [];
      res.on('data', c => chunks.push(c));
      res.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    });
    req.on('timeout', () => req.destroy(new Error('超时')));
    req.on('error', reject);
    req.end();
  });
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const command = args._[0];
  const conn = resolveConnection(args.flags);

  if (!command || command === 'help' || command === '--help') {
    console.log('DSH Unity Bridge 客户端');
    console.log('  node bridge.mjs <op> [--参数 值 ...]');
    console.log('  op: ' + Object.keys(OPS).join(' | '));
    console.log('  连接: --project <工程根> | --url <rpc url> | --token <token>');
    process.exit(0);
  }

  if (command === 'health') {
    try {
      const text = await getHealth(conn.url);
      console.log(text);
      process.exit(0);
    } catch (e) {
      console.error('健康检查失败: ' + e.message);
      process.exit(1);
    }
  }

  if (!OPS[command]) {
    console.error(`未知 op: ${command}\n可用: ${Object.keys(OPS).join(', ')}`);
    process.exit(2);
  }

  const params = {};
  for (const key of OPS[command]) {
    if (args.flags[key] !== undefined && args.flags[key] !== true) params[key] = coerce(key, args.flags[key]);
  }
  // 未声明的参数原样透传，方便新 op 试水
  for (const [k, v] of Object.entries(args.flags)) {
    if (k === 'project' || k === 'url' || k === 'token' || k === 'port') continue;
    if (params[k] === undefined) params[k] = coerce(k, v);
  }

  const op = OP_ALIAS[command] || command;
  let res;
  try {
    res = await postJson(conn.url, { op, params }, conn.token);
  } catch (e) {
    console.error(`连接失败: ${e.message}\n  目标: ${conn.url}\n  提示: Unity 编辑器是否已打开并启动桥（Tools → DSH Unity Bridge → 启动）？`);
    process.exit(3);
  }

  let parsed;
  try {
    parsed = JSON.parse(res.text);
  } catch {
    console.error(`响应不是 JSON（HTTP ${res.status}）: ${res.text.slice(0, 500)}`);
    process.exit(4);
  }

  if (!parsed.ok) {
    console.error(`[${op}] 失败: ${parsed.error}`);
    process.exit(1);
  }
  console.log(JSON.stringify(parsed.data === undefined ? parsed : parsed.data, null, 2));
}

main().catch(e => {
  console.error('未捕获异常: ' + (e && e.stack ? e.stack : e));
  process.exit(1);
});
