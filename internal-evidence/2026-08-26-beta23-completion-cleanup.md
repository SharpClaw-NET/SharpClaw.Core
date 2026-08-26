Objective

Prepare SharpClaw.Core 0.5.0-beta.23 with the exact SharpClaw.Contracts 0.5.0-beta.30 dependency. Preserve Core behavior and provide a clean package consumer.

Plan

Update the Core CI Contracts source pin. Run the complete Core suite. Run the warnings-as-errors build. Pack and inspect the unpublished package from pushed source. Run a fresh package-only consumer and record the source and package evidence.

Work

Commit a6f6377b886448255da2115f6a12f73f0252f11f updates the Core CI source pin to Contracts commit 9d06cf799dbb3bd65111e8036101bd1632824cab. Commit c5c8953e559f40e9bada0c9bc4f2df0473e6ed27 updates that pin to the final Contracts package source 9c08e846066229c9018e11e26e7f75fa61ddd7d0. Core production behavior and the beta23 package identity remain unchanged.

Evidence

The complete Core gate passed 211/211 tests with zero skips. Its TRX SHA-256 is CE032359D70DA794094826F89ECA576035F4F1E2E9F940F6607691BE3A6E46F1. The strict warnings-as-errors build reports only ten NU1903 advisory errors for System.Security.Cryptography.Xml 10.0.7. The same build with NU1903 excluded from error promotion succeeds with ten warnings and zero errors. Exact-head Core CI run 32989557365 passed for c5c8953e559f40e9bada0c9bc4f2df0473e6ed27.

The replacement package is D:\temp\SharpClaw.Core\completion-beta23-repair-v3-final\package\SharpClaw.Core.0.5.0-beta.23.nupkg. Its length is 279023 bytes. Its SHA-256 is C4FBD44D6EAC7E25F5885205244DB88EB9D10C7279AD41430BDFA49917466165. The packed DLL SHA-256 is 9638942E3AD077DF041878F736C2E7416E46397240E8C4BE9C6E178A0C85D2D1. The packed XML SHA-256 is FFFCAFF08EBA926AB87A6B71C818175EB328BB37049FABF35F9D505C8DD776FB. The packed nuspec SHA-256 is B80851A593211DC59D3934EC0D7A27D186413FED4899B060590B5645D90F3B9E. The nuspec records beta23, source c5c8953e559f40e9bada0c9bc4f2df0473e6ed27, the canonical repository, and exact Contracts [0.5.0-beta.30].

The fresh package-only consumer restored beta30 and beta23 from task-local package sources. It built successfully and completed one typed action. It loaded Contracts DLL SHA-256 DF2ADE2596BE3A54482E7DFDB29560543CD3EA0D74C64FC100F767CCB1853A85 and Core DLL SHA-256 9638942E3AD077DF041878F736C2E7416E46397240E8C4BE9C6E178A0C85D2D1. The consumer run log SHA-256 is ECBD72A85A52E557D779AAA660DA810C44E82D76AAE90A13F72F5940A3CE8024.

Result

The Core alignment, complete suite, warnings-as-errors boundary, package, consumer, and exact-head CI gates pass. No package was published.

Diff disposition

The source diff contains only the CI Contracts source pin. This report contains sanitized evidence only. It contains no credentials, archives, caches, logs, or temporary configurations.

Commit disposition

The source commits are pushed to origin/main. This report will be pushed in a separate documentation commit. The working tree will be checked for clean status and equal local and remote heads.

Risks

The strict warnings-as-errors build remains blocked by existing NU1903 advisories. Core behavior was not changed in this turn. The CI run reports existing XML documentation annotations, one nullable warning, and Node.js 20 action deprecation annotations.

Next bounded turn

Commit and push this sanitized evidence. Send the consolidated unpublished package handoff to Codex Overwatch. Keep publication blocked.
