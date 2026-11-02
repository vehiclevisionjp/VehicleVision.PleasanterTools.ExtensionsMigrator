<#
.SYNOPSIS
    ネットワーク不要の固定入力で、監査の比較・取得・出力を検証する。
#>
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'RepoAudit.psm1') -Force -DisableNameChecking
$root = Split-Path $PSScriptRoot -Parent
$script:count = 0
function Assert-Audit {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "失敗: $Message" }
    $script:count++
}

$definition = Get-Content (Join-Path $root '.github\rulesets\master-protection.json') -Raw |
    ConvertFrom-Json -AsHashtable
$policy = @{ visibility = 'public'; includeCodeQL = $true; extraChecks = @('integration', 'build-test', 'cla') }
$expected = Get-AuditExpectedRuleset $definition $policy
$checks = @($expected.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks
Assert-Audit ($checks.Count -eq 8) 'CodeQL・追加チェック・CLA を加え、重複しない'
Assert-Audit (@($definition.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks.Count -eq 6) '元の基準を変更しない'
$omitPolicy = @{
    visibility = 'public'; includeCodeQL = $true
    extraChecks = @('integration', 'instructions', 'cla'); omitChecks = @('instructions', 'npm-audit', 'cla')
}
foreach ($branch in 'develop', 'master') {
    $baseline = Get-Content (Join-Path $root ".github\rulesets\$branch-protection.json") -Raw |
        ConvertFrom-Json -AsHashtable
    $omitted = Get-AuditExpectedRuleset $baseline $omitPolicy
    $remaining = @($omitted.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks
    Assert-Audit ($remaining.Count -eq 6 -and 'instructions' -cnotin $remaining.context -and
        'npm-audit' -cnotin $remaining.context) "$branch の指定チェックを除外し、ExtraChecks でも戻さない"
    foreach ($name in 'build-test', 'nuget-audit', 'wip-title', 'cla', 'Analyze (csharp)', 'integration') {
        Assert-Audit (@($remaining | Where-Object { $_.context -ceq $name -and $_.integration_id -eq 15368 }).Count -eq 1) "$branch の $name は Actions 固定で残る"
    }
    Assert-Audit (@($baseline.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks.Count -eq 6) "$branch の除外で基準 JSON は変わらない"
}
foreach ($invalid in @(@('instruction'), @('Instructions'), @('integration'), @('Analyze (csharp)'), @(''), @(1), 'instructions', $null)) {
    $thrown = $false
    try {
        $null = Get-AuditExpectedRuleset $definition @{visibility='public';omitChecks=$invalid}
    } catch { $thrown = $true }
    Assert-Audit $thrown '誤字・基準外・不正な型の omitChecks は拒否'
}
foreach ($visibility in 'public', 'internal') {
    foreach ($ready in $true, $false) {
        $candidate = Get-AuditExpectedRuleset $definition @{visibility=$visibility;omitChecks=@('cla')} -ClaReady $ready
        $cla = @($candidate.rules | Where-Object type -eq required_status_checks |
            ForEach-Object { $_.parameters.required_status_checks } | Where-Object context -ceq 'cla')
        Assert-Audit ($cla.Count -eq [int]($visibility -eq 'public' -and $ready)) 'omitChecks の cla は公開範囲・前提ファイルの判定を変えない'
    }
}
$config = Get-Content (Join-Path $root '.github\audited-repos.json') -Raw | ConvertFrom-Json -AsHashtable
Assert-Audit ('scripts/sync-reference.sh' -cin $config.commonFiles) 'オンデマンド取得スクリプトを共通管理する'
Assert-Audit (@($config.commonFiles | Where-Object { $_ -match 'reference-submodule' }).Count -eq 0) '廃止した参照更新を共通管理しない'
foreach ($path in $config.commonFiles) {
    Assert-Audit (Test-Path -LiteralPath (Join-Path $root $path) -PathType Leaf) "共通ファイルが存在する: $path"
}
foreach ($repository in $config.repositories) {
    foreach ($path in $repository.notApplicableFiles.Keys) {
        Assert-Audit ($path -cin $config.commonFiles -and
            -not [string]::IsNullOrWhiteSpace($repository.notApplicableFiles[$path])) '対象外は現行の共通ファイルと理由を指定する'
    }
    $names = @(if ($repository.name -eq 'VehicleVision.PleasanterTools.Core') { @('instructions', 'npm-audit') }
        elseif ($repository.name -match '\.(AtomFeed|CalendarFeed|IdentityProvider|Questionnaire)$') { @('instructions') }
        else { @() })
    if ($names.Count) {
        Assert-Audit ((ConvertTo-AuditCanonicalJson @($repository.omitChecks)) -ceq
            (ConvertTo-AuditCanonicalJson $names)) "$($repository.name) の除外設定"
    } else {
        Assert-Audit (-not $repository.ContainsKey('omitChecks')) "$($repository.name) に不要な omitChecks を足さない"
    }
}
foreach ($branch in 'develop', 'master') {
    $baseline = Get-Content (Join-Path $root ".github\rulesets\$branch-protection.json") -Raw |
        ConvertFrom-Json -AsHashtable
    $baselineChecks = @($baseline.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks
    Assert-Audit ($baselineChecks.Count -eq 6) "$branch の基準は 6 チェック"
    foreach ($name in 'build-test', 'instructions', 'nuget-audit', 'npm-audit', 'wip-title', 'cla') {
        $matching = @($baselineChecks | Where-Object context -ceq $name)
        Assert-Audit ($matching.Count -eq 1 -and $matching[0].integration_id -eq 15368) "$branch の基準 $name は Actions に固定"
    }
    $generated = Get-AuditExpectedRuleset $baseline $policy
    $generatedChecks = @($generated.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks
    Assert-Audit ($generatedChecks.Count -eq 8) "$branch の追加チェック・CodeQL は重複しない"
    foreach ($check in $generatedChecks) {
        Assert-Audit ($check.integration_id -eq 15368) "$branch の期待値 $($check.context) は Actions に固定"
        foreach ($app in 'missing', 'null', 57789) {
            $different = $generated | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
            $changed = @($different.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks |
                Where-Object context -ceq $check.context
            if ($app -eq 'missing') {
                $changed.Remove('integration_id')
            } elseif ($app -eq 'null') {
                $changed.integration_id = $null
            } else {
                $changed.integration_id = $app
            }
            Assert-Audit (@(Compare-AuditRuleset $generated $different).Count -gt 0) "$branch の $($check.context) の App=$app は差"
            $changed.integration_id = 15368
            Assert-Audit (@(Compare-AuditRuleset $generated $different).Count -eq 0) "$branch の $($check.context) の Actions 固定は一致"
        }
    }
}
foreach ($visibility in 'public', 'internal', 'private') {
    Assert-Audit ((Get-AuditClaPolicy @{visibility=$visibility}) -eq ($visibility -eq 'public')) "$visibility の CLA 既定"
}
Assert-Audit (-not (Get-AuditClaPolicy @{visibility='public';requireCla=$false;claReason='例外の根拠'})) '理由付きの例外'
foreach ($invalid in @(@{visibility='public';requireCla=$true}, @{visibility='public';requireCla='true';claReason='理由'})) {
    $thrown = $false
    try { $null = Get-AuditClaPolicy $invalid } catch { $thrown = $true }
    Assert-Audit $thrown '理由無し・bool 以外の CLA 例外は拒否'
}
$claPaths = @(Get-AuditClaPaths)
foreach ($mask in 0..3) {
    $entries = @{}
    for ($i = 0; $i -lt $claPaths.Count; $i++) {
        if ($mask -band (1 -shl $i)) { $entries[$claPaths[$i]] = @{type='blob';mode='100644'} }
    }
    $ready = Get-AuditClaReadiness $entries
    Assert-Audit ($ready -eq ($mask -eq 3)) "前提ファイルの組合せ $mask"
    $candidate = Get-AuditExpectedRuleset $definition $policy -ClaReady $ready
    $cla = @($candidate.rules | Where-Object type -eq required_status_checks |
        ForEach-Object { $_.parameters.required_status_checks } | Where-Object context -ceq 'cla')
    Assert-Audit ($cla.Count -eq [int]$ready) '両ファイルがあるときだけ cla 必須（ExtraChecks でも迂回・重複しない）'
}
$entries[$claPaths[1]].mode = '120000'
Assert-Audit (-not (Get-AuditClaReadiness $entries)) '署名一覧へのシンボリックリンクは前提を満たさない'
$internal = Get-AuditExpectedRuleset $definition @{visibility='internal';extraChecks=@('cla')} -ClaReady $true
Assert-Audit (@($internal.rules | Where-Object type -eq required_status_checks |
    ForEach-Object { $_.parameters.required_status_checks } | Where-Object context -ceq 'cla').Count -eq 0) 'Template にファイルがあっても cla を必須にしない'
$tag = Get-Content (Join-Path $root '.github\rulesets\release-tag-protection.json') -Raw | ConvertFrom-Json -AsHashtable
Assert-Audit (@(Compare-AuditRuleset $tag (Get-AuditExpectedRuleset $tag $policy)).Count -eq 0) 'タグ保護へチェックを追加しない'
Assert-Audit (@(Compare-AuditRuleset $tag (Get-AuditExpectedRuleset $tag $omitPolicy)).Count -eq 0) '除外設定はタグ保護を変えない'
$actual = $expected | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
$actual.id = 123
$actual.rules = @($actual.rules | Sort-Object type -Descending)
$pr = @($actual.rules | Where-Object type -eq pull_request)[0]
$pr.parameters.required_reviewers = @()
$pr.parameters.dismissal_restriction = @{ enabled = $false; allowed_actors = @() }
$pr.parameters.require_extra_approval_for_unattributed_changes = $true
$statusRule = @($actual.rules | Where-Object type -eq required_status_checks)[0]
$statusRule.parameters.do_not_enforce_on_create = $false
$statusRule.parameters.required_status_checks = @($statusRule.parameters.required_status_checks | Sort-Object context)
Assert-Audit (@(Compare-AuditRuleset $expected $actual).Count -eq 0) 'ルール順序・REST 既定値・メタデータは差にしない'
$statusRule.parameters.do_not_enforce_on_create = $true
Assert-Audit (@(Compare-AuditRuleset $expected $actual).Count -gt 0) '意味のある既定値変更を検出する'
$statusRule.parameters.Remove('do_not_enforce_on_create')
$unpinned = $expected | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
$unpinnedChecks = @($unpinned.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks
foreach ($check in $unpinnedChecks) { $check.Remove('integration_id') }
$nullApp = $unpinned | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
@($nullApp.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks |
    ForEach-Object { $_.integration_id = $null }
Assert-Audit (@(Compare-AuditRuleset $unpinned $nullApp).Count -eq 0) '全チェックの null は App 指定なしと同じ'
$statusRule.parameters.required_status_checks[0].integration_id = 57789
Assert-Audit (@(Compare-AuditRuleset $expected $actual).Count -gt 0) 'CodeQL App 57789 は Actions 固定と異なる'
$statusRule.parameters.required_status_checks[0].integration_id = 15368
Assert-Audit (@(Compare-AuditRuleset $expected $actual).Count -eq 0) '全チェックの Actions 固定は一致'
Assert-Audit (@(Compare-AuditRuleset $unpinned $actual).Count -gt 0) '指定していない App 制限も差として検出する'
$actual.bypass_actors = @()
Assert-Audit (@(Compare-AuditRuleset $expected $actual).Count -gt 0) 'bypass の消失を検出する'
$actual.bypass_actors = $expected.bypass_actors
$pr.parameters.allowed_merge_methods = @('squash')
Assert-Audit (@(Compare-AuditRuleset $expected $actual).Count -gt 0) 'マージ方式の差を検出する'
Assert-Audit ((ConvertTo-AuditCanonicalJson @('a','a')) -cne (ConvertTo-AuditCanonicalJson @('a'))) '配列の重複を消さない'
$withoutCla = Get-AuditExpectedRuleset $definition $policy -ClaReady $false
Assert-Audit (@(Compare-AuditRuleset $expected $withoutCla).Count -gt 0) '前提が揃ったブランチの cla 必須チェック欠落を検出'
$wrongApp = $expected | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
@($wrongApp.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks |
    Where-Object context -ceq 'cla' | ForEach-Object { $_.integration_id = 1 }
Assert-Audit (@(Compare-AuditRuleset $expected $wrongApp).Count -gt 0) 'cla の発行 App の違いを検出'

# 実スクリプトの GET 境界だけを固定する。gh に到達したら書き込みを試みたものとして失敗させる。
function Invoke-SyncFixture {
    param(
        [string]$Visibility = 'public', [string[]]$Missing = @(), [switch]$NoMaster,
        [switch]$ApiError, [switch]$Truncated, [switch]$IncludeCodeQL, [string[]]$ExtraChecks = @(),
        [string[]]$OmitChecks = @()
    )
    function Import-Module {}
    function Invoke-AuditGet {
        param($Endpoint, $Projection)
        if ($Endpoint -eq 'repos/o/r') { return @{status=200;data=@{visibility=$Visibility}} }
        if ($ApiError -and $Endpoint -match 'master-sha') { return @{status=403;data=$null} }
        $entries = @()
        foreach ($path in Get-AuditClaPaths) {
            if ($path -cin $Missing -or ($Endpoint -match 'master-sha' -and "master:$path" -cin $Missing)) { continue }
            $entries += @{path=$path;type='blob';mode='100644'}
        }
        return @{status=200;data=@{tree=$entries;truncated=[bool]$Truncated}}
    }
    function Get-AuditPages {
        param($Endpoint)
        if ($Endpoint -like '*/branches') {
            $data = @(@{name='develop';commit=@{sha='develop-sha'}})
            if (-not $NoMaster) { $data += @{name='master';commit=@{sha='master-sha'}} }
            return @{status=200;data=$data}
        }
        return @{status=200;data=@(@{name='保護ブランチ';id=1;source_type='Repository'})}
    }
    function gh { throw '-WhatIf で gh の書き込みを呼んではいけません。' }
    $output = @(& (Join-Path $PSScriptRoot 'Sync-Ruleset.ps1') -Repository o/r -WhatIf `
        -IncludeCodeQL:$IncludeCodeQL -ExtraChecks $ExtraChecks -OmitChecks $OmitChecks -WarningAction SilentlyContinue)
    return @($output | Where-Object { $_ -is [string] -and $_.StartsWith('{') } |
        ForEach-Object { $_ | ConvertFrom-Json -AsHashtable })
}
function Get-FixtureClaCount {
    param([array]$Plans, [string]$Name)
    return @($Plans | Where-Object name -ceq $Name | ForEach-Object { $_.rules } |
        Where-Object type -eq required_status_checks | ForEach-Object { $_.parameters.required_status_checks } |
        Where-Object context -ceq 'cla').Count
}
$plans = Invoke-SyncFixture
Assert-Audit ($plans.Count -eq 3 -and (Get-FixtureClaCount $plans 'master-protection') -eq 1) 'WhatIf は全計画を出すだけで旧ルールも変更しない'
$plans = Invoke-SyncFixture -IncludeCodeQL -ExtraChecks @('integration', 'build-test', 'cla')
foreach ($branch in 'develop', 'master') {
    $plan = @($plans | Where-Object name -ceq "$branch-protection")[0]
    $baseline = Get-Content (Join-Path $root ".github\rulesets\$branch-protection.json") -Raw |
        ConvertFrom-Json -AsHashtable
    Assert-Audit (@(Compare-AuditRuleset (Get-AuditExpectedRuleset $baseline $policy) $plan).Count -eq 0) "$branch の同期計画と監査期待値は一致"
    $planChecks = @($plan.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks
    Assert-Audit ($planChecks.Count -eq 8) "$branch の同期は追加チェック・CodeQL を含む"
    foreach ($check in $planChecks) {
        Assert-Audit ($check.integration_id -eq 15368) "$branch の同期 $($check.context) は Actions に固定"
    }
}
$plans = Invoke-SyncFixture -ExtraChecks @('integration, e2e', 'frontend')
$splitPlan = @($plans | Where-Object name -ceq 'develop-protection')[0]
$splitStatus = @($splitPlan.rules | Where-Object type -eq required_status_checks)[0].parameters.required_status_checks
$splitChecks = @($splitStatus | ForEach-Object { $_.context })
Assert-Audit ('integration,e2e' -notin $splitChecks -and 'integration' -in $splitChecks -and 'e2e' -in $splitChecks -and 'frontend' -in $splitChecks) '-ExtraChecks のカンマ区切りを分割する'
foreach ($omitInput in @(@('instructions', 'npm-audit', 'cla'), @(' instructions, npm-audit, cla, ', ''))) {
    $plans = Invoke-SyncFixture -IncludeCodeQL -ExtraChecks $omitPolicy.extraChecks -OmitChecks $omitInput
    foreach ($branch in 'develop', 'master') {
        $plan = @($plans | Where-Object name -ceq "$branch-protection")[0]
        $baseline = Get-Content (Join-Path $root ".github\rulesets\$branch-protection.json") -Raw |
            ConvertFrom-Json -AsHashtable
        Assert-Audit (@(Compare-AuditRuleset (Get-AuditExpectedRuleset $baseline $omitPolicy) $plan).Count -eq 0) "$branch の配列・カンマ区切りの除外を含む同期計画と監査期待値は一致"
        Assert-Audit ((Get-FixtureClaCount $plans "$branch-protection") -eq 1) "$branch の同期でも cla は除外できない"
    }
}
$corePolicy = @($config.repositories | Where-Object name -ceq 'VehicleVision.PleasanterTools.Core')[0]
$plans = Invoke-SyncFixture -IncludeCodeQL -ExtraChecks ($corePolicy.extraChecks -join ',') -OmitChecks ($corePolicy.omitChecks -join ',')
foreach ($branch in 'develop', 'master') {
    $plan = @($plans | Where-Object name -ceq "$branch-protection")[0]
    $baseline = Get-Content (Join-Path $root ".github\rulesets\$branch-protection.json") -Raw |
        ConvertFrom-Json -AsHashtable
    Assert-Audit (@(Compare-AuditRuleset (Get-AuditExpectedRuleset $baseline $corePolicy) $plan).Count -eq 0) "$branch の Core の同期引数と実際の監査設定は一致"
}
$thrown = $false
try { $null = Invoke-SyncFixture -OmitChecks 'instruction' } catch { $thrown = $true }
Assert-Audit $thrown '同期でも除外名の誤字は反映前にエラー'
$plans = Invoke-SyncFixture -Missing @('master:.github/cla/signed.yml')
Assert-Audit ((Get-FixtureClaCount $plans 'develop-protection') -eq 1 -and
    (Get-FixtureClaCount $plans 'master-protection') -eq 0) 'develop が準備済みでも master の署名一覧欠落は別判定'
$plans = Invoke-SyncFixture -Missing @('.github/workflows/cla-check.yml')
Assert-Audit ((Get-FixtureClaCount $plans 'develop-protection') -eq 0) 'ワークフロー欠落でも必須化を保留'
$plans = Invoke-SyncFixture -NoMaster
Assert-Audit ($plans.Count -eq 2 -and 'master-protection' -cnotin $plans.name) '存在しない master のルールは作らない'
$plans = Invoke-SyncFixture -Visibility internal
Assert-Audit ((Get-FixtureClaCount $plans 'develop-protection') -eq 0) 'Template の WhatIf は cla を必須化しない'
$thrown = $false
try { $null = Invoke-SyncFixture -ApiError } catch { $thrown = $true }
Assert-Audit $thrown 'CLA の取得が 403 なら安全な欠落とみなさず反映前に失敗'
$thrown = $false
try { $null = Invoke-SyncFixture -Truncated } catch { $thrown = $true }
Assert-Audit $thrown 'ツリーが打ち切られた場合は前提の欠落と断定せず反映前に失敗'

$labels = @(@{ name = 'common'; color = 'ABCDEF'; description = '説明' })
Assert-Audit (@(Compare-AuditLabels $labels @(@{name='common';color='abcdef';description='説明'}, @{name='.NET'}) @('.NET')).Count -eq 0) '色の大小文字と許可した追加ラベル'
Assert-Audit (@(Compare-AuditLabels $labels @() @()).Count -eq 1) 'ラベル欠落'
Assert-Audit (@(Compare-AuditLabels $labels @(@{name='common';color='abcdef';description='違う'}, @{name='extra'}) @()).Count -eq 2) '説明変更と未許可の追加ラベル'

Assert-Audit ((Compare-AuditFile 'a.yml' "a`nb`n" "a`r`nb`r`n" @{}).status -eq 'exact') '改行コードの違い'
Assert-Audit ((Compare-AuditFile 'a.yml' 'Template' 'App' @{App='Template'}).status -eq 'normalized') '宣言した置換だけを許可'
Assert-Audit ((Compare-AuditFile 'a.yml' 'Template' 'App2' @{App='Template'}).status -eq 'different') '置換しても残る差'
Assert-Audit ((Compare-AuditFile 'a.yml' "a`n" 'a' @{}).details.Count -gt 0) '末尾改行だけの差も詳細を出す'
$section = "[*.cs]`ncsharp_prefer_braces = true:error`n"
Assert-Audit ((Compare-AuditFile '.editorconfig' $section ("[*]`nindent_size=2`n" + $section + "[*.ts]`na=b") @{}).status -eq 'exact') 'C# 節だけを比較'
Assert-Audit ((Compare-AuditFile '.editorconfig' $section '[*.ts]' @{}).status -eq 'different') 'C# 節が無い'
$props = '<Project><PropertyGroup><EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild></PropertyGroup></Project>'
Assert-Audit ((Compare-AuditFile 'Directory.Build.props' $props $props.Replace('true','false') @{}).status -eq 'different') 'ビルドでの強制の値'
Assert-Audit ((Compare-AuditFile 'Directory.Build.props' $props $props.Replace('<PropertyGroup>', '<PropertyGroup Condition="x">') @{}).status -eq 'different') '強制が条件付きになった差'
Assert-Audit ((Compare-AuditFile 'Directory.Build.props' $props $props.Replace('<Project>', '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">') @{}).status -eq 'exact') 'MSBuild XML の名前空間だけの差は値の差にしない'
$sentinel = 'NEVER-PRINT-THIS-SECRET'
$comparison = Compare-AuditFile 'a.yml' 'expected' $sentinel @{}
Assert-Audit (($comparison | ConvertTo-Json) -notmatch $sentinel) 'diff は行本文を出さない'

$files = @{
    'Directory.Build.props' = '<Project><PropertyGroup><CoreVersion>0.1.0-preview.9</CoreVersion></PropertyGroup></Project>'
    'Directory.Packages.props' = '<Project><ItemGroup><PackageVersion Include="VehicleVision.PleasanterTools.Core.Mail" Version="0.1.0-preview.10" /></ItemGroup></Project>'
    'src/App/App.csproj' = '<Project><ItemGroup><PackageReference Include="VehicleVision.PleasanterTools.Core.Data" Version="$(CoreVersion)" /><PackageReference Include="VehicleVision.PleasanterTools.Core.Mail" /><PackageReference Include="VehicleVision.PleasanterTools.Core.Web" Version="$(Missing)" /></ItemGroup></Project>'
}
$references = @(Get-AuditCoreReferences $files)
Assert-Audit ($references.Count -eq 3) '3 パッケージを検出'
Assert-Audit ($references[0].version -eq '0.1.0-preview.9' -and $references[0].resolved) 'MSBuild のプロパティ参照'
Assert-Audit ($references[1].version -eq '0.1.0-preview.10' -and $references[1].resolved) '中央パッケージ管理'
Assert-Audit (-not $references[2].resolved) '未解決の版を一致にしない'
$files['src/App/App.csproj'] = [string][char]0xFEFF + $files['src/App/App.csproj']
Assert-Audit (@(Get-AuditCoreReferences $files).Count -eq 3) 'UTF-8 BOM 付きプロジェクト'
Assert-Audit ((Compare-AuditFile 'Directory.Build.props' $props ([string][char]0xFEFF + $props) @{}).status -eq 'exact') 'UTF-8 BOM 付き props の節比較'
$files['src/Directory.Build.props'] = '<Project />'
Assert-Audit (-not @(Get-AuditCoreReferences $files)[0].resolved) '最寄りの props がある場合は祖先の props を自動で合成しない'
$files.Remove('src/Directory.Build.props')
$files['src/App/App.csproj'] = '<Project><ItemGroup><PackageReference Include="VehicleVision.PleasanterTools.Core.Mail"><VersionOverride>1.0.0</VersionOverride></PackageReference></ItemGroup></Project>'
Assert-Audit (@(Get-AuditCoreReferences $files)[0].version -eq '1.0.0') 'VersionOverride 要素を優先'
Assert-Audit ((Get-AuditLatestVersion @('0.1.0-preview.9','0.1.0-preview.10','0.1.0-preview.2')) -eq '0.1.0-preview.10') 'preview の数値を辞書順で比べない'
Assert-Audit ((Get-AuditLatestVersion @('1.0.0-preview.10','1.0.0','0.9.9')) -eq '1.0.0') '安定版の優先順位'
Assert-Audit ((Get-AuditHttpState 404) -eq 'unknown') '権限非表示の 404 を欠落と断定しない'
Assert-Audit ((Get-AuditHttpState 404 -KnownReadable -MissingAllowed) -eq 'missing') '読めることが確定している資源の欠落'
Assert-Audit ((Get-AuditHttpState 403) -eq 'unknown') '権限不足'
Assert-Audit ((Get-AuditHttpState 429) -eq 'error') 'レート制限'
Assert-Audit ((Get-AuditHttpState 500) -eq 'error') 'API 障害'
Assert-Audit ((Get-AuditActionPresence 'key' @{status=200;data=@('key')} $null).status -eq 'ok') 'repository の名前で存在確認'
Assert-Audit ((Get-AuditActionPresence 'key' @{status=200;data=@()} @{status=200;data=@('key')}).status -eq 'ok') 'organization からの継承'
Assert-Audit ((Get-AuditActionPresence 'key' @{status=200;data=@()} @{status=200;data=@()}).status -eq 'missing') '両スコープで確認した欠落'
Assert-Audit ((Get-AuditActionPresence 'key' @{status=200;data=@()} @{status=404;data=$null}).status -eq 'unknown') '継承を確認できなければ欠落にしない'
Assert-Audit ((Get-AuditActionPresence 'key' @{status=403;data=$null} @{status=200;data=@('key')}).status -eq 'ok') '片側で存在すれば存在確認は成立'
Assert-Audit ((Get-AuditExitCode @(@{status='ok'}, @{status='exact'}, @{status='normalized'}, @{status='notApplicable'})) -eq 0) '一致と対象外だけなら終了コード 0'
foreach ($status in 'different', 'missing', 'unknown', 'error') {
    Assert-Audit ((Get-AuditExitCode @(@{status='ok'}, @{status=$status})) -eq 1) "$status は終了コード 1"
}

# モジュール内の通信境界を固定応答に置換し、複数ページでも名前以外を受け取らないことを確かめる。
& (Get-Module RepoAudit) {
    $script:calls = [Collections.Generic.List[string]]::new()
    function script:Invoke-AuditGet {
        param($Endpoint, $Projection, [switch]$PublicFallback)
        $script:calls.Add("$Endpoint $Projection")
        $items = if ($Endpoint -match 'page=1$') { @(1..30 | ForEach-Object { "NAME_$_" }) } else { @('LAST') }
        return @{ status = 200; data = @{ items = $items } }
    }
    $response = Get-AuditPages 'repos/o/r/actions/variables' 'variables' -NamesOnly
    if ($response.data.Count -ne 31 -or $script:calls.Count -ne 2 -or
        -not $script:calls[0].Contains('per_page=30&page=1') -or
        -not $script:calls[0].EndsWith('{items: [.variables[].name]}')) { throw '名前の射影またはページングが不正' }
}
$count++

# ライセンス文書の比較（正本・節マーカー・部品集）。固定入力と Template 自身の正本で確かめる。
$licenseFiles = @($config.licenseFiles)
foreach ($path in 'LICENSING.md', 'LICENSE-COMMERCIAL.md', 'CLA.md', '.github/ISSUE_TEMPLATE/cla.yml', '.github/cla/signed.yml',
    'CONTRIBUTING.md', 'NOTICE', '.github/workflows/cla-check.yml') {
    Assert-Audit ($path -cin $licenseFiles) "ライセンス文書を比較対象にする: $path"
}
$baselines = Get-AuditLicenseBaselines $root $licenseFiles
foreach ($path in $licenseFiles) {
    $text = Get-Content -LiteralPath (Join-Path $root $path) -Raw -Encoding utf8
    $result = Compare-AuditLicenseFile $path $baselines.dual[$path] $text @{} $baselines.parts.dual 'dual'
    Assert-Audit ($result.status -eq 'exact') "Template 自身が正本と一致する: $path ($($result.details -join '; '))"
}
Assert-Audit ($baselines.parts.dual.ContainsKey('core=on') -and -not $baselines.parts.triple.ContainsKey('core=on')) 'トリプルに無い core=on は選べない'
Assert-Audit ($baselines.parts.triple['release=unreleased'].Contains('3 層') -and
    $baselines.parts.dual['release=unreleased'].Contains('デュアル')) 'トリプルの部品はデュアルの同名の部品を上書きする'
Assert-Audit (-not $baselines.parts.triple.ContainsKey('sni-terms=on') -and $baselines.parts.triple.ContainsKey('sni-terms=off')) 'トリプルの sni-terms は off のみ'
Assert-Audit ($baselines.parts.triple.ContainsKey('nuget=on') -and -not $baselines.parts.dual.ContainsKey('nuget=on')) 'nuget はトリプルだけ'
Assert-Audit ($baselines.triple['LICENSING.md'].Contains('3 層') -and
    $baselines.triple['.github/ISSUE_TEMPLATE/cla.yml'] -ceq $baselines.dual['.github/ISSUE_TEMPLATE/cla.yml']) 'トリプルは LICENSING.md を別の正本とし、cla.yml はデュアルと同じ'

$licensing = Get-Content -LiteralPath (Join-Path $root 'LICENSING.md') -Raw -Encoding utf8
$commercial = Get-Content -LiteralPath (Join-Path $root 'LICENSE-COMMERCIAL.md') -Raw -Encoding utf8
function Test-LicenseCase {
    param([string]$Path, [string]$Text, [string]$Tier = 'dual', [hashtable]$Replacements = @{})
    return Compare-AuditLicenseFile $Path $baselines[$Tier][$Path] $Text $Replacements $baselines.parts[$Tier] $Tier
}
$apiDb = $licensing.Replace('<!-- license:begin connection=api-only -->', '<!-- license:begin connection=api,db-read -->').Replace(
    $baselines.parts.dual['connection=api-only'], "$($baselines.parts.dual['connection=api'])`n$($baselines.parts.dual['connection=db-read'])")
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $apiDb).status -ne 'different') '部品を ID の順に連結した connection の組み合わせは通る'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $apiDb.Replace('connection=api,db-read', 'connection=db-read,api')).status -eq 'different') 'connection の連結順の違い'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $apiDb.Replace('connection=api,db-read', 'connection=api,db-write')).status -eq 'different') '部品集に無い connection の ID'
$custom = $licensing.Replace('<!-- license:begin pleasanter-note=free -->', "<!-- license:begin pleasanter-note=free -->`n自由記述の補足です。")
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $custom).status -eq 'exact') 'free の節の中身は自由'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace('<!-- license:begin sni=off -->', "<!-- license:begin sni=off -->`n勝手な文")).status -eq 'different') '非 free の節の本文を書き換えた'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace('<!-- license:begin sni=off -->', '<!-- license:begin sni=maybe -->')).status -eq 'different') '部品集に無い ID'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace('<!-- license:begin pleasanter-note=free -->', "<!-- license:begin pleasanter-note=free -->`n〈ここに書く〉")).status -eq 'different') '〈 〉 が残っている'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace('# ライセンスについて', '# 勝手に変えた見出し')).status -eq 'different') 'マーカーの外側の書き換え'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace('<!-- license:end sni -->', '')).status -eq 'different') '終了マーカーの欠落'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace('<!-- license:end sni -->', '<!-- license:end core -->')).status -eq 'different') '終了マーカーの名前違い'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace('<!-- license:begin sni=off -->', '<!-- license:begin sni=off')).status -eq 'different') 'マーカーの形式違い'
$webService = $licensing.Replace('<!-- license:begin usage=console -->', '<!-- license:begin usage=web-service -->')
$web = $baselines.parts.dual['usage=web-service'].Replace('〈提供する画面・URL など〉', '画面と API')
$webService = $webService.Replace($baselines.parts.dual['usage=console'], $web)
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $webService).status -ne 'different') '〈…〉は手で埋めた本文でも通る'
$webWrong = $webService.Replace('点に注意してください。', '点に注意。')
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $webWrong).status -eq 'different') '〈…〉以外の本文の差'
$replaced = $licensing.Replace('VehicleVision.PleasanterTools.Template', 'VehicleVision.PleasanterTools.App')
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $replaced 'dual' @{ 'VehicleVision.PleasanterTools.App' = 'VehicleVision.PleasanterTools.Template' }).status -eq 'normalized') '製品名の置換で一致'
Assert-Audit ((Test-LicenseCase 'LICENSING.md' $licensing.Replace("`n", "`r`n")).status -eq 'exact') 'ライセンス文書の改行コード'

