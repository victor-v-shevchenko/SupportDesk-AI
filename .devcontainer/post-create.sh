#!/usr/bin/env bash
# Runs once after the dev container is created. Each part is skipped until the
# files it needs exist, so this works on the bare skeleton and on a full clone.
set -euo pipefail

# The workspace is mounted from the host, so its owner differs from the container user
git config --global --add safe.directory /workspaces/SupportDesk-AI

if [ -f dotnet-tools.json ] || [ -f .config/dotnet-tools.json ]; then
  dotnet tool restore
fi

if compgen -G "*.slnx" > /dev/null || compgen -G "*.sln" > /dev/null; then
  dotnet restore
fi

if [ -f web/package-lock.json ]; then
  npm --prefix web ci
fi

if [ -x web/node_modules/.bin/playwright ]; then
  (cd web && npx playwright install --with-deps chromium)
fi

# The Playwright MCP server (.mcp.json) pins its own browser build, separate from @playwright/test's
if [ -f .mcp.json ]; then
  mcp_package=$(grep -o '@playwright/mcp@[0-9.]*' .mcp.json)
  npx -y "$mcp_package" install-browser chrome-for-testing
fi
