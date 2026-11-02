namespace FiapCloudGames.Payments.Domain;

public enum PaymentStatus { Approved, Rejected }

public sealed record OrderData(Guid EventId, DateTimeOffset OccurredAt, Guid OrderId, Guid UserId, Guid GameId, decimal Price, string Currency)
{
    public void Validate()
    {
        if (EventId == Guid.Empty || OrderId == Guid.Empty || UserId == Guid.Empty || GameId == Guid.Empty)
            throw new ArgumentException("Identificadores do pedido inválidos.");
        if (OccurredAt == default || OccurredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Data do evento deve estar em UTC.");
        if (Currency != "BRL" || Price < 0 || Price > 9999999999999999.99m || decimal.Round(Price, 2) != Price)
            throw new ArgumentException("Moeda deve ser BRL e preço não negativo com precisão 18,2.");
    }
}

public sealed class Payment
{
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid GameId { get; private set; }
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "BRL";
    public PaymentStatus Status { get; private set; }
    public Guid ResultEventId { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }
    private Payment() { }
    public static Payment Create(OrderData order, PaymentStatus status, DateTimeOffset now)
    {
        order.Validate();
        if (!Enum.IsDefined(status)) throw new ArgumentException("Resultado inválido.");
        // PostgreSQL persiste microssegundos; o evento usa exatamente o instante persistível.
        var utc = now.ToUniversalTime();
        var persistedTime = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
        return new Payment
        {
            Id = Guid.NewGuid(),
            OrderId = order.OrderId,
            UserId = order.UserId,
            GameId = order.GameId,
            Price = order.Price,
            Currency = order.Currency,
            Status = status,
            ResultEventId = Guid.NewGuid(),
            ProcessedAt = persistedTime
        };
    }
    public bool Matches(OrderData order) => OrderId == order.OrderId && UserId == order.UserId && GameId == order.GameId && Price == order.Price && Currency == order.Currency;
}
