using MultiProject.Producer;

namespace MultiProject.Tooling;

/// <summary>
/// Tooling deliberately performs operations governed elsewhere. With no contract AdditionalFile,
/// analyzer presence alone must not opt this compilation into those policies.
/// </summary>
public static class ToolProgram
{
    public static string Run()
    {
        System.Console.WriteLine("tooling");
        return new ProducerService().GetValue();
    }
}
