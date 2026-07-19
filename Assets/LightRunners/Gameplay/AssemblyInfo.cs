using System.Runtime.CompilerServices;

// Exposes internal test hooks (MatchManager.TestSetState etc.) to the gameplay test assembly.
// Tests are edit-mode-only and live in LightRunners.Tests.Gameplay; they need to drive the
// match FSM synchronously without going through Update / Time.deltaTime.
[assembly: InternalsVisibleTo("LightRunners.Tests.Gameplay")]
