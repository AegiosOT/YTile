# Code signing — Azure Artifact Signing

Release binaries are Authenticode-signed by CI through
[Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/)
(individual-developer identity validation; the certificate shows the
maintainer's verified legal name as publisher). Signing is fully automatic —
OIDC, no stored secrets, no approval pause. The signing steps in
[.github/workflows/release.yml](../../.github/workflows/release.yml) are
skipped while the `AZURE_CLIENT_ID` repository variable is absent, so
releases keep working if signing is ever torn down.

## Azure resources (subscription NineFiveB)

| Resource | Value |
| --- | --- |
| Resource group | `ytile-signing` (East US) |
| Signing account | `aegiosot` — endpoint `https://eus.codesigning.azure.net/` (Basic tier) |
| Certificate profile | `release-signing` (Public Trust, daily-rotated 72h certs) |
| Managed identity | `ytile-release-signer` — role *Artifact Signing Certificate Profile Signer* on the account |

## GitHub ↔ Azure trust (no secrets)

The managed identity carries two federated credentials, one per repo, each
accepting GitHub OIDC tokens minted for the **`release` environment**:

- `repo:AegiosOT/YTile:environment:release`
- `repo:AegiosOT/YKeys:environment:release`

The release job therefore declares `environment: release` and
`permissions: id-token: write`; `azure/login@v3` exchanges the OIDC token,
and `azure/artifact-signing-action@v2` signs `publish/*.exe` in place with
RFC-3161 timestamping (`timestamp.acs.microsoft.com`), so signatures outlive
the 72-hour certificates.

Repository variables (Actions → Variables) in each repo:
`AZURE_CLIENT_ID` (the managed identity's client id), `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`. Plain variables, not secrets — none of them grant
anything without an OIDC token from the `release` environment of these repos.

## Renewals and gotchas

- Identity validation expires and must be renewed by the maintainer in the
  Azure portal (reminder emails start 60 days ahead). An expired validation
  fails the signing step; releases can ship unsigned meanwhile by removing
  the `AZURE_CLIENT_ID` variable.
- The bundled `ykeys.exe` is signed by the **YKeys** repo's own release run —
  YTile's signing step runs before the bundle lands, and its folder filter
  only ever sees `ytiled.exe`/`ytile.exe`.
- Basic tier: 5,000 signatures/month — two releases a day would not dent it.
