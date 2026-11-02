extern alias Consumer;
using System.Net;
using FiapCloudGames.Payments.Domain;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Order = Consumer::FiapCloudGames.Contracts.V1.OrderPlacedEvent;

namespace FiapCloudGames.Payments.Tests;

[Trait("Category", "Integration")]
public sealed class IntegrationTests(PaymentsFixture f) : IClassFixture<PaymentsFixture>
{
    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    public async Task ConfiguredResultReachesTwoIndependentQueues(string mode)
    {
        await f.RestartApi(mode);
        try
        {
            var order = PaymentsFixture.NewOrder(); await f.Publish(order); await f.WaitResult(order.OrderId);
            var result = Assert.Single(f.CatalogResults, x => x.OrderId == order.OrderId);
            Assert.Equal(result, Assert.Single(f.NotificationResults, x => x.OrderId == order.OrderId));
            Assert.Equal(mode, result.Status); Assert.Equal(order.Price, result.Price); Assert.Equal(order.UserId, result.UserId); Assert.Equal(order.GameId, result.GameId);
            await using var db = f.Db(); var payment = Assert.Single(await db.Payments.Where(x => x.OrderId == order.OrderId).ToListAsync());
            Assert.Equal(payment.Id, result.PaymentId); Assert.Equal(payment.ResultEventId, result.EventId); Assert.Equal(payment.ProcessedAt, result.OccurredAt);
            Assert.Equal(1, await db.OrderReceipts.CountAsync(x => x.EventId == order.EventId));
        }
        finally { await f.RestartApi("Approved"); }
    }
    [Fact]
    public async Task ConcurrentDuplicatesWithDifferentTransportIdsProduceOneResult()
    {
        var order = PaymentsFixture.NewOrder();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => f.Publish(order)));
        await f.WaitResult(order.OrderId);
        var receipts = Enumerable.Range(0, 5).Select(_ => order with { EventId = Guid.NewGuid() }).ToArray();
        await Task.WhenAll(receipts.Select(x => f.Publish(x))); foreach (var item in receipts) await f.WaitReceipt(item.EventId);
        await using var db = f.Db(); Assert.Equal(1, await db.Payments.CountAsync(x => x.OrderId == order.OrderId));
        Assert.Equal(6, await db.OrderReceipts.CountAsync(x => x.PaymentId == db.Payments.Where(p => p.OrderId == order.OrderId).Select(p => p.Id).Single()));
        Assert.Single(f.CatalogResults, x => x.OrderId == order.OrderId); Assert.Single(f.NotificationResults, x => x.OrderId == order.OrderId);
    }
    [Fact]
    public async Task TransportReplayAndModeChangeDoNotReplacePersistedResult()
    {
        var order = PaymentsFixture.NewOrder(); var messageId = Guid.NewGuid(); await f.Publish(order, messageId); await f.WaitResult(order.OrderId);
        await f.RestartApi("Rejected");
        try
        {
            await f.Publish(order, messageId);
            var replay = order with { EventId = Guid.NewGuid() }; await f.Publish(replay); await f.WaitReceipt(replay.EventId);
            await using var db = f.Db(); Assert.Equal(PaymentStatus.Approved, (await db.Payments.SingleAsync(x => x.OrderId == order.OrderId)).Status);
            Assert.Single(f.CatalogResults, x => x.OrderId == order.OrderId);
        }
        finally { await f.RestartApi("Approved"); }
    }
    [Theory]
    [InlineData("user")]
    [InlineData("game")]
    [InlineData("price")]
    public async Task ReusedOrderWithConflictingSnapshotGoesToError(string field)
    {
        var order = PaymentsFixture.NewOrder(); await f.Publish(order); await f.WaitResult(order.OrderId);
        var conflict = order with { EventId = Guid.NewGuid() }; conflict = field switch
        {
            "user" => conflict with { UserId = Guid.NewGuid() },
            "game" => conflict with { GameId = Guid.NewGuid() },
            _ => conflict with { Price = 0 }
        };
        await f.Publish(conflict); await f.WaitError(conflict.EventId);
        await using var db = f.Db(); Assert.False(await db.OrderReceipts.AnyAsync(x => x.EventId == conflict.EventId));
        Assert.Equal(order.Price, (await db.Payments.SingleAsync(x => x.OrderId == order.OrderId)).Price); Assert.Single(f.CatalogResults, x => x.OrderId == order.OrderId);
    }
    [Fact]
    public async Task EventIdReuseWithChangedPayloadIsRejected()
    {
        var order = PaymentsFixture.NewOrder(); await f.Publish(order); await f.WaitResult(order.OrderId);
        await f.Publish(order with { OrderId = Guid.NewGuid() }); await f.WaitError(order.EventId);
        await using var db = f.Db(); Assert.Equal(1, await db.OrderReceipts.CountAsync(x => x.EventId == order.EventId));
    }
    [Fact]
    public async Task EquivalentDecimalRepresentationIsNotAConflict()
    {
        var order = PaymentsFixture.NewOrder(59.90m); await f.Publish(order); await f.WaitResult(order.OrderId);
        await f.Publish(order with { Price = 59.9m });
        var barrier = order with { EventId = Guid.NewGuid() }; await f.Publish(barrier); await f.WaitReceipt(barrier.EventId);
        Assert.DoesNotContain(f.Errors, x => x.EventId == order.EventId); Assert.Single(f.CatalogResults, x => x.OrderId == order.OrderId);
    }
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
    public async Task InvalidMessageDoesNotCreatePaymentOrResult(string field)
    {
        var order = PaymentsFixture.NewOrder(); order = field switch
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
        await f.Publish(order); await f.WaitError(order.EventId);
        await using var db = f.Db(); Assert.False(await db.Payments.AnyAsync(x => x.OrderId == order.OrderId)); Assert.False(await db.OrderReceipts.AnyAsync(x => x.EventId == order.EventId));
        Assert.DoesNotContain(f.CatalogResults, x => x.OrderId == order.OrderId);
    }
    [Fact]
    public async Task FailedOutboxSaveRollsBackPaymentAndReceiptThenRetries()
    {
        var order = PaymentsFixture.NewOrder(); f.Failures.Remaining[order.OrderId] = 1;
        await f.Publish(order); await f.WaitResult(order.OrderId);
        Assert.Equal(2, f.Failures.Attempts[order.OrderId]); await using var db = f.Db(); Assert.Equal(1, await db.Payments.CountAsync(x => x.OrderId == order.OrderId));
    }
    [Fact]
    public async Task ExhaustedRetriesLeaveNoBusinessStateAndReplayRecovers()
    {
        var order = PaymentsFixture.NewOrder(); f.Failures.Remaining[order.OrderId] = 4;
        await f.Publish(order); await f.WaitError(order.EventId); Assert.Equal(4, f.Failures.Attempts[order.OrderId]);
        await using (var db = f.Db())
        {
            Assert.False(await db.Payments.AnyAsync(x => x.OrderId == order.OrderId)); Assert.False(await db.OrderReceipts.AnyAsync(x => x.EventId == order.EventId));
            Assert.False(await db.Set<OutboxMessage>().AnyAsync(x => x.Body.Contains(order.OrderId.ToString())));
        }
        Assert.DoesNotContain(f.CatalogResults, x => x.OrderId == order.OrderId);
        f.Failures.Remaining.TryRemove(order.OrderId, out _); f.Faults.TryRemove(order.OrderId, out _); await f.Publish(order); await f.WaitResult(order.OrderId);
    }
    [Fact]
    public async Task FailureAfterCommitBeforeAckDoesNotReprocessPayment()
    {
        var order = PaymentsFixture.NewOrder(); f.Probe.Arm(order.OrderId, fail: true);
        await f.Publish(order); await f.Probe.Committed.Task.WaitAsync(TimeSpan.FromSeconds(45)); await f.WaitResult(order.OrderId);
        await using var db = f.Db(); Assert.Equal(1, await db.Payments.CountAsync(x => x.OrderId == order.OrderId)); Assert.Equal(1, await db.OrderReceipts.CountAsync(x => x.EventId == order.EventId));
        Assert.Single(f.CatalogResults, x => x.OrderId == order.OrderId);
    }
    [Fact]
    public async Task DurableResultSurvivesBrokerOutageAndApiRestart()
    {
        var order = PaymentsFixture.NewOrder(); f.Probe.Arm(order.OrderId);
        await f.Publish(order); await f.Probe.Committed.Task.WaitAsync(TimeSpan.FromSeconds(45));
        try
        {
            await f.Docker("stop", "rabbitmq");
            await using (var db = f.Db())
            {
                Assert.True(await db.Payments.AnyAsync(x => x.OrderId == order.OrderId)); Assert.True(await db.OrderReceipts.AnyAsync(x => x.EventId == order.EventId));
                Assert.True(await db.Set<OutboxMessage>().AnyAsync(x => x.Body.Contains(order.OrderId.ToString())));
            }
            Assert.DoesNotContain(f.CatalogResults, x => x.OrderId == order.OrderId);
            f.Probe.Release.TrySetResult(true); await f.RestartApi(waitForBroker: false);
            Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/health/ready")).StatusCode);
        }
        finally { f.Probe.Release.TrySetResult(true); await f.Docker("start", "rabbitmq"); }
        await f.WaitResult(order.OrderId);
        await PaymentsFixture.Wait(async () => { await using var db = f.Db(); return !await db.Set<OutboxMessage>().AnyAsync(x => x.Body.Contains(order.OrderId.ToString())); });
        await using var verify = f.Db(); Assert.Equal(1, await verify.Payments.CountAsync(x => x.OrderId == order.OrderId));
    }
    [Fact]
    public async Task BothDestinationQueuesRetainBeforeFirstConsumer()
    {
        var warmup = PaymentsFixture.NewOrder(); await f.Publish(warmup); await f.WaitResult(warmup.OrderId);
        await f.RecreateTargetQueues(); var order = PaymentsFixture.NewOrder(); await f.Publish(order); await f.WaitResult(order.OrderId);
        await f.VerifyLateConsumers(order.OrderId);
    }
    [Fact]
    public async Task DatabaseUniquenessProtectsOrderId()
    {
        var order = PaymentsFixture.NewOrder(); await f.Publish(order); await f.WaitResult(order.OrderId);
        await using var db = f.Db(); db.Payments.Add(Payment.Create(new(order.EventId, order.OccurredAt, order.OrderId, order.UserId, order.GameId, order.Price, order.Currency), PaymentStatus.Rejected, DateTimeOffset.UtcNow));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); Assert.Equal("UX_Payments_OrderId", Assert.IsType<PostgresException>(error.InnerException).ConstraintName);
    }
    [Fact]
    public async Task HttpExposesOnlyHealthAndStartupDoesNotMigrate()
    {
        Assert.Equal(HttpStatusCode.OK, (await f.Client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.PostAsync("/api/payments", null)).StatusCode);
        var schema = "g4_" + Guid.NewGuid().ToString("N"); await using var connection = new NpgsqlConnection(f.Config["ConnectionStrings:Payments"]); await connection.OpenAsync();
        var quoted = new NpgsqlCommandBuilder().QuoteIdentifier(schema);
        await using (var create = new NpgsqlCommand("CREATE SCHEMA " + quoted, connection)) await create.ExecuteNonQueryAsync();
        try
        {
            var config = new Dictionary<string, string?>(f.Config) { ["ConnectionStrings:Payments"] = new NpgsqlConnectionStringBuilder(f.Config["ConnectionStrings:Payments"]) { SearchPath = schema }.ConnectionString };
            await using var isolated = new PaymentsFactory(config, new FailurePlan(), new CommitProbe()); using var client = isolated.CreateClient();
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await isolated.Services.GetRequiredService<IBusControl>().StartAsync(startupTimeout.Token);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode); Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
            await using var query = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema", connection); query.Parameters.AddWithValue("schema", schema); Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
        finally { await using var drop = new NpgsqlCommand("DROP SCHEMA " + quoted, connection); await drop.ExecuteNonQueryAsync(); }
    }
}
