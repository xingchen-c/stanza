#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [switch]$Installer,
    [string]$ReleaseTag,
    [ValidateSet('SelfContained', 'FrameworkDependent', 'All')]
    [string]$Deployment = 'SelfContained'
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $Repo 'Stanza/Stanza.csproj'

Push-Location $Repo
try {
    $version = (& dotnet msbuild $Project -nologo -getProperty:Version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw '读取项目版本失败。' }
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:alpha|beta|rc)\.(0|[1-9]\d*))?$') {
        throw "版本不符合项目约定：$version"
    }
    if ($ReleaseTag -and $ReleaseTag -cne "v$version") {
        throw "发布标签 $ReleaseTag 与项目版本 v$version 不一致。"
    }
    if ($Installer) { $compiler = Get-Command iscc -ErrorAction Stop }

    if (!$OutputDirectory) { $OutputDirectory = Join-Path $Repo '../dist' }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $modes = if ($Deployment -eq 'All') { @('SelfContained', 'FrameworkDependent') } else { @($Deployment) }
    $plans = @(foreach ($mode in $modes) {
        $selfContained = $mode -eq 'SelfContained'
        $suffix = if ($selfContained) { '' } else { '-no-runtime' }
        [pscustomobject]@{
            SelfContained = $selfContained
            Publish = Join-Path $output "Stanza-$version-win-x64$suffix"
            Zip = Join-Path $output "Stanza-$version-win-x64-portable$suffix.zip"
            Setup = Join-Path $output "Stanza-$version-setup$suffix.exe"
        }
    })
    foreach ($plan in $plans) {
        $targets = @($plan.Publish, $plan.Zip, "$($plan.Zip).sha256")
        if ($Installer) { $targets += @($plan.Setup, "$($plan.Setup).sha256") }
        foreach ($target in $targets) {
            if (Test-Path -LiteralPath $target) { throw "目标已存在，不覆盖已有版本：$target" }
        }
    }

    foreach ($plan in $plans) {
        $publish = $plan.Publish
        & dotnet publish $Project -c Release -r win-x64 --self-contained ($plan.SelfContained.ToString().ToLowerInvariant()) `
            -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $publish
        if ($LASTEXITCODE -ne 0) { throw '程序发布失败。' }
        $builtVersion = (Get-Item -LiteralPath "$publish/Stanza.exe").VersionInfo.ProductVersion
        if ($builtVersion.Split('+')[0] -cne $version) { throw '发布程序版本与项目版本不一致。' }

        $runtime = Get-Content -LiteralPath "$publish/Stanza.runtimeconfig.json" -Raw | ConvertFrom-Json
        $frameworks = @($runtime.runtimeOptions.includedFrameworks) + @($runtime.runtimeOptions.frameworks)
        $desktopFramework = $frameworks | Where-Object name -EQ 'Microsoft.WindowsDesktop.App'
        $desktopMajor = $desktopFramework.version.Split('.')[0]
        $runtimeNote = if ($plan.SelfContained) {
            '完整解压后运行 Stanza.exe，无需另装 .NET。不要只提取 EXE 或删除随包 DLL。'
        } else {
            "本包不含 .NET 运行时，需先安装 .NET $desktopMajor Desktop Runtime（Windows x64）。完整解压后运行 Stanza.exe。"
        }
        Copy-Item -LiteralPath (Join-Path $Repo 'LICENSE'), (Join-Path $Repo 'THIRD-PARTY-NOTICES.md') -Destination $publish
        @"
Stanza $version — Windows 11 x64 免安装版

$runtimeNote
设置、歌词数据库和日志由启动前的 STANZA_DATA_DIR 环境变量指定，默认使用 %APPDATA%\Stanza。
Scoop 可将 STANZA_DATA_DIR 设为其 persist/stanza/data 目录；手动使用时也可指定其他目录。
指定新目录不会自动导入旧数据；如需迁移，先退出程序再复制原数据目录的内容。
升级前请先从托盘退出 Stanza。启用开机自启后，移动或删除目录前请先在设置中关闭自启。
源码：https://github.com/xingchen-c/stanza （对应 v$version 标签）
许可见 LICENSE，依赖声明见 THIRD-PARTY-NOTICES.md 和 third-party-licenses。
"@ | Set-Content -LiteralPath "$publish/README-PORTABLE.txt" -Encoding utf8

        $assets = Get-Content -LiteralPath (Join-Path $Repo 'Stanza/obj/project.assets.json') -Raw | ConvertFrom-Json
        foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
            $package = "$($framework.name.ToLowerInvariant()).runtime.win-x64"
            foreach ($root in $assets.packageFolders.PSObject.Properties.Name) {
                $packageDir = Join-Path $root "$package/$($framework.version)"
                if (!(Test-Path -LiteralPath $packageDir)) { continue }
                $licenseDir = Join-Path $publish "third-party-licenses/$package-$($framework.version)"
                New-Item -ItemType Directory -Path $licenseDir -Force | Out-Null
                Get-ChildItem -LiteralPath $packageDir -File | Where-Object Name -Match '(?i)license|notice' |
                    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $licenseDir }
                break
            }
        }

        Compress-Archive -Path "$publish/*" -DestinationPath $plan.Zip -CompressionLevel Optimal
        $artifacts = @($plan.Zip)

        if ($Installer) {
            $packageName = [IO.Path]::GetFileNameWithoutExtension($plan.Setup)
            & $compiler.Source '/Q' "/DAppVersion=$version" "/DPublishDir=$publish" "/DPackageOutputDir=$output" `
                "/DPackageName=$packageName" "/DSelfContained=$([int]$plan.SelfContained)" "/DDesktopRuntimeMajor=$desktopMajor" (Join-Path $Repo 'Stanza.iss')
            if ($LASTEXITCODE -ne 0) { throw '安装包编译失败。' }
            $artifacts += $plan.Setup
        }

        foreach ($artifact in $artifacts) {
            $hash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $([IO.Path]::GetFileName($artifact))" | Set-Content -LiteralPath "$artifact.sha256" -Encoding ascii
            Write-Output "Package: $artifact"
            Write-Output "SHA256: $hash"
        }
    }
} finally {
    Pop-Location
}
