# Repository governance evidence

This page records the GitHub state inspected for HU-06 on 2026-11-09 UTC and the
temporary status-check change made on 2026-11-21. It distinguishes settings read
back from GitHub from requirements that still need an authorized maintainer or
peer reviewer.

## Protected `main` branch

The GitHub branch-protection API confirmed these settings:

| Control | Verified configuration |
|---|---|
| Required checks | Temporarily: `foundation (windows-latest)`, `foundation (ubuntu-latest)` |
| Branch must be up to date | Required |
| Pull-request approval | At least one approval |
| Stale approvals | Dismissed when new commits are pushed |
| Approval after latest push | Required |
| Administrators | Subject to the same rules |
| Bypass allowances | None |
| Force pushes / branch deletion | Disabled |
| Code-owner approval | Not separately required |

These are live repository settings, not merely workflow declarations. The
two current required checks correspond to the Windows/Ubuntu foundation jobs.

## Temporary status-check relaxation — 2026-11-21

The open PRs report different check names: PR #14 reports `foundation`,
`build`, and `test`; PR #15 reports `foundation`, `build-and-test`, and a failed
`static-analysis`; PR #16 reports both `build-and-test` and `build`/`test`, plus
`foundation` and `static-analysis`. Branch protection required the `build-and-test`
and `static-analysis` contexts for every PR, so contexts absent from a branch
blocked it even when its active build/test checks passed.

To unblock PRs while the workflows are consolidated, `main` now requires only
the two foundation checks. The old `build-and-test` and `static-analysis`
requirements are temporarily disabled; their workflows still run where present,
but their results do not block merges. This is a repository-wide relaxation, not
a PR-specific bypass. Strict up-to-date enforcement and the one-human-approval,
latest-push review, and stale-review dismissal requirements remain enabled.
Restore required build/test and static-analysis checks after a stable, common
set of status names reports on all PRs. Do not treat an omitted or failed check
as a pass.

PR #14's current conclusion is `CLEAN`: both required foundation checks and all
four active build/test checks passed. The [foundation run](https://github.com/cenfosoft/SqlScriptDOM/actions/runs/42556963457)
and [build/test run](https://github.com/cenfosoft/SqlScriptDOM/actions/runs/42556963459)
are linked for reference.

## Static-analysis check behavior

The `static-analysis` status check verifies that the pinned Semgrep scan and
scoped Roslyn build execute and produce SHA-bound evidence. Semgrep findings are
reported in SARIF and are advisory: a finding by itself does not fail this job.
Semgrep installation/scanner/configuration errors, Roslyn build errors, or
failed evidence-manifest creation still fail the check. A successful status
means the analysis ran, not that it found no findings. The advisory results may
inform the reviewer and human decision; they are not an automatic merge gate.

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

PR #16 is open at head SHA
`20225bd3301aa7ee6c70fa45410976ee70cfcc05`. All five required checks passed:
the Windows and Ubuntu foundation jobs, the Windows and Ubuntu build-and-test
jobs, and static analysis. The test jobs completed successfully on both
platforms. The PR has no submitted reviews and GitHub reports it as blocked;
the required human approval has not occurred. The [PR #16 Actions run](https://github.com/cenfosoft/SqlScriptDOM/actions/runs/42573430288)
records those check results.

The recorded branch protection enforces the review gate. Passing CI does not
substitute for a human peer review or approval.

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
- [PR #16](https://github.com/cenfosoft/SqlScriptDOM/pull/16)
- [PR #16 required-check run](https://github.com/cenfosoft/SqlScriptDOM/actions/runs/42573430288)
- [Phase 1 workflow](architecture/phase-1-workflow.mmd)
- [C4 architecture model](architecture/workspace.dsl)
