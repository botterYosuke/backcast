/* Copyright 2026 Marimo. All rights reserved. */
import React, {
  memo,
  type PropsWithChildren,
  useEffect,
  useMemo,
  useState,
} from "react";
import { Responsive, WidthProvider } from "react-grid-layout";
import { OutputArea } from "@/components/editor/Output";
import type { CellRuntimeState } from "@/core/cells/types";
import type { ICellRendererProps } from "../types";
import type { GridLayout, GridLayoutCellSide } from "../grid-layout/types";
import type { Grid3DConfig } from "./types";
import type { SceneManager } from "@/core/three/scene-manager";

import "react-grid-layout/css/styles.css";
import "../grid-layout/styles.css";
import { startCase } from "lodash-es";
import {
  AlignEndVerticalIcon,
  AlignHorizontalSpaceAroundIcon,
  AlignStartVerticalIcon,
  CheckIcon,
  GripHorizontalIcon,
  ScrollIcon,
  XIcon,
} from "lucide-react";
import { TinyCode } from "@/components/editor/cell/TinyCode";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Switch } from "@/components/ui/switch";
import { outputIsLoading } from "@/core/cells/cell";
import type { CellId } from "@/core/cells/ids";
import type { AppMode } from "@/core/mode";
import { useIsDragging } from "@/hooks/useIsDragging";
import { cn } from "@/utils/cn";
import { Maps } from "@/utils/maps";
import { Objects } from "@/utils/objects";

type Props = ICellRendererProps<GridLayout> & {
  grid3DConfig?: Grid3DConfig;
  sceneManager?: SceneManager;
};

const ReactGridLayout = WidthProvider(Responsive);

const MARGIN: [number, number] = [0, 0];

const DRAG_HANDLE = "grid-drag-handle";

/**
 * Grid3DLayoutRenderer
 *
 * 3Dモード専用のグリッドレイアウトレンダラー
 * - GridLayoutRendererをベースに、GridControlsを除外
 * - 3DモードではGrid3DControlsがedit-app.tsxで別途表示されるため、ここでは表示しない
 */
