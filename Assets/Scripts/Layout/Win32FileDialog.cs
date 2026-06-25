// Win32FileDialog.cs — issue #69, the production IFileDialog (native comdlg32 dialog).
//
// Calls GetOpenFileNameW / GetSaveFileNameW directly (findings 0048 D2). The file buffer is a
// manually-managed unmanaged block (Marshal.AllocHGlobal) read back with PtrToStringUni — the
// robust marshalling for an in/out OPENFILENAME buffer (immutable managed strings can't be a
// reliable out-buffer). Modal on the main thread; the OS dialog runs its own message loop.
//
// OFN_NOCHANGEDIR is MANDATORY: comdlg32 changes the PROCESS cwd to the picked directory
// otherwise, which would corrupt #79's strategy-run cwd (Path(strategy_path).parent) and any
// engine relative path. We never let the dialog move the cwd.
//
// Non-Windows is a graceful no-op (returns null) so the project still compiles/runs off-Windows
// even though the app targets Windows only.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

public sealed class Win32FileDialog : IFileDialog
{
    const int OFN_OVERWRITEPROMPT = 0x00000002;
    const int OFN_NOCHANGEDIR     = 0x00000008;   // do NOT let the dialog move the process cwd (#79)
    const int OFN_PATHMUSTEXIST   = 0x00000800;
    const int OFN_FILEMUSTEXIST   = 0x00001000;
    const int OFN_EXPLORER        = 0x00080000;

    const string PyFilter = "Strategy (*.py)\0*.py\0All files (*.*)\0*.*\0\0";

    public string SaveStrategyAs(string initialDir, string initialFileName)
    {
        return Show(save: true, initialDir: initialDir, initialFile: initialFileName,
                    flags: OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER);
    }

    public string OpenStrategy(string initialDir)
    {
        return Show(save: false, initialDir: initialDir, initialFile: null,
                    flags: OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER);
    }

    // #137 S4 (findings 0107 D4): native FOLDER picker for the Settings「Data」DuckDB root [...] button.
    // Uses the modern IFileOpenDialog with FOS_PICKFOLDERS (the Vista+ common item dialog) rather than the
    // legacy SHBrowseForFolder tree. Off Windows this is a graceful no-op (null) so the typed field still
    // works (fail-soft, parity with Show()). The caller never lets a null move the field.
    public string BrowseFolder(string title, string initialDir)
    {
        if (!IsWindows) { Debug.LogWarning("[FILEDIALOG] native folder picker is Windows-only -> cancelled."); return null; }

        IFileOpenDialog dialog = null;
        IShellItem folder = null;
        IShellItem result = null;
        try
        {
            dialog = (IFileOpenDialog)new FileOpenDialogRCW();
            uint opts;
            dialog.GetOptions(out opts);
            dialog.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
            if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
            if (!string.IsNullOrEmpty(initialDir))
            {
                if (SHCreateItemFromParsingName(initialDir, IntPtr.Zero, typeof(IShellItem).GUID, out folder) == 0 && folder != null)
                    dialog.SetFolder(folder);
            }

            int hr = dialog.Show(GetActiveWindow());
            if (hr != 0) return null;   // HRESULT != S_OK → cancel (ERROR_CANCELLED) or error → treat as cancel

            dialog.GetResult(out result);
            string path;
            result.GetDisplayName(SIGDN_FILESYSPATH, out path);
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch (Exception e) { Debug.LogWarning("[FILEDIALOG] folder picker failed -> cancelled: " + e.Message); return null; }
        finally
        {
            // Release IShellItem RCWs before the dialog (LIFO ownership) — the dialog
            // doesn't own them; SHCreateItemFromParsingName / GetResult return AddRef'd refs
            // that we own. GetDisplayName's out string is a managed System.String (not an RCW).
            if (result != null) Marshal.ReleaseComObject(result);
            if (folder != null) Marshal.ReleaseComObject(folder);
            if (dialog != null) Marshal.ReleaseComObject(dialog);
        }
    }

    static bool IsWindows =>
        Application.platform == RuntimePlatform.WindowsPlayer ||
        Application.platform == RuntimePlatform.WindowsEditor;

    static string Show(bool save, string initialDir, string initialFile, int flags)
    {
        if (!IsWindows) { Debug.LogWarning("[FILEDIALOG] native picker is Windows-only -> cancelled."); return null; }

        const int bufChars = 4096;
        IntPtr buf = Marshal.AllocHGlobal(bufChars * sizeof(char));   // UTF-16
        try
        {
            // zero the buffer, then seed the default filename (Save As) so the field is pre-filled.
            Marshal.Copy(new byte[bufChars * sizeof(char)], 0, buf, bufChars * sizeof(char));
            if (!string.IsNullOrEmpty(initialFile))
            {
                byte[] seed = System.Text.Encoding.Unicode.GetBytes(initialFile);
                if (seed.Length <= (bufChars - 1) * sizeof(char)) Marshal.Copy(seed, 0, buf, seed.Length);
            }

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf(typeof(OpenFileName)),
                hwndOwner = GetActiveWindow(),
                lpstrFilter = PyFilter,
                nFilterIndex = 1,
                lpstrFile = buf,
                nMaxFile = bufChars,
                lpstrInitialDir = initialDir,
                lpstrTitle = save ? "Save Strategy As" : "Open Strategy",
                lpstrDefExt = "py",
                Flags = flags,
            };

            bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            if (!ok) return null;   // cancel or error (CommDlgExtendedError) -> treat as cancel

            string picked = Marshal.PtrToStringUni(buf);
            return string.IsNullOrEmpty(picked) ? null : picked;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;        // manually-managed UTF-16 buffer
        public int nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    static extern IntPtr GetActiveWindow();

    // ── IFileOpenDialog (folder picker) COM interop (#137 S4) ──
    const uint FOS_PICKFOLDERS     = 0x00000020;
    const uint FOS_FORCEFILESYSTEM = 0x00000040;
    const uint FOS_PATHMUSTEXIST   = 0x00000800;
    const uint SIGDN_FILESYSPATH   = 0x80058000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]   // CLSID_FileOpenDialog
    class FileOpenDialogRCW { }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);
        // IFileDialog (only the members we use; the rest must still be declared to keep the vtable order)
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        // (remaining IFileDialog / IFileOpenDialog members unused — vtable ends here for our calls)
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid,
                           [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
}
