---
status: accepted
---

# scenario sidecar の merge-write は Newtonsoft（JObject）を用いる（unknown-field PRESERVE・layout は JsonUtility 据え置き）

#29（Replay 実行設定パネル）で導出。`grill-with-docs`（2026-06-14）。Unity の scenario startup panel が
`<strategy>.json` の `"scenario"`（engine 所有・v3）を編集・永続化するにあたり、**panel が触らない v3 optional
フィールド（`account_type` / `instruments_ref` / `strategy_init_kwargs`）を漏れなく保存する merge-write** をどう
実装するかを決める。

## Context

- engine の `scenario.validate` は **strict**＝`_check_keys` が未知キーを reject する。よって sidecar の `scenario`
  object は v3 有効キーだけで構成されねばならず、merge-write が書式・エスケープ・キー順を壊すと、strict-validated
  なユーザーの strategy 設定 sidecar を **corrupt** させる（最悪の失敗）。
- panel が編集するのは `start` / `end` / `granularity` / `initial_cash` / `instruments` の 5 キーのみ。残りの v3
  optional、特に **`strategy_init_kwargs` は任意 nested dict** で、preserve には full-DOM round-trip が要る。
- backcast はこれまで `JsonUtility` ＋ハイブリッド `PeelTag` デコーダ（`DepthDecoder` / `LiveBackendEventDecoder`）で
  通し、Newtonsoft を **manifest に入れていない**。ただし `LayoutStore.cs:6` / findings 0004 §6a が
  「**unknown-field PRESERVE が必要になったら Newtonsoft へ swap**」を**予告済み**。#29 の `strategy_init_kwargs`
  preserve がまさにその trigger。
- 移植元 TTWR の parity target は `scenario_sidecar/write.rs` の `atomic_mutate_scenario_object`＝`serde_json::Value`
  の **full-DOM read-modify-write（sibling 全保存）**。`serde_json::Value` の C# 等価物は **Newtonsoft `JObject`**。

## Decision

1. **scenario sidecar の merge-write は Newtonsoft `JObject` の read-modify-write で行う**（`com.unity.nuget.newtonsoft-json`
   を manifest に追加）。JSON 全体を `JObject` に parse → `scenario` object の対象キー（5 つ）だけ set → sibling
   （`account_type` / `instruments_ref` / `strategy_init_kwargs` / `schema_version`）を verbatim 保存 → atomic write。
   TTWR `atomic_mutate_scenario_object` の正確な parity。
2. **Newtonsoft は単一 seam に封じ込める**。`ScenarioSidecarStore`（仮）の中だけで `JObject` を使い、呼び出し側は
   `SetStartupParams` / `SetInstruments`（TTWR `set_startup_params` / `set_instruments` の parity）だけを見る。
   `LayoutStore` の parser-hiding 規律を踏襲し、将来の差し替え余地を残す。
3. **layout sidecar は `JsonUtility` のまま据え置く**（`LayoutStore` は変更しない）。Newtonsoft は scenario sidecar の
   merge-write という unknown-field PRESERVE が必須の一点にのみ導入する。

## Considered Options

- **採用：Newtonsoft `JObject` merge**。任意 nested dict を無損失で跨ぐ DOM を持ち、strict-validated object を壊さない。
  TTWR writeback の正確な parity。`LayoutStore.cs:6` が予告した escape hatch の発動＝ポリシー逸脱ではなくポリシー遵守。
- **不採用：ハイブリッド手動 merge（PeelTag 型）**。`PeelTag` 慣例は engine payload（常に valid・READ 専用・malformed は
  throw）の read デコーダで、`LayoutStore.cs:12` が言う「逆の trust boundary」＝ユーザー編集可能ファイルへの
  read-modify-write には category 違い。pretty-print された nested `strategy_init_kwargs` を raw で跨ぐ string surgery は
  whitespace/escape/キー順事故でユーザーデータを corrupt させる。依存削減の対価が corruption リスクでは引き合わない。
- **不採用：`JsonUtility` ＋ `strategy_init_kwargs` 非対応明記**。zero-dependency だが任意 dict を round-trip できず、
  v3 が正式サポートする `strategy_init_kwargs` を持つ strategy の設定編集が #29 でできない＝capability gap で、いずれ
  強制的に Newtonsoft 移行に追い込まれる決定の先送り。現行フィクスチャが kwargs 未行使なら interim としては成立するが、
  ここで決着させる方が手戻りが無い。

## Consequences

- プロジェクト初の Newtonsoft 依存が入る。ただし scope は `ScenarioSidecarStore` 一点に contain され、layout を含む
  他の JSON 経路は `JsonUtility` / `PeelTag` のまま。
- scenario sidecar の round-trip は v3 full schema（optional 含む）を無損失で保てる。AC②（再起動後の復元）と AC④
  （不正値拒否）の土台が、ユーザーの既存設定を壊さずに成立する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
下位事実（`ScenarioSidecarStore` の API 詳細・schema_version の既定値など）は本 ADR に書き戻さず、#29 の
`docs/findings/` に記録し本 ADR を「方針: ADR-0005」として参照する。
