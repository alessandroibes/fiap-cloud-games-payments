using FiapCloudGames.Payments.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace FiapCloudGames.Payments.Infrastructure;

public sealed class OrderReceipt
{
    public Guid EventId { get; set; }
    public Guid PaymentId { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset ProcessedAt { get; set; }
}

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<OrderReceipt> OrderReceipts => Set<OrderReceipt>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Payment>(e =>
        {
            e.HasKey(x => x.Id); e.HasIndex(x => x.OrderId).IsUnique().HasDatabaseName("UX_Payments_OrderId");
            e.HasIndex(x => x.ResultEventId).IsUnique(); e.Property(x => x.Price).HasPrecision(18, 2);
            e.Property(x => x.Status).HasConversion<string>(); e.Property(x => x.Currency).IsRequired();
            e.ToTable("Payments", t =>
            {
                t.HasCheckConstraint("CK_Payments_Money", "\"Price\" >= 0 AND \"Currency\" = 'BRL'");
                t.HasCheckConstraint("CK_Payments_Status", "\"Status\" IN ('Approved','Rejected')");
            });
        });
        model.Entity<OrderReceipt>(e =>
        {
            e.HasKey(x => x.EventId); e.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
            e.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });
        model.AddInboxStateEntity(); model.AddOutboxMessageEntity(); model.AddOutboxStateEntity();
    }
}
