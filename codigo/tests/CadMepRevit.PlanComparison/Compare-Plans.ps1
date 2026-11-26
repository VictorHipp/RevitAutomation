param(
    [Parameter(Mandatory)][string]$Antes,
    [Parameter(Mandatory)][string]$Planos,
    [switch]$Calibrar,
    # La copia de -Antes ya tiene BuildPlan.AllowsFitting (versión del 19-11 o posterior): se compila igual que la actual.
    [switch]$AntesConUnionesActuales
)

$ErrorActionPreference = 'Stop'
$actual = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$antes = (Resolve-Path $Antes).Path
$planos = (Resolve-Path $Planos).Path
$obj = Join-Path $PSScriptRoot 'obj'
$proyecto = Join-Path $PSScriptRoot 'CadMepRevit.PlanComparison.csproj'
$muestra = Join-Path $actual 'samples\bandejas_uniones.dxf'
$salida = Join-Path $actual 'Pruebas'
$env:DOTNET_ROOT = Join-Path $env:USERPROFILE '.dotnet'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$env:NuGetAudit = 'false'
New-Item -ItemType Directory -Force -Path $obj | Out-Null
New-Item -ItemType Directory -Force -Path $salida | Out-Null

function Extract-Text([string]$text, [string]$begin, [string]$end) {
    $start = $text.IndexOf($begin, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "No se encuentra $begin" }
    $stop = $text.IndexOf($end, $start, [StringComparison]::Ordinal)
    if ($stop -lt 0) { throw "No se encuentra $end" }
    $text.Substring($start, $stop - $start)
}

function Save-Snapshot([string]$source, [string]$label, [bool]$current) {
    # Compila AutoPlanner directamente. Extrae sus tipos de datos y los métodos estáticos del formulario.
    # El catálogo del proyecto se sustituye por el mismo catálogo fijo para ambas compilaciones.
    $builder = Get-Content -Raw -Encoding UTF8 (Join-Path $source 'src\CadMepRevit\Revit\MepBuilder.cs')
    $plan = Extract-Text $builder 'public sealed class LayerPlan' 'public sealed class BuildReport'
    "using Autodesk.Revit.DB; using CadMepRevit.Core; namespace CadMepRevit.Revit;`n$plan" |
        Set-Content -Encoding UTF8 (Join-Path $obj 'ComparisonPlan.cs')
    $form = Get-Content -Raw -Encoding UTF8 (Join-Path $source 'src\CadMepRevit\Ui\ImportForm.cs')
    $systems = Extract-Text $form 'internal static string? SuggestSystem' 'private void ApplyDefaultDiameter'
    $blocks = Extract-Text $form 'internal static readonly (string Label, BuiltInCategory Cat)[] MarkerCategories' 'private void FillBlockGrid'
    "using Autodesk.Revit.DB; using CadMepRevit.Core; namespace CadMepRevit.Ui; internal static class ImportForm {`n$systems`n$blocks`n}" |
        Set-Content -Encoding UTF8 (Join-Path $obj 'ComparisonImportForm.cs')

    & $dotnet build $proyecto -c Release "-p:ComparisonSource=$source" "-p:CurrentFittings=$current"
    if ($LASTEXITCODE -ne 0) { throw "No se pudo compilar la comparación ($label)" }
    $exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\CadMepRevit.PlanComparison.dll'
    $file = Join-Path $salida "FIRMAS_${label}_CODOS_TES_ACCESORIOS_20261119.json"
    & $dotnet $exe snapshot $planos $file $muestra
    if ($LASTEXITCODE -ne 0) { throw "No se pudo generar la firma ($label)" }
}

Save-Snapshot $antes 'ANTES' ([bool]$AntesConUnionesActuales)
Save-Snapshot $actual 'DESPUES' $true
$beforeJson = Join-Path $salida 'FIRMAS_ANTES_CODOS_TES_ACCESORIOS_20261119.json'
$afterJson = Join-Path $salida 'FIRMAS_DESPUES_CODOS_TES_ACCESORIOS_20261119.json'
$exe = Join-Path $PSScriptRoot 'bin\Release\net10.0\CadMepRevit.PlanComparison.dll'
if ($Calibrar) {
    & $dotnet $exe calibrate $planos $muestra
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
& $dotnet $exe compare $beforeJson $afterJson
exit $LASTEXITCODE
