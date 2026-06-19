---
name: grill-with-docs
description: >-
  Grilling session that challenges your plan against the existing domain model, sharpens terminology, and updates documentation (CONTEXT.md, ADRs) inline as decisions crystallise. Use when user wants to stress-test a plan against their project's language and documented decisions.
  **ユーザーが `gh issue #N を実施してください /plan /grill-with-docs` のようにコマンドリストに `/grill-with-docs` を含めたときは必ず発動する**（#199 実例: `/plan /grill-with-docs /diagnose` が羅列されていたのに tdd と diagnose の確認に注力して grill-with-docs を発動しなかった）。複数スキルが羅列されたコマンドでも各スキルを順番に invoke すること。
  **実装後の docs/wiki 整合確認にも使う**: 「実装した内容が wiki と食い違っていないか確認したい」「API の呼び出し側の挙動を docs と照合したい」「契約を明文化する前に docs を読みたい」といった場面でも起動する。実装に入る前の設計ドリルだけでなく、**実装後に呼び出し側コード（`_backend_impl.py` の `hasattr` dispatch など）と docs/wiki のどちらが正しいか確認するコードリーディングとしても機能**する（実例: #189 で `set_execution_hooks` の呼び出しパターンを `_backend_impl.py` で確認）。
  **CI / automation / GitHub Actions / workflow YAML の設計 grill では、既存 artifact の runtime 契約に依存する設計決定（CI で .exe を起動する / build output path を前提にする / log 行を grep する 等）を YAML に焼く前に「実環境で想定どおり動くか」の preflight を必ず走らせる**。コード読解で「動きそう」までしか言えない invariant は、実 binary を CI と同じフラグで叩いて empirical PASS/FAIL を取り、failure なら grill 内で fallback ladder へ pivot する。preflight を skip して merge すると hosted runner の初回 fail で owner が逐次 debugging する羽目になる（#83 実例 2026-06-18: Q8 Player smoke を `-batchmode -nographics` で設計したが、preflight で `WorkspaceOwnership.ShouldClaim` の batchmode-skip-Python invariant に当たり fail → grill 内で「GUI mode smoke + log poll + Stop-Process」へ pivot 完了。issue body と findings も同じ flow で update した）。
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

**owner が「完成形 API を spike 無しで凍結しない」と言ったら、grill から throwaway 検証 spike を spawn して仮説を実証してから lock する。** load-bearing な下位決定（host seam の有無・driver の形・per-bar 契約の成否）はコード読解だけでは「〜に見える」までしか言えないことがある。owner が AskUserQuestion の回答として「その判断は正しそうだが完成形 API を固定する前に spike が要る」と仮説採用＋実証要求をしてきたら、(1) 既存 production harness（例: parity gate のテスト）をミラーした最小 throwaway を `python/spike/` に書き、(2) owner が名指した具体リスクを実機で潰し、(3) 結果（PASS/FAIL＋実測）を `docs/findings/` の設計の木に「spike で実証」として固定する。GREEN なら下位決定を binding に格上げ、RED なら失敗理由に応じ別 option へ。「コードで裏取り」の規律の延長＝主張を実機実証まで持ち上げる（#76 S6b 実例 2026-06-18: owner が「v19 に multi-instrument bar driver は不要そう」を仮説採用しつつ「完成形 driver API を spike 無しで凍結するな」と要求 → 3-instrument の minimal marimo を実 KernelRunner＋adapter で命令型 twin と order/fill/equity 突合する `v19_shape.py` を書き [V19-SHAPE PASS]、feedback dict 持続・entry 時 snapshot 完備＋no-look-ahead・multi-iid production parity の3リスクを潰して「bar driver 不追加」を binding 化）。

