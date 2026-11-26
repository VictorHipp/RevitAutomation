using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

/// <summary>
/// Lee los TEXTOS de un DWF (versión 6, «ePlot», el que exportan AutoCAD y Revit del espacio modelo) para sumarlos a
/// los del DWG del mismo plano. Solo textos: la geometría se recorre únicamente para seguir el punto actual, porque
/// en el formato W2D cada posición es relativa a la anterior.
///
/// Cómo se ha hecho (sin documentación del formato a mano): se ha deducido de los DWF de prueba y se ha COMPROBADO
/// contra su DWG: en un plano de fontanería (103 textos) y otro de ventilación (73, algunos girados 90°) todos
/// caen a menos de 3 cm de su gemelo del DWG y con el mismo giro. Solo se
/// admiten los códigos vistos en esos archivos; ante cualquier otro se para y se avisa (se sigue solo con el DWG).
///
/// Paso a coordenadas del DWG: el propio W2D trae la matriz «(Units … ((a b c d)(e f g h)(i j k l)(m n o p)))» que
/// lleva las coordenadas del dibujo a las del DWF; se invierte.
/// </summary>
public static class DwfReader
{
    public sealed class Result
    {
        public List<CadText> Texts { get; } = new();
        public List<string> Notes { get; } = new();
        /// <summary>Páginas (hojas) leídas y omitidas.</summary>
        public int PagesRead, PagesSkipped;
    }

    /// <summary>Lee los textos del DWF en unidades del DWG. No lanza excepciones: los problemas van a <see cref="Result.Notes"/>.</summary>
    public static Result Read(string path)
    {
        var res = new Result();
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[12];
            int n = fs.Read(head, 0, 12);
            string h = Encoding.ASCII.GetString(head, 0, n);
            if (!h.StartsWith("(DWF V"))
            {
                res.Notes.Add($"'{Path.GetFileName(path)}' no es un DWF de la versión 6 (empieza por «{h}»). Si es un DWFx, expórtelo como DWF. Se usan solo los textos del DWG.");
                return res;
            }
            fs.Position = 0;
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var descriptors = zip.Entries.Where(e => e.FullName.EndsWith("descriptor.xml", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var w2d in zip.Entries.Where(e => e.FullName.EndsWith(".w2d", StringComparison.OrdinalIgnoreCase)))
            {
                string folder = w2d.FullName.Contains('\\') || w2d.FullName.Contains('/') ? Regex.Split(w2d.FullName, @"[\\/]")[0] : "";
                string page = PageName(descriptors.FirstOrDefault(d => d.FullName.StartsWith(folder, StringComparison.OrdinalIgnoreCase)));
                if (page.Length > 0 && !page.Equals("Model", StringComparison.OrdinalIgnoreCase) && !page.Equals("Modelo", StringComparison.OrdinalIgnoreCase))
                {
                    res.PagesSkipped++;
                    res.Notes.Add($"DWF: la hoja «{page}» es una presentación (no el espacio modelo): sus textos no coinciden con el DWG y no se leen.");
                    continue;
                }
                using var ms = new MemoryStream();
                using (var s = w2d.Open()) s.CopyTo(ms);
                ReadW2d(ms.ToArray(), res);
                res.PagesRead++;
            }
            if (res.PagesRead == 0 && res.PagesSkipped == 0) res.Notes.Add("DWF: no contiene ningún dibujo 2D (W2D).");
        }
        catch (Exception ex)
        {
            res.Notes.Add($"DWF '{Path.GetFileName(path)}' no se pudo leer ({ex.Message}). Se usan solo los textos del DWG.");
        }
        return res;
    }

