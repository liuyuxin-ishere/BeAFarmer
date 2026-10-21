# M1 配置导出适配层。
# 正式入口参数：-RepoRoot -SourceDir -ExporterPath -ConfigVersion。
# PoC 旧入口：-Legacy，或在参数中带上 --xlsx / --set-global，转发给 Tools/gen_config.py。
# 不带上述参数时失败，避免把旧 Python 工具当成 M1 默认导表。
[CmdletBinding(PositionalBinding = $false)]
param(
    # 完整 Unity 工程根。相对的 SourceDir / ExporterPath 相对此目录解析，不使用当前工作目录。
    [string]$RepoRoot,
    # 含 luban.conf 的源根。其下使用 Datas 与 Defines。不是设计参考工作簿。
    [string]$SourceDir,
    # Luban 导出器。.dll 用 dotnet 启动，.exe 直接启动。
    [string]$ExporterPath,
    # 写入清单的配置版本。必须是正整数。小于 101 时拒绝键 m1_patch_message。
    [int]$ConfigVersion,
    # 显式使用旧 Python 导表，供 PoC。
    [switch]$Legacy,
    # 转发给 gen_config.py 的剩余参数，例如 --xlsx 与 --set-global。
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
)

$ErrorActionPreference = 'Stop'
if (Get-Variable -Name PSNativeCommandUseErrorActionPreference -Scope Global -ErrorAction SilentlyContinue) {
    $PSNativeCommandUseErrorActionPreference = $false
}

$script:ExitCode = 0
$script:StagingPath = $null
$script:PublishStarted = $false
$script:PreserveStaging = $false
$script:Targets = @()

<#
.SYNOPSIS
记录失败码并中断当前导出。
.PARAMETER Message
写到标准错误的原因。
.PARAMETER Code
进程退出码，非 0。
#>
function Stop-GenConfig {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [Parameter(Mandatory = $true)][int]$Code
    )
    if ($script:ExitCode -eq 0) { $script:ExitCode = $Code }
    throw $Message
}

<#
.SYNOPSIS
把路径变成绝对路径，并确认它位于给定根目录之内。
.PARAMETER Path
待检查的文件或目录。
.PARAMETER Root
允许的根目录。
#>
function Resolve-UnderWorkspace {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )
    $full = [System.IO.Path]::GetFullPath($Path)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\')
    $prefix = $rootFull + '\'
    $same = [string]::Equals($full.TrimEnd('\'), $rootFull, [System.StringComparison]::OrdinalIgnoreCase)
    $inside = $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $same -and -not $inside) {
        Stop-GenConfig -Message "拒绝操作工作区外路径: $full" -Code 5
    }
    return $full
}

<#
.SYNOPSIS
删除一个已确认位于工程 Temp\gen_config 下的本次暂存目录。
.PARAMETER Path
本次暂存目录的绝对路径。
.PARAMETER RepoRoot
工程根。
#>
function Remove-OwnStaging {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    $full = Resolve-UnderWorkspace -Path $Path -Root $RepoRoot
    $gate = [System.IO.Path]::GetFullPath((Join-Path $RepoRoot 'Temp\gen_config')).TrimEnd('\')
    $prefix = $gate + '\'
    if (-not $full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-GenConfig -Message "拒绝递归删除暂存根以外的目录: $full" -Code 5
    }
    if ([string]::Equals([System.IO.Path]::GetFileName($full.TrimEnd('\')), 'gen_config', [System.StringComparison]::OrdinalIgnoreCase)) {
        Stop-GenConfig -Message "拒绝删除暂存根目录本身: $full" -Code 5
    }
    if (Test-Path -LiteralPath $full) {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}

<#
.SYNOPSIS
删除发布目录中的单个已拥有文件。调用前必须已经解析到工程根内。
.PARAMETER Path
文件绝对路径。
.PARAMETER RepoRoot
工程根。
#>
function Remove-OwnedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    $full = Resolve-UnderWorkspace -Path $Path -Root $RepoRoot
    if (Test-Path -LiteralPath $full -PathType Container) {
        Stop-GenConfig -Message "拒绝删除目录: $full" -Code 5
    }
    if (Test-Path -LiteralPath $full) {
        $item = Get-Item -LiteralPath $full
        if ($item.IsReadOnly) { $item.IsReadOnly = $false }
        Remove-Item -LiteralPath $full -Force
    }
}

<#
.SYNOPSIS
相对路径按工程根解析；已是绝对路径则只规范化。
.PARAMETER Path
输入路径，可以为空。
.PARAMETER RepoRoot
工程根。
#>
function Resolve-RepoPath {
    param(
        [string]$Path,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Path))
}

<#
.SYNOPSIS
计算文件 SHA256，返回小写十六进制。
.PARAMETER Path
已存在的文件。
#>
function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $hash = $sha.ComputeHash($stream)
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
    $chars = New-Object System.Text.StringBuilder
    foreach ($b in $hash) { [void]$chars.Append($b.ToString('x2')) }
    return $chars.ToString()
}

<#
.SYNOPSIS
读取 JSON，保留单元素数组。
.PARAMETER Path
JSON 文件。
#>
function Read-JsonDocument {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not ('GenConfigJsonSerializer' -as [type])) {
        Add-Type -AssemblyName System.Web.Extensions
        Add-Type -ReferencedAssemblies System.Web.Extensions -TypeDefinition @'
public class GenConfigJsonSerializer {
    public static object Parse(string text) {
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        serializer.MaxJsonLength = 67108864;
        return serializer.DeserializeObject(text);
    }
}
'@
    }
    $text = [System.IO.File]::ReadAllText($Path)
    return [GenConfigJsonSerializer]::Parse($text)
}

<#
.SYNOPSIS
列出目录下一层文件。目录不存在或没有文件时返回空列表，避免 @($null) 变成一个空元素。
.PARAMETER Path
目录路径。
#>
function Get-DirectoryFiles {
    param([Parameter(Mandatory = $true)][string]$Path)
    $list = New-Object System.Collections.Generic.List[System.IO.FileInfo]
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return $list }
    foreach ($full in [System.IO.Directory]::GetFiles($Path)) {
        $list.Add((New-Object System.IO.FileInfo $full))
    }
    return $list
}

