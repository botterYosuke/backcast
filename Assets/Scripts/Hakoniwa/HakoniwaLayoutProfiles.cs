// HakoniwaLayoutProfiles.cs — DEAD SCHEMA (ADR-0017 / findings 0075 §6, owner-locked)
//
// Originally issue #62 "per-mode layout profile". Retired by ADR-0017 (Hakoniwa = floating dock):
// per-mode layouts are no longer the SoT; the dock cluster's geometry lives in the single
// `floatingWindows` list (one cross-mode layout, shape-flip is just `startup` show/hide).
//
// The [Serializable] POCO is RETAINED purely so a pre-#99 sidecar's `hakoniwaProfiles` field
// deserializes cleanly through JsonUtility — the data is then IGNORED by the workspace
// (ApplyLayout no longer reads it, CaptureLayout writes null). The class has NO logic, NO methods
// that interpret the data, and NO references to retired types (HakoniwaBaseTiles is gone).
// Forward-tolerance discipline (findings 0008 §3): keep the type so the document survives a
// round-trip on older readers; let the production code stop consulting it.

using System.Collections.Generic;

// One mode's persisted Hakoniwa grid: a tile-order list. Plain POCO, deserialize-only.
[System.Serializable]
public class HakoniwaProfile
{
    public List<PanelLayout> panels;

    public HakoniwaProfile() { }
    public HakoniwaProfile(List<PanelLayout> panels) { this.panels = panels; }

    public HakoniwaProfile Clone()
    {
        var c = new HakoniwaProfile { panels = new List<PanelLayout>() };
        if (panels != null)
            foreach (var p in panels)
                if (p != null) c.panels.Add(p.Clone());
        return c;
    }
}

[System.Serializable]
public class HakoniwaLayoutProfiles
{
    public HakoniwaProfile replay;
    public HakoniwaProfile live;

    public HakoniwaLayoutProfiles() { }

    public HakoniwaLayoutProfiles Clone() =>
        new HakoniwaLayoutProfiles { replay = replay?.Clone(), live = live?.Clone() };
}
