# Assets Needed — Urban Arena TDM

Free / CC0 / Unity free EULA only. Place downloads under `Assets/_Project/Art/` then tell Jarvis to wire them.

## Priority 1 — Makes it feel like COD tomorrow

| # | Asset | Source | Why | Suggested path |
|---|--------|--------|-----|----------------|
| 1 | **Quaternius Downtown / City MegaKit** (or Modular Buildings) | [Quaternius](https://quaternius.com/) CC0 | Real buildings instead of textured cubes | `Art/Models/Environment/City/` |
| 2 | **Military / urban prop pack** (crates, barrels, sandbags, dumpsters, barriers) | Quaternius / Kenney / Poly Haven models | Mid-lane clutter density | `Art/Models/Environment/Props/` |
| 3 | **Second soldier skin (OpFor / Red)** | Mixamo CC0-friendly or Unity free soldier | Red team readable at range | `Art/Models/Characters/OpFor/` |
| 4 | **War FX** (free) | Unity Asset Store | Muzzle flashes, explosions, tracer quality | `Art/VFX/WarFX/` |
| 5 | **Sonniss #GameAudioGDC** gun / Foley pack | Sonniss (free yearly) | Replace synth/gunshot placeholders | `Audio/Resources/Sfx/` |

## Priority 2 — Visual fidelity leap

| # | Asset | Source | Why |
|---|--------|--------|-----|
| 6 | PBR ground set (asphalt + concrete wet) 2K | ambientCG / Poly Haven | Already started — want wet/road markings |
| 7 | Graffiti / poster decals (CC0) | Poly Haven / ambientCG | Mid billboard + alley walls |
| 8 | Chain-link fence / railing meshes | Kenney / Quaternius | Lane edges & overlook rails |
| 9 | Abandoned vehicle (bus/van) mesh | Poly Haven models / Quaternius | Replace Mid_Bus cube |
| 10 | Urban HDRI (overcast + golden hour) | Poly Haven | Already have bakery + construction — add `urban_alley` |

## Priority 3 — Juice / presentation

| # | Asset | Source | Why |
|---|--------|--------|-----|
| 11 | Killstreak / announcer VO (CC0 or record yourself) | Freesound CC0 | Match start / lead change |
| 12 | Team icons + COD-style HUD font | Google Fonts (OFL) / self-made | Scoreboard polish |
| 13 | Bullet hole + blood decal atlases | Free Unity/CC0 packs | Impact readability |
| 14 | Smoke / dust particle textures | CC0 | Breakable cover debris |

## Already in project (do not re-download)

- Scar-H + ACR + M4 viewmodels
- MaleWarrior soldier + Kevin Iglesias soldier anim set
- ambientCG: Concrete034/048, Metal063/049A, CorrugatedSteel009, Wood095, Asphalt033, Plaster001
- Poly Haven: `abandoned_bakery`, `abandoned_construction`, `brick_4`
- Gunshot + Reload mp3s (verify Freesound licenses before ship)

## How to drop assets in

1. Download → unzip into the suggested folder under `Assets/_Project/`
2. Unity will import (or run `Arena FPS` menu tools if we add one)
3. Ping Jarvis: “assets dropped — wire city kit / props”
4. Update `Tools/AssetFetch/LICENSES.md` with source + license row
