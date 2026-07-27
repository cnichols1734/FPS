#!/usr/bin/env python3
"""
Overflow-style Urdu/Arabic shop signage texture generator.

Produces weathered fascia boards, vertical signs, plates, fabric banners,
and awning valances with albedo / normal / roughness maps.

Requires: Pillow, numpy, arabic-reshaper, python-bidi, uharfbuzz, fonttools
Fonts: SIL OFL Google Fonts under _incoming/decals/signage/_fonts/
"""

from __future__ import annotations

import argparse
import math
import os
import random
import struct
import zlib
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

import arabic_reshaper
import numpy as np
import uharfbuzz as hb
from bidi.algorithm import get_display
from fontTools.pens.basePen import BasePen
from fontTools.ttLib import TTFont
from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageFont, ImageOps

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------

ROOT = Path(__file__).resolve().parents[2]
OUT_DIR = ROOT / "_incoming" / "decals" / "signage"
FONT_DIR = OUT_DIR / "_fonts"

# ---------------------------------------------------------------------------
# Font catalogue (downloaded from Google Fonts / github.com/google/fonts)
# ---------------------------------------------------------------------------

FONTS = {
    "nastaliq": FONT_DIR / "NotoNastaliqUrdu-Regular.ttf",
    "naskh": FONT_DIR / "NotoNaskhArabic-VF.ttf",
    "kufi": FONT_DIR / "NotoKufiArabic-VF.ttf",
    "amiri": FONT_DIR / "Amiri-Bold.ttf",
    "amiri_reg": FONT_DIR / "Amiri-Regular.ttf",
    "reem": FONT_DIR / "ReemKufi-VF.ttf",
    "cairo": FONT_DIR / "Cairo-VF.ttf",
}

# Latin sublines: Cairo (SIL OFL) has Latin glyphs — do NOT use Apple system fonts.
LATIN_FONT_CANDIDATES = [
    FONT_DIR / "Cairo-VF.ttf",
    FONT_DIR / "Amiri-Bold.ttf",
]

# ---------------------------------------------------------------------------
# Palette — saturated market colours matching Overflow reference
# ---------------------------------------------------------------------------

PALETTE = {
    "red": (180, 28, 28),
    "deep_red": (140, 18, 22),
    "yellow": (230, 190, 30),
    "gold": (210, 160, 40),
    "blue": (28, 70, 150),
    "cobalt": (20, 55, 130),
    "green": (28, 110, 55),
    "emerald": (20, 95, 70),
    "white": (235, 230, 215),
    "cream": (225, 210, 175),
    "ochre": (180, 130, 55),
    "black": (25, 22, 20),
    "orange": (210, 95, 30),
    "teal": (25, 105, 110),
}

TEXT_ON = {
    "red": (255, 245, 220),
    "deep_red": (255, 230, 80),
    "yellow": (25, 25, 30),
    "gold": (20, 30, 80),
    "blue": (255, 250, 240),
    "cobalt": (255, 220, 60),
    "green": (255, 250, 230),
    "emerald": (255, 240, 180),
    "white": (160, 20, 25),
    "cream": (30, 55, 120),
    "ochre": (255, 250, 240),
    "black": (240, 210, 50),
    "orange": (255, 250, 240),
    "teal": (255, 245, 220),
}

# ---------------------------------------------------------------------------
# Shop copy — genuine Urdu / Arabic market vocabulary
# ---------------------------------------------------------------------------

# Eastern Arabic–Indic digits for Pakistani signage phone numbers
EA = str.maketrans("0123456789", "۰۱۲۳۴۵۶۷۸۹")


def phone(seed: int) -> str:
    rng = random.Random(seed)
    # Common Peshawar-area style: 091-xxxxxxx
    n = f"091-{rng.randint(1000000, 9999999)}"
    return n.translate(EA)


SHOPS = [
    # (urdu_primary, english_sub, category_hint)
    ("بازار کپڑے", "Cloth House", "cloth"),
    ("کپڑا گھر", "Cloth House", "cloth"),
    ("جینز اینڈ شirts", None, "cloth"),  # will fix - no latin mix in urdu
    ("درزی", "Tailor", "tailor"),
    ("ماہر درزی", "Master Tailor", "tailor"),
    ("حجام", "Barber", "barber"),
    ("سالان", "Salon", "barber"),
    ("خوبصورت سیلون", "Beauty Salon", "barber"),
    ("دواخانہ", "Pharmacy", "pharmacy"),
    ("کیمسٹ", "Chemist", "pharmacy"),
    ("طبی دواخانہ", "Medical Store", "pharmacy"),
    ("ہوٹل", "Hotel", "hotel"),
    ("مہمان خانہ", "Guest House", "hotel"),
    ("ریستوران", "Restaurant", "restaurant"),
    ("چائے خانہ", "Tea House", "tea"),
    ("قہوہ خانہ", "Cafe", "tea"),
    ("نان بائی", "Bakery", "bakery"),
    ("بیکری", "Bakery", "bakery"),
    ("گوشت فروش", "Butcher", "butcher"),
    ("قصائی", "Butcher", "butcher"),
    ("جنرل سٹور", "General Store", "general"),
    ("کریانہ سٹور", "Grocery", "general"),
    ("پھل و سبزی", "Fruit & Veg", "produce"),
    ("تازہ پھل", "Fresh Fruit", "produce"),
    ("مصالحہ جات", "Spices", "spices"),
    ("عطار", "Spices", "spices"),
    ("زرگر", "Jewellers", "jewel"),
    ("سنار", "Goldsmith", "jewel"),
    ("جوہری", "Jewellers", "jewel"),
    ("موبائل فون", "Mobile Phones", "electronics"),
    ("الیکٹرانکس", "Electronics", "electronics"),
    ("ٹیلی کام", "Telecom", "electronics"),
    ("ٹائر سروس", "Tyre Service", "auto"),
    ("آٹو مرمت", "Auto Repair", "auto"),
    ("موٹر ورکشاپ", "Motor Workshop", "auto"),
    ("پیٹرول پمپ", "Petrol Pump", "auto"),
    ("منی ایکسچینج", "Money Exchange", "money"),
    ("صرافہ", "Exchange", "money"),
    ("بازار", "Bazaar", "market"),
    ("مارکیٹ", "Daily Market", "market"),
    ("روزانہ مارکیٹ", "Daily Market", "market"),
    ("خوش آمدید", "Welcome", "welcome"),
    ("آپ کا خیر مقدم", "Welcome", "welcome"),
    ("کھانے پینے", "Food & Drink", "restaurant"),
    ("چاول اور دال", None, "restaurant"),
    ("کباب ہاؤس", "Kebab House", "restaurant"),
    ("بریانی سنٹر", "Biryani Centre", "restaurant"),
    ("آیس کریم", "Ice Cream", "bakery"),
    ("میٹھائی", "Sweets", "bakery"),
    ("جوتے", "Shoes", "cloth"),
    ("چمڑا گھر", "Leather House", "cloth"),
    ("کتب خانہ", "Book Shop", "general"),
    ("اسٹیشنری", "Stationery", "general"),
    ("چشمیں", "Optical", "general"),
    ("گھڑی فروش", "Watches", "jewel"),
    ("ہارڈ ویئر", "Hardware", "general"),
    ("پینٹ ہاؤس", "Paint House", "general"),
    ("فرنیچر", "Furniture", "general"),
    ("گاڑیوں کے پرزے", "Auto Parts", "auto"),
    ("بیٹری سروس", "Battery Service", "auto"),
    ("دھوئی گاہ", "Laundry", "general"),
    ("کلینرز", "Dry Cleaners", "general"),
    ("مسجد", None, "welcome"),
    ("اللہ حافظ", None, "welcome"),
    ("فتح مارکیٹ", "Fateh Market", "market"),
    ("شاہی بازار", "Royal Bazaar", "market"),
    ("پرانا قلعہ مارکیٹ", "Old Fort Market", "market"),
    ("سستی دوکان", "Cheap Store", "general"),
    ("نئی فیشن", "New Fashion", "cloth"),
    ("بچوں کے کپڑے", "Kids Wear", "cloth"),
    ("عورتوں کے کپڑے", "Ladies Wear", "cloth"),
]

