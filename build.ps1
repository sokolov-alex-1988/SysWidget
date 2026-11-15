# Сборка виджета: компилируем C#-исходники встроенным csc из .NET Framework
# (ставится вместе с Windows, SDK не нужен).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Что делает:
#   1. собирает все .cs из папки src в один SysWidget.exe;
#   2. вшивает манифест (per-monitor DPI, asInvoker) и иконку;
#   3. кладёт рядом SysWidget.exe.config — без него WPF не реагирует на смену DPI.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$exe  = Join-Path $root 'SysWidget.exe'
$cfg  = Join-Path $root 'SysWidget.exe.config'
$ico  = Join-Path $root 'icon.ico'
$man  = Join-Path $src  'app.manifest'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'

if (-not (Test-Path $csc)) { throw "Не найден компилятор: $csc" }
if (-not (Test-Path $man)) { throw "Не найден манифест: $man" }

# работающая копия держит exe — закрываем её, иначе запись упадёт
Get-Process SysWidget -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.CloseMainWindow() | Out-Null } catch { }
    Start-Sleep -Milliseconds 300
    if (-not $_.HasExited) { try { $_.Kill() } catch { } }
}

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/codepage:65001'          # исходники в UTF-8 без BOM и с кириллицей
    ('/win32manifest:' + $man)
    "/out:$exe"
)
if (Test-Path $ico) { $cscArgs += ('/win32icon:' + $ico) }

foreach ($r in @(
    "$fw\System.dll"
    "$fw\System.Core.dll"
    "$fw\Microsoft.CSharp.dll"
    "$fw\System.Xaml.dll"
    "$fw\System.Windows.Forms.dll"
    "$fw\System.Drawing.dll"
    "$fw\System.Management.dll"
    "$fw\WPF\PresentationFramework.dll"
    "$fw\WPF\PresentationCore.dll"
    "$fw\WPF\WindowsBase.dll"
)) {
    $cscArgs += "/r:$r"
}

# WinRT (заряд Bluetooth-периферии). Метаданные Windows лежат в System32\
# WinMetadata и ставятся вместе с Windows — SDK и Windows Kits не нужны.
# Если их нет (совсем старая Windows), собираем без WINRT: устройства
# останутся в списке, но без процентов заряда.
$wm = Join-Path $env:WINDIR 'System32\WinMetadata'
if (Test-Path (Join-Path $wm 'Windows.Devices.winmd')) {
    $cscArgs += '/define:WINRT'
    $cscArgs += "/r:$fw\System.Runtime.dll"
    $cscArgs += "/r:$fw\System.Runtime.WindowsRuntime.dll"
    $cscArgs += "/r:$(Join-Path $wm 'Windows.Foundation.winmd')"
    $cscArgs += "/r:$(Join-Path $wm 'Windows.Devices.winmd')"
    $cscArgs += "/r:$(Join-Path $wm 'Windows.Storage.winmd')"
}

$cscArgs += Get-ChildItem -Path $src -Filter *.cs | ForEach-Object { $_.FullName }

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc завершился с кодом $LASTEXITCODE" }

Copy-Item -Path (Join-Path $src 'SysWidget.exe.config') -Destination $cfg -Force

Write-Host "Готово: $exe"
