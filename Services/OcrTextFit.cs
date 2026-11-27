namespace TDPdf.Services
{
    /// <summary>
    /// Geometry for the invisible OCR text layer: what font size, baseline and horizontal stretch make a
    /// word of the layer cover the box Tesseract found, so selecting it in a viewer follows the printed
    /// word instead of drifting. Pure, so tests/PdfCore compiles this file as-is.
    /// </summary>
    /// <remarks>
    /// The box a line is given is treated as the font's full ascent-to-descent cell. Sizing from that
    /// (not from a single word's ink height) is what keeps "a", "on" or "." the same size as "Hello" on
    /// their line, and sharing the line's baseline is what stops a line from splitting in selection.
    /// </remarks>
    internal static class OcrTextFit
    {
        public const double MinFontSize = 1;
        public const double MinHScale = 0.25;
        public const double MaxHScale = 4;

        /// <summary>
        /// Font size (points) whose ascent-to-descent cell is <paramref name="boxHeight"/> tall.
        /// <paramref name="ascent1000"/> and <paramref name="descent1000"/> are the font's ascent and
        /// descent per 1000 em units; the descent may be given positive or negative.
        /// </summary>
        public static double FontSizeFor(double boxHeight, double ascent1000, double descent1000)
        {
            double cell = Math.Abs(ascent1000) + Math.Abs(descent1000);
            if (!(boxHeight > 0) || !(cell > 0)) return MinFontSize;
            return Math.Max(MinFontSize, boxHeight * 1000.0 / cell);
        }

        /// <summary>
        /// The baseline's y (downward frame, same unit as <paramref name="boxBottom"/>): the bottom of the
        /// box lifted by the descent the font reserves below the baseline at <paramref name="fontSize"/>.
        /// </summary>
        public static double BaselineY(double boxBottom, double fontSize, double descent1000)
            => boxBottom - Math.Abs(descent1000) * fontSize / 1000.0;

        /// <summary>
        /// The horizontal scale that stretches or squeezes a word whose natural width is
        /// <paramref name="naturalWidth"/> onto <paramref name="boxWidth"/>, kept within a sane range so a
        /// mis-boxed word cannot become a sliver or a banner. 1 when either width is unusable.
        /// </summary>
        public static double HScale(double boxWidth, double naturalWidth)
        {
            if (!(boxWidth > 0) || !(naturalWidth > 0)) return 1;
            return Math.Clamp(boxWidth / naturalWidth, MinHScale, MaxHScale);
        }

        /// <summary>
        /// Picks the box height a word is sized from: its line's when the line box is usable and no
        /// shorter than the word's own, else the word's. A line box that is shorter than one of its words
        /// is a recognition glitch, and the word's own height is the safer figure.
        /// </summary>
        public static (double Top, double Bottom) BoxFor(double wordTop, double wordBottom, double lineTop, double lineBottom)
        {
            bool lineOk = lineBottom > lineTop && lineTop <= wordTop && lineBottom >= wordBottom;
            return lineOk ? (lineTop, lineBottom) : (wordTop, wordBottom);
        }
    }
}
