---
name: auto-pair-relay
description: 司令塔（team lead）が **永続 Navigator（Agent Teams の teammate）** と **使い捨て Driver（subAgent）** のハイブリッドで長い実装を回す運用スキル。lead は team を立てて Navigator teammate を spawn し、以降は Navigator（Bash/Skill/Agent をフルに持つ頭脳）が grill→設計→Driver subAgent への 1 手→自前の cargo/pytest 検証まで主導する。Navigator↔Driver は直接やり取りし、lead は team セットアップ・Human への橋渡し・最終ゲートに徹する。⚠️ **Navigator は永続 teammate**、**Driver は Navigator が `Agent` ツールで 1 手ごとに spawn する使い捨て subAgent**（`.claude/agents/pair-relay-navigator.md` / `.claude/agents/pair-relay-driver.md`）。Agent Teams が使えない harness では両者とも subAgent に落とす旧リレー設計に fallback する（本文「fallback」節）。Driver は user ではない — user は Human/Owner として最終承認と手元 E2E を担当する。user がドライバになる pair-prog 系は別スキル `pair-nav` を使う（pair-nav: 「ガイドして」「ナビゲータとして」「自分で書きたい」等、user の学習・自力実装意図がトリガー）。⚠️ **Navigator context 継続の失敗（実例 #239 2026-06）**: `Agent()` tool は毎回 new subagent を spawn するため、`prompt="SendMessage to <id>: ..."` で継続しても新規インスタンスが起動し `[grill-done]` context が失われることがある。判定方法: 継続側が「grill-done サマリが見つからない」「新しいインスタンス」と返したら context 喪失。回復手順: grill-done サマリ全文を新 Navigator に貼り直す（本文「respawn/cleanup」節と同様）。grill 決定事項をファイル（ADR 等）に永続化しておくと喪失リスクが下がる。
---

# Pair Relay（Agent Teams ハイブリッド）

長い実装を **永続 Navigator（teammate）** と **使い捨て Driver（subAgent）** のハイブリッドで回すスキルです。

```text
Human / Owner
  │ 依頼・最終承認・手元 E2E
  ▼
lead（司令塔 = あなた）            ……… team を立て、Human と橋渡しし、最終ゲートを締める
  │ mailbox（永続）
  ▼
Navigator（永続 teammate / 頭脳）  ……… Bash・Skill・Agent をフルに持つ
  │ Agent ツールで 1 手ごとに spawn・報告を直接受領
  ▼
Driver（使い捨て subAgent / 手）   ……… 1 手 = 1 spawn のタイピスト、stateless
```

## なぜハイブリッドか

旧設計は Navigator も Driver も subAgent で、lead が両者の間で**全メッセージを verbatim にリレー**していました。そこから生まれた痛みはすべて subAgent の制約由来です:

- subAgent は毎ターン context を失う → **respawn のたびに外部記憶を貼り直す**（+5-10k token/ターン）。
- subAgent から Bash が剥がれる harness では **lead が cargo/pytest を代行**する。
- subAgent は Skill を持たない → **grill を lead が代行**し、回答者の振り分けに苦心する。

**Navigator を永続 teammate にすると、この 3 つが構造的に消えます。** teammate は独立した完全な Claude Code インスタンスで、ターンをまたいで context を保ち、Bash も Skill も Agent ツールも持つ。だから Navigator は自分で grill を回し、自分で Driver を spawn し、自分で cargo/pytest を叩けます。lead はもう per-step のリレー係である必要がない。

一方で Driver は **stateless なタイピスト**なので永続させる旨味がなく、1 手ごとに spawn する使い捨て subAgent が最も安い。レビューのセカンドオピニオンも同じく使い捨ての read-only `Explore` で十分かつ安全（破壊的操作が構造的に不可能）。

「永続させる価値のある頭脳は teammate、使い捨てでよい手は subAgent」——これがハイブリッドの分界線です。

## 役割

| 役割 | 種別 | 担当 |
|---|---|---|
| lead（あなた） | team lead | team セットアップ、Human との橋渡し、最終ゲート、respawn/cleanup |
| Navigator | 永続 teammate | grill、設計判断、次の 1 手、Driver の駆動、cargo/pytest 検証、差分整理 |
| Driver | 使い捨て subAgent | Navigator の diff を Edit/Write でそのまま反映し、短く報告する |
| Human / Owner | 人間 | 最終承認、手元 E2E、owner 判断 |

