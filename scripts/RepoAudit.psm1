$ErrorActionPreference = 'Stop'

function ConvertTo-AuditCanonicalJson {
    param([AllowNull()]$Value)
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [System.Collections.IDictionary]) {
        $parts = foreach ($key in @($Value.Keys | Sort-Object -CaseSensitive)) {
            ($key | ConvertTo-Json -Compress) + ':' + (ConvertTo-AuditCanonicalJson $Value[$key])
        }
        return '{' + ($parts -join ',') + '}'
    }
    if ($Value -is [array]) {
        # GitHub のルール・チェック・bypass・ref の配列は順序に意味が無い。重複は残す。
        $parts = @($Value | ForEach-Object { ConvertTo-AuditCanonicalJson $_ } | Sort-Object -CaseSensitive)
        return '[' + ($parts -join ',') + ']'
    }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 50
}

function Get-AuditClaPolicy {
    param([hashtable]$Policy)
    if ($Policy.visibility -notin 'public', 'private', 'internal') { throw 'CLA の対象判定には公開範囲が必要です。' }
    if ($Policy.ContainsKey('requireCla')) {
        if ($Policy.requireCla -isnot [bool] -or [string]::IsNullOrWhiteSpace($Policy.claReason)) {
            throw 'CLA の例外には bool の requireCla と claReason が必要です。'
        }
        return $Policy.requireCla
    }
    return $Policy.visibility -eq 'public'
}

function Get-AuditClaPaths {
    return '.github/workflows/cla-check.yml', '.github/cla/signed.yml'
}

function Get-AuditClaReadiness {
    param([hashtable]$Entries)
    foreach ($path in Get-AuditClaPaths) {
        if (-not $Entries.ContainsKey($path) -or $Entries[$path].type -ne 'blob' -or
            $Entries[$path].mode -notin '100644', '100755') { return $false }
    }
    return $true
}

function Get-AuditExpectedRuleset {
    param([hashtable]$Definition, [hashtable]$Policy, [bool]$ClaReady = $true)
    $expected = $Definition | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable
    $requireCla = (Get-AuditClaPolicy $Policy) -and $ClaReady
    $extra = @($Policy.extraChecks)
    $omit = @()
    if ($Policy.ContainsKey('omitChecks')) {
        if ($Policy.omitChecks -isnot [array]) { throw 'omitChecks は文字列の配列で指定してください。' }
        foreach ($name in $Policy.omitChecks) {
            if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name)) {
                throw 'omitChecks は空でない文字列の配列で指定してください。'
            }
            # -OmitChecks cla でも、公開範囲・ブートストラップの制約は迂回させない。
            if ($name -ceq 'cla') { continue }
            $omit += $name
        }
    }
    if ($Policy.includeCodeQL) { $extra += 'Analyze (csharp)' }
    foreach ($rule in $expected.rules) {
        if ($rule.type -ne 'required_status_checks') { continue }
        foreach ($name in $omit) {
            if ($name -cnotin @($rule.parameters.required_status_checks.context)) {
                throw "omitChecks に基準の必須チェックにない名前があります: $name"
            }
        }
        $rule.parameters.required_status_checks = @($rule.parameters.required_status_checks |
            Where-Object { $_.context -cnotin $omit })
        if (-not $requireCla) {
            $rule.parameters.required_status_checks = @($rule.parameters.required_status_checks |
                Where-Object context -cne 'cla')
        }
        foreach ($name in $extra) {
            # 旧い -ExtraChecks cla でも、対象外・ブートストラップの制約は迂回させない。
            if ($name -ceq 'cla') { continue }
            if ($name -cin $omit) { continue }
            if ($name -cnotin @($rule.parameters.required_status_checks.context)) {
                $rule.parameters.required_status_checks += @{ context = $name; integration_id = 15368 }
            }
        }
    }
    return $expected
}

function Select-AuditRuleset {
    param([hashtable]$Ruleset)
    $result = @{}
    foreach ($key in 'name', 'target', 'enforcement', 'conditions', 'bypass_actors', 'rules') {
        if ($Ruleset.ContainsKey($key)) { $result[$key] = $Ruleset[$key] }
    }
    foreach ($rule in @($result.rules)) {
        if (-not $rule.parameters) { continue }
        # REST が付加する既定値だけを除く。既定と異なる値は差分に残す。
        $parameters = $rule.parameters
        $defaults = if ($rule.type -eq 'pull_request') {
            @{
                required_reviewers = @()
                dismissal_restriction = @{ enabled = $false; allowed_actors = @() }
                require_extra_approval_for_unattributed_changes = $true
            }
        } elseif ($rule.type -eq 'required_status_checks') {
            @{ do_not_enforce_on_create = $false }
        } else { @{} }
        foreach ($key in $defaults.Keys) {
            if ($parameters.ContainsKey($key) -and
                (ConvertTo-AuditCanonicalJson $parameters[$key]) -ceq (ConvertTo-AuditCanonicalJson $defaults[$key])) {
                $parameters.Remove($key)
            }
        }
        foreach ($check in @($parameters.required_status_checks)) {
            # null は App「指定なし」と同じ意味。全チェックの App 固定は比較に残す。
            if ($check -and $check.ContainsKey('integration_id') -and $null -eq $check.integration_id) {
                $check.Remove('integration_id')
            }
        }
    }
    return $result
}

