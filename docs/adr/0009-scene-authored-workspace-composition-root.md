---
status: accepted
---

# 本線起動入口を scene-authored composition root（`BackcastWorkspace.unity`）に一本化する

`grill-with-docs`（2026-06-15・#59）で導出。production の **起動入口の所有形態**を固定する。既存 ADR
（ADR-0001 ホスト構成 / ADR-0003 layout capability parity / ADR-0005 1:1 表面 parity）はいずれも
「通常 Play の唯一の起動入口を誰が所有するか」を決めていない。本 ADR がその抜け穴を埋める。

上位方針として **ADR-0001 / ADR-0003 / ADR-0005 を参照**する（いずれも supersede しない）。

## Context

各 UI 表面（menu bar / sidebar / chart / footer / Strategy Editor floating window / infinite canvas /
Hakoniwa / floating window）は個別 issue で移植済みだが、それらを **1 画面に合体させる本線 scene** が
ADR-0005 にも既存 issue にも欠けていた（#59）。現状は各 HITL ハーネスが `RuntimeInitializeOnLoadMethod`
で Play 時に UI を個別組み立てし、`ScenarioStartupHitlHarness`（throwaway）が暫定 Play-owner として
Python engine を所有している。production の起動入口・単一オーナー・engine orchestration の置き場が未定。

## Decision

1. **production entry は `Assets/Scenes/BackcastWorkspace.unity`**。EditorBuildSettings の先頭かつ唯一の
   有効 scene とする。
2. **scene-authored な `BackcastWorkspaceRoot` が唯一の通常起動・Python owner**。UI 表面を scene 上の
   GameObject として authoring し、root が authored View と engine を結線・所有する（single Play-owner）。
3. **`RuntimeInitializeOnLoadMethod` 自動 bootstrap は HITL / spike 用途に限定**する。production 起動入口
   としては使わない。
4. **Replay orchestration は durable な `ReplayEngineHost` が所有**（engine lifecycle / launcher / poll /
   transport RPC）。`BackcastWorkspaceRoot` は Host と View の**結線**を担い、orchestration 本体は抱えない。
   `ScenarioStartupHitlHarness` は throwaway として demote する。

## Considered Options

- **採用：scene-authored composition root**。production entry が名実ともに本線 scene になり、単一オーナー・
  orchestration 分離が構造で保証される。代償：scene / build settings の変更、authoring 作業コスト。
- **不採用：`RuntimeInitializeOnLoadMethod` 方式の踏襲**。既存ハーネスと同形で着手は容易だが、production の
  起動入口が「scene に何も無い」ままで本線 scene の語に反し、単一オーナーをフラグ運用に依存し続ける。
- **不採用：B-thin（scene に器だけ置き UI は全動的生成）**。authoring の利益（serialized reference 移行・
  Inspector での無効化）を得られず、scene-authored の意図を満たさない。
- **不採用：scene-authored full composition（全部品をプレハブ化して authored 配置・既存ビルダー一括廃止）**。
  各表面 issue の責務を侵食し diff が巨大。段階的 serialized reference 移行で十分。

## Consequences

- **scene / build settings の変更**が伴う（新 scene 新設・EditorBuildSettings 差し替え）。
- 個別 Python HITL を実動させるには **Play 前に root GameObject を無効化**する必要がある（root 稼働中は
  `PythonEngine.IsInitialized` 判定で安全に拒否され、engine を奪い合わない）。
- 詳細な authored 階層・layout 永続化順序・検証項目（AFK probe / owner-run HITL）は
  **`docs/findings/0025-backcast-workspace-root.md`** を参照（本 ADR には重複させない）。

## Status note

`status: accepted`（2026-06-16）。#59 の実装＋AFK ゲート＋owner-run HITL 全項目の受入完了をもって昇格
（findings 0025 §11 に記録）。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
下位の実装事実は本 ADR に書き戻さず findings 0025 に記録し、本 ADR を「方針: ADR-0009」として参照する。
