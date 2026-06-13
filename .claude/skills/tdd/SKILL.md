---
name: tdd
description: Test-driven development with red-green-refactor loop. Use when user wants to build features or fix bugs using TDD, mentions "red-green-refactor", wants integration tests, or asks for test-first development. ALSO use when a task spec / issue / instruction mandates TDD or "write a failing regression test first" (e.g. "TDD 厳守", "RED→GREEN", "回帰テストを先に書いて失敗を確認してから実装", "must write the regression test from RED") — the trigger is the instruction requiring test-first, not only the user phrasing it live. Especially for safety-critical bug fixes where a RED test must prove the bug before the fix. ALSO use when an issue says "these existing tests currently assert the bug as intended behavior and must be **inverted** RED→GREEN" — invert the assertion first (make it RED), confirm failure, then fix the implementation (GREEN). This pattern is common in this repo's issue workflow: "test_xxx_rejected → 成功ケースに反転", "テストを反転", "既存テストを成功ケースに変更". ALSO covers the stale-test sub-case: a test left behind by a *prior* shipped feature is **already RED** (failing) because production already moved to the new behavior — here inverting the test to the current spec IS the whole fix, with NO production-code change (e.g. "#30 でホームモード化したのに test が旧 precondition を assert したまま RED"). Confirm RED, invert to current behavior, confirm GREEN, run the suite for regressions. Fire even when the fix looks like a trivial one-line assertion flip, and fire when an issue body explicitly names the `tdd` skill / its RED→GREEN inversion pattern. **ALSO fire when implementing "Slice N" of an issue (e.g. "Slice 4 に着手", "Slice 3b を実装", "次のスライスを実装"): issue-driven vertical slice implementations always require RED-first test coverage before the implementation code — write the Python pytest or Rust #[test] RED test first, confirm the assertion fails (not compile error), then implement until GREEN. This applies to both Rust ECS seam tests and Python pytest (server_grpc / account_sync / adapter level).** **ALSO fire when an issue or task says "verify first"「まず再現する」「RED が立つか確認」「混入するか確認してから」: this is the "verify-first" sub-pattern where you write a RED test to prove the bug exists BEFORE writing the fix — treat it identically to RED→GREEN, even when the issue gives detailed fix instructions. The test-first step must not be skipped even if the fix looks obvious from code reading.** **ALSO fire for refactoring issues ("refactor(xxx): #N", "リファクタリング", "責務分離", "分割", "抽出", "一元化", "集約", "共有モジュール") whose acceptance criteria include a test requirement ("unit test が1件追加", "integration test を追加", "テストで確認する", "テストが緑", "test が green", "env 設定のテスト", "設定テストが緑") — the test must be written RED-first even if the change is purely structural and not a bug fix.** (Issue #166 実例: `retile_hakoniwa_system` の分割 refactoring で acceptance criteria に「モード変更なしのフレームでタイル再構築が走らないことを確認する unit test が1件追加」が含まれていたが tdd を発動せず、実装後に GREEN テストを追加した。RED 先行で書いていれば「実装前の動作保証なし」という穴が埋まっていた。) **「seam を抽出して」「共有モジュールを新設して」「インターフェーステストを書く」「transport なしで pytest カバレッジ」「OrderRegistry のような shared registry を作る」「合成を interface テストで pin」「手動/auto の rail 部分集合を明示」「単一エントリポイントを呼ぶ」「evaluation gate を抽出」場合も同様に発動する。抽出先モジュールの不変条件はコード移植前に RED pytest で確立すること**（#187 実例: OrderRegistry seam 抽出で acceptance criteria に「9 件の pytest インターフェーステスト」が含まれていたが tdd を発動せず、実装後に pytest を書いて GREEN のみ確認した）（#199 実例: PreTradeGate 抽出 refactoring で acceptance criteria に「合成を interface テストで pin（手動/auto の rail 部分集合を明示）」が含まれていたが tdd を発動せず、実装後に 26 件の interface テストを GREEN のみ追加した。RED 先行で書けば「実装前の合成順序保証なし」という穴が埋まっていた）（#229 実例: `BackendTradingState` の `ChartState`/`SessionMetadata` 内部分離 refactoring で `diff_field_some` の None セマンティクス（4 件の unit test 新規）と既存 A25 の "old-GREEN → new-GREEN" 2-step 検証が tdd 範疇だったが Skill invoke しなかった。grill-with-docs Decision 9 で「refactor は RED-first 不適用」と決めたが、それは「RED を書かない」の意ではなく「既存 GREEN を base line として保存 → refactor → 新 GREEN で同じ不変条件が型強化で守られることを確認」の意味。Skill としては invoke すべきだった）。 **ALSO fire when the user command line contains `/tdd` regardless of how the task is framed** — if the user explicitly writes `/tdd` in their request, fire immediately before any implementation begins. **複数のスキルが羅列されたコマンド（例: `/plan /bevy-engine /tdd /behavior-to-e2e`）でも `/tdd` を見落とさないこと**（#179・#186・#190・#192 と 4 回連続で同じパターンで miss した。コマンドに `/tdd` が含まれている = gh issue 読了直後、実装着手前に即 invoke が義務。Python-only refactoring でも、sink/adapter 統合 refactoring でも例外なし。#190 では `replay_runner.py` 新設＋2 runner 統合という refactoring issue に `/tdd` が明示されていたが、issue 調査→実装→テスト作成の順で RED 先行を踏まなかった。#192 では `_build_trading_state_locked` 集約 refactoring で TDD 原則は守ったが Skill ツールを invoke しなかった。**Skill invoke ≠ 原則遵守: TDD の手順を踏んでいても Skill を invoke しなければカウントしない。**）。 **ALSO fire for ANY bug-fix request in this repo ("issue #N を修正して", "バグを直して", "不具合を修正", "fix issue #N") because CLAUDE.md mandates RED first for all bug fixes regardless of how the user phrases it. If the test comes out GREEN immediately (fix was already applied by a prior commit), record it as a regression guard in FLOWS.md with `[x]` and note "fix already applied" — do not force RED artificially. See behavior-to-e2e skill note on the "GREEN from start = fix already applied" pattern.** **「変換関数の戻り型変更」「API シグネチャ変更を伴う fix（-> Self → -> Option<Self> など）」でも issue の acceptance criteria に RED 先行が明記されていれば同様に発動する。変更が "純粋な変換ロジック" や "1行の型変更" に見えても、ADR/CLAUDE.md が RED 先行を求めている場合は skip 不可**（issue #183 実例: `Basis::from_granularity` の戻り型変更 + ADR-0009 準拠 fix で tdd を発動せずに手動 RED→GREEN を行った）。 **特に Python-only の修正 ("fix(live)", "fix(python)", "Python テストを書いて", "pytest で確認", "python/engine/", "python/tests/", "live order", "manual order", "order facade", "safety rails", "pre-trade check") においても同様に発動すること。ユーザーが `/tdd` を明示しなくても、CLAUDE.md が RED→GREEN を要求している場合は自動発動ルールが適用される。Rust 関連の記述が多いが本スキルは Rust/Python 両方に有効。** **Rust テストで `std::env::set_var` / `remove_var` を使う場合は必ず `#[serial]` を付ける（`serial_test` crate が Cargo.toml に存在する）。付け忘れると並列 cargo test で flaky になる。また、このプロジェクトでは `tracing::warn!` でなく `bevy::log::warn!` を使うこと（`tracing` は direct dep でないため E0433）。env var テストの RAII パターン（`EnvGuard`）は `rust-testing` スキルを参照。** **ALSO fire when a `refactor(xxx):` issue includes a latent bug fix alongside the structural change** — even if the acceptance criteria don't explicitly say "add a test". If the refactor corrects incorrect behavior in a venue adapter (e.g. `**extra 撤去`・`Protocol closed contract 化`・`explicit kwarg 昇格` が venue の挙動誤りを包含する), CLAUDE.md's RED-first rule applies. Pattern: issue ラベルが `refactor` でも「実装が venue の誤った動作（UUID 自己生成・caller 指定 client_order_id の無視など）を修正する」なら tdd invoke が必要。(#234 実例: `refactor(live): OrderingVenueAdapter **extra 撤去` で Tachibana UUID 自己生成バグを同時修正。ラベルは `refactor` だったが RED→GREEN は踏んだものの Skill invoke が漏れた。実害はなかったが「refactor = tdd 不要」と判断したこと自体が誤り。) **ALSO fire when a characterization / "contract verification" slice reveals a divergence from a primary source that the owner decides to CORRECT rather than pin** — i.e. the issue says "現挙動を characterization test で固定" but a 一次資料 (venue skill / 公式 error 表 / spec) shows the current behavior is wrong, and the decision becomes "現挙動を GREEN で固定してはいけない → 一次資料に基づき RED→fix で訂正". Write the RED test asserting the CORRECT contract, confirm it fails against current code, then fix production to GREEN. (#19 実例: kabu エラーコード取り違え 4002006↔4002001 と httpx INFO ログによる第二暗証番号/仮想 URL の平文漏洩を characterization が露呈し RED→fix で訂正したが、`tdd` Skill を invoke せず手動で RED→GREEN を回した。手順は踏んだが Skill invoke 漏れ。)
---

