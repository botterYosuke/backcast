// VenueLoginModalController.cs — #181 / ADR-0040 venue ログイン uGUI モーダルの頭脳（plain C#・Python 非依存）
//
// venue ログインの GUI が Unity 側へ移った（findings 0130: 非メイン tkinter Tk() が macOS Cocoa を abort）。
// このコントローラがフォーム状態（モード・入力・本体起動確認・busy・結果）を保持し、view（VenueLoginModalOverlay）
// と AFK probe の両方がこれを駆動する。検証も RPC も持たない——root（BackcastWorkspaceRoot.DriveVenueLoginModal）が
// view ↔ controller を毎フレーム同期し、submit を WorkspaceEngineHost.SubmitVenueLogin（headless 認証）へ渡す。
//
// SECRET 規律（kabu API パスワード・SecretModalController を踏襲）: 平文は managed string にしない。キーは
// onTextInput で 1 文字ずつ char[] バッファへため、masked dot だけ表示する。submit 時に使い捨て char[] コピーを
// 渡し（pythonnet 境界の transient string が唯一の不可避平文）、即 zeroize する。Tachibana の認証ID・秘密鍵パスは
// 秘密ではない（ID とファイルパス）ので通常文字列で扱う。

using System;

// C#→Python の submit_venue_login が返す結果（モーダルが描画する）。
public struct VenueLoginSubmitResult
{
    public bool Success;
    public string ErrorCode;
    public string StatusText;   // 日本語・失敗時にモーダル内へ赤字表示
    public bool AllowRetry;     // false = 本体/アプリ側を直してから（OK は据え置き・再確認を促す）
}

// venue_login_form_init が返す prefill（モーダル open / モード切替で再導出）。
public struct VenueLoginFormInit
{
    public string Venue;            // "TACHIBANA" / "KABU"
    public string InitialMode;      // demo/prod (tachibana) | verify/prod (kabu)
    public string AuthIdPrefill;    // tachibana（ADR-0033 debug demo のみ・else ""）
    public string KeyPathPrefill;   // tachibana
    public int StationPort;         // kabu（18080 prod / 18081 verify）
    public string ApiPasswordPrefill; // kabu（ADR-0033 debug verify のみ・else ""）
}

public sealed class VenueLoginModalController
{
    const int MaxSecretLen = 64;

    readonly char[] _secret = new char[MaxSecretLen];  // zeroable・managed string にしない
    int _secretLen;
    bool _retryBlocked;   // allow_retry=false の失敗後、再確認/入力変更まで OK を据え置く

    public bool IsOpen { get; private set; }
    public string Venue { get; private set; } = "";   // "TACHIBANA" / "KABU"
    public string Mode { get; private set; } = "";    // demo/prod/verify
    public string AuthId { get; private set; } = "";
    public string KeyPath { get; private set; } = "";
    public int StationPort { get; private set; }
    public bool StationRunning { get; private set; }
    public bool Busy { get; private set; }
    public string StatusText { get; private set; } = "";

    public bool IsKabu => Venue == "KABU";
    public int SecretLength => _secretLen;
    public string MaskedPassword => new string('•', _secretLen);  // dot-count only

    // モーダルを開く（venue_login_form_init の結果を seed）。
    public void Open(string venue, VenueLoginFormInit init)
    {
        Venue = (venue ?? "").ToUpperInvariant();
        Mode = init.InitialMode ?? "";
        AuthId = init.AuthIdPrefill ?? "";
        KeyPath = init.KeyPathPrefill ?? "";
        StationPort = init.StationPort;
        StationRunning = !IsKabu;   // 非 kabu は本体概念なし＝常時 ready（probe で kabu のみ更新）
        Busy = false;
        StatusText = "";
        _retryBlocked = false;
        ZeroizeSecret();
        SeedSecretPrefill(init.ApiPasswordPrefill);
        IsOpen = true;
    }

