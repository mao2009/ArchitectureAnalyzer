using MultiProject.Consumer;
using MultiProject.Producer;

namespace MultiProject.Tests;

/// <summary>
/// Intentionally uses Console and direct project references. The analyzer is loaded here, but no
/// Architecture Contract is supplied, so this compilation must remain an intentional no-op.
/// </summary>
public sealed class TestHarness
{
    public string Run()
    {
        System.Console.WriteLine("test harness");
        return new ConsumerService().GetValue() + new ProducerService().GetValue();
    }
}
