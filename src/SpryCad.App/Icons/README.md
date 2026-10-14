# SpryCAD icons

One SVG per icon, named as the code names it (`extrude.svg` for `Icons.Make("extrude")`). They are embedded at
build and drawn with Svg.Skia, so a change here shows at the next build. An icon with no SVG falls back to the
line drawing in `UI/Icons.cs`.

Open and edit them in Inkscape, Figma or any text editor. Keep to the rules below so the set stays consistent.

## Canvas

- `width="32" height="32" viewBox="0 0 32 32"`. The same drawing is used at every size (shrunk for the tree and
  menus, 16 to 24 px on screen).
- An optional `name-16.svg` (`viewBox="0 0 16 16"`) is used instead at 100% display scaling for icons that turn
  muddy when shrunk. Most icons do not need one.
- Plain SVG only: paths, ellipses, rects, polygons, linear/radial gradients, clip paths, groups. No text, no
  filters, no images, no CSS classes (attributes only: `fill=`, `stroke=`, ...).

## Two styles

**Solid icons** (features and cuts, parts, bodies, assembly components: anything that is a 3D shape):

- Drawn as a lit solid in a 2:1 isometric view (lines going across fall 1 px for every 2 px across, which keeps
  them clean). The standard block: top face `M4.5 17.5L16.5 11.5L28.5 17.5L16.5 23.5Z`, 7 deep; larger blocks
  keep the 2:1 slope.
- Light from the top left: top face lightest, left face mid, right face darkest, each a subtle linear gradient.
- A white highlight line (`stroke="#FFFFFF" stroke-opacity=".8"`) along the lit edges between the top and the
  side faces, inset half a pixel.
- Outline: 1 px, `stroke="#24364F"`, `stroke-linejoin="miter"`, square caps, on pixel centres (x.5 coordinates
  for vertical and horizontal edges).

**Line icons** (app commands, sketch tools, view, panels, anything flat):

- 2 px strokes (they shrink to 1 px at 16 px), `stroke="#24364F"`, on whole-pixel coordinates, square or miter
  joins for straight geometry, round only where the shape is round.
- Simple fills are fine where they help (`#E4F0FD` light blue, `#FFFFFF` white, `#D5DBE3` grey).
- The accent (`#2F7BD6`, 2 px) marks the one thing the command is about, as now.

## Colours mean something

| Colour | Meaning |
|---|---|
| Steel grey faces (`#F1F3F6`→`#D5DBE3` top, `#AEB8C5`→`#C5CDD7` left, `#8E9BAC`→`#7A8899` right) | the part as it was |
| Blue faces (`#E4F0FD`→`#B9D6F6` top, `#7FAAE0`→`#9FC2EC` left, `#5F92D2`→`#4A7DC0` right) | what the command makes or adds |
| Removed material (cuts) | the cut's pocket shaded dark blue inside the grey block (`#2C5592`→`#9CC2EE` walls, `#162B4D` deepest) |
| Amber (`#FFC45C`→`#EE9A22`, edge `#8A5200`) | direction or motion: arrows, drag, play |
| Accent `#2F7BD6`, dark accent edge `#1D4E8F` | the feature's key edge or line |
| Green `#2E9E5B` / red `#D64545` | OK / cancel, add / remove only |

## Theme colours

The app swaps two colours when it loads an icon, so dark mode and the accent setting keep working:

- `#24364F` (outlines and line-icon strokes) becomes the theme's text colour.
- `#2F7BD6` (accent) becomes the theme's accent.

Use exactly these two values for outlines and accents. Everything else stays as drawn.
