# Familias de uniones de reserva: los 12 codos, tes, cruces y transiciones (tubería y conducto rectangular y redondo) de la
# biblioteca de Autodesk. CAD-REV las carga si ni el proyecto ni las bibliotecas de la oficina tienen ninguna. Son contenido de
# Autodesk: no están en el repositorio y cada uno las pone en su PC. Lo usan crear_instalador.ps1 y preparar_pc.ps1.
# Uso: . .\buscar_familias.ps1 ; $faltan = Find-FamiliasUniones <carpeta familias_uniones> [<carpeta -Familias>]
# Este archivo se guarda en UTF-8 CON BOM: PowerShell 5.1 leería mal las tildes de los nombres sin él.

$FamiliasUnionesNecesarias = @('M_Codo - Genérico', 'M_Te - Genérica', 'M_Cruz - Genérica', 'M_Transición - Genérica',
    'M_Codo rectangular - Radio 1', 'M_Te rectangular - Recto', 'M_Cruz rectangular -  Recta', 'M_Transición rectangular - Ángulo 2',
    'M_Codo redondo 1', 'M_Te redonda - Recto', 'M_Cruz redonda - Recto', 'M_Transición redonda - Ángulo')

# Las que falten en $Carpeta se buscan, por este orden, en la carpeta $Familias, en la variable de entorno
# REVITAUTOMATION_FAMILIAS y en las bibliotecas de Revit instaladas (C:\ProgramData\Autodesk\RVT <año>\Libraries, de la más
# nueva a la más antigua), y se copian a $Carpeta. Devuelve los nombres (sin .rfa) que siguen faltando.
function Find-FamiliasUniones([string]$Carpeta, [string]$Familias) {
    $faltan = @($FamiliasUnionesNecesarias | Where-Object { -not (Test-Path -LiteralPath (Join-Path $Carpeta "$_.rfa")) })
    if ($faltan.Count -eq 0) { return @() }
    $origenes = @()
    if ($Familias) { $origenes += $Familias }
    if ($env:REVITAUTOMATION_FAMILIAS) { $origenes += $env:REVITAUTOMATION_FAMILIAS }
    $programData = if ($env:ProgramData) { $env:ProgramData } else { 'C:\ProgramData' }
    $origenes += @(Get-ChildItem (Join-Path $programData 'Autodesk') -Directory -Filter 'RVT *' -Force -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'Libraries' })
    Write-Host "Buscando $($faltan.Count) familias de uniones que faltan en familias_uniones..."
    New-Item -ItemType Directory -Force -Path $Carpeta | Out-Null
    foreach ($origen in $origenes) {
        if ($faltan.Count -eq 0) { break }
        if (-not (Test-Path -LiteralPath $origen)) {
            if ($origen -eq $Familias -or $origen -eq $env:REVITAUTOMATION_FAMILIAS) { Write-Host "  AVISO: no existe la carpeta $origen" -ForegroundColor Yellow }
            continue
        }
        # Un solo recorrido por origen; se compara el nombre exacto (Filter no distingue bien los dos espacios ni las tildes).
        $rfas = @(Get-ChildItem -LiteralPath $origen -Recurse -File -Filter *.rfa -Force -ErrorAction SilentlyContinue)
        foreach ($nombre in @($faltan)) {
            $hallado = $rfas | Where-Object { $_.Name -eq "$nombre.rfa" } | Select-Object -First 1
            if ($hallado) {
                Copy-Item -LiteralPath $hallado.FullName -Destination (Join-Path $Carpeta $hallado.Name) -Force
                Write-Host "  OK   $($hallado.Name)  (de $origen)"
                $faltan = @($faltan | Where-Object { $_ -ne $nombre })
            }
        }
    }
    return $faltan
}