function Compare-AuditRuleset {
    param([hashtable]$Expected, [hashtable]$Actual)
    $left = Select-AuditRuleset ($Expected | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable)
    $right = Select-AuditRuleset ($Actual | ConvertTo-Json -Depth 50 | ConvertFrom-Json -AsHashtable)
    $differences = foreach ($key in @($left.Keys + $right.Keys | Sort-Object -Unique)) {
        $a = ConvertTo-AuditCanonicalJson $left[$key]
        $b = ConvertTo-AuditCanonicalJson $right[$key]
        if ($a -cne $b) { "${key}: 期待=$a / 実際=$b" }
    }
    return @($differences)
}

function Compare-AuditLabels {
    param([array]$Expected, [array]$Actual, [string[]]$AllowedExtra)
    foreach ($label in $Expected) {
        $found = @($Actual | Where-Object { $_.name -ceq $label.name })
        if ($found.Count -eq 0) { "無い: $($label.name)"; continue }
        if ($found[0].color -ine $label.color -or $found[0].description -cne $label.description) {
            "色または説明が異なる: $($label.name)"
        }
    }
    foreach ($label in $Actual) {
        if ($label.name -cnotin @($Expected.name) -and $label.name -cnotin $AllowedExtra) {
            "未許可の追加ラベル: $($label.name)"
        }
    }
}

function Get-AuditFileSection {
    param([string]$Path, [AllowEmptyString()][string]$Text)
    $textLf = $Text.Replace("`r`n", "`n")
    if ($Path -eq '.editorconfig') {
        $sections = [regex]::Matches($textLf, '(?m)^\[\*\.cs\][ \t]*\n(?:(?!^\[)[\s\S])*')
        return ($sections | ForEach-Object { $_.Value.TrimEnd() }) -join "`n"
    }
    if ($Path -eq 'Directory.Build.props') {
        $xml = [xml]::new()
        $xml.XmlResolver = $null
        $xml.LoadXml($Text.TrimStart([char]0xFEFF))
        # 条件付き・重複の定義も捨てずに比較する。
        return ($xml.SelectNodes("//*[local-name()='EnforceCodeStyleInBuild']") |
            ForEach-Object {
                'value=' + $_.InnerText.Trim() + ' condition=' + $_.GetAttribute('Condition') +
                    ' parent-condition=' + $_.ParentNode.GetAttribute('Condition')
            }) -join "`n"
    }
    return $textLf
}

function Get-AuditLineHash {
    param([string]$Text)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).Substring(0, 12)
}

function Compare-AuditFile {
    param(
        [string]$Path, [AllowEmptyString()][string]$Expected,
        [AllowEmptyString()][string]$Actual, [hashtable]$Replacements
    )
    $left = Get-AuditFileSection $Path $Expected
    $right = Get-AuditFileSection $Path $Actual
    if ($left -ceq $right) { return @{ status = 'exact'; details = @('完全一致（改行コードは正規化）') } }
    $normalized = $right
    foreach ($key in @($Replacements.Keys | Sort-Object Length -Descending)) {
        $normalized = $normalized.Replace($key, [string]$Replacements[$key])
    }
    if ($left -ceq $normalized) { return @{ status = 'normalized'; details = @('宣言したアプリ名の置換で一致') } }
    $a = $left -split "`n"
    $b = $normalized -split "`n"
    $preview = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt [Math]::Max($a.Count, $b.Count) -and $preview.Count -lt 6; $i++) {
        $la = if ($i -lt $a.Count) { $a[$i] } else { '' }
        $lb = if ($i -lt $b.Count) { $b[$i] } else { '' }
        if ($la -cne $lb) {
            # ファイルに誤って認証情報が書かれていても、本文は一切出力しない。
            $preview.Add("L$($i + 1): -sha256:$(Get-AuditLineHash $la) +sha256:$(Get-AuditLineHash $lb)")
        }
    }
    if (-not $preview.Count) { $preview.Add("末尾改行・空行数の差: 期待=$($a.Count) 行 / 実際=$($b.Count) 行") }
    return @{ status = 'different'; details = @($preview) }
}

