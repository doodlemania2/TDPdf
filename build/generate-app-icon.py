#!/usr/bin/env python3
"""
Regenerates Resources/tdpdf-icon.ico — the TDPdf application icon.

WHY THIS EXISTS
The application icon used to be the Doodle Project logo at full bleed, which made every
window, taskbar button and Alt-Tab entry read as "the company" rather than "the PDF editor".
This composes the same brand mark onto a document instead: the silhouette says what the app
is, the colour says whose it is.

WHY IT IS GENERATED AND NOT A BINARY DROPPED IN A FOLDER
The gold mark is lifted out of Resources/splash-logo.png at build time rather than traced by
hand, so the icon cannot drift from the brand asset — change the logo, re-run this, and the
icon follows. Gold is the only thing in that image where R > G (gold +22, green -35), so the
channel difference separates mark from background cleanly, antialiasing included.

THREE ARTWORKS, NOT ONE SCALED DOWN
Windows asks for sizes from 16px to 256px. Detail that reads at 256 turns to mush at 16, so
the smaller entries drop the suggestion of text and give the mark progressively more of the
page. That is why the ICO container is written by hand here: Pillow's ICO writer rescales a
single source image and cannot take a different artwork per size.

USAGE
    python3 build/generate-app-icon.py            # writes Resources/tdpdf-icon.ico
    python3 build/generate-app-icon.py --preview  # also writes contact sheets to /tmp

Requires Pillow. It is a build-time-only dependency and deliberately not in the csproj: the
icon changes about once a year, and the checked-in .ico is what the build consumes.
"""
import io
import os
import struct
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError:
    sys.exit("Pillow is required: python3 -m venv .venv && .venv/bin/pip install pillow")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC  = os.path.join(ROOT, 'Resources', 'splash-logo.png')
DEST = os.path.join(ROOT, 'Resources', 'tdpdf-icon.ico')

S = 1024                          # working canvas; every size is downscaled from this

# Sampled from splash-logo.png rather than guessed, so the icon matches the brand exactly.
GOLD      = (232, 210, 101)
GREEN_TOP = (120, 155,  51)
GREEN_BOT = ( 82, 124,  58)

# Every size Windows asks for. 256 is the Explorer "extra large" tile; 16/20/24 are the ones
# people actually stare at all day in the taskbar and title bar.
SIZES = [256, 128, 96, 64, 48, 40, 32, 24, 20, 16]

# Three artworks, not one scaled down. The mark is a thin-stroked glyph, so at 16px its
# strokes go sub-pixel and it turns to mush unless it is given most of the page. Verified by
# rendering the tiers at 16/20/24 and comparing: 0.66 of the page width is unreadable there,
# 0.88 is legible, and 1.0 bleeds into the page edge.
def tier_for(size):
    if size >= 48: return 'detailed'   # room for the suggestion of text
    if size >= 32: return 'simple'     # mark only, still comfortably inset
    return 'tiny'                      # mark as large as the page allows


def glyph_mask():
    """Alpha mask of the gold doodle mark, lifted out of the brand logo and trimmed."""
    im = Image.open(SRC).convert('RGB')
    r, g, _ = im.split()
    diff = Image.new('L', im.size)
    rp, gp, dp = r.load(), g.load(), diff.load()
    w, h = im.size
    for y in range(h):
        for x in range(w):
            d = rp[x, y] - gp[x, y]
            dp[x, y] = 0 if d <= -20 else 255 if d >= 5 else int((d + 20) * 255 / 25)
    return diff.crop(diff.getbbox())


def vgrad(size, top, bot):
    g = Image.new('RGB', (1, size[1]))
    d = ImageDraw.Draw(g)
    for y in range(size[1]):
        t = y / max(1, size[1] - 1)
        d.point((0, y), tuple(int(top[i] + (bot[i] - top[i]) * t) for i in range(3)))
    return g.resize(size, Image.BILINEAR)


def fit(mask, box_w, box_h):
    """Scale `mask` to sit inside the box without distortion."""
    w, h = mask.size
    s = min(box_w / w, box_h / h)
    return mask.resize((max(1, int(w * s)), max(1, int(h * s))), Image.LANCZOS)


# rules, glyph fraction of page width, top inset, drop shadow
TIERS = {
    'detailed': (True,  0.66, 300, True),
    'simple':   (False, 0.74,  96, True),
    'tiny':     (False, 0.88,  70, False),
}