<#
.SYNOPSIS
判断 JavaScriptSerializer 的字典是否含有字段。不用 Contains，避免 Dictionary 没有单参数重载。
.PARAMETER Object
JSON 对象。
.PARAMETER Name
字段名。
#>
function Test-JsonField {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Object -isnot [System.Collections.IDictionary]) { return $false }
    foreach ($key in $Object.Keys) {
        if ([string]$key -eq $Name) { return $true }
    }
    return $false
}

<#
.SYNOPSIS
把 Luban JSON 值规范成字符串，供键值扩展与强类型 Global 对照。
.PARAMETER Value
JavaScriptSerializer 读出的标量或数组。
#>
function ConvertTo-CanonicalConfigString {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [string]) { return $Value }
    if ($Value -is [bool]) {
        if ($Value) { return 'true' }
        return 'false'
    }
    if ($Value -is [System.Collections.IDictionary]) {
        Stop-GenConfig -Message 'Global 字段出现对象，当前字符串规范只接受标量和数组。' -Code 4
    }
    if (($Value -is [System.Collections.IEnumerable]) -and -not ($Value -is [string])) {
        $parts = New-Object System.Collections.Generic.List[string]
        foreach ($item in $Value) {
            $parts.Add((ConvertTo-CanonicalConfigString -Value $item))
        }
        return '[' + ($parts -join ',') + ']'
    }
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture
    $number = [System.Convert]::ToDecimal($Value, $invariant)
    $integral = [decimal]::Truncate($number)
    if ($number -eq $integral) {
        return [System.Convert]::ToInt64($integral).ToString($invariant)
    }
    $rounded = [decimal]::Round($number, 9, [System.MidpointRounding]::AwayFromZero)
    return $rounded.ToString('0.#########', $invariant)
}

