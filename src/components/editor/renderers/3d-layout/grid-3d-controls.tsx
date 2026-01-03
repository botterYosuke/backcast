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
    <div className="flex flex-row absolute pl-5 top-8 gap-4 w-full justify-end pr-[350px] pb-3 border-b z-50 pointer-events-none">
      {/* 既存の設定項目 */}
      <div className="flex flex-row items-center gap-2 pointer-events-auto">
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
      <div className="flex flex-row items-center gap-2 pointer-events-auto">
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
      <div className="flex flex-row items-center gap-2 pointer-events-auto">
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
      <div className="flex flex-row items-center gap-2 pointer-events-auto">
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
      <div className="flex flex-row items-center gap-2 pointer-events-auto">
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
      <div className="flex flex-row items-center gap-2 pointer-events-auto">
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
    </div>
  );
};

