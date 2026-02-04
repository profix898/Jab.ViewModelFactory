Jab.ViewModelFactory
==========
[![Nuget](https://img.shields.io/nuget/v/Jab.ViewModelFactory?style=flat-square&logo=nuget&color=blue)](https://www.nuget.org/packages/Jab.ViewModelFactory)

⚡️ Source generator + analyzers + code fixes that make **View-first MVVM** easy and reflection-free with [**Jab**](https://github.com/pakrym/jab) compile-time DI generator.

---

## Motivation

I love the **Jab compile-time DI generator**. I also like a **View-first** approach (instead of ViewModel-first) in MVVM. For me, it feels natural to navigate between views/pages (the view model is just the code-behind logic supporting the UI).

In many UI frameworks, the common pattern is to **map a ViewModel to its View at runtime** using reflection. That is simple and convenient, but it goes directly against the purpose of **Jab**: *eliminating runtime reflection and moving wiring to compile-time*.

This project supplements **Jab** by enabling **View-first MVVM with auto-registered viewmodels**:

- Views declare the ViewModel they need via a `Func<..., TVm>` constructor parameter.
- The **source generator** emits strongly-typed factory methods into your Jab container (or modules).
- The **analyzer/code fix** automatically adds the matching Jab registration for the ViewModel.
- The end result: **reflection-free View-first MVVM**, checked at build time, supported in Avalonia, WPF, WinUI, MAUI, or any XAML UI stack.

**Jab.ViewModelFactory** is about close-gap wiring between view and view model - but it's just as much about developer experience: making sure you don’t forget to register the view and its view model in the first place.

*See the [Concept](./Concept.md) document for more details.*

---

## Features

- 🔍 **Source Generator**  
  Emits factory methods for every `Func<..., TVm>` parameter in view constructors. The generator picks the correct ViewModel constructor, supports injected services, and respects `[InjectionConstructor]` when multiple matches exist.

- 🛠️ **Analyzer + Code Fixes**  
  Detects when a ViewModel is missing from the container, when constructors are ambiguous, or when attributes are out of sync. Offers code fixes to:
  - Add missing `[Transient<TViewModel>(Factory = ...)]` registrations.
  - Mark the correct ViewModel constructor with `[InjectionConstructor]`.
  - Keep `[Transient<View>]` and `[Transient<ViewModel>]` attributes paired, with optional sorting.

- ⚖️ **Container/Module Support**  
  Factories and registrations can live in either `[ServiceProvider]` containers or `[ServiceProviderModule]` modules. Both are supported equally.

- 💯 **Framework-agnostic**  
  Works with Avalonia, WPF, WinUI, MAUI, or any UI framework. The only requirement: your view constructor has a `Func<..., TVm>` parameter.

---

## Why this matters

Traditionally, **View-first MVVM** is hard to wire up without reflection:

- Avalonia’s default **ViewLocator** for example reflects on a ViewModel type name, finds the corresponding View class, and instantiates it at runtime.
- That is simple, but it defeats the purpose of Jab’s compile-time DI. Reflection-based activation is slower, opaque, and harder to validate at build time.

With **Jab.ViewModelFactory**:

- ViewModels are constructed by Jab at compile time - **no reflection**.
- Views declare dependencies directly in their constructor (`Func<..., TVm>`).
- Everything is validated and wired up at **build time**, so you catch mistakes before shipping.

This combines the best of both worlds: the natural **View-first pattern** and Jab’s **reflection-free DI**.

---

## Quickstart

1. **Install the package**:

```sh
   dotnet add package Jab.ViewModelFactory
````

2. **Create your container**:

```csharp
   using Jab;

   [ServiceProvider]
   [Transient<MainView>]
   public partial class AppContainer { }
```

3. **Write your View** with a factory parameter:

```csharp
   public partial class MainView : UserControl
   {
       public MainView(Func<MainViewModel> vmFactory)
       {
           InitializeComponent();
           DataContext = vmFactory();
       }
   }
```

4. **Write your ViewModel**:

```csharp
   public class MainViewModel
   {
       public MainViewModel(ApiClient api, ILogger<MainViewModel> log) { }
   }
```

5. **Let Jab.ViewModelFactory do its work**:

   * Generator emits a factory in your container:

  ```csharp
     partial class AppContainer
     {
         private MainViewModel CreateMainViewModel(ApiClient d0, ILogger<MainViewModel> d1)
             => new MainViewModel(d0, d1);
     }
  ```

   * Analyzer warns if `MainViewModel` isn’t registered.

   * Code fix adds:

  ```csharp
     [Transient<MainView>]
     [Transient<MainViewModel>(Factory = nameof(CreateMainViewModel))]
  ```

---

## Configuration

### Attribute Sorting

By default, the code fix keeps view + viewmodel registrations **paired together** and may reorder other attributes around them to keep a clean layout.

You can configure this in **two ways**:

* **EditorConfig**:

  ```ini
  [*.cs]
  build_property.Jab.ViewModelFactoryFix_SortAttributes = false
  ```

* **MSBuild property** (in `.csproj`):

  ```xml
  <PropertyGroup>
    <Jab.ViewModelFactoryFix_SortAttributes>false</Jab.ViewModelFactoryFix_SortAttributes>
  </PropertyGroup>
  ```

When disabled, the VM registration is inserted directly below the view attribute without reordering other attributes.

---

## Diagnostics

The analyzer emits diagnostics with clear guidance:

* **JVMF001** — *View Not Registered*

  > A view with a `Func<..., TViewModel>` constructor parameter is not registered in any `[ServiceProvider]` or `[ServiceProviderModule]`.
  > **Fix:** Register the view (for example `[Transient<TView>]`) in a container/module.

* **JVMF002** — *Missing ViewModel Registration*

  > The view is registered, but the corresponding ViewModel factory is not registered with a `Factory` argument.
  > **Fix:** Use the provided code fix to add `[Transient<TViewModel>(Factory = nameof(Create...))]`.

* **JVMF003** — *No Matching Constructor*

  > No public ViewModel constructor matches the runtime parameter prefix from the `Func<...>` signature.
  > **Fix:** Adjust the ViewModel constructor signature (or the `Func<...>` parameter types) so they match.

* **JVMF004** — *Ambiguous Constructors*

  > Multiple ViewModel constructors match the runtime parameter prefix.
  > **Fix:** Mark the intended constructor with `[InjectionConstructor]`.

---

## Advanced Examples

### Parameterized ViewModel

```csharp
public class DetailView : UserControl
{
    public DetailView(Func<Guid, DetailViewModel> vmFactory)
    {
        DataContext = vmFactory(Guid.NewGuid());
    }
}

public class DetailViewModel
{
    // View provides Guid, other parameters are injected via DI
    public DetailViewModel(Guid id, ApiClient api, ILogger<DetailViewModel> log) { }
}
```

Generated factory:

```csharp
partial class AppContainer
{
    private DetailViewModel CreateDetailViewModel(Guid a0, ApiClient d0, ILogger<DetailViewModel> d1)
        => new DetailViewModel(a0, d0, d1);
}
```

Analyzer/code fix adds:

```csharp
[Transient<DetailView>]
[Transient<DetailViewModel>(Factory = nameof(CreateDetailViewModel))]
```

---

### Ambiguous Constructors

```csharp
public class SettingsViewModel
{
    public SettingsViewModel(ILogger<SettingsViewModel> log) { }
    public SettingsViewModel(ILogger<SettingsViewModel> log, IUserPreferences prefs) { }
}
```

Diagnostic `JVMF002` is raised.
Fix suggests adding:

```csharp
[InjectionConstructor]
public SettingsViewModel(ILogger<SettingsViewModel> log) { }
```

---

### Multiple Containers and Modules

Factories and registrations work with **both** `[ServiceProvider]` and `[ServiceProviderModule]`:

```csharp
[ServiceProviderModule]
[Transient<LoginView>]
public partial class UiModule { }

[ServiceProvider]
[Import(typeof(UiModule))]
public partial class AppContainer { }
```

The generator emits factories into both `UiModule` and `AppContainer`, and the code fix ensures the ViewModel registration is added next to the View.

---

## Avalonia: ViewLocator vs Jab.ViewModelFactory

**Avalonia’s default ViewLocator**:

* Looks up a `UserControl` class based on the `DataContext` type name.
* Instantiates it at runtime using reflection.
* This is simple, but slow, opaque, and impossible to validate at build time.

**With Jab.ViewModelFactory**:

* ViewModels are constructed by Jab at compile time — no reflection.
* Views declare dependencies directly (`Func<..., TVm>`).
* Everything is checked and wired up at build time.

Result: **faster startup, compile-time safety, and no reflection**.

---

## Status

🚧 Early preview. API and code-fix UX may evolve. Feedback and contributions are welcome.

---

## License
MIT (see LICENSE.txt).
