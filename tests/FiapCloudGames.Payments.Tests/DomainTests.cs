using FiapCloudGames.Payments.Application;
using FiapCloudGames.Payments.Domain;
using Xunit;

namespace FiapCloudGames.Payments.Tests;

public sealed class DomainTests
{
    private static OrderData Valid() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 39.90m, "BRL");
    [Theory]
    [InlineData("Approved", PaymentStatus.Approved)]
    [InlineData("Rejected", PaymentStatus.Rejected)]
    public void ResultIsDeterministicAndPreservesSnapshot(string mode, PaymentStatus status)
    {
        var order = Valid(); var policy = new PaymentPolicy(mode);
        var payment = Payment.Create(order, policy.Outcome, DateTimeOffset.UtcNow);
        Assert.Equal(status, payment.Status); Assert.Equal(order.Price, payment.Price); Assert.Equal(order.OrderId, payment.OrderId);
        Assert.NotEqual(Guid.Empty, payment.Id); Assert.NotEqual(Guid.Empty, payment.ResultEventId); Assert.True(payment.Matches(order));
        Assert.Equal(0, payment.ProcessedAt.Ticks % 10);
        Assert.False(payment.Matches(order with { Price = 0m }));
    }
    [Fact]
    public void DefaultIsApprovedAndFreeGameIsAllowed()
    {
        var payment = Payment.Create(Valid() with { Price = 0 }, new PaymentPolicy().Outcome, DateTimeOffset.UtcNow);
        Assert.Equal(PaymentStatus.Approved, payment.Status); Assert.Equal(0m, payment.Price);
    }
    [Theory]
    [InlineData("approved")]
    [InlineData("Random")]
    [InlineData("")]
    public void InvalidModeFailsFast(string mode) => Assert.Throws<InvalidOperationException>(() => new PaymentPolicy(mode));
    [Theory]
    [InlineData("event")]
    [InlineData("order")]
    [InlineData("user")]
    [InlineData("game")]
    [InlineData("utc")]
    [InlineData("date")]
    [InlineData("currency")]
    [InlineData("negative")]
    [InlineData("scale")]
    [InlineData("overflow")]
    public void InvalidOrderCannotProducePayment(string field)
    {
        var order = Valid(); order = field switch
        {
            "event" => order with { EventId = Guid.Empty },
            "order" => order with { OrderId = Guid.Empty },
            "user" => order with { UserId = Guid.Empty },
            "game" => order with { GameId = Guid.Empty },
            "utc" => order with { OccurredAt = order.OccurredAt.ToOffset(TimeSpan.FromHours(-3)) },
            "date" => order with { OccurredAt = default },
            "currency" => order with { Currency = "USD" },
            "negative" => order with { Price = -1 },
            "scale" => order with { Price = 1.001m },
            _ => order with { Price = 10000000000000000m }
        };
        Assert.Throws<ArgumentException>(() => Payment.Create(order, PaymentStatus.Approved, DateTimeOffset.UtcNow));
    }
}
