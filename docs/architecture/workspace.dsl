workspace "Adversarial Reviewer Agent" "Phase 1 report-only context, containers, and trust boundaries for SqlScriptDOM" {

    model {
        dev = person "Contributor" "Opens and updates pull requests from forks or repository branches"
        maint = person "Maintainer" "Authorizes a SHA-bound review cycle and approves protected environment access"

        reviewer = softwareSystem "Adversarial Reviewer Agent" "Prepares deterministic evidence and, only after separate policy approval, may provide advisory findings; cannot modify code, approve, or merge PRs" {
            validation = container "Untrusted validation" "Executes PR builds, tests, and team-supplied analysis on Windows and Ubuntu with no provider credentials" ".NET / GitHub Actions" {
                tags "Untrusted"
            }
            evidence = container "Validation evidence" "Raw check outputs and manifests bound to repository, PR head SHA, workflow run, and run attempt; treated as untrusted data" "GitHub Actions artifacts" {
                tags "Untrusted"
            }
            reporting = container "Trusted preparation and reporting" "Uses default-branch code and configuration to validate authorization and evidence and prepare a report artifact; inference remains disabled until separately approved" ".NET / GitHub Actions" {
                tags "Trusted"
            }
            history = container "Audit history" "When publication is approved, records authorization, PR head SHA, model rounds, usage, and outcomes in reports and artifacts" "GitHub PR comments / Actions artifacts" {
                tags "Data"
            }
            baseline = container "Lead-time baseline collector" "Reads PRs and timelines from the fork and Microsoft upstream; emits comparable merged and open-censored cohorts to local JSON and Markdown files" ".NET 8 CLI" {
                tags "Data"
            }
        }

        github = softwareSystem "GitHub" "Hosts source, PR events, authoritative checks, workflow identities, and environment approvals" {
            tags "External"
        }
        sourceRepositories = softwareSystem "GitHub source repositories" "Read-only PR and timeline history for cenfosoft/SqlScriptDOM and microsoft/SqlScriptDOM" {
            tags "External"
        }
        llm = softwareSystem "Configured model provider" "Provider access, model identifiers, and terms must be confirmed before inference is enabled; no model calls occur in report-only mode" {
            tags "External"
        }

        dev -> github "Opens or updates an untrusted PR" "Git / HTTPS"
        maint -> github "May apply the opt-in label and approve the protected environment once it is configured; retains merge authority" "HTTPS"
        maint -> baseline "Runs a bounded comparison with an explicit window, page limit, and source list" "dotnet CLI"
        baseline -> sourceRepositories "Reads PR metadata and timelines; sends no writes" "GitHub REST API"
        baseline -> maint "Writes provenance-preserving JSON and Markdown reports locally" "File system"
        github -> validation "Triggers deterministic validation; drafts are skipped" "pull_request"
        validation -> evidence "Uploads raw results and SHA-bound manifests, including failures" "Actions artifact upload"
        github -> reporting "Supplies authenticated authorization and check metadata; neither labels nor completed checks alone authorize inference" "GitHub API / trusted workflow events"
        reporting -> github "Reads current PR state and bounded diff as data; never executes contributor code" "GitHub API / HTTPS"
        reporting -> evidence "Validates origin, run attempt, SHA, completeness, checksums, and size before consuming data" "Actions artifact download"
        reporting -> llm "Future gated workflow only; sends bounded untrusted context after authorization, environment approval, budgets, and required checks pass" "HTTPS API"
        reporting -> history "Updates advisory report and model history; no source, approval, or merge operations" "GitHub API / artifact upload"
        history -> reporting "Supplies validated prior model usage; never authorizes spending or serves as the sole replay lock" "GitHub API / artifact download"
        dev -> history "Reads findings and deterministic evidence" "HTTPS"
        maint -> history "Reviews incomplete reports and finding dispositions" "HTTPS"
    }

    views {
        systemContext reviewer "Context" {
            include *
            autoLayout lr
        }

        container reviewer "Phase1Containers" {
            include *
            autoLayout lr
        }

        styles {
            element "Person" {
                shape Person
                background #08427b
                color #ffffff
            }
            element "Software System" {
                background #1168bd
                color #ffffff
            }
            element "External" {
                background #999999
                color #ffffff
            }
            element "Untrusted" {
                background #a63a24
                color #ffffff
            }
            element "Trusted" {
                background #237a3b
                color #ffffff
            }
            element "Data" {
                shape Cylinder
            }
        }
    }
}
