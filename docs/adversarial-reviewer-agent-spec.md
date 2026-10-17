# Adversarial Reviewer Agent Specification

| Field | Value |
|---|---|
| Status | Draft |
| Version | 0.1 |
| Project kickoff | September 24, 2026 |
| Target repository | [cenfosoft/SqlScriptDOM](https://github.com/cenfosoft/SqlScriptDOM) |
| Upstream repository | [microsoft/SqlScriptDOM](https://github.com/microsoft/SqlScriptDOM) |

## 1. Purpose

`SqlScriptDom` is an open-source library that parses T-SQL for multiple SQL Server versions. Contributors frequently extend its grammars without being experts in the parser architecture. AI-assisted authoring can accelerate those changes, but a slow manual review process can keep pull request lead time measured in months and negate those gains.

The Adversarial Reviewer Agent aims to reduce pull request lead time by automating code review and risk validation. It must challenge proposed changes, combine AI review with deterministic engineering signals, produce an auditable risk decision, and escalate uncertain or unsafe changes to human maintainers.

This project adds review automation around the development process. It does not change the behavior of the `SqlScriptDom` parser as part of the initial implementation.

## 2. Goals and Non-Goals

### 2.1 Goals

- Review all pull requests in the pilot repository, with specialized rules for grammar and parser changes.
- Identify parser regressions, missing version gates, incomplete tests, architecture violations, and performance risks.
- Combine LLM findings with tests, linters, coverage, and static analysis results.
- Bound autonomous review and correction cycles to prevent infinite loops.
- Publish a clear, reproducible audit trail on every evaluated pull request.
- Mark sufficiently low-risk pull requests as ready for integration.
- Escalate pull requests that remain unsafe, uncertain, or inconclusive.
- Measure the workflow's effect on pull request lead time and review quality.

### 2.2 Non-Goals

- Replacing maintainers for escalated or policy-sensitive decisions.
- Training or fine-tuning a language model.
- Supporting CI platforms other than GitHub Actions in the initial release.
- Sending automated changes, issues, or pull requests to the upstream Microsoft repository.
- Automatically merging pull requests before repository owners explicitly approve that capability.

## 3. Terminology

- **Authoring agent:** Produces or corrects a proposed code change under repository-specific rules.
- **Adversarial reviewer:** Attempts to disprove the correctness and safety of a proposed change.
- **Deterministic signal:** A result from a repeatable tool, such as a test, linter, coverage tool, or static analyzer.
- **Finding:** A discrete issue reported by a tool or agent, with evidence and severity.
- **Review cycle:** One adversarial review, risk evaluation, and optional correction attempt.
- **Risk score:** A normalized metric used to support the workflow decision.
- **Audit trail:** The evidence, findings, score calculation, and final decision published for a pull request.
- **Escalation:** Routing a pull request to human maintainers because automation cannot safely clear it.

The words **MUST**, **MUST NOT**, **SHOULD**, and **MAY** describe requirement strength.

## 4. Solution Architecture

The solution has four cooperating layers.

### 4.1 Code Generation Layer

The authoring agent MUST receive repository-specific instructions covering:

- Grammar ownership and SQL Server version boundaries.
- AST and script generator conventions.
- Generated-file restrictions.
- Required positive, negative, baseline, and regression tests.
- The requirement to run focused tests followed by the complete test suite for grammar changes.

The existing repository instructions under `.github/` and reusable skills under `.agents/skills/` are the initial source for these rules.

### 4.2 Adversarial Reviewer Agent

The reviewer MUST inspect the pull request diff and challenge it for:

- Unintended syntax acceptance or rejection.
- Changes to shared grammar rules with unrelated side effects.
- Incorrect SQL Server version support.
- AST values that are parsed but not generated, or generated but not parsed.
- Missing round-trip fidelity.
- Missing positive or negative tests.
- Incorrect baseline expectations.
- Hand-edited generated files.
- Parser ambiguity, excessive lookahead, or likely performance regressions.
- Security, secret-handling, and workflow-permission issues.

Each finding MUST include a stable identifier, severity, category, evidence, affected file or check, and recommended action. The reviewer MUST distinguish observed failures from hypotheses that require human confirmation.

The reviewer MUST support an ordered, repository-configured JSON pool of providers and models, including multiple models for one provider. For each pull request head SHA, the orchestrator MUST record the models used by prior review cycles and SHOULD select the next unused eligible entry until the configured pool is exhausted. It then restarts at the first eligible entry. If only one entry is eligible, review continues with that entry. This rotation introduces independent attempts to disprove the change rather than repeatedly asking one model to confirm its own conclusions.

Model-pool configuration MUST contain identifiers and selection policy only; credentials MUST remain in GitHub secrets or an approved secret manager. The audit trail MUST record the selected provider, model, cycle, selection reason, usage, latency, and outcome. The orchestrator MUST define fallback behavior for unavailable providers, missing credentials, a single eligible model, and an exhausted pool.

### 4.3 Risk Scoring Engine

The engine MUST combine deterministic signals and adversarial findings into a normalized score. It MUST NOT allow an LLM to silently override failed mandatory checks.

The initial risk model will use the following inputs:

| Input | Source | Required behavior |
|---|---|---|
| Build | .NET build | A failure is a blocking condition. |
| Tests | Focused and full test suites | A required test failure is a blocking condition. |
| Static analysis | Semgrep | Findings contribute by severity; critical findings block clearance. |
| Lint/format checks | Repository tooling | Failures contribute to risk according to severity. |
| Coverage | Coverage report | Regressions contribute to risk after a baseline is established. |
| Adversarial findings | Reviewer output | Findings contribute using severity and confidence. |
| Change scope | Pull request diff | High-impact grammar or workflow changes increase review scrutiny. |

The exact weights and acceptance threshold are configuration values and remain to be calibrated. Until calibration is approved, the workflow MUST operate in report-only mode and MUST NOT authorize automatic merging.

### 4.4 Orchestrator

GitHub Actions coordinates tools and agents through separate untrusted validation and trusted reporting workflows. The untrusted workflow may execute contributor code but MUST NOT receive protected credentials. The trusted workflow may access provider credentials and update the audit comment but MUST NOT execute code from the contributor branch.

The orchestration implementation MUST use .NET/C#. The orchestrator MUST:

1. Collect pull request metadata and the bounded diff.
2. Run deterministic checks before invoking the LLM review.
3. Normalize tool output into a common finding format.
4. Run the adversarial review and risk calculation.
5. Decide whether to clear, repeat, or escalate the review.
6. Publish or update one audit comment on the pull request.
7. Upload machine-readable reports as workflow artifacts.
8. Select an eligible provider/model for each cycle and preserve model history for the evaluated pull request head SHA.

## 5. Pull Request Workflow

### 5.1 Trigger

Deterministic validation MUST run for every non-draft pull request, including external-fork and same-repository pull requests. Draft pull requests MUST be skipped until they become ready for review.

Paid adversarial review MUST NOT run solely because a pull request was opened or updated. It requires an explicit, one-shot authorization by a configured maintainer for the current head SHA. The initial mechanism is an opt-in label event whose actor is checked against an authorization allowlist. Label presence alone is insufficient. A new commit invalidates the authorization and requires a new authorized action.

The initial opt-in label is `adversarial-review:run`, and the initial authorized actor is `allancascante`. One authorization starts the complete configured review cycle, whose maximum round count is defined in repository configuration. It does not authorize unbounded provider calls.

The workflow MUST prevent duplicate or concurrent paid reviews for the same pull request head SHA and review cycle. The audit trail MUST identify the authorizing actor, authorization time, opt-in mechanism, and authorized SHA.

### 5.2 Automated Checks

The existing Windows and Ubuntu build/test matrix remains authoritative. Both matrix legs MUST pass before a provider is called. A failed, canceled, incomplete, or stale deterministic run MUST consume no model tokens.

The first implementation SHOULD execute, at minimum:

1. Repository build using the SDK pinned in `global.json`.
2. Relevant focused tests based on changed paths.
3. The complete `SqlScriptDom` unit test suite for grammar changes.
4. Semgrep with a pinned ruleset and version.
5. Available lint and formatting checks.
6. Coverage collection once a stable baseline and comparison policy exist.

Every tool version and command MUST be recorded in the audit data.

### 5.3 Adversarial Evaluation

The reviewer receives only the context required for the task: pull request metadata, bounded diff, relevant repository instructions, changed tests, and normalized tool findings. Prompt inputs MUST exclude secrets and untrusted content that is not required for review.

The reviewer output MUST conform to a machine-validated schema. Invalid or incomplete output MUST be retried only within the configured retry limit, then escalated.

### 5.4 Loop Control

The orchestrator MUST enforce all of the following limits:

- Maximum review cycles.
- Maximum retries for malformed model output.
- Maximum input and output tokens per invocation.
- Maximum wall-clock duration for the workflow.
- Maximum estimated or actual inference cost when the provider exposes usage data.
- Maximum provider attempts and model rotations per review cycle.

A cycle can end in one of three states:

- **Clear:** Mandatory checks pass and risk is at or below the acceptance threshold.
- **Repeat:** Correctable findings remain and the cycle limit has not been reached.
- **Escalate:** A blocking condition exists, limits are exhausted, output is inconclusive, or policy requires a human decision.

The initial maximum cycle count, token budget, timeout, and cost budget are open configuration decisions. Safe defaults MUST be selected before enabling correction cycles.

### 5.5 Conditional Gate

The workflow MUST expose a required status check suitable for branch protection. During report-only mode, that check reports completion without granting automatic merge authority.

After calibration, a pull request can be marked merge-ready only when:

- All mandatory deterministic checks pass.
- No unresolved blocking finding exists.
- The risk score is at or below the approved threshold.
- The audit trail is successfully published.
- The workflow evaluated the current pull request head SHA.

Any stale, canceled, timed-out, or partially completed evaluation MUST fail closed and require rerun or human review.

## 6. Risk Model and Decision Record

### 6.1 Score Contract

The risk score MUST be represented on a scale from `0` to `100`, where `0` is lowest risk and `100` is highest risk. A score alone is insufficient: blocking conditions always take precedence.

A future calibrated model may use a weighted formula such as:

$$
R = \min\left(100, \sum_i w_i s_i c_i + p\right)
$$

where:

- $w_i$ is the approved weight for a finding category.
- $s_i$ is the severity value.
- $c_i$ is the confidence value.
- $p$ is the penalty from failed or missing deterministic signals.

Weights, severity mappings, confidence handling, and thresholds MUST be versioned configuration. A change to the model MUST be visible in the audit trail.

### 6.2 Blocking Conditions

At minimum, these conditions MUST block automated clearance:

- Build failure.
- Required test failure.
- Critical static-analysis finding.
- Detected secret exposure.
- Missing or invalid reviewer output after retries.
- Review cycle, time, token, or cost limit exhaustion.
- Evaluation of a commit other than the current pull request head.
- Failure to publish the audit trail.

### 6.3 Audit Trail

The pull request comment MUST contain:

- Evaluated commit SHA and workflow run link.
- Final state and risk score.
- Risk model version and configured threshold.
- Summary of deterministic checks.
- Findings grouped by severity and category.
- Review cycle count and limit.
- Token, latency, and cost data when available.
- Provider/model history and the selection reason for each completed cycle.
- Reasons for clearance or escalation.
- Clear next actions for the contributor or maintainer.

The workflow SHOULD update a single marker-delimited comment rather than creating a new comment for every run.

A machine-readable JSON report MUST also be retained as a workflow artifact for later analysis.

## 7. Prompt and Guardrail Requirements

### 7.1 Authoring Prompt

The authoring prompt MUST enforce the repository's architecture and testing rules. It MUST prohibit direct edits to generated parser output and MUST require tests for behavior changes.

### 7.2 Adversarial Review Prompt

The review prompt MUST prioritize correctness over agreement with the author. It MUST request concrete evidence, consider cross-version behavior, inspect round-trip fidelity, and avoid inventing failures not supported by the diff or tool output.

### 7.3 Risk Generation Prompt

If an LLM contributes to risk classification, it MUST emit structured findings rather than the final gate decision. Deterministic orchestration code MUST calculate the final score and apply blocking rules.

### 7.4 Security Guardrails

- Workflow permissions MUST follow least privilege.
- Pull requests from forks MUST NOT receive repository secrets through an unsafe event context.
- An arbitrary contributor MUST NOT be able to trigger paid inference; the trusted workflow MUST verify an allowlisted authorizing actor and the authorized head SHA.
- LLM credentials MUST be stored only in GitHub Actions secrets or an approved secret manager.
- Provider access MUST be protected by a GitHub environment requiring approval during Phase 1.
- GitHub identities used for authorization MUST NOT be treated as provider credentials; passwords and interactive account credentials MUST NOT be stored or shared.
- Prompt content and logs MUST be reviewed for source-code and data-retention constraints.
- External actions MUST be pinned to immutable commit SHAs before production use.
- Untrusted pull request text and code MUST be treated as potentially prompt-injecting input.
- Agent-proposed changes MUST pass the same checks as contributor changes.

## 8. False Positive, Latency, and Cost Controls

### 8.1 False Positives

The team MUST create a calibration set containing accepted changes, known regressions, and intentionally ambiguous cases. Prompt and scoring changes SHOULD be evaluated against this set before deployment.

The system MUST record reviewer dispositions so false-positive rates can be measured. Contributors MUST have a documented path to request human review.

### 8.2 Latency and Cost

The workflow SHOULD minimize model input by selecting relevant files and summarizing deterministic reports. It MUST enforce bounded retries and review cycles. Cost and latency SHOULD be measured per pull request and per cycle.

Review-round, per-authorization cost, and periodic cost limits MUST be represented in repository configuration. Paid review MUST remain disabled until required limit values and enforcement behavior are approved. Once enabled, it MUST fail safely when a required limit is missing, invalid, or exceeded.

## 9. Observability and Success Metrics

The project MUST collect enough data to compare the automated workflow with the pre-project baseline.

| Metric | Definition | Initial target |
|---|---|---|
| Pull request lead time | Time from ready-for-review to merge for merged human-authored PRs | Initial baseline: previous six months. |
| Open pull request age | Time from ready-for-review to observation date for open human-authored PRs | Report separately as an open-age/censored cohort. |
| Review cycle count | Number of automated cycles per pull request | Bounded by configured maximum. |
| False-positive rate | Cleared-by-human findings later classified as non-issues | Threshold to be calibrated. |
| Escalation rate | Evaluated pull requests requiring human review | Baseline and target to be established. |
| Defect escape rate | Relevant defects found after automated clearance | Must not exceed the approved baseline. |
| Workflow latency | Time from trigger to final automated decision | Budget to be approved. |
| Inference cost | LLM cost per pull request and cycle | Budget to be approved. |

Metrics MUST avoid exposing secrets or sensitive prompt content.

## 10. Delivery Plan

| Estimated date | Milestone | Technical activity | Responsible party |
|---|---|---|---|
| September 24, 2026 | Kickoff and base analysis | Select the target repository and analyze its architecture and grammar. | Project Manager / Software Lead |
| October 8, 2026 | Prompt engineering and LLM rules | Define construction, adversarial review, and risk calculation guidance. | Prompt Engineer |
| October 22, 2026 | Loop control design | Define bounded agent feedback loops and risk thresholds. | Software Architect |
| November 5, 2026 | CI/CD automation | Configure GitHub Actions and integrate Semgrep and lint reports. | DevOps Engineer |
| November 19, 2026 | Integration tests and guardrail tuning | Run simulations, reduce false positives, and validate audit trails. | QA Lead / Quality Specialist |
| November 26, 2026 | Deployment and lead-time monitoring | Release the workflow and measure its effect on integration lead time. | Entire Team |
| December 3, 2026 | Final deliverable submission | Package and present the completed project. | Entire Team |

## 11. Phased Implementation

### Phase 1: Report-Only Foundation

- Define finding and audit-report schemas.
- Add deterministic checks and artifact publication.
- Implement adversarial review without code mutation.
- Publish a non-blocking pull request comment.
- Establish lead-time, latency, cost, and false-positive baselines.

### Phase 2: Risk Calibration and Required Check

- Build and evaluate the calibration set.
- Approve versioned risk weights and thresholds.
- Enable a required status check that escalates high-risk changes.
- Document human override and incident procedures.

### Phase 3: Bounded Correction Loop

- Allow an authoring agent to propose corrections on a dedicated branch or patch.
- Re-run all deterministic checks after every correction.
- Enforce cycle, token, time, and cost limits.
- Require human approval before applying or merging agent-generated changes unless maintainers approve a stricter automation policy.

### Phase 4: Controlled Merge Readiness

- Evaluate pilot outcomes and security posture.
- Enable merge-readiness labeling for low-risk pull requests.
- Consider automatic merge only as a separate, explicitly approved repository policy.

## 12. Acceptance Criteria

The initial implementation is complete when:

1. A qualifying pull request triggers the workflow at the current head SHA.
2. The workflow runs the required deterministic checks and preserves their raw reports.
3. The adversarial reviewer returns schema-valid findings with evidence.
4. The scoring engine produces a reproducible score and applies blocking rules.
5. The workflow updates one readable audit comment and uploads a JSON report.
6. Review cycles, retries, tokens, duration, and cost are bounded.
7. Unsafe, incomplete, stale, or inconclusive runs escalate rather than clear.
8. Fork pull requests cannot access protected secrets through the workflow.
9. A calibration suite demonstrates the handling of safe, unsafe, and ambiguous changes.
10. Operational metrics are collected without exposing secrets.
11. Paid inference cannot run without current-SHA authorization by an allowlisted maintainer.

## 13. Open Decisions

The following decisions must be resolved and recorded before production gating:

- Initial provider/model entries, endpoints, secret mappings, and provider-specific disclosures.
- Additional authorized GitHub actors, if any, beyond the initial Phase 1 actor.
- Maximum review cycles and malformed-output retries.
- Token, latency, and cost budgets.
- Risk weights, severity mappings, and acceptance threshold.
- Semgrep ruleset and suppression process.
- Coverage baseline and regression tolerance.
- Human override roles and escalation service-level objective.
- Retention period for audit artifacts.
- Whether and when agent-authored corrections or automatic merge are permitted.

## 14. Traceability

| Requirement area | Primary sections |
|---|---|
| Multi-agent architecture | Sections 4 and 5 |
| Loop control | Section 5.4 |
| Prompts and guardrails | Section 7 |
| Static analysis and tests | Sections 4.3 and 5.2 |
| Risk metric | Section 6 |
| Audit trail | Section 6.3 |
| Conditional CI gate | Section 5.5 |
| False-positive mitigation | Section 8.1 |
| Latency and cost limits | Sections 5.4 and 8.2 |
| Schedule and ownership | Section 10 |

## 15. Source Note

This document is derived from the project's defined-work brief. The brief included inline citation markers referring to source materials that were not part of the repository. Those markers are intentionally omitted here; source references should be added when the underlying bibliography is available.