**REOPENED issue は本文 AC ではなく最新コメントが正本** — grill を始める前に `gh issue view #N --comments` で**コメントスレッド全体を読む**。reopen された issue の本文 AC は古く、実際のスコープは最新の reopen コメントにあることが多い（#23 実例: 本文は「demo venue で発注→約定→建玉表示」の done-gate だったが、#39→#59 完了後に reopen され、最新コメントの実スコープは「ProductionLiveShell の order ticket / live panels / secret modal を BackcastWorkspaceRoot へ *re-home* し shell を退役」という全く別の refactor だった。本文だけ読んで grill を始めると設計の的を外す）。本文 AC を額面で受ける前に、コメントで supersede されていないか・別 issue（例 #42）へ責務が切り出されていないかを確認する。**reopen されていない long-running epic issue でも同じ**: 本文が spike / epic ヘッダ（例:「spike: ◯◯ を実行基盤にできるか」の AC1–4）で、実際の次スライスのスコープは **最新の owner コメント＋ `docs/findings/NNNN` の「残務（順序）」リスト**にあることが多い。grill を始める前に `gh issue view #N --comments` でコメントスレッド全体と該当 findings を読み、「issue 本文の AC」ではなく「残務リストの最上位スライス」を実装対象としてアンカーする（#76 実例 2026-06-18: 本文は marimo `App.embed()` の spike gate（AC1–4・既に `docs/spike/` で達成）だったが、実スコープは findings 0046 残務最上位の「portfolio/position の cell 露出」スライスで、設計の下位決定（snapshot 形・読み seam・subset fail-closed）は findings の S6-7 が既に名指していた。本文の spike AC を実装対象と誤読せず、残務リスト＋既存スライス commit で現在地を確定してから設計の木に入った）。

**移植 parity スライス（ADR-0005 の 1:1 表面 parity = menu_bar / settings / theme / reconcile_modal / instruments_universe_prune 等）では、issue の AC を額面通り受けず、移植元 TTWR の実ソース（`D:\Documents\The-Trader-Was-Replaced\src\ui\*.rs`）を grill の最初に直接読んで「AC が実態の正確な言い換えか」を検証する。** issue 文は実態の不正確な圧縮であることがある（#42 実例: issue は「File: strategy の Open/Save」「実行モード picker で Replay/LiveManual/LiveAuto 切替」と書いたが、`menu_bar.rs` の実態は **File=Layout の Open/Save**・**mode は File 操作の副作用**（File→New=LiveManual / Live 中 File→Open=LiveAuto）で明示 picker は無く、strategy Open は別 issue #16、明示 mode/run picker は footer #39/#30 の責務だった）。oracle が「実配線済み（TransportCommand を実際に send している）」か「未配線 stub」かは call site を grep で確認する（[[ttwr-scaffolding-not-an-oracle]] の規律。配線済みなら oracle、stub なら oracle にならない）。食い違いを見つけたら「正すのは AC の文言であって ADR ではない」を原則に、決定を slice の `docs/findings/` に記録し ADR-0005 を参照する（ADR は自己保護条項で固定）。

**永続化 / sidecar / file-I/O parity スライス（layout save / scenario sidecar / Save As / Open / native picker 等）では、「文書（document）= どのファイルか・on-disk 形はどうか」を owner に提案する前に、必ず ① CONTEXT.md の永続化 glossary を*最後まで*読み、② 既存の sibling 永続化ストアを grep する（`*SidecarStore` / `*Store.cs` / `Save(` / `Load(` / `JObject` / merge-write）。** on-disk スキーマ・キーの共存・所有権分離は CONTEXT.md に既に明文化されていることが多く、既存ストアが「読み書きの正規パターン」を持っていることが多い。これを front-load しないと、CONTEXT.md と矛盾する文書モデルを提案して owner に何度も訂正させる（#69 実例 2026-06-18: layout の置き場を「独立 global layout.json」→「global mirror＋currentDocumentPath フィールド」と 2 度誤提案。正解は CONTEXT.md L380 が明記する「`<strategy>.json` に `scenario` キーと `layout` キーを共存」＝2 ファイル文書モデルで、既存 `ScenarioSidecarStore` が Newtonsoft merge-write の鏡像テンプレートだった。最初に L380 と `ScenarioSidecarStore` を読んでいれば 1 発で当たった）。TTWR oracle の `finish_layout_save` が `.json`+`.py` を束ねる形でも、移植先の所有権分離（engine 所有 scenario vs Unity 所有 layout・#16 所有 .py）次第で「同梱」か「参照」かが変わる——oracle の形をそのまま写す前に移植先の既存 seam で裏取りする。

