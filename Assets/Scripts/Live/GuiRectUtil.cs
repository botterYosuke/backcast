// GuiRectUtil.cs — issue #59 "Backcast workspace root" (V-host helper)
//
// Converts a ScreenSpaceOverlay RectTransform into an IMGUI screen Rect so the V-host View
// components (MenuBarView / UniverseSidebarView) derive their OnGUI draw region from the
// scene-authored container instead of hardcoding screen math (findings 0025 §6, owner-locked:
// "描画領域はハードコードせず、各コンテナの RectTransform から算出"). For an overlay canvas,
// GetWorldCorners returns screen pixels with the Unity convention (y up, origin bottom-left);
// IMGUI uses y down, origin top-left — this flips Y.

using UnityEngine;

public static class GuiRectUtil
{
    static readonly Vector3[] s_corners = new Vector3[4];

    // Screen-space Rect (IMGUI top-left origin) covering `rt`. Returns Rect.zero for a null rt.
    public static Rect GuiScreenRect(RectTransform rt)
    {
        if (rt == null) return Rect.zero;
        rt.GetWorldCorners(s_corners);   // 0=BL, 1=TL, 2=TR, 3=BR (overlay: screen px, y up)
        float xMin = s_corners[0].x;
        float xMax = s_corners[2].x;
        float yBottom = s_corners[0].y;
        float yTop = s_corners[1].y;
        return new Rect(xMin, Screen.height - yTop, Mathf.Max(0f, xMax - xMin), Mathf.Max(0f, yTop - yBottom));
    }
}
