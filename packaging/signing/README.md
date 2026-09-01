# Code signing

**Signing is currently disabled.** Releases ship unsigned until the
organization certificate below is in place.

## Why it is paused

Releases v0.1.3–v0.1.5 were signed through
[Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/)
using an **individual** identity validation. Azure issues those certificates
against a verified legal identity, so every signed binary carried the
maintainer's legal name, city and state in its Authenticode subject —
readable by anyone via Properties → Digital Signatures. The project is
published under the **AegiosOT** handle, so that disclosure was withdrawn:
those releases were deleted and the `AZURE_CLIENT_ID` repository variable was
removed, which makes the signing steps in
[release.yml](../../.github/workflows/release.yml) skip.

Nothing else about the Azure setup was torn down. The signing account
(`aegiosot`, East US), the managed identity `ytile-release-signer`, and both
repos' GitHub OIDC federated credentials are intact, as are the
`AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` variables.

## Resuming under an organization

Organization validation puts the **company name** on the certificate instead
of a person, which is the outcome we want. Steps, in order:

1. **Register the legal entity** (NineFiveB). Use a registered-agent address
   if the registered address should not be a home address — the certificate
   carries the entity's city and state, and the filing itself is public record.
2. **Get the supporting identifiers** Azure's org validation checks against
   public records: EIN, and a D-U-N-S number if requested (free from Dun &
   Bradstreet, allow a few days).
3. **Azure portal** → the `aegiosot` signing account → **Identity validations**
   → new validation of type **Organization**, with the entity's legal name,
   address, and a domain-matched contact email. Approval is not instant.
4. **Certificate profile**: create a new Public Trust profile bound to that
   validation (the old personal profile and validation should be deleted at
   this point so nothing can sign with them again).
5. **Re-enable CI**: add the `AZURE_CLIENT_ID` repository variable back to
   both repos (the managed identity's client id — the other two variables are
   still there). Nothing in the workflows needs editing; the gated steps
   light up on their own.
6. Cut a release and confirm the signature subject shows the organization,
   not a person, before publishing anything further.

Until step 5, every release is unsigned: expect Smart App Control blocks,
SmartScreen warnings, and a likely Defender false-positive on winget
submissions — the reasons signing was introduced in the first place.

## Cost note

The Artifact Signing account bills ~$9.99/month whether or not it signs
anything. If the organization route is going to take a while, deleting the
account and recreating it later avoids paying for an idle service; the
managed identity and federated credentials can stay.
