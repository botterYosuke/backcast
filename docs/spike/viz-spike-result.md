# Viz Spike 結果: numpy ndarray → Unity GPU の zero-copy interop seam

- Issue: #8 (viz-spike)
- 関連 ADR: [ADR-0001 — Unity + pythonnet embedded frontend](../adr/0001-unity-pythonnet-embedded-frontend.md)（decision 5 = viz zero-copy seam。本 spike がこの seam を**確認**した）
- 実行環境: Windows 11 (10.0.26200) / win_amd64 / Unity 6000.4.11f1 (standalone=Mono) / **Direct3D12**

## 実行環境（詳細）

| 項目 | 値 |
|---|---|
| OS | Windows 11 (10.0.26200) |
| platform | win_amd64 |
| Unity | 6000.4.11f1（standalone=Mono） |
| Graphics API | **Direct3D12**（Unity6 Auto Graphics API の実解決値） |
| pythonnet | 3.1.0（`Assets/Plugins/Python.Runtime.dll`） |
| CPython | **3.13.11**（MSC v1944） |
| numpy | **2.4.6** |
| nautilus_trader | 1.226.0（`PRECISION_BYTES=8`） |
| venv | `python/.venv`（uv 構成） |

---

## 1. 判定サマリ

**FINAL GREEN（Windows / Mono / D3D12）。**

owner playmode 目視ゲート（2026-06-13）で **「画面に緑の sine が連続して動いた」** ことを確認。同時に Console で以下の latch ログを取得（verbatim）:

```
[VIZ SPIKE PASS] python=3.13.11 numpy=2.4.6 points=4096
gen=486 uploaded=300 rendered=300 dropped=791 frames=332
maxDt=22.4ms hitches=0 uploadP50=2us uploadP95=9us uploadMax=46us
ptrAlias=OK setDataCalls=300 bytes=4915200 D3D12=OK
```

- `bytes=4915200 = 300 × 4096 × 4`（assert⑧ 成立）。
- `rendered=300`（実描画フレーム数）、`hitches=0`（warmup 後）、`OnDestroy` まで crash なし。
- = numpy ndarray から GPU までの interop seam が Windows / Mono / D3D12 上で機構的に成立。

---

## 2. backend 訂正（D3D11 → D3D12）

grill 時点（#8 設計）では Windows leg の backend を **D3D11** と想定していた。しかし **Unity6 実機の Auto Graphics API は Direct3D12 に解決**されたため、Windows leg の判定 backend を **D3D12 へ訂正**する。

- 今回の assert は実解決値である `Direct3D12` で固定した（恒久的な `D3D11 || D3D12` 緩和は**しない**）。
- **D3D11 は本 spike では未検証**であり、**D3D11 対応は主張しない**。D3D11 が必要になった場合は別途検証する。

---

## 3. numpy drift（2.4.4 → 2.4.6）

TTWR の参照 pin は numpy `2.4.4` だが、本 venv は pyproject の `numpy>=2.4.4` 制約により uv.lock が **2.4.6** に解決した。API 互換であり、本 spike の **blocker ではない**（参考情報として記録）。

---

## 4. zero-copy の正直な定義

本 spike が主張する「zero-copy」は、以下の意味に限定する（owner 確定文言）:

> アプリケーション層の中間 CPU コピーなし。numpy ptr を NativeArray が直接 alias し、各消費世代につき `GraphicsBuffer.SetData` を 1 回だけ呼ぶ。Unity/D3D12 ドライバ内部の staging 処理は射程外。

この定義を、以下の assert ①〜⑧ ＋ ptr alias 実測で立証した（`VizSpikeHarness.cs` の self-failing gate。mismatch 時に `Fail` / `LatchFail`）:

1. dtype == float32
2. C-contiguous
3. itemsize == 4
4. ptr が非 zero かつ 4-byte aligned
5. `NativeArray.GetUnsafePtr == numpy ptr`（ptr alias 実測）
6. `setDataCalls == uploadedGenerations`
7. `setDataCalls <= renderedFrames`
8. `uploadedBytes == setDataCalls × len × 4`

console latch の `ptrAlias=OK` / `setDataCalls=300` / `bytes=4915200` がこれらの成立を示す。

---

## 5. GREEN の帰結（射程の明示）

owner 確定の狭めた文言:

> GREEN は Windows/Mono/D3D12 上で numpy ndarray から GPU までの interop seam が**機構的に成立**したことを示す。数千系列での容量・throughput・実シェーダ性能は未検証で、要件到来時に別 issue で評価する。

---

## 6. S0 との切り分け

この Windows leg は **viz 専用**。S0 の Nautilus（threaded backtest）の Windows 再走は**別 seam であり、未消化のまま**（ADR-0001 / [s0-result.md](./s0-result.md) §6）。

→ **本 spike は S0 の Windows gate を discharge しない。** S0 Windows 再走は #4 着手前の必須ゲートとして引き続き残る。

---

## 7. handshake 設計要点

- **CAS 4-state**: `Free → Writing → Ready → Reading → Free` を CompareExchange で遷移。
- **世代番号 + latest-wins drop**: 消費が追いつかない世代は drop（latch の `dropped=791`）。main は常に最新世代のみを読む。
- **Python 参照解放は worker が GIL 下でのみ行う**。main スレッドは GIL を一切取らず、生 ptr のみを読む。
- **AtomicSafetyHandle**: NativeArray ラップ毎に wrap し、Unity safety system と整合させる。
- **描画**: URP の `endCameraRendering` で `DrawProceduralNow` を呼ぶ。即時の `RenderPrimitives` は URP のパイプラインに注入されなかったため不採用。

---

## 8. Play-owner 切替（spike 限定・復元済み）

本 spike 中は Play の owner を VizSpike harness に一時的に切り替えていた（`VizSpikeHarness.AutoBootstrapEnabled = true` / `ReplayPanelsHarness.AutoBootstrap` を一時無効化）。

→ **spike 完了に伴い原状回復済み**: `AutoBootstrapEnabled = false`、`ReplayPanelsHarness` の auto-bootstrap 復活。batchmode compile（exit 0 / CS0）で健全性を再確認済み。

---

## 9. 診断値（参考）

| 指標 | 値 |
|---|---|
| maxDt | 22.4 ms |
| upload P50 | 2 us |
| upload P95 | 9 us |
| upload Max | 46 us |
| hitches（warmup 後） | 0 |
| generated / uploaded / rendered / dropped | 486 / 300 / 300 / 791 |
| frames | 332 |

generated 後半の throughput は参考値（latch は warmup 後の measured 区間）。容量・throughput の本格評価は §5 のとおり別 issue。

---

## 付録: 主要成果物

- `python/spike/viz_source.py` — numpy ndarray を世代生成し ptr を公開する Python 層
- `Assets/Scripts/VizSpike/VizSpikeHarness.cs` — CAS handshake + zero-copy upload + assert ①〜⑧ self-failing gate
- `Assets/Scripts/VizSpike/SineLine.shader` — URP procedural draw 用シェーダ
