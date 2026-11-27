using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace TDPdf.Services
{
    /// <summary>What the Open dialog returned.</summary>
    internal readonly record struct OpenPick(string[] Files, bool NewWindow);

    /// <summary>
    /// The Open dialog with the two things the WPF wrapper cannot do: the preview pane forced on
    /// (FOS_FORCEPREVIEWPANEON) and an "Open in a new window" check box (IFileDialogCustomize).
    /// It is the same Windows dialog; it is just created through its COM interface. Anything that
    /// goes wrong before the dialog is on screen falls back to the WPF dialog.
    /// </summary>
    internal static partial class FileDialogs
    {
        private const string NewWindowLabel = "Open in a new window";
        private const uint NewWindowControlId = 0x7D01;

        /// <summary>
        /// Shows the Windows Open dialog for PDFs (and whatever <see cref="FileDialogFilters.OpenDocuments"/> adds)
        /// with the preview pane on. Null when the user cancels or a returned path is refused.
        /// </summary>
        public static OpenPick? PickDocuments(Window owner, FileDialogPurpose purpose, string title,
                                              string filter, bool multiselect, bool offerNewWindow)
        {
            try
            {
                var pick = ShowComOpen(owner, purpose, title, filter, multiselect, offerNewWindow, out bool cancelled);
                if (cancelled) return null;
                if (pick is { } p)
                {
                    if (!PathsAreUsable(owner, p.Files)) return null;
                    Remember(p.Files[0], isFolder: false);
                    return p;
                }
            }
            catch (Exception)
            {
                // COM refused to create or configure the dialog: use the plain WPF one.
            }

            var dlg = new OpenFileDialog { Filter = filter, Title = title, Multiselect = multiselect };
            return ShowOpen(dlg, purpose, owner) ? new OpenPick(dlg.FileNames, false) : null;
        }

        private static OpenPick? ShowComOpen(Window owner, FileDialogPurpose purpose, string title, string filter,
                                             bool multiselect, bool offerNewWindow, out bool cancelled)
        {
            cancelled = false;
            IFileOpenDialog? dialog = null;
            var items = new List<object>();
            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialogCoClass();

                var specs = FileDialogPolicy.ParseFilter(filter)
                    .Select(f => new ComdlgFilterSpec { Name = f.Name, Spec = f.Pattern }).ToArray();
                if (specs.Length > 0)
                {
                    dialog.SetFileTypes((uint)specs.Length, specs);
                    dialog.SetFileTypeIndex(1);
                }
                dialog.SetTitle(title);
                dialog.SetClientGuid(FileDialogPolicy.GuidFor(purpose));

                dialog.GetOptions(out uint options);
                options |= FosPathMustExist | FosFileMustExist | FosForceFileSystem | FosForcePreviewPaneOn;
                if (multiselect) options |= FosAllowMultiselect;
                dialog.SetOptions(options);

                foreach (var folder in CheckedPlaces())
                {
                    if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref IidShellItem, out IShellItem item) == 0)
                    {
                        items.Add(item);
                        dialog.AddPlace(item, FdapBottom);
                    }
                }

                IFileDialogCustomize? customize = null;
                if (offerNewWindow)
                {
                    customize = (IFileDialogCustomize)dialog;
                    customize.AddCheckButton(NewWindowControlId, NewWindowLabel, false);
                }

                IntPtr hwnd = new WindowInteropHelper(owner).Handle;
                int hr = dialog.Show(hwnd);
                if (hr == HrCancelled) { cancelled = true; return null; }
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                bool newWindow = false;
                if (customize is not null)
                {
                    customize.GetCheckButtonState(NewWindowControlId, out bool on);
                    newWindow = on;
                }

                dialog.GetResults(out IShellItemArray results);
                items.Add(results);
                results.GetCount(out uint count);
                var files = new List<string>();
                for (uint i = 0; i < count; i++)
                {
                    results.GetItemAt(i, out IShellItem item);
                    items.Add(item);
                    item.GetDisplayName(SigdnFileSysPath, out IntPtr raw);
                    try { if (Marshal.PtrToStringUni(raw) is { Length: > 0 } path) files.Add(path); }
                    finally { Marshal.FreeCoTaskMem(raw); }
                }
                return files.Count == 0 ? null : new OpenPick(files.ToArray(), newWindow);
            }
            finally
            {
                foreach (var o in items) { try { Marshal.FinalReleaseComObject(o); } catch { } }
                if (dialog is not null) { try { Marshal.FinalReleaseComObject(dialog); } catch { } }
            }
        }

        // ── COM plumbing (shobjidl_core.h). Vtable order matters: do not reorder methods. ──────────

        private const uint FosAllowMultiselect = 0x00000200;
        private const uint FosPathMustExist = 0x00000800;
        private const uint FosFileMustExist = 0x00001000;
        private const uint FosForceFileSystem = 0x00000040;
        private const uint FosForcePreviewPaneOn = 0x40000000;
        private const uint FdapBottom = 0;
        private const uint SigdnFileSysPath = 0x80058000;
        private const int HrCancelled = unchecked((int)0x800704C7);   // HRESULT_FROM_WIN32(ERROR_CANCELLED)

        private static Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ComdlgFilterSpec
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Name;
            [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
        }

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCoClass { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr parent);
            // IFileDialog
            void SetFileTypes(uint count, [In, MarshalAs(UnmanagedType.LPArray)] ComdlgFilterSpec[] specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, uint placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(in Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
            // IFileOpenDialog
            void GetResults(out IShellItemArray items);
            void GetSelectedItems(out IShellItemArray items);
        }

        [ComImport, Guid("8016b7b3-3d49-4504-a0aa-2a37494e606f"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialogCustomize
        {
            void EnableOpenDropDown(uint id);
            void AddMenu(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
            void AddPushButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
            void AddComboBox(uint id);
            void AddRadioButtonList(uint id);
            void AddCheckButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label,
                                [MarshalAs(UnmanagedType.Bool)] bool isChecked);
            void AddEditBox(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
            void AddSeparator(uint id);
            void AddText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetControlLabel(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetControlState(uint id, out uint state);
            void SetControlState(uint id, uint state);
            void GetEditBoxText(uint id, out IntPtr text);
            void SetEditBoxText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] out bool isChecked);
            void SetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, in Guid handler, in Guid riid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemArray
        {
            void BindToHandler(IntPtr bindContext, in Guid handler, in Guid riid, out IntPtr result);
            void GetPropertyStore(uint flags, in Guid riid, out IntPtr store);
            void GetPropertyDescriptionList(IntPtr key, in Guid riid, out IntPtr list);
            void GetAttributes(uint flags, uint mask, out uint attributes);
            void GetCount(out uint count);
            void GetItemAt(uint index, out IShellItem item);
            void EnumItems(out IntPtr enumerator);
        }
    }
}