function Get-AuditCoreReferences {
    param([hashtable]$Files)
    $documents = @{}
    foreach ($path in $Files.Keys) {
        $xml = [xml]::new()
        $xml.XmlResolver = $null
        $xml.LoadXml($Files[$path].TrimStart([char]0xFEFF))
        $documents[$path] = $xml
    }
    foreach ($path in @($Files.Keys | Where-Object { $_ -like '*.csproj' } | Sort-Object)) {
        $xml = $documents[$path]
        $ancestors = [Collections.Generic.List[string]]::new()
        $directory = ($path -replace '/[^/]+$', '')
        if ($directory -eq $path) { $directory = '' }
        while ($true) {
            $prefix = if ($directory) { "$directory/" } else { '' }
            $ancestors.Insert(0, $prefix)
            if (-not $directory) { break }
            $directory = if ($directory.Contains('/')) { $directory.Substring(0, $directory.LastIndexOf('/')) } else { '' }
        }
        $properties = @{}
        $central = $null
        foreach ($name in 'Directory.Build.props', 'Directory.Packages.props') {
            # MSBuild は最寄りの同名ファイルだけを自動で読む（親へは明示的な import が必要）。
            $candidate = @($ancestors | Where-Object { $documents.ContainsKey("$_$name") } | Select-Object -Last 1)
            if (-not $candidate.Count) { continue }
            $doc = $documents["$($candidate[0])$name"]
            if ($name -eq 'Directory.Packages.props') { $central = $doc }
            foreach ($property in $doc.SelectNodes("/*/*[local-name()='PropertyGroup']/*")) {
                $properties[$property.LocalName] = if ($property.GetAttribute('Condition') -or
                    $property.ParentNode.GetAttribute('Condition')) { '' } else { $property.InnerText }
            }
        }
        foreach ($property in $xml.SelectNodes("/*/*[local-name()='PropertyGroup']/*")) {
            $properties[$property.LocalName] = if ($property.GetAttribute('Condition') -or
                $property.ParentNode.GetAttribute('Condition')) { '' } else { $property.InnerText }
        }
        foreach ($reference in $xml.SelectNodes("//*[local-name()='PackageReference']")) {
            $id = $reference.GetAttribute('Include')
            if (-not $id) { $id = $reference.GetAttribute('Update') }
            if ($id -notmatch '^VehicleVision\.PleasanterTools\.Core\.(Data|Mail|Web)$') { continue }
            $version = $reference.GetAttribute('VersionOverride')
            if (-not $version) { $version = $reference.GetAttribute('Version') }
            if (-not $version) {
                $child = $reference.SelectSingleNode("*[local-name()='VersionOverride' or local-name()='Version']")
                if ($child) { $version = $child.InnerText }
            }
            if (-not $version -and $central) {
                $entries = @($central.SelectNodes("//*[local-name()='PackageVersion']") |
                    Where-Object { $_.GetAttribute('Include') -ieq $id -or $_.GetAttribute('Update') -ieq $id })
                if ($entries.Count -eq 1 -and -not $entries[0].GetAttribute('Condition') -and
                    -not $entries[0].ParentNode.GetAttribute('Condition')) {
                    $version = $entries[0].GetAttribute('Version')
                    if (-not $version) {
                        $child = $entries[0].SelectSingleNode("*[local-name()='Version']")
                        if ($child) { $version = $child.InnerText }
                    }
                }
            }
            for ($i = 0; $i -lt 10 -and $version -match '\$\(([^)]+)\)'; $i++) {
                $key = $Matches[1]
                if (-not $properties.ContainsKey($key) -or -not $properties[$key]) { break }
                $version = $version.Replace("`$($key)", $properties[$key])
            }
            $parsed = $null
            $resolved = [System.Management.Automation.SemanticVersion]::TryParse($version.ToLowerInvariant(), [ref]$parsed)
            [pscustomobject]@{ path = $path; package = $id; version = $version; resolved = $resolved }
        }
    }
}

function Get-AuditLatestVersion {
    param([string[]]$Versions)
    $versionsParsed = foreach ($version in $Versions) {
        [System.Management.Automation.SemanticVersion]::Parse($version.ToLowerInvariant())
    }
    return [string]($versionsParsed | Sort-Object -Descending | Select-Object -First 1)
}

function Get-AuditHttpState {
    param([int]$Status, [switch]$KnownReadable, [switch]$MissingAllowed)
    if ($Status -ge 200 -and $Status -lt 300) { return 'ok' }
    if ($Status -eq 404 -and $KnownReadable -and $MissingAllowed) { return 'missing' }
    if ($Status -in 401, 403, 404) { return 'unknown' }
    return 'error'
}

