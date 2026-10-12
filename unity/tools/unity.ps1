# Usage: .\tools\unity.ps1 compile | test-play-gfx (graphics on) | test-edit | test-play | build | method <Class.Method>
param([Parameter(Mandatory)][string]$Cmd, [string]$Arg)
$ver = "6000.6.5f1"
$unity = "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe"
$proj = Split-Path $PSScriptRoot -Parent
$log = Join-Path $proj "Logs\cli-$Cmd.log"
$base = @('-batchmode','-nographics','-projectPath',$proj,'-logFile',$log)
switch ($Cmd) {
  'compile'   { $a = $base + @('-quit') }
  'test-edit' { $a = $base + @('-runTests','-testPlatform','EditMode','-testResults',"$proj\Logs\edit.xml") }
  'test-play' { $a = $base + @('-runTests','-testPlatform','PlayMode','-testResults',"$proj\Logs\play.xml") }
  'test-play-gfx' { $a = @('-projectPath',$proj,'-logFile',$log,'-runTests','-testPlatform','PlayMode','-testResults',"$proj\Logs\play-gfx.xml") }
  'build'     { $a = $base + @('-quit','-executeMethod','BuildScript.BuildWindows') }
  'screenshot' { $a = @('-batchmode','-projectPath',$proj,'-logFile',$log,'-quit','-executeMethod','BuildRoom.Screenshot') }
  'method'    { $a = $base + @('-quit','-executeMethod',$Arg) }
}
$p = Start-Process $unity -ArgumentList $a -Wait -PassThru -NoNewWindow
"exit code: $($p.ExitCode)"
Select-String -Path $log -Pattern 'error CS\d+|Exception|Build succeeded|Test.*(Passed|Failed)' | Select -Last 30 | % { $_.Line }
exit $p.ExitCode
