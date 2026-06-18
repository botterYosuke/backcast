---
status: accepted
---

# ADR-0014: CI shippable build runner は self-hosted（Unity 6 Personal license の hosted-runner 不可ゆえ）

> 関連: [findings 0050 §Principle P1](../findings/0050-ci-shippable-build-pipeline.md)（本 ADR が修正）/ [findings 0049](../findings/0049-shippable-standalone-bundled-venv.md)（artifact hermetic 設計） / [ADR-0002](0002-embedded-python-runtime-placement-and-resolution.md)（runtime placement・本 ADR が依拠する artifact 自己完結性）

`/grill-with-docs`（2026-06-18・#83）で確定した「artifact が owner-machine state から escape する thesis を hosted runner で実現する」原則（findings 0050 §Principle P1）を、Unity 社の license 制度変更により**修正する**ための ADR。

## Context

#83 で CI shippable build pipeline を **GitHub-hosted `windows-2022` + GameCI** で実装し、commit `16bae9b` で landed。初回 onboarding 中に以下が立て続けに判明した:

| 事実 | 検出方法 |
|---|---|
| `game-ci/unity-request-activation-file@v2` は 2025 に deprecation | `gh run watch` run #27753211797（2026-06-18 10:27 UTC）で実機 fail / annotation 確認 |
| Unity 公式 manual activation page（`license.unity3d.com/manual`）から Personal 選択肢が完全撤去 | owner ブラウザ確認（URL: `license.unity3d.com/manual/serial/new`・"Enter your serial number to activate your Unity Plus or Pro license."）|
| Unity 6 は legacy `.ulf` を Unity Hub が生成しない | dev 機の `C:\ProgramData\Unity\Unity_lic.ulf` 不在を確認 |
| Unity 6 の新フォーマット `UnityEntitlementLicense.xml` は **MAC アドレス machine binding** を含む | dev 機の `$LOCALAPPDATA\Unity\licenses\UnityEntitlementLicense.xml` 内に `<Identifier Id="f4:5c:89:c5:82:7b" Type="Legacy.MachineBinding5" />` を確認 |
| `Unity.exe -batchmode -createManualActivationFile` で `.alf` 生成は可能だが、Unity 社 web upload 後の flow が Personal を受け付けない | local PowerShell で `.alf` 生成 → web upload → serial-only page に到達 |

→ **Unity 6 + Personal license + GitHub-hosted runner の組み合わせは Unity 社の license 制度変更により構造的に成立しない**。findings 0050 §Principle P1 の前提が消滅した。

代案 5 つを評価:

| Path | コスト | Principle P1 |
|---|---|---|
| A. Self-hosted runner | $0/月 + 初期設定 15 分 | 修正必要（本 ADR）|
| B. Unity Pro upgrade | $185/月 = $2,220/年 | 保てる |
| C. CI を諦め手動 build | owner 工数 | #83 自体を断念 |
| D. Unity 2022 LTS にダウングレード | backcast 全面再ポート | 守れるが現実的でない |
| E. Unity Build Server | Pro + 追加課金 | 守れるが Pro 同様 |

Owner judgment: solo-dev 取引アプリで月額 $2,220 は scope 不整合（Q5 codesign seam を CLAUDE.md YAGNI で削った精神と同じ）。

## Decision

CI shippable build の実行環境を **self-hosted Windows runner** に降格する。

- `runs-on: [self-hosted, Windows, X64]`（owner マシン上の GitHub Actions runner）
- Unity Editor / license は owner マシンの local installation を使う
- GameCI 関連 step（unity-builder / activation）を全削除し、直接 `Unity.exe -executeMethod` を呼ぶ
- Cache strategy を簡略化（Library / Editor / uv wheel は self-hosted の disk 自然持続で代替・`actions/cache` 不要）

## Consequences

### 失われたもの（honest assessment）

- **findings 0050 §Principle P1 が weaken**: build host が owner マシン自身になる。env 汚染が artifact に乗る可能性が増える
- **Single point of failure**: owner マシン offline = CI 停止。tag push を急いでいるとき機械を起こす必要がある
- **`GITHUB_TOKEN` が owner マシン上の runner process に flow**: secret material が個人マシンに常駐する security 摩擦
- **Public PR からの fork build 不可**: self-hosted は fork PR を default で受け付けない（solo-dev で fork PR 自体が無いので実害なし）

### 保たれているもの（本 ADR の合理化根拠）

- **Artifact reproducibility の本質的 defense は HITL gate**（draft Release → owner が *clean machine* で extract → Replay AC#3 完走 → Publish click）。build runner が owner 機でも HITL が「owner-machine の汚染が artifact に乗っていたら catch」する役を果たす
- **Bundled cpython+venv の hermetic 設計**（findings 0049 §2 / ADR-0002）が、build host の env 汚染が deploy artifact に漏れることを構造的に防ぐ:
  - `BackcastShippableBuild` は dev 機の `pyvenv.cfg home=` を読んで uv CPython root を verbatim copy する。owner 機固有の `home=` が deploy 用 `pyvenv.cfg` に残らないよう post-process で **DELETE** する（findings 0049 §2.5）
  - runtime-manifest.json schema check が Locator 経路で hard fail を起こす（owner 機の特殊 path が露出していれば即検知）
  - VC++ Redist DLL も bundled で hermetic
- **ADR-0001 d3 (executor orphan-absence) / ADR-0002 (runtime placement) は不変**: 本 ADR は build host 選択のみを修正し、artifact 内部設計は触らない

### Mitigations

- Self-hosted runner を **dedicated worker process** として登録（owner の interactive 作業と独立）
- Runner workspace は `_work/backcast/backcast` で project source とは別ディレクトリ・各 job ごとに自動 clean
- weekly cron が runner offline を検知（GitHub UI で runner status = "Offline" が継続したら通知）
- Findings 0050 §2.1.1 の weekly cron 3 役に **「self-hosted runner 死活監視」**を 4 役目として追加

## Self-protection clause

本 ADR の decision は固定。"GitHub-hosted runner に戻す" / "別 Path（B/C/D/E）に切り替える" 等の変更は **新規 ADR を要し、本 ADR への edit で書き戻すことを禁ずる**。

Unity 社が将来 Personal license の hosted-runner activation flow を復活させた場合、または GameCI が新 hosted Personal フロー（例: entitlement XML 対応）を実装した場合の方針も、新 ADR で declare すること。本 ADR は実装の暫定 workaround ではなく、Unity 社の制度変更への構造的応答である。

findings 0050 §Principle P1 は本 ADR で修正された旨を明記し、findings 内部の Change policy は維持する（Unity Pro upgrade / Build Server 等への将来 pivot も同様に ADR 必要）。