function Get-AuditActionPresence {
    param([string]$Name, [hashtable]$RepositoryNames, [hashtable]$OrganizationNames)
    foreach ($source in @(
        @{ response = $RepositoryNames; scope = 'repository' },
        @{ response = $OrganizationNames; scope = 'organization' }
    )) {
        if ($source.response -and $source.response.status -eq 200 -and $Name -cin $source.response.data) {
            return @{ status = 'ok'; details = @("名前の存在のみ / scope=$($source.scope)") }
        }
    }
    if ($RepositoryNames.status -eq 200 -and $OrganizationNames.status -eq 200) {
        return @{ status = 'missing'; details = @('repository と継承 organization の両方に名前が無い') }
    }
    $code = @($RepositoryNames, $OrganizationNames | Where-Object { $_ -and $_.status -ne 200 })[0].status
    $state = Get-AuditHttpState $code
    $message = if ($state -eq 'unknown') { '権限が足りず確認できない（欠落とは断定しない）' } else { '取得エラー' }
    return @{ status = $state; details = @("$message / HTTP $code") }
}

function Get-AuditExitCode {
    param([array]$Results)
    if (@($Results | Where-Object { $_.status -in 'different', 'missing', 'unknown', 'error' }).Count) { return 1 }
    return 0
}

function Invoke-AuditGet {
    param([string]$Endpoint, [string]$Projection = '', [switch]$PublicFallback)
    # gh の標準エラー（認証や応答本文を含み得る）は保存・表示せず、HTTP コードだけを取り出す。
    $start = [Diagnostics.ProcessStartInfo]::new('gh')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8
    $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.Environment.Remove('GH_DEBUG') | Out-Null
    foreach ($argument in @('api', '--method', 'GET', $Endpoint, '-H', 'X-GitHub-Api-Version: 2022-11-28')) {
        $start.ArgumentList.Add($argument)
    }
    if ($Projection) {
        $start.ArgumentList.Add('--jq')
        $start.ArgumentList.Add($Projection)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(90000)) {
            $process.Kill($true)
            $process.WaitForExit()
            return @{ status = 0; data = $null }
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -eq 0) {
            $data = if ($output.Trim()) { ConvertFrom-Json -InputObject $output -AsHashtable -NoEnumerate } else { $null }
            return @{ status = 200; data = $data }
        }
        $status = if ($errorText -match '(?i)rate limit') { 429 }
            elseif ($errorText -match 'HTTP (\d{3})') { [int]$Matches[1] } else { 0 }
    } finally { $process.Dispose() }
    if ($PublicFallback -and $status -in 0, 401, 403) {
        return Invoke-AuditPublicGet "https://api.github.com/$Endpoint"
    }
    return @{ status = $status; data = $null }
}

function Invoke-AuditPublicGet {
    param([string]$Uri)
    try {
        $response = Invoke-WebRequest -Uri $Uri -Method Get -SkipHttpErrorCheck -TimeoutSec 60
        $data = if ([int]$response.StatusCode -eq 200) {
            ConvertFrom-Json -InputObject $response.Content -AsHashtable -NoEnumerate
        } else { $null }
        return @{ status = [int]$response.StatusCode; data = $data }
    } catch {
        # 例外には URL や HTTP 本文を含めず、呼び出し側が「取得エラー」を必ず記録する。
        return @{ status = 0; data = $null }
    }
}

function Get-AuditPages {
    param([string]$Endpoint, [string]$Collection = '', [switch]$NamesOnly, [switch]$PublicFallback)
    $all = [Collections.Generic.List[object]]::new()
    $separator = if ($Endpoint.Contains('?')) { '&' } else { '?' }
    $pageSize = if ($Collection -eq 'variables') { 30 } else { 100 }
    for ($page = 1; ; $page++) {
        # variables は API が値も返すため、gh 内で名前だけに射影する。監査プロセスは値を受け取らない。
        $projection = if ($NamesOnly) { "{items: [.$Collection[].name]}" } else { '' }
        $response = Invoke-AuditGet "${Endpoint}${separator}per_page=$pageSize&page=$page" $projection -PublicFallback:$PublicFallback
        if ($response.status -ne 200) { return $response }
        $items = if ($NamesOnly) { @($response.data.items) } elseif ($Collection) {
            @($response.data[$Collection])
        } else { @($response.data) }
        foreach ($item in $items) { $all.Add($item) }
        if ($items.Count -lt $pageSize) { break }
    }
    return @{ status = 200; data = $all.ToArray() }
}

