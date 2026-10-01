using System.Collections.Concurrent;
using Zwijg.Core.Audit;

namespace Zwijg.Gateway;

// Tageslimit pro Benutzer. Gezählt wird nach Id, damit eine Umbenennung nichts zurücksetzt.
// Dazu kommen die Anfragen, die gerade laufen und noch nicht im Protokoll stehen,
// sonst kämen parallele Anfragen alle durch.
public sealed class DailyLimiter(IAuditLog audit)
{
    private readonly ConcurrentDictionary<string, Slot> _slots = new();

    private sealed class Slot
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public int Running;
    }

    // Gibt einen Platz zurück, der nach der Anfrage freigegeben wird, oder null, wenn das Limit erreicht ist.
    // Den Platz erst freigeben, wenn der Protokolleintrag geschrieben ist.
    public async Task<IDisposable?> TryReserveAsync(GatewayUser user, CancellationToken ct)
    {
        if (user.DailyLimit is not { } limit)
            return Reservation.None;

        var slot = _slots.GetOrAdd(user.Id, _ => new Slot());
        await slot.Lock.WaitAsync(ct);
        try
        {
            // Erst die laufenden lesen, dann zählen. Endet dazwischen eine Anfrage, zählt sie doppelt, aber nie gar nicht.
            var running = Volatile.Read(ref slot.Running);
            var used = await audit.CountRequestsAsync(user.Id, user.Name, ChatPipeline.StartOfToday(), ct);
            if (used + running >= limit)
                return null;

            Interlocked.Increment(ref slot.Running);
            return new Reservation(slot);
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    // Anfragen, die gerade laufen und noch nicht im Protokoll stehen
    public int Running(string userId) =>
        _slots.TryGetValue(userId, out var slot) ? Volatile.Read(ref slot.Running) : 0;

    private sealed class Reservation(Slot? slot) : IDisposable
    {
        public static readonly Reservation None = new(null);
        private int _done;

        public void Dispose()
        {
            if (slot != null && Interlocked.Exchange(ref _done, 1) == 0)
                Interlocked.Decrement(ref slot.Running);
        }
    }
}