    private static string PageName(ZipArchiveEntry? descriptor)
    {
        if (descriptor is null) return "";
        using var r = new StreamReader(descriptor.Open(), Encoding.UTF8);
        var m = Regex.Match(r.ReadToEnd(), @"<ePlot:Page[^>]*\sname=""([^""]*)""");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Altura de celda de la fuente del DWF / altura del texto del DWG (medida en los planos de prueba).</summary>
    internal const double CellToTextHeight = 1.39;

    /// <summary>Recorre un flujo W2D. Si encuentra un código no comprobado, se detiene y lo dice (los textos ya leídos valen).</summary>
    internal static void ReadW2d(byte[] b, Result res)
    {
        int i = Encoding.ASCII.GetString(b, 0, Math.Min(12, b.Length)).StartsWith("(W2D V") ? 12 : 0;
        long cx = 0, cy = 0;
        double[]? m = null; // a b e f m n de la matriz (dibujo → DWF)
        var layers = new Dictionary<int, string>();
        string layer = "";
        int fontHeight = 0, fontRotation = 0;
        var found = new List<(long X, long Y, string S, string Layer, int H, int Rot)>();

        short I16(int p) => BitConverter.ToInt16(b, p);
        int I32(int p) => BitConverter.ToInt32(b, p);
        int Pts(int p, int count, int size)
        {
            for (int k = 0; k < count; k++)
            {
                cx += size == 2 ? I16(p) : I32(p);
                cy += size == 2 ? I16(p + size) : I32(p + size);
                p += 2 * size;
            }
            return p;
        }
        // Nº de puntos: 1 byte; si es 0, sigue un nº de 2 bytes al que se suman 256 (aparece en FONTANERÍA; con esta regla
        // todos sus textos posteriores caen sobre los del DWG, así que está comprobada).
        int Count8(ref int p) { int c = b[p++]; if (c == 0) { c = BitConverter.ToUInt16(b, p) + 256; p += 2; } return c; }
        (string S, int End) Str(int p)
        {
            if (b[p] == '\'') { int end = Array.IndexOf(b, (byte)'\'', p + 1); if (end < 0) throw new FormatException("cadena sin cerrar"); return (Encoding.Latin1.GetString(b, p + 1, end - p - 1), end + 1); }
            if (b[p] == '{') { int c = I32(p + 1); if (c < 0 || p + 5 + 2 * c >= b.Length) throw new FormatException("cadena Unicode inválida"); return (Encoding.Unicode.GetString(b, p + 5, 2 * c), p + 5 + 2 * c + 1); }
            throw new FormatException("cadena desconocida");
        }
        int ExtAscii(int p)
        {
            int depth = 0;
            for (; p < b.Length; p++)
            {
                byte c = b[p];
                if (c == '\'') { p = Array.IndexOf(b, (byte)'\'', p + 1); if (p < 0) throw new FormatException("texto sin cerrar"); continue; }
                // Cadena Unicode dentro del paréntesis («{» + nº de CARACTERES + UTF-16 + «}»), p. ej. en SourceFilename y Embed.
                if (c == '{') { p = Str(p).End - 1; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return p + 1; }
            }
            throw new FormatException("paréntesis sin cerrar");
        }

        try
        {
            while (i < b.Length)
            {
                byte op = b[i];
                switch (op)
                {
                    case (byte)' ': case 0x0D: case 0x0A: case 0x09: i++; break;
                    case (byte)'(':
                    {
                        int end = ExtAscii(i);
                        string cmd = Encoding.Latin1.GetString(b, i + 1, end - i - 2);
                        if (cmd.StartsWith("EndOfDWF")) { i = b.Length; break; }
                        if (cmd.StartsWith("Units")) m = UnitsMatrix(cmd);
                        else if (cmd.StartsWith("Layer"))
                        {
                            var lm = Regex.Match(cmd, @"^Layer\s+(\d+)\s*(.*)$", RegexOptions.Singleline);
                            if (lm.Success)
                            {
                                int num = int.Parse(lm.Groups[1].Value, CultureInfo.InvariantCulture);
                                string name = lm.Groups[2].Value.Trim().Trim('\'');
                                if (name.Length > 0) layers[num] = name;
                                layer = layers.GetValueOrDefault(num, "");
                            }
                        }
                        i = end; break;
                    }
                    case (byte)'{':                                                        // bloque binario con tamaño: se salta
                    {
                        int len = I32(i + 1);
                        if (len < 0 || i + 5 + len > b.Length) throw new FormatException("bloque binario inválido");
                        i = i + 5 + len; break;
                    }
                    case (byte)'O':                                                        // nuevo origen ABSOLUTO de las coordenadas relativas
                        cx = I32(i + 1); cy = I32(i + 5); i += 9;                          // (comprobado con VENTILACIÓN: así sus textos caen sobre el DWG)
                        break;
                    case 0x10: { i++; int c = Count8(ref i); i = Pts(i, c, 2); break; }   // polilínea, puntos de 16 bits
                    case 0x0C: i = Pts(i + 1, 2, 2); break;                                // línea, 16 bits
                    case (byte)'l': i = Pts(i + 1, 2, 4); break;                           // línea, 32 bits
                    case 0x92: i = Pts(i + 1, 1, 4) + 8; break;                            // arco: centro + radio + ángulos
                    case (byte)'e': i = Pts(i + 1, 1, 4) + 14; break;                      // elipse: centro + ejes + ángulos + giro
                    case (byte)'c': i += 2; break;                                         // color por índice
                    case 0x03: i += 5; break;                                              // color RGBA
                    case 0x17: i += 5; break;                                              // grosor de línea
                    case 0xAC: i += 2; break;                                              // (atributo de 1 byte)
                    case (byte)'F': case (byte)'f': i++; break;                            // relleno sí / no
                    case 0x06:                                                             // fuente: máscara + campos
                    {
                        int mask = BitConverter.ToUInt16(b, i + 1); i += 3;
                        // Solo los campos vistos en los planos comprobados: 0x02–0x10 (1 byte), 0x20 altura (4), 0x40 giro (2), 0x80 (2).
                        if ((mask & ~0xFE) != 0) throw new FormatException($"fuente con campos no comprobados (0x{mask:X})");
                        foreach (int bit in new[] { 0x02, 0x04, 0x08, 0x10 }) if ((mask & bit) != 0) i++;
                        if ((mask & 0x20) != 0) { fontHeight = I32(i); i += 4; }
                        if ((mask & 0x40) != 0) { fontRotation = BitConverter.ToUInt16(b, i); i += 2; }
                        if ((mask & 0x80) != 0) i += 2;
                        break;
                    }
                    case 0x18:                                                             // texto con opciones: posición + cadena + opciones
                    {
                        i = Pts(i + 1, 1, 4);
                        var (s, end) = Str(i);
                        found.Add((cx, cy, s, layer, fontHeight, fontRotation));
                        // 2 bytes + recuadro del texto (4 puntos relativos de 32 bits, que SÍ mueven el punto actual) + 1 byte.
                        // Comprobado: sin sumar el recuadro, los textos que siguen salían desplazados exactamente lo que suma.
                        i = Pts(end + 2, 4, 4) + 1; break;
                    }
                    case (byte)'x':                                                        // texto: posición relativa + cadena
                    {
                        i = Pts(i + 1, 1, 4);
                        var (s, end) = Str(i);
                        found.Add((cx, cy, s, layer, fontHeight, fontRotation));
                        i = end; break;
                    }
                    default:
                        throw new FormatException($"código 0x{op:X2} no reconocido en el byte {i} de {b.Length}");
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            res.Notes.Add($"DWF: lectura detenida ({ex.Message}); se usan los {found.Count} textos leídos hasta ahí. " +
                          "Si faltan rótulos, envíe el DWF para ampliar el lector.");
        }

        if (m is null)
        {
            if (found.Count > 0) res.Notes.Add("DWF: no indica la relación con las coordenadas del dibujo («Units»): sus textos no se pueden situar y no se usan.");
            return;
        }
        // Inversa de [x y] → [a*x + e*y + mx, b*x + f*y + my]
        double a = m[0], bb = m[1], e = m[2], f = m[3], mx = m[4], my = m[5];
        double det = a * f - bb * e;
        if (Math.Abs(det) < 1e-12) { res.Notes.Add("DWF: matriz de unidades no válida; sus textos no se usan."); return; }
        double scale = Math.Sqrt(Math.Abs(det));
        foreach (var (X, Y, S, Layer, H, Rot) in found)
        {
            string value = S.Replace("%%C", "Ø").Replace("%%c", "Ø").Trim();
            if (value.Length == 0) continue;
            double lx = X - mx, ly = Y - my;
            double x = (f * lx - e * ly) / det, y = (-bb * lx + a * ly) / det;
            // La altura de la fuente del DWF es la de su celda: 1,39 veces la del texto en el DWG (medido: 1,39–1,41 en
            // FONTANERÍA y VENTILACIÓN). Se convierte a la del DWG, que es la que usan los rótulos para medir distancias.
            double height = H > 0 ? H / scale / CellToTextHeight : 0;
            double rot = Rot * 2 * Math.PI / 65536.0;
            double width = CadReader.CharWidthFactor * Math.Max(height, 1e-9) * value.Length; // mismo ancho aproximado que en el DWG
            double cs = Math.Cos(rot), sn = Math.Sin(rot);
            // La posición del DWF es el inicio de la línea base: el centro está a medio ancho y media altura.
            var center = new P3(x + cs * width / 2 - sn * height / 2, y + sn * width / 2 + cs * height / 2, 0);
            res.Texts.Add(new CadText
            {
                EntityIndex = -1, Layer = Layer, Source = "DWF", Value = value, Height = height,
                Position = new P3(x, y, 0), CenterPoint = center, RotationRad = rot,
                WidthApprox = width, HeightApprox = height
            });
        }
    }

    /// <summary>a, b, e, f, m, n de «(Units nombre ((a b c d)(e f g h)(i j k l)(m n o p)))».</summary>
    private static double[]? UnitsMatrix(string cmd)
    {
        var nums = Regex.Matches(cmd, @"-?\d+(\.\d+)?([eE][-+]?\d+)?").Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).ToList();
        if (nums.Count < 16) return null;
        var v = nums.Skip(nums.Count - 16).ToList();
        return new[] { v[0], v[1], v[4], v[5], v[12], v[13] };
    }

    /// <summary>
    /// Suma a los textos del plano los del DWF que NO están ya en el DWG (mismo texto a menos de dos alturas o de 1 m
    /// de su centro). Devuelve cuántos se han añadido y une los nuevos con sus directrices de líneas.
    /// </summary>
    public static int MergeInto(CadDrawing drawing, Result dwf, double unitToMm) => Merge(drawing, dwf, unitToMm).Added;

    /// <summary>
    /// ¿Coincide un texto del DWF con este del DWG? La posición del DWF es el inicio de la línea base; la del DWG es esa
    /// misma (TEXT) o la esquina superior (MTEXT: una altura más arriba, en la dirección del texto). Tolerancia 5 cm.
    /// </summary>
    public static bool SamePlace(CadText dwg, CadText dwf, double unitToMm)
    {
        double h = Math.Abs(dwg.Height), sx = Math.Sin(dwg.RotationRad) * h, sy = -Math.Cos(dwg.RotationRad) * h;
        double d1 = Math.Sqrt(Math.Pow(dwg.Position.X - dwf.Position.X, 2) + Math.Pow(dwg.Position.Y - dwf.Position.Y, 2));
        double d2 = Math.Sqrt(Math.Pow(dwg.Position.X + sx - dwf.Position.X, 2) + Math.Pow(dwg.Position.Y + sy - dwf.Position.Y, 2));
        return Math.Min(d1, d2) * unitToMm <= 50;
    }

    /// <summary>
    /// Suma los textos del DWF que el DWG no tiene, pero SOLO si el DWF está alineado con el DWG: entre los textos que
    /// están en los dos (mismo contenido), al menos el 80 % tienen que caer en el mismo sitio. Si no (otro plano, otra
    /// revisión o un DWF que no se ha sabido leer), no se añade ninguno y se dice. Sin textos comunes no se puede
    /// comprobar: se añaden y se avisa.
    /// </summary>
    public static (int Added, int Shared, int Aligned, string Note) Merge(CadDrawing drawing, Result dwf, double unitToMm)
    {
        var byValue = drawing.Texts.GroupBy(o => o.Value.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var fresh = new List<CadText>();
        int shared = 0, aligned = 0;
        foreach (var t in dwf.Texts)
        {
            if (byValue.TryGetValue(t.Value, out var twins))
            {
                shared++;
                if (twins.Any(o => SamePlace(o, t, unitToMm))) { aligned++; continue; }
            }
            fresh.Add(t);
        }
        if (shared >= 5 && aligned < 0.8 * shared)
            return (0, shared, aligned, $"DWF NO usado: de {shared} textos que están también en el DWG, solo {aligned} caen en el mismo sitio. " +
                                         "Puede ser de otro plano o de otra revisión: compruebe que es el DWF del mismo DWG.");
        // Los que tienen gemelo con el mismo contenido pero en otro sitio también son nuevos (otro rótulo igual en otro tubo).
        foreach (var t in fresh) drawing.Texts.Add(t);
        if (fresh.Count > 0) CadReader.RelinkLineLeaders(drawing, fresh);
        string note = shared == 0
            ? "no hay textos comunes con el DWG para comprobar que el DWF está alineado: revise los rótulos aplicados"
            : $"alineado con el DWG ({aligned} de {shared} textos comunes en el mismo sitio)";
        return (fresh.Count, shared, aligned, note);
    }
}
