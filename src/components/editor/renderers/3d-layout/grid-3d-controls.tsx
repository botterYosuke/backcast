/* Copyright 2026 Marimo. All rights reserved. */
import React from "react";
import { BorderAllIcon } from "@radix-ui/react-icons";
import { LockIcon } from "lucide-react";
import { Label } from "@/components/ui/label";
import { NumberField } from "@/components/ui/number-field";
import { Switch } from "@/components/ui/switch";
import type { Grid3DConfig } from "./types";

interface Grid3DControlsProps {
  config: Grid3DConfig;
  setConfig: (config: Grid3DConfig) => void;
}

export const Grid3DControls: React.FC<Grid3DControlsProps> = ({
  config,
  setConfig,
}) => {
  return (
    <div className="flex flex-row absolute left-0 right-[350px] top-8 gap-4 px-5 pb-3 border-b z-50 overflow-x-auto">
      {/* 既存の設定項目 */}
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="columns">Columns</Label>
        <NumberField
          data-testid="grid-3d-columns-input"
          id="columns"
          value={config.columns}
          className="w-[60px]"
          placeholder="# of Columns"
          minValue={1}
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              columns: valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="rows">Rows</Label>
        <NumberField
          data-testid="grid-3d-rows-input"
          id="rows"
          value={config.rows}
          className="w-[60px]"
          placeholder="# of Rows"
          minValue={1}
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              rows: Number.isNaN(valueAsNumber) ? undefined : valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="rowHeight">Row Height (px)</Label>
        <NumberField
          data-testid="grid-3d-row-height-input"
          id="rowHeight"
          value={config.rowHeight}
          className="w-[60px]"
          placeholder="Row Height (px)"
          minValue={1}
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              rowHeight: valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="maxWidth">Max Width (px)</Label>
        <NumberField
          data-testid="grid-3d-max-width-input"
          id="maxWidth"
          value={config.maxWidth}
          className="w-[90px]"
          step={100}
          placeholder="Full"
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              maxWidth: Number.isNaN(valueAsNumber) ? undefined : valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label className="flex flex-row items-center gap-1" htmlFor="bordered">
          <BorderAllIcon className="h-3 w-3" />
          Bordered
        </Label>
        <Switch
          data-testid="grid-3d-bordered-switch"
          id="bordered"
          checked={config.bordered}
          size="sm"
          onCheckedChange={(bordered) => {
            setConfig({
              ...config,
              bordered,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label className="flex flex-row items-center gap-1" htmlFor="lock">
          <LockIcon className="h-3 w-3" />
          Lock Grid
        </Label>
        <Switch
          data-testid="grid-3d-lock-switch"
          id="lock"
          checked={config.isLocked}
          size="sm"
          onCheckedChange={(isLocked) => {
            setConfig({
              ...config,
              isLocked,
            });
          }}
        />
      </div>

      {/* 3Dモード専用の設定項目 */}
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="spacingX">Grid Spacing X</Label>
        <NumberField
          data-testid="grid-3d-spacing-x-input"
          id="spacingX"
          value={config.spacingX}
          className="w-[80px]"
          placeholder="Spacing X"
          minValue={1}
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              spacingX: valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="spacingZ">Grid Depth</Label>
        <NumberField
          data-testid="grid-3d-spacing-z-input"
          id="spacingZ"
          value={config.spacingZ}
          className="w-[80px]"
          placeholder="Spacing Z"
          minValue={1}
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              spacingZ: valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="spacingY">Grid Height</Label>
        <NumberField
          data-testid="grid-3d-spacing-y-input"
          id="spacingY"
          value={config.spacingY}
          className="w-[80px]"
          placeholder="Spacing Y"
          minValue={0}
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              spacingY: valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="gridOpacity">Grid Opacity</Label>
        <NumberField
          data-testid="grid-3d-opacity-input"
          id="gridOpacity"
          value={config.gridOpacity}
          className="w-[80px]"
          placeholder="Opacity"
          minValue={0}
          maxValue={1}
          step={0.1}
          onChange={(valueAsNumber) => {
            setConfig({
              ...config,
              gridOpacity: valueAsNumber,
            });
          }}
        />
      </div>
      <div className="flex flex-row items-center gap-2">
        <Label htmlFor="snapToGrid">Snap to Grid</Label>
        <Switch
          data-testid="grid-3d-snap-switch"
          id="snapToGrid"
          checked={config.snapToGrid}
          size="sm"
          onCheckedChange={(snapToGrid) => {
            setConfig({
              ...config,
              snapToGrid,
            });
          }}
        />
      </div>
    </div>
  );
};

