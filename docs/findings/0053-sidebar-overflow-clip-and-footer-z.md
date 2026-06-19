# findings 0053 — footer が sidebar overflow に隠れる: 二重保証で塞ぐ (#84)

方針: 関連 ADR なし（chrome z-order は ADR 化されておらず CONTEXT.md「chrome z-order 前面順序」+ findings 0045 が正本）。
語彙: CONTEXT.md「chrome z-order 前面順序」（amend 済）。前提: findings 0045（#77 uGUI 化と z-order テーブル）。

## 症状と根本原因 (#84)

`python/strategies/v19/v19_morning_cell.py` を開くと、画面下部の footer（`WorkspaceFooterView` の Replay/Manual/Auto
セグメント＋ステータスライン）が左の universe sidebar の**背面**に描画され、footer の Replay/Manual/Auto ボタンや
status text が見えなくなる。

二つの設計の食い違いが**重ね合わせで**発火させた：

1. **sidebar 内部の overflow 非制御** — `UniverseSidebarView` は rows と picker list をそれぞれ
   `_rowsRoot.anchoredPosition.y = -index * ROW_H` の絶対算術で配置し、`RectMask2D` も `ScrollRect` も
   持たない。`v19_morning_cell.json` の universe は **50 銘柄**（`ROW_H=22`）→ rows 高 1100px に対し
   sidebar の可視高は 1552×987 のスクリーンで ≈ 923px (= 987 − MENU_H 24 − FOOTER_H 40)。
   差 ≈ 177px が sidebar 矩形の下端を超えて描画される。uGUI は親 RectTransform を自動 clip しない。

2. **footer の z-order が contract から片落ち** — findings 0045 D2 の z-order テーブルは footer を
   `field / windows / footer | 0`（メイン Canvas）にまとめていた。sidebar は `SIDEBAR_SORT=500` の own
   override-sorting Canvas を持つため、sidebar Canvas 上で overflow した子要素は footer（main Canvas, 0）の
   **前面**に描画される。`BackcastWorkspaceProbe.Section13_ChromeZOrderLayering` は menu / sidebar / secret
   の 3 層しか assert しておらず、footer の位置はテスト網にも入っていなかった。

`BackcastWorkspaceSceneBuilder.cs:180-185` の `AnchorLeftColumn(sidebar, SIDEBAR_W, MENU_H, FOOTER_H)` で
sidebar 自体は footer 帯を**避けて**author されている（bottom inset = 40px）。つまり「枠」は健全で、
症状は枠を抜けた**内部 child の overflow** によるもの。

## 決定

### D1 — sidebar 内部を構造的に clip する（同層内 overflow を物理的に閉じる）
`UniverseSidebarView` の child layout を rows / picker list の 2 つの `ScrollRect` + `RectMask2D` viewport
パターンへ書き換える。pinned 要素は `_content` 直下に置き、scroll 領域の余高公式で配分する。

```
_content (padded inset of sidebar RectTransform)
├─ Title              (pinned top, TITLE_H)
├─ RowsScroll          [ScrollRect, viewport+RectMask2D]
│   └─ RowsContent     (n × ROW_H, top-anchored)
├─ +Add                (pinned, ROW_H, between rows と picker)
├─ PickerRoot          (visibility-toggled; 高さは下式で derive)
│   ├─ "search:" label (ROW_H)
│   ├─ search InputField
│   └─ PickerListScroll [ScrollRect, viewport+RectMask2D]
│       └─ PickerListContent (i × ROW_H)
└─ FocusLabel          (pinned bottom, ROW_H)
```

**余高公式（`Relayout`）**:
- 固定要素 = `TITLE_H + ROW_H_add + ROW_H_focus + 4*GAP`
- `available = container.h − 固定要素`
- picker 閉: `RowsViewport.h = available`
- picker 開: `PickerListH = min(natural, (available − ROW_H_search − GAP) / 2)`、
  `RowsViewport.h = available − ROW_H_search − GAP − PickerListH − GAP`