    // モード切替（demo↔prod / verify↔prod）で prefill / ポートを再導出。
    public void ApplyModeRefresh(string mode, VenueLoginFormInit refreshed)
    {
        Mode = mode ?? "";
        StatusText = "";
        _retryBlocked = false;
        if (IsKabu)
        {
            StationPort = refreshed.StationPort;
            // Fail-safe: the new port's station is unconfirmed until the re-probe returns — keep OK
            // disabled meanwhile so the user can't submit against a port whose station is down.
            StationRunning = false;
            ZeroizeSecret();
            SeedSecretPrefill(refreshed.ApiPasswordPrefill);
        }
        else
        {
            AuthId = refreshed.AuthIdPrefill ?? "";
            KeyPath = refreshed.KeyPathPrefill ?? "";
        }
    }

    void SeedSecretPrefill(string prefill)
    {
        if (IsKabu && !string.IsNullOrEmpty(prefill))
            foreach (char c in prefill) AppendSecretChar(c);
    }

    // kabu パスワードのキー入力（onTextInput・1 文字ずつ）。
    public void AppendSecretChar(char c)
    {
        if (_secretLen < MaxSecretLen) _secret[_secretLen++] = c;
        _retryBlocked = false;
    }

    public void BackspaceSecret()
    {
        if (_secretLen > 0) { _secretLen--; _secret[_secretLen] = '\0'; }
        _retryBlocked = false;
    }

    public void SetAuthId(string s) { AuthId = s ?? ""; }
    public void SetKeyPath(string s) { KeyPath = s ?? ""; }

    // kabu 本体起動確認（probe_station 連動）。再確認は retry-block も解く。
    public void SetStationProbe(bool running, int port)
    {
        // Drop a stale probe whose port no longer matches the current mode's station: a
        // mode switch re-derives StationPort (ApplyModeRefresh) and fires a fresh probe, but
        // a late probe for the OLD port must not overwrite the new mode's running state.
        // port==0 = a non-kabu / port-less probe → always apply (uniform OK-enable).
        if (port > 0 && StationPort > 0 && port != StationPort) return;
        StationRunning = running;
        if (port > 0) StationPort = port;
        _retryBlocked = false;
    }

    public void SetBusy(bool busy) { Busy = busy; }

    // OK 有効判定: busy/retry-block 中は不可。kabu=本体起動＋PW 非空、tachibana=認証ID＋鍵パス非空。
    public bool CanSubmit()
    {
        if (Busy || _retryBlocked) return false;
        if (IsKabu) return StationRunning && _secretLen > 0;
        return !string.IsNullOrWhiteSpace(AuthId) && !string.IsNullOrWhiteSpace(KeyPath);
    }

    // 非秘密フィールドの JSON（submit_venue_login の fields_json）。kabu は空・tachibana は auth_id/key_path。
    public string BuildFieldsJson()
    {
        if (IsKabu) return "{}";
        return "{\"auth_id\":\"" + JsonEscape(AuthId) + "\",\"key_path\":\"" + JsonEscape(KeyPath) + "\"}";
    }

    // pythonnet 境界へ渡す使い捨て char[] コピー（呼び元が zeroize する）。tachibana は空。
    public char[] TakeSecretTransient()
    {
        var copy = new char[_secretLen];
        Array.Copy(_secret, copy, _secretLen);
        return copy;
    }

    // submit 結果を反映。成功で閉じる・失敗は閉じずに status を赤字表示し再試行（allow_retry=false は OK 据え置き）。
    public void ApplyResult(VenueLoginSubmitResult r)
    {
        Busy = false;
        if (r.Success) { Close(); return; }
        StatusText = r.StatusText ?? "";
        _retryBlocked = !r.AllowRetry;
    }

    public void SetStatus(string s) { StatusText = s ?? ""; }

    public void Close()
    {
        ZeroizeSecret();
        IsOpen = false;
        Busy = false;
        _retryBlocked = false;
    }

    public void Cancel() => Close();

    void ZeroizeSecret()
    {
        Array.Clear(_secret, 0, _secret.Length);
        _secretLen = 0;
    }

    // secret バッファが完全に zeroize されているか（gate 用）。
    public bool SecretIsZeroed()
    {
        if (_secretLen != 0) return false;
        for (int i = 0; i < _secret.Length; i++) if (_secret[i] != '\0') return false;
        return true;
    }

    static string JsonEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
