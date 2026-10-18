# Phase 1 Plan and Progress: Report-Only Foundation

| Field | Value |
|---|---|
| Phase | Phase 1 — Report-Only Foundation |
| Status | In progress — trusted integration and pilot blocked by outstanding approved inputs |
| Progress | Foundation, bounded engine, SQLite ledger, and Copilot SDK adapter implemented; metadata-only account discovery verified; 114 tests pass; paid execution disabled |
| Last updated | October 16, 2026 |
| Source specification | [Adversarial Reviewer Agent Specification](adversarial-reviewer-agent-spec.md) |
| Target repository | [cenfosoft/SqlScriptDOM](https://github.com/cenfosoft/SqlScriptDOM) |

## 1. Phase Objective

Deliver a safe, observable, report-only adversarial review workflow for qualifying pull requests. Phase 1 collects deterministic evidence, asks an LLM reviewer for schema-valid findings, computes a reproducible provisional risk report, publishes one non-blocking pull request comment, and records baseline metrics. It MUST NOT modify contributor code, approve a pull request, mark a pull request merge-ready, or merge changes.

The pilot will run in the `cenfosoft/SqlScriptDOM` fork, but its workflows, configuration, and documentation must be suitable for proposing upstream to `microsoft/SqlScriptDOM`. All pull requests are in scope, including pull requests from external forks and branches in the same repository.

Deterministic CI runs for every non-draft pull request. Paid LLM review runs only after both matrix legs pass and an explicitly authorized maintainer opts in for the current head SHA. Draft pull requests are skipped.

## 2. Phase 1 Goals

Phase 1 addresses every foundation goal from the specification:

| Goal | Phase 1 outcome | Completion evidence |
|---|---|---|
| Define finding and audit-report schemas | Versioned JSON Schemas and validated examples | Schema validation tests pass for valid and invalid fixtures. |
| Add deterministic checks and artifact publication | Existing build/test checks plus static analysis emit normalized results and raw artifacts | A pilot PR run contains complete, downloadable artifacts tied to its head SHA. |
| Implement adversarial review without code mutation | A reviewer receives bounded context and emits findings only | Workflow permissions and tests demonstrate no content-write path. |
| Publish a non-blocking PR comment | One marker-delimited comment is created or updated per PR | Re-running on the same PR updates rather than duplicates the comment. |
| Establish baseline metrics | Lead time, latency, cost, escalation, and finding-disposition data are collected | A baseline dataset and documented measurement method exist. |

## 3. Definition of Done

Phase 1 is complete only when all of the following are true:

- [ ] A qualifying pull request run is bound to the current head SHA.
- [ ] Paid inference requires a one-shot authorization from an allowlisted maintainer for that exact head SHA.
- [ ] Build, test, and static-analysis results are preserved as raw artifacts.
- [ ] Tool outputs are normalized into versioned schema-valid records.
- [ ] The reviewer prompt uses bounded context and treats pull request content as untrusted data.
- [ ] Reviewer output is rejected when it does not satisfy the schema.
- [ ] A deterministic component produces the provisional risk score; the LLM cannot override blocking evidence.
- [ ] One audit comment is created or updated without acting as a merge gate.
- [ ] The workflow cannot modify source code, approve, label as merge-ready, or merge.
- [ ] Fork pull requests do not receive protected secrets in an untrusted execution context.
- [ ] Time, token, retry, and cost limits are configured and reported.
- [ ] Safe, unsafe, malformed-output, stale-SHA, and unavailable-provider scenarios are tested.
- [ ] Baseline metric definitions, collection method, and retention policy are documented.
- [ ] Operating instructions explain reruns, failures, escalation, and disabling the workflow.

## 4. Current-State Inventory

Completed during planning on October 16, 2026:

- [x] Located the existing PR workflow at `.github/workflows/pr-validation.yml`.
- [x] Confirmed CI currently builds and tests on Windows and Ubuntu.
- [x] Confirmed actions are pinned to immutable commit SHAs.
- [x] Confirmed the original workflow had no explicit top-level permission declaration; added read-only permissions in P1-03.
- [x] Confirmed there is no Semgrep configuration or static-analysis report pipeline.
- [x] Confirmed the initial repository lacked normalized schemas, reviewer prompt, audit engine, PR audit comment, and metrics collector; offline contracts/reporting/metrics are now implemented, while live PR reporting remains pending.
- [x] Confirmed repository-specific authoring guidance already exists under `.github/instructions/`, `.github/prompts/`, and `.agents/skills/`.
- [x] Confirmed the repository pins .NET SDK `8.0.420` but has no existing Python or Node project for orchestration.
- [x] Selected .NET/C# as the only implementation runtime for orchestration, schemas, provider adapters, metrics, and reporting.
- [x] Confirmed the original C4 model was context-only; expanded it with Phase 1 containers and trust boundaries during P1-01.

## 5. Proposed Phase 1 Architecture

The recommended design separates untrusted pull request execution from trusted reporting.

### 5.1 Untrusted Validation Workflow

Runs on `pull_request` with read-only permissions and no LLM secret. It checks out and executes contributor code, then publishes immutable evidence:

1. Capture PR number, repository, base SHA, and head SHA.
2. Build and test using the existing repository commands.
3. Run static analysis using a pinned Semgrep version and repository-owned rules.
4. Normalize results where safe to do so.
5. Upload raw logs, normalized findings, metadata, and checksums as artifacts.

The existing Windows and Ubuntu matrix remains authoritative and unchanged in Phase 1. The report workflow waits for successful evidence from both matrix legs before any paid model call.

### 5.2 Trusted Report Workflow

Runs in a trusted context after validation completes. It MUST NOT execute code from the pull request. It:

1. Verifies the triggering repository, workflow identity, conclusion, PR number, and head SHA.
2. Downloads and validates the evidence artifacts.
3. Obtains the PR diff and approved repository guidance as data.
4. Builds a size-limited review context.
5. Calls the selected LLM provider using a protected credential.
6. Validates reviewer output against the finding schema.
7. Computes a provisional risk score deterministically.
8. Uploads the final audit JSON and updates one non-blocking PR comment.

The split is approved. The event topology, replay protection, provider authentication, and numeric limits still require implementation and verification before secret-bearing execution can be enabled.

### 5.3 Why the Workflows Are Split

The split prevents untrusted pull request code from accessing credentials:

- The **validation workflow** checks out and executes the proposed change. Because an external contributor can modify build files and scripts, this workflow must have no LLM secret and only read access to repository contents.
- The **report workflow** receives the LLM secret and permission to update one PR comment. It consumes the validation artifacts and PR diff as data, but never checks out or executes code from the contributor's branch.

Putting both responsibilities in one secret-bearing `pull_request` job would allow a malicious PR to alter a build script and attempt to steal the LLM credential. Using `pull_request_target` to execute the contributor branch would create the same risk. The two-workflow design is therefore the recommended default for supporting both external-fork and same-repository pull requests.

This trusted/untrusted split is approved for Phase 1.

### 5.4 Configurable Model Pool and Rotation

The reviewer must not be tied to one model. A repository-owned JSON configuration file will define an ordered pool of provider/model entries and may contain multiple models for the same provider. Each review cycle will record the provider and model used, then select the next eligible entry so independent models can attempt to disprove earlier conclusions.

The selection policy must:

1. Read only repository-approved provider and model identifiers from configuration.
2. Exclude entries that lack configured credentials or are disabled.
3. Avoid models already used for the current PR head SHA until every eligible model has been used, when possible.
4. Restart at the first eligible configured entry after the end of the list is reached.
5. Persist cycle history in the marker-delimited PR audit comment and JSON artifacts.
6. Continue with the sole eligible model when only one is available.
7. Define deterministic fallback behavior when providers fail.
8. Record selection reason, provider, model, cycle number, prompt version, usage, latency, and result.
9. Keep credentials outside the configuration file and map configured providers to GitHub secret names.

Providers may retain requests. The approved adapter uses the .NET GitHub Copilot
SDK with an extensible provider interface. Local account discovery is verified;
protected-host credentials, Actions entitlement, exact billing controls, and retention
disclosures still need deployment verification.

**Provider availability update (October 16, 2026):** The current
[GitHub Models documentation](https://docs.github.com/en/github-models) states
that GitHub Models was fully retired on July 30, 2026, including its catalog,
inference API, and BYOK. GitHub Copilot is a separate service. The approved
"GitHub-provided models" starting point therefore cannot be implemented as a
GitHub Models API adapter. This retirement does **not** mean Copilot SDK is unavailable.
The owner subsequently approved the Copilot SDK recommendation (D-023); that is an
explicit provider choice, not a silent substitution of subscription credentials.
Keep paid execution disabled until CI entitlement, billing enforcement, and limits
are verified.

**Copilot integration update (October 18, 2026 UTC):** Pinned
`GitHub.Copilot.SDK` 1.0.22/native runtime 1.0.98, added `list-models`, and verified
metadata-only discovery with the existing local account. No inference prompt or
repository content was sent. The catalog returned 23 entries; selected policy-enabled
candidates are `gpt-5.4-mini`, `claude-haiku-4.5`, and `mai-code-1.1-flash`.
They are configured in that order under `github-copilot`; global execution remains
disabled and all unapproved limits remain null. Gemini was not in the returned
catalog. Unknown policy entries and `auto` are not inference-eligible. See the
[integration/runbook](../adversarial-review/README.md#github-copilot-sdk-and-metadata-only-model-discovery).
The report-only adapter has offline coverage, but live inference, billing conversion,
and hard cap behavior have **not** been verified; SDK session ceilings are not a
substitute for approved provider-side spending enforcement.

### 5.5 Authorized Paid-Review Gate

An arbitrary contributor must not be able to trigger paid inference. The initial gate will use a repository-configured opt-in label, subject to these requirements:

1. Only an actor in the configured authorization allowlist may initiate the paid review.
2. The trusted workflow must verify the label event actor rather than trusting label presence alone.
3. Authorization is one-shot and bound to the PR head SHA current at the time of approval.
4. A new commit invalidates the authorization and requires the label to be applied again by an authorized actor.
5. The workflow must prevent duplicate or concurrent paid runs for the same PR head SHA and cycle.
6. The audit record must include the authorizing actor, authorization time, label, and authorized head SHA.
7. Missing authorization, failing deterministic checks, or a draft state must result in no provider call and no token spend.

The initial label is `adversarial-review:run`, and the initial authorized GitHub login is `allancascante`. Applying the label authorizes one complete configured review cycle for the current head SHA. The cycle may call multiple models according to the configured round count and ordered model pool.

Provider access must be protected by a GitHub environment requiring approval. The allowlist identity is an authorization control only; account passwords or interactive account credentials must never be stored in the repository or workflow.

### 5.6 Trust Boundaries and Operating Policy

The [C4 context and container model](architecture/workspace.dsl) describes the P1-01 design, not a deployed workflow. The boundaries apply equally to fork and same-repository PRs.

| Boundary | Allowed data flow | Required control |
|---|---|---|
| Contributor to validation | PR source and build instructions | No provider credentials; read-only repository permissions. |
| Validation to trusted reporting | Raw artifacts and manifests | Treat all artifact contents as untrusted. Verify the expected repository, workflow, run attempt, PR, and head SHA using GitHub metadata, then validate schema, size, paths, and checksums. A checksum alone does not establish trust. |
| Repository to trusted reporting | Orchestrator, prompts, configuration, and analysis policy | Use the trusted default-branch revision, not versions supplied by the PR. Contributor changes to policy are review data only until merged. |
| Maintainer to paid execution | Label event and environment approval | Verify the authenticated event actor, bind approval to the exact head SHA, and claim a one-shot authorization before provider access. Label presence alone is insufficient. |
| Trusted reporting to provider | Bounded diff and selected guidance | Send data only after all prerequisites pass. Give the model no execution or repository-write tools. Enforce approved budgets before every request. |
| Trusted reporting to GitHub | Advisory comment and JSON artifacts | Limit writes to reporting; never mutate source, submit approvals, mark merge-ready, or merge. Escape contributor-controlled Markdown and recheck the head SHA before publication. |

The report must record the trusted configuration revision alongside the evaluated head SHA. A model response is untrusted input, not executable instructions.

Authorization may arrive before or after CI completes. Neither event alone starts inference: the trusted orchestrator must join both pieces of evidence for the same SHA and recheck the PR state after environment approval and before each model round. New commits, draft conversion, or closure stop further calls. A persistent label does not authorize another SHA.

Model history in the bot-authored audit comment and JSON artifacts supports rotation and traceability, but is not by itself a spend ledger or replay lock. P1-04 now includes a tested atomic SQLite claim and full-cycle monthly reservation implementation for a shared persistent host. A fresh database on each hosted Actions runner does not satisfy this requirement. Deployment/storage provisioning and authenticated event admission remain unverified, so paid execution stays disabled.

### 5.7 Failure Policy and Verification Cases

These are implementation requirements for the approved boundaries, not a replacement for the authoritative CI checks. A skipped or incomplete advisory report must not turn failed CI green.

| Condition | Provider-call behavior | Required visible outcome / verification case |
|---|---|---|
| Missing opt-in, unauthorized actor, missing environment approval, or disabled execution | No calls | Record a skipped reason without exposing secrets; a contributor-applied label must not authorize inference. |
| PR is draft or closed | No new calls | Record skipped state. Test both states before starting and between model rounds. |
| Head SHA changes after authorization or while awaiting approval | No new calls | Invalidate authorization and mark the run stale; do not overwrite a newer report. |
| Windows or Ubuntu build/test evidence is missing, pending, cancelled, or failed | No calls | Identify the unavailable or failed checks and retain available raw evidence. Test each matrix leg independently. |
| Required Semgrep/Roslyn policy or successful analysis evidence is unavailable | No calls | Report incomplete deterministic evidence. Do not represent a pending team-supplied policy as a passing check. |
| Artifact origin, run attempt, SHA, checksum, schema, size, or safe-path validation fails | No calls | Reject the evidence with an explicit diagnostic. Include tampered and cross-PR artifact fixtures. |
| Model pool is empty or no model is eligible | No calls | Report incomplete review and list non-secret eligibility reasons. |
| Required numeric cost, token, time, retry, or round limits are unset or invalid | No calls | Report a configuration error; never substitute an unlimited budget. |
| Authorization was already consumed, a run is concurrent, or budget cannot be reserved | No calls for the duplicate or unreserved request | Reuse the recorded outcome or report why execution cannot proceed. Verify duplicate label and workflow-rerun cases. |
| Provider times out, refuses access, or returns an error | Stop unless an explicitly configured bounded retry is permitted | Record the failed attempt and known usage. Do not blindly retry requests with uncertain billing or silently switch models. |
| Model output is malformed or fails schema validation | Do not accept findings; further calls require configured retry and budget allowances | Preserve a redacted diagnostic and mark review incomplete if the allowed attempts are exhausted. |
| Risk calculation fails or required evidence is incomplete | No clearance | Publish an incomplete advisory result, never a zero-risk success-shaped fallback. |
| Comment publication is denied or fails | Do not repeat inference to retry publication | Retain the audit artifact, expose the publication failure in workflow status, and keep existing CI conclusions unchanged. |
| Previously stored model history is malformed or belongs to another SHA | No calls until history is reconciled | Report invalid history rather than silently resetting selection or replay state. |

Tests for these cases belong to their owning workstreams (P1-02 through P1-08). This policy defines expected behavior; it does not claim those tests or runtime safeguards already exist.

## 6. Workstream Plan

### P1-01 — Decisions, Threat Model, and Operating Boundaries

**Status:** In progress

**Deliverables**

- Approved answers to the blocking questions in Section 11.
- Trust-boundary and data-flow description for fork and same-repository PRs.
- Explicit report-only permissions and prohibited operations.
- Failure policy for missing checks, stale runs, provider failures, and malformed output.

**Tasks**

- [x] Inventory existing workflow and agent assets.
- [x] Define Phase 1 scope as all PRs in the fork, with an upstream-compatible contribution target.
- [x] Confirm repository administrators can configure Actions secrets, permissions, environments, and branch protection.
- [x] Confirm support is required for external-fork and same-repository PRs.
- [x] Approve the trusted/untrusted workflow split described in Section 5.3.
- [x] Preserve both Windows and Ubuntu CI matrix legs and wait for both before LLM review.
- [x] Select .NET/C# as the only Phase 1 runtime.
- [x] Define drafts as skipped and paid review as an authorized, current-SHA-bound opt-in.
- [x] Select GitHub-provided model access as the initial provider-adapter target while preserving an extensible interface.
- [x] Define `adversarial-review:run`, authorized login `allancascante`, and one complete configured cycle per current-SHA authorization.
- [x] Require a protected GitHub environment with approval for model access.
- [ ] Confirm the GitHub model identifiers, endpoint, Actions permission, and authentication mechanism available in the fork.
- [ ] Define token, time, retry, concurrency, and cost limits.
- [ ] Define artifact and metrics retention.
- [x] Update the [C4 model](architecture/workspace.dsl) with containers and trust boundaries.
- [x] Document least-privilege data flows, prohibited operations, and trusted configuration provenance in Section 5.6.
- [x] Define the failure policy and required verification cases in Section 5.7.
- [ ] Verify replay-claim storage and spend reservation before enabling provider calls.
- [x] Implement/test transactional SQLite claims, full-cycle reservations, microUSD accounting, replay prevention, ordered settlement, and UTC month leases locally.

**Exit criteria**

No secret-bearing workflow implementation begins until the security model and provider data policy are approved.

### P1-02 — Versioned Data Contracts

**Status:** In progress

**Deliverables**

- Finding schema.
- Deterministic-check schema.
- Review-run metadata schema.
- Audit-report schema.
- Valid, invalid, and forward-compatibility fixtures.

**Proposed minimum finding fields**

- Schema version and stable finding ID.
- Source: build, test, Semgrep, reviewer, or orchestrator.
- Category, severity, confidence, title, and description.
- Evidence with file, line range, check name, or artifact reference.
- Recommendation and disposition state.
- Evaluated head SHA.

**Tasks**

- [x] Choose JSON Schema Draft 2020-12 in [adversarial-review/schemas/](../adversarial-review/schemas/).
- [x] Define version `1.0`, closed enums, and explicit rejection of unknown fields and future versions.
- [ ] Define redaction rules for logs and model output.
- [x] Add .NET schema-validation tests, including duplicate keys, invalid fixtures, unsupported versions, and size limits.
- [x] Document schema versioning and migration policy in the [tooling README](../adversarial-review/README.md).
- [x] Implement disabled JSON configuration, pure authorization/check preflight, and ordered model rotation with offline tests.
- [ ] Verify all eventual producers and consumers use schema and cross-record validation; no live producer is implemented yet.

**Exit criteria**

All producers and consumers validate contracts, and intentionally malformed fixtures fail tests.

### P1-03 — Deterministic Checks and Evidence Artifacts

**Status:** In progress (build/test artifacts and offline validation wired; analysis policy and live matrix verification pending)

**Deliverables**

- Explicit least-privilege permissions for existing and new workflows.
- Build and test evidence tied to the PR head SHA.
- Semgrep configuration with pinned tooling and suppression policy.
- Artifact manifest with checksums, tool versions, commands, timestamps, and conclusions.

**Tasks**

- [x] Preserve the current Windows and Ubuntu matrix and require both results before reporting.
- [x] Preserve existing build and full-test commands while adding raw logs, TRX results, and SHA-bound manifests.
- [ ] Define focused-test selection; full tests remain mandatory for grammar changes.
- [ ] Select and pin Semgrep rules and version.
- [ ] Integrate the team-supplied Semgrep rules and Roslyn analyzer policy when available.
- [ ] Add secret scanning or document which existing GitHub control supplies it.
- [ ] Set artifact names, size limits, retention, and failure behavior.
- [x] Ensure artifact upload uses `if: always()` where evidence must survive failures.
- [x] Add explicit read-only workflow permissions and disable checkout credential persistence.
- [x] Add offline Windows/Ubuntu foundation validation without model credentials.
- [ ] Validate the changed workflows on actual Windows/Ubuntu Actions runs; local macOS tests are not a proxy.
- [ ] Skip all LLM calls when either authoritative matrix leg fails.

**Exit criteria**

A safe and a deliberately failing pilot PR both produce complete evidence without changing current required-check semantics.

### P1-04 — Adversarial Reviewer

**Status:** In progress (prompt, bounded engine, Copilot adapter, and metadata discovery implemented; live inference/pilot pending)

**Deliverables**

- Canonical adversarial-review prompt.
- Context builder with file, diff, token, and size limits.
- Provider adapters and a model-pool selector with timeout and usage reporting.
- Schema validation and bounded malformed-output retries.

**Tasks**

- [x] Draft review categories and a provisional severity rubric in the [reviewer prompt](../.github/prompts/adversarial-review.prompt.md); pilot calibration remains pending.
- [ ] Select which repository instructions are always included and which are selected by changed paths.
- [x] Separate trusted guidance from JSON-delimited untrusted context, with explicit UTF-8 byte bounds and credential rejection.
- [x] Require evidence and reject invented file/check/artifact references, out-of-range lines, changed SHAs, and maintainer disposition changes.
- [ ] Evaluate prompt-injection resilience with real models; offline separation is not proof of resilience.
- [ ] Ensure the reviewer has no tool or permission capable of mutating code or PR state beyond its report.
- [x] Implement and test SDK Empty-mode isolation, an empty tool allowlist, permission rejection, and disabled repository discovery; deployed behavior still requires pilot verification.
- [x] Add repository configuration for provider/model eligibility and ordered selection.
- [x] Add configurable review-round count and per-authorization/monthly spend-limit fields.
- [x] Keep paid execution disabled until required spend-limit values and enforcement behavior are approved.
- [ ] Persist model history per PR head SHA in the audit comment and JSON artifacts.
- [x] Implement ordered cyclic selection that restarts at the first entry after exhausting the list.
- [x] Test single-model, multi-model, unavailable-provider, exhausted-pool, and no-blind-retry scenarios with injected providers.
- [ ] Record model identifier, prompt version, token usage, latency, and provider request ID when available.
- [x] Reject oversized contexts explicitly rather than silently truncating; model-specific token budgeting remains pending.

**Exit criteria**

Recorded fixtures produce schema-valid findings, invalid output is bounded and escalated, and no reviewer path can write repository contents.

### P1-05 — Provisional Risk and Audit Engine

**Status:** In progress (offline completeness/blocker engine; numeric weights await approval)

**Deliverables**

- Deterministic scoring module with a versioned provisional configuration.
- Blocking-condition evaluation.
- Stable Markdown renderer and audit JSON renderer.

**Tasks**

- [ ] Define Phase 1 provisional weights for reporting only.
- [x] Encode mandatory blockers separately from numeric score; unknown numeric risk remains `null`, never zero.
- [x] Include completeness and per-finding confidence indicators.
- [x] Produce deterministic output ordering for reviewability and tests.
- [x] Add offline unit cases for clean, high-severity, missing-signal, malformed, cross-SHA, and stale-SHA inputs.
- [ ] Obtain approval for numeric scoring weights before enabling numeric reporting.

**Exit criteria**

The same normalized inputs always produce the same report and score. The output is explicitly labeled non-gating and uncalibrated.

### P1-06 — Non-Blocking PR Audit Comment

**Status:** In progress (HTTP client and synthetic tests implemented; trusted host and live pilot pending)

**Deliverables**

- Trusted comment publisher with minimal `pull-requests: write` permission.
- Marker-delimited, idempotent comment template.
- Links to workflow run and retained artifacts.

**Tasks**

- [x] Define bot-only comment marker and idempotent lookup/update behavior with run/attempt metadata.
- [x] Render head SHA, check summary, findings, uncalibrated-score limitation, model rounds, and next actions.
- [ ] Prevent stale runs from overwriting a newer report.
- [x] Sanitize untrusted Markdown/HTML, mentions, and supplied secrets in rendered comments.
- [x] Make client permission/HTTP/pagination errors explicit without model retries.
- [ ] Wire host artifact retention and workflow failure reporting for publication errors.
- [ ] Enforce a trusted per-PR publication lock and test actual fork/same-repository Actions runs.
- [ ] Record the authorized actor and head SHA without exposing credentials.

**Exit criteria**

Repeated runs update one comment, stale runs cannot overwrite current results, and comment failure is visible in artifacts and workflow status.

### P1-07 — Baseline Metrics and Observability

**Status:** In progress (collector tested and initial fork baseline collected; telemetry/policy pending)

**Deliverables**

- Metric dictionary and baseline period.
- Privacy-conscious run telemetry.
- Baseline extraction/report procedure.
- Finding disposition mechanism for later false-positive measurement.

**Tasks**

- [x] Define the initial lead-time baseline as the previous six months, starting when a PR becomes ready for review and ending when it is merged.
- [x] Include human-authored open PRs as a separate open-age/censored cohort so they are visible without inventing a merge date.
- [x] Exclude bot-authored PRs, closed-unmerged PRs, and PRs that are drafts at the observation date.
- [x] Implement an extensible .NET baseline extraction command with configurable months and bounded pagination, separate open/merged cohorts, and explicit completeness diagnostics.
- [ ] Record workflow latency, model latency, tokens, estimated cost, result, and failure reason.
- [ ] Define how maintainers mark findings valid, false positive, accepted risk, or duplicate.
- [ ] Define where aggregate metrics live and who can access them.
- [x] Collect an initial six-month fork baseline and record aggregate results below; sample size is insufficient for impact claims.

**Initial baseline observation**

Read-only GitHub collection at `2026-10-17T05:07:15.606814Z` (October 16 locally)
covered the preceding six calendar months. Collection reported complete with no
diagnostics: **1 merged human PR**, merged-only median **17.7464 hours**, and
**1 open human PR** with censored age **148.2088 hours**. These cohorts must not
be combined. The fork sample is too small to estimate improvement or calibrate
thresholds; the same command can target upstream or a longer window after scope
approval. Raw JSON is kept outside the repository pending retention/access policy.

**Exit criteria**

The measurement method is reproducible and does not store secrets, full prompts, or unnecessary source content.

### P1-08 — Verification, Pilot, and Operations

**Status:** In progress (offline tests and runbook available; live pilot not started)

**Deliverables**

- Automated tests for schemas, scoring, rendering, redaction, and stale-run protection.
- Pilot scenarios for safe, unsafe, ambiguous, provider-down, and malformed-output cases.
- Operator runbook and rollback/disable procedure.
- Phase 1 completion report.

**Tasks**

- [ ] Select pilot PRs or sanitized fixtures.
- [ ] Test same-repository and fork PR behavior separately.
- [ ] Verify no secrets are exposed to untrusted jobs.
- [ ] Verify all third-party actions are pinned to immutable SHAs.
- [ ] Test cancellation and concurrency when a PR receives a newer commit.
- [ ] Validate artifact and comment redaction.
- [ ] Document known limitations and deferred Phase 2 work.
- [x] Document current operating mode, activation prerequisites, local failures/reruns, and disable/incident procedures in the [tooling runbook](../adversarial-review/README.md).
- [x] Run offline tool tests, the existing parser suite, workflow lint, and semantic C4 validation; record exact outcomes in Section 9.

**Exit criteria**

All Definition of Done items have linked evidence and the team approves a report-only pilot release.

## 7. Planned Deliverables and Repository Layout

The implemented foundation uses .NET/C# and the following layout. The Copilot SDK
adapter is implemented, with local metadata-only discovery verified. Its paid
activation and the secret-bearing reporting workflow remain deferred until P1-01
inputs are approved; this is not a complete Phase 1 deployment.

```text
.github/
  prompts/
    adversarial-review.prompt.md
  workflows/
    pr-validation.yml
    adversarial-review-validation.yml
adversarial-review/
  Directory.Build.props
  README.md
  config/
    review.json
  schemas/
    contracts.schema.json
    configuration.schema.json
    audit-report.schema.json
    check-result.schema.json
    evidence-manifest.schema.json
    finding.schema.json
    review-run.schema.json
    reviewer-output.schema.json
  src/
    AdversarialReview.Core/
    AdversarialReview.Cli/
  tests/
    AdversarialReview.Tests/
    fixtures/
docs/
  adversarial-reviewer-agent-spec.md
  phase-1-progress.md
  architecture/
    workspace.dsl
```

The [tooling README](../adversarial-review/README.md) contains runnable commands and
documents compatibility and operating limits. No solution file is required; the
new projects deliberately remain separate from the parser build/package.

### Remaining Activation Blockers and Resume Order

Phase 1 is **not complete**. Local code and synthetic tests do not satisfy its
pilot/acceptance criteria. Remaining work must proceed in this order:

| Order | Prerequisite / work | Required evidence | Responsible party |
|---|---|---|---|
| 1 | Confirm GitHub model entitlement, identifiers, endpoint, auth, permissions, and terms; approve numeric limits and retention | Recorded decisions, protected-environment configuration, no secrets in git | Repository owner / team |
| 2 | Supply Semgrep/Roslyn policies and approve context/scoring choices | Pinned rule/analyzer inputs and approved reporting policy | Team |
| 3 | Provision a shared persistent executor/store for the tested ledger, implement trusted event/artifact join, wire the tested Copilot adapter, and persist auditable history | Local fake-provider/SQLite/SDK tests pass; deployment must prove shared reservations, authenticated admission, and bounded live requests | Implementation |
| 4 | Wire trusted publication and failure artifacts with per-PR serialization | Same-repository and external-fork Actions runs; no untrusted code execution or secret exposure | Implementation / repository owner |
| 5 | Verify the actual Windows/Ubuntu matrix, calibrate findings, collect telemetry/dispositions, and complete the pilot | Pilot PR/run links, approved baseline/data policy, team sign-off on every Definition of Done item | Entire team |

No remote push, PR publication, repository environment change, or inference request
has been performed. Local GitHub access read baseline and model-catalog metadata and
downloaded validation tooling/runtime packages. Do not close Phase 1 merely because
local tests pass.

## 8. Dependency Order and Milestones

| Milestone | Depends on | Workstreams | Result |
|---|---|---|---|
| M1: Architecture approved | Stakeholder answers | P1-01 | Security model, scope, provider, and limits are approved. |
| M2: Contracts executable | M1 | P1-02 | Schemas and fixtures are validated in CI. |
| M3: Evidence pipeline | M2 | P1-03 | Deterministic checks publish trusted, SHA-bound evidence. |
| M4: Reviewer prototype | M2, M3 | P1-04, P1-05 | Bounded reviewer creates a provisional audit report. |
| M5: PR reporting | M4 | P1-06 | One non-blocking comment is safely updated. |
| M6: Baseline ready | M1 | P1-07 | Pre-intervention metrics and collection method are documented. |
| M7: Pilot accepted | M3–M6 | P1-08 | Test scenarios pass and operations are documented. |

Work on schemas and baseline definitions can proceed in parallel after M1. Secret-bearing integration waits for M1 approval.

## 9. Progress Log

| Date | Update | Evidence / next step |
|---|---|---|
| October 18, 2026 (UTC) | Implemented the approved .NET Copilot SDK adapter and metadata-only discovery; configured three locally discovered, policy-enabled model candidates without enabling paid execution. | `dotnet test adversarial-review/tests/AdversarialReview.Tests/AdversarialReview.Tests.csproj -c Debug`: 114 passed. `dotnet build adversarial-review/src/AdversarialReview.Cli/AdversarialReview.Cli.csproj -c Debug`: 0 warnings/errors. `list-models` succeeded locally (23 entries); no inference performed. Tests cover tool denial, isolation policy, incomplete billing, aggregation, malformed output, cancellation, overruns, and adapter-to-engine/SQLite replay protection. Approved limits, verified billing conversion/provider caps, trusted deployment, and pilot remain required. |
| October 16, 2026 | Implemented transactional SQLite authorization/spend storage and integrated per-round lease validation with the bounded engine. | Combined suite: 103 passed; CLI build: 0 warnings/errors. Includes concurrent admission, replay, unknown billing, month rollover, and fake-provider cycle with persistent replay rejection across ledger instances. Shared deployment and provider activation remain blocked. |
| October 16, 2026 | Continued implementation with a bounded review-cycle engine and fake-provider integration cases: claim-before-call, fresh PR state/lease per round, ordered model rotation, usage validation, cancellation settlement, and no blind retries. | Provider and ledger are injected; no paid requests are enabled. Also verified that the requested GitHub Models API has been retired; see D-022. |
| October 16, 2026 | Documented operator procedures and explicit activation/resume dependencies; retained all unmet pilot/approval criteria as open. | [Tooling runbook](../adversarial-review/README.md). Final offline suite: 73 passed; parser suite: 1,176 passed. No live model workflow is enabled. |
| October 16, 2026 | Implemented an idempotent GitHub audit-comment client with bot-only targets, run ordering, pre/post SHA checks, and explicit duplicate/permission failures. | Combined suite: 73 passed (9 publisher cases). Synthetic HTTP tests only; no remote comment was created. Trusted workflow concurrency, artifact failure handling, and live pilot remain pending. |
| October 16, 2026 | Added bounded context building and strict reviewer-response validation without provider access. | Combined suite: 64 passed; CLI build: 0 warnings/errors. Tests cover exact UTF-8 budget boundaries, JSON-escaped secrets, wrong SHA/source/disposition, invented evidence, and malformed responses. No live adversarial loop is claimed. |
| October 16, 2026 | Added bounded/checksummed evidence manifests and updated untrusted CI to publish build logs/TRX while preserving both OS matrices. Added standalone offline foundation validation. | Combined .NET suite: 59 passed. CLI build: 0 warnings/errors; file-logger capture verified. Modified workflows pass pinned actionlint 1.7.7. Actual Actions matrix and team-supplied analysis policies remain pending. |
| October 16, 2026 | Verified existing parser behavior after adding the independent review tooling. | `dotnet test Test/SqlDom/UTSqlScriptDom.csproj -c Debug -p:TargetFramework=net8.0 --logger 'console;verbosity=normal'`: 1,176 passed. Existing serialization/discovery warnings were not changed. Synthetic manifest CLI and schema validation also passed. |
| October 16, 2026 | Ran the baseline CLI against the fork using existing read-only GitHub access; no model API or remote mutation was used. | Collection complete: 1 merged and 1 open human PR, no diagnostics. Aggregate results in P1-07; raw dataset remains outside git. CLI build: 0 warnings/errors. |
| October 16, 2026 | Added .NET baseline collector and CLI: current-state snapshot, timeline-derived readiness, six-month configurable window, merged-only median, and separate open censored cohort. | 36 metrics tests pass; combined suite 56 passed. [Command and metric definitions](../adversarial-review/README.md). Raw-data retention and access approval remain pending. |
| October 16, 2026 | Added offline advisory audit generation, cross-record semantic validation, bounded/redacted Markdown rendering, and an initial report-only reviewer prompt. | Foundation and audit test selector: 20 passed. Numeric scoring deliberately remains unavailable pending calibration decisions. No PR publication or provider requests implemented. |
| October 16, 2026 | Implemented P1-02 offline foundation: .NET library, CLI, strict local schemas, disabled configuration, authorization/check preflight, and cyclic model selection. | `dotnet test adversarial-review/tests/AdversarialReview.Tests/AdversarialReview.Tests.csproj -c Debug --filter FullyQualifiedName~FoundationTests`: 13 passed. CLI `validate-config` reports six execution blockers; valid finding fixture accepted. [Tooling and commands](../adversarial-review/README.md). Paid access, anti-replay storage, and spend enforcement remain unimplemented. |
| October 16, 2026 | Created local branch `feature/adversarial-reviewer-phase-1` and committed the planning baseline as `2afdd6a`. | README, source specification, and this tracker committed; no remote push performed. |
| October 16, 2026 | Started P1-01: translated and expanded the C4 model to context/container views; documented trust boundaries, authorization/CI ordering, and explicit failure cases. | [Architecture model](architecture/workspace.dsl), Sections 5.6-5.7. Runtime enforcement and scenario tests remain pending. |
| October 16, 2026 | P1-01 documentation validation initially lacked a Structurizr runtime. Resolved by using the standalone official CLI v2025.11.09 with Java, without Docker. | `StructurizrCliApplication validate -workspace docs/architecture/workspace.dsl`: passed. Architecture acceptance still depends on provider/limits/retention decisions. |
| October 16, 2026 | Added the project specification and made it the documentation source of truth. | `docs/adversarial-reviewer-agent-spec.md` |
| October 16, 2026 | Inventoried existing CI, prompts, skills, runtime files, and architecture. | Findings recorded in Section 4. |
| October 16, 2026 | Drafted the Phase 1 architecture, workstreams, milestones, Definition of Done, and stakeholder questionnaire. | Await answers to the stakeholder questionnaire, starting with the blocking questions. |
| October 16, 2026 | Recorded initial stakeholder decisions for scope, GitHub administration, PR origins, runtime preference, baseline measurement, schedule, ownership, and multi-model review. | Resolve the workflow trust model, initial provider pool, data policy, and runtime boundary. |
| October 16, 2026 | Approved split workflows, both CI matrix legs, .NET-only implementation, ordered cyclic model selection, owner-authorized paid review, draft exclusion, and open-PR baseline reporting. | Choose the opt-in label/login, initial provider credentials, and spend limits. |
| October 16, 2026 | Selected the `adversarial-review:run` gate, authorized `allancascante`, full configured cycles per authorization, GitHub-first model adapter, protected environment approval, and baseline exclusions. | Confirm GitHub model access details and define numeric limits; Semgrep/Roslyn policies are pending team input. |

## 10. Recorded Stakeholder Answers

These answers were supplied on October 16, 2026 and are reflected in the decision log:

- Phase 1 is developed and piloted in the fork with the intent to propose the complete change set upstream.
- Every pull request is eligible, not only parser or grammar changes.
- Repository administrators can configure secrets, permissions, environments, and branch protection.
- Both external-fork and same-repository pull requests must be supported.
- The reviewer must support a configurable list of models and prefer a different model on each review cycle when possible.
- Source may be sent to external providers because the repository is open source, and providers may retain requests; provider-specific disclosures still need to be documented.
- .NET/C# is the only Phase 1 implementation runtime, including baseline extraction.
- The existing `windows-latest` and `ubuntu-latest` build/test matrix remains authoritative; paid review waits for both legs to pass.
- Draft pull requests are skipped.
- Paid review requires a one-shot, authorized label event bound to the current head SHA.
- The initial label is `adversarial-review:run`; `allancascante` is the initial authorized actor.
- One label authorization runs the whole configured cycle; review rounds are repository-configurable.
- Cost limits are repository-configurable, but numeric values are not yet approved.
- GitHub-provided model access is the initial integration target; the provider interface remains extensible.
- Model access is protected by a GitHub environment requiring approval.
- Model configuration uses JSON, allows multiple models per provider, cycles in configured order, restarts at the first entry, and continues when only one model is available.
- Model history is stored in the PR audit comment and JSON artifacts.
- The initial historical baseline covers the previous six months. Lead time starts when a PR becomes ready for review and ends when it is merged.
- Open human-authored PRs are included as a separate open-age/censored cohort.
- Bot-authored, closed-unmerged, and currently draft PRs are excluded from the baseline.
- Baseline extraction must be an extensible .NET command so the date range and filters can be changed if the sample is too small.
- Semgrep rules and Roslyn analyzer configuration will be supplied separately by the team.
- The report-only pilot target follows the existing schedule: CI/CD automation by November 5, 2026, with tuning through November 19, 2026.
- Phase 1 acceptance is owned by the entire team.

## 11. Stakeholder Questionnaire

Record decisions in Section 12. Questions marked **BLOCKING** must be answered before implementing workflows that use secrets or write PR comments. Suggested defaults are proposals, not decisions. Questions answered in Section 10 remain listed for traceability.

### 11.1 Product Scope and Success

1. **BLOCKING:** Is Phase 1 limited to the `cenfosoft/SqlScriptDOM` fork, or must the design also be directly reusable by `microsoft/SqlScriptDOM`?
2. Which PRs qualify during the pilot: grammar only, grammar plus tests, all parser code, workflow/agent changes, or every PR?
3. Should draft PRs be skipped, analyzed without comments, or fully reported?
4. Is the Phase 1 comment informational only even when build or tests fail? Suggested default: yes; existing CI remains authoritative.
5. Who is the product owner authorized to accept Phase 1 and resolve requirement conflicts?
6. What date must the report-only pilot be operational?
7. How many pilot PRs are required before Phase 1 can be called complete?
8. What observable result matters most in Phase 1: useful findings, audit completeness, workflow reliability, baseline collection, or latency?

### 11.2 Repository and GitHub Governance

9. **BLOCKING:** Can repository administrators add Actions secrets, change workflow permissions, and configure branch protection in the fork?
10. **BLOCKING:** Will the pilot accept PRs from external forks, branches in the same repository, or both?
11. **BLOCKING:** Do you approve the proposed split between an untrusted `pull_request` validation workflow and a trusted post-validation reporting workflow? If not, what trust model is required?
12. May the reporting workflow receive `pull-requests: write` solely to maintain one audit comment?
13. Should comments be posted on every run, only successful evidence runs, or only when findings exist?
14. Should the workflow add a neutral label such as `adversarial-review:reported`, or are all labels deferred?
15. Who may manually rerun or disable the workflow?
16. Are GitHub environments with required reviewers available for protecting production LLM credentials?
17. What artifact retention period is allowed? Suggested starting point: 30 days for raw evidence and 90 days for audit summaries.
18. Are private Actions or reusable workflows available in the organization?

### 11.3 LLM Provider and Data Policy

19. **BLOCKING:** Which provider and exact model will Phase 1 use: GitHub Models, Azure OpenAI, OpenAI, another provider, or a local model?
20. **BLOCKING:** Is repository source code permitted to leave GitHub and be sent to that provider?
21. **BLOCKING:** What are the provider's retention, training, regional-processing, and data-residency requirements?
22. What secret name and endpoint configuration should the workflow use?
23. Is a secondary model/provider permitted when the primary provider is unavailable, or must the run stop?
24. Must model output be reproducible through a fixed seed or low temperature where supported?
25. What model metadata may be stored: model name, request ID, token counts, latency, estimated cost, raw response?
26. May full prompts and responses be retained as artifacts? Suggested default: no; retain redacted structured output and prompt version only.
27. Are PR descriptions and comments included as context, or only the diff and repository guidance?
28. Should the reviewer analyze files beyond the diff when needed? If yes, what maximum count and size?

### 11.4 Runtime and Implementation Stack

29. **BLOCKING:** Which orchestration runtime should we use: Python, .NET, Node.js/TypeScript, PowerShell, or another stack?
30. Is adding a package manifest and third-party dependencies acceptable, or should Phase 1 favor standard-library code?
31. Which JSON Schema draft and validator are approved?
32. Must the tools run identically on Windows and Linux, or can orchestration use Ubuntu while parser validation keeps the existing matrix?
33. Should the adversarial-review code live in this repository or a separate reusable action/service?
34. Do you expect local developer commands for running schema validation, scoring, and report rendering?
35. What minimum unit-test and coverage expectations apply to the new orchestration code?

### 11.5 Deterministic Checks

36. **BLOCKING:** Must the existing Windows and Ubuntu build/test matrix remain unchanged during Phase 1?
37. Should the reviewer wait for every matrix job or consume one canonical result plus compatibility status?
38. Which Semgrep ruleset should be used: `p/csharp`, `p/security-audit`, organization rules, or a curated repository ruleset?
39. Who may approve Semgrep suppressions, and where must suppressions be documented?
40. Is Semgrep allowed to download rules at runtime, or must rules be vendored and versioned?
41. Is code coverage required in Phase 1, or may collection start after a stable baseline command is selected?
42. Is secret scanning supplied by GitHub Advanced Security, an additional tool, or both?
43. What maximum build/test duration is acceptable before the review reports a timeout?
44. Should a failed build prevent the LLM call to save cost, or should the reviewer still analyze the diff? Suggested default: skip the LLM and report deterministic blockers.
45. What maximum artifact size is acceptable per run?

### 11.6 Reviewer Behavior and Output

46. Which finding severities are required? Suggested default: `critical`, `high`, `medium`, `low`, and `informational`.
47. Which categories are required beyond correctness, versioning, tests, architecture, performance, security, and maintainability?
48. Must every finding include an exact file and line, or can workflow-level findings cite a check or artifact instead?
49. Should duplicate findings from Semgrep and the LLM be merged automatically? If so, which source is authoritative?
50. How should the reviewer report uncertainty: confidence score, explicit `needs-human-review` flag, or both?
51. What should happen when the diff is too large: truncate by priority, split into multiple calls, or escalate without review?
52. How many malformed-output retries are allowed? Suggested default: one repair retry.
53. Should an empty finding list be reported as “no findings detected” rather than “safe” to avoid overclaiming?
54. Which repository instruction files must always be included in context?
55. Should the prompt and schema be versioned independently?

### 11.7 Provisional Risk Reporting

56. Do you want a numeric score in Phase 1 before calibration, or only severity counts and an “uncalibrated” status?
57. If numeric, may we use clearly provisional weights that cannot gate merging?
58. Which deterministic failures are blockers in the report: build, tests, critical Semgrep, missing evidence, stale SHA, schema failure, provider failure?
59. Should missing optional signals increase risk or only reduce report confidence?
60. Do you prefer lower-is-safer (`0–100`, as specified) displayed alongside a textual state?
61. Who approves changes to provisional scoring configuration?

### 11.8 Audit Comment and Artifacts

62. What language should PR comments use: English only, Spanish only, or bilingual?
63. What maximum comment length is acceptable before details move to artifacts?
64. Should low-severity findings be collapsed under a details block?
65. Must the comment link to exact source lines, workflow logs, artifacts, or all three?
66. Who should be mentioned when the report is incomplete or requires human review?
67. Should contributors be allowed to request a rerun using a comment command? If yes, who may invoke it?
68. What information is prohibited from comments and artifacts beyond credentials and full prompts?

### 11.9 Metrics and Baseline

69. **BLOCKING:** What date range defines the pre-intervention lead-time baseline?
70. **BLOCKING:** What event starts lead time: PR opening, ready-for-review, first review request, or first non-draft commit?
71. **BLOCKING:** What event ends lead time: merge, closure, approval, or merge-ready state?
72. Should abandoned, draft-only, bot, dependency, and documentation PRs be excluded?
73. How will maintainers classify findings as valid, false positive, accepted risk, or duplicate?
74. Where may aggregate metrics be stored: repository artifacts, a committed anonymized dataset, GitHub Insights export, or external storage?
75. Who can access raw metric data?
76. What cost currency and pricing source should be used?
77. What Phase 1 baseline report format is expected: Markdown, CSV/JSON, dashboard, or all of these?
78. Are there target values already agreed for latency, cost, false-positive rate, or lead-time improvement?

### 11.10 Pilot, Operations, and Ownership

79. Who owns each role: software architect, prompt engineer, DevOps engineer, QA lead, and product approver?
80. Which existing PRs or synthetic cases should form the pilot set?
81. May pilot fixtures include intentionally vulnerable or broken code committed under a test-data directory?
82. What is the incident response if a prompt, response, or secret is exposed?
83. What is the rollback mechanism: disable workflow file, repository variable kill switch, environment protection, or all three?
84. Who receives notifications when the provider is down or the report workflow repeatedly fails?
85. What support window or service-level objective applies during the pilot?
86. What evidence must be presented at the Phase 1 review meeting for formal acceptance?

## 12. Decision Log

Add approved answers here; do not rely only on chat history.

| ID | Date | Decision | Owner | Consequences / follow-up |
|---|---|---|---|---|
| D-001 | October 16, 2026 | Phase 1 starts in report-only mode and cannot merge or modify contributor code. | Project specification | Enforce through workflow permissions and tests. |
| D-002 | October 16, 2026 | Use an ordered JSON pool with multiple models per provider; choose the next unused entry, restart at the first after exhaustion, and continue when only one model is eligible. | Entire team | Persist history in the audit comment and JSON artifacts; initial providers remain pending. |
| D-003 | October 16, 2026 | Support external-fork and same-repository PRs using separate untrusted validation and trusted reporting workflows. | Entire team | Never expose credentials to code execution from contributor branches. |
| D-004 | October 16, 2026 | Use .NET/C# for all Phase 1 implementation, including baseline extraction. | Entire team | Do not add Python or PowerShell orchestration dependencies. |
| D-005 | October 16, 2026 | Evaluate all PRs in the fork and design the complete change set for a future upstream proposal. | Entire team | Avoid fork-specific assumptions and path filters that exclude non-parser changes. |
| D-006 | Pending | Phase 1 budgets and limits. | TBD | Blocks timeouts, retries, token limits, and cost controls. |
| D-007 | October 16, 2026 | Baseline the previous six months from ready-for-review to merge using an extensible .NET command; report open human PRs as a separate open-age/censored cohort. | Entire team | Define bot, never-ready draft, closed-unmerged, output, and insufficient-sample policies. |
| D-008 | October 16, 2026 | Target report-only CI/CD automation by November 5, 2026 and integration tuning by November 19, 2026. | Entire team | Sequence M1–M5 for November 5 and M6–M7 for November 19. |
| D-009 | October 16, 2026 | The entire team owns formal Phase 1 acceptance. | Entire team | Acceptance evidence must be reviewable by all team members. |
| D-010 | October 16, 2026 | Open-source content may be sent to configured external providers and providers may retain requests. | Entire team | Document each selected provider's actual terms before enabling it. |
| D-011 | October 16, 2026 | Deterministic CI is authoritative; LLM review is informational in Phase 1 and runs only after both matrix legs pass. | Entire team | A failed build or test consumes no model tokens. |
| D-012 | October 16, 2026 | Draft PRs are skipped. | Entire team | Trigger review only after ready-for-review and explicit authorization. |
| D-013 | October 16, 2026 | Paid review requires a one-shot allowlisted maintainer action bound to the current PR head SHA. | Entire team | Add anti-replay, current-SHA, and concurrency checks. |
| D-014 | October 16, 2026 | Use `adversarial-review:run` with `allancascante` as the initial authorized actor; one authorization runs the complete configured cycle. | Entire team | Store label, allowlist, and round count in repository configuration; verify the event actor and current SHA. |
| D-015 | October 16, 2026 | Start with model access supplied through GitHub and keep provider adapters extensible. | Entire team | Confirm available GitHub models, authentication, permissions, endpoint, and limits in the fork. |
| D-016 | October 16, 2026 | Protect model access with a GitHub environment requiring approval. | Entire team | Configure the environment before enabling paid review. |
| D-017 | October 16, 2026 | Review rounds and cost limits are repository-configurable; numeric cost limits are pending. | Entire team | Define schemas now; paid execution stays disabled until safe values and enforcement behavior are approved. |
| D-018 | October 16, 2026 | Exclude bot-authored, closed-unmerged, and currently draft PRs from baseline metrics. | Entire team | Open non-draft human PRs remain in the separate open-age cohort. |
| D-019 | October 16, 2026 | Semgrep rules and Roslyn analyzer policy will be supplied manually by the team. | Entire team | Design integration points now; do not invent the final rule policy. |
| D-020 | October 16, 2026 | Implement offline contracts using Draft 2020-12, explicit version `1.0`, closed schemas, and local URN resolution. | Implementation choice | Unknown/additive fields and future versions are rejected until explicitly supported; no network schema retrieval. |
| D-021 | October 16, 2026 | Leave numeric risk unavailable rather than invent unapproved weights; keep byte limits distinct from provider token accounting. | Implementation safety choice | Team approval and model-specific accounting remain required before calibrated or paid operation. |
| D-022 | October 16, 2026 | Availability check found the GitHub Models API retired; this does not apply to GitHub Copilot SDK. | Verified external constraint | [Official documentation](https://docs.github.com/en/github-models) reports Models retirement July 30, 2026. Copilot is a separate integration, explicitly approved in D-023. |
| D-023 | October 18, 2026 (UTC) | Owner approved the recommended .NET Copilot SDK integration using configurable model IDs discovered from the account. | allancascante | Local metadata discovery verified; candidate rotation starts with GPT-5.4 mini, Claude Haiku 4.5, and MAI-Code-1.1-Flash. No default billing conversion or paid execution is enabled; deployment entitlement, limits, caps, and provider terms remain unverified. |

## 13. Risks and Issues

| ID | Risk | Impact | Mitigation | Status |
|---|---|---|---|---|
| R-001 | Secret-bearing workflow processes untrusted fork code | Credential theft or repository compromise | Separate untrusted execution from trusted reporting; never execute PR code in trusted workflow | Open |
| R-002 | Prompt injection in source or PR text | Manipulated reviewer output or data disclosure | Delimit untrusted content, restrict tools, validate output, minimize context | Open |
| R-003 | LLM findings are treated as authoritative | False confidence or contributor friction | Label output advisory, retain deterministic evidence, require human calibration | Open |
| R-004 | Existing CI behavior regresses | Reduced parser validation confidence | Preserve current jobs first; test workflow changes on pilot branches | Open |
| R-005 | Artifacts expose source, prompts, or secrets | Privacy/security incident | Redaction, minimum retention, checksums, approved data policy | Open |
| R-006 | Duplicate or stale PR comments | Confusing audit trail | Marker-based update and strict head-SHA comparison | Open |
| R-007 | Metrics are not comparable | Invalid lead-time conclusions | Approve metric definitions and baseline period before intervention | Open |
| R-008 | Provider cost or latency is unbounded | Budget overrun and slow CI | Skip unnecessary calls; enforce token, retry, timeout, and cost limits | Open |
| R-009 | An untrusted contributor repeatedly triggers paid review | Token exhaustion or financial loss | One-shot allowlisted authorization bound to the current SHA, deduplication, concurrency control, and hard spend limits | Mitigation designed; implementation pending |

## 14. Change-Control Rules for This Tracker

- Update the status and checkboxes in this document in the same PR as Phase 1 work.
- Add a progress-log entry for every merged Phase 1 deliverable.
- Record stakeholder decisions in the decision log with an owner and date.
- Link implementation evidence rather than marking work complete from memory.
- Add newly discovered work to the relevant workstream; do not silently expand scope.
- Move production gating, autonomous correction, and merge automation to later phases unless the specification is formally amended.
