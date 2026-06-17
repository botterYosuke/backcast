---
name: grill-with-docs
description: >-
  Grilling session that challenges your plan against the existing domain model, sharpens terminology, and updates documentation (CONTEXT.md, ADRs) inline as decisions crystallise. Use when user wants to stress-test a plan against their project's language and documented decisions.
  **ユーザーが `gh issue #N を実施してください /plan /grill-with-docs` のようにコマンドリストに `/grill-with-docs` を含めたときは必ず発動する**（#199 実例: `/plan /grill-with-docs /diagnose` が羅列されていたのに tdd と diagnose の確認に注力して grill-with-docs を発動しなかった）。複数スキルが羅列されたコマンドでも各スキルを順番に invoke すること。
  **実装後の docs/wiki 整合確認にも使う**: 「実装した内容が wiki と食い違っていないか確認したい」「API の呼び出し側の挙動を docs と照合したい」「契約を明文化する前に docs を読みたい」といった場面でも起動する。実装に入る前の設計ドリルだけでなく、**実装後に呼び出し側コード（`_backend_impl.py` の `hasattr` dispatch など）と docs/wiki のどちらが正しいか確認するコードリーディングとしても機能**する（実例: #189 で `set_execution_hooks` の呼び出しパターンを `_backend_impl.py` で確認）。
---

<what-to-do>

Interview me relentlessly about every aspect of this plan until we reach a shared understanding. Walk down each branch of the design tree, resolving dependencies between decisions one-by-one. For each question, provide your recommended answer.

Ask the questions one at a time, waiting for feedback on each question before continuing.

If a question can be answered by exploring the codebase, explore the codebase instead.

</what-to-do>

<supporting-info>

## Domain awareness

During codebase exploration, also look for existing documentation:

**起動引数にドメインスキル参照（`/nautilus-trader` / `/tachibana` / `/kabusapi` など）が含まれるときは、grill を始める前にその `.claude/skills/<name>/SKILL.md` を読み、ドメイン不変条件を頭に入れる**こと。コード読解で挙動を裏取りするのは依然必須だが、ライブラリ内部仕様（例: nautilus の EXTERNAL/INTERNAL aggregation・backtest に data-client subscribe callback が無い等）に踏み込む主張は、コード＋ドメインスキルの両方で確認すると精度が上がる（#266-269 設計ドリル実例: `/nautilus-trader` 参照があったのにスキル本文を読まずコードだけで進めた）。

### File structure

Most repos have a single context:

```
/
├── CONTEXT.md
├── docs/
│   └── adr/
│       ├── 0001-event-sourced-orders.md
│       └── 0002-postgres-for-write-model.md
└── src/
```

If a `CONTEXT-MAP.md` exists at the root, the repo has multiple contexts. The map points to where each one lives:

```
/
├── CONTEXT-MAP.md
├── docs/
│   └── adr/                          ← system-wide decisions
├── src/
│   ├── ordering/
│   │   ├── CONTEXT.md
│   │   └── docs/adr/                 ← context-specific decisions
│   └── billing/
│       ├── CONTEXT.md
│       └── docs/adr/
```

Create files lazily — only when you have something to write. If no `CONTEXT.md` exists, create one when the first term is resolved. If no `docs/adr/` exists, create it when the first ADR is needed.

## During the session

### Challenge against the glossary

When the user uses a term that conflicts with the existing language in `CONTEXT.md`, call it out immediately. "Your glossary defines 'cancellation' as X, but you seem to mean Y — which is it?"

### Sharpen fuzzy language

When the user uses vague or overloaded terms, propose a precise canonical term. "You're saying 'account' — do you mean the Customer or the User? Those are different things."

### Discuss concrete scenarios

When domain relationships are being discussed, stress-test them with specific scenarios. Invent scenarios that probe edge cases and force the user to be precise about the boundaries between concepts.

### Cross-reference with code

When the user states how something works, check whether the code agrees. If you find a contradiction, surface it: "Your code cancels entire Orders, but you just said partial cancellation is possible — which is right?"

