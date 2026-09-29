"""
TAILED town kit (Blender 5.x, headless): houses, yard dressing, car-park props and trees, so the
suburbs read as a lived-in place rather than extruded boxes.

    blender -b -P tools/blender/build_town.py [-- --only House]

Exports unity/Assets/Tailed/Resources/Art/Town/<Name>.fbx. TownBuilder appends these into its
combined meshes (ArtKit), so hundreds of houses and trees stay a handful of draw calls.

Colours are authored as sRGB (Unity converts them, like the rest of the procedural town). Markers
are swapped per instance:
  WALL (magenta)  house walls            ROOF (cyan)   roof covering
  TRIM (yellow)   door, shutters, fences LEAF (green)  foliage, LEAF2 (dark green) its shade
Houses: x along the frontage, y up, +z towards the street; the front is the largest z (Unity puts
it on the building line) and the driveway side is -x. Window glass carries alpha 0.95 so the toon
shader lights some of them at night.
"""
import math, os, sys
sys.path.insert(0, os.path.dirname(__file__))
import bmesh  # noqa: E402
from mathutils import Matrix  # noqa: E402
from build_assets import Part, U, build_object, clear_scene, export, ROOT  # noqa: E402

OUT = os.path.join(ROOT, "unity", "Assets", "Tailed", "Resources", "Art", "Town")

WALL = (1.0, 0.0, 1.0, 1.0)
ROOF = (0.0, 1.0, 1.0, 1.0)
TRIM = (1.0, 1.0, 0.0, 1.0)
LEAF = (0.0, 1.0, 0.0, 1.0)
LEAF2 = (0.0, 0.5, 0.0, 1.0)

WHITE = (0.96, 0.95, 0.92, 1.0)
STONE = (0.62, 0.6, 0.57, 1.0)
BRICK = (0.66, 0.33, 0.26, 1.0)
WOOD = (0.64, 0.45, 0.28, 1.0)
DARKWOOD = (0.4, 0.28, 0.18, 1.0)
WINDOW = (0.33, 0.45, 0.6, 0.95)
DARK = (0.16, 0.16, 0.18, 1.0)
STEEL = (0.7, 0.72, 0.75, 1.0)
CONCRETE = (0.78, 0.77, 0.74, 1.0)
YELLOW = (0.98, 0.8, 0.2, 1.0)
RED = (0.86, 0.2, 0.18, 1.0)
BLUE = (0.25, 0.5, 0.9, 1.0)
GREEN = (0.25, 0.6, 0.3, 1.0)
GOLD = (1.0, 0.8, 0.25, 1.0)
SOIL = (0.36, 0.25, 0.17, 1.0)
WATER = (0.45, 0.75, 0.95, 1.0)
TRUNK = (0.45, 0.32, 0.22, 1.0)
BIRCH = (0.9, 0.88, 0.84, 1.0)
LAMP = (1.0, 0.95, 0.8, 0.95)
FLOWERS = [(0.95, 0.35, 0.45, 1.0), (1.0, 0.85, 0.3, 1.0), (0.7, 0.5, 0.95, 1.0), (1.0, 1.0, 1.0, 1.0), (1.0, 0.55, 0.25, 1.0)]


def box(color, lo, hi, r=0.03, seg=1):
    lo, hi = tuple(min(a, b) for a, b in zip(lo, hi)), tuple(max(a, b) for a, b in zip(lo, hi))
    return Part(color).box(lo, hi).bevel(r, seg)


def cyl(color, centre, axis, radius, depth, r=0.02, seg=12):
    return Part(color).cylinder(centre, axis, radius, depth, seg).bevel(r, 1)


def ball(color, centre, radii, seg=10, rings=7):
    return Part(color).sphere(centre, radii, seg, rings)


def cone(color, base, r1, r2, h, seg=10):
    p = Part(color)
    res = bmesh.ops.create_cone(p.bm, cap_ends=True, cap_tris=False, segments=seg, radius1=r1, radius2=r2, depth=h)
    bmesh.ops.transform(p.bm, matrix=Matrix.Translation(U(base[0], base[1] + h * 0.5, base[2])), verts=res["verts"])
    return p


def beam(color, a, b, t):
    """Square bar from Unity point a to b."""
    p = Part(color)
    ax, ay, az = a
    bx, by, bz = b
    length = math.dist(a, b)
    p.box((-t / 2, -t / 2, 0), (t / 2, t / 2, length))
    d = U(bx - ax, by - ay, bz - az).normalized()
    rot = U(0, 0, 1).rotation_difference(d).to_matrix().to_4x4()
    bmesh.ops.transform(p.bm, matrix=Matrix.Translation(U(ax, ay, az)) @ rot, verts=p.bm.verts)
    return p


def turn(parts, degrees, pivot=(0, 0, 0), offset=(0, 0, 0)):
    """Rotate parts about the vertical axis (Unity y) through pivot, then move by offset."""
    m = Matrix.Translation(U(*offset)) @ Matrix.Translation(U(*pivot)) @ Matrix.Rotation(math.radians(degrees), 4, "Z") @ Matrix.Translation(-U(*pivot))
    for p in parts:
        bmesh.ops.transform(p.bm, matrix=m, verts=p.bm.verts)
    return parts


def slab(color, a, b, x_half, t=0.2, x0=0.0):
    """Roof plane from (z, y) a to b, t thick (hangs below the line), across ±x_half."""
    (za, ya), (zb, yb) = a, b
    return Part(color).prism([(za, ya), (zb, yb), (zb, yb - t), (za, ya - t)], x_half, x0).bevel(0.02, 1)