# Test-Driven Development

## Philosophy

**Core principle**: Tests should verify behavior through public interfaces, not implementation details. Code can change entirely; tests shouldn't.

**Good tests** are integration-style: they exercise real code paths through public APIs. They describe _what_ the system does, not _how_ it does it. A good test reads like a specification - "user can checkout with valid cart" tells you exactly what capability exists. These tests survive refactors because they don't care about internal structure.

**Bad tests** are coupled to implementation. They mock internal collaborators, test private methods, or verify through external means (like querying a database directly instead of using the interface). The warning sign: your test breaks when you refactor, but behavior hasn't changed. If you rename an internal function and tests fail, those tests were testing implementation, not behavior.

See [tests.md](tests.md) for examples and [mocking.md](mocking.md) for mocking guidelines.

## Anti-Pattern: Horizontal Slices

**DO NOT write all tests first, then all implementation.** This is "horizontal slicing" - treating RED as "write all tests" and GREEN as "write all code."

This produces **crap tests**:

- Tests written in bulk test _imagined_ behavior, not _actual_ behavior
- You end up testing the _shape_ of things (data structures, function signatures) rather than user-facing behavior
- Tests become insensitive to real changes - they pass when behavior breaks, fail when behavior is fine
- You outrun your headlights, committing to test structure before understanding the implementation

