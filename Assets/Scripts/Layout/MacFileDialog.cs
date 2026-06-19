// MacFileDialog.cs — the macOS IFileDialog so File→Open / Save As work on Mac (Win32FileDialog's
// comdlg32 is Windows-only and no-ops off Windows). The app TARGETS Windows; this exists for the
// Mac dev / HITL loop in the Unity Editor, where EditorUtility.OpenFilePanel / SaveFilePanel give
// the native Cocoa panel with ZERO plugin. A non-Editor Mac standalone is not a shipping target,
// so outside the Editor this is a graceful no-op (returns null) — mirroring Win32FileDialog's
// off-platform behaviour (findings 0048 D2/D3: the native dialog is hidden behind IFileDialog).
//
// Same contract as Win32FileDialog: returns the chosen ABSOLUTE .py path, or null on cancel. The
// document anchor is the .py (#78); the picker filters for *.py and Save As defaults the .py ext.

using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class MacFileDialog : IFileDialog
{
    public string OpenStrategy(string initialDir) => Pick(save: false, initialDir, null);

    public string SaveStrategyAs(string initialDir, string initialFileName) => Pick(save: true, initialDir, initialFileName);

    // One funnel for Open and Save (mirrors Win32FileDialog.Show): a single off-Editor no-op and a
    // single ""→null cancel normalisation. Save As passes the suggested name WITHOUT its extension
    // because EditorUtility.SaveFilePanel re-appends "py"; a name that already ends in .py would round
    // -trip to "strategy.py.py" (OnFileSaveAs's Path.GetExtension guard sees ".py" and would not strip it).
    static string Pick(bool save, string initialDir, string initialFile)
    {
#if UNITY_EDITOR
        string p = save
            ? EditorUtility.SaveFilePanel("Save Strategy As", initialDir ?? "",
                  Path.GetFileNameWithoutExtension(initialFile ?? "strategy.py"), "py")
            : EditorUtility.OpenFilePanel("Open Strategy", initialDir ?? "", "py");
        return string.IsNullOrEmpty(p) ? null : p;   // EditorUtility returns "" on cancel; null = cancel (IFileDialog)
#else
        Debug.LogWarning("[FILEDIALOG] macOS native picker is Editor-only -> cancelled.");
        return null;
#endif
    }
}
