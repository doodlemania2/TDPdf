#!/usr/bin/env python3
"""Writes build/screenshots/demo/services-agreement-v2.pdf, the revised agreement the Compare shot is
taken against.

It appends an incremental update to services-agreement.pdf (the original bytes are kept untouched):
the fee and the late-payment rate are painted over and rewritten, and a new section 5 is added. The
people and companies in the file are invented. Plain Python, no packages; a fixed byte layout means
re-running this changes nothing. Run it again only if you change the changes; the PDF is checked in.
"""
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
SRC = HERE / "demo" / "services-agreement.pdf"
OUT = HERE / "demo" / "services-agreement-v2.pdf"

# Object numbers new to the file (the original uses 1..70).
CONTENT, TIMES, HELV_B = 71, 72, 73
PAGE = 2


def tj(font, size, x, top_y, s):
    s = s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    return f"BT /{font} {size} Tf {x} {792 - top_y} Td ({s}) Tj ET\n"


def paint_over(x, top_y, w, h):
    return f"1 1 1 rg {x} {792 - top_y - h} {w} {h} re f\n"


def build_stream():
    # The original page stream leaves its ".24 0 0 -.24 0 792 cm" in force, so undo it first.
    c = "q\n4.1666667 0 0 -4.1666667 0 3300 cm\n"
    c += paint_over(144, 243, 50, 17)
    c += paint_over(373, 262, 25, 17)
    c += "0.1 0.1 0.1 rg\n"
    c += tj("F20", 11.5, 145, 255, "$19,200,")
    c += tj("F20", 11.5, 374, 274, "2%")
    c += "0.1216 0.2275 0.3529 rg\n"
    c += tj("F21", 10.5, 70, 560, "5. Revisions")
    c += "0.1 0.1 0.1 rg\n"
    c += tj("F20", 11.5, 70, 584, "Either party may propose a change in writing. A change takes effect only when both")
    c += tj("F20", 11.5, 70, 601, "parties have signed it.")
    c += "Q\n"
    return c.encode("latin-1")


def main():
    d = SRC.read_bytes()
    start = int(re.search(rb"startxref\s+(\d+)\s+%%EOF\s*$", d).group(1))
    i = d.index(b"2 0 obj\n<</Type /Page")
    j = d.index(b"endobj", i)
    page = d[i:j].decode("latin-1")
    page = page.replace("/Font <</F4 4 0 R", f"/Font <</F20 {TIMES} 0 R\n/F21 {HELV_B} 0 R\n/F4 4 0 R", 1)
    page = page.replace("/Contents 10 0 R", f"/Contents [10 0 R {CONTENT} 0 R]", 1)
    assert "/F20" in page and f"{CONTENT} 0 R]" in page

    stream = build_stream()
    out = bytearray(d)
    if not out.endswith(b"\n"):
        out += b"\n"
    off = {}

    def add(n, body):
        off[n] = len(out)
        out.extend(f"{n} 0 obj\n".encode() + body + b"\nendobj\n")

    add(PAGE, page.split("obj\n", 1)[1].encode("latin-1"))
    add(CONTENT, f"<</Length {len(stream)}>>\nstream\n".encode() + stream + b"endstream")
    add(TIMES, b"<</Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding>>")
    add(HELV_B, b"<</Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding>>")

    xref = len(out)
    out += b"xref\n"
    out += f"{PAGE} 1\n{off[PAGE]:010d} 00000 n \n".encode()
    out += f"{CONTENT} 3\n".encode()
    for n in (CONTENT, TIMES, HELV_B):
        out += f"{off[n]:010d} 00000 n \n".encode()
    out += f"trailer\n<</Size 74 /Root 46 0 R /Info 1 0 R /Prev {start}>>\nstartxref\n{xref}\n%%EOF\n".encode()
    OUT.write_bytes(bytes(out))
    print(f"wrote {OUT} ({len(out)} bytes)")


if __name__ == "__main__":
    main()
