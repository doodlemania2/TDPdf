using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TDPdf
{
    public partial class MainWindow
    {
        // ============================================================
        // App-wide UI scale       (upstream KillerPDF v1.6.5)
        // ============================================================
        //
        // Rolling the mouse wheel over the title-bar logo — or Ctrl+Shift with +/-, Ctrl+Shift+0 to
        // reset — grows or shrinks the app CHROME in fine steps between 70% and 250%, remembered
        // across launches. Three deliberate design points, all inherited from upstream:
        //
        //   * LAYOUT transform, not a render transform. A RenderTransform would bitmap-stretch the
        //     chrome and blur the text; a LayoutTransform makes WPF re-measure and re-rasterize, so
        //     glyphs stay sharp and panels reflow (the toolbar's SafeWrapPanel simply wraps to more
        //     rows as it grows, which is exactly what should happen).
        //
        //   * THE DOCUMENT PANE IS UNTOUCHED. App size and page zoom are two separate controls:
        //     PageContentGrid keeps its own ScaleTransform (Zoom / Ctrl+0/+/-) and this never goes
        //     near it. Scaling the chrome must not change what the page looks like.
        //
        //   * THE TITLE BAR AND FOOTER STAY FIXED, so the logo can never move out from under the
        //     cursor mid-scroll, and the window's own sizing behaviour — including the
        //     WM_GETMINMAXINFO hook in MainWindow_SourceInitialized that keeps a maximized window
        //     off the taskbar — is completely unaffected: that hook works in screen pixels on the
        //     top-level HWND, and nothing here changes the window's outer geometry.
        //
        // VIEW PREFERENCE, NOT A DOCUMENT EDIT: this never touches _isDirty.
        //
        // HOW IT COMPOSES WITH THE SIDEBAR COLLAPSE GLIDE (SetSidebarCollapsed / AnimateSidebarWidth):
        // SidebarCol lives in the UNSCALED outer grid, so its width is in screen px, while
        // everything inside SidebarOuterGrid lays out in logical px at screen/scale. The two are
        // bridged by SbPx() below: every site that pushes one of the logical sidebar constants
        // (the 24px toggle strip, the 180px open width, the 260px cap) into the column converts
        // through it, and BeginSidebarSlide divides back out because the pinned content panel is
        // inside the transform. ApplyAppScale then rescales the live column width, MinWidth,
        // MaxWidth, the remembered expanded width and any in-flight glide target by the same
        // factor, after landing a running animation — so a collapse started at 100% and a scale
        // change at 180% can't fight each other, and the rail's collapsed and expanded widths grow
        // with the thumbnails instead of squeezing them.

        internal double _appScale = 1.0;
        private const double AppScaleMin = 0.7, AppScaleMax = 2.5, AppScaleStep = 0.05;

        /// <summary>
        /// Converts a LOGICAL sidebar width (the constants the collapse logic reasons in) to the
        /// SCREEN px the unscaled SidebarCol expects. Identity at 100%.
        /// </summary>
        private double SbPx(double logical) => logical * _appScale;

        /// <summary>Restores the persisted scale. Called from the constructor.</summary>
        private void InitAppScale()
        {
            double saved;
            try { saved = TDPdf.Properties.Settings.Default.AppScale; }
            catch { return; }   // corrupt user.config: stay at 100%
            if (double.IsNaN(saved) || double.IsInfinity(saved)) return;
            if (Math.Abs(saved - 1.0) < 0.001) return;
            ApplyAppScale(saved);
        }

        // Roll the wheel over the logo: one fine step per notch, no big jumps.
        private void LogoBar_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ApplyAppScale(_appScale + (e.Delta > 0 ? AppScaleStep : -AppScaleStep), persist: true);
            e.Handled = true;
        }

        private void AppScaleUp() => ApplyAppScale(_appScale + AppScaleStep, persist: true);
        private void AppScaleDown() => ApplyAppScale(_appScale - AppScaleStep, persist: true);
        private void AppScaleReset() => ApplyAppScale(1.0, persist: true);

        // The footer chip: click resets, and it carries the same wheel handler as the logo so the
        // gesture is still there when the custom title bar is hidden (native window frame).
        private void AppScaleReset_Click(object sender, RoutedEventArgs e) => AppScaleReset();

        /// <summary>
        /// Applies a new chrome scale, clamped to 70%–250%, optionally persisting it.
        /// </summary>
        private void ApplyAppScale(double scale, bool persist = false)
        {
            double prev = _appScale;
            scale = Math.Round(Math.Clamp(scale, AppScaleMin, AppScaleMax), 3);
            if (Math.Abs(scale - prev) < 0.0005 && !persist) return;
            _appScale = scale;

            // A frozen transform is cheap to share across the chrome hosts. CHROME ONLY — the
            // document pane's Border and PageContentGrid are pointedly absent from this list.
            Transform t;
            if (scale == 1.0)
            {
                t = Transform.Identity;
            }
            else
            {
                var st = new ScaleTransform(scale, scale);
                st.Freeze();
                t = st;
            }
            // MainMenu is deliberately NOT scaled. Its dropdowns render in separate popup windows
            // that sit outside this LayoutTransform, so scaling the bar would leave every submenu
            // at 100% — a visible mismatch that gets worse the further you scale. Upstream scales
            // the toolbar, sidebar and tab strip only, for the same reason.
            _toolbarBorder.LayoutTransform = t;
            _tabStripBorder.LayoutTransform = t;
            _sidebarOuterGrid.LayoutTransform = t;

            // Keep the sidebar's LOGICAL width constant across the change: the column and the
            // remembered widths are screen px, so they grow with the scale. Land any glide in
            // flight first — an animation on ColumnDefinition.Width outranks the local values set
            // here and would swallow them (same reason ToggleFullScreen does this).
            if (Math.Abs(scale - prev) > 0.0005 && prev > 0)
            {
                FinishSidebarAnimation();
                double f = scale / prev;
                _sidebarExpandedWidth *= f;
                _sidebarAnimTarget *= f;
                if (_sidebarCol.Width.GridUnitType == GridUnitType.Pixel)
                    _sidebarCol.Width = new GridLength(_sidebarCol.Width.Value * f);
                if (_sidebarCol.MinWidth > 0) _sidebarCol.MinWidth *= f;
                if (!double.IsPositiveInfinity(_sidebarCol.MaxWidth)) _sidebarCol.MaxWidth *= f;
                // Full screen owns the live column while it is active and restores these on exit.
                if (_fullScreen)
                {
                    if (_fsSidebarWidth.GridUnitType == GridUnitType.Pixel)
                        _fsSidebarWidth = new GridLength(_fsSidebarWidth.Value * f);
                    _fsSidebarMin *= f;
                }
            }

            _appScaleButton.Content = $"UI {(int)Math.Round(scale * 100)}%";

            if (persist)
            {
                try
                {
                    TDPdf.Properties.Settings.Default.AppScale = scale;
                    TDPdf.Properties.Settings.Default.Save();
                }
                catch { /* non-critical user preference */ }
                ShowScaleReadout(scale);
            }
        }

        // The readout is TRANSIENT. Every wheel notch rewrites it and restarts the hide timer, so
        // the footer carries it while you are resizing and gives the line back a beat after you
        // stop. Before this it was a bare SetStatus with nothing to take it down again: it sat on
        // the status bar until some unrelated code — a page change, a tool switch, an open —
        // happened to write over it.
        //
        // It still goes out through SetStatusHeld, because the chrome resize re-runs the fit
        // pipeline and its page/zoom status would otherwise stomp this on the same layout pass
        // (MainWindow.xaml.cs SetStatus). That hold is short and covers only the stomp.
        //
        // The snapshot / hold / restore itself now lives in MainWindow.xaml.cs (FlashStatus), because
        // the status-line file-size flash needs exactly the same behaviour and two copies of a
        // save-and-put-back is how the two drift apart.
        private void ShowScaleReadout(double scale)
            => FlashStatus($"App size {(int)Math.Round(scale * 100)}% — the page keeps its own zoom",
                           holdMs: 1200, life: TimeSpan.FromSeconds(5));
    }
}
