# Releasing

Packages are published to nuget.org by [`.github/workflows/release.yml`](../.github/workflows/release.yml) using
**NuGet Trusted Publishing**. GitHub Actions exchanges a short-lived OIDC token for a one-hour API key, so no
long-lived key is stored anywhere.

## One-time setup

1. **nuget.org:** sign in, click your user name, choose **Trusted Publishing**, and add a policy:
   - **Repository Owner:** `Murat7Ay`
   - **Repository:** `Writeback`
   - **Workflow File:** `release.yml` (the file name only)
   - **Environment:** `nuget`
   - **Scopes:** allow pushing new packages and new versions for `Writeback*`.
2. **GitHub:** add a repository secret `NUGET_USER` containing your nuget.org **profile name** (not your email):
   ```bash
   gh secret set NUGET_USER --repo Murat7Ay/Writeback
   ```
3. **Optional:** under *Settings → Environments → nuget*, add yourself as a required reviewer, so every publish
   waits for your approval.

## Publishing a version

1. Set `<Version>` in `Directory.Build.props` (e.g. `0.2.0`) and merge to `master`.
2. Tag the merged commit and push the tag:
   ```bash
   git tag v0.2.0
   ```
   ```bash
   git push origin v0.2.0
   ```
3. The workflow checks that the tag matches `<Version>`, builds, runs the unit tests, packs, and pushes
   `Writeback`, `Writeback.SqlServer` and `Writeback.PostgreSql` (with symbol packages). Re-running a tag is
   safe (`--skip-duplicate`).

The policy may start as *temporarily active* for 7 days and becomes permanent after the first successful publish.
The temporary API key is valid for one hour and is requested right before the push.