これで「候補 3 件なら picker は小さく rows が広い」「候補 100 件なら picker は available の半分まで取って
rest を rows に譲る」が同じ式で成立し、rows / picker のどちらも overflow すれば内部スクロールに収まる。
`Rebuild()` 中は `verticalNormalizedPosition` を pre/post capture して、× / +Add などのイベントで scroll
位置がリセットされない。

### D2 — footer に own override-sorting Canvas を追加（chrome 層間の z-order を構造的に閉じる）
`WorkspaceFooterView.Build(bar)` で `bar.gameObject` に `Canvas(overrideSorting=true, sortingOrder=FOOTER_SORT)`
+ `GraphicRaycaster` を追加する。`FOOTER_SORT = 550` で **sidebar(500) < footer(550) < menu backdrop(599) <
menu+dropdown(600) < secret modal(1000)** の関係に挿入する。

選定根拠（owner と grill で確定）:
- footer > sidebar: sidebar overflow が footer を上書きできない（#84 の構造的禁止）
- menu > footer: dropdown は依然 footer 帯を覆える（デスクトップ規約・VSCode/IntelliJ/Figma 全例で
  dropdown は status bar を覆う）
- menu backdrop(599) > footer(550): menu 展開中に footer をクリック → backdrop が消費して menu を閉じる
  desktop semantic を保つ（footer が backdrop の前に出ると「menu 開いたまま mode 切替」する exotic state ができる）

値は派生で、`Section13_ChromeZOrderLayering` の assert は**関係のみ**（menu > footer > sidebar > 0 / secret > menu）。

### D3 — 二重保証は意図的（片方が抜けてももう片方が支える）
D1 と D2 はどちらか一方でも #84 を塞げる。owner は両方の採用を選んだ。理由:
- D1（structural clip）= **同層内**の overflow を物理的に閉じる。新規 chrome が同 sortingOrder で増えても
  sidebar 内部からは脱出不能。
- D2（layered z）= **層間**の z-order を構造的に契約化する。将来 `LivePanel` 系の panel を footer 帯近傍に
  mount する slice や toast/notification 系のレイヤを足すときに、footer の正規 sortingOrder が
  doc 化されていないと毎回ローカルに発明することになる（findings 0045 D2 の「関係グラフは契約」思想の継承）。

`SecretModalOverlay`(1000) が既に踏襲している「各 chrome は own override-sorting Canvas」pattern に footer
だけ加わらないのは asymmetric だった、というのも理由。

## ゲート

- **headless（正本 AFK・RED→GREEN 済）**: `BackcastWorkspaceProbe`
  - **Section13** が footer を first-class chrome 層として assert に追加（amend）。
    関係：`menu > footer > sidebar > 0` / `secret > menu`。fix 前 RED 想定 = `zorder: footer sortingOrder (0)
    must be > sidebar (500) so the status bar can't be hidden by overflowing sidebar content (#84)`。
  - **Section15**（新規）= `Section15_SidebarOverflowClipping`。sidebar に rows ScrollRect が存在し、
    その viewport が RectMask2D を持ち、vertical-only であること＋ sidebar RectTransform が依然 footer
    inset を持つこと（D1 の RectMask2D と D2 の Canvas、両保証の構造を assert）。
- **owner HITL（manual-gate）**: ① `v19_morning_cell.py`（50 銘柄）を開いて footer の Replay/Manual/Auto
  と status が完全に見える、② sidebar の rows をホイールでスクロールできる、③ +Add で picker が開き、
  candidate list が overflow しても picker 内部でスクロールできる、④ ×/+Add 後に rows scroll 位置が保持される、
  ⑤ menu dropdown は依然 footer の前面に出る、⑥ menu 展開中に footer 帯をクリックして menu が閉じる。

## 参照
- `Assets/Scripts/Universe/UniverseSidebarView.cs`（D1: rows/picker scroll、Relayout 余高公式）
- `Assets/Scripts/ReplayTransport/WorkspaceFooterView.cs`（D2: `FOOTER_SORT=550` + Build で own Canvas）
- `Assets/Editor/BackcastWorkspaceProbe.cs` Section13（amended）/ Section15（new）
- findings 0045（#77 menu/sidebar uGUI 化と z-order table — 本 finding が amend）
- CONTEXT.md「chrome z-order 前面順序」（footer 順位を明文化）
