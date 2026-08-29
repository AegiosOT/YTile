# Releasing

1. Bump the `Version` const in `src/YTile/Program.cs` and `src/YTile.Cli/Program.cs`
   to the new version (no `v` prefix, no `-dev` suffix). The workflow refuses to
   release binaries whose `--version` does not match the tag.
2. Commit, then tag and push:

   ```
   git tag vX.Y.Z
   git push origin main vX.Y.Z
   ```

3. The [Release workflow](../.github/workflows/release.yml) tests, publishes
   NativeAOT binaries, signs them via Azure Artifact Signing (see below), bundles
   `ykeys.exe` from the pinned [YKeys](https://github.com/AegiosOT/YKeys)
   release, packages `ytile-X.Y.Z-win-x64.zip` (the winget installer asset),
   writes `SHA256SUMS.txt`, generates winget manifests with the zip's hash,
   and creates the GitHub release with everything attached.
   `scripts/install.ps1` serves users from that release immediately.

   **To ship a newer ykeys**: release it in the YKeys repo first, then update
   `YKEYS_VERSION` and `YKEYS_SHA256` (the exe's line from that release's
   `SHA256SUMS.txt`) in the "Bundle ykeys" step of release.yml. The step
   fails the release on any mismatch, so a stale pin cannot ship silently.

   Signing is fully automatic (Azure Artifact Signing via OIDC — no secrets,
   no approval pause). While the `AZURE_CLIENT_ID` repository variable is not
   configured, the signing steps are skipped and the release ships unsigned —
   setup and details in [packaging/signing/README.md](../packaging/signing/README.md).
4. Submit the release's winget manifests to
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs): download
   the three `AegiosOT.YTile*.yaml` assets into
   `manifests/a/AegiosOT/YTile/X.Y.Z/` in a fork and open a PR.
   (`wingetcreate update AegiosOT.YTile --version X.Y.Z --urls <zip-url>` also
   works, but only from the second release onward — `update` requires the
   package to already exist in winget-pkgs.)
   `winget install AegiosOT.YTile` picks the version up once the PR is merged.
5. Back on `main`, bump both `Version` consts to the next patch version with a
   `-dev` suffix (e.g. `0.1.2-dev`) so dev builds stay distinguishable from the
   released binaries. CI tolerates the suffix; step 1 strips it at the next
   release.

## Re-cutting a tag

If a tag has to be re-pointed (say the release run was cancelled and the fix
landed after tagging), move it explicitly — `git push origin main vX.Y.Z`
happily re-pushes the stale local tag, and the workflow builds whatever the
tag points at:

```
git tag -f vX.Y.Z
git push origin :refs/tags/vX.Y.Z
git push origin vX.Y.Z
```

Verify with `git rev-parse vX.Y.Z` == `git rev-parse HEAD` before pushing.
Only do this while nothing has shipped from the tag; a published release's
tag never moves.