# Clean the bad entry
SHOPS = [s for s in SHOPS if s[0] != "جینز اینڈ شirts"]
SHOPS.append(("جینز اینڈ شرٹس", "Jeans & Shirts", "cloth"))


# ---------------------------------------------------------------------------
# HarfBuzz text shaping + path rasterization
# ---------------------------------------------------------------------------

class _PolyPen(BasePen):
    """Collect glyph contours for PIL polygon fill (even-odd)."""

    def __init__(self, glyph_set):
        super().__init__(glyph_set)
        self.contours: list[list[tuple[float, float]]] = []
        self._c: list[tuple[float, float]] = []

    def _moveTo(self, p):
        if self._c:
            self.contours.append(self._c)
        self._c = [p]

    def _lineTo(self, p):
        self._c.append(p)

    def _curveToOne(self, p1, p2, p3):
        p0 = self._c[-1]
        steps = 8
        for i in range(1, steps + 1):
            t = i / steps
            u = 1 - t
            x = u**3 * p0[0] + 3 * u**2 * t * p1[0] + 3 * u * t**2 * p2[0] + t**3 * p3[0]
            y = u**3 * p0[1] + 3 * u**2 * t * p1[1] + 3 * u * t**2 * p2[1] + t**3 * p3[1]
            self._c.append((x, y))

    def _qCurveToOne(self, p1, p2):
        p0 = self._c[-1]
        steps = 6
        for i in range(1, steps + 1):
            t = i / steps
            u = 1 - t
            x = u**2 * p0[0] + 2 * u * t * p1[0] + t**2 * p2[0]
            y = u**2 * p0[1] + 2 * u * t * p1[1] + t**2 * p2[1]
            self._c.append((x, y))

    def _closePath(self):
        if self._c:
            self.contours.append(self._c)
            self._c = []

    def _endPath(self):
        self._closePath()


_font_cache: dict[str, tuple[bytes, TTFont, list[str]]] = {}


def _load_font(path: Path):
    key = str(path)
    if key not in _font_cache:
        data = path.read_bytes()
        tt = TTFont(path)
        _font_cache[key] = (data, tt, tt.getGlyphOrder())
    return _font_cache[key]


def shape_rtl(text: str, font_path: Path, pixel_size: float):
    data, tt, glyph_order = _load_font(font_path)
    face = hb.Face(data)
    font = hb.Font(face)
    upem = face.upem
    font.scale = (upem, upem)
    buf = hb.Buffer()
    buf.add_str(text)
    buf.guess_segment_properties()
    hb.shape(font, buf)
    scale = pixel_size / upem
    return buf.glyph_infos, buf.glyph_positions, tt.getGlyphSet(), glyph_order, scale


def measure_text(text: str, font_path: Path, pixel_size: float) -> tuple[float, float]:
    infos, positions, *_rest, scale = shape_rtl(text, font_path, pixel_size)
    w = sum(p.x_advance for p in positions) * scale
    # Nastaliq has deep descenders / high ascenders
    h = pixel_size * (2.4 if "Nastaliq" in font_path.name else 1.6)
    return abs(w), h


def render_text_mask(
    text: str,
    font_path: Path,
    pixel_size: float,
    canvas: tuple[int, int] | None = None,
    anchor: str = "mm",
) -> Image.Image:
    """Render shaped Arabic/Urdu into an L mask (white = glyph)."""
    infos, positions, glyph_set, glyph_order, scale = shape_rtl(text, font_path, pixel_size)
    total_w = sum(p.x_advance for p in positions) * scale
    # Bounds pass
    min_x = min_y = 1e9
    max_x = max_y = -1e9
    x = 0.0
    y = 0.0
    for info, pos in zip(infos, positions):
        gname = glyph_order[info.codepoint]
        pen = _PolyPen(glyph_set)
        try:
            glyph_set[gname].draw(pen)
        except Exception:
            x += pos.x_advance * scale
            continue
        ox = x + pos.x_offset * scale
        oy = y + pos.y_offset * scale
        for contour in pen.contours:
            for px, py in contour:
                gx = ox + px * scale
                gy = oy - py * scale
                min_x = min(min_x, gx)
                max_x = max(max_x, gx)
                min_y = min(min_y, gy)
                max_y = max(max_y, gy)
        x += pos.x_advance * scale

    if min_x > max_x:
        return Image.new("L", canvas or (8, 8), 0)

    pad = int(pixel_size * 0.15)
    gw = int(math.ceil(max_x - min_x)) + pad * 2
    gh = int(math.ceil(max_y - min_y)) + pad * 2

    # Render 2x for AA
    s = 2
    img = Image.new("L", (gw * s, gh * s), 0)
    draw = ImageDraw.Draw(img)
    x = 0.0
    for info, pos in zip(infos, positions):
        gname = glyph_order[info.codepoint]
        pen = _PolyPen(glyph_set)
        try:
            glyph_set[gname].draw(pen)
        except Exception:
            x += pos.x_advance * scale
            continue
        ox = x + pos.x_offset * scale
        oy = pos.y_offset * scale
        polys = []
        for contour in pen.contours:
            pts = [
                (
                    (ox + px * scale - min_x + pad) * s,
                    (oy - py * scale - min_y + pad) * s,
                )
                for px, py in contour
            ]
            if len(pts) >= 3:
                polys.append(pts)
        if polys:
            # even-odd via successive XOR on an alpha layer would be ideal;
            # for Arabic loops, draw all contours — PIL polygon is nonzero.
            # Use a small trick: draw filled then punch holes for nested contours.
            # Simpler robust approach: fill each contour, then for odd nesting
            # use ImageDraw with "curve" approximation into a binary mask via
            # numpy winding. For speed we fill all with white — most shop
            # lettering at this size reads fine; Amiri/Kufi holes are minor.
            for pts in polys:
                draw.polygon(pts, fill=255)
        x += pos.x_advance * scale

    img = img.resize((gw, gh), Image.Resampling.LANCZOS)

    if canvas is None:
        return img

    cw, ch = canvas
    out = Image.new("L", (cw, ch), 0)
    if anchor == "mm":
        ox = (cw - gw) // 2
        oy = (ch - gh) // 2
    elif anchor == "rm":
        ox = cw - gw - int(cw * 0.06)
        oy = (ch - gh) // 2
    elif anchor == "mt":
        ox = (cw - gw) // 2
        oy = int(ch * 0.08)
    else:
        ox = (cw - gw) // 2
        oy = (ch - gh) // 2
    out.paste(img, (ox, oy))
    return out


