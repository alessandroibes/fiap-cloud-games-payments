using System.Data;
using FiapCloudGames.Contracts.V1;
using FiapCloudGames.Payments.Application;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FiapCloudGames.Payments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<PaymentsDbContext>(o => o.UseNpgsql(config.GetConnectionString("Payments") ?? throw new InvalidOperationException("ConnectionStrings:Payments obrigatório.")));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new PaymentPolicy(config["PaymentSimulation:Outcome"] ?? "Approved"));
        services.AddScoped<IPaymentStore, PaymentStore>(); services.AddScoped<PaymentService>();
        services.AddMassTransit(bus =>
        {
            bus.AddConsumer<OrderConsumer>();
            bus.AddEntityFrameworkOutbox<PaymentsDbContext>(o => { o.UsePostgres(); o.IsolationLevel = IsolationLevel.ReadCommitted; });
            bus.UsingRabbitMq((context, cfg) =>
            {
                cfg.DeployPublishTopology = true;
                cfg.Publish<PaymentProcessedEvent>(p =>
                {
                    p.BindQueue("FiapCloudGames.Contracts.V1:PaymentProcessedEvent", "catalog-payment-processed-v1", b => { b.Durable = true; b.AutoDelete = false; });
                    p.BindQueue("FiapCloudGames.Contracts.V1:PaymentProcessedEvent", "notifications-payment-processed-v1", b => { b.Durable = true; b.AutoDelete = false; });
                });
                cfg.Host(config["RabbitMq:Host"] ?? "localhost", config.GetValue<ushort>("RabbitMq:Port", 5672), config["RabbitMq:VirtualHost"] ?? "fcg", h =>
                {
                    h.Username(config["RabbitMq:Username"] ?? throw new InvalidOperationException("RabbitMq:Username obrigatório."));
                    h.Password(config["RabbitMq:Password"] ?? throw new InvalidOperationException("RabbitMq:Password obrigatório."));
                });
                cfg.ReceiveEndpoint("payments-order-placed-v1", e =>
                {
                    e.UseMessageRetry(r => { r.Ignore<InvalidOrderException>(); r.Intervals(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)); });
                    e.UseEntityFrameworkOutbox<PaymentsDbContext>(context);
                    e.ConfigureConsumer<OrderConsumer>(context);
                });
            });
        });
        services.Configure<MassTransitHostOptions>(o => { o.WaitUntilStarted = false; o.StopTimeout = TimeSpan.FromSeconds(10); });
        return services;
    }
}
