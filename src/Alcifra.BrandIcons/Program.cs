using Avalonia;
using Avalonia.Headless;
using Alcifra.BrandIcons;

// Writes the brand's icons (ADR 0993): Alcifra.BrandIcons <output directory> <brand assets directory>.
// Headless, with Skia rather than the headless drawing stub, because these are real pixels. Silent on success; a
// failure throws, which fails the build that ran it.
if (args.Length != 2)
{
    throw new ArgumentException("Usage: Alcifra.BrandIcons <output directory> <brand assets directory>");
}

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

IconWriter.Write(args[0], args[1]);
