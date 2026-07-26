#!/usr/bin/env node
/**
 * Blind A/B pairing for critic agents.
 * Puts unlabeled A/B copies into Tools/VisualQA/pairs/ and writes a hidden key.
 *
 * Usage:
 *   node Tools/VisualQA/pair.mjs Tools/VisualQA/out/strip_xxx/frame_00.png Tools/VisualQA/refs/cod_01.png
 */
import { mkdir, copyFile, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import crypto from 'node:crypto';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ours = process.argv[2];
const refs = process.argv[3];
if (!ours || !refs) {
  console.error('Usage: node Tools/VisualQA/pair.mjs <our.png> <ref.png>');
  process.exit(1);
}

async function mustExist(p) {
  try { await access(p); } catch { throw new Error(`missing: ${p}`); }
}

await mustExist(ours);
await mustExist(refs);

const id = crypto.randomBytes(4).toString('hex');
const outDir = path.join(__dirname, 'pairs', id);
await mkdir(outDir, { recursive: true });

const oursIsA = Math.random() < 0.5;
const aSrc = oursIsA ? ours : refs;
const bSrc = oursIsA ? refs : ours;
await copyFile(aSrc, path.join(outDir, 'A.png'));
await copyFile(bSrc, path.join(outDir, 'B.png'));

const key = {
  id,
  A: oursIsA ? 'ours' : 'cod_ref',
  B: oursIsA ? 'cod_ref' : 'ours',
  created: new Date().toISOString(),
};
// Key is written beside pairs but named so critics aren't pointed at it.
await writeFile(path.join(outDir, '.answer_key.json'), JSON.stringify(key, null, 2));
console.log(`Paired → ${outDir}`);
console.log('Give the critic ONLY A.png and B.png. Do not show .answer_key.json.');
