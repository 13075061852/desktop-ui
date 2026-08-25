# Toolbar Rocket Transition Design

## Goal

Animate manual toolbar alignment changes from the settings window with a restrained rocket-like transition.

## Interaction

- Trigger only when the user selects 靠左、居中 or 靠右 in Settings.
- Move the toolbar horizontally over 900ms with cubic ease-in acceleration, starting slowly and continuously accelerating into the target.
- Place a mint arrowhead on the leading edge.
- Emit eighteen clearly visible mint, cyan-white, and white glowing particles from the trailing edge, with denser exhaust later in the launch.
- Mirror arrowhead and exhaust direction automatically for right-to-left motion.
- Fade and remove all temporary effects on arrival.
- Disable toolbar hit testing during flight and restore it when complete.
- Do not animate startup restoration, saving, or cancellation restoration.

## Implementation

`SettingsWindow` marks toolbar-originated previews with a boolean callback argument. `MainWindow` measures the toolbar's current and target X coordinates, changes the WPF alignment, and uses a temporary `TranslateTransform` to animate between the measured positions. A hit-test-transparent overlay Canvas hosts the arrowhead and particle elements. A flight version token prevents stale completion callbacks from clearing a newer animation.

## Verification

- Center to right: arrow on right, particles on left.
- Right to left: arrow on left, particles on right.
- Arrival clears effects and restores interaction.
- Cancel restores the original alignment without animation.
