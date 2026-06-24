// PickerListWindow.cs — virtualized [+ Add] picker: which row window to instantiate.
//
// The Replay universe (listed_info.duckdb) is ~4400 instruments. Mounting one GameObject per
// candidate — and rebuilding them on every search keystroke — would freeze the UI, so
// UniverseSidebarView renders the picker list as a VIRTUAL scroll: the content keeps its FULL
// logical height (count*ROW_H, so the scrollbar spans the whole universe) while only the rows
// inside the visible viewport (plus a small off-screen buffer on each side) are actually mounted.
//
// This is the pure visible-window arithmetic, factored out of the view so the AFK runner can gate
// it deterministically: headless (-batchmode -nographics) the view's RectTransform has rect.height==0
// (Relayout early-returns), so the window can't be observed through real layout — but the math can.
// See findings 0101 and UniverseSidebarE2ERunner Section14.

using UnityEngine;

public static class PickerListWindow
{
    // Rows kept mounted off-screen on EACH side of the visible viewport (smooths fast scrolling).
    // Single source of truth for the buffer — UniverseSidebarView and the AFK runner both read this
    // (no per-caller mirror that could silently drift). The window math is buffer-agnostic, so any
    // value stays correct.
    public const int DefaultBuffer = 6;

    // total       : candidate count.
    // scrollTopPx : pixels the content is scrolled down from the top (>= 0).
    // viewportH   : visible viewport height in pixels (0 headless / when not yet laid out).
    // rowH        : per-row height in pixels.
    // buffer      : extra rows to keep mounted off-screen on EACH side (smooths fast scrolling).
    // out first/count : mount rows [first, first+count). Always 0 <= first and first+count <= total.
    //                   total <= 0 or rowH <= 0 yields an empty window.
    public static void Compute(int total, float scrollTopPx, float viewportH, float rowH, int buffer,
                               out int first, out int count)
    {
        if (total <= 0 || rowH <= 0f) { first = 0; count = 0; return; }
        if (buffer < 0) buffer = 0;

        int visibleRows = viewportH > 0f ? Mathf.CeilToInt(viewportH / rowH) : 0;
        int firstVisible = Mathf.FloorToInt(Mathf.Max(0f, scrollTopPx) / rowH);

        first = Mathf.Clamp(firstVisible - buffer, 0, total - 1);
        int last = Mathf.Min(total - 1, firstVisible + visibleRows + buffer);
        count = last - first + 1;
        if (count < 0) count = 0;
    }
}
