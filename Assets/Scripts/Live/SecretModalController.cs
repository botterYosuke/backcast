// SecretModalController.cs — issue #21 "Venue login and secret flow" (durable tier)
//
// Owns the plaintext lifetime of the tachibana second password (findings 0012 D5).
// The C# side — NOT Python's SecretVault TTL — owns the pre-submit plaintext, because
// the secret exists in this buffer BEFORE it is ever sent (the vault TTL only governs
// the already-submitted value's short reuse window).
//
// Contract:
//   * keyboard-drain into a char[] — NEVER a string/InputField/Text/IME/clipboard.
//     The display is the masked dot-count only (no plaintext leaves this object).
//   * 25s ABSOLUTE timeout from Open() (NOT idle-extending): closes + zeroizes before
//     the backend's 30s secret wait. Containment invariant: 25s < 30s < 40s.
//   * Submit() hands a one-shot char[] payload to the urgent-secret lane and zeroizes
//     its own buffer; the lane is contracted to Array.Clear() the payload after the
//     submit_secret call. The only irreducible plaintext is the transient pythonnet
//     string argument at the call site — never stored on a field/log/view-model.
//   * Cancel/Close/Timeout all zeroize. Zeroization is explicit Array.Clear (not GC).
//
// This object is pure logic (time is passed in as nowSeconds) so the AFK gate drives
// it deterministically. The real UGUI rendering + Input.inputString keyboard drain
// live in the owner-manual playmode harness; this controller is what they bind to.
using System;

public class SecretModalController
{
    public const double AbsoluteTimeoutSeconds = 25.0;
    const int MaxLen = 64;

    readonly char[] _buf = new char[MaxLen];
    int _len;
    double _openedAt;

    public bool IsOpen { get; private set; }
    public string RequestId { get; private set; }
    public string Venue { get; private set; }
    public string Purpose { get; private set; }

    // observability for the UI/gate — counts only, never plaintext.
    public int Length => _len;
    public bool TimedOut { get; private set; }
    public long SubmitCount { get; private set; }
    public long CancelCount { get; private set; }
    public long TimeoutCount { get; private set; }

    /// Masked display — dot per typed char. The ONLY rendering of the buffer.
    public string MaskedDisplay => new string('•', _len);

    /// Open for a SecretRequired. Resets buffer + arms the 25s absolute timeout.
    public void Open(LiveSecretRequiredEvent req, double nowSeconds)
    {
        ZeroBuffer();
        RequestId = req.RequestId;
        Venue = req.Venue;
        Purpose = req.Purpose;
        _openedAt = nowSeconds;
        TimedOut = false;
        IsOpen = true;
    }

    /// Keyboard drain — append one typed char. Ignores control chars; caps length.
    public void AppendChar(char c)
    {
        if (!IsOpen) return;
        if (c == '\0' || char.IsControl(c)) return;
        if (_len >= MaxLen) return;
        _buf[_len++] = c;
    }

    // NOTE: there is deliberately NO AppendInput(string) entry point. The secret must
    // never flow through a managed string (immutable → un-zeroable, lingers until GC).
    // Callers drain the keyboard ONE char at a time via AppendChar (OnGUI Event.character
    // / IMGUI KeyDown), so the only plaintext lives in the zeroable char[] buffer (D5).

    public void Backspace()
    {
        if (!IsOpen || _len == 0) return;
        _buf[--_len] = '\0';
    }

    /// 25s absolute timeout. Returns true the moment it fires (closes + zeroizes).
    public bool TickExpire(double nowSeconds)
    {
        if (!IsOpen) return false;
        if (nowSeconds - _openedAt < AbsoluteTimeoutSeconds) return false;
        TimedOut = true;
        TimeoutCount++;
        CloseAndZero();
        return true;
    }

    /// Hand a one-shot payload to the urgent-secret lane, then zeroize our buffer.
    /// Returns null if not open / empty. CALLER MUST Array.Clear(payload) after use.
    public char[] Submit()
    {
        if (!IsOpen || _len == 0) return null;
        var payload = new char[_len];
        Array.Copy(_buf, payload, _len);
        SubmitCount++;
        CloseAndZero();
        return payload;
    }

    public void Cancel()
    {
        if (!IsOpen) return;
        CancelCount++;
        CloseAndZero();
    }

    /// Test/audit hook: true iff the backing buffer holds no plaintext. Returns a
    /// bool only (never the contents) so it is safe to call from the AFK gate.
    public bool BufferIsZeroed()
    {
        for (int i = 0; i < _buf.Length; i++)
            if (_buf[i] != '\0') return false;
        return true;
    }

    void CloseAndZero()
    {
        ZeroBuffer();
        IsOpen = false;
        RequestId = null;
        Venue = null;
        Purpose = null;
    }

    void ZeroBuffer()
    {
        Array.Clear(_buf, 0, _buf.Length);
        _len = 0;
    }
}
