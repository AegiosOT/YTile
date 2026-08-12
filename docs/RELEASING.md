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
   NativeAOT binaries, packages `ytile-X.Y.Z-win-x64.zip` (the winget installer
   asset), writes `SHA256SUMS.txt`, generates winget manifests with the zip's
   hash, and creates the GitHub release with everything attached.
   `scripts/install.ps1` serves users from that release immediately.
4. Submit the release's winget manifests to
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs): download
   the three `AltimG.YTile*.yaml` assets into
   `manifests/a/AltimG/YTile/X.Y.Z/` in a fork and open a PR.
   (`wingetcreate update AltimG.YTile --version X.Y.Z --urls <zip-url>` also
   works, but only from the second release onward — `update` requires the
   package to already exist in winget-pkgs.)
   `winget install AltimG.YTile` picks the version up once the PR is merged.
