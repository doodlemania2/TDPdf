#!/usr/bin/env python3
"""Writes build/screenshots/demo/bakery-price-list.pdf, the page with a picture on it that the
picture-edit shot clicks.

The page is a one-page price list for an invented bakery, with a small logo drawn here pixel by pixel
(a wheat ear in a green disc) and stored as a DeviceRGB image, which is what TDPdf's in-place picture
edit accepts. Plain Python, no packages; a fixed byte layout means re-running this changes nothing.
Every name and number is invented (phones use 555-01xx). Run it again only if you change the page;
the PDF it writes is checked in.
"""
import math
import zlib
from pathlib import Path

OUT = Path(__file__).resolve().parent / "demo" / "bakery-price-list.pdf"
W, H = 612, 792
N = 192  # logo pixels per side

# Logo placement on the page, points from the top-left. The capture script clicks its centre:
# x 72..252 across, y 96..276 down, so the centre is 0.265 across and 0.235 down the page.
LOGO_X, LOGO_TOP, LOGO_SIZE = 72, 96, 180


def logo_pixels():
    cream, green, wheat = (250, 244, 230), (31, 120, 110), (244, 214, 130)
    c = (N - 1) / 2
    rows = bytearray()
    for y in range(N):
        for x in range(N):
            # 2x2 supersampling for smooth edges.
            acc = [0, 0, 0]
            for sy in (0.25, 0.75):
                for sx in (0.25, 0.75):
                    px, py = x + sx - c - 0.5, y + sy - c - 0.5
                    col = cream
                    if math.hypot(px, py) <= 86:
                        col = green
                        # stem
                        if abs(px) <= 2.2 and -62 <= py <= 66:
                            col = wheat
                        # grains: ellipses leaning out from the stem, three pairs
                        for k, gy in enumerate((-50, -26, -2)):
                            for side in (-1, 1):
                                gx, ang = side * 15, side * 0.6
                                dx, dy = px - gx, py - gy
                                u = dx * math.cos(ang) + dy * math.sin(ang)
                                v = -dx * math.sin(ang) + dy * math.cos(ang)
                                if (u / 9.5) ** 2 + (v / 19) ** 2 <= 1:
                                    col = wheat
                        # top grain
                        if (px / 9.5) ** 2 + ((py + 70) / 17) ** 2 <= 1:
                            col = wheat
                    for i in range(3):
                        acc[i] += col[i]
            rows += bytes(a // 4 for a in acc)
    return bytes(rows)


def text(x, top, size, s, font):
    s = s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    return f"BT /{font} {size} Tf {x} {H - top} Td ({s}) Tj ET\n"


def page_content():
    c = "0.12 0.47 0.43 rg\n" + text(72, 70, 24, "Willow Court Bakery", "F2")
    c += "0.35 0.35 0.35 rg\n" + text(72, 88, 11, "Price list, spring 2027", "F1")
    # The logo, scaled to LOGO_SIZE points.
    c += f"q {LOGO_SIZE} 0 0 {LOGO_SIZE} {LOGO_X} {H - LOGO_TOP - LOGO_SIZE} cm /Logo Do Q\n"
    items = [("Sourdough loaf", "$6.50"), ("Seeded rye", "$7.00"), ("Cinnamon roll", "$3.75"),
             ("Almond croissant", "$4.25"), ("Rhubarb tart", "$5.50")]
    top = 320
    c += "0.12 0.47 0.43 rg\n" + text(72, top - 12, 14, "Today", "F2")
    c += "0.2 0.2 0.2 rg\n"
    for name, price in items:
        top += 28
        c += text(72, top, 12, name, "F1") + text(420, top, 12, price, "F1")
    c += "0.45 0.45 0.45 rg\n" + text(72, 730, 9, "Willow Court Bakery, 9 Orchard Row. Orders: (555) 014-0166.", "F1")
    return c.encode("latin-1")


def build():
    img = zlib.compress(logo_pixels(), 9)
    content = page_content()
    objs = {
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Contents 4 0 R /Resources << "
            f"/Font << /F1 5 0 R /F2 6 0 R >> /XObject << /Logo 7 0 R >> >> >>").encode(),
        5: b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        6: b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
    }
    stream = lambda d, body: d + b"\nstream\n" + body + b"\nendstream"
    objs[4] = stream(f"<< /Length {len(content)} >>".encode(), content)
    objs[7] = stream(f"<< /Type /XObject /Subtype /Image /Width {N} /Height {N} /ColorSpace /DeviceRGB "
                     f"/BitsPerComponent 8 /Filter /FlateDecode /Length {len(img)} >>".encode(), img)
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = {}
    for i in sorted(objs):
        offsets[i] = len(out)
        out += f"{i} 0 obj\n".encode() + objs[i] + b"\nendobj\n"
    xref = len(out)
    out += f"xref\n0 {len(objs) + 1}\n".encode() + b"0000000000 65535 f \n"
    for i in sorted(objs):
        out += f"{offsets[i]:010d} 00000 n \n".encode()
    out += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(out)


if __name__ == "__main__":
    OUT.write_bytes(build())
    print(f"wrote {OUT}")
