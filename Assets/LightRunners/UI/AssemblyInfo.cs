using System.Runtime.CompilerServices;

// Exposes internal test hooks (TacticalRadar.TestSimulateStopped etc.) to the gameplay test
// assembly. The expand-on-stop logic is verified in isolation without a Canvas.
[assembly: InternalsVisibleTo("LightRunners.Tests.Gameplay")]
