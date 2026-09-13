namespace IncidentCompass.UnitTests;

/// <summary>
/// A throwaway model directory under the system temp path, removed when the test ends.
/// </summary>
internal sealed class LocalOnnxTestDirectory : IDisposable
{
    public LocalOnnxTestDirectory()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "incidentcompass-local-onnx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(FullPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
