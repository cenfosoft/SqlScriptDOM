# Adversarial Review Foundation

This is separate .NET 8 tooling for the [Phase 1 report-only plan](../docs/phase-1-progress.md).
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

The shipped [configuration](config/review.json) is intentionally disabled, lists three
model candidates discovered through the approved Copilot SDK,
and leaves unapproved limits unset. Configuration validation can succeed while execution
blockers remain: structural validity is not authorization to spend. A provider adapter
is implemented, but there is no paid execution command or secret-bearing workflow. The
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
experimental SDK surfaces, isolated behind a pinned version. Paid activation remains
blocked pending approved limits and verified provider-side enforcement.

There is deliberately **no default currency conversion**: the trusted host must supply
an explicit verified `CopilotBillingConversion`. Complete `assistant.usage` records
are aggregated across calls, must identify the requested model, and are rounded up to
integer microUSD for the ledger. The SDK `Cost`/model multiplier is **not dollars**.
Missing/partial billing, model substitution, truncation, filtering, invalid usage, and
overruns fail closed without retry and leave billing unconfirmed. Malformed findings
retain known usage so the engine can record a malformed outcome. Cancellation is
forwarded and the runtime attempts a bounded abort; reservations must remain held.
Session disposal and deletion are attempted after each call. Cleanup failures are
explicit errors, not successful reviews; the trusted host must inspect/protect any
remaining private state and enforce its approved retention policy.

The engine enforces at most three rounds per authorization and treats the configured
input/output token values as aggregate per-cycle budgets: each SDK call receives only
the remaining allowance. Each round is cancellation-bounded by the configured timeout
and reservation expiry. An unresolved critical/high finding stops additional calls and
renders the report status as **Human Review Required**; an attempted but incomplete
review also escalates. SQLite records the separate `human-review-required` final
status. The JSON v1.0 report contract remains unchanged, and the advisory status does
not apply a GitHub label or change the PR's open/closed state. Numeric risk scoring
remains disabled; no score threshold is inferred.

Before any inference, SQLite atomically consumes the authorization and reserves the
cycle's full financial maximum against the repository's monthly balance. The durable
claim records the authorization ID, actor, label, PR, evaluated head SHA, and final
status; ordered round outcomes and usage are recorded separately. Duplicate
authorizations fail closed, and unknown billing does not release reserved funds.

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

The mandatory evidence set currently includes build/test results on both Windows and
Ubuntu, plus independent Semgrep and Roslyn results. Missing team-supplied analysis
policies must not be fabricated as successful checks.

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
dotnet run --project adversarial-review/src/AdversarialReview.Cli -- baseline cenfosoft SqlScriptDOM baseline.json 6 100
```

Supply a read-only GitHub API token using `GH_TOKEN`, never a command-line argument.
The final parameters are trailing calendar months (default 6) and the per-resource
pagination cap (default 100). Every request has a 30-second timeout and collection
has a ten-minute cancellation limit. Outputs are new files; existing files are not
overwritten. No paid model API is used. For a small sample, rerun into a different
file with a longer month range; inspect completeness before comparing results.

The command collects a **current-state** snapshot, not a reconstruction of arbitrary
historical PR states. It scans PRs regardless of creation date so an older PR readied
recently is not lost. Timelines establish the first ready-for-review event; creation
is used only when a complete timeline has no draft/readiness transitions. Ambiguous
or unavailable timelines are diagnosed, not guessed.

- Merged human PRs: ready-for-review to merge, with merged-only count/median hours.
- Open non-draft human PRs: ready-for-review to observation, as a separate censored
  cohort. Do not treat open age as completed lead time.
- Excluded: bots, currently draft PRs, closed-unmerged PRs, and readiness outside
  the trailing window. Human documentation-only PRs remain in scope.

The JSON records observation/range, durations, exclusions, limitations, and diagnostics.
`isComplete: false` and exit code **3** indicate truncated, unavailable, malformed,
or inconsistent evidence; partial cohorts must not be presented as a complete baseline.
Argument/input failures exit **1**, and command syntax errors exit **2**.
Snapshots can be recalculated offline through `BaselineCalculator.Calculate`.
Raw snapshots contain PR numbers and timestamps, not source, prompts, or credentials.
Storage, retention, and access policy still require team approval; do not automatically
commit or upload a raw dataset.

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
write permission. Artifacts currently use the repository's existing retention setting;
the team must approve a retention policy before live review.

Trusted consumption must verify workflow provenance and safe archive extraction
**before** parsing a manifest; checksum validation alone is insufficient. Semgrep
and Roslyn evidence remain unavailable until team-owned policies are supplied.
Missing outputs or a failed manifest step are failures/incomplete evidence, never
successful deterministic checks.

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
discovery is verified locally; no paid execution, deployed ledger, or trusted reporting
workflow is enabled.

## PR comment client (persistent-host integration; not deployed)

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
publication without rerunning inference. There is deliberately no CLI `publish`
command or secret-bearing workflow until authorization, budgets, and environment
protection are implemented and approved.

## Operator runbook and activation prerequisites

### Current operating mode

- Deterministic validation and a trusted **preparation-only** workflow are defined.
  Both have read-only repository permissions and receive no model credential.
- No event, label, comment, workflow rerun, or configuration edit can invoke a model
  through the current CLI: it has no model-execution command.
- Applying `adversarial-review:run` by `allancascante` prepares evidence only after
  the workflow is installed on the default branch; it does **not** start paid review.
- Existing CI remains authoritative. Offline tool tests, rendered reports, and
  synthetic successful checks are not evidence that a real PR passed validation.

### Before implementing or enabling trusted execution

1. Confirm repository-supported GitHub model access, exact model identifiers,
   authentication/permissions, and provider retention terms. Personal chat access
   or the local GitHub CLI token is not evidence of Actions model entitlement.
2. Approve round, token, timeout, retry, per-authorization USD, and monthly USD
   limits. Configure provider-side limits as defense in depth; do not assume an
   application estimate is an enforceable billing cap.
3. Provision and verify a shared persistent host/store for atomic authorization and
   monthly spend reservations. The SQLite ledger is locally tested; deployment must
   preserve its single-store guarantees across crashes, concurrent PRs, uncertain
   billing, and workflow attempts before any inference request can be made.
4. Create the protected reporting environment with the approved reviewer identity
   and restrict workflow execution to trusted default-branch code. Do not grant
   an untrusted validation job environment access.
5. Supply the team-owned Semgrep rules and Roslyn analyzer policy, pin tooling,
   and verify successful evidence for both authoritative OS matrices.
6. Approve artifact/metrics retention and raw-data access, model-context selection,
   and reporting redaction. Keep raw prompts and credentials out of artifacts.
7. Deploy the implemented admission/evidence/host components to the chosen persistent
   executor. Wire its authenticated webhook head snapshot, verified protected-environment
   approval, Copilot credential/billing policy, and bot publication credential. The
   preparation workflow intentionally does not perform that handoff. Require fresh
   head/state checks after approval and before every round.
8. Run fork and same-repository pilot scenarios using approved limits and obtain
   team acceptance. Calibrate numeric risk before introducing numeric reporting;
   automatic correction/merge remains outside Phase 1.

### Failures and reruns

The [preparation workflow](../.github/workflows/adversarial-review-prepare.yml) is
triggered only by the authorized label on PRs targeting the default branch. It checks
out the base workflow's immutable `github.sha`, never the PR head or merge ref.
It names the `adversarial-review` environment but receives only a read-only Actions
token. Environment protection must be configured manually; naming an environment does
not establish approval. If GitHub creates an unprotected environment, this workflow
still cannot invoke inference or spend tokens.

Its command is:

```text
dotnet run --no-build --project adversarial-review/src/AdversarialReview.Cli -- prepare-review adversarial-review/config/review.json <output-directory>
```

It reads `GITHUB_EVENT_PATH`, `GITHUB_EVENT_NAME`, `GITHUB_REPOSITORY`, `GITHUB_ACTOR`,
`GITHUB_SHA`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`, and read-only `GH_TOKEN` from the
trusted Actions runtime. It validates a labeled snapshot against the allowlist and
default branch, correlates label/update time conservatively, selects the newest
head-matching validation run, and performs admission/evidence verification.
No Copilot client, source context, spend database, or comment publisher is created.
It remains preparation-only even if someone enables the JSON configuration.
It does not turn label history or its generated artifact into reusable funding authority.
Apply the label **after CI completes**; if correlation is ambiguous, the head changes,
or the newest run is incomplete/failed, it fails instead of using an older run.
Remove/reapply the label to request preparation for a new head; a workflow rerun
retains the original webhook snapshot and cannot authorize a different head.

