using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Jab.ViewModelFactory;

/// <summary>
/// Analyzer that validates View/ViewModel registrations in Jab service providers.
/// </summary>
/// <remarks>
/// <para>This analyzer reports the following diagnostics:</para>
/// <list type="bullet">
/// <item><description>JVMF001: View with Func&lt;ViewModel&gt; not registered in any provider</description></item>
/// <item><description>JVMF002: View registered but missing corresponding ViewModel factory registration</description></item>
/// <item><description>JVMF004: Multiple ViewModel constructors match the runtime parameter prefix</description></item>
/// </list>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JabViewModelFactoryAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => [Diagnostics.NotRegisteredAnywhere, Diagnostics.MissingViewModelRegistration, Diagnostics.AmbiguousCtors];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    /// <summary>
    /// Sets up analysis callbacks for the compilation.
    /// </summary>
    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var state = new AnalysisState();

        // Collect providers and view/ViewModel pairs incrementally
        context.RegisterSymbolAction(ctx => CollectTypeInfo(ctx, state), SymbolKind.NamedType);

        // Per-file analysis for immediate IDE feedback
        context.RegisterSemanticModelAction(ctx => AnalyzeForJVMF001(ctx, context.Compilation));
        context.RegisterSemanticModelAction(ctx => AnalyzeForJVMF002(ctx, context.Compilation));

        // Cross-file analysis at compilation end
        context.RegisterCompilationEndAction(ctx => ReportCrossFileDiagnostics(ctx, state));
    }

    #region Constants

    private const string ServiceProviderAttribute = "Jab.ServiceProviderAttribute";
    private const string ServiceProviderModuleAttribute = "Jab.ServiceProviderModuleAttribute";
    private const string InjectionConstructorAttribute = "Jab.ViewModelFactory.InjectionConstructorAttribute";

    #endregion

    #region JVMF001: View Not Registered

    /// <summary>
    /// Reports JVMF001 for views with Func&lt;ViewModel&gt; parameters that aren't registered anywhere.
    /// </summary>
    private static void AnalyzeForJVMF001(SemanticModelAnalysisContext ctx, Compilation compilation)
    {
        var model = ctx.SemanticModel;
        var root = model.SyntaxTree.GetRoot(ctx.CancellationToken);

        // Build set of all registered types across all providers
        var registeredTypes = CollectAllRegisteredTypes(compilation);

        // Check each class in this file
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var viewSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (viewSymbol is null)
                continue;

            // Skip if no constructor requests a Func<ViewModel>
            if (!HasViewModelFactoryParameter(classDecl, model))
                continue;

            // Report if not registered in any provider
            if (!registeredTypes.Contains(viewSymbol))
            {
                // Report on class identifier
                ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotRegisteredAnywhere, classDecl.Identifier.GetLocation(), viewSymbol.ToDisplayString()));

                // Also report on each constructor for better IDE experience
                foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.NotRegisteredAnywhere, ctor.Identifier.GetLocation(), viewSymbol.ToDisplayString()));
                }
            }
        }
    }

    /// <summary>
    /// Collects all types registered in any service provider/module.
    /// </summary>
    private static HashSet<INamedTypeSymbol> CollectAllRegisteredTypes(Compilation compilation)
    {
        var registered = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var type in GetAllNamedTypes(compilation))
        {
            if (!IsServiceProvider(type))
                continue;

            var regs = ExtractRegistrations(type);
            foreach (var reg in regs.Registrations)
                registered.Add(reg.Type);
        }

        return registered;
    }

    /// <summary>
    /// Checks if a class has any constructor with a Func&lt;ViewModel&gt; parameter.
    /// </summary>
    private static bool HasViewModelFactoryParameter(ClassDeclarationSyntax classDecl, SemanticModel model)
    {
        return classDecl.Members.OfType<ConstructorDeclarationSyntax>().Any(ctor => GetFuncType(model, ctor) is not null);
    }

    #endregion

    #region JVMF002: Missing ViewModel Registration

    /// <summary>
    /// Reports JVMF002 for providers that have views registered without corresponding ViewModel registrations
    /// or missing partial method stubs.
    /// </summary>
    private static void AnalyzeForJVMF002(SemanticModelAnalysisContext ctx, Compilation compilation)
    {
        var model = ctx.SemanticModel;
        var root = model.SyntaxTree.GetRoot(ctx.CancellationToken);

        // Collect all view/ViewModel pairs from the compilation
        var viewViewModelPairs = CollectViewViewModelPairs(compilation);
        if (viewViewModelPairs.Count == 0)
            return;

        // Check each service provider in this file
        foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var providerSymbol = model.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
            if (providerSymbol is null || !IsServiceProvider(providerSymbol))
                continue;

            var regs = ExtractRegistrations(providerSymbol);

            foreach (var pair in viewViewModelPairs)
            {
                // Check if this provider registers the view
                var viewReg = regs.Registrations.FirstOrDefault(r => SymbolEqualityComparer.Default.Equals(r.Type, pair.View));

                if (viewReg.Type is null)
                    continue;

                // Build factory info to check both attribute and stub
                var factoryInfo = ViewModelFactoryInfo.Create(pair.ViewModel, pair.FuncType);
                if (factoryInfo is null)
                    continue;

                // Check if the corresponding Func<ViewModel> is registered with a Factory
                var hasViewModelFunc = regs.Registrations.Any(r => IsFuncReturningViewModel(r.Type, pair.ViewModel) && r.HasFactory);

                // Check if the partial method stub exists with correct signature
                var hasStub = providerSymbol.GetMembers().OfType<IMethodSymbol>().Any(m => factoryInfo.MatchesMethod(m));

                if (!hasViewModelFunc || !hasStub)
                    ReportMissingViewModelRegistration(ctx, viewReg, providerSymbol, pair, factoryInfo, hasViewModelFunc, hasStub);
            }
        }
    }

    /// <summary>
    /// Collects all view/ViewModel pairs from the compilation.
    /// </summary>
    private static List<(INamedTypeSymbol View, INamedTypeSymbol ViewModel, INamedTypeSymbol FuncType)> CollectViewViewModelPairs(Compilation compilation)
    {
        var pairs = new List<(INamedTypeSymbol, INamedTypeSymbol, INamedTypeSymbol)>();

        foreach (var typeSymbol in GetAllNamedTypes(compilation))
        {
            foreach (var location in typeSymbol.Locations.Where(l => l.IsInSource))
            {
                var syntaxTree = location.SourceTree;
                if (syntaxTree is null)
                    continue;

                var syntaxNode = syntaxTree.GetRoot()?.FindNode(location.SourceSpan);
                if (syntaxNode is not ClassDeclarationSyntax classDecl)
                    continue;

                var model = compilation.GetSemanticModel(syntaxTree);

                foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
                {
                    var funcType = GetFuncType(model, ctor);
                    if (funcType is null)
                        continue;

                    var viewModel = funcType.TypeArguments.LastOrDefault() as INamedTypeSymbol;
                    if (viewModel is not null)
                    {
                        pairs.Add((typeSymbol, viewModel, funcType));
                        break; // One match per view is sufficient
                    }
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// Reports a JVMF002 diagnostic for a missing ViewModel registration or stub.
    /// </summary>
    private static void ReportMissingViewModelRegistration(SemanticModelAnalysisContext ctx, (INamedTypeSymbol Type, bool HasFactory, Location? AttributeLocation) viewReg,
                                                           INamedTypeSymbol provider, (INamedTypeSymbol View, INamedTypeSymbol ViewModel, INamedTypeSymbol FuncType) pair,
                                                           ViewModelFactoryInfo factoryInfo, bool hasAttribute, bool hasStub)
    {
        var props = ImmutableDictionary<string, string?>.Empty.Add("viewModelName", pair.ViewModel.ToDisplayString()).Add("viewModelShort", pair.ViewModel.Name)
                                                        .Add("viewName", pair.View.ToDisplayString()).Add("funcType", factoryInfo.FuncTypeDisplay)
                                                        .Add("runtimeTypes", factoryInfo.GetRuntimeTypesString()).Add("injectedSignature", factoryInfo.InjectedSignature)
                                                        .Add("hasAttribute", hasAttribute.ToString()).Add("hasStub", hasStub.ToString());

        var location = viewReg.AttributeLocation ?? provider.Locations.FirstOrDefault();
        if (location is not null)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingViewModelRegistration, location, props, pair.ViewModel.ToDisplayString(), pair.ViewModel.Name));
        }
    }

    #endregion

    #region Symbol Collection

    /// <summary>
    /// Collects provider and view/ViewModel information for a named type.
    /// </summary>
    private static void CollectTypeInfo(SymbolAnalysisContext ctx, AnalysisState state)
    {
        if (ctx.Symbol is not INamedTypeSymbol typeSymbol)
            return;

        // Collect service providers/modules
        if (IsServiceProvider(typeSymbol))
        {
            var regs = ExtractRegistrations(typeSymbol);
            lock (state.Providers)
            {
                state.Providers[typeSymbol] = regs;
            }
        }

        // Collect view/ViewModel pairs from constructors
        foreach (var location in typeSymbol.Locations.Where(l => l.IsInSource))
        {
            var syntaxNode = location.SourceTree?.GetRoot()?.FindNode(location.SourceSpan);
            if (syntaxNode is not ClassDeclarationSyntax classDecl)
                continue;

            var model = ctx.Compilation.GetSemanticModel(location.SourceTree!);

            foreach (var ctor in classDecl.Members.OfType<ConstructorDeclarationSyntax>())
            {
                var funcType = GetFuncType(model, ctor);
                if (funcType is null)
                    continue;

                var viewModel = funcType.TypeArguments.LastOrDefault() as INamedTypeSymbol;
                if (viewModel is null)
                    continue;

                var runtimeTypes = funcType.TypeArguments.Take(funcType.TypeArguments.Length - 1).ToArray();

                lock (state.ViewViewModelPairs)
                {
                    state.ViewViewModelPairs.Add((classDecl, typeSymbol, viewModel, runtimeTypes, funcType));
                }
            }
        }
    }

    /// <summary>
    /// Enumerates all named types in the compilation.
    /// </summary>
    private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.GlobalNamespace);

        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var member in ns.GetMembers())
            {
                if (member is INamespaceSymbol childNs)
                    stack.Push(childNs);
                else if (member is INamedTypeSymbol type)
                    yield return type;
            }
        }
    }

    #endregion

    #region Cross-File Diagnostics

    /// <summary>
    /// Reports cross-file diagnostics (JVMF004) using collected state.
    /// Note: JVMF002 is handled in AnalyzeForJVMF002 to avoid duplicate diagnostics.
    /// </summary>
    private static void ReportCrossFileDiagnostics(CompilationAnalysisContext ctx, AnalysisState state)
    {
        if (state.ViewViewModelPairs.Count == 0)
            return;

        // JVMF004: Ambiguous constructors
        ReportAmbiguousConstructors(ctx, state);
    }

    /// <summary>
    /// Reports JVMF004 for ViewModels with ambiguous constructors.
    /// </summary>
    private static void ReportAmbiguousConstructors(CompilationAnalysisContext ctx, AnalysisState state)
    {
        foreach (var pair in state.ViewViewModelPairs)
        {
            var viewModel = pair.ViewModel;

            // Find all constructors matching the runtime parameter prefix
            var candidates = viewModel.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public).Where(c => c.Parameters.Length >= pair.RuntimeTypes.Length)
                                      .Where(c => ParameterPrefixMatches(c, pair.RuntimeTypes)).ToArray();

            // Not ambiguous if 0 or 1 candidates
            if (candidates.Length <= 1)
                continue;

            // Resolved if any constructor has [InjectionConstructor]
            var anyMarked = candidates.Any(c => c.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == InjectionConstructorAttribute));

            if (anyMarked)
                continue;

            // Report on each ambiguous constructor
            foreach (var ctor in candidates)
            {
                foreach (var location in ctor.Locations)
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(Diagnostics.AmbiguousCtors, location, viewModel.ToDisplayString()));
                }
            }
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Checks if a type is a Jab service provider or module.
    /// </summary>
    private static bool IsServiceProvider(INamedTypeSymbol type)
    {
        return type.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.ToDisplayString();
            return name == ServiceProviderAttribute || name == ServiceProviderModuleAttribute;
        });
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
    /// Gets the first Func&lt;...&gt; parameter type from a constructor.
    /// </summary>
    private static INamedTypeSymbol? GetFuncType(SemanticModel model, ConstructorDeclarationSyntax ctor)
    {
        foreach (var param in ctor.ParameterList.Parameters)
        {
            if (param.Type is null)
                continue;

            var type = model.GetTypeInfo(param.Type).ConvertedType as INamedTypeSymbol;
            if (type is null)
                continue;

            var originalDef = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (originalDef.StartsWith("global::System.Func"))
                return type;
        }
        return null;
    }

    /// <summary>
    /// Checks if a type is a Func&lt;..., ViewModel&gt; where the last type argument matches.
    /// </summary>
    private static bool IsFuncReturningViewModel(INamedTypeSymbol? type, INamedTypeSymbol expectedViewModel)
    {
        if (type is null || type.TypeArguments.Length < 1)
            return false;

        var originalDef = type.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        if (!originalDef.StartsWith("global::System.Func"))
            return false;

        var lastArg = type.TypeArguments[type.TypeArguments.Length - 1] as INamedTypeSymbol;
        return lastArg is not null && SymbolEqualityComparer.Default.Equals(lastArg, expectedViewModel);
    }

    /// <summary>
    /// Extracts registration information from a service provider/module.
    /// </summary>
    private static ProviderRegistrations ExtractRegistrations(INamedTypeSymbol provider)
    {
        var registrations = new List<(INamedTypeSymbol Type, bool HasFactory, Location? AttributeLocation)>();

        foreach (var attr in provider.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
                continue;

            // Match Jab lifetime attributes (Transient, Scoped, Singleton)
            var attrDef = attrClass.OriginalDefinition;
            var fullName = attrDef.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (!IsLifetimeAttribute(fullName))
                continue;

            // Extract the registered type from generic arguments or constructor arguments
            INamedTypeSymbol? registeredType = null;

            if (attrClass.TypeArguments.Length == 1)
                registeredType = attrClass.TypeArguments[0] as INamedTypeSymbol;
            else if (attrClass.TypeArguments.Length == 2)
                registeredType = attrClass.TypeArguments[1] as INamedTypeSymbol;
            else if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Kind == TypedConstantKind.Type)
                registeredType = attr.ConstructorArguments[0].Value as INamedTypeSymbol;

            if (registeredType is null)
                continue;

            var hasFactory = attr.NamedArguments.Any(kv => kv.Key == "Factory");
            var location = attr.ApplicationSyntaxReference?.GetSyntax()?.GetLocation();
            registrations.Add((registeredType, hasFactory, location));
        }

        return new ProviderRegistrations(provider, [..registrations]);
    }

    /// <summary>
    /// Checks if the attribute full name is a Jab lifetime attribute.
    /// </summary>
    private static bool IsLifetimeAttribute(string fullName)
        => fullName is "global::Jab.TransientAttribute" or "global::Jab.TransientAttribute<TService>" or "global::Jab.TransientAttribute<TService, TImpl>"
            or "global::Jab.ScopedAttribute" or "global::Jab.ScopedAttribute<TService>" or "global::Jab.ScopedAttribute<TService, TImpl>" or "global::Jab.SingletonAttribute"
            or "global::Jab.SingletonAttribute<TService>" or "global::Jab.SingletonAttribute<TService, TImpl>";

    #endregion

    #region Nested Types

    /// <summary>
    /// State collected during symbol analysis for cross-file diagnostics.
    /// </summary>
    private sealed class AnalysisState
    {
        public Dictionary<INamedTypeSymbol, ProviderRegistrations> Providers { get; } = new(SymbolEqualityComparer.Default);

        public List<(ClassDeclarationSyntax ViewDecl, INamedTypeSymbol View, INamedTypeSymbol ViewModel, ITypeSymbol[] RuntimeTypes, INamedTypeSymbol FuncType)> ViewViewModelPairs
        {
            get;
        } = [];
    }

    /// <summary>
    /// Registration information for a service provider/module.
    /// </summary>
    private sealed class ProviderRegistrations
    {
        public ProviderRegistrations(INamedTypeSymbol provider, ImmutableArray<(INamedTypeSymbol Type, bool HasFactory, Location? AttributeLocation)> registrations)
        {
            Provider = provider;
            Registrations = registrations;
        }

        public INamedTypeSymbol Provider { get; }

        public ImmutableArray<(INamedTypeSymbol Type, bool HasFactory, Location? AttributeLocation)> Registrations { get; }
    }

    #endregion
}
