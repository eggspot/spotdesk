using SpotDesk.Core.Import;
using SpotDesk.Core.Models;

namespace SpotDesk.UI.ViewModels;

/// <summary>Wraps MRemoteNgImporter for DI / testability.</summary>
public interface IMRemoteNgImporter
{
    Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path);
}

/// <summary>Bridges Core's DevolutionsImporter to the IDevolutionsImporter interface.</summary>
internal sealed class DevolutionsImporterAdapter : IDevolutionsImporter
{
    private readonly DevolutionsImporter _inner = new();

    public Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path, string? masterKey = null)
    {
        using var stream = File.OpenRead(path);
        var result = _inner.Import(stream, masterKey);
        return Task.FromResult<IReadOnlyList<ConnectionEntry>>(result.Connections);
    }
}

/// <summary>Bridges Core's RdpFileImporter to the IRdpFileImporter interface.</summary>
internal sealed class RdpFileImporterAdapter : IRdpFileImporter
{
    private readonly RdpFileImporter _inner = new();

    public Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path)
    {
        using var stream = File.OpenRead(path);
        var result = _inner.Import(stream);
        return Task.FromResult<IReadOnlyList<ConnectionEntry>>(result.Connections);
    }
}

/// <summary>Bridges Core's MRemoteNgImporter to the IMRemoteNgImporter interface.</summary>
internal sealed class MRemoteNgImporterAdapter : IMRemoteNgImporter
{
    private readonly MRemoteNgImporter _inner = new();

    public Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path)
    {
        using var stream = File.OpenRead(path);
        var result = _inner.Import(stream);
        return Task.FromResult<IReadOnlyList<ConnectionEntry>>(result.Connections);
    }
}

/// <summary>Wraps MobaXtermImporter for DI / testability.</summary>
public interface IMobaXtermImporter
{
    Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path);
}

/// <summary>Bridges Core's MobaXtermImporter to the IMobaXtermImporter interface.</summary>
internal sealed class MobaXtermImporterAdapter : IMobaXtermImporter
{
    private readonly MobaXtermImporter _inner = new();

    public Task<IReadOnlyList<ConnectionEntry>> ImportAsync(string path)
    {
        using var stream = File.OpenRead(path);
        var result = _inner.Import(stream);
        return Task.FromResult<IReadOnlyList<ConnectionEntry>>(result.Connections);
    }
}
