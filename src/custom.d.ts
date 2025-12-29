/* Copyright 2026 Marimo. All rights reserved. */

declare module "*.svg" {
  const content: string;
  export default content;
}

declare module "*.svg?inline" {
  const content: string;
  export default content;
}

declare module "*.png?inline" {
  const content: string;
  export default content;
}