export const Grid3DLayoutRenderer: React.FC<Props> = ({
  layout,
  setLayout,
  cells,
  mode,
  grid3DConfig,
  sceneManager,
}) => {
  const isReading = mode === "read";
  const inGridIds = new Set(layout.cells.map((cell) => cell.i));
  const [droppingItem, setDroppingItem] = useState<{
    i: string;
    w?: number;
    h?: number;
  } | null>(null);
  // isLockedはenableInteractionsの計算で使用されるため保持
  // ただし、GridControlsを削除したため、現時点では常にfalseのまま
  // 将来的にGrid3DControlsに「Lock Grid」機能を追加する可能性がある
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const [isLocked] = useState(false);

  const cols = useMemo(
    () => ({
      // we only allow 1 responsive breakpoint
      // we can change this later if we want to support more,
      // but this increases complexity to the user
      lg: layout.columns,
    }),
    [layout.columns],
  );

  // Add class to update the background of the app
  useEffect(() => {
    const appEl = document.getElementById("App");
    if (layout.bordered) {
      appEl?.classList.add("grid-bordered");
    } else {
      appEl?.classList.remove("grid-bordered");
    }

    return () => {
      appEl?.classList.remove("grid-bordered");
    };
  }, [layout.bordered]);

  const { isDragging, ...dragProps } = useIsDragging();

  const enableInteractions = !isReading && !isLocked;
  const layoutByCellId = Maps.keyBy(layout.cells, (cell) => cell.i);

  // スケール変更を監視して、react-grid-layout要素のDOMサイズを調整
  useEffect(() => {
    let lastScale: number | null = null;

    const updateGridLayoutSize = () => {
      // grid-3d-container要素を取得
      const gridContainer = document.querySelector('.grid-3d-container') as HTMLElement;
      if (!gridContainer) {
        return;
      }

      // react-grid-layout要素を取得
      const reactGridLayoutElement = gridContainer.querySelector('.react-grid-layout') as HTMLElement;
      if (!reactGridLayoutElement) {
        return;
      }

      // スケール値を取得
      const transform = gridContainer.style.transform || '';
      const scaleMatch = transform.match(/scale\(([^)]+)\)/);
      const scale = scaleMatch?.[1] ? parseFloat(scaleMatch[1].trim()) : 1.0;

      // スケール値が変わっていない場合はスキップ（初回を除く）
      if (lastScale !== null && scale === lastScale) {
        return;
      }
      lastScale = scale;

      if (scale <= 0 || Number.isNaN(scale) || scale === 1.0) {
        // スケールが1.0の場合は、サイズをリセット
        reactGridLayoutElement.style.width = '';
        reactGridLayoutElement.style.height = '';
        return;
      }

      // 現在の見た目サイズを取得
      const rect = reactGridLayoutElement.getBoundingClientRect();
      const currentVisualWidth = rect.width;
      const currentVisualHeight = rect.height;

      // DOMサイズを計算（見た目サイズ / スケール）
      const domWidth = currentVisualWidth / scale;
      const domHeight = currentVisualHeight / scale;

      // DOMサイズを設定
      reactGridLayoutElement.style.width = `${domWidth}px`;
      reactGridLayoutElement.style.height = `${domHeight}px`;
    };

    // 初回実行を少し遅らせる（DOM要素が確実に存在するように）
    const timeoutId = setTimeout(updateGridLayoutSize, 0);

    // 定期的にチェック（スケール変更を検出）
    const intervalId = setInterval(updateGridLayoutSize, 100);

    return () => {
      clearTimeout(timeoutId);
      clearInterval(intervalId);
    };
  }, []);

  // ReactGridLayout上のホイールイベントをthree.js OrbitControlsに転送
  useEffect(() => {
    // 3Dモード時のみ有効化
    if (!grid3DConfig || !sceneManager) {
      return;
    }

    // 共通のホイールイベントハンドラーを作成
    const createWheelHandler = (sceneManager: SceneManager) => {
      return (event: WheelEvent) => {
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
        if (!canvas) {
          return;
        }

        // イベントをcanvas要素に転送（clientX/Yは既にビューポート座標なのでそのまま使用可能）
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
    };

    // grid-3d-containerとreact-grid-layout要素が見つかるまで待つ
    let cleanupFn: (() => void) | null = null;
    let isSetup = false;

    const setupWheelHandler = (): boolean => {
      if (isSetup) return true;

      // grid-3d-container内のreact-grid-layoutを取得
      const gridContainer = document.querySelector('.grid-3d-container');
      if (!gridContainer) {
        return false;
      }

      const gridLayoutElement = gridContainer.querySelector('.react-grid-layout') as HTMLElement;
      if (!gridLayoutElement) {
        return false;
      }

      const cleanupFns: (() => void)[] = [];
      const wheelHandler = createWheelHandler(sceneManager);

      // ReactGridLayoutにイベントリスナーを追加
      gridLayoutElement.addEventListener('wheel', wheelHandler, { passive: false });
      cleanupFns.push(() => {
        gridLayoutElement.removeEventListener('wheel', wheelHandler);
      });

      // サイドバー要素にイベントリスナーを追加
      const sidebarElement = gridContainer.querySelector('[data-sidebar="outputs"]') as HTMLElement;
      if (sidebarElement) {
        sidebarElement.addEventListener('wheel', wheelHandler, { passive: false });
        cleanupFns.push(() => {
          sidebarElement.removeEventListener('wheel', wheelHandler);
        });
      }

      cleanupFn = () => {
        for (const cleanup of cleanupFns) {
          cleanup();
        }
      };
      isSetup = true;
      return true;
    };

    // 複数の方法で要素を見つける試行
    let retryCount = 0;
    const maxRetries = 200; // 約10秒（50ms * 200）
    let intervalId: ReturnType<typeof setInterval> | null = null;
    let rafId: number | null = null;
    let rafRetryCount = 0;
    const maxRafRetries = 300; // 約5秒（60fps * 5秒 = 300フレーム）

    const trySetup = () => {
      if (setupWheelHandler()) {
        if (intervalId) {
          clearInterval(intervalId);
          intervalId = null;
        }
        if (rafId !== null) {
          cancelAnimationFrame(rafId);
          rafId = null;
        }
        return true;
      }
      return false;
    };

    // 初回試行（即座に）
    if (trySetup()) {
      return () => {
        if (cleanupFn) {
          cleanupFn();
        }
      };
    }

    // requestAnimationFrameで再試行（次のフレームで、最大300フレーム）
    const tryWithRAF = () => {
      rafRetryCount++;
      if (rafRetryCount >= maxRafRetries) {
        // 最大試行回数に達したら停止
        return;
      }
      rafId = requestAnimationFrame(() => {
        if (!trySetup()) {
          // まだ見つからない場合、次のフレームで再試行
          tryWithRAF();
        }
      });
    };
    tryWithRAF();

    // 定期的な再試行（50msごと、最大10秒）
    intervalId = setInterval(() => {
      retryCount++;
      if (trySetup() || retryCount >= maxRetries) {
        if (intervalId) {
          clearInterval(intervalId);
          intervalId = null;
        }
      }
    }, 50);

    // MutationObserverで監視（要素が後から追加される場合に備える）
    const observer = new MutationObserver(() => {
      if (trySetup()) {
        observer.disconnect();
      }
    });

    // DOM全体を監視（grid-3d-containerが追加されるのを待つ）
    observer.observe(document.body, {
      childList: true,
      subtree: true,
    });

    // タイムアウトも設定（最大10秒待つ）
    const timeoutId = setTimeout(() => {
      observer.disconnect();
      if (intervalId) {
        clearInterval(intervalId);
        intervalId = null;
      }
      if (rafId !== null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
    }, 10000);

    return () => {
      observer.disconnect();
      clearTimeout(timeoutId);
      if (intervalId) {
        clearInterval(intervalId);
        intervalId = null;
      }
      if (rafId !== null) {
        cancelAnimationFrame(rafId);
        rafId = null;
      }
      if (cleanupFn) {
        cleanupFn();
      }
    };
  }, [grid3DConfig, sceneManager]);

  const handleMakeScrollable = (cellId: CellId) => (isScrollable: boolean) => {
    const scrollableCells = new Set(layout.scrollableCells);
    if (isScrollable) {
      scrollableCells.add(cellId);
    } else {
      scrollableCells.delete(cellId);
    }
    setLayout({
      ...layout,
      scrollableCells: scrollableCells,
    });
  };

  const handleSetSide = (cellId: CellId) => (side: GridLayoutCellSide) => {
    const cellSide = new Map(layout.cellSide);
    if (side === cellSide.get(cellId)) {
      cellSide.delete(cellId);
    } else {
      cellSide.set(cellId, side);
    }
    setLayout({
      ...layout,
      cellSide: cellSide,
    });
  };

  const styles: React.CSSProperties = {};
  // Max width styles
  if (layout.maxWidth) {
    styles.maxWidth = `${layout.maxWidth}px`;
  }
  // Height styles (based on rows setting - fixed height, not maxHeight)
  if (grid3DConfig?.rows !== undefined) {
    const height = grid3DConfig.rows * layout.rowHeight;
    styles.height = `${height}px`;
    styles.overflowY = "hidden";
  }
  // Editing background styles
  if (enableInteractions) {
    styles.backgroundImage =
      "repeating-linear-gradient(var(--gray-4) 0 1px, transparent 1px 100%), repeating-linear-gradient(90deg, var(--gray-4) 0 1px, transparent 1px 100%)";
    styles.backgroundSize = `calc((100% / ${layout.columns})) ${layout.rowHeight}px`;
  }

  let grid = (
    <ReactGridLayout
      breakpoint="lg"
      layouts={{
        lg: layout.cells,
      }}
      style={styles}
      cols={cols}
      {...(grid3DConfig?.rows !== undefined && { maxRows: grid3DConfig.rows })}
      allowOverlap={false}
      className={cn(
        "w-full mx-auto bg-background flex-1 min-h-full",
        // Show grid border and background when editing
        enableInteractions && "bg-(--slate-2) border-r",
        // Disable animations and add padding when reading
        isReading && "disable-animation",
        !layout.maxWidth && "min-w-[800px]",
      )}
      // Add additional padding if bordered when reading
      containerPadding={isReading ? [20, 20] : undefined}
      margin={MARGIN}
      isBounded={false}
      compactType={null}
      preventCollision={true}
      rowHeight={layout.rowHeight}
      onLayoutChange={(cellLayouts) => {
        // Clamp cells to maxRows if rows is set
        let adjustedCells = cellLayouts;
        if (grid3DConfig?.rows !== undefined) {
          adjustedCells = cellLayouts.map(cell => {
            const maxY = grid3DConfig.rows! - 1;
            const newY = Math.min(cell.y, maxY);
            const newH = Math.min(cell.h, grid3DConfig.rows! - newY);
            if (newY !== cell.y || newH !== cell.h) {
              return { ...cell, y: newY, h: newH };
            }
            return cell;
          });
        }
        
        setLayout({
          ...layout,
          cells: adjustedCells,
        });
      }}
      droppingItem={
        droppingItem
          ? {
              i: droppingItem.i,
              w: droppingItem.w || 2,
              h: droppingItem.h || 2,
            }
          : undefined
      }
      onDrop={(cellLayouts, dropped, _event) => {
        dragProps.onDragStop();
        if (!dropped) {
          return;
        }
        setLayout({
          ...layout,
          cells: [...cellLayouts, dropped],
        });
      }}
      onDragStart={(_layout, _oldItem, _newItem, _placeholder, event) => {
        dragProps.onDragStart(event);
      }}
      onDrag={(_layout, _oldItem, _newItem, _placeholder, event) => {
        dragProps.onDragMove(event);
      }}
      onDragStop={() => {
        dragProps.onDragStop();
      }}
      onResizeStop={() => {
        // Dispatch a resize event so widgets know to resize
        window.dispatchEvent(new Event("resize"));
      }}
      // When in read mode or locked, disable dragging and resizing
      isDraggable={enableInteractions}
      isDroppable={enableInteractions}
      isResizable={enableInteractions}
      draggableHandle={enableInteractions ? `.${DRAG_HANDLE}` : "noop"}
    >
      {cells
        .filter((cell) => inGridIds.has(cell.id))
        .map((cell) => {
          const cellLayout = layoutByCellId.get(cell.id);
          const isScrollable = layout.scrollableCells.has(cell.id) ?? false;
          const side = layout.cellSide.get(cell.id);
          const gridCell = (
            <GridCell
              code={cell.code}
              mode={mode}
              cellId={cell.id}
              output={cell.output}
              status={cell.status}
              isScrollable={isScrollable}
              side={side}
              hidden={cell.errored || cell.interrupted || cell.stopped}
            />
          );

          if (enableInteractions) {
            return (
              <EditableGridCell
                key={cell.id}
                id={cell.id}
                isDragging={isDragging}
                side={side}
                setSide={handleSetSide(cell.id)}
                isScrollable={isScrollable}
                setIsScrollable={handleMakeScrollable(cell.id)}
                display={cellLayout?.y === 0 ? "bottom" : "top"}
                onDelete={() => {
                  setLayout({
                    ...layout,
                    cells: layout.cells.filter((c) => c.i !== cell.id),
                  });
                }}
              >
                {gridCell}
              </EditableGridCell>
            );
          }

          return <div key={cell.id}>{gridCell}</div>;
        })}
    </ReactGridLayout>
  );

  const notInGrid = cells.filter((cell) => !inGridIds.has(cell.id));

  if (isReading) {
    if (layout.bordered) {
      grid = (
        <div className="flex flex-1 flex-col items-center">
          <div
            style={styles}
            className="bg-background flex-1 border rounded shadow-sm w-full overflow-hidden"
          >
            {grid}
          </div>
        </div>
      );
    }

    const sidebarCells = notInGrid.filter((cell) => isSidebarCell(cell));

    return (
      <>
        {grid}
        {/* Render sidebar outputs even if they are not in grid (hidden) */}
        <div className="hidden">
          {sidebarCells.map((cell) => {
            return (
              <GridCell
                key={cell.id}
                code={cell.code}
                mode={mode}
                cellId={cell.id}
                output={cell.output}
                status={cell.status}
                isScrollable={false}
                hidden={false}
              />
            );
          })}
        </div>
      </>
    );
  }

  if (layout.bordered) {
    grid = (
      <div
        style={styles}
        className="bg-background border rounded shadow-sm w-full mx-auto mt-4 h-[calc(100%-1rem)] overflow-hidden"
      >
        <div className={cn("h-full", grid3DConfig ? "overflow-hidden" : "overflow-auto")}>
          {grid}
        </div>
      </div>
    );
  }

  // GridControlsを除外して直接divを返す
  return (
    <div className={cn("relative flex z-10 flex-1 overflow-hidden")}>
      <div className={cn(
        "grow",
        grid3DConfig ? "overflow-hidden" : "overflow-auto",
        "transparent-when-disconnected"
      )}>
        {grid}
      </div>
      <div 
        data-sidebar="outputs"
        className="flex-none flex flex-col w-[300px] p-2 pb-20 gap-2 overflow-auto bg-(--slate-2) border-t border-x rounded-t shadow-sm transparent-when-disconnected mx-2 mt-4"
      >
        <div className="text font-bold text-(--slate-20) shrink-0">
          Outputs
        </div>
        {notInGrid.map((cell) => (
          <div
            key={cell.id}
            draggable={true}
            // eslint-disable-next-line react/no-unknown-property
            unselectable="on"
            data-cell-id={cell.id}
            // Firefox requires some kind of initialization which we can do by adding this attribute
            // @see https://bugzilla.mozilla.org/show_bug.cgi?id=568313
            onDragStart={(e) => {
              // get height of self
              const height = e.currentTarget.offsetHeight;

              setDroppingItem({
                i: cell.id,
                w: layout.columns / 4,
                h: Math.ceil(height / layout.rowHeight) || 1,
              });
              e.dataTransfer.setData("text/plain", "");
            }}
            className={cn(
              DRAG_HANDLE,
              "droppable-element bg-background border-border border overflow-hidden p-2 rounded shrink-0",
            )}
          >
            <GridCell
              code={cell.code}
              className="select-none pointer-events-none"
              mode={mode}
              cellId={cell.id}
              output={cell.output}
              isScrollable={false}
              status={cell.status}
              hidden={false}
            />
          </div>
        ))}
      </div>
    </div>
  );
};

interface GridCellProps extends Pick<CellRuntimeState, "output" | "status"> {
  className?: string;
  code: string;
  cellId: CellId;
  mode: AppMode;
  hidden: boolean;
  isScrollable: boolean;
  side?: GridLayoutCellSide;
}

const GridCell = memo(
  ({
    output,
    cellId,
    status,
    mode,
    code,
    hidden,
    isScrollable,
    side,
    className,
  }: GridCellProps) => {
    const loading = outputIsLoading(status);

    const isOutputEmpty = output == null || output.data === "";
    // If not reading, show code when there is no output
    if (isOutputEmpty && mode !== "read") {
      return <TinyCode className={className} code={code} />;
    }

    return (
      <div
        data-scrollable={isScrollable ? "true" : "false"}
        className={cn(
          className,
          "h-full w-full p-2 overflow-x-auto",
          hidden && "invisible",
          isScrollable ? "overflow-y-auto" : "overflow-y-hidden",
          side === "top" && "flex items-start",
          side === "bottom" && "flex items-end",
          side === "left" && "flex justify-start",
          side === "right" && "flex justify-end",
        )}
      >
        <OutputArea
          allowExpand={false}
          output={output}
          cellId={cellId}
          stale={loading}
          loading={loading}
        />
      </div>
    );
  },
);
GridCell.displayName = "GridCell";

const EditableGridCell = React.forwardRef(
  (
    {
      children,
      isDragging,
      className,
      onDelete,
      isScrollable,
      setIsScrollable,
      side,
      setSide,
      display,
      ...rest
    }: PropsWithChildren<{
      id: CellId;
      className?: string;
      isDragging: boolean;

      onDelete: () => void;

      isScrollable: boolean;
      setIsScrollable: (isScrollable: boolean) => void;

      side?: GridLayoutCellSide;
      setSide: (side: GridLayoutCellSide) => void;

      display: "top" | "bottom";
    }>,
    ref: React.Ref<HTMLDivElement>,
  ) => {
    const [popoverOpened, setPopoverOpened] = useState<"side" | "scroll">();

    return (
      <div
        ref={ref}
        {...rest}
        className={cn(
          className,
          "relative z-10 hover:z-20",
          "bg-background border-transparent hover:border-(--sky-8) border",
          popoverOpened && "border-(--sky-8) z-20",
          !popoverOpened && "hover-actions-parent",
          isDragging && "bg-(--slate-2) border-border z-20",
        )}
      >
        {children}
        <GridHoverActions
          onDelete={onDelete}
          isScrollable={isScrollable}
          setIsScrollable={setIsScrollable}
          side={side}
          setSide={setSide}
          display={display}
          setPopoverOpened={setPopoverOpened}
          popoverOpened={popoverOpened}
        />
      </div>
    );
  },
);
EditableGridCell.displayName = "EditableGridCell";

interface GridHoverActionsProps {
  onDelete: () => void;

  isScrollable: boolean;
  setIsScrollable: (isScrollable: boolean) => void;

  side?: GridLayoutCellSide;
  setSide: (side: GridLayoutCellSide) => void;

  display: "top" | "bottom";

  popoverOpened: "side" | "scroll" | undefined;
  setPopoverOpened: (popoverOpened: "side" | "scroll" | undefined) => void;
}

const GridHoverActions: React.FC<GridHoverActionsProps> = ({
  display,
  onDelete,
  side,
  setSide,
  isScrollable,
  setIsScrollable,
  popoverOpened,
  setPopoverOpened,
}) => {
  const buttonClassName = "h-4 w-4 opacity-60 hover:opacity-100";
  const SideIcon =
    side === "left"
      ? AlignStartVerticalIcon
      : side === "right"
        ? AlignEndVerticalIcon
        : undefined;

  const handleButtonClick = (_event: React.MouseEvent, _buttonType: string) => {
    // Button click handler - no-op for now
  };

  return (
    <div
      className={cn(
        "absolute right-0 p-1 bg-(--sky-8) text-white h-6 z-10 flex gap-2",
        !popoverOpened && "hover-action",
        display === "top" && "-top-6 rounded-t",
        display === "bottom" && "-bottom-6 rounded-b",
      )}
    >
      <DropdownMenu
        open={popoverOpened === "side"}
        onOpenChange={(open) => setPopoverOpened(open ? "side" : undefined)}
      >
        <DropdownMenuTrigger asChild={true}>
          {SideIcon ? (
            <SideIcon className={buttonClassName} />
          ) : (
            <AlignHorizontalSpaceAroundIcon className={buttonClassName} />
          )}
        </DropdownMenuTrigger>
        <DropdownMenuContent side="bottom">
          {Objects.entries(SIDE_TO_ICON).map(([option, Icon]) => (
            <DropdownMenuItem key={option} onSelect={() => setSide(option)}>
              <Icon className={"h-4 w-3 mr-2"} />
              <span className="flex-1">{startCase(option)}</span>
              {option === side && <CheckIcon className="h-4 w-4" />}
            </DropdownMenuItem>
          ))}
        </DropdownMenuContent>
      </DropdownMenu>

      <DropdownMenu
        open={popoverOpened === "scroll"}
        onOpenChange={(open) => setPopoverOpened(open ? "scroll" : undefined)}
      >
        <DropdownMenuTrigger asChild={true}>
          <ScrollIcon className={buttonClassName} />
        </DropdownMenuTrigger>
        <DropdownMenuContent side="bottom">
          <DropdownMenuItem onSelect={() => setIsScrollable(!isScrollable)}>
            <span className="flex-1">Scrollable</span>
            <Switch
              data-testid="grid-scrollable-switch"
              checked={isScrollable}
              size="sm"
              onCheckedChange={setIsScrollable}
            />
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>

      <GripHorizontalIcon
        className={cn(DRAG_HANDLE, "cursor-move", buttonClassName)}
      />
      <XIcon className={buttonClassName} onClick={(e) => { handleButtonClick(e, 'delete'); onDelete(); }} />
    </div>
  );
};

function isSidebarCell(cell: CellRuntimeState) {
  // False-positives are ok here because we rendering these cells in a hidden div
  return (
    typeof cell.output?.data === "string" &&
    cell.output.data.includes("marimo-sidebar")
  );
}

const SIDE_TO_ICON = {
  // We are only showing horizontal sides for now
  // top: AlignHorizontalSpaceAroundIcon,
  // bottom: AlignHorizontalSpaceAroundIcon,
  left: AlignStartVerticalIcon,
  right: AlignEndVerticalIcon,
};

