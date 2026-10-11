namespace PulseSharp.Tests;

internal sealed class TempDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "pulsesharp-tests", $"{Guid.NewGuid():N}.db");

    public TempDatabase()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public string ConnectionString
    {
        get
        {
            return $"Data Source={_path};Pooling=False";
        }
    }

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
