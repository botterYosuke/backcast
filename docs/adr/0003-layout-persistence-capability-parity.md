---
status: accepted
---

# レイアウト永続化は capability parity（Unity 自前の versioned スキーマ・Bevy 形式に非依存）

#3 の AC「**レイアウト保存/復元が Bevy と同等**」の「同等」を定義する。`grill-with-docs`（2026-06-12）で導出。
実装は shell スライス（floating window / Hakoniwa / infinite canvas）に後送りだが、**parity の定義だけは
ここでロック**し、shell スライスの作者が誤って「Bevy layout 形式の reader」を作る手戻りを防ぐ。

関連: ADR-0001（移行順序・TTWR は #5 で廃止）。

## Context

UI は Bevy(Rust) ~43k 行の全面書き直しで、widget も別物（Bevy ECS の entity/transform 表現 ↔ uGUI の
floating window）。「Bevy 同等」を**バイト互換（同一 sidecar スキーマ）**と解釈すると：

- **死ぬ予定のシステムの内部表現に going-forward コードを縛り付ける逆依存**になる（Bevy は #5 で廃止）。
- Bevy の layout スキーマは Bevy ECS（entity id・Bevy transform）に形を規定されており、uGUI と根本的に違う。
  互換のための翻訳層は誰の going-forward 利益にもならない。
- CONTEXT.md の「**移植であって依存ではない／TTWR を生かしたまま参照しない**」と正面衝突する。

## Decision

1. **parity = capability parity**。「save→restore のラウンドトリップで**同じ UI 状態**が甦る」ことを parity とする。
   バイト互換ではなく能力等価。
2. **スキーマは Unity が自前所有**。floating window の rect / z-order、Hakoniwa tile 順、canvas pan/zoom、
   （後で）Strategy Editor の開いていたファイル等を、Unity 自前の **versioned** スキーマ（JSON sidecar）で永続化する。
   **Bevy スキーマには一切アンカーしない**。
3. **非可逆なのは形式ではなく以下**（ここを ADR の決定事項にする）：
   - **capability surface**：何を persistable state とするか（取りこぼすと全 widget に波及）。
   - **capture 方式**：いつ/どう capture するか（autosave trigger・per-workspace か global か）。
   - **version 戦略**：`version` フィールド＋unknown フィールド寛容（forward-evolvable に保つ）。
4. **cutover 時の既存レイアウト移行は行わない**（owner 決定 2026-06-12）。Unity で作り直す。
   → Bevy sidecar を読む **一回限りの移行スクリプトも不要**。live コードパスにも one-shot にも Bevy 依存を入れない。

### slice の射程

- **今ロック（本 ADR）**：parity = capability、Unity 自前 versioned スキーマ、Bevy 形式 非互換。
- **後送り（shell スライス）**：実際の save/restore 実装（floating/Hakoniwa/canvas が出来てから）。
  slice-1 tracer の射程外。

## Considered Options

- **採用：capability parity（Unity 自前 versioned スキーマ）**。going-forward が廃止予定システムに逆依存しない。
- **不採用：format interop（Bevy sidecar スキーマを Unity が読み書き）**。死ぬシステムの内部表現にアンカーし、
  ECS↔uGUI の表現差で翻訳層が要り、CONTEXT.md の移植方針と衝突。
- **不採用：cutover 一回限り移行スクリプト**。owner が「作り直しでよい」と判断（個人レイアウトの引き継ぎ不要）。

## Consequences

- shell スライスの作者は **Bevy format reader を作らない**。Unity 自前スキーマのみ実装する。
- capability surface / capture 方式 / version 戦略は shell スライス着手時に詰めるが、本 ADR の枠（自前・versioned・
  非 Bevy）を外れない。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
capability surface の具体項目など下位事実は本 ADR に書き戻さず、shell スライスの `docs/findings/` に記録し
本 ADR を「方針: ADR-0003」として参照する。
