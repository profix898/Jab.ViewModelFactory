using System;

namespace Jab.ViewModelFactory;

/// <summary>
/// Marks a constructor as the preferred injection target when multiple
/// constructors match the runtime parameter prefix for ViewModel creation.
/// </summary>
/// <remarks>
/// When a ViewModel has multiple public constructors that could match the
/// <c>Func&lt;T1, ..., TN, ViewModel&gt;</c> signature, use this attribute
/// to indicate which constructor should be used by the generated factory.
/// </remarks>
/// <example>
/// <code>
/// public class DetailViewModel
/// {
///     // This constructor will be selected for factory generation
///     [InjectionConstructor]
///     public DetailViewModel(Guid id, IApiService api) { }
///     
///     // This constructor won't be used
///     public DetailViewModel(Guid id) { }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Constructor)]
public sealed class InjectionConstructorAttribute : Attribute
{
}
