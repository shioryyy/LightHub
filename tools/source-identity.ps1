function Get-LightHubSourceIdentity([string]$Root) {
    $revisionFile = Join-Path $Root 'SOURCE-REVISION.json'
    if ((Test-Path -LiteralPath (Join-Path $Root '.git')) -and (Get-Command git -ErrorAction SilentlyContinue)) {
        $commit = & git -C $Root rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot identify the source commit' }
        $changes = @(& git -C $Root status --porcelain --untracked-files=all)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source status' }
        return [ordered]@{ schemaVersion=1; sourceCommit=$commit.Trim(); sourceDirty=($changes.Count -gt 0); sourceKind='git' }
    }
    if (Test-Path -LiteralPath $revisionFile) {
        if ((Get-Item -LiteralPath $revisionFile).Length -gt 16384) { throw 'Source revision metadata is oversized' }
        $declared = Get-Content -LiteralPath $revisionFile -Raw | ConvertFrom-Json
        if ($declared.schemaVersion -ne 1 -or $declared.sourceCommit -notmatch '^[0-9a-f]{40}$' -or ($null -ne $declared.sourceDirty -and $declared.sourceDirty -isnot [bool])) { throw 'Unsupported source revision metadata' }
        # Without Git, local changes after extraction cannot be ruled out. Keep
        # the commit declaration but never claim a verified clean worktree.
        return [ordered]@{ schemaVersion=1; sourceCommit=$declared.sourceCommit; sourceDirty=$null; sourceKind='archive-declared' }
    }
    return [ordered]@{ schemaVersion=1; sourceCommit=$null; sourceDirty=$null; sourceKind='unknown' }
}
