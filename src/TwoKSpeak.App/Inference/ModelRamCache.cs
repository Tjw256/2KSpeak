using System.IO.MemoryMappedFiles;
using TwoKSpeak.App.Diagnostics;

namespace TwoKSpeak.App.Inference;

/// <summary>
/// Keeps model files resident in RAM by holding read-only mappings of them in the long-lived tray process and
/// touching every page once. The worker then loads from the OS file cache instead of the SSD, even after other
/// large programs have run. Pages stay shared and evictable, so this never starves the system.
/// </summary>
public sealed class ModelRamCache : IDisposable
{
    private const int PageSize = 4096;
    private readonly List<(MemoryMappedFile File, MemoryMappedViewAccessor View)> _mappings = [];

    public void Hold(IEnumerable<string> paths)
    {
        Release();
        foreach (var path in paths.Where(File.Exists))
        {
            var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _mappings.Add((file, file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)));
        }
        var views = _mappings.Select(m => m.View).ToList();
        var thread = new Thread(() => Touch(views)) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "2KSpeak model cache" };
        thread.Start();
    }

    private static void Touch(List<MemoryMappedViewAccessor> views)
    {
        try
        {
            long bytes = 0;
            foreach (var view in views)
            {
                for (long offset = 0; offset < view.Capacity; offset += PageSize)
                {
                    view.ReadByte(offset);
                }
                bytes += view.Capacity;
            }
            Log.Write($"model cache holding {bytes / (1024 * 1024)} MiB");
        }
        catch (ObjectDisposedException)
        {
            // Released while warming (settings changed); the new mapping set warms itself.
        }
    }

    public void Release()
    {
        foreach (var (file, view) in _mappings)
        {
            view.Dispose();
            file.Dispose();
        }
        _mappings.Clear();
    }

    public void Dispose() => Release();
}
