---
status: accepted
---

# ADR-0038: Shippable build は GitHub Actions を退役しローカル script で配布する

> 関連: [ADR-0014](0014-ci-shippable-build-runner-self-hosted.md)（本 ADR が supersede する CI runner 決定 / その self-protection clause が「runner 方針の変更は新規 ADR を要する」と要求）/ [findings 0050](../findings/0050-ci-shippable-build-pipeline.md)（CI pipeline 設計・本 ADR で退役）/ [findings 0049](../findings/0049-shippable-standalone-bundled-venv.md)（artifact hermetic 設計・不変）/ issue #180

ADR-0014 の self-protection clause（「GitHub-hosted に戻す / 別 Path に切り替える等は新規 ADR を要し、0014 への edit で書き戻すことを禁ずる」）に従い、CI runner 方針の変更を宣言する ADR。

## Context

ADR-0014 は「Unity 6 Personal license は hosted runner で activate 不可」ゆえ shippable build を **self-hosted Windows runner** に降格した。しかし運用で以下が判明した（issue #180）:

| 事実 | 検出 |
|---|---|
| `deploy-gh-release.yml` が存在しない action `game-ci/unity-setup@v2` を参照し、**全ラン即失敗**（"Unable to resolve action, repository not found"） | 直近 10 ラン全滅（2026-06-25〜26） |
| 移行コミット `c0c30fa` が ADR-0014 に反し `runs-on` を `windows-2022`（hosted）へ戻し、GameCI license activation を再導入していた | git 履歴・workflow 本文 |
| owner マシンに **self-hosted runner が未登録**（ディレクトリ/サービス/プロセス不在）。登録には owner の手作業（registration token + `config.cmd` 常駐）が要る | 実機調査 |
| agent の CI token に `workflow` scope / runner 権限が無く、修正 push も runner 登録も owner 手作業が必須 | push 403 / API 403 |

ここで本質的な問いが立った: **self-hosted runner は「結局 owner マシンで Unity build を走らせる」だけであり、GitHub Actions の枠（runner 登録の常駐運用・job queue・`GITHUB_TOKEN` の owner マシンへの流入）は中間マージンに過ぎない**。solo-dev・単一 build マシン・HITL publish 前提では、その枠は価値より摩擦が大きい。

## Decision

shippable Windows64 build の配布を **ローカル PowerShell script `scripts/build-and-release.ps1`** に一本化し、**GitHub Actions workflow `deploy-gh-release.yml` を退役（削除）する**。

- build/smoke/zip/SBOM/SHA256SUMS/draft-Release-upload を owner マシン上で 1 コマンド実行（workflow の実証済みステップを移植）
- Unity は owner マシンの local install + 既に activate 済みの license をそのまま使う（GameCI / `UNITY_LICENSE` secret 一切不要 → secret は遺物となり削除可）
- Release は既定で **draft**（HITL gate は不変: clean machine で extract → Replay AC → Publish click）
- 退役に伴い queue 中の workflow run はキャンセルする

### script が workflow より堅牢化した点（実機 RED→GREEN・issue #180）

owner マシン実機で通すために、workflow の素朴な PowerShell では踏んでいた環境固有の罠を script 側で塞いだ:

1. **Unity shutdown hang**: `Start-Process -Wait` は build 成功後に Unity が shutdown で hang（duckdb 上流 segfault・commit da794cf）すると無限ブロック → **artifact をポーリングし timeout で force-kill**、成否は exit code でなく成果物で判定
2. **cross-dir `Move-Item` 失敗**: bundled venv tree（>MAX_PATH・lazily-created .pyc）で copy-fallback が "already exists" → **build/ 内 atomic rename + retry**（Defender real-time scan の transient lock を back-off 吸収）
3. **GNU tar の `C:` 誤認**: PATH 上の Git tar が絶対パス `C:\...` を remote host と誤読 → **System32 bsdtar を明示 pin**
4. **buffered silence**: redirect 時 `Write-Host` が flush されず「停止」に見える → repo 直下の progress log へ即時 flush（Unity が wipe する `Temp/` は不可）

## Consequences

### 失われるもの（honest assessment）

- **Sigstore attestation（`actions/attest-build-provenance`）が消える**: GitHub Actions 専用 action。ローカルでは cosign 等で別途実装可だが当面は省略。再現性の本丸は ADR-0014 で論じた **HITL clean-machine verify** なので実害は限定的
- **push trigger の自動性**: tag を push しても自動で build されない。owner が script を手で叩く（solo-dev では HITL publish 前提なので元々 owner の手が要った）
- **weekly cron の bit-rot / runner 死活監視が消える**: self-hosted runner 自体が無くなるので runner 監視は不要化。bit-rot 検知は owner が随時 `-SkipRelease` で回す運用に置換

### 保たれるもの

- **artifact hermetic 設計**（findings 0049 / ADR-0002）は不変。bundled cpython+venv・`pyvenv.cfg` 削除・runtime-manifest schema check はすべて `BackcastShippableBuild` 側にあり script は呼ぶだけ
- **HITL gate**（draft → clean machine 検証 → Publish）不変
- **smoke 4 stages**（zip 整合 / manifest schema / venv import / Player GUI contract）不変
- single-top-folder zip・SHA256SUMS・CycloneDX SBOM も移植済み

## Self-protection clause

本 ADR の decision は固定。「GitHub Actions に戻す」「self-hosted runner を再導入する」等の変更は **新規 ADR を要し、本 ADR への edit で書き戻すことを禁ずる**。

将来 Unity 社が hosted Personal activation を復活させた場合、または複数 build マシン / fork PR build / 公開 CI signal が必要になった場合（=ローカル script の前提が崩れる場合）は、新 ADR で宣言すること。本 ADR は暫定 workaround ではなく、solo-dev・単一 build マシン・HITL publish という運用前提への構造的応答である。

findings 0050 は本 ADR で退役した旨を明記する（その Change policy は維持）。
