# Code signing

Release binaries are Authenticode-signed by CI through
[Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/)
under an **organization** identity, so the publisher on every binary is the
company, never a person:

```
CN=NineFiveB, O=NineFiveB, L=Sheridan, S=Wyoming, C=US
```

Signing is automatic — GitHub OIDC, no stored secrets, no approval pause.
The steps in [release.yml](../../.github/workflows/release.yml) are gated on
the `AZURE_CLIENT_ID` repository variable, so removing that variable is the
kill switch: releases then ship unsigned instead of failing.

> Releases signed before this setup used an *individual* identity validation,
> which put a legal name and home city into every binary. Those releases were
> withdrawn and re-released under the organization certificate. Never bind a
> certificate profile to an individual validation again — a profile's identity
> validation is fixed at creation and cannot be repointed.

## Azure resources (subscription NineFiveB)

| Resource | Value |
| --- | --- |
| Resource group | `ytile-signing` (East US) |
| Signing account | `aegiosot` — endpoint `https://eus.codesigning.azure.net/` (Basic tier) |
| Identity validation | Organization (NineFiveB) |
| Certificate profile | `release-signing` — Public Trust, daily-rotated 72h certs |
| Managed identity | `ytile-release-signer` — role *Artifact Signing Certificate Profile Signer* |

Identity validations are portal-only; they are not exposed through ARM, so
the certificate profile has to be created with the validation's GUID copied
from the portal (Objects → Identity validations).

## GitHub ↔ Azure trust (no secrets)

The managed identity carries one federated credential per repo, each accepting
GitHub OIDC tokens minted for that repo's **`release` environment**:

- `repo:AegiosOT@2933384/YTile@1330325718:environment:release`
- `repo:AegiosOT@2933384/YKeys@1350104222:environment:release`

Note the account/repo ids in those subjects — GitHub mints rename-proof
subjects, and the classic `repo:owner/name:environment:release` form is
rejected with `AADSTS700213`.

Each repo sets three plain variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
`AZURE_SUBSCRIPTION_ID`. None of them grant anything without an OIDC token
from the `release` environment of these repos.

## Renewals and gotchas

- Identity validation expires and must be renewed in the portal (reminders
  start 60 days ahead). An expired validation fails the signing step.
- The bundled `ykeys.exe` is signed by the **YKeys** repo's own release run —
  YTile's signing step runs before the bundle lands, and its folder filter
  only ever sees `ytiled.exe`/`ytile.exe`. Release YKeys first, then bump
  `YKEYS_VERSION`/`YKEYS_SHA256` in YTile's "Bundle ykeys" step.
- Basic tier allows **one** Public Trust profile, so replacing a profile means
  deleting the old one first.
- Basic tier: 5,000 signatures/month.
