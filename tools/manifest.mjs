#!/usr/bin/env node
// chaosD6-GameModels manifest: index.json = { format, hash, races: { <id>: sha256 }, buildings: …, npcs: …,
// monsters: …, villages: …, quests: …, lib: … } — sha256 of each definition's canonical JSON (keys sorted, no
// whitespace), so formatting never changes a definition's identity.
//   node tools/manifest.mjs            write index.json
//   node tools/manifest.mjs --check    exit 1 if index.json is stale
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadGameModels, buildIndex } from './engine/gamemodels.mjs';

const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const m = loadGameModels(ROOT);
if (m.errors.length) { console.error(m.errors.join('\n')); process.exit(1); }
const text = JSON.stringify(buildIndex(m), null, 2) + '\n';
const f = path.join(ROOT, 'index.json');
if (process.argv.includes('--check')) {
  if (!fs.existsSync(f) || fs.readFileSync(f, 'utf8') !== text) { console.error('index.json is stale — run node tools/manifest.mjs'); process.exit(1); }
  console.log(`index.json up to date (${m.files} definitions)`);
} else { fs.writeFileSync(f, text); console.log(`index.json written (${m.files} definitions)`); }