def gable_roof(x_half, z0, z1, y0, rise, oh=0.45, x0=0.0, wall=WALL, trim=WHITE):
    """Two roof planes (ridge along x) over walls z0..z1, attic gable in wall colour, fascia and ridge cap."""
    zc = (z0 + z1) * 0.5
    k = rise / ((z1 - z0) * 0.5)
    ridge = (zc, y0 + rise + 0.12)
    parts = [slab(ROOF, (z1 + oh, y0 - oh * k), ridge, x_half + oh, 0.22, x0),
             slab(ROOF, (z0 - oh, y0 - oh * k), ridge, x_half + oh, 0.22, x0),
             Part(wall).prism([(z1, y0 - 0.01), (zc, y0 + rise - 0.02), (z0, y0 - 0.01)], x_half, x0),
             box(ROOF, (x0 - x_half - oh, y0 + rise + 0.02, zc - 0.13), (x0 + x_half + oh, y0 + rise + 0.2, zc + 0.13), 0.04)]
    for z in (z1 + oh, z0 - oh):   # gutters
        parts.append(box(trim, (x0 - x_half - oh, y0 - oh * k - 0.3, z - 0.06), (x0 + x_half + oh, y0 - oh * k - 0.12, z + 0.06), 0.03))
    return parts


def hip_roof(x0, x1, z0, z1, y0, rise, oh=0.45):
    p = Part(ROOF)
    xa, xb, za, zb = x0 - oh, x1 + oh, z0 - oh, z1 + oh
    ye = y0 - oh * rise / ((z1 - z0) * 0.5)
    inset = (zb - za) * 0.5
    zc = (za + zb) * 0.5
    bm = p.bm
    v = [bm.verts.new(U(x, ye, z)) for x, z in ((xa, za), (xb, za), (xb, zb), (xa, zb))]
    r0, r1 = bm.verts.new(U(xa + inset, y0 + rise, zc)), bm.verts.new(U(xb - inset, y0 + rise, zc))
    bm.faces.new([v[0], v[1], v[2], v[3]])
    bm.faces.new([v[3], v[2], r1, r0])
    bm.faces.new([v[1], v[0], r0, r1])
    bm.faces.new([v[0], v[3], r0])
    bm.faces.new([v[2], v[1], r1])
    p.bevel(0.03, 1)
    return [p, box(WHITE, (xa, ye - 0.22, zb - 0.07), (xb, ye - 0.04, zb + 0.05), 0.03),
            box(WHITE, (xa, ye - 0.22, za - 0.05), (xb, ye - 0.04, za + 0.07), 0.03)]


class Facade:
    """Boxes placed relative to one wall of a W x (z0..z1) footprint: u along the wall, v up, n out."""

    def __init__(self, face, x0, x1, z0, z1):
        self.face, self.x0, self.x1, self.z0, self.z1 = face, x0, x1, z0, z1

    def pt(self, u, v, n):
        xc, zc = (self.x0 + self.x1) * 0.5, (self.z0 + self.z1) * 0.5
        if self.face == "front":
            return (xc + u, v, self.z1 + n)
        if self.face == "back":
            return (xc - u, v, self.z0 - n)
        if self.face == "right":
            return (self.x1 + n, v, zc - u)
        return (self.x0 - n, v, zc + u)

    def box(self, color, u0, u1, v0, v1, n0, n1, r=0.0):
        return box(color, self.pt(u0, v0, n0), self.pt(u1, v1, n1), r)

    def window(self, u, vb, w=1.3, h=1.3, shutters=False, box_flowers=False):
        f = 0.09
        parts = [self.box(WINDOW, u - w / 2, u + w / 2, vb, vb + h, -0.04, 0.03),
                 self.box(WHITE, u - w / 2 - f, u + w / 2 + f, vb + h, vb + h + f, 0, 0.09),       # head
                 self.box(WHITE, u - w / 2 - f - 0.06, u + w / 2 + f + 0.06, vb - f - 0.03, vb, 0, 0.16),  # sill
                 self.box(WHITE, u - w / 2 - f, u - w / 2, vb, vb + h, 0, 0.09),
                 self.box(WHITE, u + w / 2, u + w / 2 + f, vb, vb + h, 0, 0.09),
                 self.box(WHITE, u - 0.025, u + 0.025, vb, vb + h, 0, 0.06),                       # glazing bars
                 self.box(WHITE, u - w / 2, u + w / 2, vb + h * 0.5 - 0.025, vb + h * 0.5 + 0.025, 0, 0.06)]
        if shutters:
            sw = min(0.55, w * 0.42)
            for s in (-1, 1):
                a = u + s * (w / 2 + f + 0.04)
                parts.append(self.box(TRIM, a, a + s * sw, vb - 0.02, vb + h + 0.02, 0, 0.07))
                for k in range(1, 4):   # louvre lines
                    y = vb + h * k / 4
                    parts.append(self.box(DARK, a + s * 0.06, a + s * (sw - 0.06), y - 0.012, y + 0.012, 0.07, 0.085, 0.004))
        if box_flowers:
            parts.append(self.box(WOOD, u - w / 2, u + w / 2, vb - 0.42, vb - 0.12, 0.12, 0.42))
            for i in range(5):
                uu = u - w / 2 + 0.15 + (w - 0.3) * i / 4
                parts.append(ball(FLOWERS[i % len(FLOWERS)], self.pt(uu, vb - 0.1, 0.28), (0.13, 0.11, 0.13), 7, 5))
        return parts

    def door(self, u, w=1.0, h=2.15, step=True, lamp=True):
        parts = [self.box(TRIM, u - w / 2, u + w / 2, 0.3, h, -0.04, 0.05),
                 self.box(WINDOW, u - w * 0.22, u + w * 0.22, h - 0.55, h - 0.18, 0.05, 0.07),
                 self.box(WHITE, u - w / 2 - 0.1, u - w / 2, 0.3, h + 0.1, 0, 0.1),
                 self.box(WHITE, u + w / 2, u + w / 2 + 0.1, 0.3, h + 0.1, 0, 0.1),
                 self.box(WHITE, u - w / 2 - 0.1, u + w / 2 + 0.1, h, h + 0.12, 0, 0.1),
                 ball(GOLD, self.pt(u + w * 0.33, 1.2, 0.09), (0.05, 0.05, 0.05), 8, 5)]
        if step:
            parts.append(self.box(STONE, u - w / 2 - 0.3, u + w / 2 + 0.3, -0.3, 0.3, 0, 0.75, 0.04))
        if lamp:
            parts.append(self.box(DARK, u + w / 2 + 0.25, u + w / 2 + 0.45, h - 0.2, h + 0.15, 0, 0.18))
            parts.append(self.box(LAMP, u + w / 2 + 0.28, u + w / 2 + 0.42, h - 0.15, h + 0.08, 0.03, 0.16))
        return parts


