import SwiftUI

// MARK: - Brand color tokens
//
// Per the 2026-05-06 Mac UI design language decision (project_decisions.md):
// brand-tinted native styling — accent colors and status colors pulled from
// the WPF Colors.xaml palette, applied sparingly. MAC17 PrepApp leans even
// closer to pure native (Option A) because it owns destructive disk ops, so
// these tokens are used for status pills and inline indicators only —
// never to override system button chrome, focus rings, or backgrounds.
//
// If a future Mac Runner refresh wants brand tinting in earnest, these
// tokens are the conventions it builds on.

extension Color {
    // Cyan/Magenta/Purple accents, mirroring shared/UI/Theme/Colors.xaml.
    // Cyan is the canonical primary accent on the Windows side and the
    // hexagonal-chip glow color in the MAC10b shared icon — using it here
    // keeps the visual through-line.
    static let brandAccentCyan    = Color(red: 0.0,  green: 0.898, blue: 1.0)   // #00E5FF
    static let brandAccentMagenta = Color(red: 1.0,  green: 0.176, blue: 0.572) // #FF2D92
    static let brandAccentPurple  = Color(red: 0.541, green: 0.169, blue: 0.886) // #8A2BE2

    // Status semantics. Mirrors StatusSuccess/Warning/Danger/Info from the
    // WPF palette. Reserved for status pills and inline severity glyphs;
    // do NOT use these for system button chrome — destructive confirms
    // stay system red via NSAlert per the MAC17 design decision.
    static let brandStatusSuccess = Color(red: 0.298, green: 0.878, blue: 0.702) // #4CE0B3
    static let brandStatusWarning = Color(red: 1.0,   green: 0.706, blue: 0.329) // #FFB454
    static let brandStatusDanger  = Color(red: 1.0,   green: 0.302, blue: 0.427) // #FF4D6D
    static let brandStatusInfo    = Color(red: 0.357, green: 0.753, blue: 1.0)   // #5BC0FF
}
