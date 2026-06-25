# UniverseSubscribeE2ERunner — 台本（ADR-0031 S4/S5 / #144・#145）

方針: [ADR-0031 D6](../../../../docs/adr/0031-cell-driven-dynamic-universe-bt-universe-api.md) ＋
[findings 0115 §S4/§S5](../../../../docs/findings/0115-issue141-145-cell-driven-dynamic-universe.md)。

## 何を gate するか

ADR-0031 D6：universe membership 変化 → market-data 購読の**対称追従**。`InstrumentRegistry.Changed` 起動で、
戦略 cell の `bt.universe.add(X)` でも UI [+ Add] でも registry に銘柄が足されたら **add→subscribe**（S4）、
remove/clear で **unsubscribe**（S5）。ADR-0022 の「deliberately no universe-Changed auto-subscribe」を **supersede**。
購読は membership に従属（ADR-0022 D3）＝subscribe/unsubscribe 失敗は registry を変えない。venue 非依存（runner/adapter 抽象）。

2 ゲート分割：coordinator contract は recording sink で Python-FREE・決定論（UNISUB-01..06）、実 root 配線は full-stack
MOCK で非空虚に証明（UNISUB-07/08・hook 非経由の pure registry mutation → 板 render／feed 停止）。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod UniverseSubscribeE2ERunner.Run -logFile <abs>
# expect: [E2E UNIVERSE SUBSCRIBE PASS]（確認は Bash `grep -a "UNIVERSE SUBSCRIBE"`）
# NOTE: full-stack section は MOCK Python → exit=139（shutdown segfault）あり。verdict は [E2E … PASS] タグ（E2E-INDEX 規約）
```

## 操作一覧表

| Action ID | 行動 | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|
| `UNISUB-01` (S4) | LiveAuto・registry.Add（hook 非経由） | recording sink に Subscribe(X) | sink.Subs⊇{X} | 自動(E2E済) |
| `UNISUB-02` (S4) | Replay・registry.Add | 購読しない（Live-gate） | sink.Subs 空 | 自動(E2E済) |
| `UNISUB-03` (S4) | entry bulk 後の later add | fresh のみ購読（dedup） | sink.Subs=={新id} | 自動(E2E済) |
| `UNISUB-04` (S5) | LiveAuto・registry.Remove | Unsubscribe(X)・survivor 残 | sink.Unsubs⊇{X} | 自動(E2E済) |
| `UNISUB-05` (S5) | clear | 全 Unsubscribe | sink.Unsubs⊇全 | 自動(E2E済) |
| `UNISUB-06` (S4/S5) | subscribe/unsubscribe 失敗 | registry membership 不変 | reg.Ids 不変 | 自動(E2E済) |
| `UNISUB-07` (S4) | 実 root・pure `_scenario.Universe.Add` | MockVenue 板 render | DepthDecoder.HasDepth | 自動(E2E済・MOCK) |
| `UNISUB-08` (S5) | 実 root・pure `_scenario.Universe.Remove` | feed 停止（forget→板消滅） | !HasDepth | 自動(E2E済・MOCK) |
| `UNISUB-09` | 実機目視：LiveAuto cell で bt.universe.add/remove → 板の出現/停止 | owner 手元 | — | HITL専用（実 venue・LiveAuto 実走） |

## RED→GREEN litmus（findings 0115 §S4/§S5）

- `OnUniverseChanged` の `BulkSubscribeUniverse` を消す → UNISUB-01/07 RED。
- `IsLive(_lastMode)` gate を消す → UNISUB-02 RED（Replay 誤購読）。
- `OnUniverseChanged` の unsubscribe ループを消す → UNISUB-04/05/08 RED。
- `_scenario.Universe.Changed += _subCoord.OnUniverseChanged` 配線を消す → UNISUB-07/08 RED。