<#
.SYNOPSIS
确认 tbglobalkv 覆盖强类型 Global 的全部字段，且 101 之前没有补丁键。
.PARAMETER DataDir
暂存 JSON 目录。
.PARAMETER ConfigVersion
本次配置版本。
#>
function Assert-GlobalKeyValues {
    param(
        [Parameter(Mandatory = $true)][string]$DataDir,
        [Parameter(Mandatory = $true)][int]$ConfigVersion
    )
    $globalPath = Join-Path $DataDir 'tbglobal.json'
    $kvPath = Join-Path $DataDir 'tbglobalkv.json'
    if (-not (Test-Path -LiteralPath $globalPath)) {
        Stop-GenConfig -Message "暂存数据缺少 tbglobal.json: $globalPath" -Code 4
    }
    if (-not (Test-Path -LiteralPath $kvPath)) {
        Stop-GenConfig -Message "暂存数据缺少 tbglobalkv.json: $kvPath" -Code 4
    }
    $globalRows = @(Read-JsonDocument -Path $globalPath)
    if ($globalRows.Count -ne 1) {
        Stop-GenConfig -Message "tbglobal.json 必须是单行，实际 $($globalRows.Count) 行。" -Code 4
    }
    $typed = $globalRows[0]
    if ($typed -isnot [System.Collections.IDictionary]) {
        Stop-GenConfig -Message 'tbglobal.json 的单行不是对象。' -Code 4
    }
    $kvRows = @(Read-JsonDocument -Path $kvPath)
    $map = @{}
    foreach ($row in $kvRows) {
        if ($row -isnot [System.Collections.IDictionary]) {
            Stop-GenConfig -Message 'tbglobalkv.json 含非对象行。' -Code 4
        }
        if (-not (Test-JsonField -Object $row -Name 'key') -or -not (Test-JsonField -Object $row -Name 'value')) {
            Stop-GenConfig -Message 'tbglobalkv.json 行缺少 key 或 value。' -Code 4
        }
        $key = [string]$row['key']
        $value = $row['value']
        if ($value -isnot [string]) {
            Stop-GenConfig -Message "键值扩展的值必须已是字符串: $key" -Code 4
        }
        if ($map.ContainsKey($key)) {
            Stop-GenConfig -Message "键值扩展出现重复键: $key" -Code 4
        }
        if ($key -eq 'm1_patch_message' -and $ConfigVersion -lt 101) {
            Stop-GenConfig -Message "ConfigVersion=$ConfigVersion 禁止键 m1_patch_message。该键只能在 101 补丁新增。" -Code 4
        }
        $map[$key] = $value
    }
    foreach ($field in @($typed.Keys)) {
        $expected = ConvertTo-CanonicalConfigString -Value $typed[$field]
        if (-not $map.ContainsKey([string]$field)) {
            Stop-GenConfig -Message "键值扩展缺少强类型 Global 字段: $field" -Code 4
        }
        if ($map[[string]$field] -cne $expected) {
            Stop-GenConfig -Message "键 $field 的字符串值 '$($map[[string]$field])' 与强类型规范值 '$expected' 不一致。" -Code 4
        }
    }
}

<#
.SYNOPSIS
为暂存 JSON 写清单。清单文件自己不进入 files，避免哈希自引用。
.PARAMETER DataDir
暂存 JSON 目录。
.PARAMETER ConfigVersion
配置版本。
#>
function Write-ConfigManifest {
    param(
        [Parameter(Mandatory = $true)][string]$DataDir,
        [Parameter(Mandatory = $true)][int]$ConfigVersion
    )
    $manifestPath = Join-Path $DataDir 'config_manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        Remove-OwnedFile -Path $manifestPath -RepoRoot $script:RepoRootFull
    }
    $items = New-Object System.Collections.Generic.List[object]
    $files = @(Get-DirectoryFiles -Path $DataDir | Sort-Object -Property Name)
    foreach ($file in $files) {
        if ($file.Extension -ne '.json') { continue }
        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        $items.Add([pscustomobject]@{
            Name = $file.Name
            Sha = (Get-Sha256Hex -Path $file.FullName)
            Length = [int64]$bytes.LongLength
        })
    }
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.AppendLine('{')
    [void]$builder.AppendLine('  "schema": "m1.config-manifest.v1",')
    [void]$builder.AppendLine("  `"configVersion`": $ConfigVersion,")
    [void]$builder.AppendLine('  "globals": {')
    [void]$builder.AppendLine('    "file": "tbglobalkv.json",')
    [void]$builder.AppendLine('    "format": "luban-map",')
    [void]$builder.AppendLine('    "keyField": "key",')
    [void]$builder.AppendLine('    "valueField": "value",')
    [void]$builder.AppendLine('    "valueType": "string",')
    [void]$builder.AppendLine('    "typedGlobalFile": "tbglobal.json",')
    [void]$builder.AppendLine('    "aotTypes": ["CloudIsle.LubanCfg.GlobalKv", "CloudIsle.LubanCfg.TbGlobalKv"],')
    [void]$builder.AppendLine('    "snapshotShape": "ConfigSnapshot(int Version, IReadOnlyDictionary<string,string> Globals)",')
    [void]$builder.AppendLine('    "keyGrowth": "add rows only; generated GlobalKv/TbGlobalKv types stay unchanged",')
    [void]$builder.AppendLine('    "forbiddenBefore101": ["m1_patch_message"]')
    [void]$builder.AppendLine('  },')
    [void]$builder.AppendLine('  "files": [')
    for ($i = 0; $i -lt $items.Count; $i++) {
        $item = $items[$i]
        $comma = ''
        if ($i -lt ($items.Count - 1)) { $comma = ',' }
        [void]$builder.AppendLine("    {`"name`": `"$($item.Name)`", `"sha256`": `"$($item.Sha)`", `"length`": $($item.Length)}$comma")
    }
    [void]$builder.AppendLine('  ]')
    [void]$builder.Append('}')
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($manifestPath, $builder.ToString(), $utf8)
    $written = [System.IO.File]::ReadAllText($manifestPath)
    if ($written.Contains('"name": "config_manifest.json"')) {
        Stop-GenConfig -Message '清单把自身写入了 files，哈希自引用。' -Code 4
    }
}

