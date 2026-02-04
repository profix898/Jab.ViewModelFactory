using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
/// Custom FixAllProvider that efficiently handles multiple JVMF002 diagnostics
/// by grouping them by document and applying all fixes in a single pass.
/// </summary>
internal sealed class ViewModelRegistrationFixAllProvider : FixAllProvider
{
    public static readonly ViewModelRegistrationFixAllProvider Instance = new();

    private ViewModelRegistrationFixAllProvider()
    {
    }

    public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
    {
        var diagnosticsToFix = new List<DocumentDiagnostics>();

        switch (fixAllContext.Scope)
        {
            case FixAllScope.Document:
                if (fixAllContext.Document is not null)
                {
                    var diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(fixAllContext.Document).ConfigureAwait(false);
                    diagnosticsToFix.Add(new DocumentDiagnostics(fixAllContext.Document, diagnostics));
                }
                break;

            case FixAllScope.Project:
                foreach (var document in fixAllContext.Project.Documents)
                {
                    var diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
                    if (diagnostics.Length > 0)
                        diagnosticsToFix.Add(new DocumentDiagnostics(document, diagnostics));
                }
                break;

            case FixAllScope.Solution:
                foreach (var project in fixAllContext.Solution.Projects)
                {
                    foreach (var document in project.Documents)
                    {
                        var diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(document).ConfigureAwait(false);
                        if (diagnostics.Length > 0)
                            diagnosticsToFix.Add(new DocumentDiagnostics(document, diagnostics));
                    }
                }
                break;

            default:
                return null;
        }

        if (diagnosticsToFix.Count == 0)
            return null;

        var title = "Add all ViewModel registrations";
        return CodeAction.Create(title, ct => FixAllAsync(fixAllContext.Solution, diagnosticsToFix, ct), title);
    }

