// Free-AI-SSD app icon renderer.
//
// Draws the canonical Free-AI-SSD app icon at an arbitrary size with Core
// Graphics so the output stays crisp at every iconset bucket (16..1024).
// No external assets, no SVG rasterizer, no Pillow -- pure Swift + CG.
//
// Usage:
//   swift IconRenderer.swift <size> <output.png>
//
// Run via build-icons.sh which produces the .icns + .ico bundles.

import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers
import AppKit

guard CommandLine.arguments.count == 3,
      let sizePx = Int(CommandLine.arguments[1]),
      sizePx > 0 else {
    FileHandle.standardError.write(Data("usage: IconRenderer <size> <output.png>\n".utf8))
    exit(2)
}
let outputPath = CommandLine.arguments[2]
let s = CGFloat(sizePx)

func rgb(_ r: Double, _ g: Double, _ b: Double, _ a: Double = 1) -> CGColor {
    CGColor(red: r, green: g, blue: b, alpha: a)
}

let colorSpace = CGColorSpaceCreateDeviceRGB()
guard let ctx = CGContext(
    data: nil,
    width: sizePx,
    height: sizePx,
    bitsPerComponent: 8,
    bytesPerRow: 0,
    space: colorSpace,
    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
) else {
    FileHandle.standardError.write(Data("failed to create CGContext\n".utf8))
    exit(1)
}

ctx.setShouldAntialias(true)
ctx.interpolationQuality = .high

// Big Sur app-icon tile: 1024 canvas, 824 tile, ~100px padding all sides.
let pad = s * 0.0977
let tile = CGRect(x: pad, y: pad, width: s - 2 * pad, height: s - 2 * pad)
let cornerRadius = tile.width * 0.2237  // squircle approximation Apple uses for app icons.

let tilePath = CGPath(
    roundedRect: tile,
    cornerWidth: cornerRadius,
    cornerHeight: cornerRadius,
    transform: nil
)

// ---- background ---------------------------------------------------------
ctx.saveGState()
ctx.addPath(tilePath)
ctx.clip()

// Diagonal gradient: deep indigo (bottom-left) -> electric violet (mid) -> hot magenta (top-right).
let bgColors = [
    rgb(0.039, 0.047, 0.18),   // #0A0C2E deep indigo
    rgb(0.353, 0.122, 0.851),  // #5A1FD9 electric violet
    rgb(1.0,   0.176, 0.494),  // #FF2D7E hot magenta
] as CFArray
let bgGradient = CGGradient(
    colorsSpace: colorSpace,
    colors: bgColors,
    locations: [0.0, 0.55, 1.0]
)!
ctx.drawLinearGradient(
    bgGradient,
    start: CGPoint(x: tile.minX, y: tile.minY),
    end:   CGPoint(x: tile.maxX, y: tile.maxY),
    options: []
)

// Cyan halo upper-right: gives the icon dimensional shine without a heavy gloss line.
let highlight = CGGradient(
    colorsSpace: colorSpace,
    colors: [
        rgb(0.302, 0.937, 1.0, 0.45),
        rgb(0.302, 0.937, 1.0, 0.0),
    ] as CFArray,
    locations: [0.0, 1.0]
)!
let hlCenter = CGPoint(x: tile.maxX - tile.width * 0.18, y: tile.maxY - tile.height * 0.18)
ctx.drawRadialGradient(
    highlight,
    startCenter: hlCenter, startRadius: 0,
    endCenter:   hlCenter, endRadius: tile.width * 0.55,
    options: []
)

// Subtle deep-violet vignette bottom-left to balance the cyan corner.
let vignette = CGGradient(
    colorsSpace: colorSpace,
    colors: [
        rgb(0.043, 0.020, 0.114, 0.55),
        rgb(0.043, 0.020, 0.114, 0.0),
    ] as CFArray,
    locations: [0.0, 1.0]
)!
let vCenter = CGPoint(x: tile.minX + tile.width * 0.15, y: tile.minY + tile.height * 0.15)
ctx.drawRadialGradient(
    vignette,
    startCenter: vCenter, startRadius: 0,
    endCenter:   vCenter, endRadius: tile.width * 0.6,
    options: []
)
ctx.restoreGState()

// ---- hexagonal chip -----------------------------------------------------
let cx = s / 2
let cy = s / 2
let hexR = tile.width * 0.34

// Pointy-top hexagon (flat sides on left/right) -- reads as a chip silhouette.
func hexVertices(center: CGPoint, radius: CGFloat) -> [CGPoint] {
    (0..<6).map { i in
        let angle = (CGFloat(i) * 60.0 - 90.0) * .pi / 180.0
        return CGPoint(
            x: center.x + radius * cos(angle),
            y: center.y + radius * sin(angle)
        )
    }
}
func hexPath(center: CGPoint, radius: CGFloat, cornerRadius: CGFloat) -> CGPath {
    let verts = hexVertices(center: center, radius: radius)
    let path = CGMutablePath()
    func midpoint(_ a: CGPoint, _ b: CGPoint) -> CGPoint {
        CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2)
    }
    path.move(to: midpoint(verts[5], verts[0]))
    for i in 0..<6 {
        let p = verts[i]
        let n = verts[(i + 1) % 6]
        path.addArc(tangent1End: p, tangent2End: n, radius: cornerRadius)
    }
    path.closeSubpath()
    return path
}

