using FiapCloudGames.Payments.Domain;

namespace FiapCloudGames.Payments.Application;

public sealed class InvalidOrderException(string message) : Exception(message);
public interface IPaymentStore
{
    // Retorna pagamento somente quando um novo resultado deve ser publicado.
    Task<Payment?> Process(OrderData order, PaymentStatus status, CancellationToken ct);
}

public sealed class PaymentPolicy
{
    public PaymentStatus Outcome { get; }
    public PaymentPolicy(string outcome = "Approved") => Outcome = outcome switch
    {
        "Approved" => PaymentStatus.Approved,
        "Rejected" => PaymentStatus.Rejected,
        _ => throw new InvalidOperationException("PaymentSimulation:Outcome deve ser Approved ou Rejected.")
    };
}

public sealed class PaymentService(IPaymentStore store, PaymentPolicy policy)
{
    public Task<Payment?> Process(OrderData order, CancellationToken ct)
    {
        try { order.Validate(); }
        catch (ArgumentException ex) { throw new InvalidOrderException(ex.Message); }
        return store.Process(order, policy.Outcome, ct);
    }
}