def walls(x0, x1, z0, z1, h, plinth=0.35):
    return [box(STONE, (x0 - 0.08, -0.3, z0 - 0.08), (x1 + 0.08, plinth, z1 + 0.08), 0.03),
            box(WALL, (x0, plinth - 0.05, z0), (x1, h, z1), 0.02),
            *[box(WHITE, (x - 0.1, plinth, z - 0.1), (x + 0.1, h, z + 0.1), 0.02)
              for x in (x0, x1) for z in (z0, z1)]]


def chimney(x, z, y0, y1, color=BRICK):
    return [box(color, (x - 0.45, y0, z - 0.4), (x + 0.45, y1, z + 0.4), 0.03),
            box(STONE, (x - 0.52, y1, z - 0.47), (x + 0.52, y1 + 0.16, z + 0.47), 0.03),
            box(DARK, (x - 0.18, y1 + 0.16, z - 0.15), (x + 0.18, y1 + 0.34, z + 0.15), 0.02)]


def side_windows(x0, x1, z0, z1, rows, w=1.1):
    parts = []
    for face in ("left", "right", "back"):
        f = Facade(face, x0, x1, z0, z1)
        span = (z1 - z0) if face != "back" else (x1 - x0)
        us = [-span * 0.25, span * 0.25] if span > 7 else [0.0]
        for vb in rows:
            for u in us:
                parts += f.window(u, vb, w, 1.2)
    return parts


# ---- houses ------------------------------------------------------------------------------------

def house_bungalow():
    x0, x1, z0, z1, h = -5, 5, -8.4, -1.6, 3.0
    front = Facade("front", x0, x1, z0, z1)
    parts = walls(x0, x1, z0, z1, h) + gable_roof(5, z0, z1, h, 2.3) + chimney(-3.4, -6.3, 3.2, 6.6)
    parts += front.window(-2.6, 1.0, 2.0, 1.3, shutters=True) + front.window(3.9, 1.1, 0.9, 1.1)
    parts += front.door(1.8, step=False)
    # Porch: deck, posts, lean-to roof.
    parts += [box(WOOD, (0.2, -0.3, z1), (3.4, 0.38, z1 + 1.6), 0.03)]
    parts += [box(WHITE, (x - 0.09, 0.38, z1 + 1.35), (x + 0.09, 2.75, z1 + 1.53), 0.03) for x in (0.35, 3.25)]
    parts += [slab(ROOF, (z1 - 0.1, 3.05), (z1 + 1.8, 2.7), 1.75, 0.16, 1.8)]
    parts += [box(WHITE, (0.2, 0.9, z1 + 1.4), (0.3, 0.98, z1 + 1.5)), box(WHITE, (3.3, 0.9, z1 + 1.4), (3.4, 0.98, z1 + 1.5))]
    return parts + side_windows(x0, x1, z0, z1, [1.0])


def house_twostorey():
    x0, x1, z0, z1, h = -4.5, 4.5, -8.0, -0.9, 5.8
    front = Facade("front", x0, x1, z0, z1)
    parts = walls(x0, x1, z0, z1, h) + gable_roof(4.5, z0, z1, h, 2.4)
    parts += [box(WHITE, (x0 - 0.06, 2.95, z0 - 0.06), (x1 + 0.06, 3.12, z1 + 0.06), 0.02)]       # floor band
    parts += chimney(x1 + 0.45, -4.5, -0.3, 9.1)
    for u in (-2.8, 2.8):
        parts += front.window(u, 0.95, 1.3, 1.35, shutters=True)
    for u in (-2.8, 0.0, 2.8):
        parts += front.window(u, 3.85, 1.1, 1.2, shutters=u != 0.0)
    parts += front.door(0.0, step=True, lamp=True)
    # Little gabled hood over the door on brackets.
    parts += turn(gable_roof(0.2, -0.85, 0.85, 2.55, 0.45, 0.1), 90, offset=(0.0, 0.0, z1 + 0.45))
    return parts + side_windows(x0, x1, z0, z1, [0.95, 3.85])


