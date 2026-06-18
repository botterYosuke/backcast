# findings 0050 — CI shippable build pipeline（GitHub Actions / GameCI / draft Release）

- Issue: #83
- 関連 ADR: [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（d3 executor orphan-absence・d4 Mono2x）/ [ADR-0002](../adr/0002-embedded-python-runtime-placement-and-resolution.md)（runtime 配置・本 CI が build leg を hosted で再現）
- 関連 findings: [0049](0049-shippable-standalone-bundled-venv.md)（本 CI が wrap する build script）
- 関連 issue: #33 parent（progress comment で follow-up 宣言済）/ #82 sibling（login subprocess hygiene・別 issue）
- 配置の根拠: ADR-0001 / ADR-0002 の自己保護条項。本 findings は #83 で確定する CI 側の下位事実の記録であり、ADR の方針を変更するものではない。
- grill-with-docs: 2026-06-18

---

## Principle（load-bearing — 変更には ADR が必要）

### P1. 成果物は owner-machine state から escape する **— [ADR-0014](../adr/0014-ci-shippable-build-runner-self-hosted.md) で修正**

> **2026-06-18 修正**: Unity 6 + Personal license + GitHub-hosted runner の組み合わせが Unity 社の license 制度変更により構造的に成立しないことが #83 初回 onboarding で判明（[ADR-0014](../adr/0014-ci-shippable-build-runner-self-hosted.md) §Context）。Runner choice を **self-hosted（owner マシン）**に降格した。Artifact reproducibility の defense は HITL gate + bundled cpython/venv hermetic 設計（findings 0049 §2）に移譲。詳細・trade-off・mitigation は ADR-0014 を参照。

旧 Principle（hosted runner で escape する）: 本 CI workflow は「shippable artifact が clean machine で再現可能」という thesis を成立させるために GitHub-hosted runner を選択する。これは ADR-0002（embedded runtime placement）の「verbatim copy で standalone が動く」要件の延長で、build host の env 汚染を artifact に乗せると検証不能な fail mode が生まれる。

この原則を覆す変更（self-hosted → hosted への復帰 / Unity Pro upgrade / Build Server pivot 等）は、本 findings の update では不十分であり、**新規 ADR の起案が必要**（ADR-0014 自己保護条項）。

### P2. SBOM scope = bundled Python venv のみ（狭めるには ADR が必要）

Unity 側 deps の真実源は `Packages/packages-lock.json`（commit 済・de facto SBOM）。CycloneDX SBOM は bundled Python venv 専用で、Unity 側には拡張しない。

非対称 change policy:
- **広げる**（Unity 側にも CycloneDX 等の独立 SBOM を出す）= 本 findings update で可
- **狭める**（Python SBOM を廃止する）= **ADR が必要**（supply-chain audit surface を失う影響）

## Change policy

| 変更 | 必要な手続き |
|---|---|
| Trigger / cache / publish 詳細の調整 | 本 findings の update |
| Workflow file の YAML 編集（既存 step 修正） | 本 findings の update |
| Runner choice（hosted ↔ self-hosted） | **ADR**（P1 ゆえ） |
| SBOM scope 縮小 | **ADR**（P2 ゆえ） |
| Cache layer の追加・削除 | 本 findings の update（10 GB cap への影響を実測ログ付きで） |
| Pre-publish smoke の 4 段構成変更 | 本 findings の update（Q8 fallback ladder を再評価） |
| Codesign step 追加 | 本 findings の update + 配布 roadmap 別 issue |

---

## 1. なぜ #83 を立ち上げたか（scope anchor）

#33 で `BackcastShippableBuild.BuildWindows64` と bundled venv layout が landed したが、当時は **手元 dev build → HITL → owner 視認** のループで初回 cutover ゲートを通す方針（findings 0049 §6 既述）だった。Owner-machine 汚染を artifact に乗せないため、build leg を hosted runner に移すのが #83 の目的。`BackcastShippableBuild` は #33 で完成しており、本 issue は **CLI executeMethod の wrap** のみ書く。

## 2. 確定設計

### 2.1 Trigger model（Q1）

4 triggers — それぞれ独立した役割:

```yaml
on:
  push:
    tags: ['v*']                # publish path: tag = ship intent
  workflow_dispatch:            # ad-hoc HITL artifact
  push:
    branches: [main]
    paths:
      - '.github/workflows/shippable-build.yml'
      - 'Assets/Editor/BackcastShippableBuild.cs'
      - 'Assets/Scripts/S1Spike/PythonRuntimeLocator.cs'
      - 'Assets/Scripts/Live/WorkspaceEngineHost.cs'   # CONTRACT log line owner
                                # bit-rot: workflow file 自体への変更を即検知
  schedule:
    - cron: '0 7 * * 1'         # Monday 07:00 UTC = JST 16:00 月曜
                                # 3 役: license expiry 早期警告 + bit-rot + stale-draft monitor
```

#### 2.1.1 Weekly cron の 3 役

1. **License expiry 早期警告**: GameCI Personal `.ulf` は ~1 年で expire。path-filter trigger は build script を触らない期間に license 切れを検知できないので、weekly run が catch する。
2. **Bit-rot detection**: GameCI image 更新 / action major bump / dependabot drift を週 1 回の dry-run で発見。tag publish 時の「20 分待った末にコケる」を防ぐ。
3. **Stale-draft monitor**: `gh api /repos/.../releases` で `draft && (now - created_at) > 7d` を query し、workflow summary に列挙。HITL 忘れによる draft 放置を検知。

cron job は Release publish step を **skip**（dry-run 専用・`if: github.event_name != 'schedule'` で gate）。

#### 2.1.2 Cron 間隔を縮める場合の policy

将来 hourly / daily に倒すと、同一 `concurrency.group` の cron 同士が cancel しあう。license expiry 早期警告には週 1 で十分なので、間隔を縮めるなら **`concurrency.group` を `${{ github.workflow }}-cron` 等に分離**して overlap policy を再考すること。本 findings をその時に update。

### 2.2 Runner choice（Q2 + ADR-0014 pivot）

| 項目 | 値 | 根拠 |
|---|---|---|
| `runs-on` | **`[self-hosted, Windows, X64]`** | ADR-0014: Unity 6 + Personal + GitHub-hosted は構造的不可 |
| Unity activation | **不要**（owner マシンの local Unity Hub activation を直接利用） | GameCI step を全削除 |
| Unity.exe invocation | 直接 `& "C:\Program Files\Unity\Hub\Editor\${UNITY_VERSION}\Editor\Unity.exe" -batchmode ... -executeMethod BackcastShippableBuild.BuildWindows64` | GameCI 介在を排除し失敗 surface を最小化 |

#### 2.2.1 旧 Q2 grill の "Self-hosted を採らない理由" — ADR-0014 で逆転

| 旧理由 | 現状（ADR-0014） |
|---|---|
| artifact thesis "clean machine で再現可能" を成立させるのは hosted のみ | Defense を HITL gate + bundled hermetic artifact に移譲（findings 0049 §2）|
| single point of failure（owner machine offline = CI 停止） | 受容。weekly cron が runner health を catch（§2.1.1 4 役目） |
| `GITHUB_TOKEN` を personal machine に持たせる security trade-off | 受容。runner は dedicated service として登録、scope を最小化 |
| Public PR fork build 不可 | solo-dev で fork PR 自体が無いので影響なし |

詳細トレードオフは [ADR-0014 §Consequences](../adr/0014-ci-shippable-build-runner-self-hosted.md)。

### 2.3 Cache strategy（Q3 + ADR-0014 pivot）— 自然持続に collapse

ADR-0014 で self-hosted runner に降格したため、cache strategy が大幅に簡略化された:

| Layer | hosted runner 時の扱い | self-hosted（ADR-0014 後） |
|---|---|---|
| **Unity Library** | `actions/cache` で 8GB cap 管理 | **自然持続** — runner workspace に常駐し各 run で incremental import |
| **Unity Editor** | `actions/cache` で 5GB cap 管理 | **不要** — local Unity Hub installation を直接利用 |
| **uv wheel cache** | `actions/cache` で `$LOCALAPPDATA\uv\cache` | **自然持続** — `%LOCALAPPDATA%\uv\cache` がマシン常駐 |
| **python/.venv** | 毎 run materialize | **自然持続** — `uv sync --frozen` が incremental（数秒）|

**残った guard rail**:

#### 2.3.1 Library size を fail-fast 計測（Guard rail B 継続）

```pwsh
- name: Measure Library size (cap=8GB raw)
  shell: pwsh
  run: |
    $size = (Get-ChildItem Library -Recurse -Force | Measure-Object Length -Sum).Sum / 1GB
    if ($size -gt 8) { Write-Error "Library bloat: $size GB"; exit 1 }
```

self-hosted でも owner マシンの disk を不必要に食わせない目的で維持。

#### 2.3.2 Workspace cleanup at job start

self-hosted は runner workspace が persistent なため、前回 run の stale state（`build/` `dist/` `smoke-extract/` `smoke-try*.log`）を毎 run 頭で削除する step を入れる。`Library/` と `python/.venv/` は **保持**（natural cache）。

#### 2.3.3 旧 hosted 時の guard rail（archived）

- **Guard rail A** (`ProjectSettings.asset` を cache key に含める): self-hosted で cache key 自体が無いので不要。**ただし Mono2x latent observation は依然有効**: `ProjectSettings.asset` の `scriptingBackend:` map は `Android: 1` のみで Standalone は明示エントリ無し → engine default Mono2x が適用される。ADR-0001 d4 の Mono2x 要件は「default に乗っている」状態で、`Standalone: 0` 明示 pin は scope 外。
- **Guard rail C** (Editor cache 切り捨て fallback): Editor cache 自体が無いので不要。
- **Guard rail D** (`Unity_lic.ulf` 非 cache): GameCI 自体不要なので moot。

### 2.4 Secrets and license activation（Q5 + ADR-0014 pivot）

> **ADR-0014 update**: Unity 関連 secret（`UNITY_EMAIL` / `UNITY_PASSWORD` / `UNITY_LICENSE`）は **全削除**。owner マシン local の Unity Hub activation を直接利用するため、GitHub side で license を持つ必要が無い。下記の旧 §2.4.1 〜 §2.4.2 は archived（onboarding 時の reference 用に残す）。

#### 2.4.1 Secret set（archived — ADR-0014 前）

| Name | Source | Rotation |
|---|---|---|
| `UNITY_EMAIL` | Unity ID account email = `sasaicco@gmail.com` | アカウント email 変更時 |
| `UNITY_PASSWORD` | Unity ID account password | password rotation 時 |
| `UNITY_LICENSE` | `.ulf` 全文（後述 dance で取得） | ~1 年（Personal expiry）|
| `GITHUB_TOKEN` | Auto-provided | per-run |

#### 2.4.2 One-time activation dance（手動 / 自動化不可）

Unity license server が UI を要求するため、自動化は不可能。**かつての `game-ci/unity-request-activation-file@v2` action は 2025 年に deprecation され、現在の GameCI（v4）は local Unity Hub / Unity.exe で `.alf` を生成して manual upload する flow が公式手順**（https://game.ci/docs/github/activation・2026-06-18 #83 初回 onboarding で deprecation を実機検出）。手順:

1. **`.alf` を local で生成**（Unity Editor がインストールされている dev 機で実行）:
   ```pwsh
   $unity = "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
   & $unity -batchmode -createManualActivationFile -logFile "$env:USERPROFILE\Downloads\unity-activation.log" -quit
   ```
   - `$env:USERPROFILE\Downloads\Unity_v6000.4.11f1.alf` （~1 KB）が生成される
   - `-quit` で activation 行為自体は無効化されるが `.alf` 生成は完了する
   - log の末尾に `Manual activation license file successfully saved.` が出れば PASS
2. Owner が `.alf` を https://license.unity3d.com/manual に手動 upload（Unity ID でログイン）
3. 質問が 2 つ出るので回答（"primary use" → Personal / "best describes your focus" → Other 等）
4. `Unity_v6000.4.11f1.ulf` がブラウザに download される
5. `.ulf` の中身（`<?xml ... </root><DSASignature>...</DSASignature>` 全体）を **そのまま** GitHub repo secret `UNITY_LICENSE` に貼り付け（base64 encode 不要・GameCI v4 は raw `.ulf` を期待）
6. 翌年の renewal でも **本手順そのまま再実行**（自動化できる helper workflow は 2026-06 時点で GameCI が提供していない）

### 2.5 Release asset bundle（Q6）

Tag publish 時の Release に attach する assets:

| Asset | 説明 | 生成 step |
|---|---|---|
| `backcast-windows64-${tag}.zip` | shippable tree。内部 layout は単一トップフォルダ `backcast-windows64-${tag}/{backcast.exe, backcast_Data/, UnityPlayer.dll, ...}`（tarbomb 回避） | `Compress-Archive` post-build |
| `SHA256SUMS.txt` | 全 asset の SHA256（download 後の検証用）| `Get-FileHash` |
| `runtime-manifest.json` | zip 内のものを top-level にも copy（download 不要で cpython_version / built_at が見える） | `Copy-Item` |
| `sbom-python-${tag}.cdx.json` | bundled Python venv の CycloneDX SBOM（Principle P2 適用） | `uvx cyclonedx-py environment` |
| Build provenance attestation | `actions/attest-build-provenance@v2`（Sigstore signed）| `gh attestation verify` で download 後検証 |

#### 2.5.1 Release body の 2 段構成

```yaml
- uses: softprops/action-gh-release@v2
  with:
    draft: true
    prerelease: ${{ startsWith(github.ref_name, 'v') && (contains(github.ref_name, '-rc.') || contains(github.ref_name, '-beta.') || contains(github.ref_name, '-alpha.')) }}
    generate_release_notes: true     # GitHub auto changelog （冒頭）
    body_path: release-body-verify.md # verify procedure （末尾に append）
    append_body: true
    files: |
      dist/backcast-windows64-${{ github.ref_name }}.zip
      dist/SHA256SUMS.txt
      dist/runtime-manifest.json
      dist/sbom-python-${{ github.ref_name }}.cdx.json
```

`release-body-verify.md` の content（workflow 内で生成）— 4-backtick outer fence so the nested triple-backtick inside renders correctly on GitHub:

````markdown
## Verification (run before extracting)

```pwsh
gh release download ${{ github.ref_name }} -p '*.zip' -p 'SHA256SUMS.txt'
Get-FileHash backcast-windows64-${{ github.ref_name }}.zip -Algorithm SHA256
# compare to SHA256SUMS.txt
gh attestation verify backcast-windows64-${{ github.ref_name }}.zip -R ${{ github.repository }}
```
````

attestation は自動生成しても owner が `gh attestation verify` を呼ばないと security theater。Release body に焼き付けることで owner muscle memory に乗せる。

#### 2.5.2 Permissions block

```yaml
permissions:
  contents: write       # Release publish
  id-token: write       # attest-build-provenance Sigstore
  attestations: write   # attest-build-provenance
```

### 2.6 Tag → Release flow（Q7）— Draft-then-manual-Publish

| 項目 | 値 |
|---|---|
| `draft` | `true`（HITL 完了まで） |
| `prerelease` 判定 | `startsWith('v') && (-rc. \| -beta. \| -alpha.)` の三項マッチ |
| Owner action | draft の asset を download → clean machine で extract → Replay AC#3 完走確認 → Publish click |
| Auto-publish への切替 | HITL 安定後、`draft: false` に flip するだけ |
| Prerelease suffix policy | `{rc, beta, alpha}.N`（例: `v1.2.3-rc.1`） |

`v1.2.3-final` のような nonstandard suffix は production release 扱い（=`prerelease: false`）。policy は本 findings で確定済。

### 2.7 Pre-publish verification（Q8）— 4-stage gate

```
build → zip → [Stage1: zip integrity] → [Stage2: manifest schema]
              → [Stage3: bundled-venv import] → [Stage4: Player GUI smoke]
              → draft Release publish
```

全 stage は **zip-extracted clean tree** に対して走らせる（build output ではなく）。zip 化時の symlink/permission/path-length 落ちを catch。

#### 2.7.1 Stage 1: Zip integrity

```pwsh
Expand-Archive dist/backcast-windows64-${tag}.zip -DestinationPath smoke-extract
# 期待 file が全て存在するか確認
```

#### 2.7.2 Stage 2: Manifest schema

```pwsh
$m = Get-Content smoke-extract/backcast-windows64-${tag}/backcast_Data/StreamingAssets/PythonRuntime/runtime-manifest.json | ConvertFrom-Json
if ($m.schema -ne 1) { throw "schema drift" }
if ($m.target -ne 'StandaloneWindows64') { throw "target drift" }
# Locator が読む paths が clean tree 上に実在するか確認
```

#### 2.7.3 Stage 3: Bundled-venv import smoke

```pwsh
$cpython = "smoke-extract/backcast-windows64-${tag}/backcast_Data/StreamingAssets/PythonRuntime/cpython/python.exe"
& $cpython -c "import duckdb, marimo, pyarrow, sklearn, pandas, numpy, joblib, orjson, httpx, websockets, pydantic; print('venv-import OK')"
```

DLL load 失敗 / 欠損 site-package / `compileall` 不整合をここで catch。

#### 2.7.4 Stage 4: Player GUI smoke — preflight 学習を反映

**Q8 grill 時に `-batchmode -nographics` を想定していたが、preflight で挫折**（§4 参照）。GUI mode で起動し、log poll で contract regex 検出後に `Stop-Process` で強制 kill する pattern を採用:

```pwsh
- name: Player GUI smoke (retry once)
  shell: pwsh
  run: |
    function Invoke-Smoke {
      param([string]$Tag)
      $exe = "smoke-extract/backcast-windows64-${{ github.ref_name }}/backcast.exe"
      $log = "smoke-$Tag.log"
      Remove-Item $log -ErrorAction SilentlyContinue
      $proc = Start-Process $exe -ArgumentList "-logFile",$log -PassThru
      $contract = '\[WorkspaceEngineHost\] live-configured server built; main GIL-free; lanes polling\.'
      $deadline = (Get-Date).AddSeconds(60)
      $hit = $false
      while ((Get-Date) -lt $deadline) {
        if (Test-Path $log) {
          if ((Get-Content $log -Raw -ErrorAction SilentlyContinue) -match $contract) {
            $hit = $true; break
          }
        }
        Start-Sleep -Milliseconds 500
      }
      try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {}
      Wait-Process -Id $proc.Id -Timeout 10 -ErrorAction SilentlyContinue
      if ($hit) { Write-Host "smoke OK ($Tag)" }
      else { Write-Warning "smoke fail ($Tag)"; Get-Content $log -ErrorAction SilentlyContinue }
      return $hit
    }
    if (Invoke-Smoke "try1") { exit 0 }
    if (Invoke-Smoke "try2") { exit 0 }
    Write-Error "Player GUI smoke failed twice"
    exit 1
```

##### Contract（重要）

`WorkspaceEngineHost.cs:189` の log 行は **CI smoke の literal contract**:

```cs
Debug.Log("[WorkspaceEngineHost] live-configured server built; main GIL-free; lanes polling.");
```

`Assets/Scripts/Live/WorkspaceEngineHost.cs` に `// CONTRACT (findings 0050 / #83): ...` コメントが入っている（#83 で実装）。この行を refactor したい人は同 PR で `shippable-build.yml` の smoke regex も update すること。

##### Escalation policy（hosted runner で fail した時）

- 1 段目: GUI mode が hosted runner の virtual display で D3D11 init 失敗 → retry-once で catch できないので Option D へ降格
- **Option D**: `WorkspaceOwnership.ShouldClaim` に CLI flag を追加し、`-ttwr-smoke-init` を渡したら batchmode でも Python init を許可する（YAGNI 違反だが、production code 5 行で済む）
- **escalation 条件**: hosted runner 3 連続で `D3D11 init failed` log を吐いたら（findings の運用 log に記録）、Option D PR を起案

### 2.8 Workflow file naming（Q9）

| 場所 | 名前 |
|---|---|
| Main workflow | `.github/workflows/shippable-build.yml` |

(License activation 用 helper workflow は GameCI が `unity-request-activation-file@v2` action を 2025 年に deprecation したため当初予定の `request-activation.yml` を repo に残せない。代わりに dev 機の `Unity.exe -createManualActivationFile` で `.alf` 生成 → 手動 upload → `.ulf` 取得の flow に倒した・§2.4.2 参照)

3-way naming alignment（同一語彙で grep ヒット）:
- `Assets/Editor/BackcastShippableBuild.cs` (C# class)
- `Tools/Backcast/Build Shippable (Windows64|macOS)` (MenuItem)
- `.github/workflows/shippable-build.yml` (workflow file)

macOS leg の verification gate 昇格時は matrix expansion で同じ file に追加（YAGNI: 今は Windows 専用 job のみ）。

### 2.9 Concurrency policy（Q10）

```yaml
concurrency:
  group: ${{ github.workflow }}-${{ github.ref }}
  cancel-in-progress: true
```

- Tag re-push → 新 run が旧を cancel（旧の publish は idempotent overwrite で巻き取られる）
- Dispatch 連続 → 新 run が旧を cancel
- Push main 連続 → 新 run が旧を cancel
- Schedule × Dispatch → dispatch が cron を cancel（cron は週後にリトライで十分）

#### 2.9.1 Job-level timeout

```yaml
jobs:
  build:
    timeout-minutes: 60   # expected ~20-25 min・倍以上で異常検知
```

GitHub Actions default は 360 分。Unity asset import stuck の 6 時間 burn を防ぐ。

#### 2.9.2 Cancellation 耐性（half-publish 事故が起きない理由）

- Cache write: API 上 atomic、partial は landing しない
- Release publish (`softprops/action-gh-release@v2`): 同一 tag は idempotent overwrite
- Attestation: 複数 attestation でも `gh attestation verify` はどれかが通れば PASS

## 3. 却下した代案（grill）

### 3.1 Self-hosted runner — Principle P1 違反

owner-machine 汚染を escape する thesis に反する（§2.2.1）。

### 3.2 `runs-on: windows-latest` — moving target

Microsoft が image を随時 bump。reproducibility thesis を image にも適用するため `windows-2022` pin。

### 3.3 5 層 cache（venv + uv-CPython も cache）

合計 ~14 GB で 10 GB cap thrash。venv (`uv sync`) と uv-CPython download は cold ~30 秒-2 分で cheap。cache slot を payback しないので 3 層に絞る。

### 3.4 Codesign seam を day-one から gated で残す — CLAUDE.md YAGNI 違反

Q5 grill で「gated no-op seam」を一度推奨したが、CLAUDE.md の "Don't design for hypothetical future requirements" / "Don't use feature flags ... when you can just change the code" に明確に反する。Codesign cert 取得は **roadmap に無い speculative** なので seam を day-one から残す根拠が無い。配布 roadmap が固まったら **別 issue + 1 PR** で workflow を追加し、findings 0050 update か 0051 を新規に切る。seam を残すと dead-code 腐敗（`actions/sign-windows@vN` の major bump を catch できず silent breakage）の risk もある。

### 3.5 Inno Setup installer

installer authoring + license step + SmartScreen を消すなら codesign cert も併発で必要。本 issue scope を肥大化させるので別 issue に切る。

### 3.6 ADR-0014 を本 issue で mint（findings + ADR の二重持ち）

「retroactive ADR」は ADR の文書 nature（"こう変える"を declare）と逆向き。将来 self-hosted への変更 PR が ADR-0014 を mint するのが正しい順序。本 issue では findings 0050 が Principle 節 + Change policy 節で ADR-equivalent 拘束力を carry する（Q11 grill 結論）。

### 3.7 `-batchmode -nographics` での Player smoke — preflight で structural fail を実証

下記 §4 参照。grill 時に proposed したが preflight で `WorkspaceOwnership.ShouldClaim` の不変条件で Python init skip と判明。GUI mode + Stop-Process pattern に pivot した。

## 4. Player smoke の preflight 学習（owner 検証）

### 4.1 検証 3 ケース（2026-06-18）

| ケース | コマンド | 結果 |
|---|---|---|
| Q8 plan 通り | `backcast.exe -batchmode -nographics -logFile <p>` | ❌ log 25 行で stall、`[BackcastWorkspaceRoot] not Python owner (ownPlay=True, batch=True, alreadyInit=False).` で Python init skip |
| `-nographics` 単独 | `backcast.exe -nographics -logFile <p>` | ❌ 120s 実行継続するも log 一切生成されず（compatibility 不明・inconclusive） |
| GUI mode | `backcast.exe`（no flags、`windows64-player3.log:35` の実測）| ✅ 35 行目に contract line |

### 4.2 Root cause: project-wide invariant

```cs
// WorkspaceEngineHost.cs:604 (WorkspaceOwnership.ShouldClaim)
=> ownPlay && !isBatchMode && (!pythonAlreadyInitialized || weAlreadyOwn);

// BackcastWorkspaceRoot.cs:18 (header comment)
// isBatchMode suppresses Python init (the headless compile gate never inits Python or renders).
```

同 pattern が `VizSpikeHarness` / `ThemeHitlHarness` / `ScenarioStartupHitlHarness` の 3 か所にも存在。**「`isBatchMode == true` のとき Python init を skip」は project 全体の意図的不変条件**で、headless compile gate（Unity batchmode build を「コンパイル通るか」検証用に使う系）を Python init から守る目的で設計されている。

### 4.3 解（Option A 採用）

GUI mode で起動し、log poll → contract regex 検出 → `Stop-Process` で kill する pattern を §2.7.4 に確定。production code は触らない（YAGNI 遵守）。

### 4.4 Escalation: hosted runner で fail したら Option D へ

§2.7.4 の escalation policy 参照。

## 5. ADR 関係

- **ADR-0001 は不変**。本 CI は executor in-proc parity を build leg literal-text で実証する probe（findings 0049 §2.6）を **self-hosted runner で再現可能化**する。d3/d4 の文言は変えない。
- **ADR-0002 は不変**。本 CI は ADR-0002 の「verbatim copy で standalone が動く」要件を、build host が owner マシンであっても artifact 自体は hermetic で satisfy する（ADR-0014 §Consequences "保たれているもの"）。配置基準は触らない。
- **ADR-0014 は本 findings の Principle P1 を修正**。Runner choice の変更（self-hosted ↔ hosted / Pro upgrade 等）は ADR-0014 自己保護条項により新規 ADR が必要。
- **CONTEXT.md は不変**。CI workflow は domain glossary を持たないので CONTEXT.md には記載しない。

## 6. 初回 onboarding setup checklist（ADR-0014 後）

新マシン / new contributor で再走する手順:

### Step 1: owner マシン上で Unity Hub + Personal license を activate

- Unity Hub をインストール（既存ならスキップ）
- Unity ID `sasaco@live.jp` でログイン
- Preferences → Licenses → Add → "Get a free personal license"
- Unity 6000.4.11f1 Editor をインストール（Unity Hub から）

### Step 2: GitHub Actions self-hosted runner を owner マシンに登録

1. GitHub repo の `Settings → Actions → Runners` を開く
2. `New self-hosted runner` → Windows / X64 を選択
3. 表示される PowerShell コマンド一式を owner マシンの **専用ディレクトリ**（推奨: `C:\actions-runner\`）で順に実行:
   ```pwsh
   mkdir C:\actions-runner; cd C:\actions-runner
   Invoke-WebRequest -Uri <生成された URL> -OutFile actions-runner-win-x64-*.zip
   Add-Type -AssemblyName System.IO.Compression.FileSystem
   [System.IO.Compression.ZipFile]::ExtractToDirectory("$PWD\actions-runner-win-x64-*.zip", "$PWD")
   ./config.cmd --url https://github.com/botterYosuke/backcast --token <生成された token>
   ```
4. labels を `Windows,X64` に設定（default で OK）
5. service として登録（再起動後も自動起動）:
   ```pwsh
   ./svc.sh install   # or ./svc.cmd install on Windows
   ./svc.sh start
   ```
6. GitHub UI の Runners タブで status = "Idle" になったことを確認

### Step 3: 初回 dispatch smoke

```pwsh
gh workflow run shippable-build.yml
```

workflow run が GREEN になることを確認（cache 初回は cold で ~25-30 分）。

### Step 4: 初回 tag push

```pwsh
git tag v0.1.0
git push origin v0.1.0
```

draft Release が作成される → HITL Replay 完走確認 → "Publish release" click。

## 7. 監視・運用 notes

### 7.1 Cache size の実測（初回数 run）

```pwsh
gh cache list -R botterYosuke/backcast --sort size_in_bytes --order desc
```

最初の 3-5 run で実測 size を記録し、12 GB 接近していれば Editor cache を切る判断（§2.3.3 Guard rail C）。

### 7.2 License expiry の早期警告

weekly cron の log に `GameCI: license expires in N days` が出る（GameCI が API でチェック）。N < 30 で workflow summary に warning を立てる予定（後続 PR で実装）。

### 7.3 Stale draft monitor

weekly cron job:

```pwsh
gh api /repos/${{ github.repository }}/releases --jq '.[] | select(.draft and ((now - (.created_at | fromdateiso8601)) > 604800)) | .name'
```

7 日以上 publish されていない draft を workflow summary に列挙。

### 7.4 Cron 間隔を縮めるなら overlap policy 再考

§2.1.2 参照。

## 8. 未消化・射程外

- **Mac standalone leg の verification gate 昇格**: code path は両 OS 用に書かれているが（findings 0049 §6）、CI verification は Windows のみ。Mac 昇格は matrix expansion + Mac runner secret 追加（別 issue）。
- **Code-signing cert + signtool**: 配布 roadmap が固まったら別 issue（§3.4）。
- **Standalone scripting backend の明示 pin**（`Standalone: 0` を `ProjectSettings.asset` に追記）: §2.3.1 latent observation。本 CI scope 外。
- **License expiry 自動警告 PR**: §7.2 の N < 30 warning。後続。

---

## Appendix A: 4 trigger event-name map

| Trigger | `github.event_name` | `github.ref_name` 例 | Release publish? |
|---|---|---|---|
| Tag push | `push` | `v1.2.3` | YES (draft) |
| Dispatch | `workflow_dispatch` | `main`（typically）| YES (draft, with synthetic version) |
| Push main paths | `push` | `main` | NO（bit-rot only）|
| Schedule | `schedule` | `main` | NO（dry-run + stale-draft monitor）|

Conditional gating: Release publish step は `if: github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v') || github.event_name == 'workflow_dispatch'`。
