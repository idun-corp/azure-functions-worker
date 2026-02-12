# Quick Push Reference

## TL;DR - Push AppConfiguration.Host Package in 30 Seconds

### For Windows (PowerShell):
```powershell
# With cached credentials (fastest)
.\push-package.ps1

# Or with PAT token
.\push-package.ps1 -ApiKey "your-pat-token"
```

### For Linux/macOS (Bash):
```bash
chmod +x push-package.sh
./push-package.sh

# Or with PAT token
./push-package.sh --api-key "your-pat-token"
```

### Manual One-Liner (if scripts don't work):
```bash
# Build
dotnet build src/AzureFunctions.Worker.Extensions.AppConfiguration.Host/AzureFunctions.Worker.Extensions.AppConfiguration.Host.csproj -c Release

# Push
nuget push "src/AzureFunctions.Worker.Extensions.AppConfiguration.Host/bin/Release/AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.nupkg" \
  -Source "https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json" \
  -ApiKey "your-pat-token" -SkipDuplicate
```

## Getting Your PAT Token

1. Go to: https://dev.azure.com/idun-corp/_usersSettings/tokens
2. Click "New Token"
3. Name: anything (e.g., "nuget-push")
4. Scope: select "Packaging (read & write)"
5. Copy the token immediately (it won't show again)

## Feed Details

| Property | Value |
|----------|-------|
| Package | AzureFunctions.Worker.Extensions.AppConfiguration.Host |
| Version | 2.2.2 |
| Feed URL | https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json |

## Expected Output

```
=== Azure Functions AppConfiguration.Host Package Push ===

[1/3] Building project...
✓ Build completed successfully

[2/3] Locating generated packages...
Found packages:
  - AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.nupkg
  - AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.snupkg

[3/3] Pushing packages to idun-corp...
Pushing AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.nupkg...
✓ Package pushed successfully

Pushing AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.snupkg...
✓ Symbol package pushed successfully

=== Push completed successfully! ===
```

## Verify Push Success

Visit: https://dev.azure.com/idun-corp/_artifacts/feed/idun-corp
Search for "AppConfiguration.Host" and verify version 2.2.2 appears.

## Common Issues

| Problem | Solution |
|---------|----------|
| "401 Unauthorized" | Check PAT token is valid and has "Packaging (read & write)" scope |
| "403 Forbidden" | Verify you have push permissions to idun-corp feed |
| "409 Conflict" | Package already exists - use `-SkipDuplicate` flag (scripts do this automatically) |
| "NuGet not found" | Install from https://www.nuget.org/downloads or use `dotnet nuget push` instead |

## Need Help?

See `PUSH-PACKAGE-README.md` for full documentation and authentication options.