**Correct approach**: Vertical slices via tracer bullets. One test → one implementation → repeat. Each test responds to what you learned from the previous cycle. Because you just wrote the code, you know exactly what behavior matters and how to verify it.

```
WRONG (horizontal):
  RED:   test1, test2, test3, test4, test5
  GREEN: impl1, impl2, impl3, impl4, impl5

RIGHT (vertical):
  RED→GREEN: test1→impl1
  RED→GREEN: test2→impl2
  RED→GREEN: test3→impl3
  ...
```

## Workflow

### 1. Planning

When exploring the codebase, use the project's domain glossary so that test names and interface vocabulary match the project's language, and respect ADRs in the area you're touching.

Before writing any code:

- [ ] Confirm with user what interface changes are needed
- [ ] Confirm with user which behaviors to test (prioritize)
- [ ] Identify opportunities for [deep modules](deep-modules.md) (small interface, deep implementation)
- [ ] Design interfaces for [testability](interface-design.md)
- [ ] List the behaviors to test (not implementation steps)
- [ ] Get user approval on the plan

Ask: "What should the public interface look like? Which behaviors are most important to test?"

**You can't test everything.** Confirm with the user exactly which behaviors matter most. Focus testing effort on critical paths and complex logic, not every possible edge case.

### 2. Tracer Bullet

