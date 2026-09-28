using PdfSharpCore.Pdf;
using TDPdf.Services;

/// <summary>
/// Pins <see cref="PdfSignatureScan"/>, which is what the save path both WARNS from and SCRUBS
/// from.
/// </summary>
/// <remarks>
/// This walk has two jobs that must not disagree. It counts the signatures a save is about to
/// destroy, so the user can be warned, and it finds the ones to strip, so a file is never written
/// with a signature that fails validation. When those were two separate walks, the failure mode
/// was silent in the worst direction: the scrub removes a signature the counter never saw, so the
/// user is told nothing and the signature goes anyway.
///
/// So what is pinned here is the shapes a real signed PDF actually takes — a field at the top
/// level and one nested under /Kids, both reached through indirect references — plus the two
/// negatives that matter: an UNSIGNED signature field (a form with an empty signature box, where
/// nothing is lost and warning would be noise), and a malformed circular /Kids, which must
/// terminate rather than hang the save.
/// </remarks>
internal static class SignatureScan
{
    /// <summary>A signature field dictionary, signed unless <paramref name="signed"/> is false.</summary>
    private static PdfDictionary SigField(PdfDocument doc, string name, bool signed)
    {
        var field = new PdfDictionary(doc);
        field.Elements["/FT"] = new PdfName("/Sig");
        field.Elements["/T"] = new PdfString(name);
        if (signed)
        {
            var value = new PdfDictionary(doc);
            value.Elements["/Type"] = new PdfName("/Sig");
            doc.Internals.AddObject(value);
            field.Elements["/V"] = value.Reference;
        }
        doc.Internals.AddObject(field);
        return field;
    }

    /// <summary>A document whose /AcroForm /Fields array is assembled by the caller.</summary>
    private static PdfDocument NewDoc(out PdfArray fields)
    {
        var doc = new PdfDocument();
        doc.AddPage();
        var acro = new PdfDictionary(doc);
        fields = new PdfArray(doc);
        acro.Elements["/Fields"] = fields;
        doc.Internals.AddObject(acro);
        doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference;
        return doc;
    }

    internal static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine("\nDigital signature scan");

        // A document with no AcroForm at all is the common case and must be silent.
        {
            var doc = new PdfDocument();
            doc.AddPage();
            Check("an unsigned document reports no signatures",
                  PdfSignatureScan.SignedFields(doc).Count == 0, "");
            Check("an unsigned document reports no usage rights",
                  !PdfSignatureScan.HasUsageRights(doc), "");
        }

        // Top level, nested under /Kids, and an unsigned placeholder alongside them.
        {
            var doc = NewDoc(out var fields);
            fields.Elements.Add(SigField(doc, "TopLevelSigned", signed: true).Reference);
            fields.Elements.Add(SigField(doc, "EmptyBox", signed: false).Reference);

            var parent = new PdfDictionary(doc);
            var kids = new PdfArray(doc);
            kids.Elements.Add(SigField(doc, "NestedSigned", signed: true).Reference);
            parent.Elements["/Kids"] = kids;
            doc.Internals.AddObject(parent);
            fields.Elements.Add(parent.Reference);

            var found = PdfSignatureScan.SignedFields(doc);
            Check("a signed field at the top level is found",
                  found.Any(f => f.Elements.GetString("/T") == "TopLevelSigned"), "");
            Check("a signed field nested under /Kids is found",
                  found.Any(f => f.Elements.GetString("/T") == "NestedSigned"), "");
            Check("an UNSIGNED signature field is not counted — an empty box loses nothing",
                  found.All(f => f.Elements.GetString("/T") != "EmptyBox"), "");
            Check("exactly the two signed fields are found", found.Count == 2, $"found {found.Count}");
        }

        // Circular /Kids: a malformed file must terminate, not hang a save.
        {
            var doc = NewDoc(out var fields);
            var a = new PdfDictionary(doc);
            var kids = new PdfArray(doc);
            doc.Internals.AddObject(a);
            kids.Elements.Add(a.Reference);      // a's /Kids contains a
            a.Elements["/Kids"] = kids;
            fields.Elements.Add(a.Reference);

            bool terminated = false;
            var walk = Task.Run(() => { PdfSignatureScan.SignedFields(doc); terminated = true; });
            walk.Wait(TimeSpan.FromSeconds(5));
            Check("a circular /Kids chain terminates instead of hanging the save", terminated, "");
        }

        // Usage rights are reported independently of any signature.
        {
            var doc = new PdfDocument();
            doc.AddPage();
            var perms = new PdfDictionary(doc);
            doc.Internals.AddObject(perms);
            doc.Internals.Catalog.Elements["/Perms"] = perms.Reference;
            Check("/Perms is reported even with no signed field",
                  PdfSignatureScan.HasUsageRights(doc) && PdfSignatureScan.SignedFields(doc).Count == 0, "");
        }
    }
}