**設計が *先行する grill セッション群* で既に完全凍結されている「実装引き継ぎ」依頼では、Q1–… を再導出しない。** issue のコメントスレッド＋ADR＋findings に設計の木が `[x]` で出揃い、あるコメントが「実装に入る作業者はこのコメント＋正本（ADR/findings）を読めば足りる」と明言していることがある。このとき grill の価値は「新たな質問の往復」ではなく **(1) 正本（ADR=immutable / findings の設計の木）を読み込んで凍結済み下位決定を頭に入れ、(2) `git status` / `git log` / 該当シンボル grep で *working tree の現状が凍結プランのどの Step か*（例: まだ前任の SUPERSEDED 実装が残っているか）を裏取りし、(3) 実装スコープ（どの Step まで今セッションでやるか）を AskUserQuestion で 1 回だけ再アンカーして実装に入る** ことにある。先行セッションが費やした grill を Q1–Q7 の再インタビューで無駄にしない。複数コメントがあるときは **最新コメントが最終設計**で、古いコメント（特に「SUPERSEDED」明記のもの）は履歴として扱う。実装着地は slice の `docs/findings/` に追記して締める（`code-review(simplify)`＋`post-impl-skill-update` も併発）（#81 実例 2026-06-18: findings 0049=text-append が SUPERSEDED → HITL 訂正 → ADR-0013+findings 0050=cell-as-floating-window へ再設計 → 第4コメントに S1 実装分解。引き継ぎセッションは再 grill せず 2 正本を読み込み、working tree が Step 0 の旧 text-append のままと git 確認し、Step 0+1 をやるか AskUserQuestion で確定して実装・golden gate を GREEN 化した）。

**多スライス cutover / 「slice N に着手」の依頼では、grill を始める前に `git log --oneline` と関連 commit・issue コメントで「その slice が後続 sibling commit で既に実装済み / 追い越されていないか」を裏取りする。** issue 本文・memory・findings の slice table は実装に追い越されて stale になりがちで、額面の「未着手」を信じると done の作業を再実装しかける。`find <deleted-file>`・`git merge-base --is-ancestor <commit> HEAD`・該当シンボルの grep で「計画上の状態」ではなく「現 HEAD の実態」を確認すること（#42 cutover 実例 2026-06: issue は slice 2 着手依頼だったが、slice 2 は merge 済・slice 3「発注/建玉/Auto パネル移設」と slice 4「ProductionLiveShell 退役」も後続 `#23 re-home` commit `381d58c` で実装済み＝shell はディスクから削除済みで、実際の OPEN 残務は #57 の depth ladder 本線 mount だけだった。3 つの食い違いを git 裏取りで検出し AskUserQuestion で再アンカーした）。**さらに、長い実装セッション中に他ブランチ（例 #61）が main へ merge されて base が動くことがある**——commit 前に `git log` / `git diff HEAD` で HEAD の移動と自分の編集が revert / 競合していないかを必ず確認し、動いた base 上で gate を再走する（実例: 本 #57 で `#61` が session 中に merge され Hakoniwa base が 5 タイル化、暫定で当てた probe 修正が #61 の正式修正に置換された）。stale な docs/memory はその場で実態へ整合させる。**この repo は複数 feature が並行で flight するので、大規模な多ファイル実装に着手する前に `git fetch` で `origin/main` との乖離を一度見ておく**（mid-session の merge を不意打ちにしない）。

