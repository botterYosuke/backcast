# findings 0044 — menu dropdown z-order: chrome uGUI cutover (#77)

方針: ADR-0005（1:1 表面 parity）。語彙: CONTEXT.md「chrome z-order 前面順序」。

## 症状と根本原因（#77）

メニューバーの File/Edit/Venue/Help の dropdown が左の Universe sidebar の**背面**に描画され、左カラムに
重なる項目をクリックできない（#62 HITL Phase 2 の File→Save をブロック）。`MenuBarView` も
`UniverseSidebarView` も **両方 `OnGUI`（IMGUI）**。単一カメラの Screen-Space では `GUI.depth` は無視され
（`GUI.depth=-100` は無効）、IMGUI 同士の描画順は MonoBehaviour 実行順依存で、後に走る sidebar が dropdown を
上塗りしていた。`[DefaultExecutionOrder(10000)]` も効かず（issue 記録・再試行不可）＝**IMGUI のままでの
z-order 制御は袋小路**。

## 決定

### D1 — chrome を uGUI 化（IMGUI 撤去）。menu と sidebar の両方
issue の案2（menu+sidebar を単一 OnGUI に統合）は `BackcastWorkspaceProbe` Section2（menu と sidebar は
別コンポーネントで両方 Content 外）と V-host 分離を壊すので却下。案1（uGUI 化）を採用。さらに menu を uGUI 化
すると sidebar が production 唯一の IMGUI 残党になり、**uGUI menu↔IMGUI sidebar の入力ブリード**（後述 D3）を
生む。owner 方針（仮状態を残すな・理想の完成形）に従い **sidebar も uGUI 化**して mixed-input クラスごと消す
（#69 は file-picker/multi-doc が本体で sidebar の uGUI 化は無主だった＝#77 が正式に所有。`MenuBarView` の
uGUI 化メモは #69 から #77 が吸収）。ブレイン（`MenuBarViewModel` / `UniverseSidebarController`）は不変、
View のみ OnGUI→uGUI に差し替え。

### D2 — z-order は nested override-sorting Canvas で決定的に持つ
各 View は自分の GameObject に `Canvas(overrideSorting=true)` + `GraphicRaycaster` を持つ（SecretModalOverlay の
実証パターンの再利用）。レイヤリング契約（sortingOrder の数値はここが正本）:

| 層 | sortingOrder | 実体 |
|---|---|---|
| field / windows / footer | 0 | メイン Canvas（既定） |
| sidebar | `UniverseSidebarView.SIDEBAR_SORT` = **500** | nested |
| menu backdrop | **599**（MENU_SORT−1） | 全画面・menu 展開中のみ |
| menu bar + dropdown | `MenuBarView.MENU_SORT` = **600** | nested |
| secret modal | **1000** | 独自 ScreenSpaceOverlay（既存・不変） |

menu(600) > sidebar(500) なので dropdown は確定的に sidebar の前面。secret(1000) > menu(600) なので modal は
最前面を維持。値は派生で、ガード（probe Section13）は**関係のみ**を assert（menu>sidebar>0、secret>menu）。
container は authored RectTransform（top strip / left column）のまま＝描画領域は derived（hardcode 禁止を踏襲）。
dropdown は 1 行 container の外へ伸びる（uGUI はマスク無しで子をクリップしない）。

### D3 — 入力ブリードは backdrop ＋ 最前面 raycaster 解決で断つ
uGUI ボタンは EventSystem のポインタイベントしか消費せず IMGUI の `Event.current` は素通りするため、案1 単体だと
dropdown 直下の sidebar ボタンが同じクリックで発火し得た（IMGUI を残した場合）。D1 で sidebar も uGUI 化した
ことで **EventSystem が全 GraphicRaycaster を sortingOrder で解決し最前面のみへ配送**＝構造的にブリード消滅。
加えて menu 展開中だけ全画面透明 backdrop（sortingOrder 599＝sidebar と menu の間）を有効化し、外側クリックで
menu を閉じつつ sidebar への到達を消費（desktop メニューの semantics）。dropdown パネルにも raycast-target 背景を
敷き、項目間の隙間で sidebar へ抜けないようにする。

### D4 — 動的リストは retained-mode rebuild、検索フィールドは破棄しない
sidebar の銘柄行・picker 候補は毎フレーム再生成せず、controller の observable（`InstrumentRegistry.Changed` /
`SelectedSymbol.Changed` / picker トグル）で rebuild。検索フィールド（`InputField`）は picker を開いている間
**一度だけ**生成し、キーストロークごとの候補リスト rebuild では破棄しない（フォーカスを奪わない）。menu は静的
ツリーを一度組み、`Refresh()`（毎フレーム軽量）で badge / mode / dropdown 可視性 / venue 項目の interactable を
反映する（venue 接続状態の async poll に追従するため）。

## ゲート

- **headless（正本 AFK・RED→GREEN 済）**: `BackcastWorkspaceProbe` Section13「chrome z-order layering」。
  fix 前 RED＝`zorder: MenuBarView has no Canvas (still IMGUI?)`、fix 後 `[BACKCAST WORKSPACE PASS] all sections
  green.`（全13 section・origin/main merge 後）。構造契約（両 chrome が overrideSorting Canvas + GraphicRaycaster／menu>sidebar>0／
  secret>menu）を assert。pixel z-order とクリック貫通は owner HITL。
- **owner HITL（manual-gate・PASS 2026-06-17）**: ① File/Edit/Venue/Help の dropdown が sidebar の前面に描かれ
  全項目クリック可、② dropdown 直下の sidebar 行が同クリックで発火しない、③ menu 外クリックで閉じる、
  ④ secret modal が menu より前面（構造上 1000>600・接続環境がある場合）、⑤ sidebar の select/×remove/+Add/
  picker/search が退行なし、⑥ #62 HITL Phase 2（File→Save→再 Play で復元）が実施可能。**全 PASS。**

## 参照
- `Assets/Scripts/Live/MenuBarView.cs`（uGUI・`MENU_SORT`・backdrop・dropdown）
- `Assets/Scripts/Universe/UniverseSidebarView.cs`（uGUI・`SIDEBAR_SORT`・retained rebuild）
- `Assets/Scripts/Live/SecretModalOverlay.cs`（sortingOrder=1000 の実証パターン）
- `Assets/Editor/BackcastWorkspaceProbe.cs` Section13（#62 統合 smoke は Section12 で温存）
- 関連: #69（file picker / multi-doc・OnGUI 撤去 follow-up 群）、#62（本バグでブロックされていた HITL）
