namespace SampleConsumer.Domain;

/// <summary>
/// A clean Domain type: it depends on nothing outside its own layer and uses no forbidden API.
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
