using Microsoft.CodeAnalysis;

namespace HDF5.NET.SourceGenerator;

[Generator]
public class SourceGenerator : ISourceGenerator
{
    public void Execute(GeneratorExecutionContext context)
    {
        // Code generation goes here
    }

    public void Initialize(GeneratorInitializationContext context)
    {
        // No initialization required for this one
    }
}