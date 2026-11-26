# Instala RevitAutomation (Revit 2023, 2024, 2025, 2026 y 2027) en el usuario actual: genera el instalador (pruebas + compilación +
# paquete) con crear_instalador.ps1 y lo ejecuta sin ventana. Cierre Revit antes. Para repartirlo use Instalar_RevitAutomation.exe.
param([string]$Versiones)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$salida = Join-Path $root 'instalador\obj\local'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'crear_instalador.ps1') -Salida $salida
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$exe = Join-Path $salida 'Instalar_RevitAutomation.exe'
$argumentos = @('/silencioso')
if ($Versiones) { $argumentos += "/versiones:$Versiones" }
$p = Start-Process -FilePath $exe -ArgumentList $argumentos -Wait -PassThru
$informe = Join-Path $env:LOCALAPPDATA 'RevitAutomation\instalacion.txt'
if (Test-Path $informe) { Get-Content $informe -Encoding UTF8 | Write-Host }
if ($p.ExitCode -ne 0) {
    Write-Host 'La instalación no se ha completado: revise el mensaje anterior.' -ForegroundColor Red
    exit $p.ExitCode
}
Write-Host 'Instalado. Abra Revit y busque la pestaña «RevitAutomation».' -ForegroundColor Green