function Get-AuditBlob {
    param([string]$Repository, [string]$Sha, [hashtable]$Cache, [switch]$PublicFallback)
    if ($Cache.ContainsKey($Sha)) { return $Cache[$Sha] }
    $response = Invoke-AuditGet "repos/$Repository/git/blobs/$Sha" -PublicFallback:$PublicFallback
    if ($response.status -eq 200) {
        if ($response.data.encoding -ne 'base64') { return @{ status = 0; data = $null } }
        $response.data = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($response.data.content))
        $Cache[$Sha] = $response
    }
    return $response
}

function Get-AuditNuGetVersions {
    param([string[]]$Packages)
    $service = Invoke-AuditPublicGet 'https://api.nuget.org/v3/index.json'
    $base = @($service.data.resources | Where-Object { $_.'@type' -eq 'RegistrationsBaseUrl/3.6.0' })[0].'@id'
    $result = @{}
    foreach ($package in $Packages) {
        if (-not $base) { $result[$package] = @{ status = 'error'; latest = '' }; continue }
        $response = Invoke-AuditPublicGet "$base$($package.ToLowerInvariant())/index.json"
        $versions = [Collections.Generic.List[string]]::new()
        $failed = $response.status -ne 200
        foreach ($page in @($response.data.items)) {
            $entries = $page.items
            if (-not $entries) {
                $pageResponse = Invoke-AuditPublicGet $page.'@id'
                if ($pageResponse.status -ne 200) { $failed = $true; continue }
                $entries = $pageResponse.data.items
            }
            foreach ($entry in $entries) {
                if ($entry.catalogEntry.listed -ne $false) { $versions.Add($entry.catalogEntry.version) }
            }
        }
        $result[$package] = if ($failed -or $versions.Count -eq 0) {
            @{ status = 'error'; latest = '' }
        } else { @{ status = 'ok'; latest = Get-AuditLatestVersion $versions.ToArray() } }
    }
    return $result
}

function ConvertTo-AuditMarkdown {
    param([hashtable]$Report)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('# リポジトリ設定監査')
    $lines.Add('')
    $lines.Add("実行日時: $($Report.generatedAt) / 基準: ローカル Template $($Report.baselineCommit)")
    $lines.Add('')
    $categories = @('設定', 'ルールセット', 'ラベル', 'Actions', 'develop 共通', 'master 共通', 'ライセンス', '依存')
    $lines.Add('| リポジトリ | ' + ($categories -join ' | ') + ' |')
    $lines.Add('| --- | ' + (($categories | ForEach-Object { '---' }) -join ' | ') + ' |')
    foreach ($repo in $Report.repositories) {
        $cells = foreach ($category in $categories) {
            $rows = @($Report.results | Where-Object { $_.repository -eq $repo -and $_.category -eq $category })
            if (@($rows | Where-Object { $_.status -in 'different', 'missing', 'error' }).Count) { '✗' }
            elseif (@($rows | Where-Object status -eq 'unknown').Count) { '確認不可' }
            elseif (@($rows | Where-Object { $_.status -ne 'notApplicable' }).Count) { '✓' }
            else { '対象外' }
        }
        $lines.Add('| ' + $repo + ' | ' + ($cells -join ' | ') + ' |')
    }
    $lines.Add('')
    $lines.Add('✓: 一致（置換で一致を含む）、✗: 差あり・無い・取得エラー。確認不可も終了コード 1。')
    $lines.Add('')
    $lines.Add('## 項目別の結果')
    $lines.Add('')
    $lines.Add('| リポジトリ | 項目 | 状態 | 詳細 |')
    $lines.Add('| --- | --- | --- | --- |')
    $labels = @{
        exact = '✓ 完全一致'; normalized = '✓ 置換で一致'; ok = '✓'
        different = '✗ 差あり'; missing = '✗ 無い'; notApplicable = '対象外'
        unknown = '確認不可'; error = '✗ 取得エラー'
    }
    foreach ($row in $Report.results) {
        $detail = (@($row.details) -join '; ').Replace('|', '\|').Replace("`r", '').Replace("`n", '<br>')
        $lines.Add("| $($row.repository) | $($row.category): $($row.item) | $($labels[$row.status]) | $detail |")
    }
    $lines.Add('')
    $lines.Add('## 明示的に除外したリポジトリ')
    $lines.Add('')
    foreach ($key in @($Report.excludedRepositories.Keys | Sort-Object)) {
        $lines.Add("- ${key}: $($Report.excludedRepositories[$key])")
    }
    return $lines -join "`n"
}

# ---- ライセンス文書（正本との比較。設計は _documents/ライセンス.md の「正本の使い方」） ----

