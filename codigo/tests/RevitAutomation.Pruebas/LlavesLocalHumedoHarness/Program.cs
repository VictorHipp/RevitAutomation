using RevitAutomation.Addin.Commands.LlavesLocalHumedo;
int checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL: " + label); checks++; Console.WriteLine("PASS: " + label); }
var source = new[] { new LlavesPoint(0,0,0), new LlavesPoint(1000,0,0), new LlavesPoint(1000,2000,0), new LlavesPoint(0,2000,0), new LlavesPoint(500,1000,0) };
Check(LlavesAlignment.Matches(source, source, 10), "Coincidencia exacta");
Check(LlavesAlignment.Matches(source, source.Select(p => p with { X = p.X + 9 }), 10), "Error de 9 mm admisible");
Check(!LlavesAlignment.Matches(source, source.Select(p => p with { X = p.X + 11 }), 10), "No acepta puntos de celdas vecinas fuera de tolerancia");
Check(!LlavesAlignment.Matches(source, source.Select(p => p with { X = p.X * 1000, Y = p.Y * 1000 }), 10), "Rechaza unidades incorrectas");
Check(!LlavesAlignment.Matches(source, source.Select(p => p with { Z = 200 }), 10), "Rechaza desfase vertical");
Check(!LlavesAlignment.Matches(source, Enumerable.Repeat(source[0], 20), 10), "Repeticiones de un punto no validan el vínculo");
Check(!LlavesAlignment.Matches(Array.Empty<LlavesPoint>(), source, 10), "Rechaza vínculo sin geometría");
Check(!LlavesAlignment.Matches(source, source.Take(2), 10), "Exige tres puntos distintos");
Check(LlavesAlignment.Matches(source, source.Take(4).Append(new LlavesPoint(9000,9000,0)), 10), "Admite 80 por ciento de coincidencias");
Check(!LlavesAlignment.Matches(source, source.Take(3).Concat(new[] {new LlavesPoint(9000,9000,0),new LlavesPoint(8000,8000,0)}), 10), "Rechaza 60 por ciento de coincidencias");
Check(!LlavesAlignment.Matches(source, source.Take(4).Append(new LlavesPoint(double.NaN,0,0)), 10), "Rechaza coordenadas no finitas");
Console.WriteLine($"LLAVES_TOTAL={checks} PASS={checks} FAIL=0");
