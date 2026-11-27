# Genera Instalar_RevitAutomation.exe: pasa las pruebas, compila el complemento para Revit 2023, 2024, 2025, 2026 y 2027,
# lo empaqueta y crea el instalador. No instala nada en este equipo.
# Uso: doble clic en CREAR_INSTALADOR.cmd, o:
#      powershell -ExecutionPolicy Bypass -File crear_instalador.ps1 [-SinPruebas] [-Salida <carpeta>] [-Familias <carpeta>]
# Por defecto el .exe se deja en la carpeta que contiene la del código.
# -Familias: carpeta (p. ej. del servidor de la oficina) donde buscar las familias de uniones que falten.
param([switch]$SinPruebas, [switch]$SinFamilias, [string]$Salida, [string]$Familias)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Salida) { $Salida = Split-Path -Parent $root }

function Fallo($que, $como) {
    Write-Host ''
    Write-Host "ERROR: $que" -ForegroundColor Red
    Write-Host "Cómo corregirlo: $como" -ForegroundColor Yellow
    exit 1
}

Write-Host '=== RevitAutomation: crear el instalador Instalar_RevitAutomation.exe ===' -ForegroundColor Cyan

# 1) SDK de .NET 10 o posterior (Revit 2027, el instalador y 2023/2024 con .NET Framework 4.8) y SDK de .NET 8
#    (Revit 2025/2026 y las pruebas del motor), los dos en la misma carpeta de .NET.
#    Si falta alguno, ofrece instalarlo con preparar_pc.ps1 (PREPARAR_PC.cmd).
function Find-Dotnet {
    $programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $raices = @()
    if ($env:DOTNET_ROOT) { $raices += $env:DOTNET_ROOT }
    if ($env:USERPROFILE) { $raices += Join-Path $env:USERPROFILE '.dotnet' }
    if ($programFiles) { $raices += Join-Path $programFiles 'dotnet' }
    $cmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $raices += Split-Path -Parent $cmd.Source }
    $todas = @()
    foreach ($r in $raices) {
        $exe = Join-Path $r 'dotnet.exe'
        if (-not (Test-Path $exe)) { continue }
        $mayores = @(Get-ChildItem (Join-Path $r 'sdk') -Directory -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -match '^(\d+)\.\d+\.\d+' -and (Test-Path (Join-Path $_.FullName 'dotnet.dll')) } |
                     ForEach-Object { [int]($_.Name -split '\.')[0] })
        $todas += $mayores
        if (@($mayores | Where-Object { $_ -ge 10 }).Count -and ($mayores -contains 8)) { return @{ Ruta = $exe; Faltan = @() } }
    }
    $faltan = @()
    if (-not @($todas | Where-Object { $_ -ge 10 }).Count) { $faltan += 'el SDK de .NET 10' }
    if ($todas -notcontains 8) { $faltan += 'el SDK de .NET 8' }
    if (-not $faltan.Count) { $faltan += 'un SDK de .NET 10 y uno de .NET 8 en la misma carpeta (ahora están en carpetas distintas)' }
    return @{ Ruta = $null; Faltan = $faltan }
}
$busqueda = Find-Dotnet
if (-not $busqueda.Ruta) {
    $preparar = Join-Path $root 'preparar_pc.ps1'
    Write-Host "Falta $($busqueda.Faltan -join ' y ')." -ForegroundColor Yellow
    $interactivo = [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
    if ($interactivo -and (Test-Path $preparar)) {
        $respuesta = Read-Host '¿Instalarlo ahora? (S/N)'
        if ($respuesta -match '^\s*[sSyY]') {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $preparar -SoloDotnet
            $env:Path = (@([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User')) | Where-Object { $_ }) -join ';'
            $busqueda = Find-Dotnet
        }
    }
    if (-not $busqueda.Ruta) {
        Fallo "No se encuentra $($busqueda.Faltan -join ' ni ')." 'Haga doble clic en PREPARAR_PC.cmd (en esta misma carpeta) para instalar los SDK de .NET 10 y 8, o instálelos desde https://dotnet.microsoft.com/download (SDK, no solo el runtime), y repita.'
    }
}
$dotnet = $busqueda.Ruta
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROLL_FORWARD = 'Major'   # por si las pruebas (.NET 8) se ejecutan con otro .NET más nuevo
Write-Host "SDK de .NET: $dotnet"

# 2) Pruebas automáticas (fuera de Revit). Los archivos que generan quedan en obj, no en ejemplos.
if (-not $SinPruebas) {
    Write-Host 'Ejecutando las pruebas automáticas (fuera de Revit)...'
    $tmp = Join-Path $root 'tests\CadMepRevit.Tests\obj\pruebas-instalador'
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    & $dotnet run --project (Join-Path $root 'tests\CadMepRevit.Tests') -c Release -- $tmp
    if ($LASTEXITCODE -ne 0) { Fallo 'Las pruebas automáticas han fallado.' 'Revise los mensajes anteriores. Para generar el instalador igualmente: crear_instalador.ps1 -SinPruebas' }
    # Pruebas de la lógica de las herramientas de RevitAutomation (etiquetado, salas, patinillo, aislamiento, planos…).
    foreach ($h in Get-ChildItem (Join-Path $root 'tests\RevitAutomation.Pruebas') -Directory) {
        Write-Host "Pruebas de $($h.Name)..."
        & $dotnet run -c Release --project $h.FullName | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0) { Fallo "Las pruebas de $($h.Name) han fallado." 'Ejecútelas por separado con dotnet run para ver el detalle. Para generar el instalador igualmente: crear_instalador.ps1 -SinPruebas' }
    }
}

# 3) Compilar el complemento para cada versión de Revit, sin símbolos de depuración
#    (así no se incrustan rutas de este equipo en los DLL).
#    Siempre contra la API de NuGet: las DLL instaladas pueden ser de una actualización que exige un .NET más
#    nuevo (p. ej. Revit 2025.5 referencia System.Runtime 10) y no compilarían con el framework de esa versión.
$stage = Join-Path $root 'instalador\obj\paquete'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$versiones = @(
    @{ Anio = '2023'; Tfm = 'net48' },
    @{ Anio = '2024'; Tfm = 'net48' },
    @{ Anio = '2025'; Tfm = 'net8.0-windows' },
    @{ Anio = '2026'; Tfm = 'net8.0-windows' },
    @{ Anio = '2027'; Tfm = 'net10.0-windows' }
)
foreach ($v in $versiones) {
    Write-Host "Compilando RevitAutomation para Revit $($v.Anio)..."
    $obj = "-p:IntermediateOutputPath=obj\paquete\$($v.Anio)\"
    & $dotnet build (Join-Path $root 'src\RevitAutomation\RevitAutomation.csproj') -c Release -f $v.Tfm `
        "-p:RevitYear=$($v.Anio)" -p:UseNuGetRevitReferences=true -p:DebugType=none -p:DebugSymbols=false $obj --no-incremental `
        -o (Join-Path $stage $v.Anio) -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Fallo "La compilación para Revit $($v.Anio) ha fallado." 'Revise los errores anteriores. Sin Revit instalado, la API se descarga de NuGet: compruebe la conexión a Internet.' }
    Get-ChildItem (Join-Path $stage $v.Anio) -Recurse -Include *.pdb, RevitAPI*.dll | Remove-Item -Force
    foreach ($dll in 'RevitAutomation.dll', 'CadMepRevit.dll', 'CadMepRevit.Core.dll', 'ACadSharp.dll') {
        if (-not (Test-Path (Join-Path $stage "$($v.Anio)\$dll"))) { Fallo "No se ha generado $dll para Revit $($v.Anio)." 'Revise los mensajes de la compilación.' }
    }
}
# 4) Archivos comunes: guía y planos de ejemplo
$comun = Join-Path $stage 'comun'
New-Item -ItemType Directory -Force -Path (Join-Path $comun 'Ejemplos') | Out-Null
Copy-Item (Join-Path $root 'docs\Guia_CadMepRevit.txt') $comun -Force
Get-ChildItem (Join-Path $root 'ejemplos') -Include *.dwg, *.dxf -File -Recurse -ErrorAction SilentlyContinue |
    Copy-Item -Destination (Join-Path $comun 'Ejemplos') -Force
# Familias de codos, tes, cruces y transiciones (tubería y conducto rectangular y redondo): el complemento las carga si
# el proyecto y la biblioteca de Revit no tienen ninguna (si no, Revit no puede crear las uniones).
# Son contenido de Autodesk y no están en el repositorio. Las que falten en familias_uniones se buscan, por este orden, en
# la carpeta -Familias, en la variable de entorno REVITAUTOMATION_FAMILIAS y en las bibliotecas de Revit instaladas
# (C:\ProgramData\Autodesk\RVT <año>\Libraries, de la más nueva a la más antigua), y se copian a familias_uniones.
# Si aun así falta alguna, se avisa y el instalador se crea sin ella.
$avisoFamilias = $null
$carpetaFamilias = Join-Path $root 'familias_uniones'
New-Item -ItemType Directory -Force -Path (Join-Path $comun 'Familias') | Out-Null
if ($SinFamilias) {
    $avisoFamilias = 'Instalador sin familias de uniones (-SinFamilias).'
} else {
    # La lista de las 12 y la búsqueda están en buscar_familias.ps1 (también la usa preparar_pc.ps1).
    . (Join-Path $root 'buscar_familias.ps1')
    $faltan = @(Find-FamiliasUniones $carpetaFamilias $Familias)
    if ($faltan.Count -gt 0) {
        $avisoFamilias = "Faltan $($faltan.Count) de las 12 familias de uniones; el instalador se crea sin ellas:`n    " +
            (($faltan | ForEach-Object { "$_.rfa" }) -join "`n    ") +
            "`n  Sin ellas, CAD-REV usa las familias de uniones del proyecto o de las bibliotecas de la oficina; si no hay" +
            "`n  ninguna, Revit no crea esas uniones y lo indica en el informe." +
            "`n  Cómo corregirlo: copie los .rfa en codigo\familias_uniones (o indique su carpeta con -Familias <carpeta> o la" +
            "`n  variable REVITAUTOMATION_FAMILIAS) y vuelva a crear el instalador. Ver docs\es\02-instalacion.md, apartado 2.4."
    }
}
Get-ChildItem -LiteralPath $carpetaFamilias -Filter *.rfa -File -ErrorAction SilentlyContinue |
    Copy-Item -Destination (Join-Path $comun 'Familias') -Force
if ($avisoFamilias) { Write-Host "AVISO: $avisoFamilias" -ForegroundColor Yellow }

# 5) Comprobar que el paquete no lleva rutas ni nombres de este equipo
function Test-Contiene([string]$archivo, [string[]]$terminos) {
    $bytes = [IO.File]::ReadAllBytes($archivo)
    $u8 = [Text.Encoding]::UTF8.GetString($bytes)
    $u16 = [Text.Encoding]::Unicode.GetString($bytes)
    foreach ($t in $terminos) {
        if ($u8.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or $u16.IndexOf($t, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $t }
    }
    return $null
}
# Los nombres genéricos de cuenta de Windows («Administrador», «Usuario»...) no se buscan sueltos: son palabras normales
# que aparecen en los textos del instalador («no hace falta ser administrador»). Sí se busca la ruta del perfil
# (C:\Users\Administrador), que es lo que delataría el equipo.
$genericos = @('Administrador', 'Administrator', 'Admin', 'Usuario', 'Usuarios', 'User', 'Users', 'Invitado', 'Guest', 'Propietario', 'Owner')
$terminos = @($env:USERPROFILE) + @(@($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ -and $genericos -notcontains $_ }) |
    Where-Object { $_ -and $_.Length -ge 4 }
foreach ($f in Get-ChildItem $stage -Recurse -File) {
    $t = Test-Contiene $f.FullName $terminos
    if ($t) { Fallo "El archivo $($f.Name) del paquete contiene '$t' (datos de este equipo)." 'Compruebe que se compila sin símbolos de depuración (DebugType=none) y repita.' }
}

# 6) Empaquetar y crear el instalador
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $root 'instalador\obj\paquete.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

Write-Host 'Creando Instalar_RevitAutomation.exe...'
$binInst = Join-Path $root 'instalador\bin\salida'
& $dotnet build (Join-Path $root 'instalador\InstaladorCadMep.csproj') -c Release -p:DebugType=none -p:DebugSymbols=false `
    --no-incremental -o $binInst -nologo -v minimal
if ($LASTEXITCODE -ne 0) { Fallo 'La compilación del instalador ha fallado.' 'Revise los errores anteriores.' }
$exe = Join-Path $binInst 'Instalar_RevitAutomation.exe'
$t = Test-Contiene $exe $terminos
if ($t) { Fallo "Instalar_RevitAutomation.exe contiene '$t' (datos de este equipo)." 'Compruebe que se compila sin símbolos de depuración y repita.' }

New-Item -ItemType Directory -Force -Path $Salida | Out-Null
$final = Join-Path $Salida 'Instalar_RevitAutomation.exe'
Copy-Item $exe $final -Force
$hash = (Get-FileHash $final -Algorithm SHA256).Hash
Write-Host ''
Write-Host 'INSTALADOR CREADO correctamente.' -ForegroundColor Green
if ($avisoFamilias) { Write-Host "  AVISO: $avisoFamilias" -ForegroundColor Yellow }
Write-Host "  Archivo: $final"
Write-Host "  Tamaño:  $([math]::Round((Get-Item $final).Length / 1MB, 2)) MB"
Write-Host "  SHA256:  $hash"
Write-Host 'Para instalar: doble clic en Instalar_RevitAutomation.exe en el equipo con Revit 2023, 2024, 2025, 2026 o 2027.'