function ConvertTo-AuditLicenseSections {
    # 節マーカーの内側を取り除いた骨格と、節（名前・ID・本文）の一覧を返す。形式の誤りは errors に集める。
    param([AllowEmptyString()][string]$Text)
    $begin = '^(?<indent> *)(?<open><!--|//) license:begin (?<name>[a-z][a-z-]*)=(?<id>[a-z][a-z,-]*)(?<close> -->)?$'
    $end = '^(?<indent> *)(?<open><!--|//) license:end (?<name>[a-z][a-z-]*)(?<close> -->)?$'
    $skeleton = [Collections.Generic.List[string]]::new()
    $sections = [Collections.Generic.List[object]]::new()
    $errors = [Collections.Generic.List[string]]::new()
    $current = $null
    $lines = $Text.Replace("`r`n", "`n").Split("`n")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $isBegin = $line -match $begin -and (($Matches.open -eq '<!--') -eq [bool]$Matches.close)
        if ($isBegin) {
            $m = $Matches
            if ($current) { $errors.Add("節 $($current.name) の中に別の節が始まる (L$($i + 1))"); continue }
            $current = @{ name = $m.name; id = $m.id; body = [Collections.Generic.List[string]]::new() }
            $skeleton.Add("$($m.indent)$($m.open) license:begin $($m.name)$($m.close)")
            continue
        }
        $isEnd = $line -match $end -and (($Matches.open -eq '<!--') -eq [bool]$Matches.close)
        if ($isEnd) {
            $m = $Matches
            if (-not $current -or $current.name -cne $m.name) {
                $errors.Add("対応する開始の無い終了マーカー $($m.name) (L$($i + 1))")
                continue
            }
            $sections.Add(@{ name = $current.name; id = $current.id; body = ($current.body -join "`n") })
            $current = $null
            $skeleton.Add($line)
            continue
        }
        if ($line -match 'license:(begin|end)\b') {
            $errors.Add("節マーカーの形式が不正 (L$($i + 1))")
        } elseif ($current) {
            $current.body.Add($line)
        } else {
            $skeleton.Add($line)
        }
    }
    if ($current) { $errors.Add("節 $($current.name) が閉じていない") }
    return @{ skeleton = ($skeleton -join "`n"); sections = $sections.ToArray(); errors = $errors.ToArray() }
}

function Get-AuditMarkdownFences {
    # 見出し（直近の # 行）つきで、行頭のコードフェンスの中身を取り出す。
    param([string]$Text)
    $heading = ''
    $fence = $null
    foreach ($line in $Text.Replace("`r`n", "`n").Split("`n")) {
        if ($null -ne $fence) {
            if ($line -ceq '```') {
                [pscustomobject]@{ heading = $fence.heading; body = ($fence.lines -join "`n") }
                $fence = $null
            } else { $fence.lines.Add($line) }
        } elseif ($line -match '^```') {
            $fence = @{ heading = $heading; lines = [Collections.Generic.List[string]]::new() }
        } elseif ($line -match '^#{1,6} (.+)$') { $heading = $Matches[1] }
    }
}

function Get-AuditLicenseParts {
    # 部品集（デュアル）と、トリプル正本の「部品」の節から、"節名=ID" ごとの本文を作る。
    param([string]$PartsText, [string]$TripleText)
    $collect = {
        param([string]$Text)
        $found = @{}
        foreach ($fence in Get-AuditMarkdownFences $Text) {
            foreach ($section in (ConvertTo-AuditLicenseSections $fence.body).sections) {
                $found["$($section.name)=$($section.id)"] = $section.body
            }
        }
        return $found
    }
    $dual = & $collect $PartsText
    $tripleParts = $TripleText -replace '(?s)^.*?\n## 部品（トリプルで違うもの）\n', '' -replace '(?s)\n## 全文\n.*$', ''
    $triple = @{}
    foreach ($key in $dual.Keys) { $triple[$key] = $dual[$key] }
    $own = & $collect $tripleParts
    # トリプルが定義する節は、デュアルの ID を引き継がず、トリプルの定義だけにする。
    foreach ($name in @($own.Keys | ForEach-Object { $_.Split('=')[0] } | Sort-Object -Unique)) {
        foreach ($key in @($triple.Keys | Where-Object { $_.StartsWith("$name=") })) { $triple.Remove($key) }
    }
    foreach ($key in $own.Keys) { $triple[$key] = $own[$key] }
    # トリプルで選べるのは「off のみ」の節（本文はデュアルの off と同じ）。
    foreach ($name in 'sni-scope', 'sni-terms', 'core-terms') {
        foreach ($key in @($triple.Keys | Where-Object { $_.StartsWith("$name=") -and $_ -cne "$name=off" })) {
            $triple.Remove($key)
        }
    }
    return @{ dual = $dual; triple = $triple }
}

