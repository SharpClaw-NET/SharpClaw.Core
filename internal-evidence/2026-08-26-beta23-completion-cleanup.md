Objective

Prepare SharpClaw.Core 0.5.0-beta.23 with the exact SharpClaw.Contracts 0.5.0-beta.30 dependency. Preserve Core behavior and provide a clean package consumer.

Plan

Update the Core package identity, test dependency, and CI Contracts source pin. Run the complete Core suite. Pack and inspect the unpublished package from pushed source. Run a fresh package-only consumer and record the source and package evidence.

Work

Commit 9993311300c6eadb8a782722c11ea326a112954c changes only the Core beta23 identity, exact Contracts beta30 references, and the exact Contracts source pin in Core CI. The Core behavior is unchanged.

Evidence

The complete Core gate passed 211/211 tests with zero skips. Its TRX SHA-256 is 7F0990341177C1A4147E60E49102C4506E4697FC82A939266628E02ED73C4BE0. Exact-head Core CI run 32986699897 passed for source 9993311300c6eadb8a782722c11ea326a112954c.

The immutable package is D:\temp\SharpClaw.Core\completion-beta23-repair\package-final-2\SharpClaw.Core.0.5.0-beta.23.nupkg. Its length is 279025 bytes. Its SHA-256 is 09AD8E09EF5D75BD4A211DC1B6EA9E7D603A26C00DBE2A1F915434F780E54F2D. The packed DLL SHA-256 is CA7BB58BF2144D698BC5D877060831BC8C7895FF6676B583C787B06513A97868. The packed XML SHA-256 is FFFCAFF08EBA926AB87A6B71C818175EB328BB37049FABF35F9D505C8DD776FB. The packed nuspec SHA-256 is B22FBB3223C978073C17E7D18E49571EDEDDFD21A501A69308CF738CAB323F26. The nuspec records beta23, source 9993311300c6eadb8a782722c11ea326a112954c, the canonical repository, and exact Contracts [0.5.0-beta.30].

The fresh package-only consumer restored beta30 and beta23 from task-local package sources. It built successfully and completed one typed action. It loaded Contracts DLL SHA-256 07467E5B5704120C0FB3826A9EA7DB3384D3301BB04BF7E96773B7A3C7158B7F and Core DLL SHA-256 CA7BB58BF2144D698BC5D877060831BC8C7895FF6676B583C787B06513A97868.

Result

The Core source and package candidate satisfy the local alignment objective. No package was published. The package uses the final pushed Core source commit.

Diff disposition

The source diff contains only package identity, dependency alignment, and the CI source pin. This report contains sanitized evidence only. It contains no credentials, archives, caches, logs, or temporary configurations.

Commit disposition

The source commit is pushed to origin/main. The report commit will be pushed after this report is added. The source tree will be checked for clean status and equal local and remote heads.

Risks

The build reports existing NU1903 warnings for System.Security.Cryptography.Xml 10.0.7. Core behavior was not changed in this turn.

Next bounded turn

Verify the report commit and package provenance. Send the consolidated unpublished package handoff to Codex Overwatch. Keep publication blocked.
