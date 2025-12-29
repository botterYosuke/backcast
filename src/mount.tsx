/* Copyright 2026 Marimo. All rights reserved. */

import type * as api from "@marimo-team/marimo-api";
import { Provider } from "jotai";
import { createRoot } from "react-dom/client";
import { z } from "zod";
import {
  appConfigAtom,
  configOverridesAtom,
  userConfigAtom,
} from "@/core/config/config";
import { KnownQueryParams } from "@/core/constants";
import { getFilenameFromDOM } from "@/core/dom/htmlUtils";
import { getMarimoCode } from "@/core/meta/globals";
import {
  marimoVersionAtom,
  serverTokenAtom,
  showCodeInRunModeAtom,
} from "@/core/meta/state";
import { Logger } from "@/utils/Logger";
import { ErrorBoundary } from "./components/editor/boundary/ErrorBoundary";
import { notebookAtom } from "./core/cells/cells";
import { notebookStateFromSession } from "./core/cells/session";
import {
  parseAppConfig,
  parseConfigOverrides,
  parseUserConfig,
} from "./core/config/config-schema";
import { MarimoApp, preloadPage } from "./core/MarimoApp";
import { type AppMode, initialModeAtom, viewStateAtom } from "./core/mode";
import { cleanupAuthQueryParams } from "./core/network/auth";
import { requestClientAtom } from "./core/network/requests";
import { resolveRequestClient } from "./core/network/resolve";
import {
  DEFAULT_RUNTIME_CONFIG,
  runtimeConfigAtom,
} from "./core/runtime/config";
import { codeAtom, filenameAtom } from "./core/saving/file-state";
import { store } from "./core/state/jotai";
import { patchFetch, patchVegaLoader } from "./core/static/files";
import { isStaticNotebook } from "./core/static/static-state";
import { maybeRegisterVSCodeBindings } from "./core/vscode/vscode-bindings";
import type { FileStore } from "./core/wasm/store";
import { notebookFileStore } from "./core/wasm/store";
import { vegaLoader } from "./plugins/impl/vega/loader";
import { initializePlugins } from "./plugins/plugins";
import { ThemeProvider } from "./theme/ThemeProvider";
import { reportVitals } from "./utils/vitals";

let hasMounted = false;

/**
 * Main entry point for the mairmo app.
 *
 * Sets up the mairmo app with a theme provider.
 */
export function mount(options: unknown, el: Element): Error | undefined {
  if (hasMounted) {
    Logger.warn("marimo app has already been mounted.");
    return new Error("marimo app has already been mounted.");
  }

  hasMounted = true;

  const root = createRoot(el);

  try {
    // Init side-effects
    maybeRegisterVSCodeBindings();
    initializePlugins();
    cleanupAuthQueryParams();

    // Patches
    if (isStaticNotebook()) {
      // If we're in static mode, we need to patch fetch to use the virtual file
      patchFetch();
      patchVegaLoader(vegaLoader);
    }

    // Init store
    initStore(options);

    root.render(
      <Provider store={store}>
        <ThemeProvider>
          <MarimoApp />
        </ThemeProvider>
      </Provider>,
    );
  } catch (error) {
    // Most likely, configuration failed to parse.
    const Throw = () => {
      throw error;
    };
    root.render(
      <ErrorBoundary>
        <Throw />
      </ErrorBoundary>,
    );
    return error as Error;
  } finally {
    reportVitals();
  }
}

const passthroughObject = z
  .object({})
  .passthrough() // Allow any extra fields
  .nullish()
  .default({}) // Default to empty object
  .transform((val) => {
    if (val) {
      return val;
    }
    if (typeof val === "string") {
      Logger.warn(
        "[marimo] received JSON string instead of object. Parsing...",
      );
      return JSON.parse(val);
    }
    Logger.warn("[marimo] missing config data");
    return {};
  });

