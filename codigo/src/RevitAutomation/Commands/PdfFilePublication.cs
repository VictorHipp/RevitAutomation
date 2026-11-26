namespace RevitAutomation.Addin.Commands;

// Publishes a validated PDF. Recovery state is independent of Revit so failure paths can be tested.
internal sealed class PdfFilePublication
{
    private readonly string _staging;
    private readonly Action<string,string> _move;
    internal bool PreserveStaging {get;private set;}

    internal PdfFilePublication(string staging,Action<string,string>? move=null)
    {
        _staging=staging;
        _move=move??File.Move;
    }

    internal void Publish(string stagedPdf,string finalPath,bool overwriteConfirmed)
    {
        string backup=Path.Combine(_staging,"previous.pdf");
        bool hasOriginal=File.Exists(finalPath);
        if(hasOriginal)
        {
            if(!overwriteConfirmed)throw new IOException("El archivo apareció durante la exportación; no se sobrescribirá.");
            // Set before moving: even an uncertain filesystem failure must not trigger backup deletion.
            PreserveStaging=true;
            _move(finalPath,backup);
        }
        try { _move(stagedPdf,finalPath); }
        catch(Exception publicationError)
        {
            if(hasOriginal)
            {
                try
                {
                    // Move never overwrites a file that another process may have created meanwhile.
                    _move(backup,finalPath);
                    PreserveStaging=false;
                }
                catch(Exception recoveryError)
                {
                    throw new IOException(
                        $"No se pudo publicar el PDF ni restaurar el anterior. Se conserva la carpeta de recuperación: {_staging}\n"+
                        $"Copia del PDF anterior: {backup}\nDestino: {finalPath}\n"+
                        $"Error de publicación: {publicationError.Message}\nError de recuperación: {recoveryError.Message}",
                        new AggregateException(publicationError,recoveryError));
                }
            }
            throw;
        }
        PreserveStaging=false;
    }

    internal void Cleanup()
    {
        if(PreserveStaging)return;
        try { Directory.Delete(_staging,true); } catch { }
    }
}
