/* Copyright 2026 Marimo. All rights reserved. */

import { mount } from "@/mount";

declare global {
  interface Window {
    __MARIMO_MOUNT_CONFIG__: unknown;
  }
}

// eslint-disable-next-line ssr-friendly/no-dom-globals-in-module-scope
const el = document.getElementById("root");
if (el) {
  if (!window.__MARIMO_MOUNT_CONFIG__) {
    throw new Error("[marimo] mount config not found");
  }
  // Parse JSON string if it's a string
  const config =
    typeof window.__MARIMO_MOUNT_CONFIG__ === "string"
      ? JSON.parse(window.__MARIMO_MOUNT_CONFIG__)
      : window.__MARIMO_MOUNT_CONFIG__;
  mount(config, el);
} else {
  throw new Error("[marimo] root element not found");
}
