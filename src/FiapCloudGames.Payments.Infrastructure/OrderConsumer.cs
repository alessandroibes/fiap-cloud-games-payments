using FiapCloudGames.Contracts.V1;
using FiapCloudGames.Payments.Application;
using FiapCloudGames.Payments.Domain;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FiapCloudGames.Payments.Infrastructure;

public sealed class OrderConsumer(PaymentService service, ILogger<OrderConsumer> logger) : IConsumer<OrderPlacedEvent>
{
    public async Task Consume(ConsumeContext<OrderPlacedEvent> context)
    {
        var order = context.Message;
        var payment = await service.Process(new OrderData(order.EventId, order.OccurredAt, order.OrderId, order.UserId, order.GameId, order.Price, order.Currency), context.CancellationToken);
        if (payment is null)
        {
            logger.LogInformation("Pedido duplicado reconhecido. EventId {EventId}, OrderId {OrderId}", order.EventId, order.OrderId);
            return;
        }
        await context.Publish(new PaymentProcessedEvent
        {
            EventId = payment.ResultEventId,
            OccurredAt = payment.ProcessedAt,
            OrderId = payment.OrderId,
            PaymentId = payment.Id,
            UserId = payment.UserId,
            GameId = payment.GameId,
            Status = payment.Status.ToString(),
            Price = payment.Price,
            Currency = payment.Currency
        });
        logger.LogInformation("Resultado simulado preparado na transação. OrderId {OrderId}, PaymentId {PaymentId}, Status {Status}", payment.OrderId, payment.Id, payment.Status);
    }
}
