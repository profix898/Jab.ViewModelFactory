using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Jab.ViewModelFactory;

/// <summary>
/// Incremental source generator that emits factory methods for ViewModels.
/// </summary>
/// <remarks>
/// <para>
/// This generator discovers classes with constructors that request a <c>Func&lt;..., TViewModel&gt;</c>
/// parameter and emits factory methods in the Jab service provider partial class.
/// </para>
/// <para>
/// For a view constructor like <c>MainView(Func&lt;MainViewModel&gt; factory)</c>, the generator
/// produces a factory method that returns a <c>Func</c> delegate:
/// </para>
/// <code>
/// public static Func&lt;MainViewModel&gt; CreateMainViewModel(IService dep)
///     =&gt; () =&gt; new MainViewModel(dep);
/// </code>
/// <para>
/// For views with runtime parameters like <c>DetailView(Func&lt;Guid, DetailViewModel&gt; factory)</c>:
/// </para>
/// <code>
/// public static Func&lt;Guid, DetailViewModel&gt; CreateDetailViewModel(IService dep)
///     =&gt; (Guid a0) =&gt; new DetailViewModel(a0, dep);
/// </code>
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class JabViewModelFactoryGenerator : IIncrementalGenerator
{
    #region Implementation of IIncrementalGenerator

    #region IIncrementalGenerator

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Step 1: Find all Jab service provider/module classes
        var containers = context.SyntaxProvider.CreateSyntaxProvider(static (node, _) => node is ClassDeclarationSyntax, static (ctx, _) => ExtractContainerInfo(ctx))
                                .Where(static x => x is not null).Select(static (x, _) => x!);

        // Step 2: Find all constructors requesting Func<..., ViewModel> parameters
        var factoryRequests = context.SyntaxProvider.CreateSyntaxProvider(static (node, _) => node is ConstructorDeclarationSyntax, static (ctx, _) => ExtractViewSpec(ctx))
                                     .Where(static s => s is not null).Select(static (s, _) => s!);

        // Step 3: Combine and emit source for each container
        var combined = containers.Collect().Combine(factoryRequests.Collect());
        context.RegisterSourceOutput(combined, static (ctx, data) => EmitFactoryMethods(ctx, data.Left, data.Right));
    }

    #endregion

    #endregion

    #region Type Formatting

    /// <summary>
    /// Formats a type symbol using Jab's naming convention.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description><c>System.*</c> types retain the <c>global::</c> prefix (e.g., <c>global::System.Guid</c>)</description></item>
    /// <item><description>Other types use fully qualified names without <c>global::</c> (e.g., <c>MyApp.ViewModels.MainViewModel</c>)</description></item>
    /// </list>
    /// </remarks>
    private static string FormatType(ITypeSymbol type)
    {
        var fullyQualified = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Keep global:: prefix for System types
        if (fullyQualified.StartsWith("global::System"))
            return fullyQualified;

        // Strip global:: prefix for custom types
        const string globalPrefix = "global::";
        return fullyQualified.StartsWith(globalPrefix) ? fullyQualified.Substring(globalPrefix.Length) : fullyQualified;
    }

    #endregion

    #region Constants

    private const string ServiceProviderAttribute = "Jab.ServiceProviderAttribute";
    private const string ServiceProviderModuleAttribute = "Jab.ServiceProviderModuleAttribute";
    private const string InjectionConstructorAttribute = "Jab.ViewModelFactory.InjectionConstructorAttribute";

    #endregion

    #region Source Emission

    /// <summary>
    /// Emits factory method source files for each discovered container.
    /// </summary>
    private static void EmitFactoryMethods(SourceProductionContext context, ImmutableArray<ContainerInfo> containers, ImmutableArray<ViewSpec> specs)
    {
        // Early exit if nothing to generate
        if (containers.Length == 0 || specs.Length == 0)
            return;

        foreach (var container in containers)
        {
            // Deduplicate ViewSpecs by ViewModel metadata name
            var uniqueSpecs = new Dictionary<string, ViewSpec>(StringComparer.Ordinal);
            foreach (var spec in specs)
            {
                if (!uniqueSpecs.ContainsKey(spec.ViewModelMetadataName))
                    uniqueSpecs.Add(spec.ViewModelMetadataName, spec);
            }

            if (uniqueSpecs.Count == 0)
                continue;

            // Build the generated source
            var source = GenerateFactorySource(container, uniqueSpecs.Values);
            context.AddSource($"{container.HintName}.ViewModelFactories.g.cs", source);
        }
    }

    /// <summary>
    /// Generates the source text for factory methods in a container.
    /// </summary>
    private static string GenerateFactorySource(ContainerInfo container, IEnumerable<ViewSpec> specs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine();

        // Namespace declaration (if any)
        if (!String.IsNullOrEmpty(container.Namespace))
        {
            sb.Append("namespace ").Append(container.Namespace).AppendLine(";");
            sb.AppendLine();
        }

        // Partial class declaration
        sb.Append("public partial class ").AppendLine(container.TypeName);
        sb.AppendLine("{");

        // Generate each factory method
        var specList = specs as IList<ViewSpec> ?? specs.ToList();
        for (var i = 0; i < specList.Count; i++)
        {
            var spec = specList[i];
            sb.Append("    /// <summary>Factory method for <see cref=\"").Append(spec.ViewModelDisplayName).AppendLine("\"/>.</summary>");

            sb.Append("    public static partial ").Append(spec.FuncReturnType).Append(' ').Append(spec.FactoryName).Append('(').Append(spec.InjectedSignature).AppendLine(")");

            sb.AppendLine("    {");
            sb.Append("        return ").Append(spec.LambdaSignature).Append(" => new ").Append(spec.ViewModelFullName).Append('(').Append(spec.CtorCallArgs).AppendLine(");");
            sb.AppendLine("    }");

            if (i < specList.Count - 1)
                sb.AppendLine();
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    #endregion

    #region Container Discovery

    /// <summary>
    /// Extracts container info from a class if it's a Jab service provider/module.
    /// </summary>
    private static ContainerInfo? ExtractContainerInfo(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax) context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
        if (symbol is null)
            return null;

        // Only process classes marked with Jab attributes
        if (!HasAttribute(symbol, ServiceProviderAttribute) && !HasAttribute(symbol, ServiceProviderModuleAttribute))
            return null;

        var ns = symbol.ContainingNamespace?.IsGlobalNamespace == false ? symbol.ContainingNamespace.ToDisplayString() : null;

        // Create a hint name for the generated file (Namespace.TypeName)
        var hintName = String.IsNullOrEmpty(ns) ? symbol.Name : $"{ns}.{symbol.Name}";

        return new ContainerInfo(ns, symbol.Name, hintName);
    }

    /// <summary>
    /// Checks if the type has an attribute with the specified full name.
    /// </summary>
    private static bool HasAttribute(INamedTypeSymbol type, string attributeFullName)
    {
        return type.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == attributeFullName);
    }

    #endregion

    #region Factory Discovery

    /// <summary>
    /// Extracts a <see cref="ViewSpec" /> from a constructor that requests a <c>Func&lt;..., TViewModel&gt;</c> parameter.
    /// </summary>
    private static ViewSpec? ExtractViewSpec(GeneratorSyntaxContext context)
    {
        var ctor = (ConstructorDeclarationSyntax) context.Node;
        if (ctor.ParameterList.Parameters.Count == 0)
            return null;

        var model = context.SemanticModel;

        // Find the first Func<..., TViewModel> parameter
        var funcType = FindFuncParameter(ctor, model);
        if (funcType is null)
            return null;

        var typeArgs = funcType.TypeArguments;
        if (typeArgs.Length < 1)
            return null;

        // Last type argument is the ViewModel type
        var viewModel = typeArgs[typeArgs.Length - 1] as INamedTypeSymbol;
        if (viewModel is null)
            return null;

        // Preceding type arguments are runtime parameters (passed at call site)
        var runtimeTypes = typeArgs.RemoveAt(typeArgs.Length - 1).ToArray();

        // Find the best matching constructor on the ViewModel
        var ctorResult = SelectViewModelConstructor(viewModel, runtimeTypes);
        if (ctorResult.Error is not null)
            return null; // Generator can't report diagnostics; analyzer will handle it

        // Build the factory method components
        return BuildViewSpec(viewModel, runtimeTypes, ctorResult.InjectedParams, funcType);
    }

    /// <summary>
    /// Finds the first <c>Func&lt;...&gt;</c> parameter in the constructor.
    /// </summary>
    private static INamedTypeSymbol? FindFuncParameter(ConstructorDeclarationSyntax ctor, SemanticModel model)
    {
        foreach (var param in ctor.ParameterList.Parameters)
        {
            if (param.Type is null)
                continue;

            var typeInfo = model.GetTypeInfo(param.Type);
            if (typeInfo.ConvertedType is INamedTypeSymbol namedType && IsFuncType(namedType))
                return namedType;
        }
        return null;
    }

    /// <summary>
    /// Determines if the type is a <c>System.Func&lt;...&gt;</c>.
    /// </summary>
    private static bool IsFuncType(INamedTypeSymbol? type)
    {
        if (type is null)
            return false;

        var originalDef = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return originalDef.StartsWith("global::System.Func");
    }

    /// <summary>
    /// Selects the best ViewModel constructor matching the runtime parameter prefix.
    /// Prefers constructors marked with <see cref="InjectionConstructorAttribute" />.
    /// </summary>
    private static (ImmutableArray<IParameterSymbol> InjectedParams, Diagnostic? Error) SelectViewModelConstructor(INamedTypeSymbol viewModel, ITypeSymbol[] runtimeTypes)
    {
        // Find all public constructors where first N params match the runtime types
        var candidates = viewModel.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).Where(c => c.Parameters.Length >= runtimeTypes.Length)
                                  .Where(c => ParameterPrefixMatches(c, runtimeTypes)).ToArray();

        if (candidates.Length == 0)
        {
            var error = Diagnostic.Create(Diagnostics.NoMatchingCtor, viewModel.Locations.FirstOrDefault(), viewModel.ToDisplayString(), runtimeTypes.Length);
            return (default(ImmutableArray<IParameterSymbol>), error);
        }

        // Prefer constructor marked with [InjectionConstructor]
        var preferred = candidates.FirstOrDefault(c => c.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == InjectionConstructorAttribute));

        var chosen = preferred ?? (candidates.Length == 1 ? candidates[0] : null);

        if (chosen is null)
        {
            var error = Diagnostic.Create(Diagnostics.AmbiguousCtors, viewModel.Locations.FirstOrDefault(), viewModel.ToDisplayString());
            return (default(ImmutableArray<IParameterSymbol>), error);
        }

        // Return the injected parameters (those after the runtime prefix)
        var injected = chosen.Parameters.Skip(runtimeTypes.Length).ToImmutableArray();
        return (injected, null);
    }

    /// <summary>
    /// Checks if the first N parameters of the constructor match the runtime types.
    /// </summary>
    private static bool ParameterPrefixMatches(IMethodSymbol ctor, ITypeSymbol[] runtimeTypes)
    {
        for (var i = 0; i < runtimeTypes.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(runtimeTypes[i], ctor.Parameters[i].Type))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Builds a <see cref="ViewSpec" /> from the analyzed constructor information.
    /// </summary>
    private static ViewSpec BuildViewSpec(INamedTypeSymbol viewModel, ITypeSymbol[] runtimeTypes, ImmutableArray<IParameterSymbol> injectedParams, INamedTypeSymbol funcType)
    {
        // Build parameter lists for the factory method
        var runtimeSigParts = new List<string>(runtimeTypes.Length);
        var callParts = new List<string>(runtimeTypes.Length + injectedParams.Length);
        var injectedSigParts = new List<string>(injectedParams.Length);

        // Runtime parameters (passed to the returned Func)
        for (var i = 0; i < runtimeTypes.Length; i++)
        {
            var typeName = FormatType(runtimeTypes[i]);
            runtimeSigParts.Add($"{typeName} a{i}");
            callParts.Add($"a{i}");
        }

        // Injected parameters (passed to the factory method, forwarded to ctor)
        for (var j = 0; j < injectedParams.Length; j++)
        {
            var typeName = FormatType(injectedParams[j].Type);
            injectedSigParts.Add($"{typeName} d{j}");
            callParts.Add($"d{j}");
        }

        // Build the Func<...> return type string
        var funcTypeArgs = runtimeTypes.Select(FormatType).ToList();
        funcTypeArgs.Add(FormatType(viewModel));
        var funcReturnType = $"System.Func<{String.Join(", ", funcTypeArgs)}>";

        // Lambda signature: "()" or "(Type a0)" or "(Type a0, Type a1)"
        var lambdaSig = runtimeSigParts.Count == 0 ? "()" : $"({String.Join(", ", runtimeSigParts)})";

        return new ViewSpec(viewModel.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), viewModel.ToDisplayString(), FormatType(viewModel), $"Create{viewModel.Name}",
                            String.Join(", ", injectedSigParts), String.Join(", ", callParts), funcReturnType, lambdaSig, funcType.ToDisplayString());
    }

    #endregion

    #region Nested Types

    /// <summary>
    /// Information about a Jab service provider/module container.
    /// </summary>
    private sealed class ContainerInfo
    {
        public ContainerInfo(string? ns, string typeName, string hintName)
        {
            Namespace = ns;
            TypeName = typeName;
            HintName = hintName;
        }

        /// <summary>A unique hint name for the generated file.</summary>
        public string HintName { get; }

        /// <summary>The namespace of the container, or null if global.</summary>
        public string? Namespace { get; }

        /// <summary>The type name of the container class.</summary>
        public string TypeName { get; }
    }

    /// <summary>
    /// Specification for a ViewModel factory method to be generated.
    /// </summary>
    private sealed class ViewSpec
    {
        public ViewSpec(string viewModelMetadataName, string viewModelDisplayName, string viewModelFullName, string factoryName, string injectedSignature, string ctorCallArgs,
                        string funcReturnType, string lambdaSignature, string funcTypeDisplay)
        {
            ViewModelMetadataName = viewModelMetadataName;
            ViewModelDisplayName = viewModelDisplayName;
            ViewModelFullName = viewModelFullName;
            FactoryName = factoryName;
            InjectedSignature = injectedSignature;
            CtorCallArgs = ctorCallArgs;
            FuncReturnType = funcReturnType;
            LambdaSignature = lambdaSignature;
            FuncTypeDisplay = funcTypeDisplay;
        }

        /// <summary>Arguments to pass to the ViewModel constructor.</summary>
        public string CtorCallArgs { get; }

        /// <summary>Name of the factory method (e.g., CreateMainViewModel).</summary>
        public string FactoryName { get; }

        /// <summary>Return type of the factory (e.g., System.Func&lt;Guid, DetailViewModel&gt;).</summary>
        public string FuncReturnType { get; }

        /// <summary>The Func type as declared in the view's constructor.</summary>
        public string FuncTypeDisplay { get; }

        /// <summary>Parameters for the factory method (DI-injected dependencies).</summary>
        public string InjectedSignature { get; }

        /// <summary>Lambda parameter signature (e.g., "(global::System.Guid a0)").</summary>
        public string LambdaSignature { get; }

        /// <summary>Human-readable ViewModel name for comments.</summary>
        public string ViewModelDisplayName { get; }

        /// <summary>Fully qualified ViewModel name for code generation.</summary>
        public string ViewModelFullName { get; }

        /// <summary>Stable identifier for deduplication (metadata name).</summary>
        public string ViewModelMetadataName { get; }
    }

    #endregion
}
