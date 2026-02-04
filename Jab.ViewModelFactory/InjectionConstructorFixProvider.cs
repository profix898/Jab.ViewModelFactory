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

namespace Jab.ViewModelFactory;

/// <summary>
/// Provides a code fix for the JVMF004 diagnostic produced by the analyzer.
/// When applied the fix marks a constructor with the <see cref="InjectionConstructorAttribute" />
/// and adds a using directive for the attribute's namespace if needed.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(InjectionConstructorFixProvider))]
[Shared]
public sealed class InjectionConstructorFixProvider : CodeFixProvider
{
    /// <summary>
    /// The diagnostic IDs this provider can fix.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => [Diagnostics.JVMF004];

    /// <summary>
    /// Returns the batch fixer for FixAll operations.
    /// </summary>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers a code fix that marks the constructor reported by the diagnostic with the attribute.
    /// The JVMF004 diagnostic is reported on constructor declaration locations; this
    /// code fix locates the containing class and updates the selected constructor.
    /// </summary>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var document = context.Document;

        // Get the syntax root for the document. Bail out if it's not available.
        var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;

        var diagnostic = context.Diagnostics.First();
        var node = root.FindNode(diagnostic.Location.SourceSpan);

        // Find the containing class declaration for the reported diagnostic.
        // JVMF004 is attached to constructor declarations; we navigate to the class so we can pick
        // a constructor to mark with [InjectionConstructor].
        var classDecl = node.FirstAncestorOrSelf<ClassDeclarationSyntax>();
        if (classDecl is null)
            return;

        var ctorDecl = node.FirstAncestorOrSelf<ConstructorDeclarationSyntax>();
        if (ctorDecl is null)
            return;

        context.RegisterCodeFix(CodeAction.Create("Mark constructor with [InjectionConstructor]", ct => AddAttributeAsync(document, classDecl, ctorDecl, ct), "MarkCtor"),
                                diagnostic);
    }

    /// <summary>
    /// Adds an [InjectionConstructor] attribute to the selected constructor in the class
    /// and adds a using directive for the attribute namespace if missing.
    /// This modifies the constructor declaration that the JVMF004 diagnostic was reported on.
    /// </summary>
    private static async Task<Document> AddAttributeAsync(Document document, ClassDeclarationSyntax cls, ConstructorDeclarationSyntax ctor, CancellationToken cancellationToken)
    {
        // Retrieve the document root again to perform edits.
        var root = (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;

        // Re-find the class and constructor in the current root (the original nodes may be stale)
        var currentClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == cls.Identifier.Text && c.SpanStart == cls.SpanStart);
        if (currentClass is null)
            return document;

        var currentCtor = currentClass.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault(c => c.SpanStart == ctor.SpanStart);
        if (currentCtor is null)
            return document;

        if (currentCtor.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() is "InjectionConstructor" or "InjectionConstructorAttribute"))
            return document;

        // Create the attribute and attach it to the constructor.
        var attr = SyntaxFactory.Attribute(SyntaxFactory.IdentifierName("InjectionConstructor"));
        var attrList = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(attr)).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

        var newCtor = currentCtor.AddAttributeLists(attrList);

        var newRoot = root.ReplaceNode(currentCtor, newCtor);

        // Ensure "using Jab.ViewModelFactory;" is present
        if (newRoot is CompilationUnitSyntax cu)
        {
            var hasUsing = cu.Usings.Any(u => u.Name?.ToString() == "Jab.ViewModelFactory");
            if (!hasUsing)
            {
                // Add the using directive for the attribute's namespace.
                // This ensures the added [InjectionConstructor] attribute resolves.
                cu = cu.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Jab.ViewModelFactory")));
                newRoot = cu;
            }
        }

        return document.WithSyntaxRoot(newRoot);
    }
}
