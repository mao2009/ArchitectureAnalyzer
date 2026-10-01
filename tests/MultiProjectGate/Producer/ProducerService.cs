namespace MultiProject.Producer;

/// <summary>Producer API referenced by the consumer-side violation fixture.</summary>
public sealed class ProducerService
{
    public string GetValue() => "producer";
}
