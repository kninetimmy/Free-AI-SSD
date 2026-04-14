// Project-wide usings for the companion app.
//
// The companion csproj uses `Microsoft.NET.Sdk.WindowsDesktop` with both
// `UseWPF=true` and `UseWindowsForms=true`. Two quirks of that combo need to
// be papered over centrally:
//
//  1. The WinForms SDK imports `System.Windows.Forms` into implicit usings,
//     which collides with types of the same name in `System.Windows` (WPF) —
//     most notably `MessageBox` and `Application`. We alias `MessageBox` to
//     the WPF variant because the companion's UI dialogs are WPF windows;
//     `Application` we fully-qualify at the only call site that extends it.
//
//  2. With this legacy SDK the standard `System.IO` / `System.Net.Http`
//     implicit usings are not applied to source files, so `Path`, `Directory`,
//     `File`, `Stream`, `HttpClient`, `HttpRequestMessage`, etc. fail to
//     resolve unless imported explicitly. Adding them as global usings here
//     keeps individual source files clean.
global using System.IO;
global using System.Net.Http;
global using MessageBox = System.Windows.MessageBox;