<#
.SYNOPSIS
判断数据文件是否属于本工具拥有的生成结果。
.PARAMETER Name
文件名。
.PARAMETER ListedNames
上一份清单里出现过的文件名。
#>
function Test-OwnedDataName {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [AllowEmptyCollection()][string[]]$ListedNames
    )
    if ($Name -eq 'config_manifest.json') { return $true }
    if ($Name -match '^tb[a-z0-9_]+\.json$') { return $true }
    if ($ListedNames -contains $Name) { return $true }
    return $false
}

<#
.SYNOPSIS
判断生成代码文件是否属于本工具，手写的同目录文件返回否。
.PARAMETER Path
文件绝对路径。
.PARAMETER ListedNames
上一份代码清单中的文件名。
#>
function Test-OwnedCodeFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [AllowEmptyCollection()][string[]]$ListedNames
    )
    $name = [System.IO.Path]::GetFileName($Path)
    if ($name -eq 'gen_config_code_manifest.json') { return $true }
    if ($ListedNames -contains $name) { return $true }
    if ([System.IO.Path]::GetExtension($name) -ne '.cs') { return $false }
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $buffer = New-Object byte[] 512
        $count = $stream.Read($buffer, 0, $buffer.Length)
    }
    finally {
        $stream.Dispose()
    }
    if ($count -le 0) { return $false }
    $text = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $count)
    return $text.Contains('This code was generated by a tool')
}

<#
.SYNOPSIS
读取旧清单里的 files[].name。文件不存在时返回空列表。
.PARAMETER Path
config_manifest.json 或代码清单。
.PARAMETER CodeManifest
为真时读取代码清单的 files 字符串数组。
#>
function Read-ListedNames {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$CodeManifest
    )
    $names = New-Object System.Collections.Generic.List[string]
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    $doc = Read-JsonDocument -Path $Path
    if ($CodeManifest) {
        if ($doc -is [System.Collections.IDictionary] -and (Test-JsonField -Object $doc -Name 'files')) {
            foreach ($name in @($doc['files'])) { $names.Add([string]$name) }
        }
        return @($names)
    }
    if ($doc -is [System.Collections.IDictionary] -and (Test-JsonField -Object $doc -Name 'files')) {
        foreach ($row in @($doc['files'])) {
            if ($row -is [System.Collections.IDictionary] -and (Test-JsonField -Object $row -Name 'name')) {
                $names.Add([string]$row['name'])
            }
        }
    }
    return @($names)
}

<#
.SYNOPSIS
记录发布目录的现有文件哈希，并把文件复制到本次暂存备份。
.PARAMETER Target
包含 Name、Path、Kind 的发布目标。
.PARAMETER BackupRoot
本次备份根目录。
#>
function Backup-Target {
    param(
        [Parameter(Mandatory = $true)]$Target,
        [Parameter(Mandatory = $true)][string]$BackupRoot
    )
    $path = $Target.Path
    $existed = Test-Path -LiteralPath $path
    $files = @{}
    $backupDir = Join-Path $BackupRoot $Target.Name
    New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
    if ($existed) {
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            Stop-GenConfig -Message "发布路径被文件占用，拒绝覆盖: $path" -Code 5
        }
        foreach ($file in (Get-DirectoryFiles -Path $path)) {
            $files[$file.Name] = Get-Sha256Hex -Path $file.FullName
            $destination = Join-Path $backupDir $file.Name
            [System.IO.File]::Copy($file.FullName, $destination, $true)
        }
    }
    $listed = @()
    if ($Target.Kind -eq 'data') {
        $listed = @(Read-ListedNames -Path (Join-Path $path 'config_manifest.json'))
    }
    else {
        $listed = @(Read-ListedNames -Path (Join-Path $path 'gen_config_code_manifest.json') -CodeManifest)
    }
    return @{
        Name = $Target.Name
        Path = $path
        Kind = $Target.Kind
        Existed = [bool]$existed
        Files = $files
        Listed = @($listed)
        BackupDir = $backupDir
        Mutated = $false
    }
}