def house_cottage():
    x0, x1, z0, z1, h = -4.75, 4.75, -9.2, -1.2, 3.0
    front = Facade("front", x0, x1, z0, z1)
    parts = walls(x0, x1, z0, z1, h) + hip_roof(x0, x1, z0, z1, h, 2.6) + chimney(2.4, -6.8, 3.4, 6.3, STONE)
    # Bay window with its own little roof.
    bay = Facade("front", -3.4, -1.0, z1, z1 + 0.9)
    parts += [box(WALL, (-3.4, 0.3, z1 - 0.1), (-1.0, 2.65, z1 + 0.9), 0.03)]
    parts += bay.window(0, 0.95, 1.9, 1.35, box_flowers=True)
    parts += [slab(ROOF, (z1 + 1.15, 2.6), (z1 - 0.05, 3.05), 1.35, 0.16, -2.2)]
    parts += front.door(1.4) + front.window(3.45, 1.0, 0.95, 1.25, box_flowers=True)
    return parts + side_windows(x0, x1, z0, z1, [1.0])


def house_ranch():
    """Long and low with a street-facing garage wing on the driveway side (-x)."""
    x0, x1, z0, z1, h = -5.6, 5.6, -8.6, -2.6, 2.9
    parts = walls(x0, x1, z0, z1, h) + gable_roof(5.6, z0, z1, h, 1.9)
    # Garage wing: x -5.6..-0.6, out to the front line.
    gx0, gx1, gz1 = -5.6, -0.6, 0.0
    parts += walls(gx0, gx1, -5.0, gz1, 2.9)
    parts += turn(gable_roof(2.6, gx0, gx1, 2.9, 1.5, 0.4), 90, pivot=(0, 0, 0), offset=((gx0 + gx1) / 2, 0, -2.6))
    g = Facade("front", gx0, gx1, -5.0, gz1)
    parts += [g.box(WHITE, -1.75, 1.75, 0.3, 2.45, -0.04, 0.04)]
    parts += [g.box(STONE, -1.75, 1.75, v - 0.02, v + 0.02, 0.04, 0.07, 0.005) for v in (0.8, 1.3, 1.8)]
    parts += [g.box(WHITE, -1.95, 1.95, 2.45, 2.62, 0, 0.12)]
    parts += [g.box(LAMP, 2.0, 2.2, 2.0, 2.3, 0, 0.14)]
    parts += [ball(WINDOW, g.pt(0, 3.55, 0.02), (0.38, 0.38, 0.06), 12, 6)]           # round attic vent-window
    front = Facade("front", x0, x1, z0, z1)
    parts += front.door(0.9) + front.window(3.3, 1.0, 2.1, 1.25, shutters=True)
    parts += [box(WOOD, (0.1, -0.3, z1), (1.7, 0.36, z1 + 1.1), 0.03)]
    return parts + side_windows(x0, x1, z0, z1, [1.0])


def house_aframe():
    x0, x1, z0, z1 = -4.0, 4.0, -9.4, -2.2
    parts = [box(STONE, (x0 - 0.1, -0.3, z0), (x1 + 0.1, 0.4, z1), 0.03),
             box(WALL, (x0 + 0.3, 0.35, z0 + 0.1), (x1 - 0.3, 1.0, z1 - 0.1), 0.02)]
    # Steep roof reaching nearly to the ground, gable to the street (ridge along z).
    rise, oh = 6.6, 0.45
    roof = gable_roof((z1 - z0) / 2, x0, x1, 0.8, rise, oh, 0.0)
    parts += turn(roof, 90, offset=(0, 0, (z0 + z1) / 2))
    # Glazed front triangle: mullion grid over glass.
    tri = [(x0 + 0.35, 0.95), (x1 - 0.35, 0.95), (0.0, 0.8 + rise - 0.5)]
    glass = Part(WINDOW)
    bm = glass.bm
    vs = [bm.verts.new(U(x, y, z1 - 0.05)) for x, y in tri] + [bm.verts.new(U(x, y, z1 - 0.2)) for x, y in tri]
    bm.faces.new(vs[:3]); bm.faces.new(vs[3:][::-1])
    for i in range(3):
        j = (i + 1) % 3
        bm.faces.new([vs[i], vs[j], vs[3 + j], vs[3 + i]])
    parts.append(glass.bevel(0))
    for y in (2.6, 4.4):
        half = (x1 - 0.35) * (1 - (y - 0.95) / (rise - 0.65))
        parts.append(box(WHITE, (-half, y - 0.05, z1 - 0.06), (half, y + 0.05, z1 + 0.04)))
    parts.append(box(WHITE, (-0.05, 0.95, z1 - 0.06), (0.05, 0.8 + rise - 0.5, z1 + 0.04)))
    parts.append(box(TRIM, (-0.55, 0.4, z1 - 0.1), (0.55, 2.4, z1 + 0.06)))
    # Deck with a railing.
    parts += [box(WOOD, (x0 + 0.2, -0.3, z1), (x1 - 0.2, 0.5, z1 + 2.1), 0.03)]
    for x in (x0 + 0.3, x1 - 0.3):
        parts.append(box(DARKWOOD, (x - 0.06, 0.5, z1 + 0.1), (x + 0.06, 1.45, z1 + 2.0)))
    for x in (x0 + 0.3, -1.0, 1.0, x1 - 0.3):
        parts.append(box(DARKWOOD, (x - 0.06, 0.5, z1 + 1.9), (x + 0.06, 1.45, z1 + 2.05)))
    parts += [box(DARKWOOD, (x0 + 0.25, 1.4, z1 + 1.92), (-0.9, 1.5, z1 + 2.05)), box(DARKWOOD, (0.9, 1.4, z1 + 1.92), (x1 - 0.25, 1.5, z1 + 2.05))]
    parts += chimney(2.2, -7.5, 3.0, 7.0, STONE)
    return parts


