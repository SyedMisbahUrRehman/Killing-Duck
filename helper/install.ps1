# Builds KillingDuck.exe and registers it with Chrome as a native messaging
# host, for the current Windows user only. Run install.cmd rather than this.

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$hostName = 'com.killingduck.tabstrip'
# Extension IDs the helper answers to. The first is the unpacked copy, fixed
# by "key" in ../manifest.json. The second is the Chrome Web Store copy.
$extensionIds = @(
    'foifgpacdfdginklojklodfnoljjhgkj',
    'bcebhbmffhngbmdigmkfgeiecpmbaale'
)

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $framework 'csc.exe'))) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$csc = Join-Path $framework 'csc.exe'
if (-not (Test-Path $csc)) {
    throw "The .NET Framework 4 C# compiler was not found at $csc"
}

$source = Join-Path $here 'KillingDuck.cs'
$exe = Join-Path $here 'KillingDuck.exe'

# Chrome keeps the helper running; stop it so the exe can be replaced.
Get-Process KillingDuck -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

Write-Host 'Building KillingDuck.exe ...'
# csc.rsp already references System, System.Drawing, Windows Forms and
# System.Web.Extensions; UI Automation and WindowsBase live in the WPF folder.
& $csc /nologo /target:winexe /optimize+ "/out:$exe" `
    "/lib:$(Join-Path $framework 'WPF')" `
    /r:UIAutomationClient.dll /r:UIAutomationTypes.dll /r:WindowsBase.dll `
    $source
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }

$manifestPath = Join-Path $here "$hostName.json"
$manifest = [ordered]@{
    name            = $hostName
    description     = 'Killing Duck tab strip companion'
    path            = $exe
    type            = 'stdio'
    allowed_origins = @($extensionIds | ForEach-Object { "chrome-extension://$_/" })
}
# Chrome wants UTF-8 without a byte order mark.
[System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))

$key = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
New-Item -Path $key -Force | Out-Null
Set-Item -Path $key -Value $manifestPath

Write-Host ''
Write-Host 'Done. The duck can now live on your tab strip.'
Write-Host 'Reload Killing Duck on chrome://extensions to wake it up.'
