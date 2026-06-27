// AccountSummaryIconStage.cs — issue #177 / S5 "account summary bar icons" (ADR-0038 / findings 0126)
//
// ScreenSpaceOverlay can't draw a 3D object, so the bar's 4 icon frames are fed by RenderTextures:
// one tiny off-world stage per slot (a primitive + an orthographic camera that frames only it), each
// camera rendering into its own RenderTexture that the bar's RawImage samples. The project has no
// finance-metric art yet (§Decision 3), so the placeholders are 3D primitives — ① cube / ② sphere /
// ③ capsule / ④ cylinder — visually distinguishing the four metrics. The SWAP SEAM is the RawImage's
// texture (AccountSummaryBarView.SetIconTexture): a future real sprite replaces the RenderTexture with
// no other change. Real on-screen pixels (the lit primitives) are owner HITL — headless -nographics
// renders nothing, but the RenderTexture object still exists and is assigned (the seam AFK asserts).
//
// The stages live far from the origin (beyond a default camera's far plane) on dedicated transforms so
// neither the main workspace camera sees the primitives nor the icon cameras see each other's.

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

    static readonly PrimitiveType[] Prims =
        { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Capsule, PrimitiveType.Cylinder };

    readonly RenderTexture[] _rts = new RenderTexture[ICON_COUNT];
    bool _built;

    public int Count => ICON_COUNT;
    public RenderTexture Texture(int i) => (i >= 0 && i < ICON_COUNT) ? _rts[i] : null;

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

        // primitive (placeholder icon). Drop the auto-added collider (not needed for a render-only prop).
        var prim = GameObject.CreatePrimitive(Prims[index]);
        prim.name = "prim";
        prim.layer = ICON_LAYER;       // only the ICON_LAYER light/cameras see it (scene isolation)
        prim.transform.SetParent(stage.transform, false);
        prim.transform.localPosition = Vector3.zero;
        prim.transform.localRotation = Quaternion.Euler(20f, 30f, 0f);
        prim.transform.localScale = Vector3.one * 0.9f;
        var col = prim.GetComponent<Collider>();
        if (col != null) { if (Application.isPlaying) Destroy(col); else DestroyImmediate(col); }

        // per-slot RenderTexture.
        var rt = new RenderTexture(RT_SIZE, RT_SIZE, 16, RenderTextureFormat.ARGB32);
        rt.name = "AccountIconRT" + index;
        rt.Create();
        _rts[index] = rt;

        // orthographic camera framing only this primitive, rendering into the RT. Transparent clear so
        // the icon frame's themed background shows through around the primitive.
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
        cam.cullingMask = 1 << ICON_LAYER;   // render ONLY this rig's primitives, never other scene geometry
        cam.targetTexture = rt;
        cam.enabled = false;        // static prop: render once below, not every frame

        cam.Render();               // one-shot bake (inert under -nographics; the RT object still exists)
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
    }
}
