#!/usr/bin/env python3
"""Writes build/screenshots/demo/membership-form.pdf, the fillable form the screenshot run fills in.

Plain Python, no packages: the file is small enough to write by hand, and a fixed byte layout means
re-running this changes nothing. Every name and number in it is invented (phones use 555-01xx).
Run it again only if you change the form; the PDF it writes is checked in.
"""
from pathlib import Path

W, H = 612, 792  # US Letter, points

TEXT_FIELDS = [
    # name, label, x, y (baseline of the box bottom), width
    ("full_name", "Full name", 72, 600, 468),
    ("phone", "Phone", 72, 548, 220),
    ("start_date", "Start date", 320, 548, 220),
    ("plot", "Preferred plot", 72, 496, 220),
]
CHECK = ("newsletter", "Send me the monthly newsletter", 72, 456)
BOX_H = 22


def text(x, y, size, s, font="F1"):
    s = s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
    return f"BT /{font} {size} Tf {x} {y} Td ({s}) Tj ET\n"


def page_content():
    c = "0.18 0.29 0.42 rg\n0 712 612 80 re f\n1 1 1 rg\n"
    c += text(72, 742, 24, "Riverside Community Garden", "F2")
    c += "0.2 0.2 0.2 rg\n"
    c += text(72, 690, 16, "Membership application", "F2")
    c += text(72, 668, 11, "Fill in the form and bring it to the garden shed, or email it back to the committee.")
    c += text(72, 653, 11, "Plots are 3 m by 6 m. The season runs from March to November.")
    c += "0.55 0.55 0.55 RG 0.8 w\n"
    for _, label, x, y, w in TEXT_FIELDS:
        c += "0.2 0.2 0.2 rg\n" + text(x, y + BOX_H + 6, 10, label)
        c += f"{x} {y} {w} {BOX_H} re S\n"
    _, label, x, y = CHECK
    c += f"{x} {y} 14 14 re S\n" + "0.2 0.2 0.2 rg\n" + text(x + 22, y + 3, 11, label)
    c += "0.45 0.45 0.45 rg\n"
    c += text(72, 120, 9, "Riverside Community Garden, 14 Mill Lane. Questions: (555) 014-0178.")
    return c.encode("latin-1")


def build():
    objs = {}
    # 1 catalog, 2 pages, 3 page, 4 content, 5-6 fonts, 7.. fields
    field_ids = []
    n = 7
    for name, _, x, y, w in TEXT_FIELDS:
        objs[n] = (f"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({name}) /V () /Ff 0 "
                   f"/Rect [{x} {y} {x + w} {y + BOX_H}] /P 3 0 R /F 4 /DA (/Helv 11 Tf 0 g) "
                   f"/MK << /BC [0.55 0.55 0.55] >> >>")
        field_ids.append(n)
        n += 1
    name, _, x, y = CHECK
    objs[n] = (f"<< /Type /Annot /Subtype /Widget /FT /Btn /T ({name}) /V /Off /AS /Off "
               f"/Rect [{x} {y} {x + 14} {y + 14}] /P 3 0 R /F 4 /DA (/ZaDb 0 Tf 0 g) "
               f"/MK << /CA (4) /BC [0.55 0.55 0.55] >> >>")
    field_ids.append(n)
    refs = " ".join(f"{i} 0 R" for i in field_ids)
    content = page_content()
    objs[1] = (f"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{refs}] /NeedAppearances true "
               f"/DA (/Helv 11 Tf 0 g) /DR << /Font << /Helv 5 0 R /ZaDb 6 0 R >> >> >> >>")
    objs[2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
    objs[3] = (f"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W} {H}] /Contents 4 0 R "
               f"/Resources << /Font << /F1 5 0 R /F2 8 0 R >> >> /Annots [{refs}] >>")
    objs[4] = None  # stream, written below
    objs[5] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"
    objs[6] = "<< /Type /Font /Subtype /Type1 /BaseFont /ZapfDingbats >>"
    bold = max(objs) + 1
    objs[bold] = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"
    objs[3] = objs[3].replace("/F2 8 0 R", f"/F2 {bold} 0 R")

    out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    offsets = {}
    for i in sorted(objs):
        offsets[i] = len(out)
        if i == 4:
            out += f"4 0 obj\n<< /Length {len(content)} >>\nstream\n".encode() + content + b"endstream\nendobj\n"
        else:
            out += f"{i} 0 obj\n{objs[i]}\nendobj\n".encode("latin-1")
    xref = len(out)
    count = max(objs) + 1
    out += f"xref\n0 {count}\n0000000000 65535 f \n".encode()
    for i in range(1, count):
        out += f"{offsets[i]:010d} 00000 n \n".encode()
    out += f"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    return bytes(out)


if __name__ == "__main__":
    dest = Path(__file__).resolve().parent / "demo" / "membership-form.pdf"
    dest.write_bytes(build())
    print(f"wrote {dest} ({dest.stat().st_size} bytes)")
