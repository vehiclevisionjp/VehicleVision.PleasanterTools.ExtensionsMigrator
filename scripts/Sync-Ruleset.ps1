<#
.SYNOPSIS
    .github/rulesets/*.json を GitHub リポジトリのルールセットへ作成・更新する。
.DESCRIPTION
    gh CLI でログイン済みで、反映にはリポジトリの管理者権限があること。
    develop・master・v* タグを保護する。管理者は迂回できる。
    必須チェックは追加分も含め GitHub Actions（App ID 15368）に固定する。
    public は CLA を必須にするが、base にワークフローと署名一覧があるブランチだけが対象。
    存在しない master のルールセットは作らない。取得エラーは反映前に停止する。
    -WhatIf は GET と反映予定の JSON 出力だけを行い、旧ルールセットも削除しない。
.PARAMETER Repository
    owner/name。省略時は現在のリポジトリ。
.PARAMETER IncludeCodeQL
    develop と master の必須チェックへ Analyze (csharp) を加える。
.PARAMETER ExtraChecks
    リポジトリ固有の CI ジョブ名。cla は基準 JSON が持つので指定不要。
.PARAMETER OmitChecks
    基準 JSON の必須チェックから外すジョブ名。cla の指定は無視する。基準にない名前はエラー。
.PARAMETER RequireCla
    公開範囲による既定の例外。例: -RequireCla:$false。監査設定の例外と理由も揃える。
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Repository = '',
    [switch]$IncludeCodeQL,
    [string[]]$ExtraChecks = @(),
    [Nullable[bool]]$RequireCla = $null,
    [string[]]$OmitChecks = @()
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'RepoAudit.psm1') -Force -DisableNameChecking
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $Repository) {
    $Repository = gh repo view --json nameWithOwner --jq .nameWithOwner
    if ($LASTEXITCODE -ne 0) { throw 'リポジトリを特定できません。-Repository を指定してください。' }
}
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Repository は owner/name で指定してください。' }
$metadata = Invoke-AuditGet "repos/$Repository" '{visibility: .visibility}'
if ($metadata.status -ne 200) { throw "公開範囲を取得できません。HTTP $($metadata.status)" }
# pwsh ファイル.ps1 -ExtraChecks 'a','b' の形では 'a,b' の 1 要素になるため、カンマで分割する。
$ExtraChecks = @($ExtraChecks | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$OmitChecks = @($OmitChecks | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$policy = @{
    visibility = $metadata.data.visibility; includeCodeQL = [bool]$IncludeCodeQL
    extraChecks = $ExtraChecks; omitChecks = $OmitChecks
}
if ($null -ne $RequireCla) {
    $policy.requireCla = [bool]$RequireCla
    $policy.claReason = '同期時に明示した例外（監査設定にも理由を記録すること）'
}
$claRequired = Get-AuditClaPolicy $policy
if ('cla' -cin $ExtraChecks) { Write-Warning 'cla は基準 JSON から判定します。-ExtraChecks の cla は無視します。' }
if ('cla' -cin $OmitChecks) { Write-Warning 'cla は基準 JSON から判定します。-OmitChecks の cla は無視します。' }
$branches = Get-AuditPages "repos/$Repository/branches"
if ($branches.status -ne 200) { throw "ブランチ一覧を取得できません。HTTP $($branches.status)" }
$existing = Get-AuditPages "repos/$Repository/rulesets"
if ($existing.status -ne 200) { throw "ルールセットを取得できません。HTTP $($existing.status)" }

# 全ブランチの読み取りが成功してから反映する。監査と同じくツリーをコミットに固定する。
$plans = @()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repoRoot '.github\rulesets') -Filter '*.json' | Sort-Object Name) {
    $definition = Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
    $claReady = $false
    if ($definition.target -eq 'branch') {
        $branch = $definition.conditions.ref_name.include[0] -replace '^refs/heads/', ''
        $head = @($branches.data | Where-Object name -ceq $branch)
        if (-not $head.Count) {
            if ($branch -eq 'master') {
                Write-Warning "$Repository に master が無いため、master-protection は作成・更新しません。"
                continue
            }
            throw "$Repository に $branch がありません。"
        }
        if ($claRequired) {
            $response = Invoke-AuditGet "repos/$Repository/git/trees/$($head[0].commit.sha)?recursive=1"
            if ($response.status -ne 200 -or $response.data.truncated) {
                throw "$Repository / $branch の CLA ファイルを確認できません（ツリーの打ち切りもエラー）。HTTP $($response.status)"
            }
            $entries = @{}
            foreach ($entry in $response.data.tree) { $entries[$entry.path] = $entry }
            $claReady = Get-AuditClaReadiness $entries
            foreach ($path in Get-AuditClaPaths) {
                if (-not $entries.ContainsKey($path) -or $entries[$path].type -ne 'blob' -or
                    $entries[$path].mode -notin '100644', '100755') {
                    Write-Warning "$Repository / $branch に $path がありません。cla の必須化を保留します。"
                }
            }
        }
    }
    $ruleset = Get-AuditExpectedRuleset $definition $policy -ClaReady $claReady
    $matches = @($existing.data | Where-Object { $_.name -ceq $ruleset.name -and $_.source_type -eq 'Repository' })
    if ($matches.Count -gt 1) { throw "同名ルールセットが複数あります: $($ruleset.name)" }
    $plans += @{ ruleset = $ruleset; id = if ($matches.Count) { $matches[0].id } else { $null } }
}
foreach ($plan in $plans) {
    $body = $plan.ruleset | ConvertTo-Json -Depth 50
    if ($WhatIfPreference) { Write-Output $body }
    if ($PSCmdlet.ShouldProcess("$Repository / $($plan.ruleset.name)", 'ルールセットの作成・更新')) {
        if ($plan.id) {
            $body | gh api --method PUT "repos/$Repository/rulesets/$($plan.id)" --input - | Out-Null
        } else {
            $body | gh api --method POST "repos/$Repository/rulesets" --input - | Out-Null
        }
        if ($LASTEXITCODE -ne 0) { throw "ルールセットを反映できません: $($plan.ruleset.name)" }
        Write-Output "ルールセットを反映しました: $Repository / $($plan.ruleset.name)"
    }
}

# 新しい形の反映後にだけ旧い形を消す。-WhatIf では削除しない。
foreach ($legacy in @($existing.data | Where-Object { $_.name -ceq '保護ブランチ' -and $_.source_type -eq 'Repository' })) {
    if ($PSCmdlet.ShouldProcess("$Repository / $($legacy.id)", '旧いルールセット（保護ブランチ）の削除')) {
        gh api --method DELETE "repos/$Repository/rulesets/$($legacy.id)" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "旧いルールセットを消せません: $($legacy.id)" }
    }
}
