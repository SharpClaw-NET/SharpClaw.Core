# SharpClaw.Core

SharpClaw.Core is the AGPL-3.0 host-agnostic behavior package for SharpClaw. It
provides shared chat and provider pipelines, runtime state, module registry behavior,
resource management, permissions, and tool infrastructure. It does not provide an
application host, API, CLI, database, migrations, sidecar launcher, or UI. It consumes
SharpClaw.Contracts, the MIT-licensed contract package, through NuGet and requires a
host to provide stores, provider clients, clocks, logging, metrics, and dependency
injection.