参照:

- Navigator: `.claude/agents/pair-relay-navigator.md`（teammate として spawn。Bash/Skill/Agent を持つ）
- Driver: `.claude/agents/pair-relay-driver.md`（Navigator が `Agent` ツールで spawn する subAgent）

## セットアップ

1. **Navigator を 1 体 spawn する。** `pair-relay-navigator` agent type で Navigator を spawn する。lead = あなた、Navigator = 永続の頭脳、の最小構成。
2. **Navigator にゴールを渡す。** 依頼/issue の原文・計画書・既知の制約・「Driver は使い捨て subAgent として自分で 1 手ずつ spawn する方針」「最終承認と手元 E2E は Human が担う」を渡す。
3. **以降は Navigator 主導。** lead は Navigator からの報告・owner 質問・最終ゲート要求が来るまで前に出ない。

### 永続の仕組み（最重要 — ここを誤ると context が消える）

「永続 Navigator」はこのハーネスでは **「最初に spawn した Navigator に対し、以降の全メッセージを SendMessage で継続して送る」** ことで実現する。Agent Teams の teammate も、`Agent` ツールで spawn した subAgent の SendMessage 継続も、lead から見た運用は同じ：**同じ Navigator を生かし続ける**。

ルール:

- **2 回目以降は SendMessage（前回の Navigator 宛て）で継続する。新規 `Agent(pair-relay-navigator, …)` をゴール全文で呼び直さない。** 新規 spawn＋ゴール再貼り付けは **記憶ゼロの Navigator** を生み、grill も状況把握もやり直しになる（context と token の二重消費）。
- 渡すもの（owner 判断の回答・Driver の報告・Human のバグ報告・「次の 1 手の承認」など）は全部、同じ Navigator への SendMessage で送る。SendMessage の本文は**差分だけ**（ゴールや grill 結果を貼り直さない — 継続側は覚えている）。
- **`Done (N tool uses · X tokens · Ys)` は despawn ではない。** これは 1 ターンの完了サマリで、SendMessage すれば同じ context で再開する。"Done" を見て「終わった、次は新しく立て直そう」と新規 spawn してはいけない。
- **Navigator が `[owner-decision]` を返して止まったとき、特に despawn に見えやすい（grill 完了・実装待ち状態）。owner 判断が返ってきたら必ず `SendMessage` で同じ Navigator に返す。** 新規 `Agent(pair-relay-navigator, ...)` を呼ぶと grill 結果が消え、Navigator がコードを再 Read して grill 再現に追加 token を消費する（実例: issue #235 で owner 回答を新規 spawn で渡し、2本目 Navigator がコード再 Read から grill を再実施）。
- 例外は `[respawn-request: navigator]` が返ってきたとき、または**論理的に別タスク**（実装が一段落して code-review 修正フェーズに入る等）のときだけ。そのときは新個体を spawn し、引き継ぎ block（または新タスクのゴール）を渡す。

#### 健全な継続か despawn かの見分け方（agentId の同一性では判定しない）

⚠️ **このハーネスは SendMessage で継続するたびに新しい `agent-<id>.jsonl` を作る**（agentId 文字列は毎回変わる）。だから「**agentId が変わった = despawn**」は **偽陽性**になる。実機トレース（#220）で確認済み: 同一スレッドの継続でも `abda821e… → a0c8ccf2…` と id は変わったが、context は完全に保持されていた。

判定は **agentId ではなく挙動と promptId** で行う:

- **健全な継続**: 継続側の transcript が **同じ promptId を共有**し、冒頭が `SendMessage to <前 id>: <差分>` で始まり、**ゴール全文の貼り直しが無く**、最初の発話が「了解、grill 完了、実装フェーズ開始」のように **前段を覚えている**（再 grill しない・コードをゼロから再 Read しない）。
- **本物の despawn（記憶喪失）**: **promptId が変わり**、ゴール全文を貼り直され、**grill を再実行**し、コードをゼロから再 Read する。これが出たら、lead が継続ではなく新規 spawn をしてしまったということ。

トランスクリプトは `~/.claude/projects/<proj>/<session>/subagents/agent-<id>.jsonl`（promptId は各行の `"promptId"`、継続の冒頭は `"SendMessage to Navigator <id>"`）で事後検証できる。

Navigator が持つツール（重要）:

