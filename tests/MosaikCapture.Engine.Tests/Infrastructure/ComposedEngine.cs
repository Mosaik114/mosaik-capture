[assembly: AssemblyFixture(typeof(ComposedEngine))]

namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>
/// Builds the engine's object graph once for the test run, as <see cref="CaptureBridge"/> does at startup, so
/// tests that drive the real windows (pixel tests, the self-test) find the facades ready. No window is shown.
/// </summary>
public sealed class ComposedEngine
{
    public ComposedEngine() => EngineComposition.Compose();
}
