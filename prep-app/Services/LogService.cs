using System.Collections.ObjectModel;
using System.Windows.Threading;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class LogService : ILogService
{
    private readonly ObservableCollection<string> _logLines;
    private readonly Dispatcher _dispatcher;
    private SsdLogger? _logger;

    public LogService(ObservableCollection<string> logLines, Dispatcher dispatcher)
    {
        _logLines = logLines;
        _dispatcher = dispatcher;
    }

    public void SetLogger(string rootPath, string logPrefix)
    {
        _logger = new SsdLogger(rootPath, logPrefix);
    }

    public void AppendLog(string message)
    {
        _dispatcher.Invoke(() => _logLines.Add(message));
    }

    public void Info(string message)
    {
        _logger?.Info(message);
    }

    public void Error(string message)
    {
        _logger?.Error(message);
    }
}
