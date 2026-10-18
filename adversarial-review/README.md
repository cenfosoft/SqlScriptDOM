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

The shipped [configuration](config/review.json) is intentionally disabled, has no models,
and leaves unapproved limits unset. Configuration validation can succeed while execution
blockers remain: structural validity is not authorization to spend. No provider client
or paid execution command is implemented. In addition, the current
[official GitHub Models documentation](https://docs.github.com/en/github-models)
states that GitHub Models was retired on July 30, 2026. Its former inference API
is not an available activation target. GitHub Copilot is a separate service and
must not be treated as interchangeable API entitlement. A supported provider
selection remains necessary.

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

Findings carry evidence and a disposition, checks carry the evaluated SHA, and review
metadata carries both the head SHA and trusted configuration revision. JSON Schema
checks document shape, not GitHub authenticity, artifact provenance, or cross-record
consistency. Those require the trusted orchestrator and semantic validation.

Only SHA-bound, ordered model history may drive rotation. Disabled/unavailable entries
are skipped, the ordered list wraps, and one eligible model is reused. Availability is
supplied by the host; it must not probe a provider by spending tokens.

## Execution boundary

`ReviewGate` is a pure, offline preflight, **not** an authorization service. Its inputs
must eventually come from authenticated GitHub metadata, verified artifacts, and a
protected environment. A successful result means eligible for an atomic authorization
claim and spend reservation, not eligible to call a model immediately. Persistent
anti-replay storage and monthly budget reservations are not implemented.

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
Missing checks or rounds produce `incomplete`; a changed head produces `stale`.
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

These components are offline building blocks, not a running model loop. No live
provider adapter, persistent authorization claim, monthly spend ledger, or trusted
reporting workflow is enabled.

## PR comment client (not wired to a live workflow)

`GitHubAuditPublisher` is tested against synthetic HTTP handlers only. It supports
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

- Only deterministic validation is wired into Actions. The foundation workflow has
  read-only repository permissions and does not receive a model credential.
- No event, label, comment, workflow rerun, or configuration edit can invoke a model
  through the current CLI: it has no model-execution command.
- Applying `adversarial-review:run` currently does **not** start paid review. Do not
  interpret the presence of the label as a completed authorization implementation.
- Existing CI remains authoritative. Offline tool tests, rendered reports, and
  synthetic successful checks are not evidence that a real PR passed validation.

### Before implementing or enabling trusted execution

1. Confirm repository-supported GitHub model access, exact model identifiers,
   authentication/permissions, and provider retention terms. Personal chat access
   or the local GitHub CLI token is not evidence of Actions model entitlement.
2. Approve round, token, timeout, retry, per-authorization USD, and monthly USD
   limits. Configure provider-side limits as defense in depth; do not assume an
   application estimate is an enforceable billing cap.
3. Implement/test a persistent atomic authorization claim and monthly spend
   reservation. Handle crashes, concurrent PRs, retries with uncertain billing, and
   repeated workflow attempts before any inference request can be made.
4. Create the protected reporting environment with the approved reviewer identity
   and restrict workflow execution to trusted default-branch code. Do not grant
   an untrusted validation job environment access.
5. Supply the team-owned Semgrep rules and Roslyn analyzer policy, pin tooling,
   and verify successful evidence for both authoritative OS matrices.
6. Approve artifact/metrics retention and raw-data access, model-context selection,
   and reporting redaction. Keep raw prompts and credentials out of artifacts.
7. Implement the trusted event join, artifact download/extraction checks, provider
   adapter, bounded model loop, concurrency locks, durable audit history, and failure
   artifact publication. Require fresh head/state checks after approval and before
   every round. The current comment client is not a spending authorization service.
8. Run fork and same-repository pilot scenarios using approved limits and obtain
   team acceptance. Calibrate numeric risk before introducing numeric reporting;
   automatic correction/merge remains outside Phase 1.

### Failures and reruns

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
No live provider is implemented or invoked by these tests.
