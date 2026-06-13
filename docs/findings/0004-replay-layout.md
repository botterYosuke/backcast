# Replay layout Findings: レイアウト永続化インフラ（capability parity・Unity 自前 versioned スキーマ）

- Issue: #12 (Replay layout — レイアウト永続化インフラ)
- 親: #3 (Step 1: Unity host + 埋め込み Python + Replay parity)
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted, self-protection 節あり）, [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（proposed）, [ADR-0002](../adr/0002-embedded-python-runtime-placement-and-resolution.md)（accepted）
- 配置の根拠: ADR-0003 self-protection 節（capability surface の具体項目など下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0003」として参照）。
- 先行: #9（Replay tracer / findings 0001）, #10（Replay chart / findings 0002）, #11（Replay panels / findings 0003）
- 実行環境（先行 slice と同一）: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）

> **状態: AFK ゲート GREEN（Mac leg, 2026-06-13）。** 設計は `grill-with-docs` で確定、直接実装（単一言語 C#・
> Python 非依存・6 ファイル）。`ReplayLayoutProbe.Run` が batchmode で `exit=0`、CS エラー 0。実装結果は §11。

---

## 1. 非空転ゲートの定義（vacuous round-trip 撲滅）— owner 確定 2026-06-13

レイアウト永続化の固有 false-green: `save(default) → load → assert(loaded==default)` は、
何も書かない/常に default を返す壊れた serializer でも **default==default で緑**になる（S0 16-byte pin /
#10 POINT-A zero-fill / #11 equity=0 と同型）。これを構造的に kill する。

round-trip ゲートの必須条件:
- **non-default な `LayoutDocument` を API で構築・変更**（panel 順序・visibility・chart split/rect を複数変更）。
- save 後に **新規インスタンス**で load。
- `loaded == mutated`（変更が生き残った）。
- `loaded != default`（default-fallback の偽緑を排除）。
- **保存 JSON 文字列に変更値が実在する**ことも構造 assert（in-memory round-trip だが実際にはディスクに
  書けていない serializer を捕まえる — 同じ buggy serializer を 2 度通すだけの round-trip では検出不能）。
- 欠落・空 JSON・全 default 化では **FAIL**。

**ドラッグ UI は #12 の射程外**。#12 は永続化 seam と schema を証明し、操作 UI は後続 shell slice（#7 floating
windows 等）が同じ API を利用する。capability surface は実データ（panel rect/visibility/slot 順 + chart split）
だが、ゲートでの mutation は **API 経由（synthetic-via-API）** で、drag UI を介さない。

## 2. durable 境界と API（owner 確定 2026-06-13）

durable コードは新規フォルダ **`Assets/Scripts/Layout/`** に置く（既存 `ReplayChart/` に相乗りしない。
レイアウトは ReplayChart 固有でなく going-forward な cross-cutting infra で shell slice が再利用するため）。

- **`LayoutDocument`** — `[Serializable]` POCO。将来拡張に備え **class**（struct でなく）。`int version` +
  panels（slot 順・visibility・rect）+ chart split。MonoBehaviour/UnityEngine（RectTransform・ピクセル）に
  **直接結合しない**（probe が純 C# で round-trip できる plain POCO、`ReplayRunLifecycle` と同方針）。
  rect は **正規化 float**（resolution-independent）で持ち、RectTransform/画面ピクセルへ直接束ねない。
- **`LayoutStore`** — `Save(LayoutDocument, path)` / `LayoutDocument Load(path)`。**JsonUtility を内部に隠蔽**
  （decoder と同じ swap-point 規律）。ファイル欠落時は default document を返す（first-run / forward-evolvable）。
  明示パス引数（テストが temp パスを渡し決定的にする）。
- **`LayoutPathResolver`** — 本番デフォルトパス（`Application.persistentDataPath` 由来）の解決を隔離する
  薄いラッパ。`LayoutStore` は path を知らない（テスト可能性のため分離）。

throwaway 側（AFK probe・任意の HITL）はこの API を消費するだけ（#10/#11 の durable+throwaway 3 層を踏襲）。

## 3. スキーマの具体形（owner 確定 2026-06-13）

```
class LayoutDocument { int version;            // CURRENT_VERSION = 1
                       List<PanelLayout> panels; }   // chart も id="chart" の 1 entry
class PanelLayout   { string id;    // 安定キー: status/positions/orders/run_result/chart
                      int    slot;   // 論理並び順 *専用*（zOrder とは同一視しない／必要時に別 field 追加）
                      bool   visible;
                      LayoutRect rect; }
class LayoutRect    { float minX, minY, maxX, maxY; }  // 0..1
```

- panel は安定 **`id`** で同定（restore 時に live と突き合わせ。list 順や slot が変わっても id でマッチ）。
- **`slot` は rect と独立フィールド**（順序変更と rect 移動を別 mutation にして次元を潰さない）。
  slot は**論理並び順専用**で、将来 floating window の **zOrder とは同一視しない**（必要時に zOrder 別 field を追加）。
- chart も **id="chart" の同一 PanelLayout** に統一（専用 `chartSplit` フィールドは設けない。split = chart entry の rect）。
- **`LayoutRect` の意味 = 親領域に対する正規化済み表示矩形**（**Unity anchor 値そのものではない**）。現行 UI は
  anchor に加え pixel offset を持つため anchor だけでは表示矩形を表せない。スキーマを RectTransform 実装詳細に
  固定せず、**変換層**が RectTransform（anchor+offset）⟷ 正規化表示矩形の相互変換を担当する（§4）。

## 4. layout binder（owner 確定 2026-06-13）— 用語衝突回避

UI⟷document の変換層を **「adapter」と呼ばない**（CONTEXT.md の `adapter` は engine/pythonnet 境界専用の
予約語）。正準名 **`LayoutBinder`**、API は `Capture`（live → document）/ `Apply`（document → live）。
CONTEXT.md に glossary 追加済み。

- **中核は純 C# 算術**（親領域サイズ + anchor + offset ⟷ 正規化表示矩形）。RectTransform の resolved-rect
  解決に依存させず playmode 不要にする。
- **RectTransform の収集・id 対応は薄い Unity 側境界**に分離（純算術部とは別レイヤ）。

## 5. ゲート設計（owner 確定 2026-06-13）— AFK-only

**#12 は AFK-only**（HITL playmode・Python・新規 auto-bootstrap を**追加しない**）。round-trip は決定的データ +
矩形算術で headless 完全 assert 可能。#11 §8 の single-Play-owner 衝突を無駄に再発させない（#12 は Python 不要）。

単一の **`ReplayLayoutProbe.Run`**（`-executeMethod`）に集約してよいが、内部の**失敗区分を明確に**する:
1. **document mutation / non-vacuous 証明**（§1: mutated≠default, JSON 文字列に変更値実在）
2. **save / load / version / unknown-field**（§6）
3. **Capture 変換**（live → 正規化矩形）
4. **Apply 変換**（document → live 値反映）
5. **fresh target への復元**（新規 instance/target に load→Apply して一致）

実 movable UI での視覚復元は #7 が初めて提供する（現行 panel は固定で視覚 mutation 無し → #12 で HITL は空転気味）。

## 6. version 戦略 / unknown 寛容（owner 確定 2026-06-13）

`CURRENT_VERSION = 1`（今は v1 のみ）。

- **(a) unknown field は ignore-only**。書き戻し時の preserve は保証しない（JsonUtility は drop する）。
  preserve が必要になったら Newtonsoft swap で対応（decoder と同じ swap-point。今は scope 外）。
- **(b) version 不一致の Load 挙動**:
  - `version > CURRENT_VERSION` → **warning** + 既知フィールドを best-effort 使用。
  - `1 <= version < CURRENT_VERSION` → 既知フィールド使用。migration は必要時に追加。
  - `version <= 0 / 欠落 / 非数値` → **無効文書** → **default へ fallback**（必須 version の空転防止。
    version 無しファイルを valid v1 と誤認しない。FILE 欠落＝first-run も default で一貫）。
- **(c) panel-id 寛容**: `Apply` 時、doc にあり live に無い id は **skip**、live にあり doc に無い id は **現状維持**。

probe は **`version: 999` ＋ 未知 field ＋ 未知 panel を同時投入**し、(i) throw せず (ii) 既知値が適用され
(iii) 未知要素が既存 UI state を壊さないことを assert。**warning の有無自体はゲート条件にしない**（brittle）。

## 7. capture 方式 / workspace（owner 確定 2026-06-13）

- **#12 は明示的 `Save`/`Load` ＋ `LayoutPathResolver` まで**。autosave wiring は実変更イベントを持つ
  shell slice（#7 等）へ後送り。
- **per-frame autosave は行わない**。
- 将来の autosave 候補トリガ = **変更確定時** と **正常終了時**（per-frame ではない）。
- **debounce / atomic write は実 wiring 時に決定**（#12 では plain write）。
- 現行パス = **`Application.persistentDataPath` 配下の固定ファイル名**（`LayoutPathResolver` が解決）。
- **`LayoutStore` 自体は明示パスを受ける**（テスト可能性を維持。path 解決と永続化を分離）。
- **単一 global sidecar**。per-workspace は workspace 概念導入時に additive 拡張（`LayoutDocument` を
  workspace でラップする等）。

## 8. ADR の扱い・破損ファイル・パス・Save 意味論（owner 確定 2026-06-13）

- **新規 ADR を起こさない。** ADR-0003 が方針をロック済みで self-protection 節があり、#12 のスキーマ形・
  binder・version 規則は「下位事実」かつ version 戦略で reverse 可能。**本 findings のみに記録し ADR-0003 を
  「方針: ADR-0003」として参照、ADR ファイルは編集しない。**
- **破損/読めない/欠落/無効 version の Load は warning ＋ default fallback**（throw しない）。これは
  **decoder 規律（findings 0002/0003: malformed は握り潰さず throw）からの意図的逸脱**。理由＝信頼境界が違う:
  engine payload は常に valid `json.dumps` 出力なので parse 失敗＝実バグ。layout sidecar は**ユーザのディスク上の
  永続ファイル**で手編集・部分書き込み・旧破損があり得るため、crash は UX 誤り → graceful default fallback。
- **パス** = `Path.Combine(Application.persistentDataPath, "layout.json")`（`LayoutPathResolver` が解決）。
- **`LayoutStore.Save` は #12 では plain overwrite**。**親ディレクトリ作成は Save の責務**。atomic write
  （temp+rename）/ debounce は実 autosave wiring 時に決定（#12 scope 外）。

## 9. 成果物（予定）

| 区分 | 成果物 | 役割 | durability |
|---|---|---|---|
| schema | `Assets/Scripts/Layout/LayoutDocument.cs`（`LayoutDocument`/`PanelLayout`/`LayoutRect`） | versioned スキーマ POCO（class、正規化矩形、UnityEngine 非依存） | **durable** |
| persistence | `Assets/Scripts/Layout/LayoutStore.cs` | `Save(doc,path)`/`Load(path)`、JsonUtility 隠蔽、破損/欠落/無効 version → default fallback、親 dir 作成 | **durable** |
| path | `Assets/Scripts/Layout/LayoutPathResolver.cs` | `persistentDataPath/layout.json` 解決の薄いラッパ | **durable** |
| binder（純算術） | `Assets/Scripts/Layout/LayoutBinder.cs`（中核） | 親領域サイズ+anchor+offset ⟷ 正規化表示矩形（純 C#、playmode 非依存） | **durable** |
| binder（Unity 境界） | 同上 or 薄い別ファイル | RectTransform 収集・id 対応（Capture/Apply の Unity 側） | **durable** |
| AFK ゲート | `Assets/Editor/ReplayLayoutProbe.cs`（`-executeMethod ReplayLayoutProbe.Run`） | 5 失敗区分（§5）を headless assert。破損 JSON＋欠落ファイルの両 default fallback も検証 | durable（regression gate） |

## 10. 射程外（#12 に含めない）

- HITL playmode / Python / 新規 auto-bootstrap（AFK-only）。
- 実 drag/操作 UI・autosave トリガ wiring（#7 等の shell slice）。
- per-workspace、zOrder、Hakoniwa tile 順、canvas pan/zoom（各 shell slice が capability surface へ additive 追加）。
- atomic write / debounce、unknown field の preserve（必要時に Newtonsoft swap）。
- multi-instrument、Windows leg。

## 11. 実装結果（ゲートログ・Mac leg, 2026-06-13）

durable 5 ファイル + AFK probe を直接実装（pair-relay/parallel は使わず — 単一言語 C#・Python 非依存・仕様
完全確定・小スコープのため。CLAUDE.md の Unity スライス規約に対する逸脱理由を明記）。

成果物（全て新規・§9 通り）:
- `Assets/Scripts/Layout/LayoutDocument.cs`（`LayoutDocument`/`PanelLayout`/`LayoutRect`、`Default()`/`Clone()`/
  `StructurallyEqual`。ctor は version=0 sentinel で「version 欠落 → invalid → default」を JsonUtility 挙動非依存に）
- `Assets/Scripts/Layout/LayoutStore.cs`（`Save`/`Load`/`LoadFromJson`、JsonUtility 隠蔽、fail-soft fallback、親 dir 作成）
- `Assets/Scripts/Layout/LayoutPathResolver.cs`（`persistentDataPath/layout.json`）
- `Assets/Scripts/Layout/LayoutBinder.cs`（純算術 `ToNormalizedRect`/`ToCanonicalAnchors` + Unity 境界 `Capture`/`Apply`。
  Apply は rect+visible のみ適用、slot は document メタとして round-trip）
- `Assets/Editor/ReplayLayoutProbe.cs`（5 区分 + fallback、`-executeMethod ReplayLayoutProbe.Run`）

ゲート（VERBATIM, `UNITY_EXIT=0`, CS エラー 0）:
```
[REPLAY LAYOUT PASS] non-vacuous doc<->disk round-trip + version/unknown tolerance + Capture/Apply conversion + fresh-target restore + corrupt/missing fallback (Unity-owned versioned schema, ADR-0003 capability parity, under Unity Mono)
```

AC 達成の証跡:
- **AC1（save→reload で同状態復元）**: S1（mutated→save→fresh load→`loaded==mutated`）＋ S5（disk→load→`Apply` を
  fresh target へ→値反映）。
- **AC2（version + unknown 寛容）**: S2（version 999 best-effort・未知 field/panel 無害・version≤0/欠落→default）。
- **AC3（Bevy reader 不在）**: 自前 JSON sidecar のみ（`grep -riE 'bevy|ron'` 0 件、自前 sidecar 言及のみ）。
- **AC4（round-trip 自動テスト）**: `ReplayLayoutProbe` GREEN。

非空転の証跡: S1 が `loaded!=default` ＆ on-disk JSON が default の逐語 JSON と非一致 ＆ `"false"`（hide mutation）が
テキストに実在、を構造 assert（vacuous round-trip kill）。

### #9-12 レビュー指摘 Medium-2（default が正規化表示矩形でなく raw anchor, fixed 2026-06-13）

レビュー指摘: `LayoutDocument.Default()` の rect は §3 が定める **正規化表示矩形**ではなく、
`ReplayPanelsHarness` が実 UI の **外側 panel** に設定する pixel offset（chart `(60,40)/(-10,-20)`・
panel `(4,4)/(-8,-4)`）を無視した **raw anchor 値**だった。`LayoutBinder.ToNormalizedRect` は
offset を正規化 rect に**畳み込んで表示矩形を保存**する設計（Capture→Apply で同じ表示矩形を
anchor として再現）なので、実 UI を Capture すると gutter 込みの box になる一方、`Default()` は
gutter 抜きの box。→ missing/corrupt fallback が現行 default UI と**異なる配置**になる。

**初回の誤修正（Option (b)）**: 「`Default()` の意味を canonical offset-zero と確定し doc を正直化」
だけでは、**divergence を意図的仕様として固定しただけ**で UI 不一致は残る（再レビューで指摘）。

**確定修正（Option 1: harness の外側 panel を offset-zero 化, owner 確定 2026-06-13）**:
永続化対象である**外側 panel の offset をゼロ**にし、chart 軸ラベル gutter `(60,40)/(-10,-20)` は
chart panel の **子 `PlotArea`** へ、panel padding は **子 `Text`** inset へ移した（= widget 内部
chrome で seam は永続化しない）。これで **実 default の panel 配置 == `Default()`** が全解像度で成立し、
fallback は live default を正確に再現する（畳み込まれて失われる gutter が無い）。candle サイズ算出は
`_chartArea.rect` → `_plotArea.rect` に変更。

不変条件を probe **Section 7** で両方向に機械固定: (a) harness の offset-zero panel anchor を
Capture したものが `Default()` と構造一致（doc が UI を反映）、(b) `Apply(Default())` を fresh target
へ適用すると anchor == live default の box・offset == 0（fallback が live default を canonical/解像度
非依存に再現）。`ReplayLayoutProbe` 再ゲート `UNITY_EXIT=0` / CS エラー 0:
```
[REPLAY LAYOUT PASS] non-vacuous doc<->disk round-trip + version/unknown tolerance + Capture/Apply conversion + fresh-target restore + corrupt/missing fallback + Default()==live-default panel layout (Unity-owned versioned schema, ADR-0003 capability parity, under Unity Mono)
```
harness の panel 構造（offset-zero + `PlotArea` 子）は batchmode compile GREEN（CS エラー 0）。実描画
（panel が隣接・chart gutter が PlotArea 経由で従来通り）は HITL 目視が owner 待ち（AFK は Python 不要の
layout probe のみ。harness playmode は #11 §11 手順で次回 Play 時に確認）。

### 射程逸脱（設計から実装で 1 点変更）
`LayoutBinder.Apply` は当初案の `SetSiblingIndex(slot)` を**外した**。現行の非重複 stacked panel では sibling index
は視覚的に無意味で、逐次 `SetSiblingIndex` の最終 index が脆い assert になるため。slot は「論理並び順専用」（§3）
として **document レベルで round-trip を証明**（S1 の `StructurallyEqual` が slot を含む）し、live への順序適用は
実 ordering semantic を持つ shell slice（#7 の z-order / Hakoniwa tile 順）に委ねる。