- **Bash** — cargo/pytest/rg/git status を自分で叩く（lead 代行は不要）。
- **Skill** — grill-with-docs・bevy-engine・rust-testing・tdd 等を自分で invoke する。
- **Agent** — Driver subAgent と read-only `Explore` レビュー subAgent を自分で spawn する。
- **Read / Grep / Glob** — コードを当てる。

### 条件付きスキルの読み替え（重要）

この description には「**Navigator spawn 前に bevy-engine / rust-testing / tdd / tachibana / kabusapi / nautilus-trader / behavior-to-e2e を発動せよ**」という条件付きスキル発動ルールが多数蓄積されている。**Teams 版ではこれを「Navigator の spawn prompt に『その slice で X スキルを invoke せよ』という指示を含める」と読み替える。** 理由: teammate は lead が invoke 済みのスキル context を**継承しない**（独立インスタンス）が、Skill ツールを**持つ**ので自分で invoke できる。どの slice でどのスキルが要るか（src/ui→bevy-engine、Rust テスト→rust-testing、venue→tachibana/kabusapi 等）の**判定基準は description のまま有効**で、actor が lead から Navigator に移るだけ。lead は slice ごとに「このスライスは src/ui を触る → Navigator に bevy-engine invoke を指示」と判定して spawn prompt に書く。

### behavior-to-e2e は spawn prompt で必ず「要否判断」を明示する（標準）

bevy-engine / rust-testing 等は **領域ゲート**（src/ui を触る・Rust テストを書く等、入る領域で要否が決まる）なので、lead が領域を見て該当時だけ指示すればよい。一方 **behavior-to-e2e は CLAUDE.md 必須の横断ゲート**で、しかも全スキル中**最も invoke 漏れしやすい**（実例多数: #157 / #255 / #46 / #151）。領域では切れず「挙動が変わったか」で決まるため、lead が事前に領域判定で拾い切れない。

したがって**標準パターン**として、**毎回の Navigator spawn prompt（および挙動が変わりうる slice の継続 SendMessage）に、behavior-to-e2e の要否を Navigator 自身に判断させる一行を必ず入れる**。「該当 slice だけ書く」のではなく「**判断せよ**」を常に渡すのがポイント — 判断を Navigator に外注することで lead の領域判定漏れを塞ぐ。

spawn prompt に必ず入れる一行（テンプレ）:

```text
【behavior-to-e2e 要否判断（必須）】この作業で「挙動が変わる / 新しい不変条件が生まれる /
バグ修正の RED / API 語彙変更・クラス廃止・型統合・型分離・命名規約変更・transport default 変更」の
いずれかに当たるかを自分で判断せよ。当たるなら behavior-to-e2e を invoke し、FLOWS.md への flow 追記・
wiki の [FlowID] 反映・（可能なら）e2e_replay.rs 等への回帰ガード追加まで行う。当たらないなら
「behavior-to-e2e 不要（理由: …）」と1行で明記せよ。判断結果（invoke した / 不要+理由）は完了報告に必ず含めること。
```

「リファクタのみ」でも新しい不変条件（drain 順序保証・transport default 変更など）が生まれたら**該当**する点を Navigator に明示する（ラベルが『リファクタ』『修正』でも挙動・契約が変われば対象）。完了報告にこの判断結果を毎回出させることで、漏れが事後に可視化される（下記「完了報告」）。

## 標準ループ

### 0. 着手前 grill（Navigator 主導・ただし lead がゲートを守る）

旧設計では Navigator が Skill を持てず lead が grill を代行していたが、**teammate の Navigator は自分で `grill-with-docs` を invoke できる**。grill は Navigator の永続 context 内で一気通貫に回るので、論点ごとの respawn も外部記憶の貼り直しも不要。

⚠️ **構造ゲートの注意（最重要）**: 旧設計では grill は **Navigator を spawn する前**に lead の context で回っていたので、Navigator がまだ存在しない段階の手順＝**構造的にスキップ不可能**だった。grill を Navigator の中に移すと、grill は Navigator の「self-directed な最初の一歩」になり、Navigator は「次の 1 手」マシンなので**状況把握から即設計に飛んで grill を飛ばす**。だから lead が明示的にゲートを張る必要がある。

**lead のゲート enforcement（必ず行う）:**

