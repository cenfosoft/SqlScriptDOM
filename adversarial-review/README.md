# Adversarial Review Foundation

This is separate .NET 8 tooling for the [Phase 1 plan](../docs/phase-1-progress.md), extended with the
JEV risk assessment, labels, and auto-merge described in the [JEV how-to](../docs/jev-risk-assessment.md).
It does not reference, ship with, or change the ScriptDom parser package.
Use the repository SDK selected by [global.json](../global.json).

## Local validation

From the repository root:

```text
dotnet test adversarial-review/tests/AdversarialReview.Tests/AdversarialReview.Tests.csproj -c Debug
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- validate-config adversarial-review/config/review.json
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- validate finding adversarial-review/tests/fixtures/finding.valid.json
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- render audit.json report.md
```

The shipped [configuration](config/review.json) enables paid review with the approved
limits and lists three model candidates discovered through the approved Copilot SDK.
Structural validity is not authorization to spend: each run still requires the label,
environment approval, and current-SHA admission. The trusted `run-review` command and
[`adversarial-review`](../.github/workflows/adversarial-review.yml) workflow execute it. The
[official GitHub Models documentation](https://docs.github.com/en/github-models)
states that GitHub Models was retired on July 30, 2026. Its former inference API
is not an available activation target. GitHub Copilot SDK is a separate, explicitly
approved integration; local catalog discovery does not prove Actions entitlement.

## GitHub Copilot SDK and metadata-only model discovery

The [Copilot integration](src/AdversarialReview.Copilot/) uses
`GitHub.Copilot.SDK` **1.0.22** and its pinned native runtime **1.0.98**.
The package downloads the platform runtime and verifies its release checksum during
build; no separately installed CLI, Node.js, Python, or PowerShell is required.
Build needs access to the GitHub release assets (or an approved SDK-supported mirror).
The existing Windows/Ubuntu tooling matrix must still verify this dependency.

With a supported credential supplied through `COPILOT_GITHUB_TOKEN`, run:

```text
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- list-models
```

This prints bounded JSON catalog metadata to stdout, creates **no inference session**,
sends **no prompt or repository content**, and makes no repository changes.
It uses a fresh private temporary state/working directory, disables ambient login,
logging, and remote sessions, and removes that directory after client disposal.
Discovery has a 60-second timeout and exits nonzero on failures without logging provider
payloads. Tokens must not be supplied as CLI arguments or saved in repository files.
Supported credentials include Copilot OAuth and a fine-grained PAT with the **Copilot
Requests** account permission; classic PATs are rejected. A normal Actions
`GITHUB_TOKEN` is not assumed to grant Copilot access. See
[official authentication guidance](https://docs.github.com/en/copilot/how-tos/copilot-cli/set-up-copilot-cli/authenticate-copilot-cli).

Metadata-only discovery on October 18, 2026 (UTC) returned 23 entries for the current
account, including the following **policy-enabled** candidates:

| Rotation order | Exact model ID | Family |
|---|---|---|
| 1 | `gpt-5.4-mini` | OpenAI |
| 2 | `claude-haiku-4.5` | Anthropic |
| 3 | `mai-code-1.1-flash` | Microsoft |

These are candidates for testing diversity, not a verified price ranking. The configured
order is independent of the sorted discovery display. Availability must be refreshed
on the trusted host; inference entitlement, current prices, and terms must be checked
before use. `auto` is not eligible for deterministic rotation. Unknown/disabled policy
states are ineligible; zero/absent limits are unknown, not unlimited. Gemini was not
returned by this account discovery, so no Gemini model was invented or configured.
The legacy `github-models` schema value remains readable for compatibility with old
fixtures; it has no adapter and is never silently mapped to Copilot.

### Report-only adapter and accounting boundary

`CopilotReviewProvider` implements `IReviewProvider`. Each call uses the exact
discovered, policy-enabled configured model; it disables all tools/MCP/custom agents,
denies permission requests, replaces the system prompt with trusted host guidance,
disables repository instruction/config/skill/hook discovery, host git operations,
embedding retrieval, session stores, telemetry, and infinite-session compaction.
The working directory must be a host-owned empty directory, never the contributor
checkout. JSON context remains bounded and credential-checked; evidence validation
and maintained SHA/state/authorization checks remain the engine/host's responsibility.

The adapter requests prompt/output limits through the SDK capability override and a
session AI-credit ceiling. These SDK controls are **not verified hard per-request
financial caps**; admission-time limits can overshoot on an in-flight call and cannot
prove cancellation stopped billing. Capability overrides and session limits are
experimental SDK surfaces, isolated behind a pinned version. Paid review is enabled
with approved limits; provider-side enforcement is still not verified, so the ledger
reservation and best-effort SDK ceilings are the controls.

There is deliberately **no default currency conversion**: the trusted host must supply
an explicit verified `CopilotBillingConversion`. Complete `assistant.usage` records
are aggregated across calls, must identify the requested model, and are rounded up to
integer microUSD for the ledger. The SDK `Cost`/model multiplier is **not dollars**.
Missing/partial billing, model substitution, truncation, filtering, invalid usage, and
overruns with incomplete usage fail closed without retry and leave billing
unconfirmed. When the SDK reports complete usage beyond a requested token or cost
ceiling, the adapter returns that usage for accounting; the cycle records a
`budget-exceeded` outcome, rejects the response findings, stops further inference, and
retains the financial reservation. Malformed findings retain known usage so the
engine can record a malformed outcome. Cancellation is forwarded and the runtime
attempts a bounded abort; reservations must remain held.
Session disposal and deletion are attempted after each call. Cleanup failures are
explicit errors, not successful reviews; the trusted host must inspect/protect any
remaining private state and enforce its approved retention policy.

The engine enforces at most three rounds per authorization and treats the configured
input/output token values as best-effort aggregate per-cycle budgets: each SDK call
receives the remaining allowance as a requested ceiling, but the SDK does not provide
a verified hard enforcement guarantee. The engine accumulates reported usage and, if
a completed response exceeds the remaining token or cost budget, persists the actual
usage and known cost as `budget-exceeded`, rejects its findings, and makes no further
model calls. Each round is cancellation-bounded by the configured timeout and
reservation expiry. An unresolved critical/high finding stops additional calls and
renders the report status as **Human Review Required**; an attempted but incomplete
review also escalates. SQLite records the separate `human-review-required` final
status. The report schema remains at version 1.0 and its round outcome enum includes
`budget-exceeded`. The review cycle itself does not apply labels; the risk stage in
`run-review` applies the risk label and enables auto-merge. Numeric risk scoring is
replaced by the categorical JEV assessment.

Before any inference, SQLite atomically consumes the authorization and reserves the
cycle's full financial maximum against the repository's monthly balance. When a
reported cost exceeds the remaining cycle reservation, the ledger idempotently adds
the known excess to the monthly reserved balance; if integer capacity is exhausted,
it saturates at the maximum and consequently blocks additional reservations. The durable
claim records the authorization ID, actor, label, PR, evaluated head SHA, and final
status; ordered round outcomes and usage are recorded separately. Duplicate
authorizations fail closed, and neither unknown billing nor a reported overrun
releases the full reservation. A reported cost may exceed the original per-cycle
reservation and is preserved for reconciliation; this is not proof of provider-side
spend enforcement.

The [SDK usage guide](https://github.com/github/copilot-sdk/blob/v1.0.22/docs/features/usage-and-billing.md)
warns that nano-unit conversion must be checked against current billing documentation.
No currency conversion or model inference has been validated against live billing.

## Contracts and compatibility

[Schemas](schemas/) use JSON Schema Draft 2020-12, contract version `1.0`, and local
URN references. `contracts.schema.json` contains shared definitions; consumers must
register it when evaluating the finding, check, run, or audit entry-point schemas.
The .NET validator embeds and registers schemas locally and performs no network retrieval.

Unknown properties, duplicate JSON keys, unsupported versions, invalid enum values,
oversize documents (over 2 MiB), and excessive nesting (over 32 levels) are rejected.
Version `1.0` is intentionally closed: even additive fields require a new contract
version and explicit consumer support. Never silently downgrade a future version.
Tests include valid, invalid, and unsupported-version cases.
For backward compatibility, validation and rendering can read historical `1.0`
reports marked `complete` that contain unresolved high/critical findings. New report
writes still reject that combination, and rendering escalates it to **Human Review
Required** rather than treating the legacy status as clearance.

Findings carry evidence and a disposition, checks carry the evaluated SHA, and review
metadata carries both the head SHA and trusted configuration revision. JSON Schema
checks document shape, not GitHub authenticity, artifact provenance, or cross-record
consistency. Those require the trusted orchestrator and semantic validation.

Only SHA-bound, ordered model history may drive rotation. Disabled/unavailable entries
are skipped, the ordered list wraps, and one eligible model is reused. Availability is
supplied by the host; it must not probe a provider by spending tokens.

## Execution boundary

`TrustedAdmission` verifies live GitHub PR state, base/head repository identities,
the latest matching issue-label event and human actor, and the successful
`pr-validation.yml` run and exact attempt for the PR head. It supports same-repository
and fork identities and fails on partial pagination. Its request head and actor must
come from a trusted `pull_request_target:labeled` snapshot; issue label history alone
does not establish which head was authorized. It does not consume authorization,
grant environment approval, or establish artifact-content trust. `GitHubReviewApi`
requires a finite-timeout client with automatic redirects disabled.

`GitHubReviewEvidence` lists the admitted run's artifacts and exact attempt's jobs.
Required artifacts are named `<check>-<os>-<headSha>-<attempt>`, where OS is
`windows-latest`, `ubuntu-latest`, or `independent`. Jobs must be successful
`build (<os>)`/`test (<os>)` or independent `semgrep`/`roslyn`; required steps are
`Build`, `Run tests Windows`, `Run tests Linux`, `Run Semgrep`, and `Run Roslyn`.
Analyzer jobs/artifacts do not exist until the team supplies policy; missing evidence
remains a blocker, never a synthetic pass.

Downloads are bounded at 55 MiB compressed; manifest/raw limits additionally apply.
ZIPs are inspected in memory, not extracted or executed. Absolute/traversal/device
paths, case-colliding entries, links, unsupported file types, excess nesting/expansion,
unlisted files, and checksum/identity mismatches are rejected. Signed downloads allow
only HTTPS Azure Blob or Actions storage origins and receive no GitHub credential;
both HTTP clients must disable automatic redirects and have finite timeouts.
Admission also compares the PR workflow blob to the trusted configuration revision.
A PR changing the authoritative workflow requires human handling, rather than trusting
its own revised checks. Checksums establish integrity, not correctness of contributor
code. Live GitHub artifact/job shapes and fork SHA lookup still require pilot evidence.

`ReviewGate` is a pure, offline preflight, **not** an authorization service. Its inputs
must eventually come from authenticated GitHub metadata, verified artifacts, and a
protected environment. A successful result means eligible for an atomic authorization
claim and spend reservation, not eligible to call a model immediately. The SQLite
implementation below provides these controls for a shared persistent host; hosted
Actions deployment and trusted event admission are not configured.

The mandatory evidence set includes build/test results on both Windows and Ubuntu,
plus independent Semgrep and Roslyn results. Repository-owned Semgrep rules live in
`.semgrep.yml`; `.editorconfig` scopes Roslyn diagnostics to vector AST helpers and
their script-generator visitors. These policies are bounded and advisory; they do not
represent a complete security audit.

The repository `static-analysis` workflow records Semgrep findings as advisory SARIF;
findings alone do not fail the workflow or claim a clean review. Tool installation,
scanner/configuration, Roslyn build, and evidence-manifest failures remain explicit
failures. A successful static-analysis check means the tools ran and evidence was
recorded, not that no findings exist. Reviewers must inspect the attached SARIF and
logs before using the results in an advisory assessment.

Never include API keys, request headers, full prompts, or raw provider responses in
configuration, fixtures, comments, or logs. Structural validation errors deliberately
omit document values. Provider-specific retention disclosures, output redaction, and
artifact retention remain required before live operation.

## Offline advisory reports

`AuditEngine.Create` binds checks and findings to one run, requires the configured
round count for a complete report, and lists unresolved high/critical findings.
Unresolved high/critical findings or an attempted incomplete model review produce
`incomplete` in the versioned report (rendered as **Human Review Required** and
recorded separately in SQLite); a preparation report with no model rounds and missing
checks/rounds remains `incomplete`. A changed head produces `stale`.
Complete describes the evidence collection, **not** safety or merge clearance.
Numeric risk stays `null` until the team approves provisional weights; zero is not
a substitute for missing evidence.

The renderer validates audit input, escapes untrusted Markdown/HTML and mentions,
redacts supplied known secrets and common API-token patterns, and bounds comments
to 45,000 characters. Heuristic redaction cannot prove that arbitrary text contains
no credentials; the host must supply exact secret values for redaction and enforce
data-minimization before persistence. Raw audit input must never be published directly.
Rendering writes a new local file only and refuses to overwrite an existing file.
It does not publish to GitHub or treat stored history as spending authorization.

The [reviewer prompt](../.github/prompts/adversarial-review.prompt.md) is an offline
starting point. Prompt-injection resilience still needs live pilot evaluation.

## Lead-time baseline

```text
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- baseline 6 100 phase1-human-nondraft-merged-only baseline.json baseline.md cenfosoft/SqlScriptDOM microsoft/SqlScriptDOM
```

Supply a read-only GitHub API token using `GH_TOKEN`, never a command-line argument.
The token is sent only to `cenfosoft/SqlScriptDOM`; Microsoft upstream and any
additional sources are requested anonymously, so the fork credential is not sent to
other organizations. All sources must be publicly readable. The arguments explicitly
select the trailing calendar-month window, per-resource pagination cap, approved
exclusion profile, JSON and Markdown outputs, and one or more `owner/repository`
sources. The comparison must include both `cenfosoft/SqlScriptDOM` and
`microsoft/SqlScriptDOM`. Every request has a 30-second timeout and the combined
collection has a ten-minute cancellation limit. Outputs are new files; existing
files are not overwritten. No paid model API is used.
The command exits with code **3** if either repository is incomplete; inspect
diagnostics before using or comparing its cohorts.

The command collects a **current-state** snapshot, not a reconstruction of arbitrary
historical PR states. It scans PRs regardless of creation date so an older PR readied
recently is not lost. Timelines establish the first ready-for-review event; creation
is used only when a complete timeline has no draft/readiness transitions. Ambiguous
or unavailable timelines are diagnosed, not guessed.

- Merged human PRs: first ready-for-review (or creation if initially non-draft) to
  `merged_at`, with merged-only count and median elapsed hours.
- Open non-draft human PRs: the same start event to the repository extraction time,
  reported separately as a right-censored cohort. Do not treat open age as completed
  lead time.
- The inclusive trailing calendar-month window is applied to the start event, not
  `created_at`; timestamps are compared as UTC instants and durations are elapsed
  hours. Fork and upstream extraction timestamps can differ slightly.
- Excluded: bots, currently draft PRs, closed-unmerged PRs, and readiness outside
  the window. Human dependency and documentation-only PRs remain in scope; no
  topic or label exclusions are applied. Unknown/malformed author or PR state,
  duplicate PRs, incomplete timelines, and ambiguous histories make the
  collection incomplete and are diagnosed, not inferred.
- PRs last updated before the window are not sent a timeline request: their
  readiness cannot occur after their last update. The collector still scans PR
  list pages to find older PRs updated within the window.
- GitHub rate-limit responses (`429`, or `403` with zero remaining quota) are
  reported as `RateLimited`, mark the collection incomplete, and produce exit
  code **3**. The client does not retry blindly or claim a complete sample.

The JSON records the collection policy, extraction timestamp, both source repositories,
per-source observation/range, PR numbers with full repository provenance, durations,
cohort counts, exclusions, limitations, and diagnostics. The Markdown report provides
the comparable per-repository summary and separate merged/open tables. A median is
unavailable when there are no eligible merges. The report does not set a minimum sample
threshold or improvement target; both require Product Owner approval. Never claim
causality or improvement from an incomplete, open, or otherwise non-comparable sample.
Argument/input failures exit **1**, and command syntax errors exit **2**.
Snapshots can be recalculated offline through `BaselineCalculator.Calculate`.
The report contains public PR numbers, URLs, and timestamps, not author names,
source, prompts, or credentials. Raw API snapshots are not emitted. Offline
recalculation requires a separately authorized saved `RepositorySnapshot` passed to
`BaselineCalculator.Calculate`. Storage, retention, and access policy still require
team approval; do not automatically commit or upload a raw dataset.

## Deterministic evidence

`manifest <directory> <check> <platform> <conclusion>` inventories raw evidence and
writes `manifest.json`. It reads repository, PR head, workflow run, and attempt from
the standard GitHub Actions environment/event file. The manifest records SHA-256,
byte sizes, check identity, platform, and conclusion. Empty evidence, symbolic links,
excessive nesting, more than 1,000 files, files over 20 MiB, and totals over 50 MiB
are rejected. `EvidenceManifest.Verify` requires independently obtained trusted run
metadata and rejects missing, modified, extra, or duplicate files. The manifest does
not attest that contributor-supplied code or logs are honest.

The [existing PR workflow](../.github/workflows/pr-validation.yml) retains both OS
matrices and build/test commands while publishing raw build logs and TRX results,
including failures where output is available. Drafts are skipped; ready-for-review
events start validation. [Foundation validation](../.github/workflows/adversarial-review-validation.yml)
runs the offline tool tests on Windows and Ubuntu, without model credentials or PR
write permission. The earlier enforced suite total was 173, up from the 172-test
Phase 1 reference after adding manifest-schema coverage for the preparation and
static-analysis checks. Subsequent integration and budget-accounting coverage brings
the current unfiltered suite to 194 tests, and the workflow enforces that count.
Artifacts currently use the repository's existing retention setting; the team must
approve a retention policy before live review.

Trusted consumption must verify workflow provenance and safe archive extraction
**before** parsing a manifest; checksum validation alone is insufficient. The
`static-analysis` workflow now runs the repository-owned Semgrep and scoped Roslyn
policies and records SHA-bound evidence. Missing outputs or a failed manifest step
are failures/incomplete evidence, never successful deterministic checks.

## Bounded reviewer inputs and outputs

`ReviewContextBuilder.Build` separates trusted guidance from a JSON `untrustedData`
object. The host supplies an explicit UTF-8 byte budget, bounded file inventory,
known secret values, and the trusted configuration revision. Oversize contexts fail
instead of silently truncating away important changes. This is a **byte** bound,
not model-specific token accounting; token limits still require a provider adapter.

`ReviewerOutput.Parse` accepts only schema-valid JSON arrays, rejects changed SHAs,
invented source/maintainer dispositions, duplicate finding IDs, out-of-context files,
out-of-range line numbers, and unknown check/artifact references. It rejects
credential-like output before persistence, including JSON-escaped known secrets.
Evidence membership does not prove the finding is true; reviewer validation and
later human dispositions remain necessary.

These components are offline building blocks. The bounded engine and SQLite ledger
can be exercised with fake providers. The SDK adapter is implemented and metadata
discovery is verified locally. The trusted workflow composes them with the ledger
persisted on the `adversarial-review-ledger` branch.

## PR comment client

`GitHubAuditPublisher` and its persistent-host integration are tested against synthetic HTTP handlers only. It supports
one `github-actions[bot]` comment with a stable marker and head/run/attempt metadata,
creates or updates idempotently, refuses ambiguous duplicates or newer stored runs,
and rejects draft, closed, or mismatched-head PRs. It rechecks before publication
and marks its own comment stale if the PR changes during the write.

The host **must serialize the complete call per PR**; GitHub comment updates are
not an atomic compare-and-swap with PR head updates. Pre/post checks detect races
but are not a substitute for a trusted workflow concurrency policy. Comment metadata
is for publication ordering only, never authorization or budget accounting.

Use a finite-timeout `HttpClient`, an Actions bot reporting credential, and a bounded
cancellation token. The client uses only GitHub API PR reads and issue-comment
reads/writes, logs neither tokens nor response bodies, and performs no model calls,
approvals, source writes, or merges. HTTP/permission/schema/pagination errors remain
explicit; the eventual host must retain the audit artifact and expose failed
publication without rerunning inference. `run-review` publishes through this client
under the workflow's global concurrency group.

## Operator runbook and activation prerequisites

The initial categorical factor boundaries are stored in
[`config/risk-rubric.json`](config/risk-rubric.json). `RiskRubric` classifies
changed code lines, added-line coverage, adversarial findings, and new static
warnings using those boundaries. Missing coverage fails validation, while zero
instrumentable added lines are `not-applicable`. Validate the configuration with:

```text
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- validate-risk-rubric adversarial-review/config/risk-rubric.json
```

`RiskSignalCollector` derives the factor inputs for the admitted head SHA, and
`RiskStage` sends them to JEV and records the decision in the audit report.

`JevRiskAssessor` sends the classified factors to the TypeSafe JEV System One
endpoint (`https://api.typesafe.ai/v1/systemone`) as one `choice` question and
validates the typed `low`/`mid`/`high` answer, confidence, probabilities, and usage.
The request contains only categorical factors and the rubric version: no source,
paths, finding text, or identities. The model is pinned in the rubric
(`jevModel`); aliases such as `jev-latest` are rejected. Non-success HTTP status,
timeouts, network errors, and malformed or unexpected responses are recorded as
`unavailable` or `invalid` without retry and never make a PR eligible. Only an
assessed `low` result for a complete audit without an unresolved high/critical
finding is eligible for the low-risk label and auto-merge.

Inspect the exact payload without a credential or network request:

```text
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- assess-risk adversarial-review/config/risk-rubric.json adversarial-review/tests/fixtures/risk-signals.sample.json request.json --dry-run
```

For a live call, set `JEV_API_KEY` in the environment (never as an argument) and
omit `--dry-run`. The command exits 3 when the assessment is not `assessed`. The
manual [`jev-risk-smoke`](../.github/workflows/jev-risk-smoke.yml) workflow runs
the same synthetic check in the protected `adversarial-review` environment with
the `JEV_API_KEY` environment secret; `workflow_dispatch` is available only after
the workflow exists on the default branch. See the
[JEV how-to](../docs/jev-risk-assessment.md) for setup, configuration locations,
and how the assessment connects to the review flow.

### Current operating mode

- [`config/review.json`](config/review.json) enables paid review with the approved
  limits: 3 rounds, 60,000 input and 8,000 output tokens per authorization, a 120 s
  timeout, 1 retry, USD 1 per authorization, and USD 20 per month.
- `copilotBilling` converts SDK usage to dollars. GitHub bills Copilot in AI credits
  at USD 0.01 each; the SDK reports nano-units of a credit, so 1,000,000,000 nano-units
  equal one credit. The monthly budget therefore equals 2,000 credits.
- The label `adversarial-review:run`, applied by `allancascante` after CI completes,
  starts the trusted [`adversarial-review`](../.github/workflows/adversarial-review.yml)
  workflow on a non-draft PR that targets the default branch.
- Existing CI remains authoritative. A failed build, test, or required static analysis
  blocks before any model call.

### Trusted review workflow

The workflow runs default-branch code in the protected `adversarial-review`
environment and reads the PR only through the GitHub API; it never checks out or
executes PR code. It runs:

```text
dotnet run --no-build --project adversarial-review/src/AdversarialReview.Cli -- run-review adversarial-review/config/review.json adversarial-review/config/risk-rubric.json .github/prompts/adversarial-review.prompt.md .github/copilot-instructions.md <ledger.db> <output-directory>
```

`run-review` reads `GITHUB_EVENT_PATH`, `GITHUB_EVENT_NAME`, `GITHUB_REPOSITORY`,
`GITHUB_ACTOR`, `GITHUB_SHA`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`, `GH_TOKEN`,
`COPILOT_GITHUB_TOKEN`, and the optional `JEV_API_KEY`. It then:

1. Correlates the label webhook with its label event and the newest
   `pr-validation` run for the head, and verifies admission.
2. Requires a recorded approval of the `adversarial-review` environment for the
   current run; without it the audit is blocked and no model is called.
3. Verifies the CI evidence for the head and runs up to three Copilot review rounds
   with the [trusted reviewer prompt](../.github/prompts/adversarial-review.prompt.md), reserving spend in
   the SQLite ledger first.
4. Collects risk signals and calls JEV. Missing inputs skip JEV.
5. Publishes or updates one audit comment, applies `adversarial-review:low-risk`
   or `adversarial-review:human-review`, and for eligible PRs enables GitHub
   auto-merge (merge commit) bound to the evaluated head SHA. Branch protection,
   including the required human approval on `main`, still applies before GitHub merges.
   A non-eligible result disables auto-merge left over from an earlier eligible run.
6. Republishes the comment with the label and auto-merge outcomes.

Spend is persisted to the `adversarial-review-ledger` branch after every run,
including failed runs. If the ledger branch cannot be read for any reason other
than not existing yet, the job stops before the review so the monthly budget is never
bypassed. A global concurrency group serializes runs so the monthly
budget is enforced against one ledger. If a runner crashes before the push, that
run's spend record is lost; this is an accepted pilot limitation.

The PR validation workflow must be identical on the default branch and the PR head;
otherwise admission requires human handling. Apply the label **after CI completes**;
if correlation is ambiguous, the head changes, or the newest run is incomplete or
failed, the review fails instead of using an older run. Remove and reapply the label
for a new head; a rerun keeps the original webhook snapshot.

Successful runs write `audit.json` and `report.md`. Expected failures write a
constant-content `failure.json` without source, tokens, or provider payloads. The
uploaded artifact includes logs and a SHA-256 manifest. The manifest is an integrity
checksum, not an authenticity proof.

### Required GitHub settings

- Environment `adversarial-review` with required reviewers; allow self-review when
  the label actor also approves the run.
- Environment secrets `COPILOT_GITHUB_TOKEN` (fine-grained PAT with Copilot Requests
  permission) and `JEV_API_KEY`.
- Repository setting **Allow auto-merge**.

### Failures and reruns

- A structural validation failure exits nonzero without echoing document contents.
  Fix the input rather than relaxing schema enforcement.
- An incomplete baseline exits 3 and retains diagnostics in its JSON. Extend the
  window/page allowance deliberately; do not relabel partial results as complete.
- Missing/failed evidence or manifest generation must stay failed/incomplete.
  Inspect raw artifacts and the actual Actions run. Never fabricate analyzer passes.
- Publication retries must not repeat model inference. A new head always needs new
  authorization. There is no automatic inference retry.

### Disable and incident handling

Set `enabled: false` in `config/review.json` to stop paid review; the flag is not a
provider-side billing control. For an incident, disable the `adversarial-review`
workflow, cancel queued or running review jobs, revoke or rotate the Copilot and JEV
credentials, block environment approvals, disable auto-merge on affected PRs, and
review provider usage before restoring access. Do not disable authoritative parser CI
to disable advisory review. Delete exposed secrets from comments and artifacts using
repository administration tools.

## Bounded review-cycle engine

`ReviewCycle.RunAsync` joins validated configuration, the run/context identities,
authorization preflight, required checks, a provider, and a spend ledger. It claims
the whole cycle before model execution, checks the current PR state and reservation
lease before every round, rotates eligible configured models, and bounds request
timeouts by both configuration and reservation expiry. The provider receives the
remaining input-token and maximum-cost allowances as requested ceilings. These are
best-effort SDK controls, not verified hard caps.
Validating reported usage is post-request detection: a known overrun is recorded as
`budget-exceeded`, findings are discarded, and the cycle stops without another call.

The engine rejects malformed output, invalid usage, conflicting finding identifiers,
and unexpected history. Failed/uncertain requests stop the cycle without retries,
even when a larger retry allowance is configured: an allowance is a ceiling, not a
requirement to retry. Known usage is recorded; unknown usage stays unknown and
retains the full reservation. Settlement is attempted with a separate bounded token
after request cancellation. Storage errors propagate instead of losing accounting.

Prior model history is explicit input tied to the evaluated SHA. The trusted host
must authenticate its origin and persist each outcome before acknowledging a run.
Synthetic tests exercise complete rotation, resumed rotation, stale heads, denied
claims, replay, budget exhaustion, malformed output, timeouts, and usage violations.
The Copilot adapter is tested with an injected runtime; these tests invoke no live model.

## Persistent authorization and spend ledger

`TrustedReviewHost` now joins admission, verified evidence, context, the real ledger,
and an injected provider. It holds a private per-PR file lock across execution and
uses immutable, atomically written audit JSON. Blocked runs produce incomplete reports
without fetching source or creating model calls. Admission is rechecked after ingestion
and while the PR remains current before each round. The caller still supplies trusted
environment approval, reporting-run identity, provider policy, and deployment storage.
`PublishStoredAsync` reads a persisted report under the same lock; publication retries
never re-enter inference. Recoverable cycle errors preserve a sanitized incomplete report
and available ledger accounting, not a fabricated clean result. Once a claim is
consumed, interruptions also persist `human-review-required` using a separate bounded
settlement token. Failure to persist is an explicit error.

`GitHubReviewContext` retrieves bounded textual patches and current Git blobs through
the API, verifies blob hashes and patch addition/deletion counts, and rechecks PR state.
Binary/missing patches, unavailable blobs, invalid UTF-8, and oversized contexts fail
rather than silently reviewing a subset. It does not execute or check out PR content.
The trusted host must select the instruction policy; all API source text is untrusted.

The ledger schema is now version 2 and includes provider/model/prompt-version metadata
for SHA-bound rotation across authorizations. Arbitrary selection explanations are not
persisted; recovered history uses a fixed explanation. Version-1 migration preserves all
claims, usage, and reservations. Legacy rounds lack model metadata, so history reads
require explicit reconciliation instead of inventing models or resetting spending.
History reads are bounded at 100 rounds and never release reservations. Audits contain
findings and therefore need an approved private-storage/retention policy. Per-PR file
locks require one shared persistent executor/filesystem; they are not distributed locks
for independent hosted runners. Root storage and all ancestors must be host-owned,
private, and outside contributor control.

`SqliteReviewSpendLedger` implements atomic admission using an immediate SQLite
transaction. The host must supply **one trusted persistent database shared by every
worker for a repository**. A fresh database on each GitHub-hosted runner is not
durable storage or a repository-wide budget and must never be used as such.
This implementation is suitable for a single persistent host with local shared
SQLite storage; independent hosted runners require a separately designed shared
transactional service or serialized persistent executor before activation.

Every authorization event reserves its entire configured maximum cost against the
repository's UTC calendar-month budget, across PRs. The same event cannot be reused
for another PR or SHA. Lowered monthly limits are retained; automatic limit increases
do not retroactively expand that month's ledger. Claims expire at the next UTC month
boundary; the engine checks the lease and bounds cancellation to that boundary.
Delayed settlement remains possible, but provider billing that crosses the boundary
requires reconciliation. Cancellation cannot guarantee a remote provider stopped
billing; provider-side limits remain essential.

Accounting uses integer microUSD, not floating-point currency. Sub-microUSD values,
overflow, invalid claims, missing claims, altered claims, out-of-order rounds,
conflicting duplicate records, and overspending are rejected. Identical settlement
retries are idempotent. Unknown usage is persisted as unknown and blocks further
calls. Full reservations are **never automatically released**, even for unused
rounds or failures; this is intentionally conservative until an explicit reconciliation
policy is approved.

The database stores claim identity, accounting, usage and outcome metadata, not
prompts or provider response bodies. Storage/corruption/lock errors remain visible
and stop execution. Database protection, backup, retention, and access are host
responsibilities; do not put the ledger in git, artifacts, or mutable PR content.
