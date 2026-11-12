namespace Ranked.Backend.Notifications;

// Outgoing notifications (e.g. Discord). Calls are fire-and-forget and must never throw or block the caller;
// implementations load whatever they need from the database themselves (create their own scope).
public interface IRankedNotifier
{
	// A player joined the ranked queue; queueSize includes them.
	void PlayerQueued(Guid userId, int queueSize);

	// A match finished with a result (Completed status, ratings already applied and saved).
	void MatchCompleted(Guid matchId);
}

public sealed class NullRankedNotifier : IRankedNotifier
{
	public void PlayerQueued(Guid userId, int queueSize) { }
	public void MatchCompleted(Guid matchId) { }
}
