#!/usr/bin/env bash
# Runs once after the dev container is created. Each part is skipped until the
# files it needs exist, so this works on the bare skeleton and on a full clone.
set -euo pipefail

# The workspace is mounted from the host, so its owner differs from the container user
git config --global --add safe.directory /workspaces/SupportDesk-AI

if [[ -f dotnet-tools.json ]]; then
  dotnet tool restore
fi

if compgen -G "*.slnx" > /dev/null; then
  dotnet restore
fi

# No package needs its install scripts; npm 11 blocks them by default anyway
if [[ -f web/package-lock.json ]]; then
  npm --prefix web ci --ignore-scripts
fi

if [[ -x web/node_modules/.bin/playwright ]]; then
  web/node_modules/.bin/playwright install --with-deps chromium
fi

# The Playwright MCP server (.mcp.json) pins its own browser build, separate from @playwright/test's
if [[ -f .mcp.json ]]; then
  mcp_package=$(grep -o '@playwright/mcp@[0-9.]*' .mcp.json)
  # Claude Code can start the same npx package for the MCP server while this
  # runs, and two installs into one npx cache entry can leave it without its
  # package.json. A private npm cache keeps this install out of that entry;
  # the browser itself goes to ~/.cache/ms-playwright either way.
  npx_cache=$(mktemp -d)
  NPM_CONFIG_CACHE="$npx_cache" npx --ignore-scripts -y "$mcp_package" install-browser chrome-for-testing
  rm -rf "$npx_cache"
fi
