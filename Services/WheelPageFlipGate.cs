using System;

namespace TDPdf.Services
{
    // ============================================================
    // Separating fast scrolling from page navigation (upstream KillerPDF #205).
    //
    // In Single Page and Two-Page views a wheel notch that reaches the scroll boundary falls
    // through to page navigation, so the reader can leave the page without the sidebar. The
    // problem is that a FAST scroll arrives at that boundary still carrying its remaining
    // notches: the user meant "get to the bottom of this page", the last few events land after
    // the offset has already clamped, and the document jumps to the next page unasked.
    //
    // Two independent conditions therefore guard the flip:
    //
    //   1. A quiet period after any real content scroll. Events inside it are momentum from the
    //      gesture that just ended, never a fresh intent, so they are dropped outright.
    //   2. A deliberate confirmation afterwards. One standard geared notch is 120, which passes
    //      immediately; a precision touchpad sends many small deltas, which accumulate to the
    //      same 120 within a short window. Reversing direction or pausing restarts the count, so
    //      a hesitant nudge never adds up to a page turn across unrelated gestures.
    //
    // The result is that scrolling keeps its existing speed and a page change costs one extra
    // deliberate gesture, which is the only way to tell the two apart from the delta stream.
    // ============================================================
    internal sealed class WheelPageFlipGate
    {
        private static readonly TimeSpan MomentumQuietPeriod = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ConfirmationWindow  = TimeSpan.FromMilliseconds(650);
        private const int ConfirmationDelta = 120;   // one geared notch

        private DateTime _blockUntilUtc;
        private DateTime _lastEdgeWheelUtc;
        private int _direction;
        private int _accumulatedDelta;

        /// <summary>
        /// Records that the wheel actually scrolled page content. Starts the quiet period, so the
        /// momentum tail of this gesture cannot be mistaken for a page-turn request.
        /// </summary>
        internal void NoteContentScroll(DateTime nowUtc)
        {
            _blockUntilUtc = nowUtc + MomentumQuietPeriod;
            ResetConfirmation();
        }

        /// <summary>
        /// True when this wheel event at the scroll boundary should turn the page. Call only at a
        /// boundary; every other event belongs to <see cref="NoteContentScroll"/>.
        /// </summary>
        internal bool TryConfirm(int delta, DateTime nowUtc)
        {
            if (delta == 0 || nowUtc < _blockUntilUtc)
            {
                ResetConfirmation();
                return false;
            }

            int direction = Math.Sign(delta);
            if (_direction != direction || nowUtc - _lastEdgeWheelUtc > ConfirmationWindow)
            {
                _direction = direction;
                _accumulatedDelta = 0;
            }

            _lastEdgeWheelUtc = nowUtc;
            _accumulatedDelta += Math.Abs(delta);
            if (_accumulatedDelta < ConfirmationDelta) return false;

            ResetConfirmation();
            return true;
        }

        /// <summary>
        /// Clears any part-accumulated gesture. Used when the view changes underneath the wheel
        /// (mode switch, document change) so a stale half-gesture cannot complete later.
        /// </summary>
        internal void Reset()
        {
            _blockUntilUtc = default;
            ResetConfirmation();
        }

        private void ResetConfirmation()
        {
            _lastEdgeWheelUtc = default;
            _direction = 0;
            _accumulatedDelta = 0;
        }
    }
}
