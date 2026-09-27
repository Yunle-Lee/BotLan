import { createRequire } from "node:module";
// Reuse the locked build dependency already supplied by tsx; no installer/runtime change.
const { build } = createRequire(import.meta.resolve("tsx/package.json"))("esbuild");
const result = await build({
  entryPoints: ["apps/server/src/island-entry.ts"],
  outfile: "dist/island-server/island-server.mjs",
  bundle: true, platform: "node", format: "esm", target: "node22",
  // Database packages load native/WASM resources relative to their own package.
  external: ["@electric-sql/pglite", "pg"],
  banner: { js: 'import { createRequire as __islandCreateRequire } from "node:module"; const require = __islandCreateRequire(import.meta.url);' },
  keepNames: true, legalComments: "eof", metafile: true, logLevel: "info",
});
console.log(`Embedded server: ${Object.keys(result.metafile.inputs).length} source modules in one local bundle.`);