def house_modern():
    x0, x1, z0, z1 = -5.0, 5.0, -8.8, -1.0
    parts = [box(STONE, (x0, -0.3, z0), (x1, 0.3, z1), 0.02),
             box(WALL, (x0, 0.25, z0), (x1, 3.2, z1), 0.02),
             box(ROOF, (x0 - 0.1, 3.2, z0 - 0.1), (x1 + 0.1, 3.4, z1 + 0.1), 0.02)]
    # Upper box, cantilevered towards the street, clad in vertical boards.
    ux0, ux1, uz0, uz1 = -5.0, 2.2, -8.0, 0.0
    parts += [box(WOOD, (ux0, 3.35, uz0), (ux1, 6.3, uz1), 0.02),
              box(ROOF, (ux0 - 0.15, 6.3, uz0 - 0.15), (ux1 + 0.15, 6.55, uz1 + 0.15), 0.02)]
    up = Facade("front", ux0, ux1, uz0, uz1)
    for i in range(18):
        u = -3.4 + i * 0.4
        parts.append(up.box(DARKWOOD, u - 0.03, u + 0.03, 3.4, 6.25, 0, 0.04, 0.005))
    parts += [up.box(WINDOW, -3.2, 2.6, 4.2, 5.6, -0.03, 0.08), up.box(DARK, -3.3, 2.7, 4.1, 4.2, 0, 0.1), up.box(DARK, -3.3, 2.7, 5.6, 5.7, 0, 0.1)]
    low = Facade("front", x0, x1, z0, z1)
    parts += [low.box(WINDOW, 1.0, 4.6, 0.35, 2.9, -0.03, 0.05)]
    parts += [low.box(DARK, u - 0.05, u + 0.05, 0.3, 2.95, 0, 0.08) for u in (1.0, 2.8, 4.6)]
    parts += [low.box(DARK, 1.0, 4.6, 2.9, 3.0, 0, 0.08)]
    parts += low.door(-1.2, 1.2, 2.4, step=True, lamp=False)
    parts += [low.box(DARK, -3.9, -2.4, 0.3, 2.6, -0.02, 0.04)]          # carport-style dark garage door on the -x side
    parts += [low.box(STEEL, -3.9, -2.4, v, v + 0.03, 0.04, 0.06, 0.003) for v in (0.8, 1.4, 2.0)]
    parts += side_windows(x0, x1, z0, z1, [0.9], 1.6)
    return parts


def house_gambrel():
    """Barn-roofed colonial with dormers."""
    x0, x1, z0, z1, h = -4.8, 4.8, -8.8, -1.4, 2.9
    front = Facade("front", x0, x1, z0, z1)
    parts = walls(x0, x1, z0, z1, h)
    zc = (z0 + z1) / 2
    oh = 0.4
    prof = [(z1 + oh, h - 0.25), (z1 - 0.7, h + 2.0), (zc, h + 3.0), (z0 + 0.7, h + 2.0), (z0 - oh, h - 0.25)]
    for a, b in zip(prof, prof[1:]):
        parts.append(slab(ROOF, a, b, 4.8 + 0.35, 0.2))
    parts.append(Part(WALL).prism([(z1, h - 0.01), (z1 - 0.65, h + 1.92), (zc, h + 2.9), (z0 + 0.65, h + 1.92), (z0, h - 0.01)], 4.8).bevel(0))
    for x in (-2.4, 2.4):   # dormers
        parts += [box(WALL, (x - 0.8, h + 0.6, z1 - 1.6), (x + 0.8, h + 2.2, z1 - 0.1), 0.02)]
        parts += Facade("front", x - 0.8, x + 0.8, z1 - 1.6, z1 - 0.1).window(0, h + 0.85, 0.9, 1.0)
        parts += turn(gable_roof(0.75, x - 0.8, x + 0.8, h + 2.2, 0.7, 0.15), 90, pivot=(x, 0, 0), offset=(0, 0, z1 - 0.85))
    parts += front.door(0.0) + front.window(-2.9, 0.95, 1.3, 1.3, shutters=True) + front.window(2.9, 0.95, 1.3, 1.3, shutters=True)
    parts += [box(WHITE, (-0.75, 2.55, z1), (0.75, 2.68, z1 + 1.2)), box(WHITE, (-0.7, 0.3, z1 + 1.0), (-0.58, 2.55, z1 + 1.12)),
              box(WHITE, (0.58, 0.3, z1 + 1.0), (0.7, 2.55, z1 + 1.12))]
    parts += chimney(-4.3, -5.5, 2.5, 8.1, BRICK)
    return parts + side_windows(x0, x1, z0, z1, [0.95])


HOUSES = {"House_Bungalow": house_bungalow, "House_TwoStorey": house_twostorey, "House_Cottage": house_cottage,
          "House_Ranch": house_ranch, "House_AFrame": house_aframe, "House_Modern": house_modern, "House_Gambrel": house_gambrel}


# ---- yard dressing (origin on the ground, +z towards the street) --------------------------------

def fence_picket():
    """4 m of picket fence along x (TRIM: white or stained per street). Kept lean: it's everywhere."""
    parts = [box(TRIM, (-2.0, 0.3, -0.04), (2.0, 0.38, 0.0), 0), box(TRIM, (-2.0, 0.72, -0.04), (2.0, 0.8, 0.0), 0)]
    for i in range(10):
        x = -1.8 + i * 0.4
        parts.append(box(TRIM, (x - 0.07, 0.0, 0.0), (x + 0.07, 0.95, 0.04), 0))
        parts.append(Part(TRIM).prism([(0.0, 0.95), (0.04, 0.95), (0.02, 1.08)], 0.07, x).bevel(0))
    for x in (-1.98, 1.98):
        parts.append(box(TRIM, (x - 0.07, 0.0, -0.06), (x + 0.07, 1.1, 0.06), 0))
    return parts


