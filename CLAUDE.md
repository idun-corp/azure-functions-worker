# Working in this repository

Isolated-worker Azure Functions extensions, published as nine packages on GitHub Packages:
`AzureFunctions.Worker.Extensions.*` plus `DurableTask.Client.AzureStorage`. A **fork** of
[nazar-kuzo/azure-functions-worker](https://github.com/nazar-kuzo/azure-functions-worker) — see
*This is a fork* below, which affects both pull requests and package metadata.

The solution is `src/AzureFunction.Worker.Extensions.slnx`. Note the singular **AzureFunction**
while every project inside is plural; tab-completion gets this wrong.

## Bump the version, or publishing silently does nothing

All packages ship in lockstep from one `<Version>` in `src/Directory.Build.props`. Any change that
should reach consumers needs that bumped.

Unlike the sibling `shared-packages`, **nothing here will tell you if you forget.** That repo fails
the build on a missing changelog section or an existing tag; this one has no changelog, no tag and no
version guard. The publish step pushes with `--skip-duplicate`, so a forgotten bump uploads nothing,
reports success, and leaves you believing the fix shipped. The only guard is against an *empty*
package list, which a forgotten bump does not produce.

If a change is genuinely internal — build configuration, tests, the test host — it does not need a
bump, because nothing packable changed. Everything else does.

### Do not bump `AppConfigurationHostVersion`

It sits next to `<Version>` and looks like it belongs to the same release. It does not.
`AppConfiguration` bakes it into an `ExtensionInformationAttribute`, and the Functions Worker SDK
generates an inner project under `obj/` that restores `AppConfiguration.Host` **from the feed** at
exactly that version. Bumping it breaks every Functions app project in this solution *before* the
build that would publish the new Host package, so it can never self-publish. Releasing a new Host
needs a deliberate two-step. It is currently 2.2.2 while everything else is on 3.1.x, and that is
correct.

## Release notes live in `PackageReleaseNotes`

There is no `CHANGELOG.md`. Notes come from `<PackageReleaseNotes>` in `src/Directory.Build.props`,
which individual projects may override.

An override **wins for that package until you remove it**, which is how notes go stale: a
project-specific note describing release N is still attached at release N+1, where it is simply
wrong. Delete an override once the change it describes has shipped, so the package falls back to the
repo-wide note. Several overrides currently carry text inherited from upstream describing changes
from various past versions — treat those as known-stale rather than as examples to follow.

## Building and testing

```bash
dotnet build src/AzureFunction.Worker.Extensions.slnx -c Release
```

`GeneratePackageOnBuild` is on, so a build also packs. The solution multi-targets `net8.0;net10.0`,
so **every diagnostic is reported twice** — once per framework. Halve the count before concluding
anything from it.

```bash
dotnet test src/AzureFunction.Worker.Extensions.slnx --no-build -c Release
```

The whole suite is hermetic — `AzureFunctions.TestFramework` runs the real worker pipeline over an
in-memory `TestServer`, with no Core Tools, no Docker and no TCP ports — so CI runs all of it with no
category filter. There is nothing to exclude locally.

## Warnings are errors, including a NuGet advisory

`TreatWarningsAsErrors` is on for every build, local ones included, and the warning count is zero —
so anything reported is a regression introduced by the change in hand. Fix it rather than silencing
it. If a warning is genuinely in the way mid-edit, build that one project with
`-p:TreatWarningsAsErrors=false`; do not weaken the property.

There is deliberately **no** `WarningsNotAsErrors` exemption, so `NuGetAudit` findings (`NU1901` low,
`NU1902` moderate, `NU1903` high, `NU1904` critical) fail the build too — on `restore`, before
anything compiles.

**When an advisory blocks a hotfix and the real fix is not yet available, suppress that one
advisory** rather than reaching for a blanket exemption:

```xml
<ItemGroup>
    <!-- GHSA-xxxx: no patched version yet, tracked in OSUI-1234. Delete when the bump lands. -->
    <NuGetAuditSuppress Include="https://github.com/advisories/GHSA-xxxx-xxxx-xxxx" />
</ItemGroup>
```

It matches on the advisory URL, so every other advisory still fails. A suppression is temporary by
intent: pair it with a ticket and a comment saying what un-blocks it, and delete it in the pull
request that lands the real fix.

Analyzer severities are set in `src/.editorconfig`. Path-scoped sections work, and the glob is
relative to **that file's own directory** — `[AzureFunctions.Worker.Extensions.Tests/**.cs]`, not
`[**/AzureFunctions...]`, where the leading `**/` silently matches nothing and the rule stays on.

## This is a fork

- **`gh pr create` defaults to the upstream repository**, because GitHub resolves the fork's parent.
  Always pass `--repo idun-corp/azure-functions-worker`, and check the result: a correct pull request
  reports `isCrossRepository: false`. Opening one against `nazar-kuzo` has happened.
- **Package metadata must point at the fork.** `RepositoryUrl` and `PackageProjectUrl` are
  `idun-corp`, not upstream: GitHub Packages resolves the owner from the nuspec repository URL and
  rejects a push whose owner does not match the feed namespace.

## Restoring needs a token

Private packages come from GitHub Packages, which requires authentication even to read. Use a
**classic** PAT with `read:packages` — fine-grained tokens are rejected — stored in the user-level
NuGet config, never in `src/NuGet.config`. Setup:
[nuget-migration.md](https://github.com/idun-corp/shared-packages/blob/main/docs/nuget-migration.md).

`packageSourceMapping` keeps public lookups off the private feed, which 403s on them. Adding a new
private package means adding its pattern there too.

## Conventions

- Branches: `feature/<ISSUE-KEY>-<short-kebab-summary>`, or `fix/` when the Jira issue type is Bug.
- Commit messages are prefixed with the Jira key: `OSUI-1234: what changed`. One logical change per
  commit, and the version bump is its own commit on the branch.
- MSBuild and workflow files carry comments explaining *why* a setting exists, not what it does.
  Match that when editing them — several settings look removable and are not.

## Known gaps

- **The Swashbuckle extension has no runtime test coverage.**
  `AzureFunctions.Worker.Extensions.Tests.FunctionApp` does not reference it, so nothing exercises
  Swagger generation and a green suite says nothing about it. Every Swashbuckle bump here is
  compile-verified only. To check a bump for real, diff the generated `swagger.json` before and
  after: byte-diffing the document catches filter regressions that reading the code will not.
- `AzureFunctions.Worker.Extensions.TestHost` is the manual Swagger showcase, not a test. Running it
  needs Azurite and an Azure App Configuration connection that `local.settings.json` does not carry.