def build(tier, gm):
    rules, frac, top_inset, shadow = TIERS[tier]
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))

    # ── Page ────────────────────────────────────────────────────────────────
    # Portrait with the top-right corner turned down. The fold is what makes this
    # read as a document at 16px, so it is deliberately generous.
    pw, ph = 636, 824
    x0, y0 = (S - pw) // 2, (S - ph) // 2
    x1, y1 = x0 + pw, y0 + ph
    fold, rad = 196, 46

    mask = Image.new('L', (S, S), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle([x0, y0, x1, y1], radius=rad, fill=255)
    md.polygon([(x1 - fold, y0 - 2), (x1 + 2, y0 + fold), (x1 + 2, y0 - 2)], fill=0)
    img.paste(vgrad((S, S), GREEN_TOP, GREEN_BOT).convert('RGBA'), (0, 0), mask)

    # The fold, drawn as the sheet's underside so the corner reads as turned rather
    # than merely cut off.
    fl = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    fd = ImageDraw.Draw(fl)
    fd.polygon([(x1 - fold, y0), (x1, y0 + fold), (x1 - fold, y0 + fold)],
               fill=(255, 255, 255, 82))
    fd.line([(x1 - fold, y0), (x1 - fold, y0 + fold), (x1, y0 + fold)],
            fill=(255, 255, 255, 125), width=7)
    img.alpha_composite(fl)

    # ── Content box for the mark ────────────────────────────────────────────
    # Detailed art reserves the top of the page for the ruled lines; the simplified
    # art gives the mark the whole page and a little more size, because at 16-32px
    # the mark IS the icon and anything else is noise.
    if rules:
        rl = Image.new('RGBA', (S, S), (0, 0, 0, 0))
        rd = ImageDraw.Draw(rl)
        lx, ly, lh, gap = x0 + 78, y0 + 92, 24, 52
        avail = pw - 156 - fold * 0.62
        # NB: not `frac` — that is the tier's glyph fraction and shadowing it here silently
        # resized the mark to whatever the last rule happened to be.
        for i, rule_w in enumerate((0.92, 0.66, 0.80)):
            rd.rounded_rectangle([lx, ly + i * gap, lx + avail * rule_w, ly + i * gap + lh],
                                 radius=lh // 2, fill=(255, 255, 255, 66))
        img.alpha_composite(rl)
    top = y0 + top_inset

    box = (int(pw * frac), int((y1 - 78) - top))
    g = fit(gm, *box)
    gx = x0 + (pw - g.size[0]) // 2
    gy = top + (box[1] - g.size[1]) // 2

    # Soft shadow so gold-on-green keeps its edge through hard downscaling. Omitted on the
    # tiny tier, where at 16px it only muddies the few pixels the mark actually has.
    if shadow:
        sh = Image.new('RGBA', (S, S), (0, 0, 0, 0))
        sh.paste((0, 0, 0, 85), (gx, gy + 9), g)
        img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(10)))

    gold = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    gold.paste(GOLD + (255,), (gx, gy), g)
    img.alpha_composite(gold)
    return img


def write_ico(path, frames):
    """Write a PNG-payload ICO. frames is [(size, PIL image)], largest first.

    Vista and later accept PNG payloads at every size, which is what the project's
    existing pdf-file.ico already uses, and it keeps the file a fraction of the size
    of the equivalent BMP-with-AND-mask encoding.
    """
    blobs = []
    for _, im in frames:
        b = io.BytesIO()
        im.save(b, format='PNG', optimize=True)
        blobs.append(b.getvalue())

    out = io.BytesIO()
    out.write(struct.pack('<HHH', 0, 1, len(frames)))
    offset = 6 + 16 * len(frames)
    for (size, _), blob in zip(frames, blobs):
        # 0 means 256 in the directory entry — a byte cannot hold 256.
        d = 0 if size >= 256 else size
        out.write(struct.pack('<BBBBHHII', d, d, 0, 0, 1, 32, len(blob), offset))
        offset += len(blob)
    for blob in blobs:
        out.write(blob)
    with open(path, 'wb') as f:
        f.write(out.getvalue())
    return len(out.getvalue())


def main():
    gm = glyph_mask()
    art = {name: build(name, gm) for name in TIERS}

    frames = [(s, art[tier_for(s)].resize((s, s), Image.LANCZOS)) for s in SIZES]
    n = write_ico(DEST, frames)
    print(f"wrote {DEST} ({n:,} bytes, {len(frames)} sizes: {', '.join(map(str, SIZES))})")

    if '--preview' in sys.argv:
        out = os.environ.get('TMPDIR', '/tmp')
        for name, im in art.items():
            im.save(os.path.join(out, f'tdpdf-icon-{name}.png'))
        W = sum(SIZES) + 16 * (len(SIZES) + 1)
        sheet = Image.new('RGBA', (W, 470), (245, 247, 244, 255))
        dark  = Image.new('RGBA', (W, 170), (30, 32, 30, 255))
        # Bottom-aligned on both grounds, which is how you actually compare weights.
        for ground, base in ((sheet, 290), (dark, 150)):
            x = 16
            for sz, im in frames:
                ground.alpha_composite(im, (x, base - sz))
                x += sz + 16
        sheet.alpha_composite(dark, (0, 300))
        p = os.path.join(out, 'tdpdf-icon-sheet.png')
        sheet.save(p)
        print('preview:', p)


if __name__ == '__main__':
    main()
