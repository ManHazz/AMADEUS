# Local Windows launcher. Reads settings from .env (token + Gemini key live there).
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (-not (Test-Path .env)) { throw ".env missing" }
Get-Content .env | ForEach-Object {
    if ($_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], "Process")
    }
}
if ($env:BRAIN_API_KEY -eq "PASTE_GEMINI_KEY_HERE") { Write-Warning "Paste your Gemini key into .env (BRAIN_API_KEY) first." }
. .\.venv\Scripts\Activate.ps1
$port = if ($env:PORT) { $env:PORT } else { "5000" }
Write-Host "URL  : http://127.0.0.1:$port"
Write-Host "Token: read VOICE_AGENT_TOKEN from .env"
python -m uvicorn server:app --host 127.0.0.1 --port $port
