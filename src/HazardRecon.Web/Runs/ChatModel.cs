namespace HazardRecon.Web.Runs;

/// <summary>
/// Which model answers a question about a run.
///
/// A run that had AI analysis carries the model that produced its memo, and that
/// one answers - the conversation should not disagree with the document beside
/// it. A run reconciled without analysis has none, and then whatever the user
/// picked in the conversation answers instead.
///
/// Deliberately not written back to the run: model_id records what generated the
/// analysis, and asking a question afterwards generates nothing.
/// </summary>
internal static class ChatModel
{
    public static string? Choose(string? runModelId, string? requestedModelId)
    {
        if (!string.IsNullOrWhiteSpace(runModelId)) return runModelId.Trim();
        if (!string.IsNullOrWhiteSpace(requestedModelId)) return requestedModelId.Trim();

        // nothing to answer with - ChatService says so rather than calling the
        // gateway with an empty model id
        return null;
    }
}
