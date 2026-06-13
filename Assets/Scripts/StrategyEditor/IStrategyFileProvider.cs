// IStrategyFileProvider.cs — issue #16 "Strategy Editor" (DURABLE tier, seam)
//
// The EXPLICIT provider contract a future Replay/Live run layer calls to obtain the
// strategy `.py` PATH (findings 0010 §5). The engine consumes a PATH, not source
// text — _backend_impl._load_strategy opens the file from disk — so the seam hands
// over a SAVED path, never a buffer.
//
// "Supplyable" is intentionally strict: TryGetStrategyFile returns true (with a
// canonical absolute .py path) ONLY when ALL hold (findings 0010 §5, owner-locked):
//   1. a path is bound,
//   2. the document is NOT dirty,
//   3. the last Open or Save succeeded,
//   4. the path is a canonical absolute .py,
//   5. the file still exists as a normal file at call time.
// When dirty it returns FALSE rather than a stale path — so a returned path ALWAYS
// reflects what the buffer shows on disk. (External rewrites after save are out of
// scope; a future hash/mtime contract can add that.) This seam owns NO run lifecycle
// and NO active-strategy selection (run-UI's job).

public interface IStrategyFileProvider
{
    // True + canonical absolute .py path when supplyable (all 5 conditions); false otherwise.
    bool TryGetStrategyFile(out string path);
}
