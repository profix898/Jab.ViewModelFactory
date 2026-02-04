using Microsoft.CodeAnalysis;

namespace Jab.ViewModelFactory;

/// <summary>
/// Diagnostic IDs and descriptors emitted by the Jab.ViewModelFactory analyzers.
/// </summary>
/// <remarks>
/// Diagnostic IDs follow the pattern JVMF### where:
/// <list type="bullet">
///   <item><description>JVMF001 - View not registered in any provider</description></item>
///   <item><description>JVMF002 - Missing ViewModel registration for a view</description></item>
///   <item><description>JVMF003 - No matching ViewModel constructor</description></item>
///   <item><description>JVMF004 - Ambiguous ViewModel constructors</description></item>
/// </list>
/// </remarks>
public static class Diagnostics
{
    // Category used for all diagnostics in this analyzer
    private const string Category = "JabViewModelFactory";

    /// <summary>View/ViewModel not registered in any container/module.</summary>
    public const string JVMF001 = nameof(JVMF001);

    /// <summary>ViewModel registration missing for a registered view.</summary>
    public const string JVMF002 = nameof(JVMF002);

    /// <summary>No ViewModel constructor matches the runtime parameter prefix.</summary>
    public const string JVMF003 = nameof(JVMF003);

    /// <summary>Multiple ViewModel constructors match the runtime parameter prefix.</summary>
    public const string JVMF004 = nameof(JVMF004);

    /// <summary>
    /// Reported when a view with a <c>Func&lt;..., ViewModel&gt;</c> constructor parameter
    /// isn't registered in any <c>[ServiceProvider]</c> or <c>[ServiceProviderModule]</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor NotRegisteredAnywhere = new(JVMF001, "View/ViewModel not registered in any Jab provider/module",
                                                                            "Register '{0}' (and its ViewModel) in a [ServiceProvider] or [ServiceProviderModule]", Category,
                                                                            DiagnosticSeverity.Error, true);

    /// <summary>
    /// Reported when a provider/module has a view registered but is missing
    /// the corresponding <c>Func&lt;..., ViewModel&gt;</c> registration with a Factory.
    /// </summary>
    public static readonly DiagnosticDescriptor MissingViewModelRegistration = new(JVMF002, "Missing Jab ViewModel registration",
                                                                                   "Add [Transient<Func<{0}>>(Factory = nameof(Create{1}))] below the view registration", Category,
                                                                                   DiagnosticSeverity.Error, true);

    /// <summary>
    /// Reported when a ViewModel has no public constructor whose first N parameters
    /// match the runtime types from the <c>Func&lt;T1, ..., TN, ViewModel&gt;</c> signature.
    /// </summary>
    public static readonly DiagnosticDescriptor NoMatchingCtor = new(JVMF003, "No matching ViewModel constructor",
                                                                     "No public constructor on '{0}' matches the runtime parameter prefix of length {1}", Category,
                                                                     DiagnosticSeverity.Error, true);

    /// <summary>
    /// Reported when multiple ViewModel constructors match the runtime prefix.
    /// Use <see cref="InjectionConstructorAttribute" /> to disambiguate.
    /// </summary>
    public static readonly DiagnosticDescriptor AmbiguousCtors = new(JVMF004, "Multiple matching ViewModel constructors",
                                                                     "Multiple constructors on '{0}' match the runtime parameter prefix. Mark one with [InjectionConstructor].",
                                                                     Category, DiagnosticSeverity.Error, true);
}