def fence_board():
    """4 m of close-boarded garden fence along x (TRIM: stain): one panel with board grooves."""
    parts = [box(TRIM, (-1.95, 0.05, -0.02), (1.95, 1.75, 0.02), 0)]
    parts += [box(DARKWOOD, (x - 0.015, 0.1, 0.02), (x + 0.015, 1.7, 0.03), 0) for x in (-1.3, -0.65, 0.0, 0.65, 1.3)]
    parts += [box(DARKWOOD, (-2.0, 0.4, -0.07), (2.0, 0.5, -0.02), 0), box(DARKWOOD, (-2.0, 1.3, -0.07), (2.0, 1.4, -0.02), 0)]
    parts += [box(DARKWOOD, (x - 0.06, 0.0, -0.1), (x + 0.06, 1.85, 0.03), 0) for x in (-1.98, 1.98)]
    return parts


def hedge():
    parts = [box(LEAF2, (-2.0, 0.0, -0.45), (2.0, 1.0, 0.45), 0.25, 2)]
    for i in range(6):
        x = -1.7 + i * 0.68
        parts.append(ball(LEAF, (x, 0.95, 0.05 * (i % 2)), (0.5, 0.3, 0.48), 8, 5))
    return parts


def mailbox():
    return [box(WOOD, (-0.05, 0.0, -0.05), (0.05, 1.0, 0.05)),
            box(TRIM, (-0.16, 1.0, -0.26), (0.16, 1.2, 0.26), 0.02),
            cyl(TRIM, (0, 1.2, 0), "z", 0.16, 0.52, 0.01, 10),
            box(RED, (0.16, 1.1, -0.1), (0.19, 1.45, -0.04)), box(RED, (0.16, 1.35, -0.04), (0.19, 1.45, 0.06))]


def bins():
    parts = []
    for i, lid in enumerate((GREEN, BLUE)):
        x = i * 0.75
        parts += [box(DARK, (x - 0.3, 0.05, -0.35), (x + 0.3, 1.0, 0.35), 0.05),
                  box(lid, (x - 0.33, 1.0, -0.38), (x + 0.33, 1.08, 0.38), 0.03),
                  cyl(DARK, (x - 0.2, 0.08, -0.33), "x", 0.08, 0.08, 0.01, 8), cyl(DARK, (x + 0.2, 0.08, -0.33), "x", 0.08, 0.08, 0.01, 8)]
    return parts


def swing():
    parts = []
    for x in (-1.6, 1.6):
        parts += [beam(RED, (x, 0, -0.9), (x, 2.2, 0), 0.08), beam(RED, (x, 0, 0.9), (x, 2.2, 0), 0.08)]
    parts.append(cyl(YELLOW, (0, 2.2, 0), "x", 0.07, 3.4, 0.01, 8))
    for x in (-0.7, 0.7):
        parts += [box(STEEL, (x - 0.22, 2.2, -0.01), (x - 0.2, 0.55, 0.01), 0), box(STEEL, (x + 0.2, 2.2, -0.01), (x + 0.22, 0.55, 0.01), 0),
                  box(BLUE, (x - 0.25, 0.5, -0.12), (x + 0.25, 0.56, 0.12), 0.02)]
    return parts


def trampoline():
    parts = [cyl(BLUE, (0, 0.72, 0), "y", 1.7, 0.12, 0.03, 20), cyl(DARK, (0, 0.74, 0), "y", 1.5, 0.12, 0.0, 20)]
    for a in range(4):
        t = a * math.pi / 2 + 0.4
        parts.append(box(STEEL, (math.cos(t) * 1.55 - 0.04, 0, math.sin(t) * 1.55 - 0.04), (math.cos(t) * 1.55 + 0.04, 0.7, math.sin(t) * 1.55 + 0.04)))
    return parts


def shed():
    parts = [box(TRIM, (-1.3, 0.0, -1.0), (1.3, 2.0, 1.0), 0.02)]
    parts += gable_roof(1.3, -1.0, 1.0, 2.0, 0.8, 0.2, wall=TRIM)
    parts += [box(DARKWOOD, (-0.45, 0.0, 1.0), (0.45, 1.8, 1.05)), box(WINDOW, (0.7, 1.0, 0.99), (1.1, 1.5, 1.04))]
    return parts


def washing_line():
    parts = [box(STEEL, (x - 0.04, 0, -0.04), (x + 0.04, 1.9, 0.04)) for x in (-2.0, 2.0)]
    parts.append(box(WHITE, (-2.0, 1.83, -0.01), (2.0, 1.85, 0.01), 0))
    for i, c in enumerate([BLUE, WHITE, RED, YELLOW, GREEN]):
        x = -1.5 + i * 0.72
        parts.append(box(c, (x - 0.25, 1.1 + (i % 2) * 0.2, -0.02), (x + 0.25, 1.84, 0.02), 0.01))
    return parts


def pool():
    return [cyl(BLUE, (0, 0.2, 0), "y", 1.3, 0.4, 0.08, 18), cyl(WATER, (0, 0.36, 0), "y", 1.15, 0.1, 0.0, 18),
            ball(RED, (0.4, 0.45, 0.2), (0.25, 0.08, 0.25), 10, 5)]


def bbq():
    return [ball(DARK, (0, 0.85, 0), (0.35, 0.3, 0.35), 12, 7), box(STEEL, (-0.38, 0.84, -0.38), (0.38, 0.86, 0.38), 0),
            *[beam(DARK, (0, 0.6, 0), (math.cos(a) * 0.35, 0, math.sin(a) * 0.35), 0.04) for a in (0.3, 2.4, 4.5)],
            box(WOOD, (0.6, 0.0, -0.3), (1.2, 0.45, 0.3)), box(WOOD, (0.55, 0.45, -0.35), (1.25, 0.5, 0.35))]


