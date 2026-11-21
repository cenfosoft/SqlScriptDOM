# AI usage and prompt traceability

This log records only verifiable task metadata. It does not retain full chat
transcripts, model responses, source excerpts, credentials, or other sensitive
context. A prompt reference is not evidence that a model inference occurred.

## Tool and model inventory

| Tool or model | Use in this work | Evidence / qualification |
|---|---|---|
| GitHub Copilot SDK in VS Code | Coding assistant used for the HU-05/HU-06 analysis, implementation, and validation in this session. | The active model identifier is not exposed in the session evidence; no model name is inferred. |
| GitHub Copilot CLI | Not invoked for this work. | It was named in the requested inventory, but there is no CLI execution evidence. |
| `gpt5.4-mini` | Candidate named in the request; not verified as selected or invoked in this session. | Treat as a candidate, not an observed model run. |
| `claude-haiku-4.5` | Candidate named in the request; not verified as selected or invoked in this session. | Treat as a candidate, not an observed model run. |
| GitHub REST API | Read-only issue, pull request, collaborator, workflow, and check inspection; authorized repository-settings verification and configuration are recorded separately. | No write was made to source repositories during metric collection. |

Model discovery or candidate configuration in the repository is not proof of
inference or deployment. The model selected by the Copilot SDK and its execution
entitlement must be recorded from runtime evidence before reporting model usage.

## Prompt catalog

| ID | Task / HU | Prompt reference and safe summary | Result / evidence |
|---|---|---|---|
| AI-2026-11-09-01 | HU-05, issue [#12](https://github.com/cenfosoft/SqlScriptDOM/issues/12) | User request in the implementation session: inspect HU-05, complete the C# Lead Time collection and JSON/Markdown report, compare fork and upstream, and record AI usage. The full chat prompt is not copied into the repository. | Metrics code, tests, and runbook changes in `adversarial-review/`; verifiable through the associated branch commit and PR. |
| AI-2026-11-09-02 | HU-06, issue [#13](https://github.com/cenfosoft/SqlScriptDOM/issues/13) | User request in the same session: inspect HU-06, align repository governance, require human peer review and CI, and add renderable architecture/process diagrams. The full chat prompt is not copied into the repository. | Governance evidence, protected-branch configuration, and diagrams documented in this change; acceptance remains open until the functional checks and team review are verified. |
| AI-2026-11-09-03 | HU-05 / D-007 | The user was asked to resolve the conflict between the current request's “PR opened” wording and approved decision D-007. The response selected “first ready-for-review → merge,” retaining D-007. | The metric definition and user decision are recorded in `docs/phase-1-progress.md`; open PRs remain separately right-censored. |
| PROMPT-REVIEW | Phase 1 advisory review | Versioned template: [adversarial-review.prompt.md](../.github/prompts/adversarial-review.prompt.md). | The template exists in the repository; its use for a specific model run is not asserted here. |
| PROMPT-IMPLEMENT | Parser feature implementation | Versioned template: [new-feature-implementation.prompt.md](../.github/prompts/new-feature-implementation.prompt.md). | Available template; not recorded as used for this HU-05/HU-06 change. |
| PROMPT-VERIFY-TSQL | T-SQL syntax verification | Versioned template: [verify-and-test-tsql-syntax.prompt.md](../.github/prompts/verify-and-test-tsql-syntax.prompt.md). | Available template; not recorded as used for this HU-05/HU-06 change. |

Task-specific prompts are summarized, not retained verbatim. The GitHub issue,
this catalog entry, and the repository diff provide a reviewable reference. If a
historical interaction cannot be recovered, it is marked unavailable rather
than reconstructed as an exact prompt.

## Retention and privacy

- Do not commit full prompt transcripts, model responses, PR source content, or
  tokens by default.
- Keep any raw metric snapshot outside the repository until the project's
  retention, access, and publication policy is approved. The generated report
  contains public PR identifiers and timestamps because source provenance is
  required; use the local report outputs only for authorized analysis.
- Record only model/tool identifiers that are present in execution evidence.
  Candidate lists and model discovery do not establish that a model was called.

## Commit attribution

AI-assisted commits created for this work must include:

```text
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
```

The commit message is the authoritative evidence; verify with
`git show -s --format=%B <commit>`. The following implementation and
CI-correction commits were checked in local history and contain the trailer:

| Commit | Subject |
|---|---|
| [`a68e93a`](https://github.com/cenfosoft/SqlScriptDOM/commit/a68e93a39ef217657be6ee2e12b05467745936aa) | docs: record draft team-review PR and refreshed validation |
| [`ea9328d`](https://github.com/cenfosoft/SqlScriptDOM/commit/ea9328d) | feat: integrate Copilot SDK discovery and report-only provider |
| [`be41298`](https://github.com/cenfosoft/SqlScriptDOM/commit/be41298) | feat: verify trusted PR authorization and validation metadata |
| [`9637918`](https://github.com/cenfosoft/SqlScriptDOM/commit/9637918) | feat: ingest bounded SHA-bound CI evidence safely |
| [`047ef81`](https://github.com/cenfosoft/SqlScriptDOM/commit/047ef81) | feat: orchestrate durable report-only review cycles and publication |
| [`abec65e`](https://github.com/cenfosoft/SqlScriptDOM/commit/abec65e) | feat: add trusted preparation-only Actions entry point |
| [`3153a0b`](https://github.com/cenfosoft/SqlScriptDOM/commit/3153a0b) | feat: add HU-05 metrics and HU-06 governance |
| [`20225bd`](https://github.com/cenfosoft/SqlScriptDOM/commit/20225bd) | fix: accept static-analysis evidence manifests |

This table lists the HU-05/HU-06 implementation commits and representative
prior work. Verify every commit in the pull-request history, including later
documentation updates, with `git log --format="%h %s%n%b"`; do not infer trailer
presence from this inventory alone.