$claCheck = Get-Content -LiteralPath (Join-Path $root '.github/workflows/cla-check.yml') -Raw -Encoding utf8
$claTriple = $claCheck.Replace('// license:begin cla-intro=dual', '// license:begin cla-intro=triple').Replace(
    $baselines.parts.dual['cla-intro=dual'], $baselines.parts.triple['cla-intro=triple'])
Assert-Audit ((Test-LicenseCase '.github/workflows/cla-check.yml' $claTriple 'triple').status -ne 'different') 'トリプルの cla-check.yml は cla-intro=triple'
Assert-Audit ((Test-LicenseCase '.github/workflows/cla-check.yml' $claTriple 'dual').status -eq 'different') 'デュアルのリポジトリで cla-intro=triple'
Assert-Audit ((Test-LicenseCase '.github/workflows/cla-check.yml' $claCheck 'triple').status -eq 'different') 'トリプルのリポジトリで cla-intro=dual'
Assert-Audit ((Test-LicenseCase '.github/workflows/cla-check.yml' $claCheck.Replace('exit 1', 'exit 0')).status -eq 'different') 'cla-check.yml の外側の書き換え'

$notice = Get-Content -LiteralPath (Join-Path $root 'NOTICE') -Raw -Encoding utf8
Assert-Audit ((Test-LicenseCase 'NOTICE' ($notice + "`n独自の追記")).status -eq 'exact') 'NOTICE の 4 行目以降は自由'
Assert-Audit ((Test-LicenseCase 'NOTICE' $notice.Replace('AGPL-3.0-or-later OR', 'AGPL-3.0-only OR')).status -eq 'different') 'NOTICE の SPDX 行'
$contributing = Get-Content -LiteralPath (Join-Path $root 'CONTRIBUTING.md') -Raw -Encoding utf8
Assert-Audit ((Test-LicenseCase 'CONTRIBUTING.md' ($contributing + "`n## 追加の節`n`n自由`n")).status -eq 'exact') 'CONTRIBUTING.md の CLA 節の外は自由'
Assert-Audit ((Test-LicenseCase 'CONTRIBUTING.md' $contributing.Replace('### 手順', '### やり方')).status -eq 'different') 'CONTRIBUTING.md の CLA 節の書き換え'
Assert-Audit ((Test-LicenseCase 'CONTRIBUTING.md' '# 貢献のしかた').status -eq 'different') 'CLA 節が無い'
$signed = Get-Content -LiteralPath (Join-Path $root '.github/cla/signed.yml') -Raw -Encoding utf8
$signedRecord = $signed -replace '(?m)^signatories: \[\]', "`n# 初期記録の根拠`nsignatories:`n  - login: someone`n    signed: 2026-01-01`n    type: individual"
Assert-Audit ((Test-LicenseCase '.github/cla/signed.yml' $signedRecord).status -eq 'exact') 'signed.yml の署名記録と、その根拠の注記は自由'
Assert-Audit ((Test-LicenseCase '.github/cla/signed.yml' $signed.Replace('# 書き方', '# 書き方（変更）')).status -eq 'different') 'signed.yml の先頭の注記の書き換え'
Assert-Audit ((Test-LicenseCase '.github/cla/signed.yml' '# だけ').status -eq 'different') 'signatories が無い signed.yml'

