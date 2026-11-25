#!/usr/bin/env node
// chaosD6-GameModels offline check: every definition in this repo, with the same checker the game runs
// (tools/engine/gamemodels.mjs = chaosD6 game/gamemodels.mjs, byte-identical — tools/pins.json).
//   npm install            # once: json-logic-js
//   node tools/check.mjs   # → "ok: N definitions" or one line per problem, exit 1
// Without the chaosD6 game tree this cannot see which effect / skill / creature ids or sprites exist — the game's
// own build (game/tools/gamemodels-build.mjs) checks those when it takes the definitions in.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
let gm;
try { gm = await import('./engine/gamemodels.mjs'); }
catch (e) { console.error(`cannot load the engine (${e.message}) — run \`npm install\` in ${ROOT}`); process.exit(2); }
const problems = [];
const pins = JSON.parse(fs.readFileSync(path.join(ROOT, 'tools', 'pins.json'), 'utf8'));
for (const [rel, p] of Object.entries(pins.files || {})) {
  const f = path.join(ROOT, rel);
  const sha = fs.existsSync(f) ? createHash('sha256').update(fs.readFileSync(f, 'utf8').replace(/\r\n/g, '\n')).digest('hex') : null;
  if (sha !== p.sha256) problems.push(`${rel}: ${sha ? 'changed' : 'missing'} (pinned to ${p.source}) — players never edit tools/`);
}
const m = gm.loadGameModels(ROOT);
problems.push(...gm.checkGameModels(m));
const idx = JSON.stringify(gm.buildIndex(m), null, 2) + '\n';
if (fs.readFileSync(path.join(ROOT, 'index.json'), 'utf8') !== idx) problems.push('index.json is stale — run node tools/manifest.mjs');
if (problems.length) { console.error(problems.join('\n')); process.exit(1); }
console.log(`ok: ${m.files} definitions`);
