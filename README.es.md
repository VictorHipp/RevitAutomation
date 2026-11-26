# RevitAutomation

[English](README.md) · **Español**

Complemento para **Autodesk Revit 2023, 2024, 2025, 2026 y 2027** que añade una sola pestaña en la cinta, **«RevitAutomation»**, con herramientas para el diseño de instalaciones (MEP). Su motor principal, **CAD-REV**, convierte un plano CAD (DWG/DXF) en un modelo de Revit: tuberías, conductos, bandejas, piezas de unión, luminarias y dispositivos.

## Qué hace

| Panel | Desplegable | Botones |
|---|---|---|
| CAD-REV | CAD-REV | Tuberías · Conductos · Bandejas · Luminarias · Equipos y PCI (del plano DWG/DXF a Revit) |
| Instalaciones | Salas técnicas | Contadores de agua · Electricidad · PCI · Fontanería |
| Instalaciones | Fontanería | Patinillo de fontanería · Aislamiento RITE · Llaves de local húmedo |
| Etiquetado | Etiquetado | Tuberías · Conductos · Bandejas · Familias · Luminarias (seleccionadas) |
| Documentación | Planos | Fontanería · Climatización · Electricidad · Iluminación · Genéricas y PCI · Conjunto completo |
| Documentación | Exportar | Exportar PDF · Exportar CAD |
| Región | Región | Región de sectorización |

Cada operación se agrupa en un solo paso de deshacer (**Ctrl+Z** deshace todo) y termina con un informe de lo modelado, lo no modelado y por qué.

## Documentación

| | |
|---|---|
| [1. Visión general](docs/es/01-vision-general.md) | Qué hace cada herramienta y cómo encajan las piezas |
| [2. Instalación](docs/es/02-instalacion.md) | Instalar, actualizar y desinstalar el complemento |
| [3. Guía de uso](docs/es/03-guia-de-uso.md) | Cómo usar CAD-REV y las demás herramientas; informes y archivos de ajustes |
| [4. Desarrollo](docs/es/04-desarrollo.md) | Requisitos, compilar cada versión de Revit, generar el instalador, **preparar un PC nuevo** |
| [5. Arquitectura](docs/es/05-arquitectura.md) | Proyectos, flujo de procesamiento y mapa del código |
| [6. Pruebas](docs/es/06-pruebas.md) | Pruebas automáticas fuera de Revit, pruebas de herramientas, comparación de planes |
| [7. Estado y limitaciones](docs/es/07-estado-y-limitaciones.md) | Qué está verificado, qué falta comprobar en Revit, límites conocidos |
| [8. Propiedad intelectual](docs/es/08-propiedad-intelectual.md) | Derechos de autor, licencia, componentes de terceros y marcas |

Las especificaciones de diseño y el historial de cambios detallados están en [`codigo/docs/`](codigo/docs/), [`codigo/LEEME_VERSION_COMBINADA.txt`](codigo/LEEME_VERSION_COMBINADA.txt) y [`codigo/TRASPASO_PROYECTO.txt`](codigo/TRASPASO_PROYECTO.txt).

## Inicio rápido (desarrollo)

```powershell
git clone https://github.com/VictorHipp/RevitAutomation.git
cd RevitAutomation\codigo
# 1. Instale los SDK de .NET 10 y 8, y Git                 → docs/es/04-desarrollo.md § 4.1
# 2. Copie las 12 familias de uniones en codigo\familias_uniones\   → § 4.2
.\CREAR_INSTALADOR.cmd     # pasa las pruebas, compila las 5 versiones de Revit y crea ..\Instalar_RevitAutomation.exe (§ 4.3)
```

Paso a paso: [herramientas que hay que descargar](docs/es/04-desarrollo.md#41-herramientas-que-hay-que-descargar) · [conseguir las 12 familias](docs/es/04-desarrollo.md#42-conseguir-las-12-familias-de-uniones) · [crear el instalador](docs/es/04-desarrollo.md#43-crear-el-instalador-paso-a-paso) · [preparar un PC nuevo](docs/es/04-desarrollo.md#44-preparar-un-pc-nuevo).

## Estructura del repositorio

```
RevitAutomation/
├── README.md / README.es.md     Portada (inglés / español)
├── LICENSE                      Licencia MIT
├── docs/
│   ├── en/                      Documentación en inglés
│   └── es/                      Documentación en español
└── codigo/                      Código fuente (solución de Visual Studio CadMepRevit.sln)
    ├── src/                     Proyectos del complemento (RevitAutomation, CadMepRevit, CadMepRevit.Core)
    ├── tests/                   Programas de prueba de consola (fuera de Revit)
    ├── instalador/              Proyecto del instalador (Instalar_RevitAutomation.exe)
    ├── docs/                    Especificaciones y guía para el usuario final
    ├── ejemplos/, samples/      Planos DWG/DXF sintéticos de prueba
    └── familias_uniones/        Familias de uniones de reserva (no están en Git: contenido de Autodesk)
```

## Licencia

Copyright © 2026 Victor Hipp Murcia. Publicado con la [licencia MIT](LICENSE). Los componentes de terceros conservan sus propias licencias; vea [Propiedad intelectual](docs/es/08-propiedad-intelectual.md).

*Autodesk, Revit, AutoCAD, DWG y DWF son marcas registradas o marcas comerciales de Autodesk, Inc. Este proyecto no está afiliado, respaldado ni patrocinado por Autodesk.*
