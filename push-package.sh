#!/bin/bash

# Azure Functions AppConfiguration.Host Package Push Script
#
# Usage:
#   ./push-package.sh              # Build and push packages
#   ./push-package.sh --skip-build # Only push existing packages
#   ./push-package.sh --api-key <token>  # Provide API key

set -e

# Configuration
PROJECT_PATH="src/AzureFunctions.Worker.Extensions.AppConfiguration.Host"
PROJECT_NAME="AzureFunctions.Worker.Extensions.AppConfiguration.Host"
BUILD_CONFIG="Release"
FEED_URL="https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json"

# Parse arguments
SKIP_BUILD=false
API_KEY=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --skip-build)
            SKIP_BUILD=true
            shift
            ;;
        --api-key)
            API_KEY="$2"
            shift 2
            ;;
        *)
            echo "Unknown option: $1"
            echo "Usage: $0 [--skip-build] [--api-key <token>]"
            exit 1
            ;;
    esac
done

echo "=== Azure Functions AppConfiguration.Host Package Push ==="

# Step 1: Build the project
if [ "$SKIP_BUILD" = false ]; then
    echo ""
    echo "[1/3] Building project..."
    if ! dotnet build "$PROJECT_PATH/$PROJECT_NAME.csproj" -c "$BUILD_CONFIG"; then
        echo "✗ Build failed"
        exit 1
    fi
    echo "✓ Build completed successfully"
else
    echo ""
    echo "[1/3] Skipping build..."
fi

# Step 2: Locate the generated packages
echo ""
echo "[2/3] Locating generated packages..."
PACKAGE_PATH="$PROJECT_PATH/bin/$BUILD_CONFIG"

if [ ! -d "$PACKAGE_PATH" ]; then
    echo "✗ Package directory not found: $PACKAGE_PATH"
    exit 1
fi

NUPKG=$(find "$PACKAGE_PATH" -name "$PROJECT_NAME.*.nupkg" -type f | head -1)
SNUPKG=$(find "$PACKAGE_PATH" -name "$PROJECT_NAME.*.snupkg" -type f | head -1)

if [ -z "$NUPKG" ]; then
    echo "✗ No .nupkg file found in $PACKAGE_PATH"
    exit 1
fi

echo "Found packages:"
echo "  - $(basename "$NUPKG")"
if [ -n "$SNUPKG" ]; then
    echo "  - $(basename "$SNUPKG")"
fi

# Step 3: Push packages
echo ""
echo "[3/3] Pushing packages to idun-corp..."

PUSH_ARGS=(
    "push"
    "$NUPKG"
    "-Source" "$FEED_URL"
    "-SkipDuplicate"
)

if [ -n "$API_KEY" ]; then
    PUSH_ARGS+=("-ApiKey" "$API_KEY")
fi

echo "Pushing $(basename "$NUPKG")..."
if ! nuget "${PUSH_ARGS[@]}"; then
    echo "✗ Push failed"
    exit 1
fi
echo "✓ Package pushed successfully"

if [ -n "$SNUPKG" ]; then
    PUSH_ARGS[1]="$SNUPKG"
    echo ""
    echo "Pushing $(basename "$SNUPKG")..."
    if ! nuget "${PUSH_ARGS[@]}"; then
        echo "✗ Symbol package push failed"
        exit 1
    fi
    echo "✓ Symbol package pushed successfully"
fi

echo ""
echo "=== Push completed successfully! ==="
echo "Feed URL: $FEED_URL"
