global using System;
global using System.Collections.Generic;
global using System.Diagnostics;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Text;
global using System.Threading;
global using System.Windows;
global using System.Windows.Media;
global using System.Windows.Media.Imaging;
global using System.Windows.Threading;
global using MosaikCapture.Engine.Core;
global using MosaikCapture.Engine.Recording;
global using MosaikCapture.Engine.Screenshot;
global using MosaikCapture.Engine.Tests.Infrastructure;
global using Xunit;

// Tests share process-wide state (environment variable for the settings file, the WPF dispatcher,
// static engine state such as Rec and Palette), so they run one after another.
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