**owner が grill 中に実装を済ませてしまったら、インタビューを畳んで「実装レビュー＋決定の記録」へ pivot する。** grill は本来 pre-implementation の設計ドリルだが、owner が質問（AskUserQuestion 等）への回答として「実装しました」と diff＋テストを出してくることがある（#70 実例 2026-06-17: AC#3「未確定価格の扱い」を尋ねた直後に owner が deny 実装＋特性化テストを完了）。このときインタビューを続けるのではなく、(1) grill で組み立てた設計の木に実 diff を読んで突き合わせる（caller / 削除された不変条件 / golden 影響 / 端のケース）、(2) issue の AC を 1 つずつ満たしているか verify（#70 では byte-identical golden・cross-instrument fill・未確定 deny・risk 参照価格の per-instrument 化を確認）、(3) **確定した下位決定を slice の `docs/findings/` に追記**して締める。grill の成果物は「質問の往復」ではなく「設計の木が docs に固定されること」なので、実装が先行しても skill の価値は失われない（`code-review(simplify)` を続けて発動し Medium+ が無いことまで見る）。

**REOPENED issue は本文 AC ではなく最新コメントが正本** — grill を始める前に `gh issue view #N --comments` で**コメントスレッド全体を読む**。reopen された issue の本文 AC は古く、実際のスコープは最新の reopen コメントにあることが多い（#23 実例: 本文は「demo venue で発注→約定→建玉表示」の done-gate だったが、#39→#59 完了後に reopen され、最新コメントの実スコープは「ProductionLiveShell の order ticket / live panels / secret modal を BackcastWorkspaceRoot へ *re-home* し shell を退役」という全く別の refactor だった。本文だけ読んで grill を始めると設計の的を外す）。本文 AC を額面で受ける前に、コメントで supersede されていないか・別 issue（例 #42）へ責務が切り出されていないかを確認する。

**移植 parity スライス（ADR-0005 の 1:1 表面 parity = menu_bar / settings / theme / reconcile_modal / instruments_universe_prune 等）では、issue の AC を額面通り受けず、移植元 TTWR の実ソース（`D:\Documents\The-Trader-Was-Replaced\src\ui\*.rs`）を grill の最初に直接読んで「AC が実態の正確な言い換えか」を検証する。** issue 文は実態の不正確な圧縮であることがある（#42 実例: issue は「File: strategy の Open/Save」「実行モード picker で Replay/LiveManual/LiveAuto 切替」と書いたが、`menu_bar.rs` の実態は **File=Layout の Open/Save**・**mode は File 操作の副作用**（File→New=LiveManual / Live 中 File→Open=LiveAuto）で明示 picker は無く、strategy Open は別 issue #16、明示 mode/run picker は footer #39/#30 の責務だった）。oracle が「実配線済み（TransportCommand を実際に send している）」か「未配線 stub」かは call site を grep で確認する（[[ttwr-scaffolding-not-an-oracle]] の規律。配線済みなら oracle、stub なら oracle にならない）。食い違いを見つけたら「正すのは AC の文言であって ADR ではない」を原則に、決定を slice の `docs/findings/` に記録し ADR-0005 を参照する（ADR は自己保護条項で固定）。

**永続化 / sidecar / file-I/O parity スライス（layout save / scenario sidecar / Save As / Open / native picker 等）では、「文書（document）= どのファイルか・on-disk 形はどうか」を owner に提案する前に、必ず ① CONTEXT.md の永続化 glossary を*最後まで*読み、② 既存の sibling 永続化ストアを grep する（`*SidecarStore` / `*Store.cs` / `Save(` / `Load(` / `JObject` / merge-write）。** on-disk スキーマ・キーの共存・所有権分離は CONTEXT.md に既に明文化されていることが多く、既存ストアが「読み書きの正規パターン」を持っていることが多い。これを front-load しないと、CONTEXT.md と矛盾する文書モデルを提案して owner に何度も訂正させる（#69 実例 2026-06-18: layout の置き場を「独立 global layout.json」→「global mirror＋currentDocumentPath フィールド」と 2 度誤提案。正解は CONTEXT.md L380 が明記する「`<strategy>.json` に `scenario` キーと `layout` キーを共存」＝2 ファイル文書モデルで、既存 `ScenarioSidecarStore` が Newtonsoft merge-write の鏡像テンプレートだった。最初に L380 と `ScenarioSidecarStore` を読んでいれば 1 発で当たった）。TTWR oracle の `finish_layout_save` が `.json`+`.py` を束ねる形でも、移植先の所有権分離（engine 所有 scenario vs Unity 所有 layout・#16 所有 .py）次第で「同梱」か「参照」かが変わる——oracle の形をそのまま写す前に移植先の既存 seam で裏取りする。

