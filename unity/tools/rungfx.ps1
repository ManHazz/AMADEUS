param([Parameter(Mandatory)][string]$Method)
$unity = "C:\Program Files\Unity\Hub\Editor\6000.6.5f1\Editor\Unity.exe"
$proj = Split-Path $PSScriptRoot -Parent
$log = Join-Path $proj "Logs\cli-gfx.log"
$p = Start-Process $unity -ArgumentList @('-batchmode','-projectPath',$proj,'-logFile',$log,'-quit','-executeMethod',$Method) -Wait -PassThru -NoNewWindow
"exit $($p.ExitCode)"
# Graphics-on PlayMode verify: tools/unity.ps1 test-play-gfx (writes Logs/face_*.png, hud.png, subtitles_stack.png)
