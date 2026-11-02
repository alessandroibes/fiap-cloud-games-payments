extern alias Consumer;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using FiapCloudGames.Payments.Domain;
using FiapCloudGames.Payments.Infrastructure;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Order = Consumer::FiapCloudGames.Contracts.V1.OrderPlacedEvent;
using Result = Consumer::FiapCloudGames.Contracts.V1.PaymentProcessedEvent;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace FiapCloudGames.Payments.Tests;

public sealed class FailurePlan : SaveChangesInterceptor
{
    public ConcurrentDictionary<Guid, int> Remaining { get; } = new();
    public ConcurrentDictionary<Guid, int> Attempts { get; } = new();
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var db = data.Context!;
        // Falha após Payment/Receipt terem sido salvos na transação, antes de persistir a Outbox.
        if (db.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added))
            foreach (var entry in db.ChangeTracker.Entries<Payment>())
            {
                var id = entry.Entity.OrderId;
                if (!Remaining.ContainsKey(id)) continue;
                Attempts.AddOrUpdate(id, 1, (_, count) => count + 1);
                if (Remaining.AddOrUpdate(id, 0, (_, count) => count - 1) >= 0)
                    throw new IOException("Falha controlada ao salvar Outbox antes do commit.");
            }
        return base.SavingChangesAsync(data, result, ct);
    }
}

public sealed class CommitProbe : DbTransactionInterceptor
{
    public Guid PauseOrder { get; set; }
    public Guid FailOrder { get; set; }
    public TaskCompletionSource<bool> Committed { get; private set; } = NewSignal();
    public TaskCompletionSource<bool> Release { get; private set; } = NewSignal();
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Arm(Guid order, bool fail = false) { PauseOrder = fail ? Guid.Empty : order; FailOrder = fail ? order : Guid.Empty; Committed = NewSignal(); Release = NewSignal(); }
    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
    {
        var db = data.Context;
        if (db is null || !db.ChangeTracker.Entries<OutboxMessage>().Any()) return;
        var ids = db.ChangeTracker.Entries<Payment>().Select(e => e.Entity.OrderId).ToArray();
        if (FailOrder != Guid.Empty && ids.Contains(FailOrder))
        {
            FailOrder = Guid.Empty; Committed.TrySetResult(true);
            throw new IOException("Falha controlada após commit e antes de entrega/ACK.");
        }
        if (PauseOrder != Guid.Empty && ids.Contains(PauseOrder))
        {
            PauseOrder = Guid.Empty; Committed.TrySetResult(true);
            await Release.Task.WaitAsync(ct);
        }
    }
}

public sealed class PaymentsFactory(Dictionary<string, string?> config, FailurePlan failures, CommitProbe probe) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing"); foreach (var pair in config) builder.UseSetting(pair.Key, pair.Value);
        builder.ConfigureServices(services => services.AddDbContext<PaymentsDbContext>(o => o.AddInterceptors(failures, probe)));
    }
}

