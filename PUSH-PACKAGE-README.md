# Push Package to idun-corp

This guide explains how to manually push the `AzureFunctions.Worker.Extensions.AppConfiguration.Host` package to the idun-corp Azure DevOps feed.

## Package Information

- **Package Name**: AzureFunctions.Worker.Extensions.AppConfiguration.Host
- **Current Version**: 2.2.2 (defined in `src/Directory.Build.props`)
- **Feed URL**: https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json
- **Target Frameworks**: net8.0, net9.0, net10.0

## Prerequisites

1. **NuGet CLI**: Install the latest [nuget.exe](https://www.nuget.org/downloads) or use `dotnet nuget` commands
2. **Azure DevOps Access**: You must have access to the idun-corp Azure DevOps organization
3. **Personal Access Token (PAT)**: For authentication, create a PAT with at least "Packaging (read & write)" scope:
   - Go to: https://dev.azure.com/idun-corp/_usersSettings/tokens
   - Create a new token with scope: "Packaging (read & write)"

## Method 1: Using PowerShell (Windows)

### Basic Usage (with cached credentials)
```powershell
.\push-package.ps1
```

This will:
1. Build the AppConfiguration.Host project in Release mode
2. Locate the generated .nupkg and .snupkg files
3. Push both packages to idun-corp feed

### Skip Build (push existing packages)
```powershell
.\push-package.ps1 -SkipBuild
```

### Provide API Key
```powershell
.\push-package.ps1 -ApiKey "your-personal-access-token"
```

## Method 2: Using Bash (Linux/macOS)

### Basic Usage (with cached credentials)
```bash
chmod +x push-package.sh
./push-package.sh
```

### Skip Build
```bash
./push-package.sh --skip-build
```

### Provide API Key
```bash
./push-package.sh --api-key "your-personal-access-token"
```

## Method 3: Manual Steps

If you prefer to push manually or the scripts don't work, follow these steps:

### Step 1: Build the Project
```bash
dotnet build src/AzureFunctions.Worker.Extensions.AppConfiguration.Host/AzureFunctions.Worker.Extensions.AppConfiguration.Host.csproj -c Release
```

### Step 2: Locate the Packages
The generated packages will be in:
```
src/AzureFunctions.Worker.Extensions.AppConfiguration.Host/bin/Release/
```

Look for:
- `AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.nupkg`
- `AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.snupkg` (symbols)

### Step 3: Push to idun-corp

**Using nuget.exe:**
```bash
nuget push "src/AzureFunctions.Worker.Extensions.AppConfiguration.Host/bin/Release/AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.nupkg" \
  -Source "https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json" \
  -ApiKey "your-personal-access-token" \
  -SkipDuplicate
```

**Using dotnet CLI:**
```bash
dotnet nuget push "src/AzureFunctions.Worker.Extensions.AppConfiguration.Host/bin/Release/AzureFunctions.Worker.Extensions.AppConfiguration.Host.2.2.2.nupkg" \
  --source "https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json" \
  --api-key "your-personal-access-token"
```

## Authentication Setup

### Option A: One-time PAT (Recommended for scripts)
Pass the PAT directly as shown above using `-ApiKey` or `--api-key` flags.

### Option B: Store Credentials in NuGet Config
Add credentials to `~/.nuget/NuGet/NuGet.Config`:

```xml
<configuration>
  <packageSources>
    <add key="idun-corp" value="https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <idun-corp>
      <add key="Username" value="anything" />
      <add key="ClearTextPassword" value="your-personal-access-token" />
    </idun-corp>
  </packageSourceCredentials>
</configuration>
```

### Option C: Azure Artifacts Credential Provider
Install the [Azure Artifacts Credential Provider](https://github.com/microsoft/artifacts-credprovider) for automatic authentication.

## Troubleshooting

### "401 Unauthorized" Error
- Verify your PAT has the "Packaging (read & write)" scope
- Check if your PAT has expired
- Ensure you're authenticated to the correct Azure DevOps organization

### "403 Forbidden" Error
- Verify you have permissions to push to the idun-corp feed
- Check your organization and feed access

### "409 Conflict" Error (Package already exists)
- This is normal if you're re-pushing the same version
- The script uses `-SkipDuplicate` flag which will skip duplicate versions
- To force update, remove the existing version from the feed first

### NuGet CLI Not Found
Install NuGet CLI or use the `dotnet nuget` commands instead:
```bash
dotnet nuget push "path/to/package.nupkg" \
  --source "https://pkgs.dev.azure.com/idun-corp/_packaging/idun-corp/nuget/v3/index.json" \
  --api-key "your-pat"
```

### Package Version Changed
The version is defined in `src/Directory.Build.props` under the `AppConfigurationHostVersion` property. Update it there and rebuild if needed.

## Verify the Push

After pushing, verify the package is available in idun-corp:
1. Go to: https://dev.azure.com/idun-corp/
2. Navigate to Artifacts → idun-corp feed
3. Search for "AzureFunctions.Worker.Extensions.AppConfiguration.Host"
4. Verify version 2.2.2 (or your version) is listed

## CI/CD Alternative

For automated pushes, the project uses Azure Pipelines (see `build/build.yml`). To trigger a build and push:
1. Go to the Azure DevOps pipeline
2. Manually trigger the build (the trigger is set to `none` currently)
3. The pipeline will automatically build and push to the feed

## Additional Notes

- Both .nupkg and .snupkg (symbol packages) are pushed together
- Symbol packages enable debugging with downloaded source
- The NuGet.config in `src/` maps all `AzureFunctions.Worker.Extensions.*` packages to the idun-corp feed
- The `-SkipDuplicate` flag prevents errors when pushing the same version twice