    private static async Task<Solution> FixAllAsync(Solution solution, List<DocumentDiagnostics> diagnosticsByDocument, CancellationToken cancellationToken)
    {
        foreach (var docDiag in diagnosticsByDocument)
        {
            var document = docDiag.Document;
            var diagnostics = docDiag.Diagnostics;

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null)
                continue;

            // Group diagnostics by container class
            var diagnosticsByContainer = diagnostics.Select(d => new DiagnosticWithContainer(d, GetContainerClass(root, d))).Where(x => x.Container is not null)
                                                    .GroupBy(x => x.Container!.Identifier.Text).ToList();

            foreach (var group in diagnosticsByContainer)
            {
                // Find the current container in the potentially modified root
                var containerDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == group.Key);

                if (containerDecl is null)
                    continue;

                var updatedContainer = containerDecl;

                // Collect all fixes for this container
                var attributesToAdd = new List<AttributeToAdd>();
                var stubsToAdd = new List<MethodDeclarationSyntax>();

                foreach (var item in group)
                {
                    var props = item.Diagnostic.Properties;

                    if (!props.TryGetValue("viewModelShort", out var viewModelShort) || String.IsNullOrEmpty(viewModelShort))
                        continue;

                    var funcType = props.TryGetValue("funcType", out var ft) ? ft ?? "" : "";
                    var injectedSignature = props.TryGetValue("injectedSignature", out var sig) ? sig ?? "" : "";
                    var hasAttribute = props.TryGetValue("hasAttribute", out var ha) && ha == "True";
                    var hasStub = props.TryGetValue("hasStub", out var hs) && hs == "True";

                    // Add attribute if missing
                    if (!hasAttribute && !String.IsNullOrEmpty(funcType))
                    {
                        var attr = CreateAttribute("Transient", funcType, viewModelShort);
                        var inferredViewName = InferViewNameFromViewModel(viewModelShort);
                        attributesToAdd.Add(new AttributeToAdd(attr, inferredViewName));
                    }

                    // Add stub if missing
                    if (!hasStub)
                    {
                        var stub = CreatePartialMethodStub(funcType, viewModelShort, injectedSignature);
                        if (stub is not null)
                            stubsToAdd.Add(stub);
                    }
                }

                // Apply all attribute additions
                foreach (var attrToAdd in attributesToAdd)
                {
                    var matchResult = FindMatchingViewAttribute(updatedContainer, attrToAdd.AfterViewName);
                    if (matchResult.List is null)
                        matchResult = FindAnyViewAttribute(updatedContainer);

                    if (matchResult.Attr is not null && matchResult.List is not null && matchResult.Index >= 0)
                    {
                        var newAttrs = matchResult.List.Attributes.Insert(matchResult.Index + 1, attrToAdd.Attr);
                        var newList = matchResult.List.WithAttributes(newAttrs);
                        updatedContainer = updatedContainer.ReplaceNode(matchResult.List, newList);
                    }
                    else
                    {
                        var newList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attrToAdd.Attr));
                        updatedContainer = updatedContainer.WithAttributeLists(updatedContainer.AttributeLists.Add(newList));
                    }
                }

                // Apply all stub additions at once
                if (stubsToAdd.Count > 0)
                    updatedContainer = updatedContainer.AddMembers(stubsToAdd.ToArray());

                // Replace the container in the root
                root = root.ReplaceNode(containerDecl, updatedContainer);
            }

            solution = solution.WithDocumentSyntaxRoot(document.Id, root);
        }

        return solution;
    }

    private static ClassDeclarationSyntax? GetContainerClass(SyntaxNode root, Diagnostic diagnostic)
    {
        var node = root.FindNode(diagnostic.Location.SourceSpan);
        return node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
    }

    private static AttributeSyntax CreateAttribute(string lifetime, string funcType, string viewModelShort)
    {
        var genericName = SyntaxFactory.GenericName(SyntaxFactory.Identifier(lifetime))
                                       .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.ParseTypeName(funcType))));

        return SyntaxFactory.Attribute(genericName,
                                       SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(SyntaxFactory.NameEquals("Factory"),
                                                                                                                    null,
                                                                                                                    SyntaxFactory.ParseExpression($"nameof(Create{
                                                                                                                        viewModelShort})")))));
    }

    private static MethodDeclarationSyntax? CreatePartialMethodStub(string funcType, string viewModelShort, string injectedSignature)
    {
        if (String.IsNullOrEmpty(funcType))
            return null;

        var parameters = new List<ParameterSyntax>();
        if (!String.IsNullOrEmpty(injectedSignature))
        {
            var paramParts = injectedSignature.Split(',');
            foreach (var part in paramParts)
            {
                var trimmed = part.Trim();
                if (String.IsNullOrEmpty(trimmed))
                    continue;

                var lastSpace = trimmed.LastIndexOf(' ');
                if (lastSpace <= 0)
                    continue;

                var typeName = trimmed.Substring(0, lastSpace);
                var paramName = trimmed.Substring(lastSpace + 1);

                parameters.Add(SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(SyntaxFactory.ParseTypeName(typeName)));
            }
        }

        return SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(funcType), $"Create{viewModelShort}")
                            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.StaticKeyword),
                                                                   SyntaxFactory.Token(SyntaxKind.PartialKeyword)))
                            .WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)))
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    private static string? InferViewNameFromViewModel(string viewModelShort)
    {
        const string suf = "ViewModel";
        if (viewModelShort.EndsWith(suf))
            return viewModelShort.Substring(0, viewModelShort.Length - suf.Length) + "View";
        return null;
    }

    private static AttributeMatchResult FindMatchingViewAttribute(ClassDeclarationSyntax container, string? inferredViewShort)
    {
        if (String.IsNullOrWhiteSpace(inferredViewShort))
            return default(AttributeMatchResult);

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
                    return new AttributeMatchResult(list, a, ai);
            }
        }

        return default(AttributeMatchResult);
    }

    private static AttributeMatchResult FindAnyViewAttribute(ClassDeclarationSyntax container)
    {
        foreach (var list in container.AttributeLists)
        {
            for (var ai = 0; ai < list.Attributes.Count; ai++)
            {
                var a = list.Attributes[ai];
                if (IsLifetimeAttr(a) && !HasFactory(a))
                    return new AttributeMatchResult(list, a, ai);
            }
        }

        return default(AttributeMatchResult);
    }

    private static bool IsLifetimeAttr(AttributeSyntax a)
    {
        var n = a.Name.ToString();
        return n.Contains("Transient") || n.Contains("Scoped") || n.Contains("Singleton");
    }

    private static bool HasFactory(AttributeSyntax a)
    {
        return a.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Factory") == true;
    }

    private static string? GetGenericTypeName(AttributeSyntax a)
    {
        if (a.Name is GenericNameSyntax g && g.TypeArgumentList.Arguments.Count == 1)
            return g.TypeArgumentList.Arguments[0].ToString();
        return null;
    }

    #region Helper Types

    private sealed class DocumentDiagnostics
    {
        public DocumentDiagnostics(Document document, ImmutableArray<Diagnostic> diagnostics)
        {
            Document = document;
            Diagnostics = diagnostics;
        }

        public ImmutableArray<Diagnostic> Diagnostics { get; }

        public Document Document { get; }
    }

    private sealed class DiagnosticWithContainer
    {
        public DiagnosticWithContainer(Diagnostic diagnostic, ClassDeclarationSyntax? container)
        {
            Diagnostic = diagnostic;
            Container = container;
        }

        public ClassDeclarationSyntax? Container { get; }

        public Diagnostic Diagnostic { get; }
    }

    private sealed class AttributeToAdd
    {
        public AttributeToAdd(AttributeSyntax attr, string? afterViewName)
        {
            Attr = attr;
            AfterViewName = afterViewName;
        }

        public string? AfterViewName { get; }

        public AttributeSyntax Attr { get; }
    }

    private struct AttributeMatchResult
    {
        public AttributeMatchResult(AttributeListSyntax? list, AttributeSyntax? attr, int index)
        {
            List = list;
            Attr = attr;
            Index = index;
        }

        public AttributeListSyntax? List { get; }

        public AttributeSyntax? Attr { get; }

        public int Index { get; }
    }

    #endregion
}
