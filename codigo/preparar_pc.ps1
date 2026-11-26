# Prepara un PC para compilar RevitAutomation: instala lo que falte de
#   - SDK de .NET 10 (Revit 2027, el instalador y las versiones 2023/2024 con .NET Framework 4.8),
#   - SDK de .NET 8 (Revit 2025/2026 y las pruebas del motor),
#   - Git para Windows y GitHub CLI.
# Lo ya instalado se salta. Usa winget si está disponible; si no, descarga el instalador oficial desde su enlace
# directo, comprueba su firma digital y lo ejecuta sin ventanas. Windows pide permiso de administrador una sola vez.
# Uso: doble clic en PREPARAR_PC.cmd, o:
#      powershell -ExecutionPolicy Bypass -File preparar_pc.ps1 [-SoloDotnet]
# -SoloDotnet: solo los dos SDK de .NET (lo usa crear_instalador.ps1).
# -Elevado / -Instalar: uso interno (la copia que se ejecuta como administrador).
param([switch]$SoloDotnet, [switch]$Elevado, [string]$Instalar)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$script = $MyInvocation.MyCommand.Path
$programFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$raizDotnetSistema = Join-Path $programFiles 'dotnet'

# Herramientas que puede instalar. Firma: texto que debe aparecer en el certificado del instalador descargado.
$herramientas = [ordered]@{
    sdk10 = @{ Nombre = 'SDK de .NET 10'; Winget = 'Microsoft.DotNet.SDK.10'; Firma = 'Microsoft Corporation'
               Url = 'https://aka.ms/dotnet/10.0/dotnet-sdk-win-x64.exe'; Argumentos = '/install /quiet /norestart'
               Manual = 'https://dotnet.microsoft.com/download/dotnet/10.0' }
    sdk8  = @{ Nombre = 'SDK de .NET 8'; Winget = 'Microsoft.DotNet.SDK.8'; Firma = 'Microsoft Corporation'
               Url = 'https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe'; Argumentos = '/install /quiet /norestart'
               Manual = 'https://dotnet.microsoft.com/download/dotnet/8.0' }
    git   = @{ Nombre = 'Git para Windows'; Winget = 'Git.Git'; Firma = 'Johannes Schindelin'
               Repo = 'git-for-windows/git'; Archivo = '^Git-[\d.]+-64-bit\.exe$'
               Argumentos = '/VERYSILENT /NORESTART /NOCANCEL /SP- /SUPPRESSMSGBOXES'
               Manual = 'https://git-scm.com/download/win' }
    gh    = @{ Nombre = 'GitHub CLI'; Winget = 'GitHub.cli'; Firma = 'GitHub'
               Repo = 'cli/cli'; Archivo = '^gh_[\d.]+_windows_amd64\.msi$'; Argumentos = '/quiet /norestart'
               Manual = 'https://cli.github.com' }
}

# ---------------------------------------------------------------------------------------------------------------
# Detección
# ---------------------------------------------------------------------------------------------------------------

# Vuelve a leer el PATH del registro: lo recién instalado no aparece en el PATH de esta ventana.
function Update-RutaEntorno {
    $partes = @([Environment]::GetEnvironmentVariable('Path', 'Machine'), [Environment]::GetEnvironmentVariable('Path', 'User'))
    $env:Path = ($partes | Where-Object { $_ }) -join ';'
}

# Carpetas de .NET de este equipo con las versiones principales de SDK que contiene cada una.
function Get-RaicesDotnet {
    $rutas = @()
    if ($env:DOTNET_ROOT) { $rutas += $env:DOTNET_ROOT }
    if ($env:USERPROFILE) { $rutas += Join-Path $env:USERPROFILE '.dotnet' }
    $rutas += $raizDotnetSistema
    $cmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $rutas += Split-Path -Parent $cmd.Source }
    $vistas = @{}
    foreach ($r in $rutas) {
        $clave = $r.TrimEnd('\', '/').ToLowerInvariant()
        if ($vistas.ContainsKey($clave) -or -not (Test-Path (Join-Path $r 'dotnet.exe'))) { continue }
        $vistas[$clave] = $true
        $sdks = @(Get-ChildItem (Join-Path $r 'sdk') -Directory -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -match '^\d+\.\d+\.\d+' -and (Test-Path (Join-Path $_.FullName 'dotnet.dll')) } |
                  ForEach-Object { $_.Name })
        $mayores = @($sdks | ForEach-Object { [int]($_ -split '\.')[0] } | Sort-Object -Unique)
        [pscustomobject]@{ Raiz = $r; Sdks = $sdks; Mayores = $mayores }
    }
}

