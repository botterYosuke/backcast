# E2E 台本 共通規約（Surface / Journey 全台本が従う正本）

`Assets/Tests/E2E/Editor/*E2ERunner.md` の全台本が共通で従う規約。各台本はこのファイルを参照し、語彙や
ルールを再掲しない（凡例だけ短く引用してよい）。命名・配置の上位規約は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

## 二層 E2E

```text
1. Surface E2E … 画面部品ごとに、そのサーフェスでユーザーができる操作を網羅する回帰ゲート。
                 入力・状態遷移・host 呼び出しまでを主対象にする。
2. Journey E2E … 複数サーフェスをまたぐ実ユーザーストーリー。横断データ伝播・実業務フローを保証する。
                 本数は絞り、壊れると致命的な代表 journey に限定する。
```

## 共通ルール

1. **Action ID** は `<SurfaceOrJourney>-<NN>` 形式に統一する（2桁ゼロ詰め、例: `MENU-01`, `HAKONIWA-01`,
   `STRATEGY-01`, `JOURNEY-REPLAY-01`）。
2. **入口(file:line)** は「現時点で確認できた代表実装箇所」。将来のリファクタで line がずれても、
   **Action ID と行動定義を正**とする。
3. **Surface / Journey の責務境界**: Surface E2E はサーフェス内の入力・状態遷移・host 呼び出しまで。
   複数サーフェスをまたぐデータ伝播・実業務フローは Journey E2E に寄せる。重複する場合は **Surface 側に
   Journey への参照を置く**。
4. **HITL専用 / 対象外 は必ず理由を書く**（載せない、ではなく理由付きで載せる）。これは「自動化済み一覧」
   ではなく「ユーザー行動の網羅台帳」だから。

## カバー状態の語彙（5値）

| 状態 | 意味 |
|---|---|
| `自動(E2E済)` | **この台本に対応する E2ERunner 自身**で自動検証済み |
| `自動(E2E済・<Runner>)` | **別 Runner** が既に回帰ゲート化済み（この台本では実装しない＝正本は名指しした Runner）。例: `自動(E2E済・ReplayToHakoniwa)` |
| `自動(Probe有・要昇格)` | 既存 Probe が assert を持つ。E2ERunner へ昇格すればよい |
| `要新規自動化` | 自動化可能だが現状テスト無し。新規に書く |
| `HITL専用` | 自動化しない。理由を併記（実ピクセルの美観／実 venue 接続／OS ネイティブダイアログ依存／外部認証・秘密情報依存／GPU・実ウィンドウ前提 など） |
| `対象外` | E2E の対象にしない。理由を併記（未配線 stub／廃止予定／開発者専用／テスト補助 UI など） |

## 台本のセクション構成（この順を踏襲）

1. 対象サーフェス（Journey は: 対象ストーリー）
2. 対象ユーザー行動
3. 操作一覧表（網羅台帳）— `Action ID / ユーザー行動 / 入口(file:line) / 観測点 / 自動判定 / カバー状態 / 既存Probe`
4. 観測点（詳細）
5. 自動判定（合格条件）— `[E2E <NAME> PASS]` ログ・exit 0・`error CS\d+` 0 件・delete-the-logic litmus
   - **後方互換と詳細化**: 従来の surface 単位の `[E2E <NAME> PASS]` に加え、個別の Action-ID に対応する `[E2E <Action-ID> PASS]` の個別/併記出力を許容・推奨する。これにより、テスト実行ランナー（run-live-e2e.ps1）でのシナリオ単位の rollup 判定を段階的に詳細化できる。
   - **C# 側 per-Action-ID タグは到達マイルストン（PASS 専用）**: 各ステップの成功地点で `[E2E <id> PASS]` を吐く設計。失敗時は手前で抜けるため**当該 id は出力されず**、surface の `[E2E <NAME> FAIL]` が立つ（rollup では「id 不在＋surface FAIL＝そのステップ未到達」と読む）。明示 FAIL を per-id で出したい runner は失敗分岐で `[E2E <id> FAIL] <msg>` を吐いてもよい。なお **pytest 側（conftest）は実 outcome から PASS/FAIL/SKIP の三状態**を自動出力する（[E2E-INDEX](./E2E-INDEX.md) の pytest 正本行）。
6. 既存 Probe との対応（昇格元 / 探索用に残す の仕分け）
7. 将来の `*E2ERunner.cs` 実装方針（第二波の設計メモ）

> カバー状態の凡例（セクション）は各台本で再掲せず、本ファイルへの参照で足りる。

## 共通の実装前提（第二波の runner 用メモ）

- runner は実 `BackcastWorkspaceRoot` を反射合成する（`OpenScene` → `SetSynthesizer(FakeMarimoSynthesizer)` →
  `ResolvePaths` → `BuildWorkspace`）。Python-FREE が既定。Python kernel を要する観測点のみ
  `host.InitializePython("MOCK")` を直呼び（batchmode の所有権スキップを迂回する正当手）。
- ファイルダイアログは `StubFileDialog`、メニュー/ボタンは対応する `On*` を反射 invoke。
- 実行: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod <Name>.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド。Unity ログは UTF-8 なので **ripgrep で grep**。

## runner の section ↔ Action ID 対応方針

Runner の section は原則として台本の Action ID と対応づける。ただし、既存 Probe の実証済み section や
共有 pure validation のように、複数 Action ID を一つの自然な検証単位で assert するほうが保守性が高い場合は、
1 section が複数 Action ID を cover してよい（`Validate()` のような共有純関数を Action ID ごとに人工分割しない
＝検証単位が自然なまとまりであることを優先する。保守コストだけ増えて E2E の信頼性は上がらないため）。

その場合、section header/comment に `Covers: <Action IDs>` を明記し、台本側から runner section へ追跡できるようにする。