$parsedLicensing = (ConvertTo-AuditLicenseSections $licensing).sections
$parsedCommercial = (ConvertTo-AuditLicenseSections $commercial).sections
Assert-Audit (@(Test-AuditLicenseConsistency $parsedLicensing $parsedCommercial).Count -eq 0) '正本の節の ID は整合する'
$sniOn = (ConvertTo-AuditLicenseSections $commercial.Replace('<!-- license:begin sni-terms=off -->', '<!-- license:begin sni-terms=on -->')).sections
Assert-Audit (@(Test-AuditLicenseConsistency $parsedLicensing $sniOn).Count -eq 1) 'sni と sni-terms の食い違い'
$coreOn = (ConvertTo-AuditLicenseSections $licensing.Replace('<!-- license:begin core=off -->', '<!-- license:begin core=on -->')).sections
Assert-Audit (@(Test-AuditLicenseConsistency $coreOn $parsedCommercial).Count -eq 1) 'core と core-terms の食い違い'
$coreLibrary = (ConvertTo-AuditLicenseSections $licensing.Replace('<!-- license:begin core=off -->', '<!-- license:begin core=library -->')).sections
$coreTermsLibrary = (ConvertTo-AuditLicenseSections $commercial.Replace('<!-- license:begin core-terms=off -->', '<!-- license:begin core-terms=library -->')).sections
Assert-Audit (@(Test-AuditLicenseConsistency $coreLibrary $coreTermsLibrary).Count -eq 0) 'core=library と core-terms=library は整合する'
Assert-Audit (@(Test-AuditLicenseConsistency $coreLibrary $parsedCommercial).Count -eq 1) 'core=library なのに core-terms=off'
$coreTermsOn = (ConvertTo-AuditLicenseSections $commercial.Replace('<!-- license:begin core-terms=off -->', '<!-- license:begin core-terms=on -->')).sections
Assert-Audit (@(Test-AuditLicenseConsistency $coreLibrary $coreTermsOn).Count -eq 1) 'core=library なのに core-terms=on'
$sniLibrary = (ConvertTo-AuditLicenseSections $licensing.Replace('<!-- license:begin sni=off -->', '<!-- license:begin sni=library -->')).sections
$sniScopeLibrary = (ConvertTo-AuditLicenseSections $commercial.Replace('<!-- license:begin sni-scope=off -->', '<!-- license:begin sni-scope=library -->').Replace('<!-- license:begin sni-terms=off -->', '<!-- license:begin sni-terms=library -->')).sections
Assert-Audit (@(Test-AuditLicenseConsistency $sniLibrary $sniScopeLibrary).Count -eq 0) 'sni=library と sni-scope・sni-terms=library は整合する'
Assert-Audit (@(Test-AuditLicenseConsistency $sniLibrary $parsedCommercial).Count -eq 2) 'sni=library なのに sni-scope・sni-terms=off'
Assert-Audit ($baselines.parts.dual.ContainsKey('sni=library') -and $baselines.parts.dual.ContainsKey('core=library') -and
    $baselines.parts.dual.ContainsKey('core-terms=library') -and -not $baselines.parts.triple.ContainsKey('sni=library') -and
    -not $baselines.parts.triple.ContainsKey('core-terms=library')) 'ライブラリ向けの部品はデュアルだけ（トリプルは off のみ）'
