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
or paid execution command is implemented.

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