// This should be extremely backwards compatible and require no options
const mountOptionsSchema = z.object({
  /**
   * filename of the notebook to open
   */
  filename: z
    .string()
    .nullish()
    .transform((val) => {
      if (val) {
        return val;
      }
      Logger.warn("No filename provided, using fallback");
      return getFilenameFromDOM();
    }),
  /**
   * notebook code
   */
  code: z
    .string()
    .nullish()
    .transform((val) => val ?? getMarimoCode() ?? ""),
  /**
   * marimo version
   */
  version: z
    .string()
    .nullish()
    .transform((val) => val ?? "unknown"),
  /**
   * 'edit' or 'read'/'run' or 'home'
   */
  mode: z.enum(["edit", "read", "home", "run"]).transform((val): AppMode => {
    if (val === "run") {
      return "read";
    }
    return val;
  }),
  /**
   * marimo config
   */
  config: passthroughObject,
  /**
   * marimo config overrides
   */
  configOverrides: passthroughObject,
  /**
   * marimo app config
   */
  appConfig: passthroughObject,
  /**
   * show code in run mode
   */
  view: z
    .object({
      showAppCode: z.boolean().default(true),
    })
    .nullish()
    .transform((val) => val ?? { showAppCode: true }),

  /**
   * server token
   */
  serverToken: z
    .string()
    .nullish()
    .transform((val) => val ?? ""),

  /**
   * File stores for persistence
   */
  fileStores: z.array(z.custom<FileStore>()).optional(),

  /**
   * Serialized Session["NotebookSessionV1"] snapshot
   */
  session: z.union([
    z.null().optional(),
    z
      .object({
        // Rough shape, we don't need to validate the full schema
        version: z.literal("1"),
        metadata: z.any(),
        cells: z.array(z.any()),
      })
      .passthrough()
      .transform((val) => val as api.Session["NotebookSessionV1"]),
  ]),

  /**
   * Serialized Notebook["NotebookV1"] snapshot
   */
  notebook: z.union([
    z.null().optional(),
    z
      .object({
        // Rough shape, we don't need to validate the full schema
        version: z.literal("1"),
        metadata: z.any(),
        cells: z.array(z.any()),
      })
      .passthrough()
      .transform((val) => val as api.Notebook["NotebookV1"]),
  ]),

  /**
   * Runtime configs
   */
  runtimeConfig: z
    .array(
      z
        .object({
          url: z.string(),
          authToken: z.string().nullish(),
        })
        .passthrough(),
    )
    .nullish()
    .transform((val) => val ?? []),
});

/**
 * Initialize Electron runtime configuration (server URL)
 */
async function initElectronRuntime(
  serverToken?: string,
): Promise<void> {
  // Check if running in Electron
  const isElectron =
    typeof window !== "undefined" &&
    typeof window.electronAPI !== "undefined";

  if (!isElectron) {
    // Not in Electron, nothing to do
    return;
  }

  const electronAPI = window.electronAPI;
  if (!electronAPI) {
    Logger.warn("Electron API not available");
    return;
  }

  try {
    // Maximum retry attempts
    const maxRetries = 10;
    const retryDelay = 500; // 500ms between retries

    // Function to wait for server to be ready
    const waitForServer = async (): Promise<string | null> => {
      for (let retries = 0; retries < maxRetries; retries++) {
        const status = await electronAPI.getServerStatus();
        
        if (status.status === "running" && status.url) {
          Logger.debug("⚡ Server is running at", status.url);
          return status.url;
        }

        if (status.status === "error") {
          Logger.warn(`Server status error (attempt ${retries + 1}/${maxRetries}), retrying...`);
        } else {
          // Server is starting or stopped, wait a bit and retry
          Logger.debug(
            `Server status: ${status.status} (attempt ${retries + 1}/${maxRetries}), waiting for server to be ready...`,
          );
        }

        // Wait before retry (don't wait after the last attempt)
        if (retries < maxRetries - 1) {
          await new Promise((resolve) => setTimeout(resolve, retryDelay));
        }
      }

      Logger.error("Server did not become ready within timeout");
      return null;
    };

    // Try to get server URL immediately
    let serverURL = await electronAPI.getServerURL();
    Logger.debug("⚡ Initial server URL check:", serverURL);
    
    // If not available, wait for server to be ready
    if (!serverURL) {
      Logger.debug("⚡ Server URL not available, waiting for server to start...");
      serverURL = await waitForServer();
    }

    if (serverURL) {
      // Update runtime config with server URL
      // This will trigger runtimeManagerAtom to recompute and create a new RuntimeManager
      Logger.info("⚡ Setting runtime config with server URL:", serverURL);
      store.set(runtimeConfigAtom, {
        url: serverURL,
        serverToken,
      });
      Logger.info("⚡ Runtime config updated with Electron server URL:", serverURL);
    } else {
      Logger.error("⚡ Failed to get server URL from Electron after retries");
      // Don't throw here, let the app continue with default config
      // The RuntimeManager will attempt to connect and handle connection errors appropriately
    }

    // Listen for server status changes
    const cleanup = electronAPI.onServerStatusChange((status) => {
      if (status.status === "running" && status.url) {
        Logger.debug("⚡ Server status changed, updating runtime config", status.url);
        store.set(runtimeConfigAtom, {
          url: status.url,
          serverToken,
        });
      } else if (status.status === "error") {
        Logger.warn("Server status changed to error");
      }
    });

    // Store cleanup function (could be used when unmounting, but not critical)
    // For now, we'll let it run until the app closes
  } catch (error) {
    Logger.error("Failed to initialize Electron runtime", error);
    // Don't throw, let the app continue with default config
    // The user might see connection errors, but the app won't crash
  }
}

