#!/usr/bin/env python
# Copyright 2026 Marimo. All rights reserved.
"""
Backcast server startup script.

This script starts the marimo backend server for the backcast application.
It can be run directly or via the npm script: `npm run server`
"""

import os
from pathlib import Path

from marimo._server.file_router import AppFileRouter
from marimo._server.model import SessionMode
from marimo._server.start import start
from marimo._server.tokens import AuthToken


def main():
    """Start the marimo backend server."""
    # Get server directory (where this script is located)
    server_dir = Path(__file__).parent.resolve()
    
    # Change to server directory
    os.chdir(server_dir)
    
    # Get configuration from environment variables
    env = os.getenv("ENV", "production")
    development_mode = env == "development"
    
    # Get port and host from environment or use defaults
    port = int(os.getenv("PORT", "2718"))
    host = os.getenv("HOST", "127.0.0.1")
    
    # Notebook file path
    notebook_file = server_dir / "backcast.py"
    
    # Ensure notebook file exists
    if not notebook_file.exists():
        print(f"Creating default notebook file: {notebook_file}")
        notebook_file.parent.mkdir(parents=True, exist_ok=True)
        notebook_file.write_text(
            "# Backcast Notebook\n\nimport marimo\n\n__generated_with = \"0.18.4\"\napp = marimo.App()\n\n\n@app.cell\ndef _():\n    # Backcast Notebook\n    return\n\n\nif __name__ == \"__main__\":\n    app.run()\n",
            encoding="utf-8"
        )
    
    # Create file router
    file_router = AppFileRouter.infer(str(notebook_file))
    
    # Start the server
    start(
        file_router=file_router,
        development_mode=development_mode,
        quiet=not development_mode,
        host=host,
        port=port,
        proxy=None,
        headless=True,  # Don't launch browser
        mode=SessionMode.EDIT,
        include_code=True,
        watch=False,  # Disable watch mode for now
        skew_protection=False,  # Disable for electron app
        cli_args={},  # Empty CLI args dict
        argv=[],
        base_url="",
        allow_origins=None,
        redirect_console_to_browser=True,
        ttl_seconds=None,
        remote_url=None,
        mcp=False,
        server_startup_command=None,
        asset_url=None,
        timeout=None,
        auth_token=AuthToken(""),  # Empty token for no authentication
    )


if __name__ == "__main__":
    main()