$library = (ConvertTo-AuditLicenseSections $licensing.Replace('<!-- license:begin independence=app -->', '<!-- license:begin independence=library -->')).sections
Assert-Audit (@(Test-AuditLicenseConsistency $library $parsedCommercial).Count -eq 1) 'usage=console なのに independence=library'
$refOff = (ConvertTo-AuditLicenseSections $commercial.Replace('<!-- license:begin reference-scope=on -->', '<!-- license:begin reference-scope=off -->')).sections
Assert-Audit (@(Test-AuditLicenseConsistency $parsedLicensing $refOff).Count -eq 1) 'reference と reference-scope の食い違い'

$report = @{
    generatedAt = 'fixed'; baselineCommit = 'fixed'; repositories = @('o/r'); excludedRepositories = @{ other = '対象外' }
    results = @(
        @{ repository='o/r'; category='develop 共通'; item='file'; status='normalized'; details=@('置換') },
        @{ repository='o/r'; category='Actions'; item='secret'; status='unknown'; details=@('権限不足') }
    )
}
$markdown = ConvertTo-AuditMarkdown $report
Assert-Audit ($markdown.Contains('✓ 置換で一致') -and $markdown.Contains('確認不可')) 'Markdown の分類'
Write-Output "監査ロジック: $count 件成功"