Successful preparation writes schema-valid `audit.json` and escaped `report.md`,
explicitly marked incomplete/stale with no model rounds and no numeric risk score.
Missing analyzers remain blockers. Expected command failures exit nonzero and write
a constant-content `failure.json` without source, tokens, exception text, or provider
payloads, validated by [its versioned schema](schemas/preparation-failure.schema.json).
Output files are create-new only. The workflow uploads available reports
with `always()`; build/setup failures are still visible as failed jobs and may produce
no report. Actual Actions/fork runs and environment approval behavior remain unverified.
There is no automatically posted comment and no paid execution step in this workflow.
Disable this preparation workflow separately if metadata preparation itself must stop;
`enabled: false` controls paid execution, not this read-only diagnostic path.

- A structural validation failure exits nonzero without echoing document contents.
  Fix the input rather than relaxing schema enforcement.
- An incomplete baseline exits 3 and retains diagnostics in its JSON. Extend the
  window/page allowance deliberately; do not relabel partial results as complete.
- Missing/failed evidence or manifest generation must stay failed/incomplete.
  Inspect raw artifacts and the actual Actions run. Never fabricate analyzer passes.
- Retry only the offline command or publication step that failed. Publication
  retries must not repeat model inference. A new head always needs new authorization.
- There is no live inference retry mechanism yet. Future retries must be bounded,
  reserved, auditable, and safe when a timed-out request may already have been billed.

### Disable and incident handling

Keep `enabled: false` as the default. The eventual trusted host must enforce it
before accessing providers, but the flag is not a provider-side billing control.
For an incident after deployment, disable the trusted workflow, cancel queued/running
review jobs, revoke or rotate provider credentials, block environment approvals, and
review provider usage before restoring access. Do not disable authoritative parser CI
to disable advisory review. Preserve sanitized incident evidence under the approved
retention policy; delete exposed secrets from comments/artifacts using repository
administration tools. No repository environment or remote configuration has been
created by this implementation.

## Bounded review-cycle engine

`ReviewCycle.RunAsync` joins validated configuration, the run/context identities,
authorization preflight, required checks, a provider, and a spend ledger. It claims
the whole cycle before model execution, checks the current PR state and reservation
lease before every round, rotates eligible configured models, and bounds request
timeouts by both configuration and reservation expiry. The provider must honor
cancellation and enforce input-token and maximum-cost limits before sending a
request; validating reported usage afterward is only detection, not a billing cap.

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