**sibling feature が実装中に merge され、共有 seam で *設計が* 衝突したとき（テキスト conflict ではなく設計 conflict）は、conflict ごとに「どちらの feature の凍結決定が governs するか」を両者の findings/ADR を相互参照して裁定し、調停を *自分の slice の* findings に記録する。** 単に「片方を取る」のではなく 3 通りを使い分ける: (a) **両立** → 相手の機構を*自分の seam 経由で配線し直して採用*（例: #76 U3「起動時 canonical を開く」を #81 の `editor.Open` 退役後 coordinator.Open 経由へ rewire）、(b) **supersede** → 自 feature の凍結設計が相手の方式を明示的に却下しているなら相手を退ける（例: #76 U2「File→New で template を本文 seed」を、findings 0050 が「本文 seed 却下＝空セル＋placeholder」と凍結済みなので supersede）、(c) **両方残す** → 直交する追加（const 2 つ等）。判定の根拠は「最新の findings/ADR が何を凍結しているか」で、相手 commit の新しさではない。共有ファイルの compile は **merge 後ツリーで compile gate＋AFK を再走**して裏取りし、相手が足した probe（自 feature で壊れる API を参照）も同じ方針で移行する（#81↔#76 実例 2026-06-18: editor 単一バッファ→cell モデルの設計衝突を 4 conflict で (a)/(b)/(c) 裁定、`WorkspaceUiCutoverProbe` の `editor.Document` を notebook 版へ移行、調停を findings 0050「#76↔#81 マージ調停」節に固定。compile gate 0 error・AFK 4 本 GREEN で確認）。

**削除（sunset / retire）スライスで probe やファイルを消すときは、それが *pin している契約* が sibling の ADR/findings で「移送対象」と名指されていないかを先に確認し、消す前にその契約が *別 probe で冗長カバー* されているかをコードで裏取りする。** 「delete ではなく契約の移送」と凍結されている probe を単純削除すると回帰網が穴あく。冗長カバーを主張するなら **citing する surviving assertion が本当にその契約を assert しているか（section/行番号まで）** を読んで確認する——「近い名前のテスト」は別の契約を assert していることがある（#76 命令型 sunset 実例 2026-06-19: #80 `StrategyPickerProbe` を delete する際、ADR-0013/findings 0050 が「§5 stale-guard を集約へ移送」と名指していた。`StrategyEditorProbe` が冗長カバーと判断したが、当初 `S4`（`File.Delete` 後の `Open`）を等価と citation→ code-review が「S4 は再書込み済みファイルを Open する provider-level テストで `Open(vanished)→false` は assert しない。直接等価は `S3:275 Open(missing)→false`」と訂正。契約は保持されていたが citation が誤りだった＝**削除の安全根拠は exact assertion まで読む**）。ADR が当該 probe を名指していても ADR は無改変——解決は自 slice の findings に記録し、sibling findings の dangling 参照には stale-marker を足す。

**AskUserQuestion を `(P1)/(P2)/(P3)` のような多択ラベル＋技術ジャーゴンで盛り過ぎると owner に「意味わからない」で差し戻される。** grill 中盤で下位決定が枝分かれしたとき、設計の木の全枝を 1 つの multi-select に詰めると、option 文言が「`bound + not dirty` `WYSIWYR` `Save As 強制` `destructive 上書き`」のような repo 内部語の連鎖になり owner が読み下せない。owner が中央の問い（核）を先に固定したいフェーズでは、**最小の yes/no 1 問へ畳む**のが正解。「Save したとき、ファイルを marimo 形式に書き換えてよい？／はい・いいえ」のように、一語で答えられる核を 1 つだけ立て、派生（dirty 扱い・provider 供給条件・resume 経路）は yes/no 取った後で個別の小問にする。AskUserQuestion 1 問あたり「decision の 1 軸 + 平易な日本語の 2 択」が上限の目安（#86 実例 2026-06-19: 3 択 `(P1)/(P2)/(P3)` で「bound + dirty 状態」「destructive 上書き」「provider supplyable」「Save As 強制」を一度に聞こうとして「意味わからない。もっとわかりやすく質問して」で reject、Save 上書き OK か否かの yes/no 1 問に畳んだら即着地）。設計の木の全枝を見せる衝動は grill agent 側の事情で、owner にとっては「決めて欲しい一点を一度に問われた方が早い」。

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
