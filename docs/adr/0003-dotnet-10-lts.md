# ADR-0003: Target .NET 10 LTS instead of .NET 8

- **Status:** Accepted
- **Date:** 2026-11-01
- **Deciders:** Arihant Jain
- **Spec references:** §5, §5.2 (specified .NET 8)

## Context

The spec names .NET 8. Microsoft support for .NET 8 (LTS) ends on **10 November 2026**, nine days after this project
starts. After that date it receives no security patches. Base container images would be flagged by image scanners, and
starting a new security-focused platform on an unsupported runtime would undercut the project's credibility.

.NET 10 is the current LTS release (November 2025), supported until November 2028.

## Decision

- Target `net10.0` for all projects (set centrally in `Directory.Build.props`).
- Pin the SDK band with `global.json` (`10.0.100`, `rollForward: latestFeature`).
- Use `mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0` images.
- Use the `.slnx` solution format (the default since .NET 10).

## Consequences

**Positive:** supported runtime for the project's whole lifetime; current C# language features; the ASP.NET Core
improvements matter here (built-in validation, OpenAPI 3.1).

**Negative:** some third-party packages may lag on explicit .NET 10 support. In practice packages targeting
`net8.0` or `netstandard2.0` work fine on .NET 10.

## Alternatives considered

| Option | Why not |
| --- | --- |
| .NET 8 as specified | Out of support within days of project start |
| .NET 9 (STS) | Shorter support window than 10 and no advantage |

## Revisit when

.NET 12 LTS ships (November 2027). Plan the upgrade before .NET 10 reaches end of support in November 2028.
