using TDPdf.Services;

/// <summary>
/// Pins <see cref="AnnotationFrame"/>: text boxes, signatures and pictures turn with their page,
/// and their selection box, resize and click area follow the turned box.
/// </summary>
internal static class AnnotationFrameTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

        Check("annotation frame: normalize folds angles",
              AnnotationFrame.Normalize(450) == 90 && AnnotationFrame.Normalize(-90) == 270 && AnnotationFrame.Normalize(360) == 0, "");

        var v0 = AnnotationFrame.VisualRect(10, 20, 100, 40, 0);
        Check("annotation frame: unrotated box is itself", v0 == (10, 20, 100, 40), $"{v0}");
        var v180 = AnnotationFrame.VisualRect(10, 20, 100, 40, 180);
        Check("annotation frame: 180 keeps the box", v180 == (10, 20, 100, 40), $"{v180}");
        // 100x40 at (10,20): centre (60,40). Turned a quarter it is 40x100 about the same centre.
        var v90 = AnnotationFrame.VisualRect(10, 20, 100, 40, 90);
        Check("annotation frame: quarter turn swaps size about the centre", v90 == (40, -10, 40, 100), $"{v90}");
        Check("annotation frame: 270 matches 90", AnnotationFrame.VisualRect(10, 20, 100, 40, 270) == v90, "");

        // Position <-> visual top-left round trip for every angle.
        foreach (int rot in new[] { 0, 90, 180, 270 })
        {
            var v = AnnotationFrame.VisualRect(37, 51, 120, 30, rot);
            var p = AnnotationFrame.PositionFromVisualTopLeft(v.X, v.Y, 120, 30, rot);
            Check($"annotation frame: position round trip at {rot}", Near(p.X, 37) && Near(p.Y, 51), $"{p}");
        }

        // Resizing a turned box keeps its visual top-left: grow 100x40 (turned 90) to 160x60.
        var before = AnnotationFrame.VisualRect(10, 20, 100, 40, 90);
        var np = AnnotationFrame.PositionFromVisualTopLeft(before.X, before.Y, 160, 60, 90);
        var after = AnnotationFrame.VisualRect(np.X, np.Y, 160, 60, 90);
        Check("annotation frame: resize keeps the visual top-left", Near(after.X, before.X) && Near(after.Y, before.Y), $"{after}");
        Check("annotation frame: resize swaps the size for a quarter turn", Near(after.W, 60) && Near(after.H, 160), $"{after}");

        // Page turns. Page 600x800, a 100x40 box at (50,100): centre (100,120).
        var t90 = AnnotationFrame.TurnWithPage(50, 100, 100, 40, 0, 90, 600, 800);
        // Centre maps (x,y) -> (800-120, 100) = (680,100); turned box is 40x100 -> visual TL (660,50); unrotated TL (630,80).
        Check("annotation frame: 90 turn moves the centre and adds the rotation",
              Near(t90.X, 630) && Near(t90.Y, 80) && t90.Rotation == 90, $"{t90}");
        var t270 = AnnotationFrame.TurnWithPage(50, 100, 100, 40, 0, 270, 600, 800);
        // Centre (y, 600-x) = (120, 500); visual TL (100,450); unrotated TL (70,480).
        Check("annotation frame: 270 turn", Near(t270.X, 70) && Near(t270.Y, 480) && t270.Rotation == 270, $"{t270}");
        var t180 = AnnotationFrame.TurnWithPage(50, 100, 100, 40, 0, 180, 600, 800);
        Check("annotation frame: 180 turn keeps the size", Near(t180.X, 450) && Near(t180.Y, 660) && t180.Rotation == 180, $"{t180}");

        // Four quarter turns bring a box back (page size alternates).
        var q = (X: 50.0, Y: 100.0, Rotation: 0);
        double pw = 600, ph = 800;
        for (int i = 0; i < 4; i++)
        {
            q = AnnotationFrame.TurnWithPage(q.X, q.Y, 100, 40, q.Rotation, 90, pw, ph);
            (pw, ph) = (ph, pw);
        }
        Check("annotation frame: four quarter turns come home", Near(q.X, 50) && Near(q.Y, 100) && q.Rotation == 0, $"{q}");

        // A turned box is kept on the new page: a tall box on the long edge of a landscape result.
        var edge = AnnotationFrame.TurnWithPage(500, 10, 90, 400, 0, 90, 600, 800);   // box runs off the new 800x600 page?
        var ev = AnnotationFrame.VisualRect(edge.X, edge.Y, 90, 400, edge.Rotation);
        Check("annotation frame: turned box stays on the page",
              ev.X >= -1e-9 && ev.Y >= -1e-9 && ev.X + ev.W <= 800 + 1e-9 && ev.Y + ev.H <= 600 + 1e-9, $"{ev}");

        // Rotation already present adds up, and a zero turn changes nothing.
        var add = AnnotationFrame.TurnWithPage(50, 100, 100, 40, 270, 180, 600, 800);
        Check("annotation frame: rotations add (270 + 180 = 90)", add.Rotation == 90, $"{add}");
        var zero = AnnotationFrame.TurnWithPage(50, 100, 100, 40, 90, 0, 600, 800);
        Check("annotation frame: zero turn is a no-op", zero == (50, 100, 90), $"{zero}");
    }
}
