# 4. Desarrollo

[← Índice](../../README.es.md) · [English](../en/04-development.md)

Este capítulo es para quien **cambia el código** y sube cambios a GitHub. Para descargar el proyecto, preparar el PC y crear el instalador, vea [Descarga e instalación](02-instalacion.md).

Salvo que se indique otra cosa, todos los comandos se ejecutan en **PowerShell** desde la carpeta `codigo`.

## 4.1 Preparar un PC para desarrollar

1. Haga la **Parte A** de [Descarga e instalación](02-instalacion.md#parte-a-preinstalación-una-vez-por-pc) (pasos 2.1 a 2.4). En `PREPARAR_PC.cmd`, responda **S** a «¿Va a desarrollar el complemento o subir cambios a GitHub?» para que instale también GitHub CLI.
2. Configure la identidad de sus commits. Use la dirección «noreply» de GitHub para no publicar su correo (GitHub → *Settings* → *Emails* → *Keep my email addresses private* la muestra):

   ```powershell
   git config --global user.name  "Victor Hipp Murcia"
   git config --global user.email "<id>+VictorHipp@users.noreply.github.com"
   ```

3. Inicie sesión en GitHub una vez para poder subir cambios: `gh auth login` (o Git abrirá el navegador la primera vez que haga `git push`).

Herramientas opcionales:

| Herramienta | Para qué | Descarga oficial | O con winget |
|---|---|---|---|
| Visual Studio 2022 o posterior (Community es gratis) con la carga de trabajo *Desarrollo de escritorio de .NET*, o VS Code / Rider | Editar y depurar el código. Abra `codigo\CadMepRevit.sln` | <https://visualstudio.microsoft.com/es/downloads/> | `winget install --id Microsoft.VisualStudio.2022.Community -e --source winget` |
| Autodesk Revit 2023–2027 | Probar dentro de Revit (no hace falta para compilar) | Su cuenta de Autodesk | — |

`CLAUDE.md` (raíz) y `codigo/CLAUDE.md` están en el repositorio: Claude Code recibe las reglas y la arquitectura del proyecto en cualquier PC.

## 4.2 Rutina diaria con Git

```powershell
git pull                      # al empezar: trae los últimos cambios
# ... trabajar ...
git add -A
git commit -m "Describa el cambio"
git push                      # al terminar: sube sus cambios
```

## 4.3 Archivos que no van en el repositorio

| Elemento | Por qué | Cómo recuperarlo |
|---|---|---|
| `codigo/familias_uniones/*.rfa` (12 familias de uniones) | Contenido de la biblioteca de Autodesk, no cubierto por la licencia de este proyecto | [Descarga e instalación § 2.4](02-instalacion.md#24-conseguir-las-12-familias-de-uniones) |
| `Instalar_RevitAutomation.exe` | Archivo generado | [Descarga e instalación § 2.5](02-instalacion.md#25-crear-el-instalador) |
| `bin/`, `obj/` | Resultado de la compilación | Se generan solos |
| `codigo/ejemplos/ejemplo_mep.dwg` | Lo generan las pruebas y sale distinto en cada ejecución; se conserva la versión DXF | No hace falta |
| Planos reales de clientes, `Pruebas/`, `FIRMAS_*.json` | Confidenciales | Nunca se guardan; vea [Pruebas](06-pruebas.md#64-planos-reales-confidenciales) |

## 4.4 Compilar a mano

Una versión de Revit:

```powershell
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net48           -p:RevitYear=2023
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net48           -p:RevitYear=2024
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net8.0-windows  -p:RevitYear=2025
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net8.0-windows  -p:RevitYear=2026
dotnet build src/RevitAutomation/RevitAutomation.csproj -c Release -f net10.0-windows -p:RevitYear=2027
```

Si compila varios años seguidos, dé a cada uno su carpeta intermedia, p. ej. `-p:IntermediateOutputPath=obj\y2026\`.

La solución (2025 + 2027 si Revit 2027 está instalado o con `BuildForRevit2027=true`):

```powershell
dotnet build CadMepRevit.sln -c Release -p:BuildForRevit2027=true
```

### Qué hace `crear_instalador.ps1`

1. Busca una carpeta de .NET con el SDK 10 (o posterior) y el SDK 8 (`DOTNET_ROOT`, `%USERPROFILE%\.dotnet`, `C:\Program Files\dotnet` o el `PATH`). Si falta alguno, pregunta «¿Instalarlo ahora? (S/N)» y, con «S», ejecuta `preparar_pc.ps1 -SoloDotnet`.
2. Pasa las pruebas del motor (`tests/CadMepRevit.Tests`) y cada prueba de herramienta de `tests/RevitAutomation.Pruebas`.
3. Compila `RevitAutomation.csproj` para 2023, 2024 (`net48`), 2025, 2026 (`net8.0-windows`) y 2027 (`net10.0-windows`) **sin símbolos de depuración**, cada una en su carpeta `obj\paquete\<año>`, y quita los `RevitAPI*.dll` y los `.pdb`.
4. Añade la guía de usuario, los planos de ejemplo y las `Familias` de reserva.
5. Comprueba que ningún archivo del paquete contiene el nombre de usuario, la ruta del perfil ni el nombre del equipo.
6. Comprime el paquete, lo incrusta en `instalador/InstaladorCadMep.csproj` (WinForms, .NET Framework 4.8) y genera `Instalar_RevitAutomation.exe`.

### Cómo se elige la versión de Revit (`src/Revit.props`)

- `RevitYear` elige la plataforma: 2023/2024 → `net48`, 2025/2026 → `net8.0-windows`, 2027 → `net10.0-windows`. Sin `RevitYear`: `net48` → 2024, `net8.0-windows` → 2025, `net10.0-windows` → 2027.
- Usa `C:\Program Files\Autodesk\Revit <año>\RevitAPI.dll` / `RevitAPIUI.dll` si existen; si no, los paquetes NuGet `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` (`2023.4.0`, `2024.3.0`, `2025.4.60`, `2026.4.10`, `2027.4.0`), solo para compilar.
- El objetivo `ValidateRevitApiVersion` detiene la compilación si la versión principal de la API no corresponde al año.
- Define la constante `REVIT<año>` (p. ej. `REVIT2026`) para código condicional.

### Ajustes comunes (`Directory.Build.props`)

C# `latest`, tipos de referencia anulables, usings implícitos, versión y metadatos de copyright. SourceLink y las consultas de control de código fuente están desactivados (fallaban en una unidad de red). Para `net48` añade PolySharp, `System.Text.Json` 8.0.5 y un `using CadMepRevit.Compat` global.

## 4.5 Reglas de código

Vienen de [`codigo/CLAUDE.md`](../../codigo/CLAUDE.md) y se aplican a todo cambio:

- Las reglas de reconocimiento y geometría deben valer para planos de **cualquier oficina**: nunca adaptarlas a un nombre de archivo de prueba, una capa exacta, unas coordenadas o un solo plano.
- Usar solo tipos, métodos y parámetros de la API de Revit que **compilen en todas las versiones admitidas**. Si una llamada no compila, se considera que no existe. Revit 2023 no tiene `ElementId.Value` ni `new ElementId(long)`: use `RevitIds.FromValue(long)` (`src/CadMepRevit/Compat/RevitApiCompat.cs`).
- Escribir el código para .NET 8. Si la compilación `net48` falla por un miembro de la BCL que falta, añádalo a `src/CadMepRevit.Core/Compat/NetFrameworkCompat.cs` en lugar de cambiar el código común.
- Tomar las medidas del plano o de otra fuente identificable; marcar los valores por defecto como `SUPUESTO` en el informe.
- Todos los cambios del modelo de un clic en la cinta van en un solo `TransactionGroup` asimilado (un solo Ctrl+Z).
- Textos de la cinta, ventanas, informes, ayudas y **comentarios del código en español**. Los mensajes de error dicen qué falló **y cómo corregirlo**.
- Mantener **ACadSharp en 3.8.35** (otros complementos cargan la misma DLL dentro de Revit; otra versión puede chocar).
- WinForms y Revit comparten nombres de tipos (`Form`, `Control`, `TaskDialog`…): use alias como `using Form = System.Windows.Forms.Form;`.
- Informar por separado de la compilación, las pruebas fuera de Revit y la verificación dentro de Revit.
- Guardar las especificaciones en `codigo/docs/ESPEC_*.md`.
- Antes de aceptar un cambio en el motor, comparar los planes antes y después (vea [Pruebas](06-pruebas.md#63-comparar-planes-antes-de-aceptar-un-cambio)).
