import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// Renders the ClaudeWarp plugin icon: one keypad page, exactly as the device shows it.
//
// The icon deliberately shows what the device shows - a grid of tiles in the plugin's own state
// palette - rather than another Claude burst on coral, because ClaudeDesktop and ClaudeConsole
// already occupy that look in the same plugin list.
//
// The layout is the real one, not a decorative grid: the host takes the top-left key for Back,
// the bottom row is the three command keys, and the five tiles between them are sessions.

let S: CGFloat = 256

func rgb(_ hex: UInt32, _ a: CGFloat = 1) -> CGColor {
    CGColor(red: CGFloat((hex >> 16) & 0xFF) / 255,
            green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255,
            alpha: a)
}

let space = CGColorSpaceCreateDeviceRGB()
guard let ctx = CGContext(data: nil, width: Int(S), height: Int(S), bitsPerComponent: 8,
                          bytesPerRow: 0, space: space,
                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
    fatalError("no context")
}
ctx.setAllowsAntialiasing(true)
ctx.interpolationQuality = .high

// --- background squircle -------------------------------------------------------------------
let body = CGPath(roundedRect: CGRect(x: 0, y: 0, width: S, height: S),
                  cornerWidth: 57, cornerHeight: 57, transform: nil)
ctx.saveGState()
ctx.addPath(body)
ctx.clip()
let bg = CGGradient(colorsSpace: space,
                    colors: [rgb(0x2E333A), rgb(0x16181C)] as CFArray,
                    locations: [0, 1])!
ctx.drawLinearGradient(bg, start: CGPoint(x: 0, y: S), end: CGPoint(x: 0, y: 0), options: [])
ctx.restoreGState()

// A dark icon on a dark plugin list needs an edge to sit on.
ctx.saveGState()
ctx.addPath(CGPath(roundedRect: CGRect(x: 1, y: 1, width: S - 2, height: S - 2),
                   cornerWidth: 56, cornerHeight: 56, transform: nil))
ctx.setStrokeColor(rgb(0xFFFFFF, 0.13))
ctx.setLineWidth(2)
ctx.strokePath()
ctx.restoreGState()

// --- the nine keys -------------------------------------------------------------------------
// Session colours are brighter than the on-device palette: those values are darkened to carry
// white text, which an icon does not have to do.
let busy = rgb(0xD9775A)      // Claude coral
let done = rgb(0x4F9E61)
let attention = rgb(0xC4453C)
let idle = rgb(0x5E646C)

// The command row is pushed well below idle grey. On the device those two are close - both are
// grey - and what tells them apart is that a command key is mostly text. An icon has no text at
// this size, so the separation has to come from value instead.
let command = rgb(0x34383E)
let back = rgb(0x20242A)

enum Key {
    case back
    case session(CGColor)
    case command
}

let grid: [[Key]] = [
    // The host's own Back key, then two sessions.
    [.back, .session(done), .session(attention)],
    // The centre tile carries the burst, so it is the one that is working.
    [.session(idle), .session(busy), .session(done)],
    // esc / clear / compact.
    [.command, .command, .command],
]

let margin: CGFloat = 38
let gap: CGFloat = 13
let tile = (S - 2 * margin - 2 * gap) / 3

func rect(row: Int, col: Int) -> CGRect {
    let x = margin + CGFloat(col) * (tile + gap)
    let y = margin + CGFloat(row) * (tile + gap)   // row 0 is the TOP row, so flip
    return CGRect(x: x, y: S - y - tile, width: tile, height: tile)
}

for (row, keys) in grid.enumerated() {
    for (col, key) in keys.enumerated() {
        let r = rect(row: row, col: col)
        let path = CGPath(roundedRect: r, cornerWidth: 12, cornerHeight: 12, transform: nil)

        switch key {
        case .session(let color):
            ctx.addPath(path)
            ctx.setFillColor(color)
            ctx.fillPath()

        case .command:
            ctx.addPath(path)
            ctx.setFillColor(command)
            ctx.fillPath()

            // A short bar standing in for the key's label. Without it the bottom row reads as
            // three dead tiles; with it, it reads as three keys with something written on them.
            let barW = tile * 0.46
            let barH = tile * 0.11
            let bar = CGRect(x: r.midX - barW / 2, y: r.midY - barH / 2, width: barW, height: barH)
            ctx.addPath(CGPath(roundedRect: bar, cornerWidth: barH / 2, cornerHeight: barH / 2,
                               transform: nil))
            ctx.setFillColor(rgb(0xFFFFFF, 0.30))
            ctx.fillPath()

        case .back:
            ctx.addPath(path)
            ctx.setFillColor(back)
            ctx.fillPath()

            // Outlined rather than filled: the Back key belongs to the host, not to the plugin,
            // and drawing it as an empty slot is what says "this one is not ours".
            ctx.addPath(CGPath(roundedRect: r.insetBy(dx: 1, dy: 1), cornerWidth: 11,
                               cornerHeight: 11, transform: nil))
            ctx.setStrokeColor(rgb(0xFFFFFF, 0.16))
            ctx.setLineWidth(2)
            ctx.strokePath()

            // A back chevron, drawn as two strokes so it stays a chevron at 48px.
            let arm = tile * 0.17
            ctx.setStrokeColor(rgb(0xFFFFFF, 0.55))
            ctx.setLineWidth(tile * 0.085)
            ctx.setLineCap(.round)
            ctx.setLineJoin(.round)
            ctx.move(to: CGPoint(x: r.midX + arm * 0.55, y: r.midY + arm))
            ctx.addLine(to: CGPoint(x: r.midX - arm * 0.55, y: r.midY))
            ctx.addLine(to: CGPoint(x: r.midX + arm * 0.55, y: r.midY - arm))
            ctx.strokePath()
        }
    }
}

// --- Claude burst on the centre tile --------------------------------------------------------
// Without it this is a generic coloured grid; with it the icon says whose sessions these are.
// Kept to eleven thick rays so it survives being scaled to 48px in the plugin list.
let centre = rect(row: 1, col: 1)
let cx = centre.midX
let cy = centre.midY
let rays = 11
let inner: CGFloat = tile * 0.06
let lengths: [CGFloat] = [0.40, 0.33, 0.38, 0.30, 0.41, 0.31, 0.37, 0.34, 0.39, 0.30, 0.36]

ctx.setStrokeColor(rgb(0xFFFFFF, 0.97))
ctx.setLineCap(.round)
ctx.setLineWidth(tile * 0.115)
for i in 0..<rays {
    let angle = (CGFloat(i) / CGFloat(rays)) * .pi * 2 + 0.18
    let outer = tile * lengths[i]
    ctx.move(to: CGPoint(x: cx + cos(angle) * inner, y: cy + sin(angle) * inner))
    ctx.addLine(to: CGPoint(x: cx + cos(angle) * outer, y: cy + sin(angle) * outer))
}
ctx.strokePath()

// --- write -----------------------------------------------------------------------------------
let out = URL(fileURLWithPath: CommandLine.arguments[1])
guard let image = ctx.makeImage(),
      let dest = CGImageDestinationCreateWithURL(out as CFURL, UTType.png.identifier as CFString, 1, nil)
else { fatalError("no image") }
CGImageDestinationAddImage(dest, image, nil)
guard CGImageDestinationFinalize(dest) else { fatalError("write failed") }
print("wrote \(out.path)")
