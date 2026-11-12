global using System;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Text;
global using System.Threading;
global using System.Threading.Tasks;
global using System.Windows;
global using System.Windows.Media;
global using System.Windows.Media.Imaging;
global using System.Windows.Threading;
global using MosaikCapture.Engine.Core;
global using MosaikCapture.Engine.Recording;
global using MosaikCapture.Engine.Screenshot;
global using MosaikCapture.Engine.Tests.Infrastructure;
global using Xunit;
global using RECT = MosaikCapture.Engine.Core.NativeMethods.RECT;

// Parallel execution stays off on purpose. Many classes share process-wide state that cannot be made
// per-test cheaply: the environment variable for the settings file (CaptureSettings), the one WPF
// dispatcher thread that owns Application.Current, Palette.Force, the ScreenRecorder facade, the error
// log facade, Thread.CurrentCulture changes and the timing-sensitive pixel and ffmpeg tests. The pure
// classes would run in parallel, but xUnit only allows parallel collections when the assembly allows
// parallelism, and then every stateful class would need its own collection: not worth the risk for a
// suite that runs in about 15 seconds.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
