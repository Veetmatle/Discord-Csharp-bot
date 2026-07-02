namespace Discord_Bot_AI.Strategy.Notification;

/// <summary>
/// Interface defining the contract for match notification strategies.
/// </summary>
public interface IMatchNotification
{
    Task StartMonitoringAsync();
    Task StopMonitoringAsync();
}