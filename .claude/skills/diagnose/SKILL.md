---
name: diagnose
description: Disciplined diagnosis loop for hard bugs, hangs, and performance regressions. Reproduce → minimise → hypothesise → instrument → fix → regression-test. Use when user says "diagnose this" / "debug this", reports a bug, says something is broken/throwing/failing, or describes a performance regression. ALSO trigger on hang/freeze/deadlock symptoms: "フリーズする", "固まる", "応答しない", "ハングする", "デッドロック", "hangs", "freezes", "deadlock", "stuck", "unresponsive" — these are classic root-cause hunts (thread/lock/event-loop blocking) that need the reproduce→hypothesise→instrument loop just as much as crashes do. Especially platform-specific freezes ("macOS だけ固まる" 等) where a minimal repro / differential harness pins the cause. **ALSO trigger when user opens an issue with `/plan` or `/bevy-engine /plan` that describes a UI-level stuck/永久ループ bug**: the plan phase should not skip the reproduce step — the diagnose loop and plan go hand-in-hand. Symptoms: "Loading..." が消えない, "永久ループ", "[+ Add] が反応しない", "grpc:OK なのに UI が更新されない" など UI 操作の結果が帰ってこない系のバグはすべて本スキルの対象。**ALSO trigger on 空リスト / データが出ない系**（"反応しない" とは別カテゴリ＝操作は通るが結果が空）: "一覧が出てこない", "銘柄一覧が出てこない", "候補が出ない", "リストが空", "空のまま", "データが出ない", "ピッカーが空", "0 件", "何も表示されない" — これらは「経験的に production が実際に何を渡しているか」を計装して切り分けるのが要。**最重要の落とし穴＝config-vs-code triage を最初の仮説で打ち切らない**: 空リストには複数の原因候補が重なる（env/mount 未設定・データのカバレッジ不足・**配線バグで stale/ハードコード値を渡している**）。最初に見えた config/env 仮説（"ボリューム未マウント" 等）は transient な偽の手掛かりであることが多く、**production コードが実際に渡している引数を実データで empirical に確認**（例: backend RPC を実 DB に対し複数入力で直叩きし、返ってくる件数を観測）してからでないと真因を取り違える。実例（sidebar +Add 2026-06-22）: "銘柄一覧が出てこない" の初手仮説は "DuckDB 未マウント" だったが、マウント後も空 → 実データで `list_instruments("local", end)` を複数 end_date で直叩きし「実 scenario.end(今日)=4424 件 / picker のハードコード 2024-12-31=0 件」を観測 → 真因は C# 配線（root が view へ mode+scenario.end を push せず Bind 既定値固定）と確定。`behavior-to-e2e` と併発で RED→GREEN gate（Python supply + C# AFK）まで縫った。**ALSO trigger on UI 描画グリッチ / ちらつき系**: "チラつく", "チカチカ", "flicker", "flickering", "1-frame pop", "geometry pop", "spawn 直後だけ崩れる/潰れる", "1 フレームだけ 0×0", "アイドルで再描画される" — これらは「どのフレームで何が変わって glitch るか」を **正しい粒度の RED で再現**してから直すのが要で、reproduce→instrument→regression-test の loop がそのまま効く（実例: #72/#75/#99/#156 の Strategy Editor flicker。#156 は spawn system 単体を回す headless RED で 0×0 clip pop を固定した）。原因が「prime suspect（未確証の仮説）」として issue 化されている場合は特に、fix の前に RED で仮説を確証/反証すること。**ALSO trigger on 描画品質 / ぼやけ / 解像度系**: "ぼやける", "blurry", "不鮮明", "シャープでない", "テキストが読みにくい", "UI がぼんやりする", "DPI ぼやけ", "高 DPI で崩れる", "Intel GPU 描画", "MSAA ぼやけ", "Vulkan 描画品質" — GPU ドライバ・MSAA resolve・DPI スケール不一致など複数の可能性を絞り込む必要があり、仮説生成 → 計装（ログ・config 変更） → RED テストで根本原因を固定する loop が有効（実例: #176 Intel HD + Vulkan 旧ドライバで Msaa::Sample4 の resolve がぼやけを起こし、Msaa::Off で解消）。**ALSO trigger on 文字化け / 豆腐 / グリフ欠落系**: "文字化け", "文字化けする", "豆腐になる", "□ になる", "日本語が出ない/化ける", "グリフが出ない", "CJK が表示されない", "mojibake" — 表示が壊れて見えるが、多くは **encoding 崩れではなくフォントの glyph 欠落（TMP fallback 未配線）** が真因。まずデータ経路（C#↔Python=UTF-16・保存/JSON/console=UTF-8）が健全かを empirical に確認し、次にフォント資産の CJK カバレッジ/fallback を切り分ける（実例 #16: Strategy Editor は CascadiaMono SDF 一択で CJK グリフ無し→全日本語が□。M+ を fallback 連結し `JapaneseFontFallbackE2ERunner` で RED→GREEN・2026-06-27。memory: unity-mojibake-is-missing-cjk-glyph）。**ALSO trigger on 外部 venue API / 外部サーバが拒否系のエラーを返したとき**: "p_errno=N", "sResultCode=N", "ST frame p_errno=2", "HTTP 4xx/5xx", "サーバが拒否", "session inactive", "仮想URL無効", "WS が即切断される", "EC push が来ない", "FILLED 通知が届かない" — venue 由来の失敗もコードロジック由来と同様に reproduce / minimise / instrument の loop が効く。**特に Unity batchmode / 実 demo session のような high-overhead な E2E を hypothesise → fix のループ単位に使うと 1 試行あたり 30s+ startup + demo rate-limit (`p_errno=-3` システム混雑) 消費で無駄が大きい**。まず **standalone Python probe**（adapter のみ使う最小 WS / REQUEST スクリプト）を書いて分単位で hypothesise を回し、確証してから E2E で最終確認する。実例: #85 follow-up で `ST p_errno=2` を 3 つの URL 仮説で Unity batchmode フル実走 × 3 回 → 各 ~75s + demo rate-limit `p_errno=-3` に到達。最初に立花 `samples/e_api_websocket_receive_tel.py` ベースの standalone probe を書いていれば数十秒で各仮説を反証できた（2026-06-19）。**`/grill-with-docs` が明示指定された bug 修正でも diagnose は併発する**（grill = 設計階層、diagnose = 経験的再現 + 計装 — 直交する）。**ALSO trigger on ネイティブクラッシュ / segfault を verify・実機 HITL 中に再現したとき**: "落ちた", "クラッシュ", "crash", "SIGSEGV", "segfault", "EXC_CRASH", "Editor.log にスタック", ".ips", "PyUnicode/_PyObject_Malloc", "pythonnet/Mono の GIL" — `verify`/HITL ガイド中に出た native crash は手なりで fix せず本スキルへ切り替える: Editor.log（`~/Library/Logs/Unity/Editor.log`）と `.ips`（`~/Library/Logs/DiagnosticReports/`）でスタックを取り、reproduce→同一フレーム確認→根本原因（埋め込み Python の GIL 規律違反等）→fix→regression-test（probe/RED で AFK gap を塞ぐ）の loop を回す（実例: #23 で Cancel last の SIGSEGV を clean state で 2 回・同一フレーム再現し `LiveRpcLanes.SubmitCancelOrder` の GIL 取得前 `new PyString` を特定→修正 2026-06）。**ALSO trigger when a CLOSED / 出荷済み feature is reported broken**: "機能していない", "issue #N が機能していない", "動かない", "効いていない", "出荷したのに〜しない", "前は動いていたのに" — owner が「鵜呑みにせず実機の目視テストと同じテストを実行して現象を確認して」と言うのが典型。手なりで仮説修正せず、まず **目視と同経路の既存 AFK probe / pytest を実機で実走して RED 再現**する（probe が real prod トリガを駆動する設計なら self-trigger より強い・[[e2e-self-triggers-masks-missing-prod-wiring]]）。出荷後に「呼ぶ人（配線）」だけ消える回帰は、自分の実装バグより先に **後発の無関係 PR が共有ファイル編集時に配線を巻き込み削除**した疑いを置き、`git log --oneline -S "<symbol>" -- <file>` で「追加 commit→削除 commit」を裏取りする。復旧は blind revert でなく削除分を現行ツリー（後発変更後）へ再適用する。実例 #129（2026-06-25）: spawn-preview を `12bcd1b` で着地（Python RPC + C# トリガ + AFK probe + pytest 9 passed）したのに、後続 #132「Miro 風テーマ」`79b3978` が `BackcastWorkspaceRoot.cs` 編集時に C# トリガ配線（`RequestChartPreviewsForAllLiveCharts` + `_scenario.Committed`）を丸ごと削除→Python 半分は無傷で RPC 永遠未発火→chart 空。AFK probe `ChartSpawnPreviewE2ERunner` を Unity batchmode で実走し `CHARTPREVIEW-01 RED` 再現→3 seam 復元→`exit=0` GREEN。**この系統は `/grill-with-docs` が明示指定されていても diagnose を必ず併発する**（grill=設計階層 / diagnose=経験的再現）。
---

