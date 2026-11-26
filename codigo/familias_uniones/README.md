# familias_uniones — fallback fitting families

**English** · [Español](LEEME.md)

The installer copies the `.rfa` files in this folder into the add-in's `Familias` folder. CAD-REV uses them as the **last-resort library** for elbows, tees, crosses and transitions when neither the project nor the office libraries have any (otherwise Revit cannot create the fittings).

## Why the files are not in Git

They are families from the **Autodesk Revit content library** (Spanish metric library), so they are Autodesk's property and are not covered by this project's MIT licence. They are excluded by the root `.gitignore` (`codigo/familias_uniones/*.rfa`). See [Intellectual property](../../docs/en/08-intellectual-property.md).

## What must be here before running `CREAR_INSTALADOR.cmd`

`crear_instalador.ps1` stops with an error if this folder has no `.rfa` file. The original set is:

| File | Fitting |
|---|---|
| `M_Codo - Genérico.rfa` | Generic pipe elbow |
| `M_Te - Genérica.rfa` | Generic pipe tee |
| `M_Cruz - Genérica.rfa` | Generic pipe cross |
| `M_Transición - Genérica.rfa` | Generic pipe transition |
| `M_Codo rectangular - Radio 1.rfa` | Rectangular duct elbow |
| `M_Te rectangular - Recto.rfa` | Rectangular duct tee |
| `M_Cruz rectangular -  Recta.rfa` | Rectangular duct cross (note the two spaces) |
| `M_Transición rectangular - Ángulo 2.rfa` | Rectangular duct transition |
| `M_Codo redondo 1.rfa` | Round duct elbow |
| `M_Te redonda - Recto.rfa` | Round duct tee |
| `M_Cruz redonda - Recto.rfa` | Round duct cross |
| `M_Transición redonda - Ángulo.rfa` | Round duct transition |

## Where and how to download them

Full instructions, including a script that copies them for you: [Development § 4.2](../../docs/en/04-development.md#42-getting-the-12-fitting-families). In short:

1. **From your private copy** (USB drive, private cloud): copy the 12 files back here after `git clone`.
2. **From your Autodesk Account:** <https://manage.autodesk.com> → **Products and Services** → **Revit** → year → **Libraries** → download the **Spain** content pack and run it. It installs into `C:\ProgramData\Autodesk\RVT <year>\Libraries\` (hidden folder); copy the 12 files from there.
3. **From Revit (2021.1 or later):** **Insert › Load Autodesk Family** → search for the name → **Load**; then in the Project Browser right-click the family → **Save…** as `.rfa` into this folder.
4. **Equivalent families** with other names also work: CAD-REV recognises fitting families by the meaning of their name (`Core/FittingNames.cs`) and checks their category and part type when loading them.

Then create the installer: [Development § 4.3](../../docs/en/04-development.md#43-creating-the-installer-step-by-step).
