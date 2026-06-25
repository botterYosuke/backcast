# DuckDbRootSettingsE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`DuckDbRootSettingsE2ERunner.cs` が自動検証する **Settings「Data」DuckDB root サーフェス**（#137 S4・findings 0107）の台本。
Action ID 採番・カバー状態の語彙・共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

## 対象サーフェス

Settings モーダル「実行」タブの **Data 節**（`SettingsDataSectionView`）。`.env` の `BACKCAST_JQUANTS_DUCKDB_ROOT` を
ダイアログで設定するための、枠付きテキスト欄＋フォルダ参照ボタン `[...]`＋赤エラー。配線の正本は findings 0107 D1–D5:
PlayerPrefs 保存（`JquantsDuckdbRootStore`・app-global）→ `Py.GIL()` で `os.environ` 注入（`JquantsDuckdbRootInjector`）→
Python は従来どおり `engine/paths.py:jquants_duckdb_root()` で lazy 解決（**再起動不要・D4**）。`.env` ローダは残る（D-C）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*。C#↔Python の継ぎ目を 2 ゲートに分割（behavior-to-e2e 規約）——
> DUCKROOT-01/02/03 は **Python-FREE**（store・純 validator・Data view の browse/commit 配線）、DUCKROOT-04 のみ
> **MOCK Python** を起こして実 `os.environ` 注入→実 `engine.paths` 解決を端から端まで通す（engine 読み側は production resolver で fake ではない）。
> engine 解決の単体契約（lazy re-read・listed_info 解決）は `python/tests/test_paths_dotenv.py` が別途 pin。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存Probe |
|---|---|---|---|---|---|---|
| DUCKROOT-01 | DuckDB root を入力して保存・再起動後も保持 | `JquantsDuckdbRootStore.Save/Load` | Save→Load 往復・空保存で clear（=override 無し）・ClearForTests で wipe | store を直接駆動し往復/clear を assert | 自動(E2E済) | `DuckDbRootSettingsE2ERunner`(S01) |
| DUCKROOT-02 | 存在しないパス／`listed_info.duckdb` 欠落の検出 | `JquantsDuckdbRootValidator.Validate` | 空=OK(override 無し)・無いフォルダ=error・`listed_info.duckdb` 欠落=error・有=OK | 実 temp dir で validator を assert | 自動(E2E済) | `DuckDbRootSettingsE2ERunner`(S02) |
| DUCKROOT-03 | `[...]` でフォルダ参照→欄/store/注入へ反映・cancel は fail-soft・不正で赤エラー | `SettingsDataSectionView.OnBrowse/Commit/RefreshError` / `StubFileDialog.BrowseFolder` | 選択フォルダが field＋store＋onCommit へ・data field に枠線/プレースホルダ・null は欄不変・不正パスで赤エラー＋保存 | Stub を注入し `[...]` onClick/onEndEdit を invoke | 自動(E2E済) | `DuckDbRootSettingsE2ERunner`(S03) |
| DUCKROOT-04 | 保存値が次の Replay で使われる（os.environ 注入） | `JquantsDuckdbRootInjector.Inject` / `engine/paths.py` | MOCK Python で Inject→`os.environ` readback 一致・`engine.paths.jquants_listed_info_path()` が注入 root 下の実ファイルを解決（再起動不要） | MOCK Python を起こし注入→readback→engine 解決を assert | 自動(E2E済) | `DuckDbRootSettingsE2ERunner`(S04) |
| DUCKROOT-05 | 実 OS のネイティブフォルダ選択ダイアログを目視操作 | `MacFileDialog.BrowseFolder`(`EditorUtility.OpenFolderPanel`) / `Win32FileDialog`(`IFileOpenDialog`) | 実 Cocoa/Win32 フォルダパネルが開き選択が欄へ入る・Player でネイティブ未対応ならボタン隠しでもテキスト欄は機能 | — | HITL専用（OS ネイティブダイアログ依存） | — |

## 自動判定（合格条件）

- 各 section が null（pass）を返したら `[E2E DUCKROOT-0N PASS]`（単一トークン＝rollup-visible）を吐く。最後に `[E2E DUCKROOT PASS]`。
- いずれかが落ちたら `[E2E DUCKROOT-0N FAIL] <msg>` ＋ `[E2E DUCKROOT FAIL]` で `EditorApplication.Exit(1)`。`error CS` 0 件。
- **DUCKROOT-04 は MOCK Python を起こすため、`EditorApplication.Exit(0)` 後の pythonnet shutdown で `exit=139`（SIGSEGV）になりうる**
  ——`[E2E DUCKROOT-04 PASS]` **タグが verdict の正本**（#107 規約。exit code は環境ノイズで判定に使わない）。
- delete-the-production-logic litmus（findings 0107）: `Inject` を no-op にすると S04 RED（os.environ readback≠root）/ `Validate` の
  `listed_info.duckdb` 存在チェックを消すと S02 RED / `[...]` ボタンを未配線にすると S03 RED（field/store 空のまま）。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値に従う。`HITL専用`（DUCKROOT-05）は理由併記済み。

## 実行コマンド

```
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod DuckDbRootSettingsE2ERunner.Run -logFile <abs log>
```

compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8＝**Bash `grep -a "E2E DUCKROOT"`** で確認。
DUCKROOT-04 が MOCK Python を起こすので `exit=139` でも PASS タグがあれば GREEN（#107 規約）。