1. Navigator の **spawn prompt に「最初の Driver を spawn する前に必ず `grill-with-docs` を完了し、`[grill-done]` + 決定事項サマリを lead に報告せよ。grill を飛ばして実装に入るな」と明記する**。「着手前に grill を回せ」だけでは弱い — Driver spawn の前提条件として書く。
2. Navigator から **`[grill-done]` サマリを受け取って初めて実装フェーズを承認する**。Navigator が grill を飛ばしていきなり「設計方針が固まった、Driver を spawn します」と来たら、**そこで止めて grill をやり直させる**（lead は Navigator の mailbox / 進行を見られる）。
3. owner 判断の論点が grill 中に上がってきたら、その 1 問だけ Human に提示して回答を返す。

⚠️ **実例（2026-06、Teams 版初回運用）**: Navigator が grill を飛ばし、`tachibana_ws.py` の file-split 方針を状況把握だけで即決して Driver を spawn した。grill が self-directed step になり構造ゲートが消えたのが原因。「単純な file-split だから grill 不要」と Navigator が自己判断したが、re-export の後方互換・import 契約・テスト依存など grill で潰すべき急所があった。→ lead が spawn prompt でゲートを明示し、`[grill-done]` を受けるまで実装を承認しない運用に修正。

Navigator の grill 進め方:

