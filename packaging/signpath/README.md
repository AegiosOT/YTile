# SignPath code signing — one-time setup

YTile release binaries are Authenticode-signed through
[SignPath Foundation](https://signpath.org/)'s free OSS program. The release
workflow ([.github/workflows/release.yml](../../.github/workflows/release.yml))
already contains the signing stage; it is skipped while the
`SIGNPATH_API_TOKEN` secret is absent, so releases keep working before the
application is approved. This file records how the SignPath side is wired up.

## 1. Apply

Apply at <https://signpath.org/apply> with:

- Project: **YTile**, <https://github.com/AegiosOT/YTile>, GPL-3.0.
- CI: GitHub Actions (hosted runners), builds from tags, workflow linked above.
- The README's **Code signing** section satisfies the Foundation's
  attribution / team / privacy-statement requirements — keep it intact.

Approval creates a SignPath organization backed by a SignPath Foundation
certificate (SignPath Foundation is the publisher name on the signature).

## 2. Configure the SignPath organization

1. **Project**: create a project with slug `ytile` linked to the
   `AegiosOT/YTile` repository.
2. **Artifact configuration**: paste
   [artifact-configuration.xml](artifact-configuration.xml) as the project's
   **default** artifact configuration (the workflow passes no explicit slug).
3. **Signing policy**: create a policy with slug `release-signing`, manual
   approval required (Foundation default). The approver is AegiosOT.
4. **Trusted build system**: link the predefined *GitHub.com* trusted build
   system to the project, and install the SignPath GitHub App on the
   repository so origin verification works.
5. **CI user**: create a CI user with *submitter* permission on
   `release-signing`, and copy its API token.

## 3. Configure the GitHub repository

| Where | Name | Value |
| --- | --- | --- |
| Actions **secret** | `SIGNPATH_API_TOKEN` | the CI user's API token |
| Actions **variable** | `SIGNPATH_ORGANIZATION_ID` | the organization's GUID (SignPath UI → organization settings) |

## Note: the bundled ykeys.exe

The release zip also contains `ykeys.exe`, bundled from the
[YKeys](https://github.com/AegiosOT/YKeys) repo's own release. It never
passes through THIS project's signing step — the artifact submitted to
SignPath holds only `ytiled.exe` and `ytile.exe`, and the bundle happens
afterwards, just before zipping. Sign it at its source instead: add a second
SignPath project `ykeys` for the YKeys repo (same organization, same policy
shape — its release.yml already carries the gated steps), then bump the
pinned version/hash in YTile's "Bundle ykeys" step so YTile bundles the
signed build.

## 4. Release flow after setup

`git push origin vX.Y.Z` as usual. The workflow publishes the binaries, then
**pauses at "Sign binaries (SignPath)"** — approve the signing request at
<https://app.signpath.io> within an hour and the run continues: the signed
exes replace the unsigned ones before the zip, `SHA256SUMS.txt`, and winget
manifests are produced, so every published hash refers to signed binaries. A
denied or unapproved request fails the release; nothing unsigned ships from a
run that was supposed to sign.
