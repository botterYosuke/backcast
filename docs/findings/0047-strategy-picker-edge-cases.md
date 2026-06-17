# findings 0047 — 戦略 .py ピッカーの分岐挙動（#80・grill-with-docs 設計ドリル）

`grill-with-docs`（2026-06-17・owner インタビュー）で導出。受け皿 issue **#80**（`戦略 .py をアプリ内で開く UI が
無い`）。#78（WYSIWYR・findings 0044）の直接の後続、#16（Strategy Editor / provider seam・findings 0010）・
#66（inline SCENARIO fallback・findings 0043）に隣接。発火: v19 Replay HITL（2026-06-17・findings 0029）。

> **状態: ピッカー分岐の挙動を lock（owner 2026-06-17）。** 実装着手時に §5 へ証跡を追記する。

---

## 0. 大前提（コードが既に決めていること・本ドリルで裏取り済み）

- **列挙は `.py` のみ**。`.json` は「開く対象」ではなく `SidecarPathFor(.py) = Path.ChangeExtension(.py, ".json")` で
  導出される随伴ファイル（`ScenarioSidecarStore.cs:44`）。
- open→bind 後に `SeedScenarioFromEditor`（`BackcastWorkspaceRoot.cs:234`）が走り、scenario populate の優先順位は
  **sidecar `.json` ?? inline `.py` SCENARIO**（`ScenarioStartupController.Populate` の `ReadScenario(sidecar) ?? fallback`・
  `:50`。sidecar が going-forward の SoT として inline に勝つ）。
- inline reader `ScenarioInlineReader.Read` は **Python-free / Awake-safe / never-throws**、status を 3 値で返す:
  **Found / Absent / Unparseable**（`ScenarioInlineReader.cs:30,36`）。
- `SeedScenarioFromEditor` は **Unparseable のとき既に** `ShowMessage("strategy SCENARIO unreadable — save a scenario
  sidecar…")` を出す（`:240-241`・#66 の silent-drop 防止 measure）。Absent は無通知で SeedDefaults（空 universe）。

## 1. 分岐ごとの正しい挙動（決定表・owner 確定 2026-06-17）

| ケース | ピッカー表示 | Open 可否 | open 後の状態 |
|---|---|---|---|
| `.py`あり / `.json`なし（#80 repro=新規戦略） | 列挙する | 可（無条件） | sidecar 無→inline fallback。SCENARIO **Found**→universe populate→Run 解禁。**Absent**→空・Run blocked・無通知。**Unparseable**→空・Run blocked・`ShowMessage` |
| `.py`なし / `.json`あり（orphan sidecar） | 列挙しない（`.py` 列挙なので構造的に出ない） | — | 開けない。sidecar 単独では走らせる本体が無いので正しい |
| 両方あり（通常・v19） | 列挙する | 可 | **sidecar が inline に勝つ**。✅ |
| stale list で `.py` が消えた状態でクリック | — | `File.Exists` ガードで弾く | crash させず message＋リスト再列挙 |

**精度補正（issue 文面の言い分け用）**: Absent ケースの「Run blocked」の権威は2系統あり混同しない:
- **#78 保証**＝「エディタ**未 bind** → `SeedScenarioFromEditor` が early-return し何も seed しない → Run blocked」。
- Absent ケース＝「bind **済み**だが SCENARIO 無し → universe 空 → no instruments で Run blocked」。
結果は同じ "走らない" だが発火経路が別ゲート。

## 2. 設計判断（owner 確定 2026-06-17）

### ① Open は scenario の有無で gate しない（無条件で開ける）
Absent/Unparseable の `.py` こそ「開いて直す」対象。Open を valid scenario で塞ぐと、壊れた/新規 `.py` を編集で直す
導線が消える。**ブロックは Run gate の責務で Open gate の責務ではない**（#78 の「未ロード→走らない」は Run 側で既に効く）。

### ② orphan `.json` は列挙も自動修復もしない・列挙対象は `.py` 一本厳守
`.py` を rename して `.json` が取り残されるケースはあるが、ピッカーが reconcile（自動削除/生成）する責務は持たせない。
`.json` を別エントリで出すと「sidecar を開く」誤操作を誘発する。

### ③（採用・最有効）各 `.py` 行に scenario status を注記する
開く前に何が起きるか可視化。既存の pure-C# reader を流用するだけで AFK-probe 可:
- `File.Exists(SidecarPathFor(py))` → `scenario: sidecar`
- 無ければ `ScenarioInlineReader.Read` の status → `inline` / `none (Run blocked)` / `⚠ unreadable`

#80 repro の「赤字 scenario 未選択・No instruments」が、開く前に `v19_morning.py — scenario: none` と分かる。owner が
「開いたのに Run できない」で迷子になる事故を消す。

## 3. 本ドリルで lock した端（決定表が当初カバーしていなかった2点）

### A. 列挙フィルタ = **全 `.py` 列挙**（scenario 有無でフィルタしない）— 実質強制
Option「scenario を持つ `.py` 限定」は **#80 の存在意義と矛盾**: repro は「新規 `.py`・scenario まだ無し（Absent）」を
開く導線で、フィルタするとその `.py` が永久に列挙から消え①と直接矛盾する。Python-free では sidecar/inline の有無しか
判定材料が無く、それは「開くに値するか」の指標ではない（新規は当然 scenario 無し）。
- **ヘルパ `.py`（`features.py` 等）ノイズはフィルタを正当化しない**: 現状 `python/strategies/**` は `v19_morning.py`
  1本のみ＝ノイズは未存在（YAGNI）。Python-free で「Strategy サブクラスか」は厳密判定不可だが、#80 の責務は「`.py` を
  開く」であって「Strategy を分類する」ではない。③ status 注記＋Run gate（バインドしても scenario 無しで Run 封鎖・
  crash なし）で誤操作を吸収。将来ノイズが実害化したら別スライスで軽量ヒューリスティック（lexical な `class …Strategy`
  注記グレーアウト、`strategies/lib/` 規約での除外等）。**#80 で先回り filter を入れると新規戦略を巻き込むリスクの方が高い。**
- `__pycache__` は `*.py` 列挙では `.pyc` ゆえ自然に除外（明示除外でも可）。

### B. Unparseable 時の通知 = **既存の開く瞬間 `ShowMessage` を維持＋行注記**（既存挙動は不変）
「行注記だけ（open 時 `ShowMessage` 抑制）」は却下。開く瞬間の `ShowMessage` は #66/findings 0027・0043 の意図的な
silent-drop 防止 measure（`SeedScenarioFromEditor` が Unparseable 限定で出す既存挙動）。抑制するには
`SeedScenarioFromEditor` に手を入れる＝#66 保証を #80 が弱めるスコープ逸脱・回帰リスク。二重通知ではなく**異なる時点・
目的**:
- 行注記（③・開く前）＝選ぶ前に「⚠ unreadable」と知らせる。
- `ShowMessage`（開いた後）＝今エディタを見ている owner に「sidecar を保存せよ」とその場で actionable に促す。
Unparseable は「開いて直す」典型（①）＝開いた直後に直し方を出すのは UX 上正しい。**行注記は additive、`ShowMessage` は
touch しない**＝#80 AC「既存 restore 経路は不変」と整合。

## 4. 射程外（隣接スライスとして明示）

- **アプリ内で新規 `.py` を作る機能**（repro は owner が外部で作成）→ #80 は「既存 `.py` を開く」で repro 解消。新規作成は
  別スライス（#69/#32 隣接）。
- **任意パス（`python/strategies/` 外）の `.py` を開く**・OS ネイティブダイアログ → #69 の native picker 領域。#80 は
  `python/strategies/**` 簡易列挙で十分。
- **複数窓 active-pick**（複数 `.py` を別窓で開く）→ #78 同様スコープ外（#80 提案にも明記）。

## 5. 実装証跡（着手時に追記）

（未着手）

## 6. ADR 判断

新規 ADR は起こさない。可逆・additive・既存 seam（`SeedScenarioFromEditor` / `ScenarioInlineReader` / `SidecarPathFor`）の
**呼び出し元を足すだけ**で新たな不変条件を導入しない。方針は #78（WYSIWYR・findings 0044）/ #66（findings 0043）に
従属し、本 findings が #80 の下位事実の正本。
