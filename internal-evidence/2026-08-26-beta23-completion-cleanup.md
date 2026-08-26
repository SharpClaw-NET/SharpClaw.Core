Objective

Prepare SharpClaw.Core 0.5.0-beta.23 with the exact SharpClaw.Contracts 0.5.0-beta.30 dependency. Preserve Core behavior and provide a clean package consumer.

Plan

Update the Core CI Contracts source pin. Run the complete Core suite. Run the warnings-as-errors build. Pack and inspect the unpublished package from pushed source. Run a fresh package-only consumer and record the source and package evidence.

Work

Commit a6f6377b886448255da2115f6a12f73f0252f11f updates only the Core CI source pin to Contracts commit 9d06cf799dbb3bd65111e8036101bd1632824cab. Core production behavior and the beta23 package identity remain unchanged.

Evidence

The complete Core gate passed 211/211 tests with zero skips. Its TRX SHA-256 is CE032359D70DA794094826F89ECA576035F4F1E2E9F940F6607691BE3A6E46F1. The strict warnings-as-errors build reports only ten NU1903 advisory errors for System.Security.Cryptography.Xml 10.0.7. The same build with NU1903 excluded from error promotion succeeds with ten warnings and zero errors.

The replacement package is D:\temp\SharpClaw.Core\completion-beta23-repair-v2-final\package\SharpClaw.Core.0.5.0-beta.23.nupkg. Its length is 279027 bytes. Its SHA-256 is E707AFAC4F7CD769BDE1C32A7FAC8A127EC84A722CF788B6ECE3DD157FCEBAA3. The packed DLL SHA-256 is 682D0144275DA87195347EFF388139891D3D6FFA8250F1C5F6FFD381CCD32448. The packed XML SHA-256 is FFFCAFF08EBA926AB87A6B71C818175EB328BB37049FABF35F9D505C8DD776FB. The packed nuspec SHA-256 is 3A45E634A2A642932AD682BB7A8B8DC509935FB87D51E5B3CF73836C18260158. The nuspec records beta23, source a6f6377b886448255da2115f6a12f73f0252f11f, the canonical repository, and exact Contracts [0.5.0-beta.30].

The fresh package-only consumer restored beta30 and beta23 from task-local package sources. It built successfully and completed one typed action. It loaded Contracts DLL SHA-256 A7C75B47C11194AF46BF6684C57FBDB38A4075136F4C625DEE2CD6AADD3A6A66 and Core DLL SHA-256 682D0144275DA87195347EFF388139891D3D6FFA8250F1C5F6FFD381CCD32448. The consumer run log SHA-256 is ECBD72A85A52E557D779AAA660DA810C44E82D76AAE90A13F72F5940A3CE8024.

Result

The Core alignment and package consumer gates pass. No package was published.

Diff disposition

The source diff contains only the CI Contracts source pin. This report contains sanitized evidence only. It contains no credentials, archives, caches, logs, or temporary configurations.

Commit disposition

The source commit is pushed to origin/main. The report commit will follow after CI status is checked. The working tree will be checked for clean status and equal local and remote heads.

Risks

The strict warnings-as-errors build remains blocked by existing NU1903 advisories. Core behavior was not changed in this turn. Contracts CI has no run for source 9d06cf799dbb3bd65111e8036101bd1632824cab yet.

Next bounded turn

Confirm exact-head CI status. Commit and push this sanitized evidence. Send the consolidated unpublished package handoff to Codex Overwatch. Keep publication blocked.