# Una carpeta de .NET sirve si tiene a la vez un SDK 10 (o posterior) y un SDK 8: crear_instalador.ps1 usa una sola.
function Test-RaizCompleta($raiz) {
    return (@($raiz.Mayores | Where-Object { $_ -ge 10 }).Count -gt 0) -and ($raiz.Mayores -contains 8)
}

function Find-Programa([string]$nombre, [string[]]$rutas) {
    $cmd = Get-Command $nombre -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return $cmd.Source }
    foreach ($base in @($programFiles, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'Programs' }))) {
        foreach ($r in $rutas) {
            if (-not $base) { continue }
            $completa = Join-Path $base $r
            if (Test-Path $completa) { return $completa }
        }
    }
    return $null
}
# Rutas relativas a Archivos de programa y a %LOCALAPPDATA%\Programs (instalación solo para el usuario).
function Find-Git { Find-Programa 'git.exe' @('Git\cmd\git.exe') }
function Find-Gh  { Find-Programa 'gh.exe'  @('GitHub CLI\gh.exe') }

# Estado de cada herramienta: Clave, Nombre, Instalada, Detalle.
function Get-Estado {
    $estado = @()
    $raices = @(Get-RaicesDotnet)
    $completa = $raices | Where-Object { Test-RaizCompleta $_ } | Select-Object -First 1
    if ($completa) {
        $v10 = $completa.Sdks | Where-Object { [int]($_ -split '\.')[0] -ge 10 } | Select-Object -Last 1
        $v8  = $completa.Sdks | Where-Object { $_ -like '8.*' } | Select-Object -Last 1
        $estado += [pscustomobject]@{ Clave = 'sdk10'; Nombre = $herramientas.sdk10.Nombre; Instalada = $true; Detalle = "$v10 en $($completa.Raiz)" }
        $estado += [pscustomobject]@{ Clave = 'sdk8';  Nombre = $herramientas.sdk8.Nombre;  Instalada = $true; Detalle = "$v8 en $($completa.Raiz)" }
    } else {
        # Se instalan los dos juntos en la carpeta normal de .NET (Archivos de programa\dotnet).
        $sistema = $raices | Where-Object { $_.Raiz.TrimEnd('\') -ieq $raizDotnetSistema.TrimEnd('\') } | Select-Object -First 1
        $mayores = if ($sistema) { $sistema.Mayores } else { @() }
        $otras = @($raices | Where-Object { $_ -ne $sistema -and $_.Mayores.Count -gt 0 } | ForEach-Object { $_.Raiz })
        $nota = if ($otras.Count) { " (hay otro SDK en $($otras -join ', '), pero no tiene los dos juntos)" } else { '' }
        $tiene10 = @($mayores | Where-Object { $_ -ge 10 }).Count -gt 0
        $tiene8 = $mayores -contains 8
        $estado += [pscustomobject]@{ Clave = 'sdk10'; Nombre = $herramientas.sdk10.Nombre; Instalada = $tiene10
                                      Detalle = $(if ($tiene10) { "en $raizDotnetSistema" } else { "no está en $raizDotnetSistema$nota" }) }
        $estado += [pscustomobject]@{ Clave = 'sdk8'; Nombre = $herramientas.sdk8.Nombre; Instalada = $tiene8
                                      Detalle = $(if ($tiene8) { "en $raizDotnetSistema" } else { "no está en $raizDotnetSistema$nota" }) }
    }
    if (-not $SoloDotnet) {
        $git = Find-Git
        $estado += [pscustomobject]@{ Clave = 'git'; Nombre = $herramientas.git.Nombre; Instalada = [bool]$git; Detalle = $(if ($git) { $git } else { 'no encontrado' }) }
        $gh = Find-Gh
        $estado += [pscustomobject]@{ Clave = 'gh'; Nombre = $herramientas.gh.Nombre; Instalada = [bool]$gh; Detalle = $(if ($gh) { $gh } else { 'no encontrado' }) }
    }
    return $estado
}

function Show-Estado($estado) {
    foreach ($e in $estado) {
        if ($e.Instalada) { Write-Host ("  [OK]    {0}: {1}" -f $e.Nombre, $e.Detalle) -ForegroundColor Green }
        else { Write-Host ("  [FALTA] {0}: {1}" -f $e.Nombre, $e.Detalle) -ForegroundColor Yellow }
    }
}

# Comprueba una sola herramienta después de instalarla (en la copia que se ejecuta como administrador).
function Test-Instalada([string]$clave) {
    Update-RutaEntorno
    switch ($clave) {
        'sdk10' { return @(Get-RaicesDotnet | Where-Object { @($_.Mayores | Where-Object { $_ -ge 10 }).Count -gt 0 -and $_.Raiz.TrimEnd('\') -ieq $raizDotnetSistema.TrimEnd('\') }).Count -gt 0 }
        'sdk8'  { return @(Get-RaicesDotnet | Where-Object { $_.Mayores -contains 8 -and $_.Raiz.TrimEnd('\') -ieq $raizDotnetSistema.TrimEnd('\') }).Count -gt 0 }
        'git'   { return [bool](Find-Git) }
        'gh'    { return [bool](Find-Gh) }
    }
    return $false
}

# ---------------------------------------------------------------------------------------------------------------
# Instalación (solo en la copia que se ejecuta como administrador)
# ---------------------------------------------------------------------------------------------------------------

function Install-ConWinget($h) {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) { Write-Host '  winget no está disponible en este equipo: se descarga el instalador directamente.'; return $false }
    Write-Host "  Instalando con winget ($($h.Winget))..."
    & $winget.Source install --id $h.Winget -e --source winget --silent --accept-package-agreements --accept-source-agreements | Out-Host
    Write-Host "  winget ha terminado (código $LASTEXITCODE)."
    return $true
}

function Get-UrlUltimaVersion($h) {
    if ($h.Url) { return $h.Url }
    # Última versión publicada en GitHub (Git para Windows y GitHub CLI no tienen un enlace directo fijo).
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$($h.Repo)/releases/latest" -UseBasicParsing -Headers @{ 'User-Agent' = 'RevitAutomation-preparar-pc' }
    $asset = $rel.assets | Where-Object { $_.name -match $h.Archivo } | Select-Object -First 1
    if (-not $asset) { throw "La última versión de $($h.Repo) no tiene un archivo que coincida con $($h.Archivo)." }
    return $asset.browser_download_url
}

function Save-Descarga([string]$url, [string]$destino) {
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (Test-Path $curl) {
        & $curl -L --fail --retry 3 --progress-bar -o $destino $url | Out-Host
        if ($LASTEXITCODE -eq 0 -and (Test-Path $destino)) { return }
        Write-Host '  curl no ha podido descargarlo; se reintenta con PowerShell (usa el proxy de Windows)...'
    }
    $anterior = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # la barra de progreso de Invoke-WebRequest lo hace muy lento
    try { Invoke-WebRequest -Uri $url -OutFile $destino -UseBasicParsing } finally { $ProgressPreference = $anterior }
}

function Install-Directo($h) {
    $carpeta = Join-Path $env:TEMP 'RevitAutomation_preparar'
    New-Item -ItemType Directory -Force -Path $carpeta | Out-Null
    $url = Get-UrlUltimaVersion $h
    $nombre = [IO.Path]::GetFileName(([Uri]$url).AbsolutePath)
    if (-not $nombre -or $nombre -notmatch '\.(exe|msi)$') { $nombre = "$($h.Winget).exe" }
    $archivo = Join-Path $carpeta $nombre
    try {
        Write-Host "  Descargando $url"
        Save-Descarga $url $archivo
        $firma = Get-AuthenticodeSignature $archivo
        $sujeto = if ($firma.SignerCertificate) { $firma.SignerCertificate.Subject } else { '(sin firma)' }
        if ($firma.Status -ne 'Valid' -or $sujeto -notlike "*$($h.Firma)*") {
            throw "La firma digital del archivo descargado no es válida o no es de $($h.Firma) (estado: $($firma.Status); firmante: $sujeto). No se ha ejecutado."
        }
        Write-Host "  Firma comprobada: $sujeto"
        Write-Host '  Instalando (puede tardar unos minutos)...'
        if ($archivo -like '*.msi') {
            $p = Start-Process msiexec.exe -ArgumentList "/i `"$archivo`" $($h.Argumentos)" -Wait -PassThru
        } else {
            $p = Start-Process $archivo -ArgumentList $h.Argumentos -Wait -PassThru
        }
        if (@(0, 1641, 3010) -notcontains $p.ExitCode) { throw "El instalador ha terminado con el código $($p.ExitCode)." }
        if (@(1641, 3010) -contains $p.ExitCode) { Write-Host '  Windows pide reiniciar el equipo para terminar.' -ForegroundColor Yellow }
    } finally {
        Remove-Item $archivo -Force -ErrorAction SilentlyContinue
    }
}

function Install-Herramientas([string[]]$claves) {
    $fallos = @()
    foreach ($clave in $claves) {
        $h = $herramientas[$clave]
        if (-not $h) { continue }
        Write-Host ''
        Write-Host "== $($h.Nombre) ==" -ForegroundColor Cyan
        try {
            if ((Install-ConWinget $h) -and (Test-Instalada $clave)) { Write-Host "  $($h.Nombre): instalado." -ForegroundColor Green; continue }
            Install-Directo $h
            if (-not (Test-Instalada $clave)) { throw 'El instalador ha terminado, pero no se encuentra lo instalado.' }
            Write-Host "  $($h.Nombre): instalado." -ForegroundColor Green
        } catch {
            Write-Host "  ERROR: no se ha podido instalar $($h.Nombre). $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "  Cómo corregirlo: compruebe la conexión a Internet y repita, o descárguelo a mano desde $($h.Manual)" -ForegroundColor Yellow
            $fallos += $h.Nombre
        }
    }
    return $fallos
}

# ---------------------------------------------------------------------------------------------------------------
# Copia que se ejecuta como administrador: instala lo pedido y vuelve
# ---------------------------------------------------------------------------------------------------------------
if ($Elevado) {
    Write-Host '=== RevitAutomation: instalando las herramientas (como administrador) ===' -ForegroundColor Cyan
    $fallos = @(Install-Herramientas ($Instalar -split ',' | Where-Object { $_ }))
    if ($fallos.Count) {
        Write-Host ''
        Write-Host "No se ha podido instalar: $($fallos -join ', '). Lea los mensajes anteriores." -ForegroundColor Red
        Read-Host 'Pulse Intro para cerrar esta ventana' | Out-Null
        exit 1
    }
    exit 0
}

# ---------------------------------------------------------------------------------------------------------------
# Comprobar, instalar lo que falte y volver a comprobar
# ---------------------------------------------------------------------------------------------------------------
function Fallo($que, $como) {
    Write-Host ''
    Write-Host "ERROR: $que" -ForegroundColor Red
    Write-Host "Cómo corregirlo: $como" -ForegroundColor Yellow
    exit 1
}

Write-Host '=== RevitAutomation: preparar este PC para compilar ===' -ForegroundColor Cyan
Write-Host 'Comprobando lo que ya está instalado...'
$estado = @(Get-Estado)
Show-Estado $estado
$faltan = @($estado | Where-Object { -not $_.Instalada } | ForEach-Object { $_.Clave })

if ($faltan.Count) {
    $nombres = ($faltan | ForEach-Object { $herramientas[$_].Nombre }) -join ', '
    Write-Host ''
    Write-Host "Se va a instalar: $nombres." -ForegroundColor Cyan
    Write-Host 'Windows le pedirá permiso de administrador: pulse «Sí». Se abrirá otra ventana con el progreso.'
    $esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($esAdmin) {
        $null = Install-Herramientas $faltan
    } else {
        $argumentos = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Elevado -Instalar $($faltan -join ',')"
        try {
            Start-Process powershell.exe -ArgumentList $argumentos -Verb RunAs -Wait
        } catch {
            Fallo "No se ha podido abrir la instalación como administrador: $($_.Exception.Message)" `
                  "Si ha cancelado el aviso de Windows, vuelva a ejecutar PREPARAR_PC.cmd y pulse «Sí». Si su usuario no puede instalar programas, pida a su departamento de informática que instale: $nombres."
        }
    }
    Write-Host ''
    Write-Host 'Comprobando de nuevo...'
    Update-RutaEntorno
    $estado = @(Get-Estado)
    Show-Estado $estado
}

# Git: rutas largas de Windows (el proyecto tiene rutas de más de 260 caracteres). Se hace con el usuario actual.
$git = Find-Git
if ($git) { try { & $git config --global core.longpaths true } catch { } }

$siguenFaltando = @($estado | Where-Object { -not $_.Instalada })
if ($siguenFaltando.Count) {
    $lista = ($siguenFaltando | ForEach-Object { "$($_.Nombre) ($($herramientas[$_.Clave].Manual))" }) -join '; '
    Fallo "Sigue faltando: $(($siguenFaltando | ForEach-Object { $_.Nombre }) -join ', ')." `
          "Lea los mensajes de la ventana de instalación y vuelva a ejecutar PREPARAR_PC.cmd. También puede instalarlo a mano: $lista. Después cierre y vuelva a abrir PowerShell."
}

Write-Host ''
Write-Host 'TODO LISTO.' -ForegroundColor Green
if (-not $SoloDotnet) {
    Write-Host 'Siguiente paso: ponga las 12 familias de uniones en familias_uniones\ y haga doble clic en CREAR_INSTALADOR.cmd.'
    Write-Host 'Si va a usar la consola, cierre y vuelva a abrir PowerShell para que encuentre los programas nuevos.'
}
exit 0