# Diagnose

A discipline for hard bugs. Skip phases only when explicitly justified.

When exploring the codebase, use the project's domain glossary to get a clear mental model of the relevant modules, and check ADRs in the area you're touching.

## Phase 1 — Build a feedback loop

**This is the skill.** Everything else is mechanical. If you have a fast, deterministic, agent-runnable pass/fail signal for the bug, you will find the cause — bisection, hypothesis-testing, and instrumentation all just consume that signal. If you don't have one, no amount of staring at code will save you.

Spend disproportionate effort here. **Be aggressive. Be creative. Refuse to give up.**

### Ways to construct one — try them in roughly this order

1. **Failing test** at whatever seam reaches the bug — unit, integration, e2e.
2. **Curl / HTTP script** against a running dev server.
3. **CLI invocation** with a fixture input, diffing stdout against a known-good snapshot.
4. **Headless browser script** (Playwright / Puppeteer) — drives the UI, asserts on DOM/console/network.
5. **Replay a captured trace.** Save a real network request / payload / event log to disk; replay it through the code path in isolation.
6. **Throwaway harness.** Spin up a minimal subset of the system (one service, mocked deps) that exercises the bug code path with a single function call.
7. **Property / fuzz loop.** If the bug is "sometimes wrong output", run 1000 random inputs and look for the failure mode.
8. **Bisection harness.** If the bug appeared between two known states (commit, dataset, version), automate "boot at state X, check, repeat" so you can `git bisect run` it.
9. **Differential loop.** Run the same input through old-version vs new-version (or two configs) and diff outputs.
10. **HITL bash script.** Last resort. If a human must click, drive _them_ with `scripts/hitl-loop.template.sh` so the loop is still structured. Captured output feeds back to you.

