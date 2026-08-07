using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;

namespace TDPdf.Services
{
    // ============================================================
    // Keyboard-layout aware shortcut matching (upstream KillerPDF #153).
    //
    // WPF's Key enum is a VIRTUAL KEY code, which is POSITIONAL: it says which key was pressed,
    // not what character that key types. Every punctuation shortcut matched by virtual key is
    // therefore a US-layout assumption. On a German keyboard "?" is Shift+ß and "=" is Shift+0,
    // so Ctrl+? and Ctrl+= pressed the keys we were not listening for — and the usual
    // "Keyboard.Modifiers == ModifierKeys.Control" equality test then failed a second time,
    // because producing those characters holds Shift down as well.
    //
    // So punctuation is matched by the character the keystroke PRODUCES under the active layout,
    // which covers German, AZERTY, Nordic and the rest at once instead of one layout at a time.
    // Letters and F-keys keep the virtual-key path: they are positional by nature and cheaper.
    // ============================================================
    internal static class KeyLayout
    {
        [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint idThread);
        [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);
        [DllImport("user32.dll")] private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);
        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        private const uint MapVkToVsc = 0;   // MAPVK_VK_TO_VSC
        private const int VkShift = 0x10;
        private const int VkSpace = 0x20;

        /// <summary>
        /// The character <paramref name="key"/> types on the CURRENT layout, or '\0' when it types
        /// nothing (F-keys, arrows, modifiers). Ctrl is deliberately NOT fed to the translator —
        /// with Ctrl down Windows reports control codes rather than characters.
        /// </summary>
        internal static char CharFor(Key key, bool shift)
        {
            try
            {
                uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                if (vk == 0) return '\0';
                IntPtr hkl = GetKeyboardLayout(0);
                uint sc = MapVirtualKeyEx(vk, MapVkToVsc, hkl);

                var state = new byte[256];
                if (shift) state[VkShift] = 0x80;

                var sb = new StringBuilder(8);
                int rc = ToUnicodeEx(vk, sc, state, sb, sb.Capacity, 0, hkl);

                // DEAD KEYS: a negative result means this is a dead key (accents on many European
                // layouts) and the translator has just swallowed it into its internal state, where
                // it would silently combine with whatever the user types next. Pushing a harmless
                // key through the same call clears it back out — the documented two-call dance.
                // Without it, typing an accent right after a shortcut check yields the wrong letter.
                if (rc < 0)
                {
                    var flush = new StringBuilder(8);
                    ToUnicodeEx(VkSpace, MapVirtualKeyEx(VkSpace, MapVkToVsc, hkl),
                                new byte[256], flush, flush.Capacity, 0, hkl);
                    return '\0';
                }
                return rc > 0 ? sb[sb.Length - 1] : '\0';
            }
            catch { return '\0'; }   // never let a shortcut check throw
        }

        /// <summary>
        /// True when Ctrl (and not Alt) is held and the keystroke types one of <paramref name="chars"/>.
        /// Shift is not required or forbidden, it is simply fed to the translator: on most layouts
        /// the shifted state is exactly how these characters are produced.
        /// </summary>
        /// <remarks>
        /// Because Shift is ignored as a gate, a Ctrl+Shift chord whose shifted character is one of
        /// <paramref name="chars"/> also matches here. Callers that own such a chord must therefore
        /// test it BEFORE calling this — see MainWindow.TryPunctuationShortcut.
        /// </remarks>
        internal static bool IsCtrlChar(Key key, params char[] chars)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) == 0) return false;
            if ((mods & ModifierKeys.Alt) != 0) return false;   // AltGr combinations are not ours
            return Matches(CharFor(key, (mods & ModifierKeys.Shift) != 0), chars);
        }

        /// <summary>
        /// True when Ctrl+Shift (and not Alt) is held and the key's UNSHIFTED character is one of
        /// <paramref name="chars"/>. Unshifted is the right question for a Ctrl+Shift chord: the
        /// Shift belongs to the chord itself, so what identifies the key is its base legend — "="
        /// on US, "+" on German, both reached by the same physical press.
        /// </summary>
        internal static bool IsCtrlShiftChar(Key key, params char[] chars)
        {
            var mods = Keyboard.Modifiers;
            if ((mods & ModifierKeys.Control) == 0) return false;
            if ((mods & ModifierKeys.Shift) == 0) return false;
            if ((mods & ModifierKeys.Alt) != 0) return false;
            return Matches(CharFor(key, shift: false), chars);
        }

        private static bool Matches(char c, char[] chars)
        {
            if (c == '\0') return false;
            foreach (char want in chars) if (c == want) return true;
            return false;
        }

        /// <summary>
        /// Can this character be typed on the current layout WITHOUT Shift? Used to label shortcuts
        /// honestly: on a layout where "=" needs Shift, advertising Ctrl+= is a lie.
        /// </summary>
        internal static bool TypedUnshifted(char ch)
        {
            try
            {
                short r = VkKeyScanEx(ch, GetKeyboardLayout(0));
                if (r == -1) return false;              // not typeable at all here
                return ((r >> 8) & 0xFF) == 0;          // high byte 0 = no modifiers needed
            }
            catch { return false; }
        }

        /// <summary>
        /// The character to print for "zoom in" on this layout: "=" when it is a plain keypress,
        /// otherwise "+". On US both are on the same key and "=" is the unshifted, familiar
        /// spelling; on German "+" is the unshifted one and "=" would need Shift.
        /// </summary>
        internal static string ZoomInChar() => TypedUnshifted('=') ? "=" : "+";

        /// <summary>"-" is unshifted on every layout TDPdf targets, so this is a constant today —
        /// it exists so the two halves of a "+ / -" label are produced the same way.</summary>
        internal static string ZoomOutChar() => "-";
    }
}
