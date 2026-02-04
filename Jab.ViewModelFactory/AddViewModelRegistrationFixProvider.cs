using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Jab.ViewModelFactory;

/// <summary>
/// Code fix provider for JVMF002 that adds a ViewModel registration attribute
/// and partial method stub next to the corresponding view registration in a Jab service provider.
/// </summary>
/// <remarks>
/// <para>
/// This provider generates attributes of the form:
/// <c>[Transient&lt;System.Func&lt;ViewModel&gt;&gt;(Factory = nameof(CreateViewModel))]</c>
/// </para>
/// <para>
/// It also generates a partial method stub declaration that the source generator will implement:
/// <c>public static partial Func&lt;ViewModel&gt; CreateViewModel(IDependency d0);</c>
/// </para>
/// <para>
/// The fix inserts the ViewModel registration immediately after the view registration
/// and optionally sorts View/ViewModel pairs for readability.
/// </para>
/// </remarks>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(AddViewModelRegistrationFixProvider))]
[Shared]
public sealed class AddViewModelRegistrationFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.JVMF002];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => ViewModelRegistrationFixAllProvider.Instance;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.First();
        var properties = diagnostic.Properties;

        // Read properties from the diagnostic
        if (!properties.TryGetValue("viewModelName", out var viewModelFull) || String.IsNullOrWhiteSpace(viewModelFull))
            return;

        var viewModelShort = properties.TryGetValue("viewModelShort", out var shortName) && !String.IsNullOrWhiteSpace(shortName) ? shortName! : viewModelFull!.Split('.').Last();

        var funcType = properties.TryGetValue("funcType", out var ft) && !String.IsNullOrWhiteSpace(ft) ? ft! : $"System.Func<{viewModelFull}>";

        var injectedSignature = properties.TryGetValue("injectedSignature", out var sig) ? sig ?? "" : "";

        // Check what's missing
        var hasAttribute = properties.TryGetValue("hasAttribute", out var ha) && ha == "True";
        var hasStub = properties.TryGetValue("hasStub", out var hs) && hs == "True";

        // Offer one fix per lifetime option
        foreach (var lifetime in new[] { "Transient", "Scoped", "Singleton" })
        {
            var action = CodeAction.Create($"Add {lifetime} ViewModel registration",
                                           ct => InsertViewModelRegistrationAsync(context.Document, diagnostic.Location, funcType, viewModelFull!, viewModelShort,
                                                                                  injectedSignature, lifetime, hasAttribute, hasStub, ct), $"Add{lifetime}ViewModelRegistration");
            context.RegisterCodeFix(action, diagnostic);
        }
    }

    /// <summary>
    /// Inserts the ViewModel registration attribute and/or partial method stub into the container class.
    /// Only adds what's missing based on hasAttribute and hasStub flags.
    /// </summary>
    private static async Task<Document> InsertViewModelRegistrationAsync(Document document, Location diagLocation, string funcType, string viewModelFull, string viewModelShort,
                                                                         string injectedSignature, string lifetime, bool hasAttribute, bool hasStub,
                                                                         CancellationToken cancellationToken)
    {
        var root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
        var node = root.FindNode(diagLocation.SourceSpan);
        var containerDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (containerDecl is null)
            return document;

        var updatedContainer = containerDecl;

        // Add attribute if missing
        if (!hasAttribute)
        {
            var genericName = SyntaxFactory.GenericName(SyntaxFactory.Identifier(lifetime))
                                           .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.ParseTypeName(funcType))));
            var viewModelAttr = SyntaxFactory.Attribute(genericName,
                                                        SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(SyntaxFactory
                                                                                                    .NameEquals("Factory"), null,
                                                                                                SyntaxFactory.ParseExpression($"nameof(Create{viewModelShort})")))));

            var inferredViewName = InferViewNameFromViewModel(viewModelShort);
            var (viewList, viewAttr, viewIdx) = FindMatchingViewAttribute(updatedContainer, inferredViewName)
                                                ?? FindAnyViewAttribute(updatedContainer) ?? default((AttributeListSyntax list, AttributeSyntax attr, int index));

            if (viewAttr is not null && viewList is not null && viewIdx >= 0)
            {
                var newAttrs = viewList.Attributes.Insert(viewIdx + 1, viewModelAttr);
                var newList = viewList.WithAttributes(newAttrs);
                updatedContainer = updatedContainer.ReplaceNode(viewList, newList);
            }
            else
            {
                var newList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(viewModelAttr));
                updatedContainer = updatedContainer.WithAttributeLists(updatedContainer.AttributeLists.Add(newList));
            }
        }

        // Add partial method stub if missing
        if (!hasStub)
        {
            var partialMethodStub = GeneratePartialMethodStub(funcType, viewModelShort, injectedSignature);
            if (partialMethodStub is not null)
                updatedContainer = updatedContainer.AddMembers(partialMethodStub);
        }

        // Sorting (optional via MSBuild property; default true)
        var sort = ShouldSort(document.Project.AnalyzerOptions);
        if (sort)
            updatedContainer = SortAttributePairs(updatedContainer);

        var newRoot = root.ReplaceNode(containerDecl, updatedContainer);

        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    /// Reads an MSBuild analyzer config property to determine if attributes should be sorted.
    /// </summary>
    private static bool ShouldSort(AnalyzerOptions options)
    {
        if (options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.JabViewModelFactoryFix_SortAttributes", out var v))
            return !String.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

        return true; // default ON
    }

    #region Sorting

    /// <summary>
    /// Sorts attribute pairs so that each View registration is immediately followed by its ViewModel registration.
    /// </summary>
    private static ClassDeclarationSyntax SortAttributePairs(ClassDeclarationSyntax container)
    {
        // We'll rebuild attribute lists while preserving:
        // - relative order of non-pair attributes before/after the block of pairs
        // - inside the pairs block, each [Lifetime<View>] is immediately followed by the matching [Lifetime<ViewModel>(Factory=...) ]
        // Pairing uses ViewModel short name -> inferred view name by replacing "ViewModel" with "View".

        var allLists = container.AttributeLists;
        if (allLists.Count == 0)
            return container;

        // Flatten attributes with their parent list
        var items = allLists.SelectMany(al => al.Attributes.Select(a => (list: al, attr: a))).ToList();
        if (items.Count == 0)
            return container;

        // Collect views and ViewModels (by simple convention)
        var viewAttrs = new List<(AttributeListSyntax list, AttributeSyntax attr)>();
        var viewModelAttrs = new List<(AttributeListSyntax list, AttributeSyntax attr, string viewModelShort, string? inferredViewShort)>();
        var otherAttrs = new List<(AttributeListSyntax list, AttributeSyntax attr)>();

        foreach (var (list, attr) in items)
        {
            if (!IsLifetimeAttr(attr))
            {
                otherAttrs.Add((list, attr));
                continue;
            }

            if (HasFactory(attr))
            {
                // ViewModel registration (registered as Func<..., ViewModel>)
                var gen = GetGenericTypeName(attr);

                // Extract ViewModel from Func<..., ViewModel> - get the last type argument
                var viewModelShort = ExtractViewModelFromFunc(gen)?.Split('.').Last() ?? "";
                var viewShort = String.IsNullOrEmpty(viewModelShort) ? null : InferViewNameFromViewModel(viewModelShort);
                viewModelAttrs.Add((list, attr, viewModelShort, viewShort));
            }
            else
            {
                // Treat as a view (or some service) ? we'll only pair if a ViewModel references it
                viewAttrs.Add((list, attr));
            }
        }

        // Build map: viewShortName -> (list, attr)
        var viewMap = viewAttrs.Select(v =>
        {
            var t = GetGenericTypeName(v.attr);
            var shortName = t?.Split('.').Last();
            return (key: shortName, v.list, v.attr);
        }).Where(x => !String.IsNullOrEmpty(x.key)).GroupBy(x => x.key!).ToDictionary(g => g.Key, g => g.First());

        // Collect pair members that we will regroup (only if we can match ViewModel -> View)
        var pairMembers = new List<(AttributeListSyntax list, AttributeSyntax viewAttr, AttributeSyntax viewModelAttr)>();

        foreach (var viewModel in viewModelAttrs)
        {
            if (viewModel.inferredViewShort is not null && viewMap.TryGetValue(viewModel.inferredViewShort, out var v))
                pairMembers.Add((v.list, v.attr, viewModel.attr));
        }

        if (pairMembers.Count == 0)
            return container; // nothing to sort

        // Determine original order of first-seen pairs to keep stability
        var pairOrder = pairMembers.Select(pm => pm.viewAttr).Distinct().ToList();

        // Rebuild attribute lists:
        // 1) Gather all attributes that participate in pairs
        var pairAttrSet = new HashSet<AttributeSyntax>(pairMembers.SelectMany(pm => new[] { pm.viewAttr, pm.viewModelAttr }));

        // 2) Build new lists:
        var newLists = new List<AttributeListSyntax>();
        foreach (var list in allLists)
        {
            // Take attributes from this list that are NOT part of pairs (we'll add pairs later)
            var remaining = new SeparatedSyntaxList<AttributeSyntax>();
            foreach (var a in list.Attributes)
            {
                if (!pairAttrSet.Contains(a))
                    remaining = remaining.Add(a);
            }

            var rebuilt = list.WithAttributes(remaining);
            newLists.Add(rebuilt);
        }

        // 3) Insert pairs back in order, keeping their view+viewModel contiguous
        //    We'll place the pairs block at the position where the first pair's view originally lived,
        //    and preserve all other attributes before/after that block.
        // To keep it simple (and stable), for each pair we insert the ViewModel right after its view in that view's original list.

        // Build quick lookup: attribute list -> current list index (as newLists mirrors allLists)
        var listIndexMap = allLists.Select((al, i) => (al, i)).ToDictionary(x => x.al, x => x.i);

        foreach (var view in pairOrder)
        {
            var pm = pairMembers.First(x => x.viewAttr == view);
            var listIdx = listIndexMap[pm.list];
            var list = newLists[listIdx];

            // Find view position in the *current* list (it might have been removed if duplicate; ensure insert)
            var idx = list.Attributes.IndexOf(view);
            if (idx < 0)
            {
                // If the view was removed (shouldn't), append at end
                newLists[listIdx] = list.WithAttributes(list.Attributes.Add(view).Add(pm.viewModelAttr));
            }
            else
            {
                // Insert ViewModel right after the view
                var attrs = list.Attributes.Insert(idx + 1, pm.viewModelAttr);
                newLists[listIdx] = list.WithAttributes(attrs);
            }
        }

        return container.WithAttributeLists(new SyntaxList<AttributeListSyntax>(newLists));

        // Classify attributes
        static bool IsLifetimeAttr(AttributeSyntax a)
        {
            var n = a.Name.ToString();
            return n.Contains("Transient") || n.Contains("Scoped") || n.Contains("Singleton");
        }

        static bool HasFactory(AttributeSyntax a)
        {
            return a.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Factory") == true;
        }

        static string? GetGenericTypeName(AttributeSyntax a)
        {
            // Attribute of form Transient<TFoo>(...)
            if (a.Name is GenericNameSyntax g && g.TypeArgumentList.Arguments.Count == 1)
                return g.TypeArgumentList.Arguments[0].ToString();

            // Or Transient(typeof(TFoo)) ? not typical with Jab; ignore
            return null;
        }

        // Extracts the ViewModel type from a Func type string like "System.Func<System.Guid, Namespace.ViewModel>".
        static string? ExtractViewModelFromFunc(string? funcTypeName)
        {
            if (String.IsNullOrWhiteSpace(funcTypeName) || funcTypeName is null)
                return null;

            // Check if it starts with Func< or System.Func<
            const string funcPrefix = "Func<";
            const string systemFuncPrefix = "System.Func<";
            int startIdx;

            if (funcTypeName.StartsWith(systemFuncPrefix))
                startIdx = systemFuncPrefix.Length;
            else if (funcTypeName.StartsWith(funcPrefix))
                startIdx = funcPrefix.Length;
            else
                return funcTypeName; // Not a Func type, return as-is

            if (!funcTypeName.EndsWith(">"))
                return funcTypeName;

            // Extract the content between Func< and >
            var content = funcTypeName.Substring(startIdx, funcTypeName.Length - startIdx - 1);

            // Find the last comma at depth 0 to get the last type argument
            var depth = 0;
            var lastComma = -1;
            for (var i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (c == '<')
                    depth++;
                else if (c == '>')
                    depth--;
                else if (c == ',' && depth == 0)
                    lastComma = i;
            }

            // Return the last type argument (ViewModel)
            return lastComma >= 0 ? content.Substring(lastComma + 1).Trim() : content.Trim();
        }
    }

    #endregion

    #region Partial Method Stub Generation

    /// <summary>
    /// Generates a partial method stub declaration for the ViewModel factory.
    /// Uses the pre-computed injected signature from the analyzer.
    /// </summary>
    private static MethodDeclarationSyntax? GeneratePartialMethodStub(string funcType, string viewModelShort, string injectedSignature)
    {
        // Parse the injected signature into parameters
        var parameters = new List<ParameterSyntax>();
        if (!String.IsNullOrEmpty(injectedSignature))
        {
            // Split "Type1 d0, Type2 d1" into individual parameter declarations
            var paramParts = injectedSignature.Split(',');
            foreach (var part in paramParts)
            {
                var trimmed = part.Trim();
                if (String.IsNullOrEmpty(trimmed))
                    continue;

                // Find the last space to separate type from name
                var lastSpace = trimmed.LastIndexOf(' ');
                if (lastSpace <= 0)
                    continue;

                var typeName = trimmed.Substring(0, lastSpace);
                var paramName = trimmed.Substring(lastSpace + 1);

                parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(typeName)));
            }
        }

        var returnType = SyntaxFactory.ParseTypeName(funcType);

        var method = SyntaxFactory.MethodDeclaration(returnType, $"Create{viewModelShort}")
                                  .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                                                                         SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                                  .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
                                  .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));

        return method;
    }

    #endregion

    #region Matching

    private static (AttributeListSyntax list, AttributeSyntax attr, int index)? FindMatchingViewAttribute(ClassDeclarationSyntax container, string? inferredViewShort)
    {
        if (String.IsNullOrWhiteSpace(inferredViewShort))
            return null;

        foreach (var list in container.AttributeLists)
        {
            for (var ai = 0; ai < list.Attributes.Count; ai++)
            {
                var a = list.Attributes[ai];
                if (!IsLifetimeAttr(a) || HasFactory(a))
                    continue;

                var tn = GetGenericTypeName(a);
                var shortName = tn?.Split('.').Last();
                if (shortName == inferredViewShort)
                    return (list, a, ai);
            }
        }

        return null;

        static bool IsLifetimeAttr(AttributeSyntax a)
        {
            var n = a.Name.ToString();
            return n.Contains("Transient") || n.Contains("Scoped") || n.Contains("Singleton");
        }

        static bool HasFactory(AttributeSyntax a)
        {
            return a.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Factory") == true;
        }

        static string? GetGenericTypeName(AttributeSyntax a)
        {
            if (a.Name is GenericNameSyntax g && g.TypeArgumentList.Arguments.Count == 1)
                return g.TypeArgumentList.Arguments[0].ToString();
            return null;
        }
    }

    private static (AttributeListSyntax list, AttributeSyntax attr, int index)? FindAnyViewAttribute(ClassDeclarationSyntax container)
    {
        foreach (var list in container.AttributeLists)
        {
            for (var ai = 0; ai < list.Attributes.Count; ai++)
            {
                var a = list.Attributes[ai];
                var n = a.Name.ToString();
                var isLifetime = n.Contains("Transient") || n.Contains("Scoped") || n.Contains("Singleton");
                var hasFactory = a.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Factory") == true;

                if (isLifetime && !hasFactory)
                    return (list, a, ai);
            }
        }

        return null;
    }

    private static string? InferViewNameFromViewModel(string viewModelShort)
    {
        // DetailViewModel -> DetailView (simple convention)
        const string suf = "ViewModel";
        if (viewModelShort.EndsWith(suf))
            return viewModelShort.Substring(0, viewModelShort.Length - suf.Length) + "View";

        return null;
    }

    #endregion
}
