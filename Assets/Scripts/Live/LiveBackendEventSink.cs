// LiveBackendEventSink.cs — issue #20 "Live adapter tracer" (durable tier)
//
// The C# end of the LIVE backend-event sink. A single instance is constructed in
// C# and registered into the engine via engine.core.set_rust_event_sink(sink).
// The production path (_backend_impl.publish_backend_event) then calls
//   sink.push_json(json.dumps(_backend_event_to_wire_dict(event)).encode("utf-8"))
// FROM the Python live-loop thread while that thread holds Py.GIL(). The argument
// is Python bytes, which pythonnet marshals to a C# byte[]. push_json UTF-8-decodes
// it and enqueues the externally-tagged wire string ({"OrderEvent":{...}}) onto a
// GIL-free ConcurrentQueue; the Unity main thread drains it WITHOUT the GIL.
//
// This is NOT ReplayEventSink (push_bar = market-data bars) and NOT EventSink.push_*
// (kernel→ReplayPanel projection). This sink carries Live backend_events only (D1).
//
// pythonnet name exposure is VERBATIM: Python calls obj.push_json(...), so the C#
// method MUST be named push_json exactly (lowercase+underscore is a legal C# id).
//
// Threading: push_json runs on the Python live-loop thread UNDER the GIL and only
// does Encoding + ConcurrentQueue.Enqueue + Interlocked — no Unity main-thread API,
// provably non-throwing, so no try/catch (publish_backend_event swallows sink
// exceptions as warnings, which would silently drop events).
using System.Collections.Concurrent;
using System.Text;
using System.Threading;

public class LiveBackendEventSink
{
    readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();
    long _pushed;

    public void push_json(byte[] data)
    {
        string json = Encoding.UTF8.GetString(data);
        _events.Enqueue(json);
        Interlocked.Increment(ref _pushed);
    }

    public bool TryDequeue(out string json) => _events.TryDequeue(out json);

    public long Pushed => Interlocked.Read(ref _pushed);
}