Build the right feedback loop, and the bug is 90% fixed.

### Iterate on the loop itself

Treat the loop as a product. Once you have _a_ loop, ask:

- Can I make it faster? (Cache setup, skip unrelated init, narrow the test scope.)
- Can I make the signal sharper? (Assert on the specific symptom, not "didn't crash".)
- Can I make it more deterministic? (Pin time, seed RNG, isolate filesystem, freeze network.)

A 30-second flaky loop is barely better than no loop. A 2-second deterministic loop is a debugging superpower.

### Non-deterministic bugs

The goal is not a clean repro but a **higher reproduction rate**. Loop the trigger 100×, parallelise, add stress, narrow timing windows, inject sleeps. A 50%-flake bug is debuggable; 1% is not — keep raising the rate until it's debuggable.

### When you genuinely cannot build a loop

Stop and say so explicitly. List what you tried. Ask the user for: (a) access to whatever environment reproduces it, (b) a captured artifact (HAR file, log dump, core dump, screen recording with timestamps), or (c) permission to add temporary production instrumentation. Do **not** proceed to hypothesise without a loop.

Do not proceed to Phase 2 until you have a loop you believe in.

## Phase 2 — Reproduce

Run the loop. Watch the bug appear.

Confirm:

- [ ] The loop produces the failure mode the **user** described — not a different failure that happens to be nearby. Wrong bug = wrong fix.
- [ ] The failure is reproducible across multiple runs (or, for non-deterministic bugs, reproducible at a high enough rate to debug against).
- [ ] You have captured the exact symptom (error message, wrong output, slow timing) so later phases can verify the fix actually addresses it.

