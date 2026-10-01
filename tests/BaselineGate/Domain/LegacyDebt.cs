using BaselineGate.Application;

namespace BaselineGate.Domain;

/// <summary>Existing architecture debt recorded in architecture.baseline.json.</summary>
public sealed class LegacyDebt
{
    public AppService Service { get; } = new();
}
