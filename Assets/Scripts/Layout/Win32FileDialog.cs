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
}
