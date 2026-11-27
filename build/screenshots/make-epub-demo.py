#!/usr/bin/env python3
"""Writes build/screenshots/demo/window-garden.epub, the small book the epub-open shot opens.

An EPUB is a zip: a "mimetype" entry stored first and uncompressed, META-INF/container.xml, a package
file, a navigation document and the chapters. This one is EPUB 3 with two short chapters. Plain Python,
no packages; fixed timestamps and entry order mean re-running this changes nothing. The book, its
author and its text are invented. Run it again only if you change the book; the file is checked in.
"""
import zipfile
from pathlib import Path

OUT = Path(__file__).resolve().parent / "demo" / "window-garden.epub"
STAMP = (2027, 1, 1, 0, 0, 0)

CONTAINER = """<?xml version="1.0" encoding="UTF-8"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>
"""

OPF = """<?xml version="1.0" encoding="UTF-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="bookid">urn:uuid:5b1f6a0e-7c3d-4e52-9a10-2d4c8e7f3a11</dc:identifier>
    <dc:title>A Short Guide to Window Gardens</dc:title>
    <dc:creator>Priya Ramanathan</dc:creator>
    <dc:language>en</dc:language>
    <meta property="dcterms:modified">2027-01-01T00:00:00Z</meta>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
    <item id="c2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
  </manifest>
  <spine>
    <itemref idref="c1"/>
    <itemref idref="c2"/>
  </spine>
</package>
"""

NAV = """<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head>
<body>
  <nav epub:type="toc"><h1>Contents</h1>
    <ol>
      <li><a href="chapter1.xhtml">1. Choosing a window</a></li>
      <li><a href="chapter2.xhtml">2. What to grow first</a></li>
    </ol>
  </nav>
</body></html>
"""

STYLE = "body{font-family:Georgia,serif;line-height:1.5}h1{font-family:Arial,sans-serif;color:#1f5f58}"


def chapter(title, paragraphs):
    body = "\n".join(f"    <p>{p}</p>" for p in paragraphs)
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<html xmlns="http://www.w3.org/1999/xhtml"><head><title>{title}</title><style>{STYLE}</style></head>
<body>
  <section>
    <h1>{title}</h1>
{body}
  </section>
</body></html>
"""


CH1 = chapter("1. Choosing a window", [
    "A good window garden starts with the light. Stand at the window on a clear morning and notice how long the sun stays on the sill.",
    "A window that gets direct sun for four hours or more suits herbs and small tomatoes. A shadier one is happier with ferns, mint and trailing ivy.",
    "Check the sill itself too. It should hold a long trough, and the glass should be cool enough in winter that leaves do not touch it.",
])
CH2 = chapter("2. What to grow first", [
    "Begin with three plants you will actually use: basil for pasta, chives for eggs and a pot of lettuce for sandwiches.",
    "Water in the morning, a little at a time, and turn each pot a quarter turn every week so the plants grow evenly.",
    "When the first basil leaves are as long as your thumb, pick the top pair. The plant will answer with two new shoots.",
])


def main():
    with zipfile.ZipFile(OUT, "w") as z:
        def put(name, text, compress):
            zi = zipfile.ZipInfo(name, STAMP)
            zi.compress_type = zipfile.ZIP_DEFLATED if compress else zipfile.ZIP_STORED
            zi.external_attr = 0o644 << 16
            z.writestr(zi, text.encode("utf-8"))
        put("mimetype", "application/epub+zip", False)
        put("META-INF/container.xml", CONTAINER, True)
        put("OEBPS/content.opf", OPF, True)
        put("OEBPS/nav.xhtml", NAV, True)
        put("OEBPS/chapter1.xhtml", CH1, True)
        put("OEBPS/chapter2.xhtml", CH2, True)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