function Get-AuditLicenseSlice {
    # ファイルのうち、正本が受け持つ範囲だけを取り出す。無ければ $null。
    param([string]$Path, [AllowEmptyString()][string]$Text)
    $lf = $Text.TrimStart([char]0xFEFF).Replace("`r`n", "`n")
    switch ($Path) {
        'NOTICE' { return (($lf.Split("`n") | Select-Object -First 3) -join "`n") }
        '.github/cla/signed.yml' {
            $m = [regex]::Match($lf, '(?m)^signatories:')
            if (-not $m.Success) { return $null }
            return $lf.Substring(0, $m.Index).TrimEnd()
        }
        'CONTRIBUTING.md' {
            $m = [regex]::Match($lf, '(?ms)^## CLA へ署名する[ \t]*\n.*?(?=^## |\z)')
            if (-not $m.Success) { return $null }
            return $m.Value.TrimEnd()
        }
        default { return $lf.TrimEnd() }
    }
}

function Get-AuditLicenseBaselines {
    # Template の作業ツリーから、デュアルとトリプルの比較元（パス -> 範囲の本文）と部品を作る。
    param([string]$Root, [string[]]$Paths)
    $read = { param($p) Get-Content -LiteralPath (Join-Path $Root $p) -Raw -Encoding utf8 }
    $dual = @{}
    foreach ($path in $Paths) { $dual[$path] = Get-AuditLicenseSlice $path (& $read $path) }
    $tripleText = & $read '_documents/ライセンス正本-トリプル.md'
    $byHeading = @{}
    foreach ($fence in Get-AuditMarkdownFences $tripleText) { $byHeading[$fence.heading] = $fence.body }
    $triple = @{}
    foreach ($path in $Paths) { $triple[$path] = $dual[$path] }
    foreach ($pair in @(
        @('LICENSING.md', 'LICENSING.md'), @('LICENSE-COMMERCIAL.md', 'LICENSE-COMMERCIAL.md'), @('CLA.md', 'CLA.md'),
        @('CONTRIBUTING.md', 'CONTRIBUTING.md の CLA 節'), @('NOTICE', 'NOTICE の冒頭 3 行')
    )) {
        if ($pair[0] -cnotin $Paths) { continue }
        if (-not $byHeading.ContainsKey($pair[1])) { throw "トリプル正本に見出しが無い: $($pair[1])" }
        $triple[$pair[0]] = Get-AuditLicenseSlice $pair[0] $byHeading[$pair[1]]
    }
    $parts = Get-AuditLicenseParts (& $read '_documents/ライセンス部品.md') $tripleText
    return @{ dual = $dual; triple = $triple; parts = $parts }
}

function Get-AuditLicensePattern {
    # 部品の〈…〉（手で埋める箇所）を、1 文字以上に当たる穴として扱う。
    param([string]$Body)
    $pieces = [regex]::Split($Body, '〈[^〉\n]*〉') | ForEach-Object { [regex]::Escape($_) }
    return '^' + ($pieces -join '[\s\S]+?') + '$'
}

function Test-AuditLicenseSections {
    # 節の ID が有効で、本文が部品集と一致する（free は除く）かを確かめる。メッセージの配列を返す。
    param([array]$Sections, [string]$Path, [hashtable]$Parts, [string]$Tier, [hashtable]$Replacements)
    $messages = [Collections.Generic.List[string]]::new()
    $order = 'api', 'db-read', 'db-same', 'db-convert', 'config-write'
    foreach ($section in $Sections) {
        $name = $section.name
        $tag = "${Path}: $name=$($section.id)"
        if ($section.body -match '[〈〉]') { $messages.Add("$tag 〈 〉 が残っている") }
        if ($name -ceq 'cla-intro' -and $section.id -cne $Tier) { $messages.Add("$tag 提供構成($Tier)と違う ID") }
        if ($section.id -ceq 'free') {
            if ($name -cnotin 'pleasanter-note', 'deps-note') { $messages.Add("$tag free は使えない節") }
            continue
        }
        $ids = if ($name -ceq 'connection') { $section.id.Split(',') } else { @($section.id) }
        $known = $true
        foreach ($id in $ids) {
            if (-not $Parts.ContainsKey("$name=$id")) { $known = $false; $messages.Add("$tag 部品集に無い ID: $id") }
        }
        if (-not $known) { continue }
        if ($name -ceq 'connection' -and $ids.Count -gt 1) {
            $indexes = @($ids | ForEach-Object { [array]::IndexOf($order, $_) })
            $sorted = @($indexes | Sort-Object)
            if (@($indexes | Where-Object { $_ -lt 0 }).Count -or (($indexes -join ',') -cne ($sorted -join ',')) -or
                @($ids | Sort-Object -Unique).Count -ne $ids.Count) {
                $messages.Add("$tag 組み合わせは api, db-read, db-same, db-convert, config-write の順（api-only・none は単独）")
                continue
            }
        }
        $expected = ($ids | ForEach-Object { $Parts["$name=$_"] }) -join "`n"
        $actual = $section.body
        foreach ($key in @($Replacements.Keys | Sort-Object Length -Descending)) {
            $actual = $actual.Replace($key, [string]$Replacements[$key])
        }
        if ($actual -cne $expected -and $actual -cnotmatch (Get-AuditLicensePattern $expected)) {
            $messages.Add("$tag 本文が部品集と一致しない（マーカーの内側を直接書き換えない）")
        }
    }
    return @($messages)
}

