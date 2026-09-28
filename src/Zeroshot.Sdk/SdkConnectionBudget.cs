namespace Zeroshot.Native;

/// <summary>
/// Keeps one OECP connection per ZeroshotClient free for control. SDK observations open their connection
/// before native subscription admission, and several SDK clients can share one native client, so the
/// subscription limit alone cannot stop observations from taking every connection.
/// Invariant: clients + SDK observations never exceed MaxOecpConnections. Direct connections opened on a
/// shared native client are the caller's own budget and are not covered.
/// </summary>
internal sealed class SdkConnectionBudget(int connections)
{
    private readonly object gate = new();
    private int clients;
    private int observations;

    public void AddClient()
    {
        lock (gate)
        {
            if (clients + observations == connections)
                throw new InvalidOperationException("MaxOecpConnections leaves no control connection for another SDK client.");
            clients++;
        }
    }

    public void RemoveClient() { lock (gate) clients--; }

    public void AddObservation()
    {
        lock (gate)
        {
            if (clients + observations == connections)
                throw new NativeSubscriptionException(NativeSubscriptionFailureKind.Admission);
            observations++;
        }
    }

    public void RemoveObservation() { lock (gate) observations--; }
}