let chipCenter = CGPoint(x: cx, y: cy)
let chipPath = hexPath(center: chipCenter, radius: hexR, cornerRadius: hexR * 0.16)
let chipVerts = hexVertices(center: chipCenter, radius: hexR)

// Outer glow stroke -- lays down a wide soft cyan halo behind the chip.
ctx.saveGState()
let glowStroke = max(s * 0.018, 1.5)
ctx.setShadow(
    offset: .zero,
    blur: s * 0.05,
    color: rgb(0.302, 0.937, 1.0, 0.9)
)
ctx.addPath(chipPath)
ctx.setStrokeColor(rgb(0.722, 0.949, 1.0, 0.95))
ctx.setLineWidth(glowStroke)
ctx.strokePath()
ctx.restoreGState()

// Chip body fill -- darken the interior so the neural nodes pop.
ctx.saveGState()
ctx.addPath(chipPath)
ctx.clip()
let chipFill = CGGradient(
    colorsSpace: colorSpace,
    colors: [
        rgb(0.020, 0.031, 0.086, 0.85),  // top -- nearly opaque dark navy
        rgb(0.090, 0.043, 0.227, 0.55),  // bottom -- semi-transparent violet
    ] as CFArray,
    locations: [0.0, 1.0]
)!
ctx.drawLinearGradient(
    chipFill,
    start: CGPoint(x: chipCenter.x, y: chipCenter.y + hexR),
    end:   CGPoint(x: chipCenter.x, y: chipCenter.y - hexR),
    options: []
)
ctx.restoreGState()

// Crisp inner stroke on the chip outline so the silhouette stays sharp at small sizes.
ctx.addPath(chipPath)
ctx.setStrokeColor(rgb(0.85, 0.97, 1.0, 1.0))
ctx.setLineWidth(max(s * 0.008, 1.0))
ctx.strokePath()

// ---- neural spokes ------------------------------------------------------
// Lines from the central core to each of the 6 hex vertices.
ctx.saveGState()
ctx.setLineWidth(max(s * 0.006, 1.0))
ctx.setStrokeColor(rgb(0.85, 0.95, 1.0, 0.55))
for v in chipVerts {
    let dx = v.x - chipCenter.x
    let dy = v.y - chipCenter.y
    let len = sqrt(dx * dx + dy * dy)
    // Pull the line endpoints in slightly so the spoke doesn't visually pierce the chip stroke
    // or kiss the core directly (leaves room for the node/core dots to sit on top).
    let inner = CGPoint(
        x: chipCenter.x + dx / len * (hexR * 0.18),
        y: chipCenter.y + dy / len * (hexR * 0.18)
    )
    let outer = CGPoint(
        x: chipCenter.x + dx / len * (hexR * 0.78),
        y: chipCenter.y + dy / len * (hexR * 0.78)
    )
    ctx.move(to: inner)
    ctx.addLine(to: outer)
}
ctx.strokePath()
ctx.restoreGState()

// ---- nodes --------------------------------------------------------------
func drawNode(at p: CGPoint, color: CGColor, radius r: CGFloat, glowBlur: CGFloat) {
    ctx.saveGState()
    ctx.setShadow(offset: .zero, blur: glowBlur, color: color)
    ctx.setFillColor(color)
    ctx.addEllipse(in: CGRect(x: p.x - r, y: p.y - r, width: r * 2, height: r * 2))
    ctx.fillPath()
    ctx.restoreGState()
}

let cyan    = rgb(0.302, 0.937, 1.0)   // #4DEFFF
let magenta = rgb(1.0,   0.176, 0.553) // #FF2D8D
let nodeR = max(s * 0.024, 1.5)

// Vertex nodes: alternate cyan/magenta around the hex (2 cyan + 1 mag triangles).
for (i, v) in chipVerts.enumerated() {
    // Pull node toward the chip body slightly so it sits visibly inside the outline at small sizes.
    let dx = chipCenter.x - v.x
    let dy = chipCenter.y - v.y
    let len = sqrt(dx * dx + dy * dy)
    let pulled = CGPoint(
        x: v.x + dx / len * (hexR * 0.16),
        y: v.y + dy / len * (hexR * 0.16)
    )
    let color = (i % 2 == 0) ? cyan : magenta
    drawNode(at: pulled, color: color, radius: nodeR, glowBlur: s * 0.025)
}

// Central core -- larger, brighter, double-stacked for that "lit from inside" feel.
let coreR = max(s * 0.052, 3.0)
drawNode(at: chipCenter, color: rgb(0.7, 0.95, 1.0, 0.9), radius: coreR * 1.35, glowBlur: s * 0.08)
drawNode(at: chipCenter, color: rgb(1.0, 1.0, 1.0, 1.0),  radius: coreR,        glowBlur: s * 0.04)

// ---- emit PNG -----------------------------------------------------------
guard let cgImage = ctx.makeImage() else {
    FileHandle.standardError.write(Data("CGContext.makeImage failed\n".utf8))
    exit(1)
}

let url = URL(fileURLWithPath: outputPath) as CFURL
guard let dest = CGImageDestinationCreateWithURL(
    url,
    UTType.png.identifier as CFString,
    1,
    nil
) else {
    FileHandle.standardError.write(Data("CGImageDestinationCreateWithURL failed\n".utf8))
    exit(1)
}
CGImageDestinationAddImage(dest, cgImage, nil)
guard CGImageDestinationFinalize(dest) else {
    FileHandle.standardError.write(Data("CGImageDestinationFinalize failed\n".utf8))
    exit(1)
}
