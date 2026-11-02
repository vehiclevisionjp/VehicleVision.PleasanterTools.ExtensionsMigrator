<#
.SYNOPSIS
    GitHub の共通設定・ファイル・Core の依存版を GET だけで監査する。
.DESCRIPTION
    gh の認証を利用する。管理者でないと見えない項目は確認不可として区別する。
    標準出力は Markdown。JSON の既定の保存先は temp/repo-audit.json。
    -JsonPath と -MarkdownPath で保存先を指定できる（結果以外は書き換えない）。
    一致なら 0、差分・欠落・確認不可・取得エラーは 1、基準や設定の不正は 2。
#>
param(
    [string]$ConfigPath = '',
    [string]$JsonPath = '',
    [string]$MarkdownPath = ''
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'RepoAudit.psm1') -Force -DisableNameChecking
$root = Split-Path $PSScriptRoot -Parent
$results = [Collections.Generic.List[object]]::new()

function Add-AuditResult {
    param([string]$Repository, [string]$Category, [string]$Item, [string]$Status, [string[]]$Details = @())
    $results.Add([ordered]@{
        repository = $Repository; category = $Category; item = $Item; status = $Status; details = @($Details)
    })
}

function Add-AuditUnavailable {
    param([string]$Repository, [string]$Category, [string]$Item, [int]$HttpStatus)
    $state = Get-AuditHttpState $HttpStatus
    $detail = if ($state -eq 'unknown') { '権限が足りず確認できない（404 は権限による非表示も含む）' }
        else { '取得エラー（通信・認証・レート制限・API 応答を確認）' }
    Add-AuditResult $Repository $Category $Item $state @("$detail / HTTP $HttpStatus")
}

