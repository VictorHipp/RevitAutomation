# 8. Propiedad intelectual

[← Índice](../../README.es.md) · [English](../en/08-intellectual-property.md)

## 8.1 Derechos de autor y licencia

**Copyright © 2026 Victor Hipp Murcia.** Todo el código fuente, los scripts y la documentación de este repositorio, salvo los elementos de terceros indicados más abajo, son obra del autor y se publican con la **licencia MIT** ([`LICENSE`](../../LICENSE)).

En resumen, la licencia MIT permite a cualquiera usar, copiar, modificar, fusionar, publicar, distribuir, sublicenciar y vender copias del software, **siempre que se conserven el aviso de copyright y el texto de la licencia** en todas las copias o partes sustanciales. El software se ofrece «tal cual», sin garantía de ningún tipo; el autor no responde de los daños derivados de su uso.

> Este resumen es informativo. El texto con validez legal es el original en inglés del archivo `LICENSE`.

El titular de los derechos y la licencia figuran también en los metadatos de las DLL compiladas y del instalador (`Directory.Build.props`).

## 8.2 Componentes de terceros

Estos componentes **no** están cubiertos por la licencia de este proyecto; cada uno conserva la suya.

| Componente | Uso | Licencia | ¿Se distribuye? |
|---|---|---|---|
| [ACadSharp](https://github.com/DomCR/ACadSharp) 3.8.35 | Lectura de DWG/DXF | MIT | Sí, dentro del instalador |
| [PolySharp](https://github.com/Sergio0694/PolySharp) 1.15.0 | Atributos del lenguaje C# para .NET Framework 4.8 | MIT | No (generador de código en compilación) |
| [System.Text.Json](https://github.com/dotnet/runtime) 8.0.5 | JSON en .NET Framework 4.8 | MIT | Sí, en las versiones 2023/2024 |
| API de Revit (`RevitAPI.dll`, `RevitAPIUI.dll`), del Revit instalado o de los paquetes NuGet `Nice3point.Revit.Api.*` | Referencia para compilar | © Autodesk, Inc. (vea la licencia de cada paquete) | **No**: `crear_instalador.ps1` las quita del paquete |
| Familias de la biblioteca de Revit en `codigo/familias_uniones/*.rfa` | Uniones de reserva | © Autodesk, Inc. | **No están en el repositorio.** Van dentro del instalador que usted genera en su equipo |

## 8.3 Familias de Autodesk y el instalador

Las 12 familias de uniones de reserva proceden de la biblioteca de contenido de Autodesk Revit. Están excluidas de este repositorio público (`.gitignore`) y, para que vayan dentro del instalador, hay que ponerlas en su equipo o dejar que `CREAR_INSTALADOR.cmd` las encuentre en las bibliotecas de Revit instaladas (vea [`codigo/familias_uniones/LEEME.md`](../../codigo/familias_uniones/LEEME.md)).

Como el `Instalar_RevitAutomation.exe` generado en su equipo lleva esas familias dentro, no lo publique (por ejemplo, como «Release» de GitHub) salvo que tenga derecho a redistribuirlas, o sustitúyalas antes por familias propias.

## 8.4 Marcas

Autodesk, Revit, AutoCAD, DWG y DWF son marcas registradas o marcas comerciales de Autodesk, Inc. o de sus filiales en EE. UU. y otros países. Aquí se citan solo para indicar compatibilidad. **Este proyecto no está afiliado, respaldado ni patrocinado por Autodesk, Inc.** Otros nombres pueden ser marcas de sus respectivos titulares.

## 8.5 Datos confidenciales de proyectos

El repositorio no contiene planos, informes ni nombres de proyectos de clientes. Los planos de ejemplo de `codigo/ejemplos` y `codigo/samples` son sintéticos. Quien contribuya debe mantenerlo así (vea [Pruebas § 6.4](06-pruebas.md#64-planos-reales-confidenciales)).

## 8.6 Contribuciones

Salvo que se indique lo contrario, toda contribución enviada a este repositorio se acepta con la misma licencia MIT.
