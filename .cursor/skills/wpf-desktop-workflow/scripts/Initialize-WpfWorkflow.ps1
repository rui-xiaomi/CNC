[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProjectPath,

    [switch]$Apply,

    [switch]$UseWpfUiHandyControlProfile,

    [switch]$IncludeThemeBridge
)

$ErrorActionPreference = 'Stop'

$resolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$packageRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..\..')).Path
$templateRoot = Join-Path $packageRoot 'templates\wpf-desktop'

if (-not (Test-Path -LiteralPath $templateRoot -PathType Container)) {
    throw "找不到模板目录：$templateRoot"
}

$projectFiles = @(Get-ChildItem -LiteralPath $resolvedProject -Recurse -File -Filter *.csproj)
$isWpf = $false
foreach ($projectFile in $projectFiles) {
    if ((Get-Content -LiteralPath $projectFile.FullName -Raw) -match '<UseWPF>\s*true\s*</UseWPF>') {
        $isWpf = $true
        break
    }
}

if ($projectFiles.Count -gt 0 -and -not $isWpf) {
    throw '发现 .csproj，但未发现 <UseWPF>true</UseWPF>；停止初始化。'
}

$mappings = @(
    @{ Source = 'CONTEXT.md.template'; Destination = 'CONTEXT.md' },
    @{ Source = 'docs\prd\片段-状态策略.md'; Destination = 'docs\prd\_templates\片段-状态策略.md' },
    @{ Source = 'docs\prd\片段-app-shell.md'; Destination = 'docs\prd\_templates\片段-app-shell.md' },
    @{ Source = 'docs\testing\WPF-TEST-STRATEGY.md'; Destination = 'docs\testing\WPF-TEST-STRATEGY.md' }
)

if ($UseWpfUiHandyControlProfile) {
    $mappings += @(
        @{ Source = 'docs\adr\0001-双UI库分工与主题桥接.md'; Destination = 'docs\adr\0001-双UI库分工与主题桥接.md' },
        @{ Source = 'docs\design\DESIGN.md'; Destination = 'docs\design\DESIGN.md' }
    )
}

if ($IncludeThemeBridge -and -not $UseWpfUiHandyControlProfile) {
    throw '-IncludeThemeBridge 只能与 -UseWpfUiHandyControlProfile 一起使用。'
}

if ($IncludeThemeBridge) {
    $mappings += @{ Source = 'src\Themes\HandyControlBridge.xaml'; Destination = 'src\Themes\HandyControlBridge.xaml' }
}

$hasConflict = $false
foreach ($mapping in $mappings) {
    $source = Join-Path $templateRoot $mapping.Source
    $destination = Join-Path $resolvedProject $mapping.Destination
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        Write-Error "缺少模板文件：$source"
        $hasConflict = $true
        continue
    }

    if (Test-Path -LiteralPath $destination) {
        Write-Host "[EXISTS] $destination"
        $hasConflict = $true
    } else {
        Write-Host "[CREATE] $destination"
    }
}

if (-not $Apply) {
    Write-Host "`n当前仅预览。确认后使用 -Apply；已有文件不会被覆盖。"
    if (-not $UseWpfUiHandyControlProfile) {
        Write-Host '未选择 WPF-UI + HandyControl profile：DESIGN 与双库 ADR 将由 /grill-with-docs 按真实项目生成。'
    }
    exit 0
}

if ($hasConflict) {
    throw '存在缺失模板或目标冲突；未写入任何文件。请先处理差异。'
}

foreach ($mapping in $mappings) {
    $source = Join-Path $templateRoot $mapping.Source
    $destination = Join-Path $resolvedProject $mapping.Destination
    $destinationDirectory = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}

Write-Host 'WPF 工作流模板已初始化。请先按真实依赖修改技术基线和待确认项，再运行 /setup-skills。'
