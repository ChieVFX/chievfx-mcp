---
name: hotreload-running
description: Prefer Unity Hot Reload-compatible edits when user says "hotreload running", Hot Reload is active, or avoiding Unity Editor/play session disruption matters.
---

# Hot Reload Running

When user prompt contains `hotreload running`, assume Unity Hot Reload is active and prefer edits that apply without disrupting running Editor or play session.

Prefer:
- Edit existing method/property bodies.
- Add or edit local functions inside existing methods.
- Add or edit `using` directives only when needed.
- Keep changes small and localized.

Avoid if possible:
- Adding, modifying, or deleting `.asmdef` files.
- Modifying define symbols, `.csproj`, or `.sln` files.
- Adding new C# files, classes, structs, or enums.
- Adding/removing attributes.
- Adding/removing/changing declared constructors, especially structs or generic classes.
- Adding/removing method keywords: `partial`, `abstract`, `virtual`, `override`, `extern`.
- Changing public method signatures, return types, generic parameters, constraints, or `ref`/`out`/`in` modifiers unless needed.
- Adding, removing, renaming, or changing fields unless needed; field type/static/const changes can reset values, and Inspector edit behavior may require full Unity recompile.
- Editing multiple fields at once.
- Relying on changes to ongoing async methods or coroutines; changes apply to new invocations only.

If avoided edit is necessary, say so before editing and explain likely Hot Reload impact.
