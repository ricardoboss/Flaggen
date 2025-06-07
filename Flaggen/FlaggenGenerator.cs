using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Flaggen;

[Generator]
public class FlaggenGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var enumDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: (s, _) => IsEnumWithAttributes(s),
                transform: (ctx, _) => GetSemanticTargetForGeneration(ctx))
            .Where(symbol => symbol is not null);

        var compilationAndEnums = context.CompilationProvider.Combine(enumDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndEnums, (spc, source) =>
        {
            var (_, enums) = source;
            foreach (var enumSymbol in enums.Where(s => s is not null).Cast<INamedTypeSymbol>().Distinct(SymbolEqualityComparer.Default))
            {
                var namedTypeSymbol = enumSymbol as INamedTypeSymbol;
                if (namedTypeSymbol!.GetAttributes()
                    .Any(attr => attr.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
                {
                    var sourceCode = GenerateExtensionClass(namedTypeSymbol);
                    spc.AddSource($"{namedTypeSymbol.Name}_FlaggenExtensions.g.cs",
                        SourceText.From(sourceCode, Encoding.UTF8));
                }
            }
        });
    }

    private static bool IsEnumWithAttributes(SyntaxNode node)
    {
        return node is EnumDeclarationSyntax { AttributeLists.Count: > 0 };
    }

    private static INamedTypeSymbol? GetSemanticTargetForGeneration(GeneratorSyntaxContext context)
    {
        var enumDecl = (EnumDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(enumDecl);
        return symbol as INamedTypeSymbol;
    }

    private static string GenerateExtensionClass(INamedTypeSymbol enumSymbol)
    {
        var namespaceName = enumSymbol.ContainingNamespace.ToDisplayString();
        if (namespaceName == "<global namespace>")
            namespaceName = "Flaggen";

        var enumName = enumSymbol.Name;

        var sb = new StringBuilder($$"""
                                     using System;

                                     namespace {{namespaceName}}
                                     {
                                         public static class {{enumName}}FlaggenExtensions
                                         {
                                             public static void Add(ref this {{enumName}} value, {{enumName}} flag)
                                             {
                                                 value |= flag;
                                             }
                                     
                                             public static void Remove(ref this {{enumName}} value, {{enumName}} flag)
                                             {
                                                 value &= ~flag;
                                             }
                                     
                                             public static void Toggle(ref this {{enumName}} value, {{enumName}} flag)
                                             {
                                                 value ^= flag;
                                             }
                                     
                                             public static bool Has(ref this {{enumName}} value, {{enumName}} flag)
                                             {
                                                 return (value & flag) == flag;
                                             }
                                         }
                                     }
                                     """);

        return sb.ToString();
    }
}