<#
.SYNOPSIS
把暂存结果写入一个正式目录，并只删除本工具拥有的陈旧生成文件。
.PARAMETER State
Backup-Target 返回的状态。
.PARAMETER SourceDir
暂存中的代码目录或数据目录。
.PARAMETER RepoRoot
工程根。
#>
function Publish-Target {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    $created = $false
    if (-not (Test-Path -LiteralPath $State.Path)) {
        New-Item -ItemType Directory -Force -Path $State.Path | Out-Null
        $created = $true
    }
    $State.Created = $created
    $State.Mutated = $true
    $incoming = @{}
    foreach ($file in (Get-DirectoryFiles -Path $SourceDir)) {
        $incoming[$file.Name] = $true
        $destination = Join-Path $State.Path $file.Name
        if (Test-Path -LiteralPath $destination) {
            $existing = Get-Item -LiteralPath $destination
            if ($existing.PSIsContainer) {
                Stop-GenConfig -Message "发布目标是目录而不是文件: $destination" -Code 5
            }
            if ($existing.IsReadOnly) { $existing.IsReadOnly = $false }
        }
        [System.IO.File]::Copy($file.FullName, $destination, $true)
    }
    if ($State.Kind -eq 'code') {
        $names = @($incoming.Keys | Sort-Object)
        $codeManifest = New-Object System.Text.StringBuilder
        [void]$codeManifest.AppendLine('{')
        [void]$codeManifest.AppendLine('  "schema": "m1.config-code-manifest.v1",')
        [void]$codeManifest.AppendLine('  "files": [')
        for ($i = 0; $i -lt $names.Count; $i++) {
            $comma = ''
            if ($i -lt ($names.Count - 1)) { $comma = ',' }
            [void]$codeManifest.AppendLine("    `"$($names[$i])`"$comma")
        }
        [void]$codeManifest.AppendLine('  ]')
        [void]$codeManifest.Append('}')
        $manifestPath = Join-Path $State.Path 'gen_config_code_manifest.json'
        $utf8 = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($manifestPath, $codeManifest.ToString(), $utf8)
        $incoming['gen_config_code_manifest.json'] = $true
    }
    foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
        if ($incoming.ContainsKey($file.Name)) { continue }
        $owned = $false
        if ($State.Kind -eq 'data') {
            $owned = Test-OwnedDataName -Name $file.Name -ListedNames $State.Listed
        }
        else {
            $owned = Test-OwnedCodeFile -Path $file.FullName -ListedNames $State.Listed
        }
        if ($owned) {
            Remove-OwnedFile -Path $file.FullName -RepoRoot $RepoRoot
        }
    }
}

<#
.SYNOPSIS
用备份把一个已经写过的发布目录恢复到发布前。
.PARAMETER State
Backup-Target 的状态。发布未开始时直接返回。
.PARAMETER RepoRoot
工程根。
#>
function Restore-Target {
    param(
        [Parameter(Mandatory = $true)]$State,
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )
    if (-not $State.Mutated) { return }
    if (-not $State.Existed) {
        if (Test-Path -LiteralPath $State.Path) {
            foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
                Remove-OwnedFile -Path $file.FullName -RepoRoot $RepoRoot
            }
            $left = @([System.IO.Directory]::GetFileSystemEntries($State.Path))
            if ($left.Count -eq 0) {
                $dir = Resolve-UnderWorkspace -Path $State.Path -Root $RepoRoot
                Remove-Item -LiteralPath $dir -Force
            }
        }
    }
    else {
        foreach ($name in @($State.Files.Keys)) {
            $source = Join-Path $State.BackupDir $name
            $destination = Join-Path $State.Path $name
            if (Test-Path -LiteralPath $destination) {
                $existing = Get-Item -LiteralPath $destination
                if ($existing.IsReadOnly) { $existing.IsReadOnly = $false }
            }
            if (Test-Path -LiteralPath $destination) {
                Remove-Item -LiteralPath $destination -Force
            }
            [System.IO.File]::Copy([string]$source, [string]$destination)
        }
        foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
            if (-not $State.Files.ContainsKey($file.Name)) {
                Remove-OwnedFile -Path $file.FullName -RepoRoot $RepoRoot
            }
        }
    }
    if (-not $State.Existed) {
        if (Test-Path -LiteralPath $State.Path) {
            Stop-GenConfig -Message "回滚后目录仍在: $($State.Path)" -Code 5
        }
        return
    }
    $now = @{}
    foreach ($file in (Get-DirectoryFiles -Path $State.Path)) {
        $now[$file.Name] = Get-Sha256Hex -Path $file.FullName
    }
    foreach ($name in @($State.Files.Keys)) {
        if (-not $now.ContainsKey($name) -or $now[$name] -cne $State.Files[$name]) {
            Stop-GenConfig -Message "回滚后哈希与发布前不一致: $($State.Path)\$name" -Code 5
        }
    }
    foreach ($name in @($now.Keys)) {
        if (-not $State.Files.ContainsKey($name)) {
            Stop-GenConfig -Message "回滚后仍有发布前不存在的文件: $($State.Path)\$name" -Code 5
        }
    }
}

<#
.SYNOPSIS
确认三个数据目录的已拥有 JSON 与清单一致，且清单哈希不自引用。
.PARAMETER Directories
三个正式 JSON 目录。
#>
function Assert-PublishedData {
    param([Parameter(Mandatory = $true)][string[]]$Directories)
    $reference = $null
    foreach ($dir in $Directories) {
        $manifestPath = Join-Path $dir 'config_manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath)) {
            Stop-GenConfig -Message "发布后缺少清单: $manifestPath" -Code 5
        }
        $doc = Read-JsonDocument -Path $manifestPath
        if ([int]$doc['configVersion'] -ne $script:ExpectedVersion) {
            Stop-GenConfig -Message "清单版本不是 $($script:ExpectedVersion): $manifestPath" -Code 5
        }
        if ([string]$doc['schema'] -ne 'm1.config-manifest.v1') {
            Stop-GenConfig -Message "清单 schema 不符: $manifestPath" -Code 5
        }
        $expected = @{}
        foreach ($row in @($doc['files'])) {
            $name = [string]$row['name']
            if ($name -eq 'config_manifest.json') {
                Stop-GenConfig -Message "清单哈希自引用: $manifestPath" -Code 5
            }
            $full = Join-Path $dir $name
            $actual = Get-Sha256Hex -Path $full
            $length = (Get-Item -LiteralPath $full).Length
            if ($actual -cne [string]$row['sha256'] -or [int64]$length -ne [int64]$row['length']) {
                Stop-GenConfig -Message "清单与文件字节不一致: $full" -Code 5
            }
            $expected[$name] = $actual
        }
        $kvName = [string]$doc['globals']['file']
        if (-not $expected.ContainsKey($kvName)) {
            Stop-GenConfig -Message "清单没有覆盖键值文件哈希: $kvName" -Code 5
        }
        foreach ($file in (Get-DirectoryFiles -Path $dir)) {
            $owned = Test-OwnedDataName -Name $file.Name -ListedNames @($expected.Keys)
            if ($owned -and -not $expected.ContainsKey($file.Name) -and $file.Name -ne 'config_manifest.json') {
                Stop-GenConfig -Message "发布目录仍有陈旧生成文件: $($file.FullName)" -Code 5
            }
        }
        $pair = @{
            Manifest = (Get-Sha256Hex -Path $manifestPath)
            Files = $expected
        }
        if ($null -eq $reference) {
            $reference = $pair
            continue
        }
        if ($reference.Manifest -cne $pair.Manifest) {
            Stop-GenConfig -Message "两端清单字节不一致: $dir" -Code 5
        }
        foreach ($name in @($reference.Files.Keys)) {
            if ($reference.Files[$name] -cne $pair.Files[$name]) {
                Stop-GenConfig -Message "两端文件哈希不一致: $dir $name" -Code 5
            }
        }
    }
}

<#
.SYNOPSIS
调用 Luban。dll 走 dotnet，exe 直接执行。
.PARAMETER ExporterPath
导出器文件。
.PARAMETER ConfPath
luban.conf 绝对路径。
.PARAMETER CodeDir
暂存代码目录。
.PARAMETER DataDir
暂存数据目录。
#>
function Invoke-LubanExporter {
    param(
        [Parameter(Mandatory = $true)][string]$ExporterPath,
        [Parameter(Mandatory = $true)][string]$ConfPath,
        [Parameter(Mandatory = $true)][string]$CodeDir,
        [Parameter(Mandatory = $true)][string]$DataDir
    )
    $extension = [System.IO.Path]::GetExtension($ExporterPath)
    $lubanArgs = @(
        '--conf', $ConfPath,
        '-t', 'all',
        '-c', 'cs-simple-json',
        '-d', 'json',
        '-x', "outputCodeDir=$CodeDir",
        '-x', "outputDataDir=$DataDir",
        '--validationFailAsError'
    )
    Write-Host "==> Luban $($lubanArgs -join ' ')"
    if ($extension -eq '.dll') {
        & dotnet $ExporterPath @lubanArgs
    }
    elseif ($extension -eq '.exe') {
        & $ExporterPath @lubanArgs
    }
    else {
        Stop-GenConfig -Message "ExporterPath 只支持 .dll 或 .exe: $ExporterPath" -Code 3
    }
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        if ($null -eq $code) { $code = 1 }
        Stop-GenConfig -Message "导出器失败，exit=$code。正式目录未更新。" -Code $code
    }
}

<#
.SYNOPSIS
走旧 Python 导表。与 M1 四个参数同时出现时拒绝。
.PARAMETER RepoOfScript
本脚本所在工程根，用来定位 gen_config.py。
.PARAMETER LegacyArgs
转发给 Python 的参数。
#>
function Invoke-LegacyGenConfig {
    param(
        [Parameter(Mandatory = $true)][string]$RepoOfScript,
        [AllowEmptyCollection()][string[]]$LegacyArgs
    )
    $pythonScript = Join-Path $RepoOfScript 'Tools\gen_config.py'
    if (-not (Test-Path -LiteralPath $pythonScript)) {
        [Console]::Error.WriteLine("缺少旧导表脚本: $pythonScript")
        exit 1
    }
    Write-Host "==> legacy python $pythonScript"
    & python $pythonScript @LegacyArgs
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = 1 }
    exit $code
}

$repoOfScript = Split-Path -Parent $PSScriptRoot
$remainingArgs = New-Object System.Collections.Generic.List[string]
if ($null -ne $Remaining) {
    foreach ($token in $Remaining) {
        if (-not [string]::IsNullOrWhiteSpace([string]$token)) {
            $remainingArgs.Add([string]$token)
        }
    }
}
$legacyRequested = [bool]$Legacy
foreach ($token in $remainingArgs) {
    if ($token -eq '--xlsx' -or $token -eq '--set-global' -or $token -eq '-xlsx' -or $token -eq '-set-global') {
        $legacyRequested = $true
    }
}
$m1Bound = $PSBoundParameters.ContainsKey('RepoRoot') -or $PSBoundParameters.ContainsKey('SourceDir') -or $PSBoundParameters.ContainsKey('ExporterPath') -or $PSBoundParameters.ContainsKey('ConfigVersion')
if ($legacyRequested -and $m1Bound) {
    $boundNames = @($PSBoundParameters.Keys) -join ','
    $rest = @($remainingArgs) -join ','
    [Console]::Error.WriteLine('旧入口与 -RepoRoot/-SourceDir/-ExporterPath/-ConfigVersion 不能同时使用。')
    exit 2
}
if ($legacyRequested) {
    Invoke-LegacyGenConfig -RepoOfScript $repoOfScript -LegacyArgs @($remainingArgs.ToArray())
}
if (-not $m1Bound) {
    [Console]::Error.WriteLine('M1 入口需要 -RepoRoot -SourceDir -ExporterPath -ConfigVersion。PoC 请使用 -Legacy，或传入 --xlsx / --set-global。')
    exit 2
}
if ($remainingArgs.Count -gt 0) {
    [Console]::Error.WriteLine("无法识别的参数: $($remainingArgs -join ' ')")
    exit 2
}

try {
    if ([string]::IsNullOrWhiteSpace($RepoRoot) -or [string]::IsNullOrWhiteSpace($SourceDir) -or [string]::IsNullOrWhiteSpace($ExporterPath)) {
        Stop-GenConfig -Message 'RepoRoot、SourceDir、ExporterPath 都不能为空。' -Code 2
    }
    if ($ConfigVersion -lt 1) {
        Stop-GenConfig -Message "ConfigVersion 必须是正整数，实际 $ConfigVersion。" -Code 2
    }
    $script:ExpectedVersion = $ConfigVersion
    if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
        Stop-GenConfig -Message "RepoRoot 不是已有目录: $RepoRoot" -Code 2
    }
    $script:RepoRootFull = [System.IO.Path]::GetFullPath($RepoRoot)
    $sourceFull = Resolve-RepoPath -Path $SourceDir -RepoRoot $script:RepoRootFull
    $exporterFull = Resolve-RepoPath -Path $ExporterPath -RepoRoot $script:RepoRootFull
    if (-not (Test-Path -LiteralPath $sourceFull -PathType Container)) {
        Stop-GenConfig -Message "SourceDir 不是已有目录: $sourceFull" -Code 2
    }
    $confPath = Join-Path $sourceFull 'luban.conf'
    if (-not (Test-Path -LiteralPath $confPath -PathType Leaf)) {
        Stop-GenConfig -Message "SourceDir 下没有 luban.conf: $confPath" -Code 2
    }
    foreach ($required in @('Datas', 'Defines')) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceFull $required) -PathType Container)) {
            Stop-GenConfig -Message "SourceDir 缺少 $required : $sourceFull" -Code 2
        }
    }
    if (-not (Test-Path -LiteralPath $exporterFull -PathType Leaf)) {
        Stop-GenConfig -Message "ExporterPath 不存在: $exporterFull" -Code 3
    }
    $exporterExtension = [System.IO.Path]::GetExtension($exporterFull)
    if ($exporterExtension -ne '.dll' -and $exporterExtension -ne '.exe') {
        Stop-GenConfig -Message "ExporterPath 只支持 .dll 或 .exe: $exporterFull" -Code 3
    }

    $runId = [guid]::NewGuid().ToString('n')
    $script:StagingPath = Join-Path $script:RepoRootFull "Temp\gen_config\$runId"
    $codeStage = Join-Path $script:StagingPath 'code'
    $dataStage = Join-Path $script:StagingPath 'data'
    $backupRoot = Join-Path $script:StagingPath 'backup'
    New-Item -ItemType Directory -Force -Path $codeStage, $dataStage, $backupRoot | Out-Null

    Invoke-LubanExporter -ExporterPath $exporterFull -ConfPath $confPath -CodeDir $codeStage -DataDir $dataStage
    Assert-GlobalKeyValues -DataDir $dataStage -ConfigVersion $ConfigVersion
    Write-ConfigManifest -DataDir $dataStage -ConfigVersion $ConfigVersion

    # data 目标四处：Shared 历史输出、Server、StreamingAssets 直读副本、Assets/ConfigLuban（Yoo EditorSimulate 可收集镜像）。
    # StreamingAssets 不可直接作为 Yoo 收集路径；运行时清单地址见 ConfigAddresses（config_manifest）。
    $publishTargets = @(
        @{ Name = 'code'; Kind = 'code'; Path = (Join-Path $script:RepoRootFull 'Assets\Config\Generated') },
        @{ Name = 'output'; Kind = 'data'; Path = (Join-Path $script:RepoRootFull 'Shared\config\Output\json') },
        @{ Name = 'server'; Kind = 'data'; Path = (Join-Path $script:RepoRootFull 'Server\ConfigLuban') },
        @{ Name = 'client'; Kind = 'data'; Path = (Join-Path $script:RepoRootFull 'Assets\StreamingAssets\ConfigLuban') },
        @{ Name = 'yooCollect'; Kind = 'data'; Path = (Join-Path $script:RepoRootFull 'Assets\ConfigLuban') }
    )
    $states = New-Object System.Collections.Generic.List[object]
    foreach ($target in $publishTargets) {
        $states.Add((Backup-Target -Target $target -BackupRoot $backupRoot))
    }
    $script:Targets = $states
    $script:PublishStarted = $true
    $index = 0
    foreach ($state in $states) {
        $from = $dataStage
        if ($state.Kind -eq 'code') { $from = $codeStage }
        Publish-Target -State $state -SourceDir $from -RepoRoot $script:RepoRootFull
        $index += 1
        if ($env:GEN_CONFIG_FAIL_POINT -eq 'AfterFirstPublish' -and $index -eq 1) {
            Stop-GenConfig -Message '验证钩子 GEN_CONFIG_FAIL_POINT=AfterFirstPublish：第一个目录已写，随后回滚。这不是跨目录原子事务。' -Code 5
        }
        # 新增 Yoo 可收集镜像专用失败点：该目标已写后触发，验证 Backup/Restore 覆盖 Assets/ConfigLuban。
        if ($env:GEN_CONFIG_FAIL_POINT -eq 'AfterYooCollect' -and $state.Name -eq 'yooCollect') {
            Stop-GenConfig -Message '验证钩子 GEN_CONFIG_FAIL_POINT=AfterYooCollect：Assets/ConfigLuban 已写，随后回滚。这不是跨目录原子事务。' -Code 5
        }
    }
    Assert-PublishedData -Directories @(
        (Join-Path $script:RepoRootFull 'Shared\config\Output\json'),
        (Join-Path $script:RepoRootFull 'Server\ConfigLuban'),
        (Join-Path $script:RepoRootFull 'Assets\StreamingAssets\ConfigLuban'),
        (Join-Path $script:RepoRootFull 'Assets\ConfigLuban')
    )
    Write-Host "OK ConfigVersion=$ConfigVersion code=$(Join-Path $script:RepoRootFull 'Assets\Config\Generated')"
}
catch {
    $message = $_.Exception.Message
    if ($script:PublishStarted) {
        Write-Host '==> 发布未完成，按目录备份恢复已修改的目标'
        # 一个目录恢复失败仍继续其余目录；保留所有备份以便后续恢复。
        foreach ($state in $script:Targets) {
            try {
                Restore-Target -State $state -RepoRoot $script:RepoRootFull
            }
            catch {
                $script:PreserveStaging = $true
                $message += [Environment]::NewLine + "Rollback failed ($($state.Name)): " + $_.Exception.Message
                $script:ExitCode = 5
            }
        }
        if ($script:PreserveStaging) {
            $message += [Environment]::NewLine + "Recovery backup retained: $($script:StagingPath)"
        }
    }
    if ($script:ExitCode -eq 0) { $script:ExitCode = 1 }
    [Console]::Error.WriteLine($message)
}
finally {
    if ($script:StagingPath -and -not $script:PreserveStaging) {
        try {
            Remove-OwnStaging -Path $script:StagingPath -RepoRoot $script:RepoRootFull
        }
        catch {
            [Console]::Error.WriteLine("清理暂存失败: $($_.Exception.Message)")
            if ($script:ExitCode -eq 0) { $script:ExitCode = 5 }
        }
    }
}

exit $script:ExitCode
