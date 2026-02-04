using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Jab.ViewModelFactory;

/// <summary>
/// Centralized information about a ViewModel factory method.
/// Used by both the analyzer and code fix to ensure consistency.
/// </summary>
internal sealed class ViewModelFactoryInfo
{
    private const string InjectionConstructorAttribute = "Jab.ViewModelFactory.InjectionConstructorAttribute";

    private ViewModelFactoryInfo(INamedTypeSymbol viewModel, INamedTypeSymbol funcType, string factoryName, ImmutableArray<ITypeSymbol> runtimeTypes,
                                 ImmutableArray<IParameterSymbol> injectedParams, string funcTypeDisplay, string injectedSignature)
    {
        ViewModel = viewModel;
        FuncType = funcType;
        FactoryName = factoryName;
        RuntimeTypes = runtimeTypes;
        InjectedParams = injectedParams;
        FuncTypeDisplay = funcTypeDisplay;
        InjectedSignature = injectedSignature;
    }

    /// <summary>The factory method name (e.g., CreateMainViewModel).</summary>
    public string FactoryName { get; }

    /// <summary>The Func type (e.g., Func&lt;Guid, ViewModel&gt;).</summary>
    public INamedTypeSymbol FuncType { get; }

    /// <summary>The Func type as a display string for attributes.</summary>
    public string FuncTypeDisplay { get; }

    /// <summary>Injected parameters from the ViewModel constructor (after runtime params).</summary>
    public ImmutableArray<IParameterSymbol> InjectedParams { get; }

    /// <summary>The injected parameter signature for the factory method (e.g., "ApiClient d0, ILogger d1").</summary>
    public string InjectedSignature { get; }

    /// <summary>Runtime types passed to the Func (all type args except the last).</summary>
    public ImmutableArray<ITypeSymbol> RuntimeTypes { get; }

    /// <summary>The ViewModel type.</summary>
    public INamedTypeSymbol ViewModel { get; }

    /// <summary>
    /// Creates factory info from a ViewModel and its Func type.
    /// Returns null if the ViewModel constructor cannot be resolved.
    /// </summary>
    public static ViewModelFactoryInfo? Create(INamedTypeSymbol viewModel, INamedTypeSymbol funcType)
    {
        if (viewModel is null || funcType is null)
            return null;

        // Extract runtime types (all type args except the last, which is the ViewModel)
        var typeArgs = funcType.TypeArguments;
        if (typeArgs.Length < 1)
            return null;

        var runtimeTypes = typeArgs.RemoveAt(typeArgs.Length - 1);

        // Find the best matching constructor on the ViewModel
        var injectedParams = GetInjectedParameters(viewModel, runtimeTypes);
        if (injectedParams.IsDefault)
            return null;

        var factoryName = $"Create{viewModel.Name}";
        var funcTypeDisplay = funcType.ToDisplayString();

        // Build injected signature
        var injectedSigParts = new List<string>(injectedParams.Length);
        for (var i = 0; i < injectedParams.Length; i++)
        {
            var typeName = FormatType(injectedParams[i].Type);
            injectedSigParts.Add($"{typeName} d{i}");
        }
        var injectedSignature = String.Join(", ", injectedSigParts);

        return new ViewModelFactoryInfo(viewModel, funcType, factoryName, runtimeTypes, injectedParams, funcTypeDisplay, injectedSignature);
    }

    /// <summary>
    /// Gets the injected parameters from the ViewModel constructor (parameters after the runtime types).
    /// </summary>
    private static ImmutableArray<IParameterSymbol> GetInjectedParameters(INamedTypeSymbol viewModel, ImmutableArray<ITypeSymbol> runtimeTypes)
    {
        // Find all public constructors where first N params match the runtime types
        var candidates = viewModel.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).Where(c => c.Parameters.Length >= runtimeTypes.Length)
                                  .Where(c => ParameterPrefixMatches(c, runtimeTypes)).ToArray();

        if (candidates.Length == 0)
            return default(ImmutableArray<IParameterSymbol>);

        // Prefer constructor marked with [InjectionConstructor]
        var preferred = candidates.FirstOrDefault(c => c.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == InjectionConstructorAttribute));

        var chosen = preferred ?? (candidates.Length == 1 ? candidates[0] : null);
        if (chosen is null)
            return default(ImmutableArray<IParameterSymbol>);

        // Return the injected parameters (those after the runtime prefix)
        return chosen.Parameters.Skip(runtimeTypes.Length).ToImmutableArray();
    }

    /// <summary>
    /// Checks if the first N parameters of the constructor match the runtime types.
    /// </summary>
    private static bool ParameterPrefixMatches(IMethodSymbol ctor, ImmutableArray<ITypeSymbol> runtimeTypes)
    {
        for (var i = 0; i < runtimeTypes.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(runtimeTypes[i], ctor.Parameters[i].Type))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Formats a type symbol for code generation.
    /// </summary>
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

    /// <summary>
    /// Checks if a method symbol matches this factory's expected signature.
    /// </summary>
    public bool MatchesMethod(IMethodSymbol method)
    {
        if (method.Name != FactoryName)
            return false;

        if (!method.IsStatic || !method.IsPartialDefinition)
            return false;

        // Check parameter count matches
        if (method.Parameters.Length != InjectedParams.Length)
            return false;

        // Check each parameter type matches
        for (var i = 0; i < InjectedParams.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(method.Parameters[i].Type, InjectedParams[i].Type))
                return false;
        }

        // Check return type is the correct Func type
        if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, FuncType))
            return false;

        return true;
    }

    /// <summary>
    /// Serializes runtime types to a comma-separated string for diagnostic properties.
    /// </summary>
    public string GetRuntimeTypesString()
    {
        return RuntimeTypes.Length > 0 ? String.Join(",", RuntimeTypes.Select(t => t.ToDisplayString())) : "";
    }
}
