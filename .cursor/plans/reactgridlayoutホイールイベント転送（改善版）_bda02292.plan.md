---
name: ReactGridLayoutホイールイベント転送（改善版）
overview: 3Dモード時、ReactGridLayout上のマウスホイールイベントをthree.jsのOrbitControlsに転送。座標変換、セル内スクロールの考慮、親要素のoverflow制御を含む
todos:
  - id: pass-scenemanager
    content: Grid3DRendererからGrid3DLayoutRendererにsceneManagerをpropsとして渡す
    status: pending
  - id: add-scrollable-attr
    content: GridCellコンポーネントにdata-scrollable属性を追加してスクロール可能なセルを識別可能にする
    status: pending
  - id: modify-parent-overflow
    content: 3Dモード時は親要素のoverflow-autoをoverflow-hiddenに変更
    status: pending
  - id: add-wheel-handler
    content: "Grid3DLayoutRendererでReactGridLayout要素を取得し、wheelイベントリスナーを追加（passive: false）"
    status: pending
    dependencies:
      - pass-scenemanager
  - id: check-scrollable-cell
    content: wheelイベントハンドラーでスクロール可能なセル内かチェックし、該当する場合は通常のスクロールを許可
    status: pending
    dependencies:
      - add-wheel-handler
      - add-scrollable-attr
  - id: forward-wheel-event
    content: wheelイベントをpreventDefaultし、座標を変換してOrbitControlsのcanvas要素に転送
    status: pending
    dependencies:
      - add-wheel-handler
  - id: cleanup-listener
    content: useEffectのクリーンアップ関数でイベントリスナーを削除
    status: pending
    dependencies:
      - add-wheel-handler
  - id: test-wheel-zoom
    content: 3DモードでReactGridLayout上でのホイール操作がカメラズームに反映され、セル内スクロールは正常に動作することを確認
    status: pending
    dependencies:
      - forward-wheel-event
      - check-scrollable-cell
      - modify-parent-overflow
---

# ReactGridLayoutホイールイベン

トをthree.js OrbitControlsに転送（改善版）

## 概要

3DモードでReactGridLayout上でマウスホイールを操作した際、デフォルトのスクロール動作を無効化し、three.jsのOrbitControlsにイベントを転送してカメラのズーム操作を可能にする。レビュー結果を反映し、座標変換、セル内スクロールの考慮、親要素のoverflow制御を含む。

## 実装方針

1. **Grid3DRendererからGrid3DLayoutRendererにsceneManagerを渡す**

- `Grid3DRenderer`が既に`sceneManager`を持っているため、これを`Grid3DLayoutRenderer`のpropsに追加

2. **Grid3DLayoutRendererでホイールイベントを処理**

- ReactGridLayout要素をDOMクエリで取得（refは直接使えない可能性があるため）
- `useEffect`でwheelイベントリスナーを追加（`passive: false`でpreventDefault可能にする）
- スクロール可能なセル内ではイベントを転送せず、通常のスクロールを許可
- イベントを`preventDefault`してデフォルトのスクロールを無効化
- イベント座標をcanvas座標系に変換してOrbitControlsのdomElement（canvas）に転送

3. **親要素のoverflow-autoの制御**

- 3Dモード時は親要素の`overflow-auto`を`overflow-hidden`に変更するか、イベントハンドリングで制御

## 実装ファイル

### 1. `src/components/editor/renderers/grid-3d-renderer.tsx`

- `Grid3DLayoutRenderer`に`sceneManager`をpropsとして渡す

### 2. `src/components/editor/renderers/3d-layout/grid-3d-layout-renderer.tsx`

- `sceneManager`をpropsに追加
- ReactGridLayout要素をDOMクエリで取得
- `useEffect`でwheelイベントリスナーを設定
- スクロール可能なセル内の判定を追加
- イベント座標をcanvas座標系に変換
- イベントをpreventDefaultし、OrbitControlsのcanvasに転送
- 3Dモード時は親要素の`overflow-auto`を`overflow-hidden`に変更

## 実装詳細

### イベント転送の実装

```typescript
// Grid3DLayoutRenderer内
useEffect(() => {
  // 3Dモード時のみ有効化
  if (!grid3DConfig) return;
  
  const gridLayoutElement = document.querySelector('.react-grid-layout') as HTMLElement;
  if (!gridLayoutElement) return;
  
  const handleWheel = (event: WheelEvent) => {
    // イベント発生元がスクロール可能なセル内かチェック
    const target = event.target as HTMLElement;
    const scrollableCell = target.closest('[data-scrollable="true"]');
    
    if (scrollableCell) {
      // スクロール可能なセル内では通常のスクロールを許可
      return;
    }
    
    // デフォルトのスクロールを無効化
    event.preventDefault();
    
    const renderer = sceneManager.getRenderer();
    const canvas = renderer?.domElement;
    if (!canvas) return;
    
    // canvas要素の位置を取得（座標変換に必要）
    const canvasRect = canvas.getBoundingClientRect();
    
    // イベントをcanvas座標系に変換して転送
    const wheelEvent = new WheelEvent(event.type, {
      deltaX: event.deltaX,
      deltaY: event.deltaY,
      deltaZ: event.deltaZ,
      deltaMode: event.deltaMode,
      clientX: event.clientX,
      clientY: event.clientY,
      bubbles: true,
      cancelable: true,
    });
    
    canvas.dispatchEvent(wheelEvent);
  };
  
  gridLayoutElement.addEventListener('wheel', handleWheel, { passive: false });
  
  return () => {
    gridLayoutElement.removeEventListener('wheel', handleWheel);
  };
}, [grid3DConfig, sceneManager]);
```



### スクロール可能なセルの判定

GridCellコンポーネントに`data-scrollable`属性を追加：

```typescript
// GridCell内
<div
  data-scrollable={isScrollable ? "true" : "false"}
  className={cn(
    className,
    "h-full w-full p-2 overflow-x-auto",
    // ...
  )}
>
```



### 親要素のoverflow制御

3Dモード時は親要素の`overflow-auto`を`overflow-hidden`に変更：

```typescript
// 3Dモード時は親要素のスクロールを無効化
<div className={cn(
  "grow",
  grid3DConfig ? "overflow-hidden" : "overflow-auto",
  "transparent-when-disconnected"
)}>
```



## 注意事項