---
status: accepted
supersedes: ADR-0040 §D2 (char[] 無バッファ secret 規律) — kabu API パスワードの入力部分のみ
---

# kabu ログインのパスワードを uGUI InputField(Password) にする — ADR-0040 D2 の char[] 無バッファ規律を撤回

`grill-with-docs`（2026-06-30, owner HITL）で導出。issue #181 follow-up。owner の報告:
「#181 で venue ログインを Unity uGUI にしたが、**パスワードの入力欄が無い**」。

## 背景

ADR-0040 D2 は kabu の API パスワードを「**managed string を一切作らず** char[] バッファに直接ためる
（`SecretModalOverlay` の `onTextInput` 方式）」と決めた。`VenueLoginModalOverlay` は実際この規律に従い、
パスワード部を **本物の InputField ではなく背景なしの `Text` ラベル**（`_maskedPw`・`MakeLabel`）で描き、
入力はモーダル表示中にグローバルな `Keyboard.onTextInput` を hook して masked dot（`•`）を出していた。

結果、kabu モーダルのパスワード部は **枠も背景も無い透明領域**として描画され、ユーザーには
「入力欄が存在しない」ように見えた（クリックしてもフォーカス概念が無い・`raycastTarget=false`）。
機能（タイプすれば `•` が出て送信できる）は生きていたが、**発見可能性（discoverability）が無い**。
AFK gate（VLOGIN-MODAL-01..09）は controller だけを駆動し overlay の見た目を assert しないため、
この欠落は gate を素通りし owner HITL 目視で初めて露見した。

## Decision

**kabu の API パスワード入力を、Tachibana の認証ID／秘密鍵パスと同じく本物の uGUI
`InputField`（`contentType = Password`・legacy `UnityEngine.UI.InputField`）にする。**
これにより ADR-0040 **D2 の「char[] 無バッファ・managed string を作らない」規律を撤回**する。

- onTextInput グローバル hook ・char[] バッファ・masked `Text` ラベルの機構（`AppendSecretChar` /
  `BackspaceSecret` / `MaskedPassword` / `Subscribe`/`OnTextInput` / `CharTyped` / `BackspacePressed`）を撤去。
- controller はパスワードを通常の managed `string Password` で保持する（`SetPassword`）。`CanSubmit`（kabu）は
  `StationRunning && Password.Length > 0`。`Close` / mode 切替 / submit 後は `Password = ""` でクリアする
  （**ただし零化はできない**——下記トレードオフ）。
- C#↔Python 境界（`WorkspaceEngineHost.SubmitVenueLogin(char[] secret)`）の signature は**変えない**:
  controller の `TakeSecretTransient()` が `Password.ToCharArray()` を返し、host は従来どおり使い捨て char[] を
  RPC 後に zeroize する（blast radius 最小化）。

ADR-0040 のコア（uGUI モーダル化・tkinter 廃止・オーケストレーション反転・D1/D3/D4）は**一切変えない**。
本 ADR が撤回するのは D2 の secret バッファ規律 *だけ*。

## Considered Options

- **採用: 本物の InputField(Password)。** クリック / キャレット / フォーカスが uGUI 標準で得られ、誰が見ても
  入力欄だと分かる。owner が #181 follow-up で明示要求。代償は下記の零化不能 managed string。
- **不採用: char[] のまま箱＋プレースホルダだけ足す。** 透明ラベルを FieldColor の `Image` 箱に入れヒント文を
  出せば discoverability は上がり secret 規律も保てるが、owner は「本物の入力欄」を要求し本案を却下。
- **不採用: char[] へ routing するカスタム masked field。** InputField 同等の見た目（箱・キャレット・click-to-focus）
  を保ちつつ `m_Text` を使わず char[] に流す自作 field。規律を保てるが InputField 内部と戦う fragile な大改修。

## Consequences

- **UX 解消**: kabu パスワードがクリック可能・キャレット付きの本物の入力欄として描画され、「入力欄が無い」が解消。
- **セキュリティ後退（受容済み）**: kabu API パスワード平文が **零化できない managed string** として GC ヒープに
  載る（`InputField.m_Text`・`contentType=Password` は*表示*のマスクのみで backing store は平文 string）。
  GC コンパクションでコピーが残り得るため、平文が**メモリ上に無期限に滞留**する（crash / メモリダンプに乗り得る）。
  脅威モデルは同一マシン上の kabuStation アプリのローカルパスワードのメモリスクレイピング耐性であり、
  owner は discoverability を優先して本後退を受容した（2026-06-30 HITL）。
- **gate**: AFK probe を overlay レベルへ拡張し、kabu パスワードが「contentType=Password の生きた InputField」
  であることを assert する（透明ラベル回帰＝本件を直接捕捉する litmus）。char[] 機構を gate していた section
  （`MaskedPassword` / `SecretIsZeroed` 系）は string 版へ移管し、`SecretIsZeroed` は「`Password == ""`」へ honest 化
  （真の零化は不能になったため意味を弱める旨を明記）。
- **下位事実は findings に固定**: 撤去した個々のシンボル・新 `InputField` のフィールド配置・gate の assert・
  RED→GREEN・AFK 再走手順は `docs/findings/0132-*.md` に記録し、本 ADR を「方針: ADR-0042」として参照する。

## 自己保護

本 ADR の decision は固定。覆す（kabu パスワードを char[] 無バッファへ戻す／別方式にする）場合はこのファイルを
編集せず、**本 ADR を supersede する新規 ADR** を起こす。ADR-0040 は自己保護条項により編集しない——本 ADR は
ADR-0040 §D2 のみを supersede し、ADR-0040 のコア（uGUI 化・tkinter 廃止・反転）は不変で再利用する。
SecretModal（`SecretModalOverlay` / `SecretModalController`）の char[] 無バッファ規律は **kabu ログインには
適用しない**が、SecretModal 自身（他の secret 入力面）には引き続き適用する——本 ADR の撤回は kabu ログイン
パスワードに限定される。
