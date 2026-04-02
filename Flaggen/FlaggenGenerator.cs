using System.CodeDom.Compiler;
using System.Collections.Immutable;
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
            foreach (var enumSymbol in enums.Where(s => s is not null).Cast<INamedTypeSymbol>()
                         .Distinct(SymbolEqualityComparer.Default))
            {
                var namedTypeSymbol = enumSymbol as INamedTypeSymbol;
                if (namedTypeSymbol!.GetAttributes()
                    .Any(attr => attr.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
                {
                    var stream = new MemoryStream();
                    var extensionClassName = $"{namedTypeSymbol.Name}FlaggenExtensions";

                    GenerateExtensionFile(stream, namedTypeSymbol, extensionClassName);

                    stream.Position = 0;

                    spc.AddSource($"{extensionClassName}.g.cs", SourceText.From(stream));
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

    private static readonly SymbolDisplayFormat ConstraintTypeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
        SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
        SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static ImmutableArray<ITypeParameterSymbol> GetContainingTypeParameters(INamedTypeSymbol enumSymbol)
    {
        var stack = new Stack<INamedTypeSymbol>();

        for (var type = enumSymbol.ContainingType; type is not null; type = type.ContainingType)
            stack.Push(type);

        var builder = ImmutableArray.CreateBuilder<ITypeParameterSymbol>();

        while (stack.Count > 0)
            builder.AddRange(stack.Pop().TypeParameters);

        return builder.ToImmutable();
    }

    private static string BuildConstraintClauses(ImmutableArray<ITypeParameterSymbol> typeParameters)
    {
        var sb = new StringBuilder();

        foreach (var tp in typeParameters)
        {
            var parts = new List<string>();

            // Primary constraint: must come first.
            if (tp.HasReferenceTypeConstraint)
            {
                parts.Add(tp.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                    ? "class?"
                    : "class");
            }
            else if (tp.HasUnmanagedTypeConstraint)
            {
                parts.Add("unmanaged");
            }
            else if (tp.HasValueTypeConstraint)
            {
                parts.Add("struct");
            }
            else if (tp.HasNotNullConstraint)
            {
                parts.Add("notnull");
            }

            // Base class / interface / type parameter constraints.
            for (var i = 0; i < tp.ConstraintTypes.Length; i++)
            {
                parts.Add(FormatConstraintType(
                    tp.ConstraintTypes[i],
                    tp.ConstraintNullableAnnotations[i]));
            }

            // Must be last among normal constraints.
            if (tp.HasConstructorConstraint)
                parts.Add("new()");

            // Must come after all constraints.
            if (tp.AllowsRefLikeType)
                parts.Add("allows ref struct");

            if (parts.Count != 0)
                sb.Append(" where ").Append(tp.Name).Append(" : ").Append(string.Join(", ", parts));
        }

        return sb.ToString();
    }

    private static string FormatConstraintType(ITypeSymbol type, NullableAnnotation topLevelNullability)
    {
        var text = type.ToDisplayString(ConstraintTypeFormat);

        // ConstraintNullableAnnotations carries the top-level nullability that was explicitly written
        // on the constraint type.
        if (topLevelNullability == NullableAnnotation.Annotated &&
            type.IsReferenceType &&
            !text.EndsWith("?", StringComparison.Ordinal))
        {
            text += "?";
        }

        return text;
    }

    private static void GenerateExtensionFile(Stream stream, INamedTypeSymbol enumSymbol, string extensionClassName)
    {
        var ns = enumSymbol.ContainingNamespace;
        var namespaceName = ns is null || ns.IsGlobalNamespace ? null : ns.ToDisplayString();

        var typeParameters = GetContainingTypeParameters(enumSymbol);
        var methodTypeParameterList =
            typeParameters.Length == 0
                ? ""
                : "<" + string.Join(", ", typeParameters.Select(x => x.Name).Distinct()) + ">";

        var methodConstraintClauses = BuildConstraintClauses(typeParameters);

        var enumTypeReference =
            enumSymbol.ToDisplayString(NullableFlowState.NotNull, SymbolDisplayFormat.FullyQualifiedFormat);
        var docBlockTypeRef = enumTypeReference.Replace("<", "{").Replace(">", "}");

        using var streamWriter = new StreamWriter(stream, encoding: Encoding.UTF8, bufferSize: 1024, leaveOpen: true);
        using var writer = new IndentedTextWriter(streamWriter);

        if (namespaceName is not null)
        {
            writer.Write("namespace ");
            writer.WriteLine(namespaceName);
            writer.WriteLine("{");
            writer.Indent++;
        }

        GenerateExtensionClass(extensionClassName, writer, docBlockTypeRef, methodTypeParameterList,
            methodConstraintClauses, enumTypeReference);

        if (namespaceName is not null)
        {
            writer.Indent--;
            writer.WriteLine("}");
        }

        writer.Flush();
        streamWriter.Flush();
    }

    private static void GenerateExtensionClass(string extensionClassName,
        IndentedTextWriter writer,
        string docBlockTypeRef,
        string genericParams,
        string genericConstraints,
        string enumType
    )
    {
        writer.WriteLine("/// <summary>");
        writer.Write("/// Extension methods for <see cref=\"");
        writer.Write(docBlockTypeRef);
        writer.WriteLine("\"/>.");
        writer.WriteLine("/// </summary>");
        writer.Write("public static class ");
        writer.WriteLine(extensionClassName);
        writer.WriteLine("{");
        writer.Indent++;

        GenerateAddExtension(writer, genericParams, genericConstraints, enumType);

        writer.WriteLineNoTabs(string.Empty);

        GenerateRemoveExtension(writer, genericParams, genericConstraints, enumType);

        writer.WriteLineNoTabs(string.Empty);

        GenerateToggleExtension(writer, genericParams, genericConstraints, enumType);

        writer.WriteLineNoTabs(string.Empty);

        GenerateSetExtension(writer, genericParams, genericConstraints, enumType);

        writer.WriteLineNoTabs(string.Empty);

        GenerateHasExtension(writer, genericParams, genericConstraints, enumType);

        writer.Indent--;
        writer.WriteLine("}");
    }

    private static void WriteCode(IndentedTextWriter writer, string sourceCode)
    {
        foreach (var line in sourceCode.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                     .Select(l => l.Trim()))
        {
            if (line == "}")
                writer.Indent--;

            writer.WriteLine(line);

            if (line == "{")
                writer.Indent++;
        }
    }

    private static void GenerateHasExtension(IndentedTextWriter writer, string genericParams, string genericConstraints,
        string enumType) =>
        WriteCode(
            writer,
            $$"""
              /// <summary>
              /// Checks whether <paramref name="value"/> has <paramref name="flag"/> set.
              /// </summary>
              /// <param name="value">The value to check for flag</param>
              /// <param name="flag">The flag to check to the value for</param>
              public static bool Has{{genericParams}}(ref this {{enumType}} value, {{enumType}} flag){{genericConstraints}}
              {
                  return (value & flag) == flag;
              }
              """
        );

    private static void GenerateToggleExtension(IndentedTextWriter writer, string genericParams,
        string genericConstraints, string enumType) =>
        WriteCode(
            writer,
            $$"""
              /// <summary>
              /// Toggles <paramref name="flag"/> on <paramref name="value"/>.
              /// </summary>
              /// <param name="value">The value to toggle the flag on</param>
              /// <param name="flag">The flag to toggle on the value</param>
              public static void Toggle{{genericParams}}(ref this {{enumType}} value, {{enumType}} flag){{genericConstraints}}
              {
                  value ^= flag;
              }
              """
        );

    private static void GenerateSetExtension(IndentedTextWriter writer, string genericParams,
        string genericConstraints, string enumType) =>
        WriteCode(
            writer,
            $$"""
              /// <summary>
              /// Sets <paramref name="flag"/> on <paramref name="value"/> depending on <paramref name="enable"/>.
              /// </summary>
              /// <param name="value">The value to set the flag on</param>
              /// <param name="flag">The flag to set on the value</param>
              /// <param name="enable">Whether the flag should be enabled</param>
              public static void Set{{genericParams}}(ref this {{enumType}} value, {{enumType}} flag, bool enable){{genericConstraints}}
              {
                  value = enable ? value | flag : value & ~flag;
              }
              """
        );

    private static void GenerateRemoveExtension(IndentedTextWriter writer, string genericParams,
        string genericConstraints, string enumType) =>
        WriteCode(
            writer,
            $$"""
              /// <summary>
              /// Removes <paramref name="flag"/> from <paramref name="value"/>.
              /// </summary>
              /// <param name="value">The value to remove the flag from</param>
              /// <param name="flag">The flag to remove from the value</param>
              public static void Remove{{genericParams}}(ref this {{enumType}} value, {{enumType}} flag){{genericConstraints}}
              {
                  value &= ~flag;
              }
              """
        );

    private static void GenerateAddExtension(IndentedTextWriter writer, string genericParams, string genericConstraints,
        string enumType) =>
        WriteCode(
            writer,
            $$"""
              /// <summary>
              /// Adds <paramref name="flag"/> to <paramref name="value"/>.
              /// </summary>
              /// <param name="value">The value to add the flag to</param>
              /// <param name="flag">The flag to add to the value</param>
              public static void Add{{genericParams}}(ref this {{enumType}} value, {{enumType}} flag){{genericConstraints}}
              {
                  value |= flag;
              }
              """
        );
}
