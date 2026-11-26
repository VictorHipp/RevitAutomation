namespace CadMepRevit.Core;

/// <summary>
/// Paso de coordenadas del objeto (OCS) a coordenadas generales (WCS) con el
/// "algoritmo del eje arbitrario" de DXF. Necesario para polilíneas, arcos y círculos
/// cuya normal no es +Z (por ejemplo, los reflejados con SIMETRÍA, que tienen normal -Z).
/// </summary>
public static class Ocs
{
    public static P3 ToWcs(CSMath.XYZ normal, double x, double y, double z)
    {
        double nx = normal.X, ny = normal.Y, nz = normal.Z;
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len < 1e-12 || (Math.Abs(nx) < 1e-12 && Math.Abs(ny) < 1e-12 && nz > 0)) return new P3(x, y, z);
        nx /= len; ny /= len; nz /= len;
        // Ax = (Wy × N) si N está cerca del eje Z; si no, (Wz × N)
        double ax, ay, az;
        if (Math.Abs(nx) < 1.0 / 64 && Math.Abs(ny) < 1.0 / 64) { ax = nz; ay = 0; az = -nx; }
        else { ax = -ny; ay = nx; az = 0; }
        double al = Math.Sqrt(ax * ax + ay * ay + az * az);
        ax /= al; ay /= al; az /= al;
        // Ay = N × Ax
        double bx = ny * az - nz * ay, by = nz * ax - nx * az, bz = nx * ay - ny * ax;
        return new P3(x * ax + y * bx + z * nx, x * ay + y * by + z * ny, x * az + y * bz + z * nz);
    }
}
