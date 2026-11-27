using System.Text;
using PdfSharpCore.Pdf;

namespace TDPdf.Services
{
    /// <summary>
    /// Gives a picture drawn onto a page of a TAGGED PDF a place in the structure tree: a
    /// <c>/Figure</c> element with an <c>/Alt</c> description, tied to the marked-content sequence
    /// that draws it. Without one, a screen reader either skips the picture or reads nothing for the
    /// text it replaced.
    /// </summary>
    /// <remarks>
    /// Only the common shape is edited: a /StructTreeRoot whose /ParentTree is a flat /Nums array.
    /// A tree split into /Kids is left alone and the caller falls back to <see cref="AltHexString"/>
    /// in the marked-content property list, which readers also honour. Nothing here is guessed: any
    /// surprise returns false before the document has been touched.
    /// </remarks>
    internal static class PdfTaggedFigure
    {
        /// <summary>True when the document says it is tagged: /MarkInfo /Marked true, or a structure tree.</summary>
        internal static bool IsTagged(PdfDocument doc)
        {
            var cat = doc.Internals.Catalog;
            if (cat.Elements.GetDictionary("/StructTreeRoot") is not null) return true;
            var mark = cat.Elements.GetDictionary("/MarkInfo");
            return mark is not null && mark.Elements["/Marked"] is PdfBoolean { Value: true };
        }

        /// <summary>
        /// Adds a /Figure with <paramref name="alt"/> for the next marked-content sequence on
        /// <paramref name="page"/>, whose MCID is returned. False (and nothing changed) when the
        /// structure tree is not one this can extend.
        /// </summary>
        internal static bool TryAdd(PdfDocument doc, PdfPage page, string alt, out int mcid)
        {
            mcid = -1;
            try
            {
                var root = doc.Internals.Catalog.Elements.GetDictionary("/StructTreeRoot");
                var tree = root?.Elements.GetDictionary("/ParentTree");
                var nums = tree?.Elements.GetArray("/Nums");
                if (root is null || tree is null || nums is null) return false;
                if (tree.Elements.ContainsKey("/Kids") && nums.Elements.Count == 0) return false;
                if (root.Reference is null || page.Reference is null) return false;

                // The array this page's MCIDs index into.
                PdfArray? byMcid = null;
                int key;
                bool newKey = false;
                if (page.Elements.ContainsKey("/StructParents"))
                {
                    key = page.Elements.GetInteger("/StructParents");
                    for (int i = 0; i + 1 < nums.Elements.Count; i += 2)
                    {
                        if (PdfItems.Deref(nums.Elements[i]) is PdfInteger k && k.Value == key)
                        {
                            byMcid = PdfItems.Deref(nums.Elements[i + 1]) as PdfArray;
                            break;
                        }
                    }
                    if (byMcid is null) return false;
                }
                else
                {
                    int max = -1;
                    for (int i = 0; i + 1 < nums.Elements.Count; i += 2)
                        if (PdfItems.Deref(nums.Elements[i]) is PdfInteger k) max = Math.Max(max, k.Value);
                    key = root.Elements.ContainsKey("/ParentTreeNextKey")
                        ? Math.Max(root.Elements.GetInteger("/ParentTreeNextKey"), max + 1)
                        : max + 1;
                    newKey = true;
                }

                // Where the Figure hangs: under the single /Document element when there is one.
                var parent = root;
                var rootK = PdfItems.Deref(root.Elements["/K"]);
                if (rootK is PdfDictionary onlyDoc) parent = onlyDoc;
                else if (rootK is PdfArray list && list.Elements.Count > 0
                         && PdfItems.DerefDict(list.Elements[0]) is { } first
                         && first.Elements.GetName("/S") == "/Document")
                    parent = first;
                if (parent.Reference is null) return false;

                // ---- Everything checked: mutate.
                if (newKey)
                {
                    byMcid = new PdfArray(doc);
                    doc.Internals.AddObject(byMcid);
                    nums.Elements.Add(new PdfInteger(key));
                    nums.Elements.Add(byMcid.Reference!);
                    root.Elements["/ParentTreeNextKey"] = new PdfInteger(key + 1);
                    page.Elements["/StructParents"] = new PdfInteger(key);
                }
                mcid = byMcid!.Elements.Count;

                var figure = new PdfDictionary(doc);
                figure.Elements["/Type"] = new PdfName("/StructElem");
                figure.Elements["/S"] = new PdfName("/Figure");
                figure.Elements["/P"] = parent.Reference!;
                figure.Elements["/Pg"] = page.Reference;
                figure.Elements["/K"] = new PdfInteger(mcid);
                figure.Elements["/Alt"] = new PdfString(alt, PdfStringEncoding.Unicode);
                doc.Internals.AddObject(figure);

                byMcid.Elements.Add(figure.Reference!);

                var kids = parent.Elements["/K"];
                if (kids is null)
                {
                    var arr = new PdfArray(doc);
                    arr.Elements.Add(figure.Reference!);
                    parent.Elements["/K"] = arr;
                }
                else if (PdfItems.Deref(kids) is PdfArray existing && kids is not PdfDictionary)
                {
                    existing.Elements.Add(figure.Reference!);
                }
                else
                {
                    var arr = new PdfArray(doc);
                    arr.Elements.Add(kids);
                    arr.Elements.Add(figure.Reference!);
                    parent.Elements["/K"] = arr;
                }
                return true;
            }
            catch
            {
                mcid = -1;
                return false;
            }
        }

        /// <summary>
        /// A PDF hex string (<c>&lt;FEFF…&gt;</c>, UTF-16BE) for use in a marked-content property
        /// list, which cannot take an escaped literal safely for arbitrary text.
        /// </summary>
        internal static string AltHexString(string text)
        {
            var sb = new StringBuilder("<FEFF");
            foreach (byte b in Encoding.BigEndianUnicode.GetBytes(text ?? ""))
                sb.Append(b.ToString("X2"));
            return sb.Append('>').ToString();
        }
    }
}
