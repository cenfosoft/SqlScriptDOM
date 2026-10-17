# Adversarial Reviewer Agent for SqlScriptDOM

This directory contains the project documentation for an AI-assisted, adversarial pull request review workflow for `SqlScriptDOM`. The project adds automation around the development process without changing the parser or its grammars as part of the initial implementation.

## Project Documents

- [Adversarial Reviewer Agent Specification](adversarial-reviewer-agent-spec.md) — the source of truth for project scope, architecture, requirements, risk controls, delivery phases, acceptance criteria, and open decisions.
- [Phase 1 Plan and Progress](phase-1-progress.md) — the implementation plan, workstream status, decision log, risks, and stakeholder questionnaire for the report-only foundation.
- [C4 Architecture Model](architecture/workspace.dsl) — the architecture model expressed as Structurizr DSL.
- [Phase 1 Tooling and Operations](../adversarial-review/README.md) — .NET validation commands, contract compatibility, baseline collection, and activation prerequisites.

## Repository Context

- [Upstream repository](https://github.com/microsoft/SqlScriptDOM)
- [Project fork](https://github.com/cenfosoft/SqlScriptDOM)

Development and automation changes are intended for the project fork. The project does not submit automated changes, issues, or pull requests to the upstream Microsoft repository.

## Implementation Status

The specification is currently a draft. Risk thresholds, model selection, token and cost budgets, static-analysis policy, and automatic-integration permissions must be approved before the workflow can act as a production merge gate.
