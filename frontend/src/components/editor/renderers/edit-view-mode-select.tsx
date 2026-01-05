/* Copyright 2026 Marimo. All rights reserved. */

import { BoxIcon, ListIcon } from "lucide-react";
import type React from "react";
import { useAtomValue, useSetAtom } from "jotai";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { is3DModeAtom } from "@/core/mode";

export type EditViewMode = "vertical" | "3d";

const EDIT_VIEW_MODES: EditViewMode[] = ["vertical", "3d"];

export const EditViewModeSelect: React.FC = () => {
  const is3DMode = useAtomValue(is3DModeAtom);
  const setIs3DMode = useSetAtom(is3DModeAtom);

  const currentMode: EditViewMode = is3DMode ? "3d" : "vertical";

  return (
    <Select
      data-testid="edit-view-mode-select"
      value={currentMode}
      onValueChange={(v) => {
        setIs3DMode(v === "3d");
      }}
    >
      <SelectTrigger
        className="min-w-[110px] border-border bg-background"
        data-testid="edit-view-mode-select"
      >
        <SelectValue placeholder="Select a view" />
      </SelectTrigger>
      <SelectContent>
        <SelectGroup>
          <SelectLabel>View as</SelectLabel>
          {EDIT_VIEW_MODES.map((mode) => (
            <SelectItem key={mode} value={mode}>
              <div className="flex items-center gap-1.5 leading-5">
                {renderIcon(mode)}
                <span>{displayModeName(mode)}</span>
              </div>
            </SelectItem>
          ))}
        </SelectGroup>
      </SelectContent>
    </Select>
  );
};

function renderIcon(mode: EditViewMode) {
  const Icon = getModeIcon(mode);
  return <Icon className="h-4 w-4" />;
}

function getModeIcon(mode: EditViewMode) {
  switch (mode) {
    case "vertical":
      return ListIcon;
    case "3d":
      return BoxIcon;
    default:
      return ListIcon;
  }
}

function displayModeName(mode: EditViewMode): string {
  switch (mode) {
    case "vertical":
      return "Vertical";
    case "3d":
      return "3D";
    default:
      return mode;
  }
}

