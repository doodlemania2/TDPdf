#!/usr/bin/env python3
"""Writes build/screenshots/demo/garden-quote-v1.pdf and garden-quote-v2.pdf, the pair the Compare shot
is taken on.

Both come from one function, so everything outside the four planned differences is identical:
a changed price, a changed start date, one added sentence and one removed sentence. Plain Python, no
packages; text is set in the standard Helvetica and Helvetica-Bold (WinAnsi), so it extracts cleanly.
Every name and number is invented (phones use 555-01xx). A fixed byte layout means re-running this
changes nothing. Run it again only if you change the quote; the PDFs are checked in.
"""
from pathlib import Path

HERE = Path(__file__).resolve().parent / "demo"
W, H = 612, 792


def esc(s):
    return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def line(x, top, size, s, font="F1"):
    return f"BT /{font} {size} Tf {x} {H - top} Td ({esc(s)}) Tj ET\n"


def content(v2):
    price = "$4,650" if v2 else "$4,200"
    start = "17 May 2027" if v2 else "3 May 2027"
    c = "0.18 0.40 0.28 rg\n" + line(72, 80, 24, "Quote for garden landscaping", "F2")
    c += "0.35 0.35 0.35 rg\n" + line(72, 102, 11, "Prepared by Fernbank Gardens for Dana Whitfield, 14 Mill Lane. Quote 0142.")
    c += "0.18 0.40 0.28 rg\n" + line(72, 150, 13, "1. What we will do", "F2")
    c += "0.15 0.15 0.15 rg\n"
    c += line(72, 174, 11.5, "We will clear the back garden, lay a gravel path and plant two raised beds along the fence.")
    c += line(72, 192, 11.5, "Topsoil and compost for the beds are included.")
    if not v2:
        c += line(72, 210, 11.5, "Old turf is taken away on the first day.")
    c += "0.18 0.40 0.28 rg\n" + line(72, 252, 13, "2. Price", "F2")
    c += "0.15 0.15 0.15 rg\n"
    c += line(72, 276, 11.5, f"The total price is {price}, paid half on acceptance and half on completion.")
    c += line(72, 294, 11.5, "The price covers all materials and labor for the work listed above.")
    c += "0.18 0.40 0.28 rg\n" + line(72, 336, 13, "3. Timing", "F2")
    c += "0.15 0.15 0.15 rg\n"
    c += line(72, 360, 11.5, f"Work starts on {start} and takes about two weeks, weather permitting.")
    if v2:
        c += line(72, 378, 11.5, "We will send a text message the evening before each visit.")
    c += "0.45 0.45 0.45 rg\n" + line(72, 730, 9, "Fernbank Gardens, 9 Orchard Row. Questions: (555) 014-0133.")
    return c.encode("latin-1")


def build(v2):
    c = content(v2)
    objs = {
        1: b"<< /Type /Catalog /Pages 2 0 R >>",
        2: b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        3: (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Contents 4 0 R "
            f"/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> >>").encode(),
        4: f"<< /Length {len(c)} >>\nstream\n".encode() + c + b"\nendstream",
        5: b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        6: b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
    }
    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    off = {}
    for i in sorted(objs):
        off[i] = len(out)
        out += f"{i} 0 obj\n".encode() + objs[i] + b"\nendobj\n"
    xref = len(out)
    out += f"xref\n0 {len(objs) + 1}\n".encode() + b"0000000000 65535 f \n"
    for i in sorted(objs):
        out += f"{off[i]:010d} 00000 n \n".encode()
    out += f"trailer\n<< /Size {len(objs) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(out)


if __name__ == "__main__":
    for name, v2 in (("garden-quote-v1.pdf", False), ("garden-quote-v2.pdf", True)):
        (HERE / name).write_bytes(build(v2))
        print("wrote", HERE / name)
