using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace FiapCloudGames.Payments.Infrastructure;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PaymentsDbContext>
{
    public PaymentsDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PaymentsDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__Payments") ?? throw new InvalidOperationException("Configure ConnectionStrings__Payments para executar EF.")).Options);
}