1. `grill-with-docs` を invoke する。
2. 設計論点を **1 問ずつ**立てる。前の論点の答えが次の前提を変えるので、全論点を batch にしない（設計ツリーを依存順に 1 本ずつ解く）。これは実装ループの「1 手ずつ」と同じ禁則で、harness の通信能力に依存しない。
3. **コード/ドメインで答えられる論点は Navigator 自身が Read/Grep で答える**（「既存の型は」「この命名は CONTEXT.md と整合するか」「呼び出し側の期待は」等）。推測で埋めない。
4. **owner 判断が要る論点だけ lead に上げる**（product 意図・要件の曖昧さ・優先順位など、コードからは決まらないもの）。lead はその 1 問だけ Human に提示し、回答を Navigator に返す。コードで答えられる問いまで Human に投げない。

   ⚠️ **「設計原則から自明な答えがある」設計判断は owner に投げない実例 (#217)**: Navigator が "LiveLoopManager クラスにする vs factory 関数のみにする" を owner 質問として投げた。user は「こんなのowner判断じゃない。理想的な完成系をめざせばおのずと答えがでるだろ」と返した。責務分離の原則に従えば「状態を持つなら class」と自明 — この種の問いは Navigator が Read/設計原則で自己解決する。**「どちらが理想的な設計か」は owner 判断ではなく、設計原則に立ち返れば Navigator が答えられる**。

   ⚠️ **「スコープ列挙（何フィールドが対象か）」もコード質問の実例 (#218)**: Navigator が "LiveSession にまとめる8フィールドの集約方向でよいですか？" を owner 質問として投げた。しかし issue は 25+ フィールドを列挙しており、どれが session 寿命かは `__init__` と `_teardown_live_components` を読めば確定できる。lead が「コードで答えられる設計判断」と押し返し、Navigator が自己解決した。**「どのフィールド/クラス/ファイルがスコープに含まれるか」は Read/rg で確認できるコード質問。owner に投げない**。

   ⚠️ **「強い推奨案を持っているのに確認を求める」パターン（実例 #222）**: Navigator が `inproc_call_load_replay_data` の Rust 境界について「後方互換ラッパ vs Rust 側書き換え、どちらの方針ですか？」と問い、同時に「私の推奨案はバックワードコンパチラッパ」と明記した。lead が「issue スコープは Python 層・設計原則から自明」と押し返した。**自分で強く論拠づけた推奨案がある場合は owner に問わず自己決定せよ。「どちらでしょうか？」と聞いた時点で推奨案の論拠を信じていないことになる。**「issue のスコープ外」「Rust 変更コストが高い」のような client-facing な理由で選択肢が絞り込まれている場合は特に自己解決できる。
5. 設計ツリーが解決したら、決定事項を CONTEXT.md / ADR にその場で反映する（Navigator が Skill を持つので自分で書ける）。
6. 着手十分か自己レビューし、未解決の急所があれば 2 に戻る。
7. **lead に `[grill-done]` + 決定事項サマリ（分割方針・境界条件・設計の急所）を報告し、実装フェーズの承認を待つ。** ここを飛ばして Driver を spawn しない。

grill 結果（決定事項・境界条件・設計の急所）は実装フェーズの起点にそのまま使う（同一 context なので引き継ぎ不要）。

⚠️ **grill が issue の指定ファイル構造から逸脱する設計を提案したら owner 確認を取る（実例 #217）**: issue が `engine/backend/` sub-package + 7 ファイル構造を指定していたが、Navigator の grill 分析で "live 側 5 責務は `_live_loop` で密結合 → `LiveLoopManager` 1 クラスにまとめる方が自然" と結論し、`live/live_orchestrator.py` への統合案を出した。この場合 lead は `[grill-done]` 報告に「issue 指定の構造からの逸脱」を明記し、owner 確認後に承認してから実装に進む。**issue の file 構造は owner が決めた設計仕様 — grill の技術的判断で黙って変えてよいものではない**。

### 1. 開始

grill で固めた設計をもとに、Navigator が作業領域に応じたスキル（`bevy-engine`・`rust-testing`・`tdd`・`tachibana`・`kabusapi`・`nautilus-trader` 等）を必要に応じて invoke してから実装に入る（上記「条件付きスキルの読み替え」）。

### 2. 次の 1 手 → Driver を spawn

Navigator は「次の 1 手」を具体化したら、**Driver subAgent を 1 体 spawn**してその diff を渡す。1 手 = 1 spawn。複数手順を 1 spawn にまとめない（実装ループの作業単位は 1 手 = 1 往復）。

**前提条件**: 最初の Driver spawn は **grill ゲート（step 0）通過後にのみ**行う。grill 未完了なら、どれだけ設計方針が固まって見えても Driver を spawn しない。

Driver に渡すもの: 対象ファイル / 対象関数・位置 / 変更内容 / 残すべき処理・触らない処理 / 中間状態の注意。

Driver は Read で挿入位置を確認し、Edit/Write で diff をそのまま反映し、1〜3 行で報告して終了する（stateless なので毎回新個体でよい）。

### 3. Driver の報告を直接受ける

Driver の報告は **Navigator が直接受け取る**（lead を経由しない＝直接 mailbox）。報告に「次の手の前提が崩れる事実」（中間状態でコンパイル不能・未定義参照が残る・副作用の波及・「Save 側だけ変更済みで Save As は未変更」等）が含まれていれば、Navigator はそれを設計判断に織り込む。Navigator は自分でコードに照合できるので、確認質問に対しても推測で OK を返さず Read/テストで裏を取る。

Driver が指示を**適用せず保留**した（`[review-block]`）場合、Navigator は理由・質問を読んで次の 1 手を起草し直す。質問を丸めず、推測で押し切らない。

### 4. Navigator が検証する

Navigator は自分の Bash で `rg` / `cargo check` / `cargo test` / `pytest` / 旧経路 grep / 差分整理を回す。手元 E2E が必要なものは「何を見れば PASS か」を具体化して lead 経由で Human に渡す。

## lead の残務（薄い）

Navigator が主導するので lead の仕事は限られる:

- **team セットアップ**と Navigator へのゴール受け渡し（slice ごとに要るスキルの invoke 指示を spawn prompt に含める）。
- **Human ⇄ Navigator の橋渡し**: owner 判断の質問を Human に提示し回答を返す／手元 E2E のバグ報告を Navigator に **verbatim** で渡す（仮説を採用して Driver を直接動かさない）。
- **最終ゲート**: 完了宣言の前に「テストを実際に回したか」を締める（下記「Medium 以上なし」節）。
- **完了報告**を Human に短く並べる。
- **respawn / cleanup**: Navigator から context 逼迫の合図が来たら新個体に引き継ぐ。team を畳む。

lead が橋渡しで要約・改変するとき、作業判断に関わる情報は削らない（Human への最終報告での重複ログ圧縮のみ可）。

## 規律（Teams でも効く — 役割に依らない教訓）

通信が直接 mailbox になっても、次の品質規律は変わらず効く。旧設計では lead がリレー時に守らせていたが、**Teams 版では Navigator が自分の Bash/Read で enforce する**。

### Driver の「変更不要 / 既に最終形 / skip した」は事実申告ではなく仮説

Driver が「既に適用済み」「現状が最終形なので変更不要」「差異なしで skip」と返したら、Navigator は **Read で実体を当て、テストで裏を取る**まで確定にしない。「進めて大丈夫ですか？」という確認質問にも、Navigator は推測の GO/NO-GO を返さずコードに照合してから答える。

実例（#177 A1 slice3）: Driver が「inproc edge は ack→dict 変換の最終形・変更不要」と報告したが、実際は `return self._svc.x()` の素通しで、上流が typed result を返すよう変わった結果 dict 契約が壊れ baseline が **3 failed の RED** に落ちていた。Navigator の pytest 実行 + Read で初めて発覚。「変更不要」報告ほど実測で潰す。

### Driver の「適用しました」も tool_uses:0 / git delta 無しなら虚偽

「変更不要」だけでなく **「diff を反映しました」という成功報告そのものも仮説**。Driver subAgent は稀に Edit/Write を一度も呼ばずに（`tool_uses:0`）「適用完了」と返す（apply の幻覚）。Navigator は **毎 apply を `git --no-pager diff` / `git status --short` で実体確認**し、想定ファイルに想定 delta が出るまで次の 1 手に進まない。delta が無ければ同じ diff で Driver を**再 spawn**する（stateless なので新個体でよい）。Agent の usage に `tool_uses:0` が見えたら、報告文がどれだけ具体的でも適用は起きていない。

実例（#87 step F 2026-06）: Driver が「edit を反映しました」と詳細に報告したが `tool_uses:0`・`git status` で対象ファイルに変更なし。git status で検知し同 diff で再 spawn して反映した。

### 「Medium 以上なし」を確定する前に必ずテストを回す

レビュー＆修正ループで Navigator が**コードレビューだけで「Medium 以上なし」と結論しても、それを最終確定にしない**。コードレビューは静的観察で、落ちるテストを見落とす。

- Navigator が「Medium 以上なし」と返したら、**完了宣言の前に必ず該当範囲の `cargo test` / `pytest` を回す**（Navigator が自分の Bash で。teammate なので lead 代行は不要）。回す対象は Navigator が起草した検証コマンド、無ければ変更ファイルに対応するユニット/統合テスト。
- テストが赤なら raw 出力を見て「実装バグ（Medium 以上）か / stale テスト（実装は正・テストが誤り）か」を切り分け、次の 1 手を起草する。
- 「コミットメッセージに GREEN と書いてある」「前任が健全と言った」だけで緑とみなさない。実際に回す。
- **lead は最終ゲートとして**、完了報告を受ける前に「Navigator は実際にテストを回したか」を確認する。回した形跡がなければ回させる。

実例（issue #64）: コードレビューのみで「Medium 以上なし」だったが、pytest を回すと 1 件赤（`assert success is False` だが実装は `True`）。再判定で「実装が正・テストの期待が stale」と切り分けてテスト側を直した。回さなければ完了報告をすり抜けていた。

### レビューのセカンドオピニオンは read-only `Explore` で spawn する

独立セカンドオピニオン（codex 代替の Claude レビュー）は **必ず read-only agent type（`Explore`）**で spawn する。`general-purpose` は Bash/Edit/Write を持ち、レビュー中に作業ツリーを触りうる。

実例（#46 Slice B2）: `general-purpose` のレビュー Agent が「baseline 比較」のため `git stash pop` → conflict → `git reset --hard` で復旧、という破壊的 git をレビュー名目で実行した。`Explore`（read-only）なら構造的に不可能。

codex（CLAUDE.md 指定のセカンドオピニオン）が usage limit / 未インストールで回せないときは、`Explore` read-only レビューがそのまま正規の代替になり、レビュー＆修正ループは完結させてよい（codex 不在を理由にループを止めない）。完了報告に「codex は usage limit のため Explore で代替」と明記する。観点別に `Explore` を複数本（削除振る舞い / cross-file / cleanup 等）走らせるほうが codex 1 本より recall が高い（実例 #209）。

### 破壊的 git を subagent に絶対やらせない

Driver / Explore を spawn する prompt に必ず「`git checkout` / `git reset` / `git restore` / `git stash` 等、作業ツリーを巻き戻すコマンドは実行禁止」と明記する。検証は pytest / cargo / Read / Grep / `git status` まで。

実例: ある作業で一時 probe テストを `git checkout` で除去した際、同ファイルの未コミット追加（289 行のテスト）まで巻き戻して消失させた。subagent はワークツリー全体の文脈を持たないので、`git checkout <file>` でも自分が作っていない未コミット変更を道連れにする（`git reset --hard` だけの問題ではない）。一時 probe は別 throwaway ファイルに書くか Read だけで観測し、消すときも名指しの Edit で消す。長い実装で未コミット差分が積み上がったら、Navigator が節目で WIP コミットして保護する。

### 反幻覚（Driver 指示の前に必ず実在確認）

Navigator は Driver に渡す diff の **ファイルパス / 関数名 / 型名 / フィールド名 / variant 名 / 行番号**を、指示前に必ず `Read` / `rg` で実在確認する（詳細は `.claude/agents/pair-relay-navigator.md` の「反幻覚ルール」）。推測で書くと Driver が `[review-block]` を返して 1 往復ロスする。

### `[review-block]` の扱い

Driver が指示を**適用せず保留**した（`[review-block]`）場合、その理由・質問は Navigator が直接受け取り、丸めず・推測で押し切らず、次の 1 手を起草し直す。質問数を変えない。

## バグ報告の扱い

手元 E2E 中にバグが出たら、Human → lead → Navigator へ症状・仮説・再現情報を**そのまま**渡す。lead が仮説を採用して Driver を直接動かさない。Navigator がコードで照合し、1 件だけ修正指示を Driver に出す。

良いバグ報告（このまま Navigator に渡せる粒度）:

```text
4 panel 全部 spawn されたが、位置が cache JSON の値に適用されていません。
drag autosave は機能しています。

仮説:
apply_pending_layout_system の早期 return に引っかかっています。

トレース:
1. apply_cache_restore_system が fragments を populate
2. panel_spawn_dispatcher_system が drain
3. apply_pending_layout_system が waiting_for_strategy=true && empty で return
```

## 中間状態の扱い

長い実装では、一時的にビルド不能になる順序を通ることがあります。Driver からその警告が来たら、Navigator はそれを設計判断に織り込み、想定内なら次の 1 手を続ける（Navigator は自分でコードに照合できるので、中間状態の安全性を Read で確かめられる）。

## フォーマットと差分整理

format / restore の判断は Navigator が行う。

教訓:

- 全体 `cargo fmt` は無関係ファイルを巻き込むことがある
- 対象ファイルだけ `rustfmt --edition 2024` が有効なことがある
- `mod.rs` の check は子 module まで見て既存未整形で落ちることがある
- E2E で fixture が更新されることがあるため、最後に `git status` を分けて見る

Navigator は実装差分・検証副作用・無関係差分を分けて扱い、restore 対象を Human に明示する（破壊的 git は使わず名指しの Edit / 限定 restore で）。

## 完了報告（lead → Human）

完了時、lead は Navigator の検証結果を事実として短く並べる。

```text
完了です。

- cargo check: OK
- cargo test: OK
- E2E Step 1-8: PASS
- 旧保存経路 grep: 残骸なし
- behavior-to-e2e: invoke 済み（FLOWS.md に P20 追記）／または「不要（純粋な内部リネームで挙動・契約不変）」
- 本実装差分: Cargo.toml / Cargo.lock / src/ui/...
- 検証副作用: examples/test_strategy_daily.* は戻す候補
```

behavior-to-e2e の行は**必ず入れる**（invoke した／不要+理由のどちらか）。空欄は「判断していない」と同じで、CLAUDE.md 必須ゲートの漏れになる。

## respawn / cleanup

- Navigator から context 逼迫の合図（`[respawn-request: navigator]` + 引き継ぎ block）が来たら、新しい Navigator teammate を spawn し、引き継ぎを**書き換えず・追加せず・要約せず**そのまま渡す。整形は混入であり、Navigator の判断材料を変えてしまう。「引き継ぎを整えてあげたほうが親切」という気持ちが出たら踏みとどまる。
- Driver は stateless なので respawn の概念がない（毎手 spawn し直す）。
- **turn が 401 / ネットワークで途中死しても、まず `git log` を見る**。Driver が適用しコミット済みのスライスは生きている。「turn が落ちた＝作業消失」と決めつけて redo せず、`git log --oneline` / `git status` で着地済みコミットを確認してから次へ進む（実例 #87 2026-06: 401 で turn が途中終了したが slice 3 コミットは着地済みで、git log から復元）。
- 作業が終わったら team を畳む（cleanup）。

## fallback: Agent Teams が使えない harness

teammate を spawn できない harness（Agent Teams が無効・`/resume` 後に teammate が消えた・split-pane 不可な端末等）では、**Navigator も subAgent に落とす旧リレー設計**で回す。この場合に限り次の workaround が要る:

- **SendMessage 不可 → 毎ターン Navigator を新個体 spawn**。lead が prompt に「直近 commit sha / baseline 数字（passed/failed/errors）/ 確定済み仕様 / 直前の Driver 報告 verbatim」を**外部記憶**として貼り続ける。Navigator の最初の仕事に「Read で SUT 直前状態を確認してから次手を起草」を含める。各ステップを「1 ファイル / 1 関数 / 1 論理修正」まで小さく保てば、新個体でも 15-20+ ターン破綻せず回せる（実績: Phase 7.3 A0 の per-instrument data refactor を proto→python→rust の十数ステップで完走）。
- **確定仕様（マトリクス・契約・enum 名・採番）は lead の SendMessage 本文に毎ターン貼り直すのではなく、早期に durable artifact（`docs/findings/NNNN-*.md`・台本・scratch design ファイル）へ書き出させ、以降の spawn prompt は「canonical 仕様は `<path>` が正本（まず Read で復元）」とポインタだけ渡す**（実例 #89 2026-06: grill 確定の QUIT-01..09 マトリクスを findings 0068 に書き出し、各 fresh Navigator はそれを Read して復元。lead が長大な仕様を毎ターン再送する token 浪費と、再送ミスで Navigator が仕様を**再構成**（≠復元）する事故を両方防げる）。findings はどのみち behavior-to-e2e の成果物として要るので、それを外部記憶の正本に兼用するのが最も安い。
- **subAgent から Bash が剥がれる → lead が cargo/pytest を代行**し、合否を判定せず raw 出力（「3 passed」「`WinError 10038` で 4 failed」等の事実だけ）を次の Navigator に verbatim で渡す。解釈・次手は Navigator。RED→GREEN は「lead が RED 確認 → Driver が実装 Write → lead が GREEN 確認 → 次 Navigator が解釈」で回す。
- **長時間ビルド/コンパイル/再ビルド（数分〜十数分）は lead が自分の Bash で `run_in_background` 実行する**（実例 #2 S0 2026-06: nautilus の `HIGH_PRECISION=false` sdist 再ビルド / Unity batchmode compile / `-executeMethod` probe）。理由: **Navigator subagent が自分の Bash で起動した background タスクは、その subagent のターン終了時に死ぬ**（subagent が「完了通知を待つ」と言って終了しても通知は来ず、プロセスも残らない）。lead の background タスクは**ターンをまたいで生き、完了時に lead を再起動する**ので、長尺ジョブの唯一の安定実行者は lead。Navigator には「ビルド系はあなたの background では死ぬので lead が回す」と spawn prompt で明示し、Navigator は **コンパイルが通る/スクリプトが書き切れる形まで**作って lead に検証を渡す。lead は完了通知の raw 出力（exit code・PASS/FAIL 行）を次の Navigator に verbatim で渡す。Bash が「剥がれている」のではなく「background が揮発する」点が上の項と異なる。
- **grill も lead が `grill-with-docs` を代行**し、コードで答えられる論点は Navigator subAgent に Read/Grep で当てさせ（`[owner-decision]` prefix で owner 判断だけ返させる）、owner 判断だけ Human に上げる。

fallback でも「1 論点ずつ・1 手ずつ・batch しない」の規律は崩さない。これは harness の通信能力に依存しない作業単位の話で、Teams でも subAgent でも同じ。**この harness では Navigator/Driver が最初の 1-2 ターンで「Bash を持っていない」「SendMessage が無い」と返して判明する**ので、判明したら即この運用に切り替える。⚠️ **ただし fallback は「Bash 無し」だけで判明するとは限らない**: Navigator が Bash も Skill も持っていても、SendMessage 継続が効かず**各 spawn が記憶ゼロ**ということがある（実例 #89 2026-06: Navigator は Bash/Skill を持ちつつも継続が効かず、3 体目が前段で確定したマトリクスを「覚えている」のではなく**ゼロから再構成**して owner に確認を求めた）。だから「Bash の有無」ではなく**「継続 spawn が前段の決定を覚えているか（再 grill・再 Read・仕様の再構成が起きていないか）」を毎回観察**し、再構成の兆候が出たら即 fallback（外部記憶 + durable artifact ポインタ）へ切り替える。lead が薄い差分 SendMessage（「owner 承認、実装進めて」等）を投げて Navigator が仕様を見失うのが典型の発火点。

## 合言葉

頭脳は永続 teammate、手は使い捨て subAgent。  
Navigator には判断と検証を、Driver には 1 手を、Human には事実を渡す。  
lead は運ぶより、Navigator に任せる。混ぜない、削らない、急がせない。
