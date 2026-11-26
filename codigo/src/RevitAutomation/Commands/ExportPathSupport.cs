namespace RevitAutomation.Addin.Commands;

// Revit exporters receive a short local workspace, never the user's deep destination tree.
// Publication and recovery remain owned by the existing PDF/CAD publishers.
internal static class ExportPathSupport
{
    internal static string CreateStaging(string format)
    {
        if(format is not ("pdf" or "cad"))throw new ArgumentException("Formato temporal no valido.",nameof(format));
        string path=Path.Combine(Path.GetTempPath(),"ra-"+format+"-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static bool HasWriteAccess(string folder)
    {
        string probe=Path.Combine(folder,".ra-"+Guid.NewGuid().ToString("N")+".tmp");
        try {using(File.Create(probe)){} File.Delete(probe);return true;}
        // Do not misreport a path limit as a permissions error.
        catch(PathTooLongException){throw;}
        catch {return false;}
        finally {try {if(File.Exists(probe))File.Delete(probe);}catch {}}
    }

    internal static string ErrorMessage(Exception error,string? destination=null)
    {
        bool TooLong(Exception e)=>e is PathTooLongException||(e.HResult&0xFFFF)==206||
            e is AggregateException aggregate&&aggregate.InnerExceptions.Any(TooLong)||
            e.InnerException is not null&&TooLong(e.InnerException);
        if(!TooLong(error))return error.Message;
        return "La ruta completa o uno de sus nombres supera el limite admitido por Windows, Revit o el servidor.\n"+
            (string.IsNullOrWhiteSpace(destination)?"":"Carpeta ("+destination!.Length+" caracteres): "+destination+"\n")+
            "Selecciona una carpeta mas cercana a la raiz o acorta el nombre del archivo. Si el servidor sigue rechazando la ruta, exporta a una carpeta local corta y copia los archivos desde el Explorador.\n\n"+
            "Detalle original: "+error.Message;
    }
}
