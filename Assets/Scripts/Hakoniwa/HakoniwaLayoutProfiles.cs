// HakoniwaLayoutProfiles.cs — issue #62 "per-mode layout profile" (DURABLE tier, pure logic + DTO)
//
// The backcast port of TTWR HakoniwaLayoutProfiles (src/ui/hakoniwa.rs): Replay and Live each remember
// their OWN Hakoniwa tile order. PURE (no UnityEngine) so the AFK probe asserts the validity matrix
// headlessly (findings 0029 §5/§7) — the same two-tier discipline as HakoniwaGridMath/HakoniwaBaseTiles.
//
// SCOPE (grill Q1, findings 0029 §0): the ONLY per-mode dimension is the Hakoniwa tile ORDER
// (List<PanelLayout> = _hako.Capture().panels). canvasView / floatingWindows / strategyEditors stay
// flat-shared across modes (TTWR restore.rs flat-restores camera/windows).
//
// SHAPE keyed on the 2-valued base shape (Replay vs Live), NOT the 3-way DisplayMode: LiveManual and
// LiveAuto share the SAME live profile (TTWR HakoniwaLayoutProfile::from_mode → AC3). `live` is that bool.
//
// DTO + LOGIC in one type: HakoniwaProfile/HakoniwaLayoutProfiles are [Serializable] so JsonUtility
// round-trips them as LayoutDocument.hakoniwaProfiles (the new SoT); the logic methods (Get/Set/HasAny/
// IsValidForMode/BaseOrderForMode/SeedFromLegacy/FromDocument) are NOT serialized. #63 grows
// HakoniwaProfile with cols/rows/box (the additive extension point — grill Q2).

using System.Collections.Generic;

// One mode's persisted Hakoniwa grid: the tile order (slot). #63 adds cols/rows/box here (per-mode
// divider fractions; the shared box geometry goes on HakoniwaLayoutProfiles, mirroring TTWR's
// HakoniwaSnapshot{box, replay/live: GridSnapshot}).
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

    // The per-mode stored panels (slot order), or null if that mode has no profile yet.
    public List<PanelLayout> Get(bool live) => (live ? this.live : replay)?.panels;

    // Stash an order (a Capture()) into a mode's profile. Called on every mode flip (stash the OLD
    // mode before switching) and on save (stash the active mode) — TTWR build_hakoniwa_snapshot /
    // reconcile_hakoniwa_tiles parity. Stores the SAME list reference; callers Clone() before persisting.
    public void Set(bool live, List<PanelLayout> panels)
    {
        var prof = new HakoniwaProfile(panels);
        if (live) this.live = prof; else this.replay = prof;
    }

    // True if either mode carries a non-empty profile. JsonUtility may materialize an absent nested
    // object as a default instance (null/empty panels), so "has per-mode data" is count-based, not
    // null-based — an old single-`panels` doc reads as HasAny()==false and seeds from the legacy panels.
    public bool HasAny() =>
        (replay?.panels != null && replay.panels.Count > 0) ||
        (live?.panels != null && live.panels.Count > 0);

    // The base (non-chart) ids of a mode's stored profile, in saved order. Chart ids are EXCLUDED
    // (dynamic, universe-owned — #60). The single shared filter behind both validity and ordering.
    List<string> BaseIds(bool live)
    {
        var ids = new List<string>();
        var stored = Get(live);
        if (stored != null)
            foreach (var p in stored)
                if (p != null && !string.IsNullOrEmpty(p.id) && !HakoniwaBaseTiles.IsChartId(p.id))
                    ids.Add(p.id);
        return ids;
    }

    // is_valid_for parity (TTWR HakoniwaGridSnapshot::is_valid_for): the stored profile's BASE id set
    // == HakoniwaBaseTiles.Kinds(live) as a SET (Replay has startup, Live drops it). null/missing →
    // invalid → caller falls to canonical. This is also the #61 collision-safe gate generalized: a
    // legacy/#60-era/Default() seed whose base set doesn't match the mode is invalid, so its scrambled
    // order is never honored.
    public bool IsValidForMode(bool live) =>
        Get(live) != null && new HashSet<string>(BaseIds(live)).SetEquals(HakoniwaBaseTiles.Kinds(live));

    // The Reorder prefix for a mode (the base ids, in front, in the right order): a VALID profile →
    // its base ids in SAVED order (honor the user's header-drag swaps per mode); else canonical
    // Kinds(live) (TTWR default_for_mode — also the #61 collision-safe fallback). Chart tiles are not
    // in the prefix; HakoniwaController.Reorder keeps them after the base, order-preserved.
    public List<string> BaseOrderForMode(bool live) =>
        IsValidForMode(live) ? BaseIds(live) : new List<string>(HakoniwaBaseTiles.Kinds(live));

    // forward-compat (AC2): an old single-`panels` doc (no hakoniwaProfiles) seeds BOTH profiles with a
    // deep clone of the legacy panels (HakoniwaProfile.Clone — one clone path, so #63's added per-mode
    // fields can't drift between seed and capture). The validity gate (IsValidForMode) then keeps each
    // mode correct — a legacy shape whose base set doesn't match a mode falls to canonical for THAT
    // mode on load.
    public void SeedFromLegacy(List<PanelLayout> panels)
    {
        replay = new HakoniwaProfile(panels).Clone();
        live = new HakoniwaProfile(panels).Clone();
    }

    public HakoniwaLayoutProfiles Clone() =>
        new HakoniwaLayoutProfiles { replay = replay?.Clone(), live = live?.Clone() };

    // disk doc → profiles: use the doc's per-mode field if it carries data; else seed both from the
    // legacy single `panels` (forward-compat). Always returns a usable (deep-cloned) instance.
    public static HakoniwaLayoutProfiles FromDocument(LayoutDocument doc)
    {
        if (doc?.hakoniwaProfiles != null && doc.hakoniwaProfiles.HasAny())
            return doc.hakoniwaProfiles.Clone();
        var p = new HakoniwaLayoutProfiles();
        p.SeedFromLegacy(doc?.panels);
        return p;
    }
}