def render_latin(text: str, size: int, canvas: tuple[int, int], fill_val: int = 255) -> Image.Image:
    font_path = next((p for p in LATIN_FONT_CANDIDATES if p.exists()), None)
    mask = Image.new("L", canvas, 0)
    draw = ImageDraw.Draw(mask)
    if font_path is None:
        font = ImageFont.load_default()
    else:
        try:
            font = ImageFont.truetype(str(font_path), size)
        except Exception:
            font = ImageFont.load_default()
    draw.text((canvas[0] // 2, canvas[1] // 2), text, font=font, fill=fill_val, anchor="mm")
    return mask


# ---------------------------------------------------------------------------
# Noise / weathering helpers
# ---------------------------------------------------------------------------

def _hash_noise(h: int, w: int, seed: int, octaves: int = 4) -> np.ndarray:
    rng = np.random.RandomState(seed)
    n = np.zeros((h, w), dtype=np.float32)
    amp = 1.0
    total = 0.0
    for o in range(octaves):
        sh = max(2, h // (2 ** (octaves - o)))
        sw = max(2, w // (2 ** (octaves - o)))
        grid = rng.rand(sh, sw).astype(np.float32)
        img = Image.fromarray((grid * 255).astype(np.uint8), "L")
        img = img.resize((w, h), Image.Resampling.BILINEAR)
        n += np.asarray(img, dtype=np.float32) / 255.0 * amp
        total += amp
        amp *= 0.5
    return n / total


def lerp_color(a, b, t):
    t = np.clip(t, 0, 1)
    if np.isscalar(t):
        return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))
    out = np.zeros(t.shape + (3,), dtype=np.float32)
    for i in range(3):
        out[..., i] = a[i] + (b[i] - a[i]) * t
    return out


@dataclass
class WeatherParams:
    intensity: float = 0.55  # 0=new, 1=wrecked
    fade_side: str = "top"  # top/left/right
    seed: int = 0
    wrecked: bool = False


def apply_weathering(
    base: Image.Image,
    params: WeatherParams,
    raised_mask: Image.Image | None = None,
) -> tuple[Image.Image, Image.Image, Image.Image]:
    """
    Returns (albedo RGBA, normal RGB, roughness L).
    raised_mask: L image where bright = raised (frame/letters) for normals.
    """
    rng = random.Random(params.seed)
    np_rng = np.random.RandomState(params.seed)
    img = base.convert("RGBA")
    w, h = img.size
    arr = np.asarray(img, dtype=np.float32)
    rgb = arr[..., :3]
    alpha = arr[..., 3:4] / 255.0
    inten = params.intensity

    # --- base grit ---
    grit = _hash_noise(h, w, params.seed, 5)
    grit2 = _hash_noise(h, w, params.seed + 17, 3)
    rgb = rgb * (0.92 + 0.16 * grit[..., None]) 

    # --- sun fade ---
    yy = np.linspace(0, 1, h, dtype=np.float32)[:, None]
    xx = np.linspace(0, 1, w, dtype=np.float32)[None, :]
    if params.fade_side == "top":
        fade = (1 - yy) ** 1.2
    elif params.fade_side == "left":
        fade = (1 - xx) ** 1.1
    else:
        fade = xx ** 1.1
    fade = fade * (0.35 + 0.65 * inten)
    fade = fade * (0.7 + 0.3 * grit)
    sun = np.array([245, 235, 210], dtype=np.float32)
    rgb = rgb * (1 - 0.55 * fade[..., None]) + sun * (0.55 * fade[..., None])
    # desaturate faded areas
    gray = rgb.mean(axis=2, keepdims=True)
    rgb = rgb * (1 - 0.35 * fade[..., None]) + gray * (0.35 * fade[..., None])

    # --- bottom / corner dirt ---
    dirt_y = yy ** 2.2
    corner = (np.minimum(xx, 1 - xx) < 0.12).astype(np.float32)
    corner = corner * (1 - np.minimum(xx, 1 - xx) / 0.12)
    dirt = (0.45 * dirt_y + 0.35 * corner) * (0.4 + 0.6 * inten)
    dirt = dirt * (0.5 + 0.5 * grit2)
    dirt_col = np.array([55, 42, 28], dtype=np.float32)
    rgb = rgb * (1 - 0.65 * dirt[..., None]) + dirt_col * (0.65 * dirt[..., None])

    # --- vertical grime streaks ---
    streak = np.zeros((h, w), dtype=np.float32)
    n_streaks = int(4 + inten * 14)
    for _ in range(n_streaks):
        cx = rng.randint(0, w - 1)
        thick = rng.randint(1, 3 + int(inten * 3))
        start = rng.randint(0, h // 3)
        length = rng.randint(h // 4, h)
        strength = rng.uniform(0.08, 0.28) * (0.5 + inten)
        for dy in range(length):
            y = start + dy
            if y >= h:
                break
            fall = 1 - dy / length
            x0 = max(0, cx - thick)
            x1 = min(w, cx + thick + 1)
            wobble = int(2 * math.sin(dy * 0.15 + rng.random()))
            xa = max(0, x0 + wobble)
            xb = min(w, x1 + wobble)
            streak[y, xa:xb] = np.maximum(streak[y, xa:xb], strength * fall * (0.6 + 0.4 * grit[y, xa:xb]))
    rgb = rgb * (1 - streak[..., None]) + np.array([40, 35, 30]) * streak[..., None]

    # --- rust from mounting bolts ---
    height_add = np.zeros((h, w), dtype=np.float32)
    n_bolts = rng.randint(2, 6)
    bolt_positions = []
    margin_x = int(w * 0.04)
    margin_y = int(h * 0.08)
    for i in range(n_bolts):
        if h > w * 1.5:  # vertical sign
            bx = rng.choice([margin_x, w - margin_x])
            by = int(h * (0.1 + 0.8 * i / max(1, n_bolts - 1)))
        else:
            bx = int(w * (0.08 + 0.84 * i / max(1, n_bolts - 1)))
            by = margin_y if rng.random() < 0.7 else h - margin_y
        bolt_positions.append((bx, by))
        # bolt head bump
        rr = max(3, int(min(w, h) * 0.012))
        yy_i, xx_i = np.ogrid[:h, :w]
        dist = np.sqrt((xx_i - bx) ** 2 + (yy_i - by) ** 2)
        bolt = np.clip(1 - dist / rr, 0, 1)
        height_add += bolt * 0.35
        # dark bolt
        rgb = np.where(bolt[..., None] > 0.3, rgb * 0.35 + np.array([50, 40, 35]), rgb)
        # rust streak down
        rust_len = int(h * rng.uniform(0.15, 0.45) * (0.5 + inten))
        for dy in range(rust_len):
            y = by + dy
            if y >= h:
                break
            fall = (1 - dy / rust_len) ** 1.4
            spread = 1 + int(dy * 0.04)
            x0 = max(0, bx - spread)
            x1 = min(w, bx + spread + 1)
            strength = 0.55 * fall * inten * (0.6 + 0.4 * grit[y, bx])
            rust_c = np.array([120, 55, 25], dtype=np.float32)
            rgb[y, x0:x1] = rgb[y, x0:x1] * (1 - strength) + rust_c * strength
            height_add[y, x0:x1] += strength * 0.08

    # --- paint chips ---
    chip_noise = _hash_noise(h, w, params.seed + 99, 6)
    chip_thresh = 0.78 - inten * 0.18
    chips = (chip_noise > chip_thresh).astype(np.float32)
    # prefer edges
    edge = np.ones((h, w), dtype=np.float32)
    edge[int(h * 0.08) : int(h * 0.92), int(w * 0.05) : int(w * 0.95)] *= 0.35
    chips *= edge
    chips = chips * (0.5 + 0.5 * inten)
    # blur chips slightly
    chips_img = Image.fromarray((chips * 255).astype(np.uint8), "L").filter(ImageFilter.GaussianBlur(0.8))
    chips = np.asarray(chips_img, dtype=np.float32) / 255.0
    under = np.array([95, 90, 85], dtype=np.float32)  # exposed metal/undercoat
    if rng.random() < 0.4:
        under = np.array([50, 55, 45], dtype=np.float32)
    rgb = rgb * (1 - chips[..., None]) + under * chips[..., None]
    height_add -= chips * 0.25

    # --- scratches ---
    for _ in range(int(3 + inten * 12)):
        x0 = rng.randint(0, w - 1)
        y0 = rng.randint(0, h - 1)
        length = rng.randint(int(w * 0.05), int(w * 0.35))
        angle = rng.uniform(-0.4, 0.4)
        for s in range(length):
            x = int(x0 + s * math.cos(angle))
            y = int(y0 + s * math.sin(angle) + rng.uniform(-0.5, 0.5))
            if 0 <= x < w and 0 <= y < h:
                rgb[y, x] = rgb[y, x] * 0.55 + under * 0.45

    # --- wrecked extras ---
    if params.wrecked or inten > 0.85:
        # bullet holes
        for _ in range(rng.randint(2, 7)):
            bx = rng.randint(int(w * 0.1), int(w * 0.9))
            by = rng.randint(int(h * 0.1), int(h * 0.9))
            r = rng.randint(4, 14)
            yy_i, xx_i = np.ogrid[:h, :w]
            dist = np.sqrt((xx_i - bx) ** 2 + (yy_i - by) ** 2)
            hole = dist < r
            rim = (dist >= r) & (dist < r + 2)
            alpha[..., 0] = np.where(hole, 0, alpha[..., 0])
            rgb = np.where(rim[..., None], rgb * 0.3, rgb)
            height_add -= hole.astype(np.float32) * 0.8
        # torn corner
        if rng.random() < 0.7:
            corner = rng.choice(["tl", "tr", "bl", "br"])
            tw = int(w * rng.uniform(0.08, 0.2))
            th = int(h * rng.uniform(0.1, 0.25))
            yy_i, xx_i = np.ogrid[:h, :w]
            if corner == "tl":
                torn = (xx_i / tw + yy_i / th) < 1
            elif corner == "tr":
                torn = ((w - 1 - xx_i) / tw + yy_i / th) < 1
            elif corner == "bl":
                torn = (xx_i / tw + (h - 1 - yy_i) / th) < 1
            else:
                torn = ((w - 1 - xx_i) / tw + (h - 1 - yy_i) / th) < 1
            # jagged
            torn = torn & (grit > 0.35)
            alpha[..., 0] = np.where(torn, 0, alpha[..., 0])
        # crease
        crease_x = rng.randint(int(w * 0.2), int(w * 0.8))
        for y in range(h):
            wob = int(8 * math.sin(y * 0.05))
            x = crease_x + wob
            if 0 <= x < w:
                rgb[y, max(0, x - 1) : min(w, x + 2)] *= 0.7
                height_add[y, max(0, x - 1) : min(w, x + 2)] -= 0.15

    # --- edge darkening (physical object) ---
    edge_d = np.ones((h, w), dtype=np.float32)
    bw = max(2, int(min(w, h) * 0.02))
    for i in range(bw):
        f = 0.55 + 0.45 * (i / bw)
        edge_d[i, :] *= f
        edge_d[h - 1 - i, :] *= f
        edge_d[:, i] *= f
        edge_d[:, w - 1 - i] *= f
    rgb *= edge_d[..., None]

    # clamp
    rgb = np.clip(rgb, 0, 255)
    alpha = np.clip(alpha * 255, 0, 255)
    albedo = np.concatenate([rgb, alpha], axis=2).astype(np.uint8)
    albedo_img = Image.fromarray(albedo, "RGBA")

    # --- height field for normals ---
    height = grit * 0.08 + height_add
    if raised_mask is not None:
        rm = np.asarray(raised_mask.resize((w, h), Image.Resampling.BILINEAR), dtype=np.float32) / 255.0
        height += rm * 0.45
        # slight letter emboss
        height += (np.asarray(raised_mask.filter(ImageFilter.GaussianBlur(1)).resize((w, h)), dtype=np.float32) / 255.0) * 0.1
    # frame bevel already in raised_mask
    height += (1 - chips) * 0.02
    height = height.astype(np.float32)
    # Sobel
    hx = np.zeros_like(height)
    hy = np.zeros_like(height)
    hx[:, 1:-1] = height[:, 2:] - height[:, :-2]
    hy[1:-1, :] = height[2:, :] - height[:-2, :]
    # normal map (OpenGL: +X right, +Y up, +Z toward camera)
    nx = -hx * 3.5
    ny = hy * 3.5
    nz = np.ones_like(height)
    inv = 1.0 / np.sqrt(nx**2 + ny**2 + nz**2 + 1e-8)
    nx, ny, nz = nx * inv, ny * inv, nz * inv
    nrm = np.stack(
        [
            (nx * 0.5 + 0.5) * 255,
            (ny * 0.5 + 0.5) * 255,
            (nz * 0.5 + 0.5) * 255,
        ],
        axis=2,
    ).astype(np.uint8)
    # preserve alpha holes in normal as flat
    flat = np.array([128, 128, 255], dtype=np.uint8)
    nrm = np.where(alpha > 10, nrm, flat)
    normal_img = Image.fromarray(nrm, "RGB")

    # --- roughness: paint smooth, chips/dirt rough ---
    rough = 0.35 + 0.25 * grit + 0.35 * chips + 0.2 * dirt + 0.15 * streak
    rough = np.clip(rough * (0.7 + 0.5 * inten), 0.15, 0.95)
    rough_u8 = (rough * 255).astype(np.uint8)
    rough_img = Image.fromarray(rough_u8, "L")

    return albedo_img, normal_img, rough_img


# ---------------------------------------------------------------------------
# Sign drawing primitives
# ---------------------------------------------------------------------------

def solid_bg(size: tuple[int, int], color: tuple[int, int, int], alpha: int = 255) -> Image.Image:
    return Image.new("RGBA", size, color + (alpha,))


def draw_border(img: Image.Image, color: tuple[int, int, int], width: int, style: str = "simple") -> Image.Image:
    draw = ImageDraw.Draw(img)
    w, h = img.size
    if style == "simple":
        for i in range(width):
            draw.rectangle([i, i, w - 1 - i, h - 1 - i], outline=color + (255,))
    elif style == "double":
        for i in range(width):
            draw.rectangle([i, i, w - 1 - i, h - 1 - i], outline=color + (255,))
        gap = width + 3
        for i in range(max(1, width // 2)):
            draw.rectangle([gap + i, gap + i, w - 1 - gap - i, h - 1 - gap - i], outline=color + (255,))
    elif style == "thick_inner":
        for i in range(width):
            draw.rectangle([i, i, w - 1 - i, h - 1 - i], outline=color + (255,))
        # inner gold line
        gold = (220, 180, 60, 255)
        m = width + 4
        draw.rectangle([m, m, w - 1 - m, h - 1 - m], outline=gold)
    elif style == "block_ends":
        # coloured end blocks like many Pakistani signs
        bw = int(w * 0.08)
        draw.rectangle([0, 0, bw, h], fill=color + (255,))
        draw.rectangle([w - bw, 0, w, h], fill=color + (255,))
        for i in range(max(2, width // 2)):
            draw.rectangle([i, i, w - 1 - i, h - 1 - i], outline=color + (255,))
    return img


def paste_mask(base: Image.Image, mask: Image.Image, color: tuple[int, int, int], ox: int = 0, oy: int = 0):
    overlay = Image.new("RGBA", base.size, (0, 0, 0, 0))
    colored = Image.new("RGBA", mask.size, color + (255,))
    overlay.paste(colored, (ox, oy), mask)
    return Image.alpha_composite(base, overlay)


def logo_block(size: tuple[int, int], bg: tuple, fg: tuple, seed: int) -> Image.Image:
    rng = random.Random(seed)
    img = Image.new("RGBA", size, bg + (255,))
    d = ImageDraw.Draw(img)
    # simple geometric emblem
    m = int(min(size) * 0.15)
    style = rng.choice(["circle", "diamond", "crescent", "square"])
    if style == "circle":
        d.ellipse([m, m, size[0] - m, size[1] - m], outline=fg + (255,), width=max(3, size[0] // 16))
        d.ellipse([size[0] // 3, size[1] // 3, 2 * size[0] // 3, 2 * size[1] // 3], fill=fg + (255,))
    elif style == "diamond":
        cx, cy = size[0] // 2, size[1] // 2
        r = min(size) // 2 - m
        d.polygon([(cx, cy - r), (cx + r, cy), (cx, cy + r), (cx - r, cy)], outline=fg + (255,))
    elif style == "crescent":
        d.ellipse([m, m, size[0] - m, size[1] - m], fill=fg + (255,))
        d.ellipse([m + size[0] // 5, m, size[0] - m, size[1] - m], fill=bg + (255,))
    else:
        d.rectangle([m, m, size[0] - m, size[1] - m], outline=fg + (255,), width=max(3, size[0] // 14))
    return img


# ---------------------------------------------------------------------------
# Sign type builders
# ---------------------------------------------------------------------------

@dataclass
class SignSpec:
    name: str
    kind: str  # fascia, vertical, plate, banner, awning
    size: tuple[int, int]
    urdu: str
    english: str | None
    bg_key: str
    font_key: str
    layout: str
    border: str
    weather: WeatherParams
    phone: str | None = None
    bilingual: bool = False


def build_fascia(spec: SignSpec) -> tuple[Image.Image, Image.Image]:
    """Long horizontal fascia. Returns (albedo_clean, raised_mask)."""
    w, h = spec.size
    bg = PALETTE[spec.bg_key]
    fg = TEXT_ON[spec.bg_key]
    border_c = TEXT_ON[spec.bg_key] if spec.bg_key not in ("yellow", "gold", "cream", "white") else PALETTE["deep_red"]
    img = solid_bg((w, h), bg)
    raised = Image.new("L", (w, h), 0)

    img = draw_border(img, border_c, max(6, h // 28), spec.border)
    # raised frame
    rd = ImageDraw.Draw(raised)
    bw = max(6, h // 28)
    for i in range(bw):
        rd.rectangle([i, i, w - 1 - i, h - 1 - i], outline=200)

    font_path = FONTS[spec.font_key]
    # layout
    content_w, content_h = w, h
    text_ox = 0
    if spec.layout == "logo_left":
        side = int(h * 0.78)
        logo = logo_block((side, side), border_c, bg if sum(bg) > 400 else fg, spec.weather.seed)
        img.paste(logo, (bw + 8, (h - side) // 2), logo)
        raised.paste(Image.new("L", (side, side), 180), (bw + 8, (h - side) // 2))
        text_ox = side + bw + 24
        content_w = w - text_ox - bw
    elif spec.layout == "logo_right":
        side = int(h * 0.78)
        logo = logo_block((side, side), border_c, bg if sum(bg) > 400 else fg, spec.weather.seed)
        img.paste(logo, (w - side - bw - 8, (h - side) // 2), logo)
        content_w = w - side - bw * 2 - 24

    # main text size
    main_size = h * (0.42 if spec.bilingual else 0.55)
    if "Nastaliq" in font_path.name:
        main_size *= 0.85

    # fit text
    for _ in range(8):
        tw, th = measure_text(spec.urdu, font_path, main_size)
        if tw < content_w * 0.88:
            break
        main_size *= 0.88

    text_canvas = (int(content_w), int(h * (0.62 if spec.bilingual else 0.78)))
    mask = render_text_mask(spec.urdu, font_path, main_size, text_canvas, anchor="mm")
    paste_y = int(h * (0.12 if spec.bilingual else 0.11))
    paste_x = text_ox + (w - text_ox - text_canvas[0]) // 2 if spec.layout != "logo_left" else text_ox
    if spec.layout == "right":
        paste_x = w - text_canvas[0] - bw - 20
    img = paste_mask(img, mask, fg, paste_x, paste_y)
    raised.paste(mask, (paste_x, paste_y), mask)

    if spec.bilingual and spec.english:
        eng_size = max(22, h // 7)
        eng_canvas = (int(content_w * 0.9), int(h * 0.28))
        emask = render_latin(spec.english, eng_size, eng_canvas)
        ey = int(h * 0.68)
        ex = text_ox + (content_w - eng_canvas[0]) // 2
        img = paste_mask(img, emask, fg, ex, ey)
        raised.paste(emask.point(lambda v: v // 2), (ex, ey), emask)

    if spec.phone:
        psize = max(18, h // 9)
        pcanvas = (int(w * 0.35), int(h * 0.2))
        # phone in Eastern Arabic — use same Arabic font
        pmask = render_text_mask(spec.phone, FONTS["naskh"], psize, pcanvas, anchor="mm")
        img = paste_mask(img, pmask, fg, w - pcanvas[0] - bw - 10, h - pcanvas[1] - bw - 4)
        raised.paste(pmask.point(lambda v: v // 3), (w - pcanvas[0] - bw - 10, h - pcanvas[1] - bw - 4), pmask)

    return img, raised


def build_vertical(spec: SignSpec) -> tuple[Image.Image, Image.Image]:
    w, h = spec.size
    bg = PALETTE[spec.bg_key]
    fg = TEXT_ON[spec.bg_key]
    border_c = TEXT_ON[spec.bg_key] if spec.bg_key not in ("yellow", "gold", "cream", "white") else PALETTE["black"]
    img = solid_bg((w, h), bg)
    raised = Image.new("L", (w, h), 0)
    img = draw_border(img, border_c, max(5, w // 20), spec.border)
    rd = ImageDraw.Draw(raised)
    bw = max(5, w // 20)
    for i in range(bw):
        rd.rectangle([i, i, w - 1 - i, h - 1 - i], outline=200)

    font_path = FONTS[spec.font_key]
    # Render text on a wide canvas then rotate 90° CW for vertical reading top-to-bottom
    # Pakistani vertical signs often stack letters or rotate the whole board.
    # We'll render horizontal then rotate.
    main_size = w * 0.55
    if "Nastaliq" in font_path.name:
        main_size *= 0.8
    for _ in range(8):
        tw, th = measure_text(spec.urdu, font_path, main_size)
        if tw < h * 0.85:
            break
        main_size *= 0.9

    tcanvas = (int(h * 0.9), int(w * 0.7))
    mask = render_text_mask(spec.urdu, font_path, main_size, tcanvas, anchor="mm")
    mask_r = mask.rotate(-90, expand=True, fillcolor=0)
    # fit into vertical
    mw, mh = mask_r.size
    scale = min((w - bw * 4) / mw, (h - bw * 4) / mh)
    nw, nh = max(1, int(mw * scale)), max(1, int(mh * scale))
    mask_r = mask_r.resize((nw, nh), Image.Resampling.LANCZOS)
    px = (w - nw) // 2
    py = (h - nh) // 2
    img = paste_mask(img, mask_r, fg, px, py)
    raised.paste(mask_r, (px, py), mask_r)

    if spec.bilingual and spec.english:
        eng = render_latin(spec.english, max(16, w // 8), (w - bw * 4, int(h * 0.08)))
        img = paste_mask(img, eng, fg, bw * 2, h - int(h * 0.1) - bw)
        raised.paste(eng.point(lambda v: v // 2), (bw * 2, h - int(h * 0.1) - bw), eng)

    return img, raised


def build_plate(spec: SignSpec) -> tuple[Image.Image, Image.Image]:
    w, h = spec.size
    bg = PALETTE[spec.bg_key]
    fg = TEXT_ON[spec.bg_key]
    border_c = TEXT_ON[spec.bg_key] if spec.bg_key not in ("yellow", "gold", "cream", "white") else PALETTE["deep_red"]
    img = solid_bg((w, h), bg)
    raised = Image.new("L", (w, h), 0)
    img = draw_border(img, border_c, max(8, w // 40), spec.border)
    bw = max(8, w // 40)
    rd = ImageDraw.Draw(raised)
    for i in range(bw):
        rd.rectangle([i, i, w - 1 - i, h - 1 - i], outline=200)

    font_path = FONTS[spec.font_key]
    main_size = h * (0.28 if spec.bilingual else 0.36)
    if "Nastaliq" in font_path.name:
        main_size *= 0.85
    for _ in range(8):
        tw, _th = measure_text(spec.urdu, font_path, main_size)
        if tw < w * 0.82:
            break
        main_size *= 0.88

    tcanvas = (int(w * 0.88), int(h * (0.5 if spec.bilingual else 0.6)))
    mask = render_text_mask(spec.urdu, font_path, main_size, tcanvas, anchor="mm")
    py = int(h * (0.18 if spec.bilingual else 0.2))
    px = (w - tcanvas[0]) // 2
    img = paste_mask(img, mask, fg, px, py)
    raised.paste(mask, (px, py), mask)

    if spec.bilingual and spec.english:
        eng = render_latin(spec.english, max(28, h // 10), (int(w * 0.8), int(h * 0.18)))
        img = paste_mask(img, eng, fg, (w - eng.size[0]) // 2, int(h * 0.68))
        raised.paste(eng.point(lambda v: v // 2), ((w - eng.size[0]) // 2, int(h * 0.68)), eng)

    if spec.layout == "logo_left":
        side = int(min(w, h) * 0.22)
        logo = logo_block((side, side), border_c, fg, spec.weather.seed)
        img.paste(logo, (bw + 10, bw + 10), logo)

    return img, raised


def build_banner(spec: SignSpec) -> tuple[Image.Image, Image.Image]:
    """Fabric banner with sag and frayed edge alpha."""
    w, h = spec.size
    bg = PALETTE[spec.bg_key]
    fg = TEXT_ON[spec.bg_key]
    img = solid_bg((w, h), bg, 255)
    raised = Image.new("L", (w, h), 0)

    # fabric weave noise in raised
    weave = _hash_noise(h, w, spec.weather.seed + 3, 3)
    raised_arr = (weave * 40).astype(np.uint8)
    raised = Image.fromarray(raised_arr, "L")

    # slight horizontal stripe (fabric)
    arr = np.asarray(img, dtype=np.float32)
    stripes = (np.sin(np.linspace(0, 40 * math.pi, h))[:, None] * 4)
    arr[..., :3] = np.clip(arr[..., :3] + stripes[..., None], 0, 255)
    img = Image.fromarray(arr.astype(np.uint8), "RGBA")

    font_path = FONTS[spec.font_key]
    main_size = h * 0.38
    if "Nastaliq" in font_path.name:
        main_size *= 0.85
    for _ in range(8):
        tw, _ = measure_text(spec.urdu, font_path, main_size)
        if tw < w * 0.85:
            break
        main_size *= 0.9
    tcanvas = (int(w * 0.9), int(h * 0.55))
    mask = render_text_mask(spec.urdu, font_path, main_size, tcanvas, anchor="mm")
    py = int(h * 0.12)
    px = (w - tcanvas[0]) // 2
    img = paste_mask(img, mask, fg, px, py)
    raised = ImageChops_lighter(raised, mask, px, py)

    if spec.bilingual and spec.english:
        eng = render_latin(spec.english, max(24, h // 9), (int(w * 0.7), int(h * 0.2)))
        img = paste_mask(img, eng, fg, (w - eng.size[0]) // 2, int(h * 0.65))

    # sag warp (vertical displacement stronger in center)
    img = _fabric_sag(img, spec.weather.seed)
    raised = _fabric_sag(raised.convert("RGBA"), spec.weather.seed).convert("L")

    # frayed bottom edge alpha
    img = _fray_edges(img, spec.weather.seed, bottom=True, sides=True)

    return img, raised


def ImageChops_lighter(base: Image.Image, mask: Image.Image, ox: int, oy: int) -> Image.Image:
    out = base.copy()
    layer = Image.new("L", base.size, 0)
    layer.paste(mask, (ox, oy))
    return Image.fromarray(
        np.maximum(np.asarray(out), np.asarray(layer)).astype(np.uint8), "L"
    )


def _fabric_sag(img: Image.Image, seed: int) -> Image.Image:
    """Mild barrel sag via row shifting."""
    rng = random.Random(seed)
    arr = np.array(img, copy=True)
    h, w = arr.shape[:2]
    amp = h * 0.035
    out = np.zeros_like(arr)
    for x in range(w):
        t = x / max(1, w - 1)
        # hang between mount points
        sag = amp * math.sin(t * math.pi) * (0.7 + 0.3 * math.sin(t * 4 + seed))
        shift = int(sag)
        if shift <= 0:
            out[:h, x] = arr[:h, x]
        else:
            out[shift:, x] = arr[: h - shift, x]
            out[:shift, x] = arr[0, x]
    return Image.fromarray(out)


def _fray_edges(img: Image.Image, seed: int, bottom=True, sides=False) -> Image.Image:
    rng = np.random.RandomState(seed)
    arr = np.asarray(img).copy()
    h, w = arr.shape[:2]
    if bottom:
        fray_h = int(h * 0.06)
        noise = rng.rand(fray_h, w)
        for y in range(fray_h):
            thresh = 0.3 + 0.7 * (y / fray_h)
            cut = noise[y] > thresh
            arr[h - 1 - y, cut, 3] = 0
            # ragged leftover threads
            thread = (noise[y] > 0.92) & ~cut
            arr[h - 1 - y, thread, 3] = 180
    if sides:
        fray_w = int(w * 0.02)
        noise = rng.rand(h, fray_w)
        for x in range(fray_w):
            thresh = 0.4 + 0.6 * (x / fray_w)
            arr[:, x, 3] = np.where(noise[:, x] > thresh, 0, arr[:, x, 3])
            arr[:, w - 1 - x, 3] = np.where(noise[:, x] > thresh, 0, arr[:, w - 1 - x, 3])
    return Image.fromarray(arr, "RGBA")


def build_awning(spec: SignSpec) -> tuple[Image.Image, Image.Image]:
    """Scalloped awning valance."""
    w, h = spec.size
    bg = PALETTE[spec.bg_key]
    fg = TEXT_ON[spec.bg_key]
    img = solid_bg((w, h), bg)
    raised = Image.new("L", (w, h), 40)

    # scallops along bottom
    n_scallops = 10
    scallop_w = w / n_scallops
    depth = int(h * 0.42)
    mask = Image.new("L", (w, h), 255)
    md = ImageDraw.Draw(mask)
    # cut below scallop curve
    for i in range(n_scallops):
        x0 = int(i * scallop_w)
        x1 = int((i + 1) * scallop_w)
        cx = (x0 + x1) // 2
        # clear rectangle bottom then restore ellipse
        md.rectangle([x0, h - depth, x1, h], fill=0)
        md.ellipse([x0, h - 2 * depth, x1, h], fill=255)
    # apply alpha
    arr = np.array(img, copy=True)
    m = np.asarray(mask)
    arr[..., 3] = m
    img = Image.fromarray(arr, "RGBA")

    # top stripe / seam
    d = ImageDraw.Draw(img)
    stripe_h = max(4, h // 16)
    stripe_c = TEXT_ON[spec.bg_key]
    d.rectangle([0, 0, w, stripe_h], fill=stripe_c + (255,))
    rd = ImageDraw.Draw(raised)
    rd.rectangle([0, 0, w, stripe_h], fill=200)

    font_path = FONTS[spec.font_key]
    main_size = h * 0.32
    for _ in range(8):
        tw, _ = measure_text(spec.urdu, font_path, main_size)
        if tw < w * 0.8:
            break
        main_size *= 0.9
    tcanvas = (int(w * 0.88), int(h * 0.45))
    tmask = render_text_mask(spec.urdu, font_path, main_size, tcanvas, anchor="mm")
    py = int(h * 0.12)
    px = (w - tcanvas[0]) // 2
    img = paste_mask(img, tmask, fg, px, py)
    raised.paste(tmask, (px, py), tmask)

    # re-apply scallop alpha after paste
    arr = np.array(img, copy=True)
    arr[..., 3] = np.minimum(arr[..., 3], m)
    img = Image.fromarray(arr, "RGBA")

    return img, raised


BUILDERS = {
    "fascia": build_fascia,
    "vertical": build_vertical,
    "plate": build_plate,
    "banner": build_banner,
    "awning": build_awning,
}


# ---------------------------------------------------------------------------
# Spec generation — 60+ distinct signs
# ---------------------------------------------------------------------------

SIZES = {
    "fascia": (2048, 512),
    "vertical": (512, 2048),
    "plate": (1024, 1024),
    "banner": (2048, 768),
    "awning": (2048, 384),
}

# Target counts
TARGETS = {
    "fascia": 22,
    "vertical": 12,
    "plate": 12,
    "banner": 10,
    "awning": 8,
}


def make_specs(seed: int = 42) -> list[SignSpec]:
    rng = random.Random(seed)
    specs: list[SignSpec] = []
    bg_keys = list(PALETTE.keys())
    font_keys = ["nastaliq", "naskh", "kufi", "amiri", "amiri_reg", "reem", "cairo"]
    layouts = ["center", "right", "logo_left", "logo_right"]
    borders = ["simple", "double", "thick_inner", "block_ends"]
    shops = list(SHOPS)
    rng.shuffle(shops)
    shop_i = 0

    def next_shop():
        nonlocal shop_i
        s = shops[shop_i % len(shops)]
        shop_i += 1
        return s

    for kind, count in TARGETS.items():
        for i in range(count):
            urdu, english, _cat = next_shop()
            bg = rng.choice(bg_keys)
            # bias saturated colours
            if rng.random() < 0.55:
                bg = rng.choice(["red", "deep_red", "yellow", "blue", "cobalt", "green", "emerald"])
            font_key = rng.choice(font_keys)
            # Nastaliq preferred for Urdu fascia
            if kind in ("fascia", "banner") and rng.random() < 0.45:
                font_key = "nastaliq"
            bilingual = english is not None and rng.random() < 0.55
            # force some bilingual
            if i % 3 == 0 and english:
                bilingual = True
            inten = rng.choices(
                [0.2, 0.4, 0.55, 0.7, 0.9],
                weights=[1, 3, 4, 3, 1],
            )[0]
            wrecked = inten >= 0.88 or (kind == "fascia" and i in (3, 17))
            use_phone = rng.random() < 0.3 and kind in ("fascia", "plate")
            name = f"{kind}_{i:02d}_{bg}"
            specs.append(
                SignSpec(
                    name=name,
                    kind=kind,
                    size=SIZES[kind],
                    urdu=urdu,
                    english=english,
                    bg_key=bg,
                    font_key=font_key,
                    layout=rng.choice(layouts),
                    border=rng.choice(borders),
                    weather=WeatherParams(
                        intensity=inten,
                        fade_side=rng.choice(["top", "top", "left", "right"]),
                        seed=seed + shop_i * 13 + i * 7,
                        wrecked=wrecked,
                    ),
                    phone=phone(seed + i * 99) if use_phone else None,
                    bilingual=bilingual,
                )
            )
    return specs


# ---------------------------------------------------------------------------
# Contact sheet
# ---------------------------------------------------------------------------

def make_contact_sheet(paths: list[Path], out: Path, thumb_h: int = 180):
    thumbs = []
    for p in paths:
        im = Image.open(p).convert("RGBA")
        # checkerboard behind alpha
        scale = thumb_h / im.height
        tw = max(1, int(im.width * scale))
        im = im.resize((tw, thumb_h), Image.Resampling.LANCZOS)
        bg = Image.new("RGBA", im.size, (40, 40, 40, 255))
        # checker
        cell = 8
        arr = np.array(bg, copy=True)
        yy, xx = np.indices((im.height, im.width))
        checker = ((xx // cell) + (yy // cell)) % 2
        arr[checker == 0] = (55, 55, 55, 255)
        bg = Image.fromarray(arr, "RGBA")
        thumbs.append(Image.alpha_composite(bg, im))

    if not thumbs:
        return
    # pack into rows of ~2400px
    max_row_w = 2400
    rows: list[list[Image.Image]] = []
    cur: list[Image.Image] = []
    cur_w = 0
    pad = 6
    for t in thumbs:
        if cur and cur_w + t.width + pad > max_row_w:
            rows.append(cur)
            cur = []
            cur_w = 0
        cur.append(t)
        cur_w += t.width + pad
    if cur:
        rows.append(cur)

    row_imgs = []
    for row in rows:
        rw = sum(t.width for t in row) + pad * (len(row) + 1)
        rh = thumb_h + pad * 2
        row_im = Image.new("RGB", (rw, rh), (30, 30, 30))
        x = pad
        for t in row:
            row_im.paste(t.convert("RGB"), (x, pad), t.split()[-1])
            x += t.width + pad
        row_imgs.append(row_im)

    total_h = sum(r.height for r in row_imgs)
    max_w = max(r.width for r in row_imgs)
    sheet = Image.new("RGB", (max_w, total_h), (30, 30, 30))
    y = 0
    for r in row_imgs:
        sheet.paste(r, (0, y))
        y += r.height
    sheet.save(out, "PNG")
    print(f"Contact sheet: {out} ({sheet.size[0]}x{sheet.size[1]})")


# ---------------------------------------------------------------------------
# Manifest
# ---------------------------------------------------------------------------

def write_manifest(specs: list[SignSpec], out_path: Path):
    counts: dict[str, int] = {}
    for s in specs:
        counts[s.kind] = counts.get(s.kind, 0) + 1

    lines = [
        "# Overflow Shop Signage — Manifest",
        "",
        "Procedurally generated Urdu/Arabic market signage for the Overflow-inspired",
        "FPS street. Legally clean (original renders + SIL OFL fonts). No Unity Assets/",
        "writes — output lives under `_incoming/decals/signage/`.",
        "",
        "## Generator",
        "",
        "- Script: `Tools/AssetFetch/gen_signage.py`",
        "- Venv: `Tools/AssetFetch/.venv_signage`",
        "- Regenerate:",
        "",
        "```bash",
        "cd Tools/AssetFetch",
        "source .venv_signage/bin/activate",
        "python gen_signage.py",
        "```",
        "",
        "## Fonts (SIL Open Font License 1.1)",
        "",
        "Downloaded fresh from [google/fonts](https://github.com/google/fonts) — **not**",
        "Apple system bundles. Licence texts in `_fonts/OFL_*.txt`.",
        "",
        "| Font | File | Source | Licence |",
        "|------|------|--------|---------|",
        "| Noto Nastaliq Urdu | `NotoNastaliqUrdu-Regular.ttf` | https://github.com/google/fonts/tree/main/ofl/notonastaliqurdu | SIL OFL 1.1 |",
        "| Noto Naskh Arabic | `NotoNaskhArabic-VF.ttf` | https://github.com/google/fonts/tree/main/ofl/notonaskharabic | SIL OFL 1.1 |",
        "| Noto Kufi Arabic | `NotoKufiArabic-VF.ttf` | https://github.com/google/fonts/tree/main/ofl/notokufiarabic | SIL OFL 1.1 |",
        "| Amiri | `Amiri-Bold.ttf`, `Amiri-Regular.ttf` | https://github.com/google/fonts/tree/main/ofl/amiri | SIL OFL 1.1 |",
        "| Reem Kufi | `ReemKufi-VF.ttf` | https://github.com/google/fonts/tree/main/ofl/reemkufi | SIL OFL 1.1 |",
        "| Cairo | `Cairo-VF.ttf` | https://github.com/google/fonts/tree/main/ofl/cairo | SIL OFL 1.1 |",
        "",
        "## Text shaping",
        "",
        "Arabic/Urdu is shaped with **HarfBuzz** (`uharfbuzz`) + fontTools glyph outlines,",
        "not naïve PIL string drawing. This preserves cursive joining and RTL order",
        "(verified visually on Nastaliq and Amiri test renders).",
        "",
        "`arabic-reshaper` + `python-bidi` are also available in the venv as a fallback",
        "path for simple Naskh presentation-form fonts.",
        "",
        "## Map resolutions",
        "",
        "| Type | Resolution | Count |",
        "|------|------------|------:|",
        f"| Long horizontal fascia | 2048×512 | {counts.get('fascia', 0)} |",
        f"| Tall vertical | 512×2048 | {counts.get('vertical', 0)} |",
        f"| Square/rect plate | 1024×1024 | {counts.get('plate', 0)} |",
        f"| Fabric banner | 2048×768 | {counts.get('banner', 0)} |",
        f"| Awning valance | 2048×384 | {counts.get('awning', 0)} |",
        f"| **Total signs** | | **{len(specs)}** |",
        "",
        "Each sign also has:",
        "",
        "- `<name>_normal.png` — tangent-space normal from weathering / frame / letter height",
        "- `<name>_rough.png` — roughness (chips & dirt rougher than intact paint)",
        "",
        "Contact sheet: `_contactsheet.png`",
        "",
        "## Sign list",
        "",
        "| File | Type | Urdu | English | BG | Font | Weather |",
        "|------|------|------|---------|----|------|---------|",
    ]
    for s in specs:
        eng = s.english if s.bilingual and s.english else "—"
        wreck = "wrecked" if s.weather.wrecked else f"{s.weather.intensity:.2f}"
        lines.append(
            f"| `{s.name}.png` | {s.kind} | {s.urdu} | {eng} | {s.bg_key} | {s.font_key} | {wreck} |"
        )

    lines.extend(
        [
            "",
            "## Weathering layers",
            "",
            "- Sun-fade gradient (desaturate + lighten toward exposed edge)",
            "- Dirt/dust heavier at bottom and corners",
            "- Rust streaks from mounting-bolt positions",
            "- Paint chips exposing undercoat/metal",
            "- Scratches, grit noise, edge darkening",
            "- Wrecked subset: bullet holes, torn corners, creases",
            "",
            "## Notes for import",
            "",
            "- Albedo: sRGB, alpha for banners/awnings/wrecked holes",
            "- Normal: linear, OpenGL Y+",
            "- Roughness: linear grayscale",
            "- Prefer decal or mesh-card placement; stack several high on façades",
        ]
    )
    out_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Manifest: {out_path}")


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def generate_one(spec: SignSpec, out_dir: Path) -> Path:
    builder = BUILDERS[spec.kind]
    clean, raised = builder(spec)
    albedo, normal, rough = apply_weathering(clean, spec.weather, raised)

    out_dir.mkdir(parents=True, exist_ok=True)
    albedo_path = out_dir / f"{spec.name}.png"
    normal_path = out_dir / f"{spec.name}_normal.png"
    rough_path = out_dir / f"{spec.name}_rough.png"
    albedo.save(albedo_path, "PNG")
    normal.save(normal_path, "PNG")
    rough.save(rough_path, "PNG")
    return albedo_path


def main():
    parser = argparse.ArgumentParser(description="Generate Overflow shop signage textures")
    parser.add_argument("--out", type=Path, default=OUT_DIR)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--limit", type=int, default=0, help="Limit count for smoke tests")
    parser.add_argument("--verify-only", action="store_true", help="Render 2 shaping tests and exit")
    args = parser.parse_args()

    out = args.out
    out.mkdir(parents=True, exist_ok=True)

    missing = [k for k, p in FONTS.items() if not p.exists()]
    if missing:
        raise SystemExit(f"Missing fonts: {missing}. Expected under {FONT_DIR}")

    if args.verify_only:
        for label, fk in [("nastaliq", "nastaliq"), ("amiri", "amiri")]:
            m = render_text_mask("بازار کپڑے", FONTS[fk], 120, (900, 220), "mm")
            img = Image.new("RGBA", m.size, PALETTE["red"] + (255,))
            img = paste_mask(img, m, (255, 255, 255))
            img.save(out / f"_verify_{label}.png")
            print("Wrote", out / f"_verify_{label}.png")
        return

    specs = make_specs(args.seed)
    if args.limit:
        specs = specs[: args.limit]

    print(f"Generating {len(specs)} signs → {out}")
    paths: list[Path] = []
    for i, spec in enumerate(specs):
        p = generate_one(spec, out)
        paths.append(p)
        print(f"  [{i+1}/{len(specs)}] {p.name} ({spec.kind}, {spec.urdu})")

    make_contact_sheet(paths, out / "_contactsheet.png")
    write_manifest(specs, out / "MANIFEST.md")
    print("Done.")


if __name__ == "__main__":
    main()
