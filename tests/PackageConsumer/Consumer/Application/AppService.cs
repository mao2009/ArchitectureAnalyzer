using PackageConsumer.Domain;

namespace PackageConsumer.Application;

/// <summary>
/// A clean Application type. Application -> Domain is allowed by the contract.
/// </summary>
public sealed class AppService
{
    /// <summary>Describes an entity.</summary>
    /// <param name="entity">The entity to describe.</param>
    /// <returns>The entity name.</returns>
    public string Describe(DomainEntity entity)
    {
        return entity is null ? string.Empty : entity.Name;
    }
}