try {
    if (-not $JsonPath) {
        $directory = Join-Path $root 'temp'
        if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
        $JsonPath = Join-Path $directory 'repo-audit.json'
    }
    if (-not $ConfigPath) { $ConfigPath = Join-Path $root '.github\audited-repos.json' }
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
    $settings = Get-Content -LiteralPath (Join-Path $root '.github\repo-settings.json') -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable
    $labels = @(Get-Content -LiteralPath (Join-Path $root '.github\labels.json') -Raw -Encoding utf8 |
        ConvertFrom-Json -AsHashtable)
    $definitions = @(Get-ChildItem -LiteralPath (Join-Path $root '.github\rulesets') -Filter '*.json' |
        Sort-Object Name | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -AsHashtable })
    if ($definitions.Count -ne 3 -or $config.repositories.Count -eq 0) { throw 'Invalid baseline' }
    $names = @($config.repositories.name)
    if (@($names | Sort-Object -Unique).Count -ne $names.Count) { throw 'Duplicate repository' }
    foreach ($policy in $config.repositories) {
        foreach ($key in 'name', 'visibility', 'includeCodeQL', 'extraChecks', 'notApplicableFiles') {
            if (-not $policy.ContainsKey($key)) { throw 'Missing repository policy' }
        }
        if ($policy.name -notmatch '^[A-Za-z0-9_.-]+$' -or
            $policy.visibility -notin 'public', 'private', 'internal' -or
            $policy.includeCodeQL -isnot [bool] -or
            $policy.extraChecks -isnot [array] -or $policy.notApplicableFiles -isnot [hashtable] -or
            $config.excludedRepositories.ContainsKey($policy.name)) { throw 'Invalid repository policy' }
        $null = Get-AuditClaPolicy $policy
        foreach ($definition in $definitions) {
            $null = Get-AuditExpectedRuleset $definition $policy
        }
        foreach ($key in $policy.notApplicableFiles.Keys) {
            if (($key -notin $config.commonFiles -and $key -notin $config.licenseFiles) -or
                -not $policy.notApplicableFiles[$key]) { throw 'Invalid exclusion' }
        }
        if ($policy.ContainsKey('licenseTier') -and $policy.licenseTier -notin 'dual', 'triple') { throw 'Invalid license tier' }
    }
    if ($config.owner -notmatch '^[A-Za-z0-9-]+$') { throw 'Invalid owner' }
    $baselineFiles = @{}
    foreach ($path in $config.commonFiles) {
        if ([IO.Path]::IsPathRooted($path) -or $path -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Invalid file path' }
        $baselineFiles[$path] = Get-Content -LiteralPath (Join-Path $root $path) -Raw -Encoding utf8
    }
    foreach ($path in $config.licenseFiles) {
        if ([IO.Path]::IsPathRooted($path) -or $path -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Invalid file path' }
    }
    $licenseBaselines = Get-AuditLicenseBaselines $root @($config.licenseFiles)
    $commit = git --no-pager -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Invalid baseline commit' }
} catch {
    [Console]::Error.WriteLine('基準ファイルまたは監査設定が不正です。JSON・パス・対象外理由を確認してください。')
    exit 2
}

try {
    $cache = @{}
    $packages = @('Data', 'Mail', 'Web' | ForEach-Object { "VehicleVision.PleasanterTools.Core.$_" })
    $latest = Get-AuditNuGetVersions $packages
    $snapshots = [Collections.Generic.List[object]]::new()
    foreach ($policy in $config.repositories) {
        $repo = "$($config.owner)/$($policy.name)"
        $public = $policy.visibility -eq 'public'
        $metadata = Invoke-AuditGet "repos/$repo" -PublicFallback:$public
        if ($metadata.status -ne 200) {
            foreach ($category in '設定', 'ルールセット', 'ラベル', 'Actions', 'develop 共通', 'master 共通', 'ライセンス', '依存') {
                Add-AuditUnavailable $repo $category 'リポジトリを取得できない' $metadata.status
            }
            continue
        }
        foreach ($key in @($settings.repository.Keys | Sort-Object)) {
            if (-not $metadata.data.ContainsKey($key)) {
                Add-AuditUnavailable $repo '設定' $key 403
            } else {
                $status = if ($metadata.data[$key] -ceq $settings.repository[$key]) { 'ok' } else { 'different' }
                Add-AuditResult $repo '設定' $key $status @("期待=$($settings.repository[$key]) / 実際=$($metadata.data[$key])")
            }
        }
        $visibilityStatus = if ($metadata.data.visibility -ceq $policy.visibility) { 'ok' } else { 'different' }
        Add-AuditResult $repo '設定' 'visibility' $visibilityStatus @("期待=$($policy.visibility) / 実際=$($metadata.data.visibility)")
        foreach ($key in @($settings.public_security.Keys | Sort-Object)) {
            if ($metadata.data.visibility -ne 'public') {
                Add-AuditResult $repo '設定' $key 'notApplicable' @('Sync-RepoSettings と同じく public だけが対象')
                continue
            }
            if ($key -eq 'automated_security_fixes') {
                $response = Invoke-AuditGet "repos/$repo/automated-security-fixes"
                if ($response.status -ne 200 -or -not $response.data.ContainsKey('enabled')) {
                    Add-AuditUnavailable $repo '設定' $key $(if ($response.status -eq 200) { 0 } else { $response.status })
                } else {
                    $status = if ($response.data.enabled -ceq $settings.public_security[$key]) { 'ok' } else { 'different' }
                    Add-AuditResult $repo '設定' $key $status @("期待=$($settings.public_security[$key]) / 実際=$($response.data.enabled)")
                }
            } else {
                $actual = if ($metadata.data.security_and_analysis) { $metadata.data.security_and_analysis[$key].status } else { $null }
                if ($null -eq $actual) { Add-AuditUnavailable $repo '設定' $key 403 }
                else {
                    $status = if ($actual -ceq $settings.public_security[$key]) { 'ok' } else { 'different' }
                    Add-AuditResult $repo '設定' $key $status @("期待=$($settings.public_security[$key]) / 実際=$actual")
                }
            }
        }
        $alerts = Invoke-AuditGet "repos/$repo/vulnerability-alerts"
        if ($alerts.status -eq 200) { Add-AuditResult $repo '設定' 'Dependabot alerts / dependency graph' 'ok' }
        elseif ($alerts.status -eq 404 -and $metadata.data.permissions.admin) {
            Add-AuditResult $repo '設定' 'Dependabot alerts / dependency graph' 'different' @('管理者からの GET が 404: 無効')
        } else { Add-AuditUnavailable $repo '設定' 'Dependabot alerts / dependency graph' $alerts.status }

        $claRequired = Get-AuditClaPolicy $policy
        $branches = Get-AuditPages "repos/$repo/branches" -PublicFallback:$public
        $branchTrees = @{}
        $claReadiness = @{}
        foreach ($branch in 'develop', 'master') {
            if (-not $claRequired) {
                Add-AuditResult $repo "$branch 共通" 'CLA' 'notApplicable' @('公開範囲の既定または明示した例外により対象外')
            }
            if ($branches.status -ne 200) {
                if ($claRequired) { Add-AuditUnavailable $repo "$branch 共通" 'CLA ブランチ一覧' $branches.status }
                continue
            }
            $head = @($branches.data | Where-Object name -ceq $branch)
            if (-not $head.Count) { continue }
            $sha = $head[0].commit.sha
            $tree = Invoke-AuditGet "repos/$repo/git/trees/${sha}?recursive=1" -PublicFallback:$public
            $branchTrees[$branch] = $tree
            if (-not $claRequired) { continue }
            if ($tree.status -ne 200 -or $tree.data.truncated) {
                Add-AuditUnavailable $repo "$branch 共通" 'CLA ツリー' $(if ($tree.status -eq 200) { 0 } else { $tree.status })
                continue
            }
            $entries = @{}
            foreach ($entry in $tree.data.tree) { $entries[$entry.path] = $entry }
            $claReadiness[$branch] = Get-AuditClaReadiness $entries
            foreach ($path in Get-AuditClaPaths) {
                $fileReady = $entries.ContainsKey($path) -and $entries[$path].type -eq 'blob' -and
                    $entries[$path].mode -in '100644', '100755'
                Add-AuditResult $repo "$branch 共通" $path $(if ($fileReady) { 'ok' } else { 'missing' }) @('CLA の前提ファイル（内容・署名者は比較しない）')
            }
        }
        $rulesets = Get-AuditPages "repos/$repo/rulesets" -PublicFallback:$public
        if ($rulesets.status -ne 200) { Add-AuditUnavailable $repo 'ルールセット' '一覧' $rulesets.status }
        else {
            foreach ($definition in $definitions) {
                $claReady = $false
                if ($definition.target -eq 'branch') {
                    $branch = $definition.conditions.ref_name.include[0] -replace '^refs/heads/', ''
                    if ($branches.status -eq 200 -and $branch -eq 'master' -and
                        -not @($branches.data | Where-Object name -ceq $branch).Count) {
                        Add-AuditResult $repo 'ルールセット' $definition.name 'notApplicable' @('master が無いため同期対象外')
                        continue
                    }
                    $claReady = $claReadiness[$branch] -eq $true
                    if ($claRequired -and -not $claReady) {
                        $state = if ($claReadiness.ContainsKey($branch)) { 'notApplicable' } else { 'unknown' }
                        Add-AuditResult $repo 'ルールセット' "$branch cla 必須化" $state @('前提ファイルが揃うまで必須化を保留。欠落・確認不可は別項目で報告')
                    }
                }
                $matching = @($rulesets.data | Where-Object { $_.name -ceq $definition.name -and $_.source_type -eq 'Repository' })
                if ($matching.Count -eq 0) {
                    Add-AuditResult $repo 'ルールセット' $definition.name 'missing'
                    continue
                }
                if ($matching.Count -gt 1) {
                    Add-AuditResult $repo 'ルールセット' $definition.name 'different' @('同名のルールセットが複数存在')
                }
                foreach ($match in $matching) {
                    $response = Invoke-AuditGet "repos/$repo/rulesets/$($match.id)" -PublicFallback:$public
                    if ($response.status -ne 200) { Add-AuditUnavailable $repo 'ルールセット' $definition.name $response.status; continue }
                    if (-not $response.data.ContainsKey('bypass_actors')) {
                        Add-AuditUnavailable $repo 'ルールセット' "$($definition.name) bypass" 403
                        $response.data.bypass_actors = $definition.bypass_actors
                    }
                    $expected = Get-AuditExpectedRuleset $definition $policy -ClaReady $claReady
                    $diff = @(Compare-AuditRuleset $expected $response.data)
                    if ($definition.target -eq 'branch' -and $claRequired -and $claReady) {
                        $claChecks = @($response.data.rules | Where-Object type -eq 'required_status_checks' |
                            ForEach-Object { $_.parameters.required_status_checks } | Where-Object context -ceq 'cla')
                        $expectedCla = @($expected.rules | Where-Object type -eq 'required_status_checks' |
                            ForEach-Object { $_.parameters.required_status_checks } | Where-Object context -ceq 'cla')
                        $claStatus = if ((ConvertTo-AuditCanonicalJson $claChecks) -ceq
                            (ConvertTo-AuditCanonicalJson $expectedCla)) { 'ok' } else { 'different' }
                        Add-AuditResult $repo 'ルールセット' "$branch cla 必須" $claStatus @("cla の件数=$($claChecks.Count) / 発行 App も基準 JSON と比較")
                    }
                    $status = if ($diff.Count) { 'different' } else { 'ok' }
                    Add-AuditResult $repo 'ルールセット' $definition.name $status $diff
                }
            }
            foreach ($extra in @($rulesets.data | Where-Object {
                $_.name -cnotin $definitions.name -or $_.source_type -ne 'Repository'
            })) {
                Add-AuditResult $repo 'ルールセット' $extra.name 'different' @("基準に無い追加ルールセット / source=$($extra.source)")
            }
        }
        $actualLabels = Get-AuditPages "repos/$repo/labels" -PublicFallback:$public
        if ($actualLabels.status -ne 200) { Add-AuditUnavailable $repo 'ラベル' '共通ラベル' $actualLabels.status }
        else {
            $diff = @(Compare-AuditLabels $labels $actualLabels.data $config.allowedExtraLabels)
            Add-AuditResult $repo 'ラベル' '共通ラベル' $(if ($diff.Count) { 'different' } else { 'ok' }) $diff
        }
        foreach ($kind in 'variables', 'secrets') {
            $name = if ($kind -eq 'variables') { 'MY_REPO_AUTOMATION_BOT_CLIENT_ID' } else { 'MY_REPO_AUTOMATION_BOT_PRIVATE_KEY' }
            $response = Get-AuditPages "repos/$repo/actions/$kind" $kind -NamesOnly
            $organization = $null
            if ($response.status -ne 200 -or $name -cnotin $response.data) {
                $organization = Get-AuditPages "repos/$repo/actions/organization-$kind" $kind -NamesOnly
            }
            $presence = Get-AuditActionPresence $name $response $organization
            Add-AuditResult $repo 'Actions' $name $presence.status $presence.details
        }
        if ($branches.status -ne 200) {
            foreach ($category in 'develop 共通', 'master 共通', 'ライセンス', '依存') { Add-AuditUnavailable $repo $category 'ブランチ一覧' $branches.status }
            continue
        }
        foreach ($branch in 'develop', 'master') {
            $head = @($branches.data | Where-Object name -eq $branch)
            if (-not $head.Count) {
                Add-AuditResult $repo "$branch 共通" $branch $(if ($branch -eq 'master') { 'notApplicable' } else { 'missing' })
                if ($branch -eq 'develop') {
                    Add-AuditResult $repo 'ライセンス' $branch 'missing'
                    Add-AuditResult $repo '依存' $branch 'missing'
                }
                continue
            }
            $sha = $head[0].commit.sha
            $snapshots.Add(@{ repository = $repo; branch = $branch; commit = $sha })
            $tree = $branchTrees[$branch]
            if ($tree.status -ne 200 -or $tree.data.truncated) {
                $code = if ($tree.status -eq 200) { 0 } else { $tree.status }
                Add-AuditUnavailable $repo "$branch 共通" 'ツリー（打ち切りもエラー）' $code
                if ($branch -eq 'develop') { Add-AuditUnavailable $repo 'ライセンス' "$branch ツリー" $code }
                Add-AuditUnavailable $repo '依存' "$branch ツリー" $code
                continue
            }
            $entries = @{}
            foreach ($entry in $tree.data.tree) { $entries[$entry.path] = $entry }
            $replacements = @{
                "$($policy.name)" = $config.template
            }
            if ($policy.replacements) {
                foreach ($key in $policy.replacements.Keys) { $replacements[$key] = $policy.replacements[$key] }
            }
            foreach ($path in $config.commonFiles) {
                if ($policy.notApplicableFiles.ContainsKey($path)) {
                    Add-AuditResult $repo "$branch 共通" $path 'notApplicable' @($policy.notApplicableFiles[$path])
                    continue
                }
                if (-not $entries.ContainsKey($path)) { Add-AuditResult $repo "$branch 共通" $path 'missing'; continue }
                $blob = Get-AuditBlob $repo $entries[$path].sha $cache -PublicFallback:$public
                if ($blob.status -ne 200) { Add-AuditUnavailable $repo "$branch 共通" $path $blob.status; continue }
                try { $comparison = Compare-AuditFile $path $baselineFiles[$path] $blob.data $replacements }
                catch { Add-AuditResult $repo "$branch 共通" $path 'error' @('比較対象の XML または節を解析できない'); continue }
                Add-AuditResult $repo "$branch 共通" $path $comparison.status $comparison.details
            }
            if ($branch -eq 'develop') {
                # ライセンス文書は、正本の適用が済んだ develop だけを見る（master へは次のリリースで入る）。
                $tier = if ($policy.licenseTier) { $policy.licenseTier } else { 'dual' }
                $licenseBase = $licenseBaselines[$tier]
                $licenseActual = @{}
                foreach ($path in $config.licenseFiles) {
                    if ($policy.notApplicableFiles.ContainsKey($path)) {
                        Add-AuditResult $repo 'ライセンス' $path 'notApplicable' @($policy.notApplicableFiles[$path])
                        continue
                    }
                    if (-not $entries.ContainsKey($path)) { Add-AuditResult $repo 'ライセンス' $path 'missing'; continue }
                    $blob = Get-AuditBlob $repo $entries[$path].sha $cache -PublicFallback:$public
                    if ($blob.status -ne 200) { Add-AuditUnavailable $repo 'ライセンス' $path $blob.status; continue }
                    try {
                        $comparison = Compare-AuditLicenseFile $path $licenseBase[$path] $blob.data $replacements `
                            $licenseBaselines.parts[$tier] $tier
                    } catch {
                        Add-AuditResult $repo 'ライセンス' $path 'error' @('比較対象の節を解析できない')
                        continue
                    }
                    $licenseActual[$path] = $comparison.sections
                    Add-AuditResult $repo 'ライセンス' $path $comparison.status (@("提供構成=$tier") + @($comparison.details))
                }
                if ($licenseActual.ContainsKey('LICENSING.md') -and $licenseActual.ContainsKey('LICENSE-COMMERCIAL.md')) {
                    $messages = @(Test-AuditLicenseConsistency $licenseActual['LICENSING.md'] $licenseActual['LICENSE-COMMERCIAL.md'])
                    Add-AuditResult $repo 'ライセンス' 'LICENSING.md と LICENSE-COMMERCIAL.md の節の整合' `
                        $(if ($messages.Count) { 'different' } else { 'ok' }) $messages
                }
            }
            $projectFiles = @{}
            $failed = $false
            foreach ($path in @($entries.Keys | Where-Object {
                $_ -match '(^|/)([^/]+\.csproj|Directory\.(Build|Packages)\.props)$' -and
                $_ -notmatch '(^|/)(_reference|node_modules|bin|obj)/'
            } | Sort-Object)) {
                $blob = Get-AuditBlob $repo $entries[$path].sha $cache -PublicFallback:$public
                if ($blob.status -ne 200) { Add-AuditUnavailable $repo '依存' "$branch/$path" $blob.status; $failed = $true }
                else { $projectFiles[$path] = $blob.data }
            }
            try { $references = @(Get-AuditCoreReferences $projectFiles) }
            catch { Add-AuditResult $repo '依存' $branch 'error' @('プロジェクト XML を解析できない'); continue }
            if (-not $references.Count -and -not $failed) { Add-AuditResult $repo '依存' $branch 'notApplicable' @('Core.Data / Mail / Web の参照無し') }
            foreach ($reference in $references) {
                $item = "$branch/$($reference.path): $($reference.package)"
                if (-not $reference.resolved) {
                    Add-AuditResult $repo '依存' $item 'unknown' @('固定版を解決できない（条件付き定義・import・版範囲は手動確認）')
                } elseif ($latest[$reference.package].status -ne 'ok') {
                    Add-AuditResult $repo '依存' $item 'error' @('nuget.org の最新版を取得できない')
                } else {
                    $expected = $latest[$reference.package].latest
                    $status = if ([semver]$reference.version.ToLowerInvariant() -eq [semver]$expected) { 'ok' } else { 'different' }
                    Add-AuditResult $repo '依存' $item $status @("参照=$($reference.version) / nuget.org 最新（preview 含む）=$expected")
                }
            }
        }
    }
    $report = @{
        schemaVersion = 1; generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        baselineCommit = $commit; baseline = 'ローカル作業ツリーの JSON と共通ファイル（未コミット変更も含む）'
        repositories = @($config.repositories | ForEach-Object { "$($config.owner)/$($_.name)" })
        excludedRepositories = $config.excludedRepositories; snapshots = $snapshots.ToArray()
        nuget = $latest; results = $results.ToArray()
    }
    $markdown = ConvertTo-AuditMarkdown $report
    if ($JsonPath) { $report | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $JsonPath -Encoding utf8 }
    if ($MarkdownPath) { Set-Content -LiteralPath $MarkdownPath -Value $markdown -Encoding utf8 }
    Write-Output $markdown
    exit (Get-AuditExitCode $results.ToArray())
} catch {
    [Console]::Error.WriteLine('監査を完了できませんでした。gh の認証・ネットワーク・出力先を確認してください（応答本文は非表示）。')
    [Console]::Error.WriteLine("診断位置: $([IO.Path]::GetFileName($_.InvocationInfo.ScriptName)):$($_.InvocationInfo.ScriptLineNumber) / $($_.Exception.GetType().Name)")
    exit 2
}
