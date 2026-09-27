import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const app = path.resolve(process.argv[2]);
const licenses = path.resolve(root, 'licenses');
fs.mkdirSync(licenses, { recursive: true });
function packageRoot(name, from) {
  const require = createRequire(path.join(from, 'package.json'));
  // ESM-only packages may deliberately export neither package.json nor a CJS entry.
  for (const base of require.resolve.paths(name) ?? []) {
    const manifest = path.join(base, name, 'package.json');
    if (fs.existsSync(manifest)) return path.dirname(fs.realpathSync(manifest));
  }
  try { return path.dirname(fs.realpathSync(require.resolve(name + '/package.json'))); }
  catch {
    let current = path.dirname(fs.realpathSync(require.resolve(name)));
    while (current !== path.dirname(current)) {
      const file = path.join(current, 'package.json');
      if (fs.existsSync(file) && JSON.parse(fs.readFileSync(file)).name === name) return current;
      current = path.dirname(current);
    }
    throw new Error('Cannot locate dependency: ' + name);
  }
}
const runtime = new Map();
function copyPackage(name, from) {
  const source = packageRoot(name, from);
  const pkg = JSON.parse(fs.readFileSync(path.join(source, 'package.json')));
  if (runtime.has(pkg.name)) {
    if (runtime.get(pkg.name) !== pkg.version) throw new Error('Runtime version conflict: ' + name);
    return;
  }
  runtime.set(pkg.name, pkg.version);
  const target = path.join(app, 'Chat/node_modules', pkg.name);
  fs.cpSync(source, target, { recursive: true, dereference: true,
    filter: p => !p.slice(source.length).split(path.sep).includes('node_modules') && !p.endsWith('.map') });
  for (const dep of Object.keys(pkg.dependencies ?? {})) copyPackage(dep, source);
  for (const dep of Object.keys(pkg.optionalDependencies ?? {})) {
    try { copyPackage(dep, source); } catch (error) {
      if (error.code !== 'MODULE_NOT_FOUND' && error.code !== 'ERR_PACKAGE_PATH_NOT_EXPORTED') throw error;
    }
  }
}
copyPackage('@electric-sql/pglite', path.join(root, 'Chat'));
copyPackage('pg', path.join(root, 'Chat'));

// Keep complete license texts for the dependency closure, including build tools.
const inventory = new Map();
function visit(name, from, optional = false) {
  let source;
  try { source = packageRoot(name, from); } catch (error) { if (optional) return; throw error; }
  const pkg = JSON.parse(fs.readFileSync(path.join(source, 'package.json')));
  const key = `${pkg.name}@${pkg.version}`;
  if (inventory.has(key)) return;
  const texts = fs.readdirSync(source).filter(x => /^(licen[cs]e|copying|notice|third.?party)/i.test(x)
    && fs.statSync(path.join(source, x)).isFile());
  const output = path.join(licenses, 'npm', key.replace(/[/\\:*?"<>|]/g, '_'));
  if (texts.length) fs.mkdirSync(output, { recursive: true });
  for (const file of texts) fs.copyFileSync(path.join(source, file), path.join(output, file));
  inventory.set(key, { name: pkg.name, version: pkg.version, license: pkg.license ?? pkg.licenses ?? 'SEE PACKAGE NOTICE',
    repository: pkg.repository, licenseFiles: texts, separatelyPackaged: runtime.has(pkg.name) });
  for (const dep of Object.keys(pkg.dependencies ?? {})) visit(dep, source);
  for (const dep of Object.keys({ ...pkg.optionalDependencies, ...pkg.peerDependencies })) visit(dep, source, true);
}
for (const folder of ['Chat', 'Chat/apps/mobile', 'Chat/apps/worker']) {
  const from = path.join(root, folder), pkg = JSON.parse(fs.readFileSync(path.join(from, 'package.json')));
  for (const dep of Object.keys({ ...pkg.dependencies, ...pkg.devDependencies }))
    if (!['electron', 'electron-packager'].includes(dep)) visit(dep, from);
}
fs.writeFileSync(path.join(licenses, 'npm-inventory.json'), JSON.stringify([...inventory.values()].sort((a,b)=>a.name.localeCompare(b.name)), null, 2));
fs.cpSync(path.join(root, 'Chat/dist/island'), path.join(app, 'Chat/dist/island'), { recursive: true });
fs.mkdirSync(path.join(app, 'Chat/dist/island-server'), { recursive: true });
fs.copyFileSync(path.join(root, 'Chat/dist/island-server/island-server.mjs'), path.join(app, 'Chat/dist/island-server/island-server.mjs'));
fs.cpSync(licenses, path.join(app, 'licenses'), { recursive: true });
fs.copyFileSync(path.join(root, 'THIRD_PARTY_NOTICES.md'), path.join(app, 'THIRD_PARTY_NOTICES.md'));
fs.mkdirSync(path.join(app, 'runtime'), { recursive: true });
fs.copyFileSync(process.execPath, path.join(app, 'runtime/node.exe'));
fs.copyFileSync(path.join(path.dirname(process.execPath), 'LICENSE'), path.join(app, 'runtime/LICENSE'));
fs.writeFileSync(path.join(app, 'installed.marker'), 'IslandUI installation; user data is stored separately in LocalAppData/IslandUI.\n');
console.log(JSON.stringify({ runtimePackages: Object.fromEntries(runtime), licensePackages: inventory.size }));
