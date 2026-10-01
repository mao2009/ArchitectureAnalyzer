namespace PackageConsumer.Domain;

/// <summary>
/// A clean Domain type that stays inside its declared architecture layer.
/// </summary>
public sealed class DomainEntity
{
    /// <summary>Creates an entity.</summary>
    /// <param name="name">The entity name.</param>
    public DomainEntity(string name)
    {
        Name = name;
    }

    /// <summary>The entity name.</summary>
    public string Name { get; }
}
