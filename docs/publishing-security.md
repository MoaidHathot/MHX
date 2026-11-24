# Publishing trust boundary

## Authorized publication

Only stable `vMAJOR.MINOR.PATCH` tags in the canonical MHX repository can publish.
The workflow checks the repository name and immutable repository/owner IDs, permits
only tag `push` or tag `workflow_dispatch`, checks the project version, and verifies
that the tag's commit is already reachable from `origin/main`.

Build/test jobs use read-only repository permissions and do not persist checkout
credentials. Publication consumes artifacts from the same verified workflow run;
PR artifacts and external run IDs are not publication inputs.

The GitHub release job uses the `release` environment. The NuGet job uses
`nuget-publish`, and it is the only job allowed to request an OIDC token. Both
environments accept **tags matching `v*` only**, with no branch deployment rules.
Repository rules restrict updates to `main` and creation/update/deletion of release
tags to the repository owner. New releases are immutable after publication.

The NuGet trusted-publisher policy must match:

| Field | Required value |
|---|---|
| Repository owner | `MoaidHathot` |
| Repository | `MHX` |
| Workflow file | `release.yml` |
| Environment | **`nuget-publish`** |
| Package pattern | `MHX` |

Environment binding is important: workflow-only guards can be changed in a proposed
workflow. GitHub's environment rules are repository settings that a pull request
cannot alter. A fork has a different repository identity; a PR or branch cannot
deploy to the tag-only environment. The policy therefore cannot be satisfied simply
by adding a publishing step to a fork or PR workflow.

Before requesting a NuGet credential, the publisher verifies the release manifest,
package digest, size, package ID, and version. No downloaded package code is executed
in the privileged publishing job. The OIDC-derived key is short-lived and masked;
there is no long-lived NuGet key in the repository.

## Maintainer operations

The owner remains a trusted administrator and can approve/merge changes, create
release tags, or change the security settings. This protects against untrusted
contributors, not compromise of the owner's GitHub/NuGet accounts. Merged changes
can intentionally ship in a later owner-authorized release.

`scripts/Verify-ReleaseSecurity.ps1` tests accepted and rejected contexts, including
fork identities, PR events, branch dispatches, malformed tags, and version mismatch.
The manual `verify-publish-boundary` workflow is a negative control: when dispatched
on `main`, GitHub must reject the tag-only environment before starting its job. It
requests neither publishing nor OIDC permissions. An environment rejection is the
expected result for that workflow.

## Privacy checks

The initial audit scanned all reachable Git history with Gitleaks and reviewed
contact-address, personal-home-path, and internal-domain patterns. No credentials
or private contact addresses were identified. Git commit addresses use GitHub
noreply identities. Public copyright/author attribution and the repository identity
remain intentional project metadata.

The current examples use generic package names. Historical revisions retain older
public package examples; no Git history was rewritten. Binary build outputs and
local audit reports are excluded from Git. CI repeats the history credential scan
and tracked-file privacy checks. These are detection controls, not a guarantee
against every possible form of sensitive information.