Do not proceed until you reproduce the bug.

## Phase 3 — Hypothesise

Generate **3–5 ranked hypotheses** before testing any of them. Single-hypothesis generation anchors on the first plausible idea.

Each hypothesis must be **falsifiable**: state the prediction it makes.

> Format: "If <X> is the cause, then <changing Y> will make the bug disappear / <changing Z> will make it worse."

If you cannot state the prediction, the hypothesis is a vibe — discard or sharpen it.

**Show the ranked list to the user before testing.** They often have domain knowledge that re-ranks instantly ("we just deployed a change to #3"), or know hypotheses they've already ruled out. Cheap checkpoint, big time saver. Don't block on it — proceed with your ranking if the user is AFK.

## Phase 4 — Instrument

Each probe must map to a specific prediction from Phase 3. **Change one variable at a time.**

Tool preference:

1. **Debugger / REPL inspection** if the env supports it. One breakpoint beats ten logs.
2. **Targeted logs** at the boundaries that distinguish hypotheses.
3. Never "log everything and grep".

**Tag every debug log** with a unique prefix, e.g. `[DEBUG-a4f2]`. Cleanup at the end becomes a single grep. Untagged logs survive; tagged logs die.

**Perf branch.** For performance regressions, logs are usually wrong. Instead: establish a baseline measurement (timing harness, `performance.now()`, profiler, query plan), then bisect. Measure first, fix second.

## Phase 5 — Fix + regression test

Write the regression test **before the fix** — but only if there is a **correct seam** for it.

A correct seam is one where the test exercises the **real bug pattern** as it occurs at the call site. If the only available seam is too shallow (single-caller test when the bug needs multiple callers, unit test that can't replicate the chain that triggered the bug), a regression test there gives false confidence.

**If no correct seam exists, that itself is the finding.** Note it. The codebase architecture is preventing the bug from being locked down. Flag this for the next phase.

If a correct seam exists:

1. Turn the minimised repro into a failing test at that seam.
2. Watch it fail.
3. Apply the fix.
4. Watch it pass.
5. Re-run the Phase 1 feedback loop against the original (un-minimised) scenario.

## Phase 6 — Cleanup + post-mortem

Required before declaring done:

- [ ] Original repro no longer reproduces (re-run the Phase 1 loop)
- [ ] Regression test passes (or absence of seam is documented)
- [ ] All `[DEBUG-...]` instrumentation removed (`grep` the prefix)
- [ ] Throwaway prototypes deleted (or moved to a clearly-marked debug location)
- [ ] The hypothesis that turned out correct is stated in the commit / PR message — so the next debugger learns

**Then ask: what would have prevented this bug?** If the answer involves architectural change (no good test seam, tangled callers, hidden coupling) hand off to the `/improve-codebase-architecture` skill with the specifics. Make the recommendation **after** the fix is in, not before — you have more information now than when you started.
