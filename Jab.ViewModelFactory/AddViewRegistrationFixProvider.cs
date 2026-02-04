using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Jab.ViewModelFactory;

/// <summary>
/// Code fix provider for JVMF001 that adds a view registration to the nearest
/// service provider or module (in terms of folder proximity).
/// Also adds the ViewModel registration and partial method stub in one action.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddViewRegistrationFixProvider))]
[Shared]
public sealed class AddViewRegistrationFixProvider : CodeFixProvider
{
    /// <summary>The diagnostic IDs this provider can fix.</summary>
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.JVMF001];

    /// <summary>Return the batch fixer for FixAll operations.</summary>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers code fixes for JVMF001 by finding the nearest service provider/module
    /// and offering to add a Transient registration for the unregistered view,
    /// along with the ViewModel registration and partial method stub.
    /// </summary>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var document = context.Document;
        var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan);

        // Find the view class declaration from the diagnostic location.
        var viewClassDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (viewClassDecl is null)
            return;

        var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
        if (semanticModel is null)
            return;

        var viewSymbol = semanticModel.GetDeclaredSymbol(viewClassDecl, context.CancellationToken);
        if (viewSymbol is null)
            return;

        // Find the ViewModel info from the view's constructor
        var viewModelInfo = GetViewModelInfoFromView(viewClassDecl, semanticModel);

        // Find the nearest service provider/module in the solution.
        var nearestProvider = await FindNearestProviderAsync(document.Project.Solution, document.FilePath, context.CancellationToken).ConfigureAwait(false);
        if (nearestProvider is null)
            return;

        var viewFullName = viewSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted));
        var viewShortName = viewSymbol.Name;
        var providerDisplayName = Path.GetFileName(nearestProvider.Value.Document.FilePath) ?? nearestProvider.Value.ClassName;

        // Offer Transient as default, but also Scoped and Singleton options.
        foreach (var lifetime in new[] { "Transient", "Scoped", "Singleton" })
        {
            var title = viewModelInfo is not null ? $"Register view + ViewModel as {lifetime} in {providerDisplayName}" : $"Register view as {lifetime} in {providerDisplayName}";

            var action = CodeAction.Create(title, ct => AddViewRegistrationAsync(nearestProvider.Value.Document, nearestProvider.Value.ClassName, viewFullName, viewShortName, viewModelInfo,
                                                                                 lifetime, ct), $"AddView{lifetime}Registration_{viewFullName}");

            context.RegisterCodeFix(action, diagnostic);
        }
    }

    /// <summary>
    /// Extracts ViewModel information from the view's constructor Func parameter.
    /// </summary>
    private static ViewModelRegistrationInfo? GetViewModelInfoFromView(ClassDeclarationSyntax viewClassDecl, SemanticModel semanticModel)
    {
        foreach (var ctor in viewClassDecl.Members.OfType<ConstructorDeclarationSyntax>())
        {
            foreach (var param in ctor.ParameterList.Parameters)
            {
                if (param.Type is null)
                    continue;

                var typeInfo = semanticModel.GetTypeInfo(param.Type);
                if (typeInfo.Type is not INamedTypeSymbol namedType)
                    continue;

                var originalDef = namedType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                if (!originalDef.StartsWith("global::System.Func"))
                    continue;

                if (namedType.TypeArguments.Length < 1)
                    continue;

                // Last type argument is the ViewModel
                var viewModel = namedType.TypeArguments[namedType.TypeArguments.Length - 1] as INamedTypeSymbol;
                if (viewModel is null)
                    continue;

                // Build factory info
                var factoryInfo = ViewModelFactoryInfo.Create(viewModel, namedType);
                if (factoryInfo is null)
                    continue;

                return new ViewModelRegistrationInfo(viewModel.Name, factoryInfo.FuncTypeDisplay, factoryInfo.InjectedSignature);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the nearest service provider or module document by folder proximity to the given file path.
    /// </summary>
    private static async Task<(Document Document, string ClassName)?> FindNearestProviderAsync(Solution solution, string? sourceFilePath, CancellationToken cancellationToken)
    {
        if (String.IsNullOrEmpty(sourceFilePath))
            return null;

        var candidates = new List<(Document Document, string ClassName, string FilePath)>();

        // Scan all documents in the solution for service providers/modules.
        foreach (var project in solution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath is null)
                    continue;

                var root = await doc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                if (root is null)
                    continue;

                var semanticModel = await doc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (semanticModel is null)
                    continue;

                foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var symbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken);
                    if (symbol is null)
                        continue;

                    if (HasServiceProviderAttribute(symbol))
                        candidates.Add((doc, classDecl.Identifier.Text, doc.FilePath));
                }
            }
        }

        if (candidates.Count == 0)
            return null;

        // Find the nearest provider by folder proximity.
        var sourceDir = Path.GetDirectoryName(sourceFilePath) ?? "";
        var ranked = candidates.Select(c => (c.Document, c.ClassName, Distance: ComputeFolderDistance(sourceDir, Path.GetDirectoryName(c.FilePath) ?? ""))).OrderBy(x => x.Distance)
                               .ThenBy(x => x.ClassName) // Stable sort by name if same distance
                               .First();

        return (ranked.Document, ranked.ClassName);
    }

    /// <summary>
    /// Checks if the type has [ServiceProvider] or [ServiceProviderModule] attribute.
    /// </summary>
    private static bool HasServiceProviderAttribute(INamedTypeSymbol symbol)
    {
        return symbol.GetAttributes().Any(a =>
        {
            var name = a.AttributeClass?.ToDisplayString();

            return name == "Jab.ServiceProviderAttribute" || name == "Jab.ServiceProviderModuleAttribute";
        });
    }

    /// <summary>
    /// Computes a folder distance metric. Lower values mean closer folders.
    /// Same folder = 0, parent/child = 1, sibling = 2, etc.
    /// </summary>
    private static int ComputeFolderDistance(string sourceDir, string targetDir)
    {
        if (String.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
            return 0;

        var sourceParts = sourceDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var targetParts = targetDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Find common prefix length
        var commonLength = 0;
        var minLen = Math.Min(sourceParts.Length, targetParts.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (String.Equals(sourceParts[i], targetParts[i], StringComparison.OrdinalIgnoreCase))
                commonLength++;
            else
                break;
        }

        // Distance = steps up from source + steps down to target
        var stepsUp = sourceParts.Length - commonLength;
        var stepsDown = targetParts.Length - commonLength;

        return stepsUp + stepsDown;
    }

    /// <summary>
    /// Adds a view registration attribute to the specified provider document.
    /// Also adds the ViewModel registration and partial method stub if viewModelInfo is provided.
    /// </summary>
    private static async Task<Solution> AddViewRegistrationAsync(Document providerDocument, string providerClassName, string viewFullName, string viewShortName,
                                                                 ViewModelRegistrationInfo? viewModelInfo, string lifetime, CancellationToken cancellationToken)
    {
        var root = await providerDocument.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
            return providerDocument.Project.Solution;

        // Find the provider class declaration.
        var providerDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == providerClassName);

        if (providerDecl is null)
            return providerDocument.Project.Solution;

        var updatedProvider = providerDecl;

        // Build the view attribute: [Transient<ViewType>] or [Lifetime<ViewType>]
        var viewAttr = SyntaxFactory.Attribute(SyntaxFactory.GenericName(SyntaxFactory.Identifier(lifetime),
                                                                         SyntaxFactory.TypeArgumentList(SyntaxFactory
                                                                                                            .SingletonSeparatedList(SyntaxFactory.ParseTypeName(viewShortName)))));

        // Find the best insertion point - after existing lifetime attributes for views
        var existingLists = updatedProvider.AttributeLists;
        var insertIndex = FindInsertionIndex(existingLists);

        // Create attribute list with view registration
        var viewAttrList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(viewAttr));

        if (viewModelInfo is not null)
        {
            // Build the ViewModel attribute: [Lifetime<Func<ViewModel>>(Factory = nameof(CreateViewModel))]
            var viewModelAttr =
                SyntaxFactory.Attribute(SyntaxFactory.GenericName(SyntaxFactory.Identifier(lifetime), SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.ParseTypeName(viewModelInfo.FuncType)))),
                                        SyntaxFactory.AttributeArgumentList(SyntaxFactory
                                                                                .SingletonSeparatedList(SyntaxFactory.AttributeArgument(SyntaxFactory.NameEquals("Factory"), null,
                                                                                                            SyntaxFactory.ParseExpression($"nameof(Create{viewModelInfo
                                                                                                                .ViewModelShortName})")))));

            // Combine view and viewModel attributes in same list: [Transient<View>, Transient<Func<ViewModel>>(Factory = ...)]
            viewAttrList = SyntaxFactory.AttributeList(SyntaxFactory.SeparatedList(new[] { viewAttr, viewModelAttr }));
        }

        viewAttrList = viewAttrList.WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        // Insert the attribute list
        SyntaxList<AttributeListSyntax> newLists;
        if (insertIndex >= 0 && insertIndex < existingLists.Count)
            newLists = existingLists.Insert(insertIndex + 1, viewAttrList);
        else
            newLists = existingLists.Add(viewAttrList);

        updatedProvider = updatedProvider.WithAttributeLists(newLists);

        // Add partial method stub if we have ViewModel info
        if (viewModelInfo is not null)
        {
            var stub = CreatePartialMethodStub(viewModelInfo);
            if (stub is not null)
                updatedProvider = updatedProvider.AddMembers(stub);
        }

        var newRoot = root.ReplaceNode(providerDecl, updatedProvider);

        // Add using directive if needed
        if (newRoot is CompilationUnitSyntax cu)
        {
            var viewNamespace = GetNamespace(viewFullName);
            if (!String.IsNullOrEmpty(viewNamespace))
            {
                var hasUsing = cu.Usings.Any(u => u.Name?.ToString() == viewNamespace);
                if (!hasUsing)
                {
                    cu = cu.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(viewNamespace!)));
                    newRoot = cu;
                }
            }
        }

        return providerDocument.Project.Solution.WithDocumentSyntaxRoot(providerDocument.Id, newRoot);
    }

    /// <summary>
    /// Creates a partial method stub for the ViewModel factory.
    /// </summary>
    private static MethodDeclarationSyntax? CreatePartialMethodStub(ViewModelRegistrationInfo info)
    {
        if (String.IsNullOrEmpty(info.FuncType))
            return null;

        var parameters = new List<ParameterSyntax>();
        if (!String.IsNullOrEmpty(info.InjectedSignature))
        {
            var paramParts = info.InjectedSignature.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in paramParts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0)
                    continue;

                var lastSpace = trimmed.LastIndexOf(' ');
                if (lastSpace <= 0)
                    continue;

                var typeName = trimmed.Substring(0, lastSpace);
                var paramName = trimmed.Substring(lastSpace + 1);

                parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(typeName)));
            }
        }

        return SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(info.FuncType), $"Create{info.ViewModelShortName}")
                            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                                                                   SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>
    /// Finds the best index to insert a new view registration attribute.
    /// Prefers inserting after the last lifetime attribute without a Factory argument.
    /// </summary>
    private static int FindInsertionIndex(SyntaxList<AttributeListSyntax> lists)
    {
        var lastViewAttrIndex = -1;

        for (var i = 0; i < lists.Count; i++)
        {
            var list = lists[i];
            foreach (var attr in list.Attributes)
            {
                var name = attr.Name.ToString();
                var isLifetime = name.Contains("Transient") || name.Contains("Scoped") || name.Contains("Singleton");
                var hasFactory = attr.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Factory") == true;

                if (isLifetime && !hasFactory)
                    lastViewAttrIndex = i;
            }
        }

        return lastViewAttrIndex;
    }

    /// <summary>
    /// Extracts the namespace from a fully qualified type name.
    /// </summary>
    private static string? GetNamespace(string fullName)
    {
        var lastDot = fullName.LastIndexOf('.');
        return lastDot > 0 ? fullName.Substring(0, lastDot) : null;
    }

    #region Nested Type: ViewModelRegistrationInfo

    #region Helper Types

    /// <summary>
    /// Information needed to register a ViewModel.
    /// </summary>
    private sealed class ViewModelRegistrationInfo
    {
        public ViewModelRegistrationInfo(string viewModelShortName, string funcType, string injectedSignature)
        {
            ViewModelShortName = viewModelShortName;
            FuncType = funcType;
            InjectedSignature = injectedSignature;
        }

        public string FuncType { get; }

        public string InjectedSignature { get; }

        public string ViewModelShortName { get; }
    }

    #endregion

    #endregion
}
