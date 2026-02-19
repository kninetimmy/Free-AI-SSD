namespace FreeAiSsd.Shared.Services;

public interface ILogService
{
    void AppendLog(string message);
    void Info(string message);
    void Error(string message);
}