Write ONE test that confirms ONE thing about the system:

```
RED:   Write test for first behavior → test fails
GREEN: Write minimal code to pass → test passes
```

This is your tracer bullet - proves the path works end-to-end.

> **必ず RED を「実行して」確認してから実装に入る。** 仕様/issue が「回帰テストは RED 済み」と書いていても鵜呑みにしない。まず `cargo test <名前>` を走らせる。**もし既に GREEN なら、その修正は既にコミット済みの可能性が高い**（issue が OPEN なまま放置されているだけ）。`git log --oneline -- <対象ファイル>` と該当 system/guard の実装を grep で当て直し、本当に未実装か確認する。既に実装済みなら**再実装せず**、検証結果を添えてユーザーに報告する（二重実装・既存コードの上書きを防ぐ）。例: issue #23 は「i15 RED 済み」と書かれていたが実際は HEAD コミットで A+B 解決済み・i15 green で、着手前の RED 実行で発覚した。
>
> **自分の「最初の Read」も鵜呑みにしない。** セッション中に `git pull`/merge で HEAD が動き、序盤に Read したファイルが古くなることがある（作業ツリーが裏で更新される）。RED を書く直前に `git log --oneline -3` で HEAD を確認し、対象ファイルは Grep で当て直してから着手する。例: issue #24 は着手時の Read で supervisor に `LIVE_VENUE` 配線が皆無に見えたが、その後の merge（`05e5c491`）で spawn 側配線が既に入っており、Grep で再確認して初めて「残作業は attach 側照合のみ」と判明した。Read 1 回ぶんの古いスナップショットで設計すると、既存実装を再実装・上書きしかける。
>
> **このリポジトリの watcher 制約 → 「build-green / runtime-RED」で RED を作る。** この workspace は外部 watcher が走っており、**`cargo build --lib` がコンパイル不可な間は未コミットの編集（テスト含む）を巻き戻す**（bevy-engine skill 参照）。そのため「未定義シンボルを参照して compile error を出す」古典的 RED は、書いた瞬間に watcher に巻き戻されて消える。回避策＝**RED テストは lib が「コンパイルできる」形で書き、実行時に落とす**：既存シンボルだけを参照し、挙動は `app.update()` 等で駆動して assert を外す（watcher は red **build** でのみ巻き戻す。red **test** は安全）。RED の確認は「ビルドは通った／テストは runtime で FAILED（例: `left: Inherited, right: Hidden`）」で行う。**例外**: enum バリアント新規追加など、exhaustive match を壊して compile error が不可避な土台ステップは、最小の GREEN（バリアント＋全 match arm）を同じ手にまとめて lib を green に保つ（テストは intent を記録する characterization 寄りになる）。例: issue #25 は全スライスをこの build-green/runtime-RED で回し、各手を `cargo build --lib` green に保ったまま TDD した。

### 3. Incremental Loop

For each remaining behavior:

```
RED:   Write next test → fails
GREEN: Minimal code to pass → passes
```

Rules:

- One test at a time
- Only enough code to pass current test
- Don't anticipate future tests
- Keep tests focused on observable behavior

### 4. Refactor

After all tests pass, look for [refactor candidates](refactoring.md):

- [ ] Extract duplication
- [ ] Deepen modules (move complexity behind simple interfaces)
- [ ] Apply SOLID principles where natural
- [ ] Consider what new code reveals about existing code
- [ ] Run tests after each refactor step

**Never refactor while RED.** Get to GREEN first.

## Checklist Per Cycle

```
[ ] Test describes behavior, not implementation
[ ] Test uses public interface only
[ ] Test would survive internal refactor
[ ] Code is minimal for this test
[ ] No speculative features added
```
