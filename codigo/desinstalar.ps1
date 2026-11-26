# Desinstala RevitAutomation de Revit 2023, 2024, 2025, 2026 y 2027 para el usuario actual. No toca ningún proyecto de Revit ni los
# ajustes de las herramientas (Documentos\RevitAutomation). Usa la copia del instalador que se guarda al instalar.
$ErrorActionPreference = 'Stop'
$copia = Join-Path $env:LOCALAPPDATA 'RevitAutomation\Instalador\Instalar_RevitAutomation.exe'
if (-not (Test-Path $copia)) {
    Write-Host 'RevitAutomation no está instalado para este usuario (no se encuentra la copia del instalador).' -ForegroundColor Yellow
    Write-Host "Cómo corregirlo: si aparece la pestaña en Revit, ejecute Instalar_RevitAutomation.exe y pulse «Desinstalar»."
    exit 0
}
$p = Start-Process -FilePath $copia -ArgumentList '/desinstalar', '/silencioso' -Wait -PassThru
$informe = Join-Path $env:LOCALAPPDATA 'RevitAutomation\instalacion.txt'
if (Test-Path $informe) { Get-Content $informe -Encoding UTF8 | Write-Host }
exit $p.ExitCode
