// RectSpringDriver.cs — ADR-0024 §3 / findings 0088 §3 (puzzle-feel drag, "プルン" spring)
//
// The production-only visual driver for the spring rect interpolation. FloatingWindowController writes
// the AUTHORITATIVE final geometry directly (so the headless AFK gate sees the settled rect without any
// driver), then calls the injected spring side-effect (SetSpringAnimator) to animate the visual
// transition from the pre-commit pose into the final one. This MonoBehaviour IS that side-effect: one
// driver per scene, bound to both plane controllers via SetSpringAnimator(driver.Animate).
//
// The curve is FloatingWindowMath.SpringEase (ease-out-back, single 8% overshoot, settling at t=1) over
// SPRING_DURATION_MS. Re-animating a RectTransform that is mid-tween is kill-and-replace (findings 0088
// §3): the existing tween for that rt is overwritten, so a continuous magnetic-snap stickiness does not
// stack overshoots. Pure-visual: the dictionary value's `to` rect is the controller's already-written
// final, so even if the driver is destroyed mid-tween the geometry is already correct.

using System.Collections.Generic;
using UnityEngine;

public class RectSpringDriver : MonoBehaviour
{
    struct Tween
    {
        public FloatingWindowMath.DockRect from;
        public FloatingWindowMath.DockRect to;
        public float elapsedMs;
    }

    readonly Dictionary<RectTransform, Tween> _tweens = new Dictionary<RectTransform, Tween>();
    // Reused scratch lists so Update allocates NOTHING per frame: `_keys` snapshots the dictionary keys
    // (so we can write back while iterating), `_done` collects finished/dead tweens to remove afterwards.
    readonly List<RectTransform> _keys = new List<RectTransform>();
    readonly List<RectTransform> _done = new List<RectTransform>();

    // Bind to a controller: `controller.SetSpringAnimator(driver.Animate, driver.Stop)`. Kill-and-replace
    // any prior tween on the same rt (the latest target wins). A null rt is ignored.
    public void Animate(RectTransform rt, FloatingWindowMath.DockRect from, FloatingWindowMath.DockRect to)
    {
        if (rt == null) return;
        _tweens[rt] = new Tween { from = from, to = to, elapsedMs = 0f };
    }

    // ADR-0024 §3 (review fix): stop+SETTLE an in-flight tween — snap the rt to the tween's target and
    // drop it. The controller calls this in BeginDrag so a re-grab within the 200ms commit/ESC animation
    // reads a settled pose (not a transient overshoot) and the dying tween cannot fight the new drag.
    public void Stop(RectTransform rt)
    {
        if (rt == null) return;
        if (_tweens.TryGetValue(rt, out var tw))
        {
            rt.anchoredPosition = tw.to.topLeft;
            rt.sizeDelta = tw.to.size;
            _tweens.Remove(rt);
        }
    }

    void Update()
    {
        if (_tweens.Count == 0) return;
        float dtMs = Time.deltaTime * 1000f;
        _keys.Clear();
        foreach (var rt in _tweens.Keys) _keys.Add(rt);   // snapshot (reused buffer) to write back safely
        _done.Clear();
        foreach (var rt in _keys)
        {
            if (rt == null) { _done.Add(rt); continue; }
            var tw = _tweens[rt];
            tw.elapsedMs += dtMs;
            float t = Mathf.Clamp01(tw.elapsedMs / FloatingWindowMath.SPRING_DURATION_MS);
            var r = FloatingWindowMath.SpringRectAt(tw.from, tw.to, t);
            rt.anchoredPosition = r.topLeft;
            rt.sizeDelta = r.size;
            if (t >= 1f)
            {
                // Settle exactly on the target (SpringEase(1) already returns it; this is belt-and-braces).
                rt.anchoredPosition = tw.to.topLeft;
                rt.sizeDelta = tw.to.size;
                _done.Add(rt);
            }
            else
            {
                _tweens[rt] = tw;
            }
        }
        foreach (var rt in _done) _tweens.Remove(rt);
    }
}
