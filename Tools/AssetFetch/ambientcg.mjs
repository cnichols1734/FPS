#!/usr/bin/env node
/**
 * Lean ambientCG downloader — 2K ZIP packs, CC0.
 * Usage:
 *   node Tools/AssetFetch/ambientcg.mjs Concrete 3
 *   node Tools/AssetFetch/ambientcg.mjs Metal 4
 *   node Tools/AssetFetch/ambientcg.mjs Asphalt 2
 */
import { mkdir, writeFile, access } from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { pipeline } from 'node:stream/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const CACHE = path.join(__dirname, 'cache', 'ambientcg');
const API = 'https://ambientcg.com/api/v2/full_json';

const q = process.argv[2] || 'Concrete';
const limit = Math.max(1, parseInt(process.argv[3] || '3', 10));
const res = '2K';

async function exists(p) {
  try { await access(p); return true; } catch { return false; }
}

async function download(url, dest) {
  if (await exists(dest)) {
    console.log(`skip (exists): ${path.basename(dest)}`);
    return;
  }
  await mkdir(path.dirname(dest), { recursive: true });
  const r = await fetch(url, { headers: { 'User-Agent': 'ArenaFps-AssetFetch/1.0' } });
  if (!r.ok) throw new Error(`download ${url} -> ${r.status}`);
  await pipeline(r.body, createWriteStream(dest));
  console.log(`ok: ${path.basename(dest)}`);
}

async function main() {
  const url = `${API}?type=Material&q=${encodeURIComponent(q)}&include=downloadData,tagData`;
  console.log(`ambientCG: query=${q} limit=${limit} res=${res}`);
  const r = await fetch(url, { headers: { 'User-Agent': 'ArenaFps-AssetFetch/1.0' } });
  if (!r.ok) throw new Error(`api -> ${r.status}`);
  const data = await r.json();
  const materials = (data.foundAssets || data || []).slice(0, limit);

  if (!Array.isArray(materials) || !materials.length) {
    // Fallback shape: some API versions return { [id]: {...} }
    console.error('No materials found. Check query / API shape.');
    console.error(JSON.stringify(data).slice(0, 400));
    process.exit(1);
  }

  const ledger = [];
  for (const mat of materials) {
    const id = mat.assetId || mat.id || mat.name;
    const downloads = mat.downloadFolders || mat.downloads || mat.downloadData || {};
    // ambientCG v2 often nests: downloadFolders["Zip"][resolution]...
    let zipUrl = null;
    const asJson = typeof downloads === 'object' ? downloads : {};
    const walk = (node) => {
      if (!node || zipUrl) return;
      if (typeof node === 'string' && node.includes('http') && node.toLowerCase().includes('.zip')) {
        zipUrl = node;
        return;
      }
      if (typeof node !== 'object') return;
      for (const [k, v] of Object.entries(node)) {
        if (zipUrl) return;
        if (typeof v === 'string' && v.includes('http') && (k.toLowerCase().includes('zip') || v.endsWith('.zip'))) {
          if (String(k).toUpperCase().includes(res) || JSON.stringify(node).toUpperCase().includes(res)) {
            zipUrl = v;
            return;
          }
        }
        walk(v);
      }
    };
    walk(asJson);

    // Direct download pattern used by many ambientCG clients
    if (!zipUrl && id) {
      zipUrl = `https://ambientcg.com/get?file=${encodeURIComponent(id)}_${res}-JPG.zip`;
    }
    if (!zipUrl) {
      console.warn(`no zip for ${id}`);
      continue;
    }

    const dest = path.join(CACHE, `${id}_${res}.zip`);
    await download(zipUrl, dest);
    ledger.push({ id, res, url: zipUrl, dest: path.relative(path.join(__dirname, '../..'), dest), license: 'CC0-1.0' });
  }

  await mkdir(CACHE, { recursive: true });
  await writeFile(path.join(CACHE, `${q}-manifest.json`), JSON.stringify(ledger, null, 2));
  console.log(`Done. ${ledger.length} packs.`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