function initStore(options: unknown) {
  const parsedOptions = mountOptionsSchema.safeParse(options);
  if (!parsedOptions.success) {
    Logger.error("Invalid marimo mount options", parsedOptions.error);
    throw new Error("Invalid marimo mount options");
  }
  const mode = parsedOptions.data.mode;
  preloadPage(mode);

  // Initialize file stores if provided
  if (
    parsedOptions.data.fileStores &&
    parsedOptions.data.fileStores.length > 0
  ) {
    Logger.log("🗄️ Initializing file stores via mount...");
    // Insert file stores at the beginning (highest priority)
    // Insert in reverse order so first in array gets highest priority
    for (let i = parsedOptions.data.fileStores.length - 1; i >= 0; i--) {
      notebookFileStore.insert(0, parsedOptions.data.fileStores[i]);
    }
    Logger.log(
      `🗄️ Injected ${parsedOptions.data.fileStores.length} file store(s) into notebookFileStore`,
    );
  }

  // Configure networking layer
  store.set(requestClientAtom, resolveRequestClient());

  // Files
  store.set(filenameAtom, parsedOptions.data.filename);
  store.set(codeAtom, parsedOptions.data.code);
  store.set(initialModeAtom, mode);

  // Meta
  store.set(marimoVersionAtom, parsedOptions.data.version);
  store.set(showCodeInRunModeAtom, parsedOptions.data.view.showAppCode);

  // Check for view-as parameter to start in present mode
  const shouldStartInPresentMode = (() => {
    const url = new URL(window.location.href);
    return url.searchParams.get(KnownQueryParams.viewAs) === "present";
  })();

  const initialViewMode =
    mode === "edit" && shouldStartInPresentMode ? "present" : mode;
  store.set(viewStateAtom, { mode: initialViewMode, cellAnchor: null });
  store.set(serverTokenAtom, parsedOptions.data.serverToken);

  // Config
  store.set(
    configOverridesAtom,
    parseConfigOverrides(parsedOptions.data.configOverrides),
  );
  store.set(userConfigAtom, parseUserConfig(parsedOptions.data.config));
  store.set(appConfigAtom, parseAppConfig(parsedOptions.data.appConfig));

  // Check if running in Electron
  const isElectron =
    typeof window !== "undefined" &&
    typeof window.electronAPI !== "undefined";

  // Runtime config
  if (parsedOptions.data.runtimeConfig.length > 0) {
    const firstRuntimeConfig = parsedOptions.data.runtimeConfig[0];
    Logger.debug("⚡ Runtime URL", firstRuntimeConfig.url);
    store.set(runtimeConfigAtom, {
      ...firstRuntimeConfig,
      serverToken: parsedOptions.data.serverToken,
    });
  } else if (isElectron) {
    // In Electron, initialize runtime config asynchronously
    // Start initialization immediately to get server URL as soon as possible
    Logger.debug("⚡ Electron environment detected, initializing runtime config...");
    // Set temporary default to allow RuntimeManager initialization
    // initElectronRuntime will update it with the actual server URL once available
    store.set(runtimeConfigAtom, {
      ...DEFAULT_RUNTIME_CONFIG,
      serverToken: parsedOptions.data.serverToken,
    });
    // Start async initialization immediately (don't await)
    // This will update runtimeConfigAtom once the server is ready, which will
    // trigger runtimeManagerAtom to recompute and create a new RuntimeManager instance
    void initElectronRuntime(parsedOptions.data.serverToken);
  } else {
    store.set(runtimeConfigAtom, {
      ...DEFAULT_RUNTIME_CONFIG,
      serverToken: parsedOptions.data.serverToken,
    });
  }

  // Session/notebook
  const notebook = notebookStateFromSession(
    parsedOptions.data.session,
    parsedOptions.data.notebook,
  );
  if (notebook) {
    store.set(notebookAtom, notebook);
  }
}

export const visibleForTesting = {
  reset: () => {
    hasMounted = false;
  },
};
