// DevicePollMachine.cs - the device-code (QR) sign-in's polling rules, with no
// timers or I/O in it, so the rules are tested and the dialog only drives it.
//
// Contract §1.3 / §2.3: poll at `interval`; SLOW_DOWN adds 2 s; ACCESS_DENIED
// and EXPIRED_TOKEN end it; the local deadline (expires_in) ends it too, even
// when the server never says so. A network blip while the user is busy on
// their phone must not throw the code away, so transport errors back off and
// only give up after several in a row.

namespace BrainX.Core.Services.License;

public enum DevicePollState { Waiting, Approved, Denied, Expired, Failed }

public sealed class DevicePollMachine
{
    /// <summary>Consecutive transport failures before the dialog stops and
    /// offers the connection check.</summary>
    public const int MaxTransportFailures = 8;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SlowDownStep = TimeSpan.FromSeconds(2);

    public DevicePollMachine(int intervalSeconds, int expiresInSeconds, DateTimeOffset startedUtc)
    {
        Interval = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds));
        Deadline = startedUtc + TimeSpan.FromSeconds(Math.Max(1, expiresInSeconds));
    }

    public TimeSpan Interval { get; private set; }
    public DateTimeOffset Deadline { get; }
    public DevicePollState State { get; private set; } = DevicePollState.Waiting;
    public LinkResult? Result { get; private set; }
    public string? ErrorCode { get; private set; }
    public int TransportFailures { get; private set; }
    public bool IsDone => State != DevicePollState.Waiting;

    /// <summary>Time left on the code, never negative.</summary>
    public TimeSpan Remaining(DateTimeOffset now) => Deadline > now ? Deadline - now : TimeSpan.Zero;

    /// <summary>Check the local deadline. True when it just expired.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (State != DevicePollState.Waiting || now < Deadline) return false;
        State = DevicePollState.Expired;
        ErrorCode = "EXPIRED_TOKEN";
        return true;
    }

    /// <summary>
    /// Feed one poll answer. Returns how long to wait before the next poll,
    /// or null when polling is over (see <see cref="State"/>).
    /// </summary>
    public TimeSpan? OnReply(DevicePollReply reply, DateTimeOffset now)
    {
        if (State != DevicePollState.Waiting) return null;

        switch (reply.Kind)
        {
            case DevicePollKind.Approved:
                State = DevicePollState.Approved;
                Result = reply.Link;
                return null;
            case DevicePollKind.Denied:
                State = DevicePollState.Denied;
                ErrorCode = "ACCESS_DENIED";
                return null;
            case DevicePollKind.Expired:
                State = DevicePollState.Expired;
                ErrorCode = "EXPIRED_TOKEN";
                return null;
            case DevicePollKind.Error:
                State = DevicePollState.Failed;
                ErrorCode = reply.ErrorCode ?? "UNKNOWN";
                return null;
            case DevicePollKind.SlowDown:
            case DevicePollKind.RateLimited:
                TransportFailures = 0;
                Interval += SlowDownStep;
                break;
            case DevicePollKind.Transport:
                TransportFailures++;
                if (TransportFailures >= MaxTransportFailures)
                {
                    State = DevicePollState.Failed;
                    ErrorCode = reply.ErrorCode ?? "OFFLINE";
                    return null;
                }
                break;
            default:   // Pending
                TransportFailures = 0;
                break;
        }

        if (Tick(now)) return null;
        var wait = Interval;
        if (TransportFailures > 0)
        {
            // 1×, 2×, 4× the interval, capped: a blip retries soon, an outage
            // does not hammer the server.
            var backoff = TimeSpan.FromTicks(Interval.Ticks * (1L << Math.Min(TransportFailures - 1, 4)));
            wait = backoff > MaxBackoff ? (Interval > MaxBackoff ? Interval : MaxBackoff) : backoff;
        }
        var left = Remaining(now);
        return wait < left ? wait : left;
    }
}
