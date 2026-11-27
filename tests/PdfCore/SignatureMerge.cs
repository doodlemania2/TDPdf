using System.Text.Json;
using TDPdf.Services;

/// <summary>
/// Two TDPdf windows share signatures.json. Saving from one must not drop what the other saved,
/// and a deletion must stick.
/// </summary>
internal static class SignatureMerge
{
    private sealed class Sig { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }
    private static Sig S(string id) => new() { Id = id, Name = "n" + id };
    private static string Ids(IEnumerable<Sig> l) => string.Join(",", l.Select(x => x.Id));

    public static void Run(Action<string, bool, string> Check, string tmp)
    {
        // Window A read [a]. Window B then saved b. A adds c and saves.
        var m = SignatureStoreFile.Merge(new[] { S("a"), S("c") }, new[] { "a" }, new[] { S("a"), S("b") }, x => x.Id);
        Check("signature merge: another window's new signature is kept", Ids(m) == "a,c,b", Ids(m));

        // A deleted a; B had saved b meanwhile.
        m = SignatureStoreFile.Merge(new List<Sig>(), new[] { "a" }, new[] { S("a"), S("b") }, x => x.Id);
        Check("signature merge: a deletion here stays deleted, the other window's addition stays", Ids(m) == "b", Ids(m));

        // B deleted a; A still holds it and adds c.
        m = SignatureStoreFile.Merge(new[] { S("a"), S("c") }, new[] { "a" }, new[] { S("b") }, x => x.Id);
        Check("signature merge: a deletion in the other window is honoured, not resurrected", Ids(m) == "c,b", Ids(m));

        m = SignatureStoreFile.Merge(new[] { S("a") }, new[] { "a" }, null, x => x.Id);
        Check("signature merge: an unreadable file writes this window's list as it is", Ids(m) == "a", Ids(m));

        m = SignatureStoreFile.Merge(new[] { S("a") }, new[] { "a" }, new[] { S("a") }, x => x.Id);
        Check("signature merge: nothing changed elsewhere is a no-op", Ids(m) == "a", Ids(m));

        // End to end on a real file: two "windows" with their own base, interleaved.
        string file = Path.Combine(tmp, "merge-signatures.json");
        if (File.Exists(file)) File.Delete(file);
        var opt = new JsonSerializerOptions { WriteIndented = true };
        var aList = new List<Sig> { S("a1") };
        var a1 = SignatureStoreFile.SaveMerged(file, aList, Array.Empty<string>(), x => x.Id, opt);
        var bList = new List<Sig>(a1);                      // window B starts from what A wrote
        var bBase = a1.Select(x => x.Id).ToList();
        aList.Add(S("a2"));                                  // A adds a2 and saves (B has not seen it)
        SignatureStoreFile.SaveMerged(file, aList, bBase, x => x.Id, opt);
        bList.Add(S("b1"));                                  // B adds b1 and saves with its stale base
        var bOut = SignatureStoreFile.SaveMerged(file, bList, bBase, x => x.Id, opt);
        var onDisk = JsonSerializer.Deserialize<List<Sig>>(File.ReadAllText(file), opt)!;
        Check("signature merge: a file written by two windows has both windows' signatures",
              onDisk.Select(x => x.Id).OrderBy(x => x).SequenceEqual(new[] { "a1", "a2", "b1" }), Ids(onDisk));
        Check("signature merge: the saving window adopts the merged list", Ids(bOut) == Ids(onDisk), Ids(bOut));
    }
}
