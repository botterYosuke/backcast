// FileDialog.cs — issue #69 "native file picker" (the menu-bar Open/Save As seam)
//
// The swap-point that hides the native file dialog behind an interface (findings 0048 D2/D3),
// the same parser-hiding discipline LayoutStore (JsonUtility) and ScenarioSidecarStore
// (Newtonsoft) use. Production = Win32FileDialog (comdlg32 P/Invoke); the AFK gate injects
// StubFileDialog so Save As / Open round-trips run headless without a real dialog — the C#
// equivalent of TTWR's PendingFileDialog.inject_resolved test seam.
//
// WHY comdlg32 P/Invoke (owner 2026-06-18, findings 0048 D2): backcast is a Windows desktop
// app (pythonnet CPython in-proc + JP broker APIs + platform=win32). comdlg32's
// GetOpenFileNameW / GetSaveFileNameW give the native OS dialog with ZERO third-party
// dependency — a thin wrapper we OWN, vs a vendored unmaintained plugin. The dialog is MODAL
// on the main thread (it runs its own message loop), which IS the single-modal mutual
// exclusion (only one dialog at a time) TTWR's PendingFileDialog enforced explicitly.
//
// The document anchor is the .py (canonical absolute path = document identity, #78); the
// .json sidecar is derived via LayoutSidecarStore.SidecarPathFor. So the picker filters for
// *.py, and Save As defaults the extension to .py.

using System;

// returns the chosen ABSOLUTE .py path, or null when the user cancels.
public interface IFileDialog
{
    string SaveStrategyAs(string initialDir, string initialFileName);
    string OpenStrategy(string initialDir);
}

// AFK seam: the probe sets NextResult (null = cancel) and drives Save As / Open without a
// real dialog. Records the last initial dir/name so a probe can assert the default landing.
public sealed class StubFileDialog : IFileDialog
{
    public string NextResult;
    public string LastInitialDir;
    public string LastInitialFile;

    public string SaveStrategyAs(string initialDir, string initialFileName)
    {
        LastInitialDir = initialDir; LastInitialFile = initialFileName;
        return NextResult;
    }

    public string OpenStrategy(string initialDir)
    {
        LastInitialDir = initialDir; LastInitialFile = null;
        return NextResult;
    }
}
