#!/usr/bin/env node
/**
 * Lean Poly Haven downloader — 4K max, CC0 assets.
 * Usage:
 *   node Tools/AssetFetch/polyhaven.mjs hdri urban 4
 *   node Tools/AssetFetch/polyhaven.mjs texture concrete 2
 *   node Tools/AssetFetch/polyhaven.mjs model industrial 1
 */
import { mkdir, writeFile, access } from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { pipeline } from 'node:stream/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const CACHE = path.join(__dirname, 'cache', 'polyhaven');
const API = 'https://api.polyhaven.com';

const type = (process.argv[2] || 'hdri').toLowerCase();
const category = process.argv[3] || 'urban';
const limit = Math.max(1, parseInt(process.argv[4] || '4', 10));
const res = type === 'hdri' ? '4k' : '2k';

async function exists(p) {
  try { await access(p); return true; } catch { return false; }
}

async function fetchJson(url) {
  const r = await fetch(url, { headers: { 'User-Agent': 'ArenaFps-AssetFetch/1.0' } });
  if (!r.ok) throw new Error(`${url} -> ${r.status}`);
  return r.json();
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

function pickUrl(files, assetType, resolution) {
  if (assetType === 'hdri') {
    const node = files?.hdri?.[resolution];
    return node?.hdr?.url || node?.exr?.url || null;
  }
  if (assetType === 'texture') {
    // Prefer Diffuse/jpg at requested res
    for (const map of ['Diffuse', 'diff', 'Color']) {
      const n = files?.[map]?.[resolution];
      if (n?.jpg?.url) return n.jpg.url;
      if (n?.png?.url) return n.png.url;
    }
    // Fall back: first jpg at resolution
    for (const key of Object.keys(files || {})) {
      const n = files[key]?.[resolution];
      if (n?.jpg?.url) return n.jpg.url;
    }
  }
  if (assetType === 'model') {
    return files?.gltf?.[resolution]?.gltf?.url
      || files?.fbx?.[resolution]?.fbx?.url
      || null;
  }
  return null;
}

async function main() {
  const apiType = type === 'hdri' ? 'hdris' : type === 'texture' ? 'textures' : 'models';
  console.log(`Poly Haven: type=${apiType} category=${category} limit=${limit} res=${res}`);

  const list = await fetchJson(`${API}/assets?t=${apiType}&c=${encodeURIComponent(category)}`);
  const ids = Object.keys(list).slice(0, limit);
  if (!ids.length) {
    console.error('No assets matched. Try another category.');
    process.exit(1);
  }

  const ledger = [];
  for (const id of ids) {
    const files = await fetchJson(`${API}/files/${id}`);
    const url = pickUrl(files, type, res);
    if (!url) {
      console.warn(`no ${res} file for ${id}`);
      continue;
    }
    const dest = path.join(CACHE, type, path.basename(new URL(url).pathname));
    await download(url, dest);
    ledger.push({ id, type, res, url, dest: path.relative(path.join(__dirname, '../..'), dest), license: 'CC0-1.0' });
  }

  await mkdir(CACHE, { recursive: true });
  await writeFile(path.join(CACHE, `${type}-${category}-manifest.json`), JSON.stringify(ledger, null, 2));
  console.log(`Done. ${ledger.length} files. Remember: credit "Powered by Poly Haven" in-game.`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
