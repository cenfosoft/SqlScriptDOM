# Repository governance evidence

This page records the GitHub state inspected for HU-06 on 2026-11-09 UTC. It
distinguishes settings read back from GitHub from requirements that still need
an authorized maintainer or peer reviewer.

## Protected `main` branch

The GitHub branch-protection API confirmed these settings:

| Control | Verified configuration |
|---|---|
| Required checks | `foundation (windows-latest)`, `foundation (ubuntu-latest)`, `build-and-test (windows-latest)`, `build-and-test (ubuntu-latest)`, `static-analysis` |
| Branch must be up to date | Required |
| Pull-request approval | At least one approval |
| Stale approvals | Dismissed when new commits are pushed |
| Approval after latest push | Required |
| Administrators | Subject to the same rules |
| Bypass allowances | None |
| Force pushes / branch deletion | Disabled |
| Code-owner approval | Not separately required |

These are live repository settings, not merely workflow declarations. The
required checks correspond to the Windows/Ubuntu foundation and parser
build/test jobs plus the static-analysis job.

## Issues and milestones

Issues are enabled. HU-01 through HU-06 (issues #8–#13) were open at inspection,
assigned to `allancascante`, labeled, and associated with milestones:

| Issue | Milestone |
|---|---|
| #8 — HU-01 | Ingeniería de Prompts y Reglas LLM |
| #9 — HU-02 | Automatización CI/CD |
| #10 — HU-03 | Automatización CI/CD |
| #11 — HU-04 | Diseño del Control de Bucles |
| #12 — HU-05 | Despliegue y Monitoreo de Lead Time |
| #13 — HU-06 | Kick-off y Análisis Base |

All six milestones exist and are open. Their descriptions retain the official
UTC end-of-day deadlines (`23:59:59Z`); GitHub normalizes the API `due_on` field
to `00:00:00Z` on the same calendar date. No milestone dates were moved.

## CI and peer-review evidence

PR #15 was open and had no submitted reviews when checked. At head SHA
`0341238257529d57204a232dffe4feb7f2b61c95`, both foundation checks and both
`build-and-test` checks passed, while `static-analysis` failed. Its failure was
in evidence-manifest creation because the runner label `ubuntu-latest` was not
accepted as the manifest platform; the Roslyn and Semgrep steps themselves
completed. This change normalizes GitHub runner labels and adds regression
coverage. PR #15 still needs a fresh check run before its failure can be
considered resolved.

The new change's own required checks and a human peer review must pass before
it can be merged. The recorded branch protection enforces that gate; this
document does not claim that a review or approval has already occurred.

## Protected Actions environment: outstanding

The GitHub environment `adversarial-review` did not exist at inspection (the
environment API returned 404). The preparation workflow names this environment,
but that YAML reference alone does not create reviewer protection. An
authorized maintainer must create it, select the approved reviewers, and
configure its protection rules before it can serve as an approval gate.

The reviewer roster/rotation and the team's recorded agreement on the review
policy have not been verified. No screenshot of an approval is available while
there is no configured environment or completed review. These are operational
acceptance items, not evidence that can be supplied by a code change alone.

## Evidence links

- [HU-05: Lead Time metrics and AI prompt traceability](https://github.com/cenfosoft/SqlScriptDOM/issues/12)
- [HU-06: Repository governance and peer reviews](https://github.com/cenfosoft/SqlScriptDOM/issues/13)
- [PR #15](https://github.com/cenfosoft/SqlScriptDOM/pull/15)
- [PR #15 static-analysis run](https://github.com/cenfosoft/SqlScriptDOM/actions/runs/42565158519)
- [Phase 1 workflow](architecture/phase-1-workflow.mmd)
- [C4 architecture model](architecture/workspace.dsl)
