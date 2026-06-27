// AccountSummaryIconStage.cs — issue #177 / S5 "account summary bar icons" (ADR-0038 / findings 0126)
//
// ScreenSpaceOverlay can't draw a 3D object, so the bar's 4 icon frames are fed by RenderTextures:
// one tiny off-world stage per slot (a primitive group + an orthographic camera that frames only it),
// each camera rendering into its own RenderTexture that the bar's RawImage samples. The project has no
// finance-metric art yet, so the placeholders are 3D primitives — but their SHAPE and COLOUR are now
// chosen to MATCH each metric (owner 2026-06-27, #177 refinement / findings 0126 §アイコン placeholder):
//
//   ① 純資産     → 金貨   (flattened cylinder disc)           gold  #E8B84B
//   ② 買付け余力 → 札束   (thin cube slab + a paper band cube) blue  #4C8DFF
//   ③ 建玉数     → 箱積み (3 cubes stacked with an offset)     brown #A1714B
//   ④ 約定注文数 → チェック (2 thin cubes rotated into a ✓)     green #3FB950
//
// Build = PRIMITIVE COMPOSITION (scale + group), NOT vertex-authored procedural mesh: the existing
// CreatePrimitive path / bake-once / RenderTexture→RawImage swap seam are reused unchanged. Colours are
// STATIC semantic colours (theme-independent: gold is gold in Dark AND Light — they are NOT re-themed on
// a flip, so the bake-once stays valid). The SWAP SEAM is still the RawImage's texture
// (AccountSummaryBarView.SetIconTexture): a future real sprite replaces the RenderTexture with no other
// change. Real on-screen pixels (the lit groups) are owner HITL — headless -nographics renders nothing,
// but the mesh group + its baked material colour still exist CPU-side (the ASB-16 gate asserts them).
//
// The stages live far from the origin (beyond a default camera's far plane) on dedicated transforms so
// neither the main workspace camera sees the primitives nor the icon cameras see each other's.

using System.Collections.Generic;
using UnityEngine;

public sealed class AccountSummaryIconStage : MonoBehaviour
{
    public const int ICON_COUNT = 4;
    const int RT_SIZE = 64;
    const float STAGE_GAP = 50f;       // lateral spacing between stages (> ortho view, so no cross-bleed)
    const float STAGE_X = 10000f;      // far from origin (beyond a default camera's 1000 far plane)
    // Dedicated layer for the off-world icon rig so the directional light + icon cameras are SCOPED to the
    // primitives only: a directional light ignores position and would otherwise illuminate the WHOLE scene,
    // and an un-masked icon camera could capture unrelated geometry. Both masks + the prims sit on this layer.
    const int ICON_LAYER = 31;

    // Static SEMANTIC colours (theme-independent, baked once). One representative colour per stage; every
    // mesh in a stage shares it. Kept here as the single source of truth — ASB-16 pins these by value.
    public static readonly Color GOLD  = new Color(0.910f, 0.722f, 0.294f);   // #E8B84B 純資産=金貨
    public static readonly Color BLUE  = new Color(0.298f, 0.553f, 1.000f);   // #4C8DFF 買付け余力=札束
    public static readonly Color BROWN = new Color(0.631f, 0.443f, 0.294f);   // #A1714B 建玉=箱
    public static readonly Color GREEN = new Color(0.247f, 0.725f, 0.314f);   // #3FB950 約定=チェック
    static readonly Color[] SlotColors = { GOLD, BLUE, BROWN, GREEN };

    readonly RenderTexture[] _rts = new RenderTexture[ICON_COUNT];
    readonly int[] _meshCounts = new int[ICON_COUNT];
    readonly Material[] _stageMats = new Material[ICON_COUNT];
    readonly List<Material> _materials = new List<Material>();
    bool _built;

    public int Count => ICON_COUNT;
    public RenderTexture Texture(int i) => (i >= 0 && i < ICON_COUNT) ? _rts[i] : null;

    // ── probe observability (ASB-16) ──
    public int MeshCount(int i) => (i >= 0 && i < ICON_COUNT) ? _meshCounts[i] : 0;
    // Read the ACTUAL baked colour off the stage's material (not the authored array) so the gate verifies
    // MakeMaterial really applied it — breaking the tint application must RED, not pass on the constant.
    public Color IconColor(int i)
    {
        if (i < 0 || i >= ICON_COUNT || _stageMats[i] == null) return default;
        var m = _stageMats[i];
        return m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
    }

    public void Build()
    {
        if (_built) return;
        _built = true;

        // one shared directional light over the stage region so the primitives are shaded (real-pixel
        // appearance is HITL; in -nographics this is inert). cullingMask scopes it to ICON_LAYER so it does
        // NOT light the rest of the scene (a directional light is global regardless of its position).
        var lightGo = new GameObject("IconLight");
        lightGo.transform.SetParent(transform, false);
        lightGo.transform.position = new Vector3(STAGE_X, 5f, -5f);
        lightGo.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.1f;
        light.cullingMask = 1 << ICON_LAYER;

        var accent = ThemeService.Current.colors;
        for (int i = 0; i < ICON_COUNT; i++) BuildStage(i, accent);
    }