public sealed class PaymentsFixture : IAsyncLifetime
{
    public Dictionary<string, string> Env { get; }
    public Dictionary<string, string?> Config { get; }
    public string Root { get; }
    public PaymentsFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public FailurePlan Failures { get; } = new();
    public CommitProbe Probe { get; } = new();
    private IBusControl bus = null!;
    public ConcurrentQueue<Result> CatalogResults { get; } = new();
    public ConcurrentQueue<Result> NotificationResults { get; } = new();
    public ConcurrentQueue<Order> Errors { get; } = new();
    public ConcurrentDictionary<Guid, string> Faults { get; } = new();
    public PaymentsFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FiapCloudGames.Payments.sln"))) directory = directory.Parent;
        Root = directory?.FullName ?? throw new InvalidOperationException("Raiz Payments não encontrada.");
        Env = File.ReadAllLines(Path.Combine(Root, ".env")).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        var connection = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = int.Parse(Env["TEST_DB_PORT"]), Database = "fcg_payments_test", Username = "fcg_payments_test", Password = Env["TEST_DB_PASSWORD"] }.ConnectionString;
        Config = new()
        {
            ["ConnectionStrings:Payments"] = connection,
            ["PaymentSimulation:Outcome"] = "Approved",
            ["RabbitMq:Host"] = "127.0.0.1",
            ["RabbitMq:Port"] = Env["TEST_RABBIT_PORT"],
            ["RabbitMq:VirtualHost"] = "fcg_test",
            ["RabbitMq:Username"] = "payments_test",
            ["RabbitMq:Password"] = Env["TEST_RABBIT_PASSWORD"]
        };
    }
    public PaymentsDbContext Db() => new(new DbContextOptionsBuilder<PaymentsDbContext>().UseNpgsql(Config["ConnectionStrings:Payments"]).Options);
    private void Host(IRabbitMqBusFactoryConfigurator cfg) => cfg.Host("127.0.0.1", ushort.Parse(Env["TEST_RABBIT_PORT"]), "fcg_test", h => { h.Username("payments_test"); h.Password(Env["TEST_RABBIT_PASSWORD"]); });
    public async Task InitializeAsync()
    {
        await using (var db = Db()) await db.Database.MigrateAsync();
        bus = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            Host(cfg);
            cfg.ReceiveEndpoint("payments-g4-catalog-observer", e => e.Handler<Result>(ctx => { CatalogResults.Enqueue(ctx.Message); return Task.CompletedTask; }));
            cfg.ReceiveEndpoint("payments-g4-notifications-observer", e => e.Handler<Result>(ctx => { NotificationResults.Enqueue(ctx.Message); return Task.CompletedTask; }));
            cfg.ReceiveEndpoint("payments-order-placed-v1_error", e =>
            {
                e.ConfigureConsumeTopology = false;
                e.Handler<Order>(ctx =>
                {
                    Errors.Enqueue(ctx.Message);
                    Faults[ctx.Message.OrderId] = ctx.ReceiveContext.TransportHeaders.Get<string>("MT-Fault-Message") ?? "Erro de consumer sem detalhe.";
                    return Task.CompletedTask;
                });
            });
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await bus.StartAsync(timeout.Token);
        StartApi(); await Factory.Services.GetRequiredService<IBusControl>().StartAsync(timeout.Token);
    }
    private void StartApi() { Factory = new PaymentsFactory(Config, Failures, Probe); Client = Factory.CreateClient(); }
    public async Task RestartApi(string? outcome = null, bool waitForBroker = true)
    {
        Client.Dispose(); await Factory.DisposeAsync();
        if (outcome is not null) Config["PaymentSimulation:Outcome"] = outcome;
        StartApi();
        if (waitForBroker)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await Factory.Services.GetRequiredService<IBusControl>().StartAsync(timeout.Token);
        }
    }
    public Task Publish(Order order, Guid? messageId = null) => bus.Publish(order, ctx => ctx.MessageId = messageId ?? Guid.NewGuid());
    public static Order NewOrder(decimal price = 59.90m) => new() { EventId = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow, OrderId = Guid.NewGuid(), UserId = Guid.NewGuid(), GameId = Guid.NewGuid(), Price = price, Currency = "BRL" };
    public static async Task Wait(Func<Task<bool>> condition, int seconds = 60)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); while (!await condition()) await Task.Delay(100, timeout.Token);
    }
    public Task WaitResult(Guid orderId) => Wait(() =>
    {
        if (Faults.TryGetValue(orderId, out var detail)) throw new Xunit.Sdk.XunitException("Resultado esperado foi para _error: " + detail);
        return Task.FromResult(CatalogResults.Any(x => x.OrderId == orderId) && NotificationResults.Any(x => x.OrderId == orderId));
    });
    public Task WaitReceipt(Guid id) => Wait(async () => { await using var db = Db(); return await db.OrderReceipts.AnyAsync(x => x.EventId == id); });
    public Task WaitError(Guid id) => Wait(() => Task.FromResult(Errors.Any(x => x.EventId == id)));
    public async Task RecreateTargetQueues()
    {
        Client.Dispose(); await Factory.DisposeAsync();
        foreach (var queue in new[] { "catalog-payment-processed-v1", "notifications-payment-processed-v1" })
            await Docker("exec", "-T", "rabbitmq", "gosu", "rabbitmq", "rabbitmqctl", "delete_queue", "-p", "fcg_test", queue);
        StartApi();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await Factory.Services.GetRequiredService<IBusControl>().StartAsync(timeout.Token);
    }
    public async Task VerifyLateConsumers(Guid orderId)
    {
        var catalog = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = Bus.Factory.CreateUsingRabbitMq(cfg =>
        {
            Host(cfg);
            cfg.ReceiveEndpoint("catalog-payment-processed-v1", e => e.Handler<Result>(ctx => { if (ctx.Message.OrderId == orderId) catalog.TrySetResult(ctx.Message); return Task.CompletedTask; }));
            cfg.ReceiveEndpoint("notifications-payment-processed-v1", e => e.Handler<Result>(ctx => { if (ctx.Message.OrderId == orderId) notifications.TrySetResult(ctx.Message); return Task.CompletedTask; }));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await late.StartAsync(timeout.Token); Assert.Equal(await catalog.Task.WaitAsync(timeout.Token), await notifications.Task.WaitAsync(timeout.Token)); }
        finally { await late.StopAsync(); }
    }
    public async Task Docker(params string[] command)
    {
        var start = new ProcessStartInfo("docker") { WorkingDirectory = Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "compose", "--env-file", ".env", "-f", "tests/compose.yaml" }.Concat(command)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150)); await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(stdout, stderr);
        Assert.True(process.ExitCode == 0, "Docker do ambiente Payments de testes falhou; saída omitida para preservar credenciais.");
    }
    public async Task DisposeAsync()
    {
        Probe.Release.TrySetResult(true); Client?.Dispose(); if (Factory is not null) await Factory.DisposeAsync();
        if (bus is not null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await bus.StopAsync(timeout.Token); }
    }
}
