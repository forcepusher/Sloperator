param([string]$Exe, [int]$Port = 5713, [string]$Name = "now", [int]$Width = 1000, [int]$Height = 760)
# Today's Auto card with a long reason: one message on Auto against server.ps1, then a fresh capture of the window
# (shown for a moment under every other window, see .dev/centre-test/fresh.ps1). Leaves the app running for more
# shots; prints its pid.
$here = Split-Path $MyInvocation.MyCommand.Path
$out = Join-Path $here $Name
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$out\home\SloperatorData" | Out-Null
[IO.File]::WriteAllText("$out\home\SloperatorData\settings.json", @"
{ "server": { "baseUrl": "http://localhost:$Port/v1", "apiKey": "", "model": "", "effort": "auto", "autoContext": false, "manualContextSize": 262144 },
  "agent": { "approval": "Never", "customInstructions": "" },
  "window": { "x": 300, "y": 40, "width": $Width, "height": $Height, "maximized": false } }
"@)
$server = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$here\server.ps1`" -Port $Port -Out `"$out`"" -WindowStyle Hidden -PassThru
$env:SLOPERATOR_HOME = "$out\home"
$app = Start-Process $Exe -PassThru
Start-Sleep 10
$drive = "$here\..\diff-colours-test\drive.ps1"
& $drive -AppPid $app.Id -Action away | Out-Null
& $drive -AppPid $app.Id -Action send -Arg "How should I split the settings rework into steps?" | Out-Null
Start-Sleep 8
& "$here\..\centre-test\fresh.ps1" -AppPid $app.Id -Out "$out\card.png"
"app=$($app.Id) server=$($server.Id)"
