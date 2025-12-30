/* Copyright 2026 Marimo. All rights reserved. */
import { atom, useAtomValue } from "jotai";
import { isStaticNotebook } from "@/core/static/static-state";
import { store } from "../state/jotai";
import { RuntimeManager } from "./runtime";
import type { RuntimeConfig } from "./types";

function getBaseURI(): string {
  // In development, use the backend server URL from environment variable or default
  // The backend server runs on http://127.0.0.1:2718 by default (see server/run.py)
  if (import.meta.env.DEV) {
    const backendURL = import.meta.env.VITE_BACKEND_URL || "http://127.0.0.1:2718";
    // #region agent log
    fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'config.ts:getBaseURI',message:'getBaseURI called (dev mode)',data:{backendURL,envBackendURL:import.meta.env.VITE_BACKEND_URL},timestamp:Date.now(),sessionId:'debug-session',runId:'post-fix',hypothesisId:'A'})}).catch(()=>{});
    // #endregion
    return backendURL;
  }
  
  // In production, use the current page's base URI
  const url = new URL(document.baseURI);
  url.search = "";
  url.hash = "";
  const baseURI = url.toString();
  // #region agent log
  fetch('http://127.0.0.1:7244/ingest/b3cb3916-18b2-4b82-87da-2ae197889a79',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({location:'config.ts:getBaseURI',message:'getBaseURI called (production)',data:{baseURI,windowLocation:window.location.href},timestamp:Date.now(),sessionId:'debug-session',runId:'post-fix',hypothesisId:'A'})}).catch(()=>{});
  // #endregion
  return baseURI;
}

export const DEFAULT_RUNTIME_CONFIG: RuntimeConfig = {
  url: getBaseURI(),
};

export const runtimeConfigAtom = atom<RuntimeConfig>(DEFAULT_RUNTIME_CONFIG);
const runtimeManagerAtom = atom<RuntimeManager>((get) => {
  const config = get(runtimeConfigAtom);
  // "lazy" means that the runtime manager will not attempt to connect to a
  // server, which in the case of a static notebook, will not be available.
  const lazy = isStaticNotebook();
  return new RuntimeManager(config, lazy);
});

export function useRuntimeManager(): RuntimeManager {
  return useAtomValue(runtimeManagerAtom);
}

/**
 * Prefer to use useRuntimeManager instead of this function.
 */
export function getRuntimeManager(): RuntimeManager {
  return store.get(runtimeManagerAtom);
}

export function asRemoteURL(path: string): URL {
  if (path.startsWith("http")) {
    return new URL(path);
  }
  let base = getRuntimeManager().httpURL.toString();
  if (base.startsWith("blob:")) {
    // Remove leading blob:
    base = base.replace("blob:", "");
  }
  return new URL(path, base);
}