**多スライス cutover / 「slice N に着手」の依頼では、grill を始める前に `git log --oneline` と関連 commit・issue コメントで「その slice が後続 sibling commit で既に実装済み / 追い越されていないか」を裏取りする。** issue 本文・memory・findings の slice table は実装に追い越されて stale になりがちで、額面の「未着手」を信じると done の作業を再実装しかける。`find <deleted-file>`・`git merge-base --is-ancestor <commit> HEAD`・該当シンボルの grep で「計画上の状態」ではなく「現 HEAD の実態」を確認すること（#42 cutover 実例 2026-06: issue は slice 2 着手依頼だったが、slice 2 は merge 済・slice 3「発注/建玉/Auto パネル移設」と slice 4「ProductionLiveShell 退役」も後続 `#23 re-home` commit `381d58c` で実装済み＝shell はディスクから削除済みで、実際の OPEN 残務は #57 の depth ladder 本線 mount だけだった。3 つの食い違いを git 裏取りで検出し AskUserQuestion で再アンカーした）。**さらに、長い実装セッション中に他ブランチ（例 #61）が main へ merge されて base が動くことがある**——commit 前に `git log` / `git diff HEAD` で HEAD の移動と自分の編集が revert / 競合していないかを必ず確認し、動いた base 上で gate を再走する（実例: 本 #57 で `#61` が session 中に merge され Hakoniwa base が 5 タイル化、暫定で当てた probe 修正が #61 の正式修正に置換された）。stale な docs/memory はその場で実態へ整合させる。

**逆向きも確認する: 「slice N に着手」の依頼で、N が *未完了の先行 slice* や *常時走る不変条件* に blocked されていないか**を grill の冒頭で裏取りする。slice table は「N は OPEN」と書いていても、N が前提とする dep 昇格 / ADR / 別 slice の配線が未了なら、N を額面で実装すると不変条件を壊す（gate RED）か ADR-gated なポリシーに反する。`grep` で当該不変条件の gate（例 import-purity / offline）と pyproject の dep 区分を読み、N の前提が満たされているか確認してから設計に入る。満たされていなければ実装に突入せず、前提 slice を先にやるか・制約付き interim にするかを **AskUserQuestion で owner に再アンカー**する（#76 実例 2026-06-18: 「S6＝KernelRunner 載せ替え」依頼だったが、S6 は ① marimo prod 昇格＋ADR=S3 ② 発注経路注入=S4 に blocked で、runtime seam が thin_drain を import すると常時走る offline gate が RED ＋ spike-only dep のため ImportError。git/grep/pyproject で 2 つの blocker を検出し、owner が「S3 を先に」を選択 → S3 の ADR grill に pivot した）。

### Update CONTEXT.md inline

When a term is resolved, update `CONTEXT.md` right there. Don't batch these up — capture them as they happen. Use the format in [CONTEXT-FORMAT.md](./CONTEXT-FORMAT.md).

`CONTEXT.md` should be totally devoid of implementation details. Do not treat `CONTEXT.md` as a spec, a scratch pad, or a repository for implementation decisions. It is a glossary and nothing else.

### Offer ADRs sparingly

Only offer to create an ADR when all three are true:

1. **Hard to reverse** — the cost of changing your mind later is meaningful
2. **Surprising without context** — a future reader will wonder "why did they do it this way?"
3. **The result of a real trade-off** — there were genuine alternatives and you picked one for specific reasons

If any of the three is missing, skip the ADR. Use the format in [ADR-FORMAT.md](./ADR-FORMAT.md).

**Never edit an existing ADR that declares itself fixed.** Before proposing to amend an ADR,
read its end matter for a self-protection clause (e.g. ADR 0022's "The decisions are fixed …
reopening requires a new ADR superseding this one, not an edit to this file"). If present —
or if the precedent in sibling work recorded its resolution elsewhere — do **not** amend it.
Two cases are commonly mistaken for "needs an amend":
- The new decision merely **resolves a fork the ADR intentionally left open** (e.g. ADR 0022
  Decision 2 said "quantized re-raster *or* SDF", and #250 chose quantize). That is a
  confirmation, not a reopening — record it in the slice's `docs/findings/NNNN-*.md` (the
  canonical record for that slice) + `FLOWS.md` + `docs/wiki`, and have the slice **point to**
  the ADR ("方針: ADR NNNN"), never write back into it.
- The decision genuinely contradicts the ADR → write a **new superseding ADR**, not an edit.
Mirror what sibling slices did (in this repo, #247/#248/#249 all kept ADR 0022 immutable and
recorded in `docs/findings/`). Surface the clause to the user before touching any ADR file.

</supporting-info>
