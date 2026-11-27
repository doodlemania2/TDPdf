namespace TDPdf.Services
{
    /// <summary>Whole-degree angle arithmetic shared by page, annotation and form-field rotation.</summary>
    internal static class Angles
    {
        /// <summary>
        /// <paramref name="degrees"/> folded into 0..359 (so -90 is 270 and 450 is 90). Unlike
        /// <see cref="AnnotationFrame.Normalize"/>, it does not round: 45 stays 45.
        /// </summary>
        internal static int Mod360(int degrees) => ((degrees % 360) + 360) % 360;
    }
}