function Test-AuditLicenseConsistency {
    # 同じ印の節（sni / core / reference）と usage・independence の ID が、ファイルをまたいで食い違わないか。
    param([array]$Licensing, [array]$Commercial)
    $messages = [Collections.Generic.List[string]]::new()
    $idOf = { param($list, $name) @($list | Where-Object { $_.name -ceq $name })[0].id }
    foreach ($pair in @(@('sni', 'sni-scope'), @('sni', 'sni-terms'), @('reference', 'reference-scope'))) {
        $a = & $idOf $Licensing $pair[0]
        $b = & $idOf $Commercial $pair[1]
        if ($a -and $b -and $a -cne $b) { $messages.Add("$($pair[0])=$a と $($pair[1])=$b が食い違う") }
    }
    $core = & $idOf $Licensing 'core'
    $coreTerms = & $idOf $Commercial 'core-terms'
    if ($core -and $coreTerms) {
        $want = if ($core -cin 'on', 'library') { $core } else { 'off' }
        if ($want -cne $coreTerms) { $messages.Add("core=$core と core-terms=$coreTerms が食い違う") }
    }
    $usage = & $idOf $Licensing 'usage'
    $independence = & $idOf $Licensing 'independence'
    if ($usage -and $independence) {
        $want = if ($usage -ceq 'library') { 'library' } else { 'app' }
        if ($independence -cne $want) { $messages.Add("usage=$usage には independence=$want を選ぶ（実際は $independence）") }
    }
    return @($messages)
}

function Compare-AuditLicenseFile {
    # 正本の範囲を、節マーカーの内側を除いて比較し、マーカーの ID・本文も確かめる。
    param(
        [string]$Path, [AllowEmptyString()][string]$Expected, [AllowEmptyString()][string]$Actual,
        [hashtable]$Replacements, [hashtable]$Parts, [string]$Tier
    )
    $slice = Get-AuditLicenseSlice $Path $Actual
    if ($null -eq $slice) {
        return @{ status = 'different'; details = @('正本が受け持つ範囲（節・見出し・signatories）が見つからない'); sections = @() }
    }
    $hasMarkers = $Path -in 'LICENSING.md', 'LICENSE-COMMERCIAL.md', '.github/workflows/cla-check.yml'
    $left = $Expected
    $right = $slice
    $sections = @()
    $messages = @()
    if ($hasMarkers) {
        $r = ConvertTo-AuditLicenseSections $slice
        $left = (ConvertTo-AuditLicenseSections $Expected).skeleton
        $right = $r.skeleton
        $sections = $r.sections
        $messages = @($r.errors)
        if (-not $messages.Count) { $messages = @(Test-AuditLicenseSections $sections $Path $Parts $Tier $Replacements) }
    }
    if ($Path -ceq '.github/cla/signed.yml' -and $right.StartsWith($left + "`n", [StringComparison]::Ordinal)) {
        # 署名の記録に付く注記（初期記録の根拠など）は、先頭の注記の後ろの # 行に限り許す。
        $extra = $right.Substring($left.Length + 1).Split("`n")
        if (@($extra | Where-Object { $_ -cnotmatch '^(#.*)?$' }).Count -eq 0) { $right = $left }
    }
    $comparison = Compare-AuditFile 'license' $left $right $Replacements
    $status = if ($comparison.status -eq 'different' -or $messages.Count) { 'different' } else { $comparison.status }
    $details = [Collections.Generic.List[string]]::new()
    if ($comparison.status -eq 'different' -or -not $hasMarkers) { foreach ($d in $comparison.details) { $details.Add($d) } }
    elseif (-not $messages.Count) { $details.Add('節マーカーの外側は一致し、ID と本文も部品集と一致') }
    foreach ($m in $messages) { $details.Add($m) }
    return @{ status = $status; details = $details.ToArray(); sections = $sections }
}

Export-ModuleMember -Function *-Audit*, Get-AuditCoreReferences
