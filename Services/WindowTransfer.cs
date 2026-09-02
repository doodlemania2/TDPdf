using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace TDPdf.Services
{
    /// <summary>
    /// Resolves which process owns the top-level window under a screen point, for dragging a tab
    /// out of one TDPdf window and onto (or off of) another. Deliberately Win32-based rather than
    /// WPF drag-and-drop: OLE DragDrop's own "where did this land" signal is ambiguous between a
    /// cancelled drag and a drop onto empty desktop, and TDPdf's custom chrome already leans on
    /// user32 P/Invoke elsewhere (see MainWindow's DwmSetWindowAttribute / SetWindowPos block), so
    /// this follows the same pattern rather than introducing a second interop style.
    /// </summary>
    internal static class WindowHitTest
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private const uint GA_ROOT = 2;

        /// <summary>Process id owning the top-level window at the given screen point, or null if
        /// nothing is there (e.g. the point is over empty desktop).</summary>
        public static int? ProcessIdAtScreenPoint(Point screenPoint)
        {
            var pt = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y };
            IntPtr hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return null;
            hwnd = GetAncestor(hwnd, GA_ROOT);
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == 0 ? null : (int)pid;
        }

        /// <summary>True when the pid belongs to a currently-running TDPdf.exe process other than us.</summary>
        public static bool IsOtherTdpdfProcess(int pid)
        {
            if (pid == Environment.ProcessId) return false;
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return string.Equals(p.ProcessName, "TDPdf", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Already exited, or access denied (a different user's session) — either way, not
                // a window we can hand a document to.
                return false;
            }
        }
    }

    /// <summary>
    /// Per-window named-pipe server that accepts a tab handed over by another TDPdf window's drag.
    /// Independent of App.xaml's "SingleInstanceTabs" pipe (that one folds a SECOND LAUNCH into the
    /// first window and only exists while that setting is on); this one exists for the lifetime of
    /// every window regardless of that setting, since cross-window drag has to work between any two
    /// windows the user has open, however they got there.
    /// </summary>
    internal sealed class WindowTransferServer : IDisposable
    {
        private readonly Func<string, Task<bool>> _importHandler;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly Thread _thread;
        private volatile bool _stopping;

        public WindowTransferServer(System.Windows.Threading.Dispatcher dispatcher, Func<string, Task<bool>> importHandler)
        {
            _dispatcher = dispatcher;
            _importHandler = importHandler;
            _thread = new Thread(ServerLoop) { IsBackground = true, Name = "TDPdf-WindowTransfer" };
            _thread.Start();
        }

        private static string PipeNameFor(int processId) =>
            "TDPdf.Window.Pipe." + Environment.UserName + "." + processId;

        private void ServerLoop()
        {
            string pipeName = PipeNameFor(Environment.ProcessId);
            while (!_stopping)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    using var writer = new StreamWriter(server) { AutoFlush = true };
                    string? line = reader.ReadLine();
                    if (line is null) continue;

                    var parts = line.Split('|', 2);
                    if (parts.Length == 2 && parts[0] == "IMPORT")
                    {
                        string path = parts[1];
                        bool ok;
                        string? err = null;
                        try
                        {
                            // Block this background thread — not the UI thread — until the UI
                            // thread finishes actually opening the tab, so the reply only ever
                            // says OK once the document is genuinely up in the target window.
                            var op = _dispatcher.InvokeAsync(() => _importHandler(path));
                            ok = op.Task.Unwrap().GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            err = ex.Message;
                            ok = false;
                        }
                        writer.WriteLine(ok ? "OK" : $"FAIL:{err ?? "the target window rejected it"}");
                    }
                    else
                    {
                        writer.WriteLine("FAIL:bad request");
                    }
                }
                catch
                {
                    if (_stopping) break;
                    try { Thread.Sleep(150); } catch { /* shutting down */ }
                }
            }
        }

        public void Dispose() => _stopping = true;

        /// <summary>Hands a file path to the window running as <paramref name="destProcessId"/>.
        /// Returns false (with <paramref name="error"/> set) if that window never replies OK —
        /// closed mid-drag, busy, or it declined the file — so the caller can keep its own tab
        /// rather than losing the document.</summary>
        public static bool TryImport(int destProcessId, string path, out string? error, int timeoutMs = 5000)
        {
            error = null;
            try
            {
                using var client = new NamedPipeClientStream(".", PipeNameFor(destProcessId), PipeDirection.InOut);
                client.Connect(timeoutMs);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                using var reader = new StreamReader(client);
                writer.WriteLine($"IMPORT|{path}");
                string? response = reader.ReadLine();
                if (response == "OK") return true;
                error = response ?? "no response";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// The small floating label that follows the cursor while a tab is being dragged, so there is
    /// some feedback before the drop is resolved. WS_EX_TRANSPARENT is essential, not cosmetic:
    /// without it this window itself would be the "top-level window under the cursor" that
    /// WindowHitTest.ProcessIdAtScreenPoint sees, misreporting every drag as landing on ourselves.
    /// </summary>
    internal sealed class TabDragGhost : Window
    {
        public TabDragGhost(string label)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = null;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Focusable = false;
            IsHitTestVisible = false;
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12
                }
            };
            SourceInitialized += (_, _) => MakeClickThrough();
        }

        private void MakeClickThrough()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        public void MoveTo(Point screenTopLeft)
        {
            Left = screenTopLeft.X;
            Top = screenTopLeft.Y;
        }

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x80;
    }
}
