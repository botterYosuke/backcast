// CameraGlideDriver.cs — issue #167 "ウィンドウフレーミング S2" (camera tween driver, findings 0123 §3)
//
// MonoBehaviour driver that グライドs the InfiniteCanvasController's CanvasView (pan + zoom) from the
// CURRENT view to a TARGET view over ~200ms with **ease-out (no overshoot)** — the production tween that
// replaces #166 (S1) の即時 apply。Mirrors RectSpringDriver の規律: the controller writes the
// authoritative view; this driver only paints the per-frame intermediate `ApplyView` calls.
//
// The InfiniteCanvasController is plain C# (NOT a MonoBehaviour) and has no Update, so the tween needs a
// MonoBehaviour host. One scene-level driver is enough — there is exactly one CanvasView per workspace.
//
// EASE-OUT (findings 0123 §3): cubic out, `eased = 1 - (1 - t)^3`, monotonic in [0,1]. The spring driver's
// 8% overshoot is REMOVED for the camera: an overshoot on zoom would push past MAX/MIN and produce a
// clipping flash; an overshoot on pan would push the framed window past viewport centre and back. Linear
// lerp of (pan, zoom) under the monotonic ease keeps the intermediate view inside the [from, to] envelope
// (so zoom stays inside [MIN_ZOOM, MAX_ZOOM] as long as both endpoints are clamped, which FrameWindow
// guarantees).
//
// INTERRUPT (findings 0088 §14 / 0123 §3.1): "fire-point は確定点のみ・途中状態を persist しない".
// Per frame we capture the controller's CURRENT view and compare against what we wrote LAST frame; if it
// differs by more than EPS (someone else — InfiniteCanvasInputSurface's manual pan/zoom, the layout
// restore, etc. — wrote to Content in between) we KILL the tween in place and leave the intermediate
// state as the new resting view. This decouples the driver from the input surface — no extra wiring is
// needed to make manual pan/zoom abort a glide.
//
// HEADLESS TEST: `Advance(dtMs)` is the pure-style tick, separate from Unity's Update(). The AFK probe
// drives `Advance` directly so the test does not rely on a Unity playmode loop.

using UnityEngine;

public sealed class CameraGlideDriver : MonoBehaviour
{
    public const float DURATION_MS = 200f;       // findings 0123 §3: duration constant
    const float EPS_INTERRUPT = 1e-3f;           // findings 0123 §3 EPS_INTERRUPT
    // Per-tick dtMs cap (review finding): unscaledDeltaTime can be hundreds of ms on the first frame after
    // a domain reload / scene load — feeding that raw to Advance collapses the 200ms ease-out to instant.
    // Cap each tick at 25% of the duration so the worst-case first frame still takes ≥4 ticks to converge.
    const float MAX_DT_MS = DURATION_MS * 0.25f;

    InfiniteCanvasController _controller;
    CanvasView _from;
    CanvasView _to;
    CanvasView _lastApplied;
    float _elapsedMs;
    bool _animating;

    public bool IsAnimating => _animating;

    public void Bind(InfiniteCanvasController controller)
    {
        _controller = controller;
    }

    // Start a glide from the controller's CURRENT view to `target`. In-flight glide is REPLACED
    // (the killed glide leaves whatever it last wrote on Content; the new glide starts from THAT).
    public void BeginGlide(CanvasView target)
    {
        if (_controller == null || target == null) return;
        _from = _controller.CaptureView();
        _to = target.Clone();
        _elapsedMs = 0f;
        _lastApplied = _from.Clone();
        _animating = true;
    }

    // Cancel any in-flight glide. The current Content view is unchanged (the last `ApplyView` stands).
    public void Stop()
    {
        _animating = false;
    }

    void Update()
    {
        // unscaledDeltaTime so the glide is robust to Time.timeScale (a paused engine still glides).
        Advance(Time.unscaledDeltaTime * 1000f);
    }

    // Headless / AFK entry point. Returns true if the glide is still animating after this tick.
    public bool Advance(float dtMs)
    {
        if (!_animating || _controller == null) return false;

        // Interrupt detection: external write between our last ApplyView and now → kill the tween,
        // leave Content as-is. We trust the externally-written view as the new resting state.
        CanvasView live = _controller.CaptureView();
        if (!CanvasView.Approx(live, _lastApplied, EPS_INTERRUPT))
        {
            _animating = false;
            return false;
        }

        // Clamp per-tick dt (review finding): see MAX_DT_MS rationale above.
        _elapsedMs += Mathf.Clamp(dtMs, 0f, MAX_DT_MS);
        float t = Mathf.Clamp01(_elapsedMs / DURATION_MS);
        float eased = EaseOutCubic(t);

        if (t >= 1f)
        {
            // Final tick: write `_to` exactly (no redundant intermediate write). Avoids a 2-event
            // sequence where ApplyView(next) then ApplyView(_to) could write the parallax layer twice
            // with float-ULP-different pans on the same frame (review finding).
            _controller.ApplyView(_to);
            _lastApplied = _to.Clone();
            _animating = false;
        }
        else
        {
            CanvasView next = Lerp(_from, _to, eased);
            _controller.ApplyView(next);
            _lastApplied = next.Clone();
        }
        return _animating;
    }

    // findings 0123 §3: monotonic ease-out, no overshoot.
    static float EaseOutCubic(float t)
    {
        float u = 1f - Mathf.Clamp01(t);
        return 1f - u * u * u;
    }

    static CanvasView Lerp(CanvasView a, CanvasView b, float t)
    {
        return new CanvasView(
            Mathf.LerpUnclamped(a.panX, b.panX, t),
            Mathf.LerpUnclamped(a.panY, b.panY, t),
            Mathf.LerpUnclamped(a.zoom, b.zoom, t));
    }
}
