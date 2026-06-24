// SidebarPaneSplit.cs — how the sidebar's free vertical space is split between the universe ROWS
// list and the open [+ Add] picker list.
//
// findings 0101: the owner wants the expanded picker list to extend all the way DOWN to the footer
// (focus label), not stop at half the sidebar with scroll kicking in. The old Relayout capped the
// PICKER list at half the room and let the rows pane take the rest — so with a near-empty universe
// ("No instruments") the picker still only got half. This flips it: the ROWS pane is capped at half
// (so a large curated universe can't starve the picker), and the PICKER list takes ALL the leftover.
//
// Pure math, factored out of UniverseSidebarView.Relayout so the AFK runner can gate it headless
// (Relayout early-returns at rect.height==0; the heights are not observable through real layout, but
// the split arithmetic is). See UniverseSidebarE2ERunner Section15.

using UnityEngine;

public static class SidebarPaneSplit
{
    // available     : free height = container − pinned chrome (title + add + focus + gaps).
    // pickerHeaderH : picker label+input block (+ the gap before the list) reserved when the picker is open.
    // naturalRowsH  : rows content height (count*rowH; >= 1 row for the "No instruments" placeholder).
    // naturalListH  : picker content height (the full universe ≈ 4400*rowH → list takes the whole remainder).
    // pickerOpen    : whether the picker is showing.
    // out rowsH / pickerListH : viewport heights to assign to the rows scroll and the picker list scroll.
    public static void Compute(float available, float pickerHeaderH, float naturalRowsH, float naturalListH,
                               bool pickerOpen, out float rowsH, out float pickerListH)
    {
        available = Mathf.Max(0f, available);
        if (!pickerOpen)
        {
            // Picker closed: rows take their natural height bounded by the free space (the empty band
            // below the last instrument stays raycast-free so canvas pan falls through — see Relayout).
            rowsH = Mathf.Clamp(naturalRowsH, 0f, available);
            pickerListH = 0f;
            return;
        }

        float roomForLists = Mathf.Max(0f, available - Mathf.Max(0f, pickerHeaderH));
        // Rows capped at half the shared room: a big curated universe can't push the picker off-screen,
        // but a small/empty rows pane uses only its natural height — freeing the rest for the picker.
        rowsH = Mathf.Clamp(naturalRowsH, 0f, roomForLists * 0.5f);
        // Picker list takes ALL the leftover (down to the footer), bounded only by its natural height.
        pickerListH = Mathf.Min(Mathf.Max(0f, naturalListH), roomForLists - rowsH);
    }
}
