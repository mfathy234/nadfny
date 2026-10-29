# SDK and IDE caches implementation plan

**Goal:** Extend developer cache cleanup and add a safe review of old SDKs and per-user tools, following feature #3 in the supplied Nadfny 0.3.0 brief.

**Architecture:** Keep regenerable caches in CacheService. Put SDK parsing, bounded global.json discovery and per-user folder discovery in Features/Toolchains. Publish scans through scoped RPC and a separate toolchains-actions.js module; reuse existing list, confirmation and progress components.

**Constraints:** No version bump, dependencies or machine cleanup. Deletes must reference a completed scan and use SafeDeleter. .NET SDKs only show a command and Apps & Features. Preserve user data via the Recycle Bin. Commit only this feature, without pushing.

1. Add failing tests for environment-based cache definitions, read-only Go cache cleanup and rejection of unscanned caches. Implement definitions and scan authorization.
2. Add failing tests for SDK parsing, feature bands, pins, bounded discovery, AVD and image discovery, old JetBrains versions and deletion authorization. Implement ToolchainService and focused discovery helpers. Measure sequentially; reject links or folders outside the profile for removable tools.
3. Add scoped toolchain RPC, fixed Apps & Features launch, streaming state and confirmed actions. Render the new section in caches.js and extend the README table.
4. Run focused tests, full tests with coverage, host build and node --check for changed JavaScript. Review safety and the final diff, then commit on feat/0.3-sdk-caches.

Review focus: malformed or unreadable pins, preview SDKs, feature-band boundaries, junctions escaping the profile, overridden Android SDKs outside the profile and stale scan IDs. Do not label SDKs old when pin discovery is incomplete.
