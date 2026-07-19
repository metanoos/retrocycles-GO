using System.Runtime.CompilerServices;

// Exposes internal test-only helpers (GameEvents.ClearSubscribersForTests) to the Lightfield
// and Gameplay test assemblies. Round-1 review addition: tests that exercise the static bus
// need to clear subscriptions between runs to keep isolation; production code MUST NOT call
// ClearSubscribersForTests (it would silently detach every legitimate subscriber).
[assembly: InternalsVisibleTo("LightRunners.Tests.Gameplay")]
[assembly: InternalsVisibleTo("LightRunners.Tests.Lightfield")]
[assembly: InternalsVisibleTo("LightRunners.Tests.Trail")]
[assembly: InternalsVisibleTo("LightRunners.Tests.Afterglow")]
[assembly: InternalsVisibleTo("LightRunners.Tests.Backend")]
[assembly: InternalsVisibleTo("LightRunners.Tests.Multiplayer")]