using FiapCloudGames.Payments.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args.Where(a => a != "--migrate").ToArray());
builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole();
builder.Services.AddPaymentsInfrastructure(builder.Configuration);
await using var app = builder.Build();
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().Database.MigrateAsync();
    return;
}
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" }));
app.MapGet("/health/ready", async (PaymentsDbContext db, CancellationToken ct) =>
{
    try { return Results.StatusCode(await db.Database.CanConnectAsync(ct) && !(await db.Database.GetPendingMigrationsAsync(ct)).Any() ? 200 : 503); }
    catch { return Results.StatusCode(503); }
});
app.Run();
public partial class Program;
