using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("FreeAiSsd.Tests")]
// Slim cross-platform OCR test project (task #92) that links the Tesseract
// test files so the real-Tesseract integration test runs on the macOS CI lane.
[assembly: InternalsVisibleTo("FreeAiSsd.Tests.Ocr")]

[assembly: InternalsVisibleTo("FreeAiSsd.Runner")]
[assembly: InternalsVisibleTo("FreeAiSsd.Companion")]
