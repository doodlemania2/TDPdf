using TDPdf.Services;

/// <summary>Pins <see cref="Angles.Mod360"/> and the font subset-prefix strip (<see cref="PdfFontStyle.StripSubsetPrefix"/>).</summary>
internal static class AnglesTests
{
    public static void Run(Action<string, bool, string> Check)
    {
        Console.WriteLine();
        Console.WriteLine("Angles and font subset prefix");
        foreach (var (input, want) in new[] { (0, 0), (90, 90), (360, 0), (450, 90), (-90, 270), (-360, 0), (-450, 270), (45, 45), (719, 359), (-1, 359) })
            Check($"mod360({input}) = {want}", Angles.Mod360(input) == want, $"{Angles.Mod360(input)}");
        Check("mod360 is in range at the int limits",
              Angles.Mod360(int.MinValue) is >= 0 and < 360 && Angles.Mod360(int.MaxValue) is >= 0 and < 360, "");
        Check("mod360 does not round (unlike AnnotationFrame.Normalize)", Angles.Mod360(100) == 100 && AnnotationFrame.Normalize(100) == 90, "");

        foreach (var (input, want) in new[]
        {
            ("ABCDEF+Helvetica", "Helvetica"), ("Helvetica", "Helvetica"), ("", ""), ("ABCDEF+", "ABCDEF+"),
            ("+Arial", "Arial"), ("AAA+BBB+Times", "BBB+Times"),
        })
            Check($"subset prefix: \"{input}\" -> \"{want}\"", PdfFontStyle.StripSubsetPrefix(input) == want, PdfFontStyle.StripSubsetPrefix(input));
    }
}
