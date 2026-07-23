# SharpClaw.Core

SharpClaw.Core is the host-agnostic business pipeline package for SharpClaw.
It is intended to contain the canonical SharpClaw state machines, capability
rules, orchestration flow, and storage port contracts without bringing along
the SharpClaw application server.

This repository is intentionally not a runnable SharpClaw application. It does
not provide an API server, CLI, database, migrations, sidecar launcher, default
module bundle, or UI. A host application embeds SharpClaw.Core and supplies
stores, module invokers, provider clients, clocks, metrics, event sinks, and
configuration.

`SharpClaw.Contracts` is the MIT-licensed module and provider contract package.
`SharpClaw.Core` is the AGPL-3.0 host-agnostic behavior package. Core consumes
Contracts through its NuGet package dependency, not through a project reference,
so the package boundary is the same during local development and after
publishing.

Contracts distinguishes pipeline-only modules from application runtime modules.
An `ISharpClawCoreModule` can add providers, tools, and transcription
hooks, resources, permissions, storage contracts, and other pure pipeline
behavior. An `ISharpClawRuntimeModule` extends that same core module contract
and can also publish CLI commands, API endpoints, gateway routes, and frontend
contributions for a SharpClaw application runtime. The runtime module contract
is a superset; runtime modules can make core pipeline additions, but core
modules cannot publish application surfaces.

The Core package now owns the host-independent parts of the runtime pipeline.
It contains the host-agnostic business pipeline, state, module, and provider
infrastructure; the module registry and its capability, storage-contract,
protocol-contract, CLI-command, resource, flag, header-tag, runtime-host, and
initialization-order state machines; provider plugin selection and completion
parameter validation;
default resource keys; event sink dispatch; module metrics; and sidecar
capability telemetry contracts. These pieces can run in any host that supplies
the actual stores, process boundaries, provider HTTP clients, clocks, logging,
and dependency-injection container.

The SharpClaw application repository remains responsible for application
infrastructure. It still owns ASP.NET endpoints, CLI dispatch, database models,
EF Core stores, migrations, sidecar process launch, in-process module loading,
gateway routing, frontend assets, and bundled module composition. For example,
Core can decide that a module's storage contract declares an indexed
`scheduled_jobs` store with a maximum document size, but the application host is
the component that maps that contract to EF entities, enforces row ownership,
and persists the data.

The solution can be built from this repository root.

```powershell
dotnet build SharpClaw.Core.slnx
```
