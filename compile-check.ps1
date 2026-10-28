# 不开 Unity 也能查 DSH Unity Bridge 的编译错误。
# 用 dotnet SDK 对着 Unity 的托管 DLL 编一遍 Editor/*.cs，缺 API / 拼错名字会立刻报出来。
# 用法： pwsh -File dsh-unity-mcp/compile-check.ps1 [-UnityEditor "D:\Unity\2022.3.54f1c1\Editor"]
param(
    [string]$UnityEditor = 'D:\Unity\2022.3.54f1c1\Editor',
    [string]$PackageRoot = (Join-Path $PSScriptRoot 'unity-package')
)

$ErrorActionPreference = 'Stop'
$pkg = (Resolve-Path $PackageRoot).Path
$mg = Join-Path $UnityEditor 'Data\Managed'
$eng = Join-Path $mg 'UnityEngine'
if (-not (Test-Path $eng)) { throw "找不到 Unity 托管 DLL：$eng" }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('dsh-bridge-check-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null

$engineRefs = @(
    'UnityEngine.dll',
    'UnityEngine.CoreModule.dll',
    'UnityEngine.Physics2DModule.dll',
    'UnityEngine.ImageConversionModule.dll',
    'UnityEngine.IMGUIModule.dll',
    'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.AnimationModule.dll'
)
$editorRefs = @('UnityEditor.dll', 'UnityEditor.CoreModule.dll')

$lines = New-Object System.Collections.Generic.List[string]
foreach ($dll in $engineRefs) {
    $path = if ($dll -eq 'UnityEngine.dll') { Join-Path $eng $dll } else { Join-Path $eng $dll }
    if (Test-Path $path) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($dll)
        $lines.Add("    <Reference Include=`"$name`"><HintPath>$path</HintPath><Private>false</Private></Reference>")
    }
}
foreach ($dll in $editorRefs) {
    $path = Join-Path $mg $dll
    if (Test-Path $path) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($dll)
        $lines.Add("    <Reference Include=`"$name`"><HintPath>$path</HintPath><Private>false</Private></Reference>")
    }
}

$csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>DshBridge.Check</AssemblyName>
    <NoWarn>CS0649;CS0169;CS0414</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$pkg\Editor\*.cs" />
  </ItemGroup>
  <ItemGroup>
$($lines -join "`n")
  </ItemGroup>
</Project>
"@

Set-Content -Path (Join-Path $work 'check.csproj') -Value $csproj -Encoding UTF8
Push-Location $work
try {
    & dotnet build check.csproj -v m --nologo
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
if ($code -ne 0) { throw "编译校验失败（退出码 $code）" }
Write-Host '编译校验通过' -ForegroundColor Green
