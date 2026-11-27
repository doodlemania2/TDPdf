using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TDPdf.Services;

namespace TDPdf
{
    /// <summary>
    /// One entry of the Pages sidebar. The list holds one of these per page, which is cheap; the
    /// bitmap is only attached while the entry is on screen (or just off it), and comes from a
    /// bounded cache — see <see cref="PageThumbStore"/>.
    /// </summary>
    internal sealed class PageThumbItem : INotifyPropertyChanged
    {
        public PageThumbItem(int index, PageThumbSession session)
        {
            Index = index;
            Session = session;
            Label = $"Page {index + 1}";
        }

        public int Index { get; }
        public PageThumbSession Session { get; }
        public string Label { get; }

        /// <summary>True while a container shows this entry (realised by the virtualising panel).</summary>
        public bool Realised;
        /// <summary>True while a render request for this entry is waiting or in flight.</summary>
        public bool Queued;

        private BitmapSource? _thumb;
        public BitmapSource? Thumb
        {
            get => _thumb;
            set
            {
                if (ReferenceEquals(_thumb, value)) return;
                _thumb = value;
                Raise(nameof(Thumb));
                Raise(nameof(ThumbHeight));
                Raise(nameof(ThumbVisibility));
                Raise(nameof(PlaceholderVisibility));
            }
        }

        private ImageSource? _overlay;
        /// <summary>The page's not-yet-saved annotations, drawn over the thumbnail (null when there are none).</summary>
        public ImageSource? Overlay
        {
            get => _overlay;
            set
            {
                if (ReferenceEquals(_overlay, value)) return;
                _overlay = value;
                Raise(nameof(Overlay));
            }
        }

        /// <summary>Shown width of the thumbnail; set by the sidebar (it grows when the sidebar is widened).</summary>
        public double ThumbWidth => Session.ThumbWidth;

        /// <summary>
        /// Shown height. In the two-column sidebar every cell is the same size, so it is the same for
        /// every page (the picture is fitted inside it); otherwise it follows the page's own shape.
        /// </summary>
        public double ThumbHeight => Session.PairedCells
            ? Math.Ceiling(Session.ThumbWidth * SidebarWidth.PageAspect)
            : _thumb is { PixelWidth: > 0 } t ? Session.ThumbWidth * t.PixelHeight / t.PixelWidth : 100;

        /// <summary>Tells the entry's bindings the shown size changed.</summary>
        public void RefreshLayout()
        {
            Raise(nameof(ThumbWidth));
            Raise(nameof(ThumbHeight));
        }
        public Visibility ThumbVisibility => _thumb is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility PlaceholderVisibility => _thumb is null ? Visibility.Visible : Visibility.Collapsed;

        // Read by UI Automation as the entry's name.
        public override string ToString() => Label;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One generation of the sidebar's list: the work queue and cancellation for the
    /// entries built by a single <c>RefreshPageList</c>. Replacing the list abandons the session.</summary>
    internal sealed class PageThumbSession
    {
        public PageThumbSession(object owner, int generation)
        {
            Owner = owner;
            Generation = generation;
        }

        /// <summary>The tab this list belongs to (the window's DocumentContext).</summary>
        public object Owner { get; }
        /// <summary>Cached bitmaps from an older generation are shown but re-rendered.</summary>
        public int Generation { get; }
        public CancellationTokenSource Cts { get; } = new();
        public List<PageThumbItem> Pending { get; } = new();
        public bool Running;
        public bool Cancelled => Cts.IsCancellationRequested;

        /// <summary>Shown thumbnail width for this list (logical pixels); the sidebar keeps it current.</summary>
        public double ThumbWidth = SidebarWidth.ThumbDefault;
        /// <summary>True while the list is the two-column one, whose cells are all the same height.</summary>
        public bool PairedCells;
    }

    /// <summary>Per-tab thumbnail state: a bounded LRU of rendered bitmaps and the live session.</summary>
    internal sealed class PageThumbStore
    {
        /// <summary>Bitmaps kept per tab. At ~256 px a page that is a few tens of MB however long
        /// the document is; anything evicted is simply rendered again when scrolled back to.</summary>
        public const int Capacity = 240;

        public readonly LruCache<int, (BitmapSource Bitmap, int Generation)> Cache = new(Capacity);
        public int Generation;
        public int PageCount = -1;
        public PageThumbSession? Session;

        public void CancelSession()
        {
            if (Session is { } s)
            {
                s.Cts.Cancel();
                s.Pending.Clear();
            }
            Session = null;
        }

        /// <summary>Drops everything: the tab's document is gone or is a different one.</summary>
        public void Clear()
        {
            CancelSession();
            Cache.Clear();
            Generation++;
            PageCount = -1;
        }
    }
}