def flowerbed():
    parts = [box(DARKWOOD, (-1.5, 0.0, -0.5), (1.5, 0.25, 0.5), 0.02), box(SOIL, (-1.42, 0.2, -0.42), (1.42, 0.27, 0.42), 0)]
    for i in range(10):
        x, z = -1.2 + (i % 5) * 0.6, -0.2 + (i // 5) * 0.4
        parts.append(cyl(LEAF2, (x, 0.35, z), "y", 0.03, 0.2, 0, 5))
        parts.append(ball(FLOWERS[(i * 3) % len(FLOWERS)], (x, 0.5, z), (0.16, 0.13, 0.16), 7, 5))
    return parts


def gnome():
    return [ball(BLUE, (0, 0.18, 0), (0.12, 0.16, 0.1), 8, 5), ball((0.95, 0.78, 0.65, 1), (0, 0.38, 0), (0.08, 0.08, 0.08), 8, 5),
            ball(WHITE, (0, 0.33, 0.05), (0.07, 0.08, 0.05), 8, 5), cone(RED, (0, 0.42, 0), 0.09, 0.0, 0.22, 8)]


# ---- car park dressing ---------------------------------------------------------------------------

def lot_lamp():
    parts = [box(CONCRETE, (-0.35, -0.2, -0.35), (0.35, 0.5, 0.35), 0.05),
             cyl(STEEL, (0, 4.2, 0), "y", 0.1, 7.6, 0.02, 10),
             box(STEEL, (-1.6, 7.8, -0.07), (1.6, 7.95, 0.07), 0.02)]
    for x in (-1.5, 1.5):
        parts += [box(DARK, (x - 0.45, 7.6, -0.25), (x + 0.45, 7.85, 0.25), 0.05), box(LAMP, (x - 0.38, 7.56, -0.19), (x + 0.38, 7.62, 0.19), 0)]
    return parts


def wheel_stop():
    return [box(CONCRETE, (-0.9, 0.0, -0.13), (0.9, 0.14, 0.13), 0.04, 2),
            box(YELLOW, (-0.9, 0.01, -0.135), (-0.6, 0.145, 0.135), 0.02), box(YELLOW, (0.6, 0.01, -0.135), (0.9, 0.145, 0.135), 0.02)]


def dumpster():
    return [box(GREEN, (-1.0, 0.2, -0.75), (1.0, 1.3, 0.75), 0.06, 2),
            slab(DARK, (-0.8, 1.45), (0.8, 1.35), 1.02, 0.07),
            *[cyl(DARK, (x, 0.12, z), "x", 0.12, 0.08, 0.01, 8) for x in (-0.8, 0.8) for z in (-0.6, 0.6)],
            box(DARK, (-1.05, 0.9, 0.7), (1.05, 1.0, 0.8))]


def planter():
    """Raised shrub bed with a small tree: the islands that break up a big lot."""
    parts = [box(CONCRETE, (-2.0, 0.0, -1.1), (2.0, 0.35, 1.1), 0.06), box(SOIL, (-1.85, 0.3, -0.95), (1.85, 0.38, 0.95), 0)]
    for i, (x, z) in enumerate([(-1.3, -0.4), (-0.6, 0.45), (0.7, -0.3), (1.35, 0.4), (0.0, 0.0)]):
        parts.append(ball(LEAF if i % 2 else LEAF2, (x, 0.6, z), (0.5, 0.4, 0.45), 8, 5))
    parts.append(cyl(TRUNK, (0.1, 1.5, 0.1), "y", 0.09, 2.4, 0.01, 6))
    parts += [ball(LEAF, (0.1, 3.0, 0.1), (1.0, 0.85, 1.0), 9, 6), ball(LEAF2, (0.5, 2.7, -0.3), (0.6, 0.5, 0.6), 8, 5)]
    return parts


def cart_corral():
    parts = []
    for z in (-1.0, 1.0):
        parts += [box(STEEL, (-2.5, 0.9, z - 0.03), (2.5, 0.96, z + 0.03)), box(STEEL, (-2.5, 0.45, z - 0.03), (2.5, 0.5, z + 0.03))]
        parts += [box(STEEL, (x - 0.04, 0.0, z - 0.04), (x + 0.04, 0.96, z + 0.04)) for x in (-2.5, 0.0, 2.5)]
    parts += [box(STEEL, (2.47, 0.9, -1.0), (2.53, 0.96, 1.0)), box(RED, (2.45, 1.0, -0.5), (2.55, 1.5, 0.5))]
    for i in range(3):   # nested trolleys
        x = -1.8 + i * 0.5
        parts += [box(STEEL, (x - 0.35, 0.35, -0.3), (x + 0.45, 0.95, 0.3), 0.02),
                  box(RED, (x - 0.45, 1.0, -0.3), (x - 0.4, 1.05, 0.3))]
    return parts


def pallets():
    parts = []
    for k in range(2):
        y = k * 0.16
        parts += [box(WOOD, (-0.6, y + 0.12, -0.5), (0.6, y + 0.16, 0.5), 0)]
        parts += [box(DARKWOOD, (-0.6, y, z - 0.05), (0.6, y + 0.12, z + 0.05), 0) for z in (-0.45, 0.0, 0.45)]
    parts += [box((0.78, 0.62, 0.42, 1), (-0.5, 0.32, -0.4), (0.1, 0.8, 0.2), 0.02), box((0.72, 0.56, 0.38, 1), (0.05, 0.32, -0.35), (0.5, 0.65, 0.35), 0.02)]
    return parts


def bollard():
    return [cyl(YELLOW, (0, 0.5, 0), "y", 0.12, 1.0, 0.03, 10), cyl(DARK, (0, 0.75, 0), "y", 0.125, 0.08, 0, 10)]


def bench():
    return [box(WOOD, (-0.9, 0.42, -0.2), (0.9, 0.47, 0.2)), box(WOOD, (-0.9, 0.55, -0.24), (0.9, 0.85, -0.2)),
            *[box(DARK, (x - 0.04, 0, -0.2), (x + 0.04, 0.45, 0.2)) for x in (-0.75, 0.75)]]


# ---- trees (origin at the base) ------------------------------------------------------------------

def tree_round():
    return [cone(TRUNK, (0, 0, 0), 0.22, 0.14, 2.6, 7),
            ball(LEAF, (0, 3.3, 0), (1.7, 1.45, 1.7), 10, 7), ball(LEAF2, (0.9, 2.8, 0.5), (1.1, 0.95, 1.1), 9, 6),
            ball(LEAF, (-0.8, 2.9, -0.6), (1.1, 0.95, 1.1), 9, 6), ball(LEAF, (0.2, 4.4, -0.2), (1.0, 0.85, 1.0), 9, 6)]


def tree_oak():
    parts = [cone(TRUNK, (0, 0, 0), 0.32, 0.2, 2.4, 7),
             beam(TRUNK, (0, 2.0, 0), (1.3, 3.1, 0.4), 0.18), beam(TRUNK, (0, 2.0, 0), (-1.1, 3.0, -0.6), 0.18)]
    for (x, y, z, r, c) in [(0, 3.7, 0, 2.2, LEAF), (1.6, 3.3, 0.8, 1.4, LEAF2), (-1.5, 3.2, -0.9, 1.5, LEAF),
                            (0.8, 4.4, -1.1, 1.3, LEAF2), (-0.7, 4.5, 1.0, 1.2, LEAF)]:
        parts.append(ball(c, (x, y, z), (r, r * 0.72, r), 10, 7))
    return parts


def tree_pine():
    parts = [cone(TRUNK, (0, 0, 0), 0.2, 0.12, 1.6, 6)]
    for i, (y, r, h) in enumerate([(1.1, 1.9, 2.4), (2.4, 1.5, 2.2), (3.6, 1.1, 2.0), (4.7, 0.7, 1.6)]):
        parts.append(cone(LEAF2 if i % 2 == 0 else LEAF, (0, y, 0), r, 0.05, h, 9))
    return parts


def tree_poplar():
    return [cone(BIRCH, (0, 0, 0), 0.14, 0.1, 3.0, 6),
            *[box(DARK, (-0.15, y, -0.02), (0.15, y + 0.06, 0.14), 0) for y in (0.6, 1.3, 2.1)],
            ball(LEAF, (0, 4.3, 0), (1.05, 2.3, 1.05), 10, 8), ball(LEAF2, (0.35, 3.2, 0.3), (0.8, 1.2, 0.8), 8, 6)]


def tree_blossom():
    pink, pink2 = (0.98, 0.7, 0.8, 1.0), (0.95, 0.58, 0.72, 1.0)
    parts = [cone(DARKWOOD, (0, 0, 0), 0.2, 0.12, 1.9, 7), beam(DARKWOOD, (0, 1.6, 0), (0.9, 2.5, 0.2), 0.12),
             beam(DARKWOOD, (0, 1.6, 0), (-0.8, 2.4, -0.3), 0.12)]
    for (x, y, z, r, c) in [(0, 2.8, 0, 1.4, pink), (1.0, 2.6, 0.3, 0.9, pink2), (-0.9, 2.5, -0.4, 0.95, pink2), (0.2, 3.5, 0.1, 0.9, pink)]:
        parts.append(ball(c, (x, y, z), (r, r * 0.8, r), 9, 6))
    return parts


def bush():
    return [ball(LEAF, (0, 0.55, 0), (0.9, 0.65, 0.85), 9, 6), ball(LEAF2, (0.55, 0.45, 0.3), (0.55, 0.45, 0.55), 8, 5),
            ball(LEAF, (-0.5, 0.45, -0.25), (0.6, 0.5, 0.6), 8, 5)]


PROPS = {"Fence": fence_picket, "FenceBoard": fence_board, "Hedge": hedge, "Mailbox": mailbox, "Bins": bins, "Swing": swing, "Trampoline": trampoline,
         "Shed": shed, "WashingLine": washing_line, "Pool": pool, "Bbq": bbq, "Flowerbed": flowerbed, "Gnome": gnome,
         "LotLamp": lot_lamp, "WheelStop": wheel_stop, "Dumpster": dumpster, "Planter": planter, "Corral": cart_corral,
         "Pallets": pallets, "Bollard": bollard, "Bench": bench,
         "Tree_Round": tree_round, "Tree_Oak": tree_oak, "Tree_Pine": tree_pine, "Tree_Poplar": tree_poplar,
         "Tree_Blossom": tree_blossom, "Bush": bush}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    only = argv[argv.index("--only") + 1] if "--only" in argv else None
    os.makedirs(OUT, exist_ok=True)
    for name, fn in list(HOUSES.items()) + list(PROPS.items()):
        if only and only not in name:
            continue
        clear_scene()
        obj = build_object(name, fn())
        export(obj, os.path.join(OUT, name + ".fbx"))
        print(f"[art] {name}: {len(obj.data.vertices)} verts")


if __name__ == "__main__":
    main()
