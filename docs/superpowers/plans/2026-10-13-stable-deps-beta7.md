# Stable dependencies and beta 7 implementation plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Update supported direct dependencies to stable releases, refresh all lock files, and publish v0.3.0-beta.7.
**Architecture:** Retain .NET 10 and the existing package graph and release pipeline. Integrate Aspire PR #5, update central package versions and action pins, and validate before tagging.
**Tech Stack:** .NET 10, NuGet central package management, MTP, Aspire, GitHub Actions, MinVer.
**Spec:** User request in this chat; docs/RELEASING.md defines the release contract.

## Global constraints
- No .NET preview SDK or package upgrades; keep net10.0 targets.
- Keep versioning tag-driven and preserve the existing local checkout.
- Refresh Windows and Linux locks. MTP, TRX, and coverage must remain compatible.
- Update direct dependencies; do not override third-party transitive major versions blindly.
- Roll back an unshipped update by reverting its commit; a published package requires a new version.

## Review focus
- Linux locked restore must pass as well as Windows.
- Both coverage integrations must still run after MTP updates.
- NuGet packages must retain generators, build assets, and dependency metadata.
- No new preview dependency or SDK is introduced.
- Existing scenario, generator, and Aspire tests retain their assertions.

## Tasks
- [x] Integrate the tested Aspire PR into a branch from current main.
- [x] Update Directory.Packages.props, global.json SDK preview selection, and .github/workflows/ci.yml action pin; regenerate lock files, including fixture locks where applicable.
- [x] Run locked restores for both platforms, CI Release build, full tests, dependency audit, and all four package builds. Resolve compatibility failures at their source.
- [ ] Obtain independent review; commit, publish the branch, and pass Ubuntu CI.
- [ ] Fast-forward main only if unchanged, pass main CI, tag beta 7, and verify all four NuGet packages and the GitHub release.

## Execution record
- Baseline: main 2870750; Aspire PR a1e0240 had green Ubuntu CI. User explicitly authorized updates and beta publication.
- Compatibility ruling: MSBuild 18.10.1 requires net11.0; use 18.9.6 and exclude its StringTools runtime assets as required by MSBuildLocator (observed MSBL001).
- Review fixes: update Aspire.AppHost.Sdk to 13.6.1; remove redundant direct AppHost reference (Testing already supplies it without build assets); assert all four packages exist before publication.

- Verified: .NET SDK 10.0.401; Windows/Linux locked restore; Release build 0 warnings; 1,186 tests passed and 5 expected skips; Microsoft coverage and Coverlet smoke runs exit 0; all four packages inspected; no known vulnerable packages reported. Only deliberate outdated direct pins are net10-compatible MSBuild/StringTools18.9.6.
- Independent review completed; both findings fixed and re-reviewed with no remaining actionable findings.
