namespace Gen4.Local.SmokeTest.Notifications;

public sealed record ReceivedNotification(string Method, Guid Id, DateTime? Start, DateTime? End);

public sealed class ExpectedNotification(string method, Guid id, DateTime? start, DateTime? end)
{
    public string Method { get; } = method;

    public Guid Id { get; } = id;

    public DateTime? Start { get; } = start;

    public DateTime? End { get; } = end;

    public bool Matched { get; set; }
}

public sealed class NotificationTracker
{
    private readonly object sync = new();
    private readonly List<ExpectedNotification> expected = [];
    private readonly List<ReceivedNotification> received = [];

    public int ExpectedCount
    {
        get
        {
            lock (sync)
            {
                return expected.Count;
            }
        }
    }

    public int ReceivedCount
    {
        get
        {
            lock (sync)
            {
                return received.Count;
            }
        }
    }

    public bool AllMatched
    {
        get
        {
            lock (sync)
            {
                return expected.All(e => e.Matched);
            }
        }
    }

    public void Expect(string method, Guid id, DateTime? start = null, DateTime? end = null)
    {
        lock (sync)
        {
            var expectation = new ExpectedNotification(method, id, start, end);
            expected.Add(expectation);

            var match = received.FirstOrDefault(r => IsSame(expectation, r));
            if (match is not null)
            {
                expectation.Matched = true;
            }
        }
    }

    public void Record(string method, Guid id, DateTime? start = null, DateTime? end = null)
    {
        lock (sync)
        {
            var notification = new ReceivedNotification(method, id, start, end);
            received.Add(notification);

            var match = expected.FirstOrDefault(e => !e.Matched && IsSame(e, notification));
            if (match is not null)
            {
                match.Matched = true;
            }
        }
    }

    public IReadOnlyList<ExpectedNotification> SnapshotUnmatched()
    {
        lock (sync)
        {
            return expected.Where(e => !e.Matched).ToList();
        }
    }

    public IReadOnlyList<ReceivedNotification> SnapshotReceived()
    {
        lock (sync)
        {
            return received.ToList();
        }
    }

    private static bool IsSame(ExpectedNotification expectedNotification, ReceivedNotification receivedNotification)
    {
        return expectedNotification.Method == receivedNotification.Method
            && expectedNotification.Id == receivedNotification.Id
            && expectedNotification.Start == receivedNotification.Start
            && expectedNotification.End == receivedNotification.End;
    }
}