    void BuildStage(int index, ThemeColors colors)
    {
        var stage = new GameObject("stage" + index);
        stage.transform.SetParent(transform, false);
        var origin = new Vector3(STAGE_X + index * STAGE_GAP, 0f, 0f);
        stage.transform.position = origin;

        // one material per stage (URP/Lit, _BaseColor = the semantic colour), shared by every mesh in it.
        var mat = MakeMaterial(SlotColors[index]);
        _stageMats[index] = mat;
        _meshCounts[index] = BuildSemanticGroup(index, stage.transform, mat);

        // per-slot RenderTexture.
        var rt = new RenderTexture(RT_SIZE, RT_SIZE, 16, RenderTextureFormat.ARGB32);
        rt.name = "AccountIconRT" + index;
        rt.Create();
        _rts[index] = rt;

        // orthographic camera framing only this group, rendering into the RT. Transparent clear so the icon
        // frame's themed background shows through around the meshes.
        var camGo = new GameObject("cam");
        camGo.transform.SetParent(stage.transform, false);
        camGo.transform.localPosition = new Vector3(0f, 0f, -3f);
        camGo.transform.localRotation = Quaternion.identity;
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true;
        cam.orthographicSize = 0.8f;
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 6f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(colors.element_background.r, colors.element_background.g, colors.element_background.b, 0f);
        cam.cullingMask = 1 << ICON_LAYER;   // render ONLY this rig's meshes, never other scene geometry
        cam.targetTexture = rt;
        cam.enabled = false;        // static prop: render once below, not every frame

        cam.Render();               // one-shot bake (inert under -nographics; the RT object still exists)
    }

    // Build the metric-matching silhouette from scaled/grouped primitives. Returns the mesh count.
    int BuildSemanticGroup(int index, Transform stage, Material mat)
    {
        switch (index)
        {
            case 0:   // ① 純資産 → 金貨: a flattened cylinder disc, round face tilted toward the camera.
                Prim(stage, PrimitiveType.Cylinder, Vector3.zero, new Vector3(72f, 0f, 0f),
                     new Vector3(0.95f, 0.12f, 0.95f), mat);
                return 1;

            case 1:   // ② 買付け余力 → 札束: a thin wide banknote slab + a perpendicular paper band.
                Prim(stage, PrimitiveType.Cube, new Vector3(0f, 0f, 0f), new Vector3(12f, -22f, 0f),
                     new Vector3(1.10f, 0.62f, 0.16f), mat);                                   // slab
                Prim(stage, PrimitiveType.Cube, new Vector3(0f, 0f, -0.04f), new Vector3(12f, -22f, 0f),
                     new Vector3(1.18f, 0.18f, 0.20f), mat);                                   // band
                return 2;

            case 2:   // ③ 建玉数 → 箱の積み重ね: three offset cubes (an inventory stack), iso-tilted.
                Prim(stage, PrimitiveType.Cube, new Vector3(-0.05f, -0.30f, 0.00f), new Vector3(16f, 26f, 0f),
                     new Vector3(0.52f, 0.52f, 0.52f), mat);                                   // bottom
                Prim(stage, PrimitiveType.Cube, new Vector3(0.10f, 0.02f, -0.03f), new Vector3(16f, 26f, 0f),
                     new Vector3(0.46f, 0.46f, 0.46f), mat);                                   // middle
                Prim(stage, PrimitiveType.Cube, new Vector3(-0.06f, 0.33f, 0.02f), new Vector3(16f, 26f, 0f),
                     new Vector3(0.40f, 0.40f, 0.40f), mat);                                   // top
                return 3;

            default:  // ④ 約定注文数 → チェックマーク: two thin cubes rotated into a ✓ (short arm + long arm).
                Prim(stage, PrimitiveType.Cube, new Vector3(-0.22f, -0.10f, 0f), new Vector3(0f, 0f, 45f),
                     new Vector3(0.18f, 0.46f, 0.18f), mat);                                   // short arm (down-left)
                Prim(stage, PrimitiveType.Cube, new Vector3(0.14f, 0.10f, 0f), new Vector3(0f, 0f, -52f),
                     new Vector3(0.18f, 0.80f, 0.18f), mat);                                   // long arm (up-right)
                return 2;
        }
    }

    void Prim(Transform parent, PrimitiveType type, Vector3 localPos, Vector3 euler, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = "mesh";
        go.layer = ICON_LAYER;             // only the ICON_LAYER light/cameras see it (scene isolation)
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.transform.localScale = scale;
        // drop the auto-added collider (render-only prop).
        var col = go.GetComponent<Collider>();
        if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }
        var r = go.GetComponent<Renderer>();
        if (r != null) r.sharedMaterial = mat;   // sharedMaterial: don't leak a per-renderer instance
    }

    // URP/Lit material tinted by the semantic colour (_BaseColor is the URP property; _Color/.color are
    // belt-and-suspenders fallbacks for a non-URP shader). Tracked for OnDestroy cleanup.
    Material MakeMaterial(Color c)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        m.color = c;
        _materials.Add(m);
        return m;
    }

    void OnDestroy()
    {
        for (int i = 0; i < ICON_COUNT; i++)
        {
            if (_rts[i] != null)
            {
                _rts[i].Release();
                if (Application.isPlaying) Destroy(_rts[i]); else DestroyImmediate(_rts[i]);
            }
        }
        foreach (var m in _materials)
            if (m != null) { if (Application.isPlaying) Destroy(m); else DestroyImmediate(m); }
    }
}
