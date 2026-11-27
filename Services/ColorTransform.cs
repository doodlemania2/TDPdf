namespace TDPdf.Services
{
    /// <summary>
    /// Builds colour transforms through <see cref="ColorEngines.Current"/> and remembers the last few,
    /// keyed by profile, intent and black-point compensation, so switching pages or redrawing the
    /// print preview never rebuilds one. Safe on any thread.
    /// </summary>
    internal static class ColorTransformCache
    {
        private static readonly object Gate = new();
        private static readonly LruCache<string, IColorTransform> Cache = new(8);

        public static IColorTransform? Get(IColorEngine engine, IccProfile source, IccProfile destination,
                                           ColorIntent intent, bool bpc, out string? reason)
        {
            string key = $"{engine.Name}|{source.Id}|{destination.Id}|{(int)intent}|{bpc}";
            lock (Gate)
            {
                if (Cache.TryGet(key, out var hit)) { reason = null; return hit; }
            }
            var made = engine.CreateTransform(source, destination, intent, bpc, out reason);
            if (made is not null) lock (Gate) Cache.Set(key, made);
            return made;
        }

        public static IColorTransform? GetProofing(IColorEngine engine, IccProfile source, IccProfile proof,
                                                   IccProfile display, ColorIntent intent, bool bpc,
                                                   bool gamutWarning, out string? reason)
        {
            string key = $"{engine.Name}|P|{source.Id}|{proof.Id}|{display.Id}|{(int)intent}|{bpc}|{gamutWarning}";
            lock (Gate)
            {
                if (Cache.TryGet(key, out var hit)) { reason = null; return hit; }
            }
            var made = engine.CreateProofingTransform(source, proof, display, intent, bpc, gamutWarning, out reason);
            if (made is not null) lock (Gate) Cache.Set(key, made);
            return made;
        }

        internal static void Clear() { lock (Gate) Cache.Clear(); }
    }

    /// <summary>
    /// The transform applied to what the screen shows: sRGB (what PDFium renders) to the monitor's
    /// profile. Null, which is the state on a plain sRGB monitor, means no pixel is touched at all.
    /// Display callers only; print, OCR, export, flatten and redaction rasters never go through here.
    /// </summary>
    internal static class DisplayColor
    {
        private static volatile IColorTransform? _current;
        private static int _version;

        /// <summary>Bumps whenever the transform changes, so memoised display bitmaps know to rebuild.</summary>
        public static int Version => Volatile.Read(ref _version);

        public static bool IsActive => _current is { IsIdentity: false };

        public static void Set(IColorTransform? transform)
        {
            _current = transform is { IsIdentity: true } ? null : transform;
            Interlocked.Increment(ref _version);
        }

        /// <summary>Converts <paramref name="bgra"/> in place when a monitor transform is active. True when it changed pixels.</summary>
        public static bool Apply(byte[]? bgra, int width, int height)
        {
            var t = _current;
            if (t is null || bgra is null) return false;
            try { t.ApplyBgra(bgra, width, height); return true; }
            catch { return false; /* a display nicety must never break rendering */ }
        }
    }

    /// <summary>
    /// What CMYK without a profile of its own is converted with. In this release that is PDFium's
    /// built-in DeviceCMYK conversion. A bundled working CMYK profile would be injected here as the
    /// document's DefaultCMYK colour space for the render, once its redistribution terms are checked
    /// and PDFium is shown to honour it; until then <see cref="ProfilePath"/> stays null.
    /// </summary>
    internal static class ColorWorkingCmyk
    {
        public static string? ProfilePath => null;
        public static string Describe() => "PDFium's built-in conversion";
    }
}
