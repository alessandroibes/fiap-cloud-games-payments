using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FiapCloudGames.Payments.Application;
using FiapCloudGames.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace FiapCloudGames.Payments.Infrastructure;

public sealed class PaymentStore(PaymentsDbContext db, TimeProvider clock) : IPaymentStore
{
    public async Task<Payment?> Process(OrderData order, PaymentStatus status, CancellationToken ct)
    {
        // A transação pertence ao middleware Consumer Outbox, incluindo a publicação do resultado.
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Processamento exige a transação da Consumer Outbox.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"event:" + order.EventId}, 0))", ct);
        var canonical = new { order.EventId, order.OccurredAt, order.OrderId, order.UserId, order.GameId, Price = order.Price.ToString("0.00", CultureInfo.InvariantCulture), order.Currency };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
        var receipt = await db.OrderReceipts.FindAsync([order.EventId], ct);
        if (receipt is not null)
        {
            if (receipt.Fingerprint != fingerprint) throw new InvalidOrderException("EventId reutilizado com conteúdo divergente.");
            return null;
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"order:" + order.OrderId}, 0))", ct);
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.OrderId == order.OrderId, ct);
        var created = payment is null;
        if (payment is not null && !payment.Matches(order)) throw new InvalidOrderException("Pedido já processado com dados divergentes.");
        if (payment is null)
        {
            payment = Payment.Create(order, status, clock.GetUtcNow());
            db.Payments.Add(payment);
        }
        db.OrderReceipts.Add(new OrderReceipt { EventId = order.EventId, PaymentId = payment.Id, Fingerprint = fingerprint, ProcessedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        return created ? payment : null;
    }
}
