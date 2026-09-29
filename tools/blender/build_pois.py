"""
TAILED business buildings (Blender 5.x, headless): one distinctive, toy-like building per POI type,
so every location reads at a glance ("the diner with the giant coffee cup").

    blender -b -P tools/blender/build_pois.py [-- --only Diner]

Exports unity/Assets/Tailed/Resources/Art/Pois/Poi_<Type>.fbx (+ Prop_Canopy, Prop_Pump).

Local frame (Unity axes): x along the frontage, y up, +z towards the street; the front face is at
z = 0 and the building extends back to z = -depth. TownBuilder puts that front line where the old
box buildings' front was, so bays, forecourts and signs are unchanged. The brand colour is the
marker ACCENT (magenta), swapped per site at runtime; window glass carries vertex alpha 0.95 so the
toon shader lights it at night.
"""
import math, os, sys
sys.path.insert(0, os.path.dirname(__file__))
import bmesh  # noqa: E402
from mathutils import Matrix  # noqa: E402
from build_assets import Part, U, build_object, clear_scene, export, ROOT  # noqa: E402

OUT = os.path.join(ROOT, "unity", "Assets", "Tailed", "Resources", "Art", "Pois")

ACCENT = (1.0, 0.0, 1.0, 1.0)
WALL = (0.95, 0.93, 0.88, 1.0)
CREAM = (0.97, 0.9, 0.76, 1.0)
STONE = (0.86, 0.82, 0.72, 1.0)
STEEL = (0.78, 0.8, 0.84, 1.0)
CHROME = (0.88, 0.9, 0.93, 1.0)
DARK = (0.18, 0.18, 0.21, 1.0)
ROOF = (0.38, 0.38, 0.44, 1.0)
WOOD = (0.62, 0.43, 0.27, 1.0)
WINDOW = (0.3, 0.42, 0.58, 0.95)          # alpha 0.95 → lit at night (toon shader)
CLEAR = (0.55, 0.72, 0.85, 0.35)          # see-through glass (material slot 1, ToonGlass)
GLASS_GREEN = (0.55, 0.78, 0.7, 0.95)
RED = (0.88, 0.22, 0.2, 1.0)
YELLOW = (0.99, 0.82, 0.22, 1.0)
BLUE = (0.25, 0.52, 0.92, 1.0)
GREEN = (0.22, 0.72, 0.36, 1.0)
ORANGE = (0.98, 0.56, 0.16, 1.0)
PINK = (0.98, 0.55, 0.72, 1.0)
PURPLE = (0.62, 0.42, 0.9, 1.0)
WHITE = (0.97, 0.97, 0.96, 1.0)
BREAD = (0.86, 0.6, 0.3, 1.0)
TERRACOTTA = (0.78, 0.42, 0.28, 1.0)
LEAF = (0.3, 0.62, 0.3, 1.0)
TYRE = (0.12, 0.12, 0.13, 1.0)
GOLD = (1.0, 0.8, 0.22, 1.0)


def box(color, lo, hi, r=0.08, seg=2):
    return Part(color).box(lo, hi).bevel(r, seg)


def cyl(color, centre, axis, radius, depth, r=0.04, seg=20):
    return Part(color).cylinder(centre, axis, radius, depth, seg).bevel(r, 2)


def ball(color, centre, radii, seg=16, rings=10):
    return Part(color).sphere(centre, radii, seg, rings)


def cone(color, centre, r1, r2, h, seg=18):
    p = Part(color)
    res = bmesh.ops.create_cone(p.bm, cap_ends=True, cap_tris=False, segments=seg, radius1=r1, radius2=r2, depth=h)
    bmesh.ops.transform(p.bm, matrix=Matrix.Translation(U(centre[0], centre[1] + h * 0.5, centre[2])), verts=res["verts"])
    return p


def arch(color, x_half, z0, z1, y0, rise, segments=14, thickness=0.25):
    """Barrel roof running along x: a half-ellipse section from z0 to z1 sitting on y0."""
    zc, rz = (z0 + z1) * 0.5, (z1 - z0) * 0.5
    outer = [(zc + math.cos(math.pi * i / segments) * rz, y0 + math.sin(math.pi * i / segments) * rise) for i in range(segments + 1)]
    inner = [(zc + math.cos(math.pi * i / segments) * (rz - thickness), y0 + math.sin(math.pi * i / segments) * (rise - thickness)) for i in reversed(range(segments + 1))]
    return Part(color).prism(outer + inner, x_half).bevel(0.05, 1)


def gable(color, x_half, z0, z1, y0, rise, overhang=0.5):
    """Pitched roof running along x."""
    return Part(color).prism([(z1 + overhang, y0), (z0 - overhang, y0), ((z0 + z1) * 0.5, y0 + rise)], x_half + overhang).bevel(0.08, 2)


def shop_box(w, d, h, wall=ACCENT, r=0.25, hollow_front=0.0):
    """Main building volume with a chunky rounded parapet (the toy look)."""
    if hollow_front > 0:
        # A shallow shop room at the front (walls + roof), solid building behind it.
        return [box(wall, (-w / 2, 0, -d), (w / 2, h, -hollow_front), r, 3),
                box(wall, (-w / 2, 0, -hollow_front - 0.2), (-w / 2 + 0.4, h, 0), 0.1, 2),
                box(wall, (w / 2 - 0.4, 0, -hollow_front - 0.2), (w / 2, h, 0), 0.1, 2),
                box(wall, (-w / 2, 3.4, -hollow_front - 0.2), (w / 2, h, 0), 0.15, 2),
                box(ROOF, (-w / 2 + 0.3, h - 0.05, -d + 0.3), (w / 2 - 0.3, h + 0.08, -0.3), 0.04, 1)]
    return [box(wall, (-w / 2, 0, -d), (w / 2, h, 0), r, 3),
            box(ROOF, (-w / 2 + 0.3, h - 0.05, -d + 0.3), (w / 2 - 0.3, h + 0.08, -0.3), 0.04, 1)]


def shopfront(w, y0=0.5, y1=2.8, door=True):
    parts = [box(WINDOW, (-w / 2, y0, -0.05), (w / 2, y1, 0.06), 0.05, 1)]
    if door:
        parts.append(box(DARK, (-0.8, 0.05, -0.05), (0.8, 2.3, 0.09), 0.04, 1))
    return parts


def awning_stripes(w, y, depth, a, b, n=10):
    parts = []
    for i in range(n):
        x0 = -w / 2 + w * i / n
        parts.append(Part(a if i % 2 == 0 else b).prism([(0.0, y), (depth, y - 0.7), (depth, y - 0.85), (0.0, y - 0.15)], w / n / 2, x0 + w / n / 2).bevel(0.02, 1))
    return parts


def rooftop_unit(x, z, h):
    return [box(STEEL, (x - 1.2, h, z - 1), (x + 1.2, h + 1.1, z + 1), 0.12, 2),
            cyl(DARK, (x, h + 1.15, z), "y", 0.6, 0.12, 0.03)]


# ---- the businesses ----------------------------------------------------------------

def petrol():
    w, d, h = 12, 9, 4.2
    parts = shop_box(w, d, h, WALL) + shopfront(w - 2)
    parts += [box(ACCENT, (-w / 2 - 0.05, h - 1.0, -0.2), (w / 2 + 0.05, h - 0.2, 0.15), 0.1, 2)]          # brand fascia
    parts += [box(RED, (-1.4, 0.0, 0.2), (-0.3, 1.2, 1.2), 0.1, 2), box(WHITE, (-1.35, 1.2, 0.25), (-0.35, 1.3, 1.15), 0.03, 1)]  # ice chest
    for i in range(3):
        parts.append(cyl(TYRE, (w / 2 + 0.9, 0.2 + i * 0.3, -1.5), "y", 0.4, 0.28, 0.08))
    return parts


def canopy():
    """Gas-station canopy, 10 m long along x (stretched to the bays), centred on the bay row."""
    parts = [box(WHITE, (-5, 4.6, -4.5), (5, 5.5, 4.5), 0.3, 3),
             box(ACCENT, (-5.05, 4.75, -4.55), (5.05, 5.15, 4.55), 0.12, 2)]
    return parts


def pump():
    parts = [box(DARK, (-0.7, 0.0, -0.45), (0.7, 0.25, 0.45), 0.08, 2),               # island kerb
             box(WHITE, (-0.35, 0.25, -0.3), (0.35, 1.9, 0.3), 0.12, 3),              # pump body
             box(ACCENT, (-0.36, 1.35, -0.31), (0.36, 1.75, 0.31), 0.06, 2),          # brand band
             box(DARK, (-0.25, 0.9, 0.29), (0.25, 1.25, 0.34), 0.03, 1),              # display
             cyl(DARK, (0.4, 1.1, 0.0), "x", 0.07, 0.18, 0.02, 10)]                  # nozzle
    return parts


def diner():
    w, d, h = 22, 11, 3.6
    parts = [box(CHROME, (-w / 2, 0.6, -d), (w / 2, h, 0), 0.35, 3)]
    for i in range(int(w / 0.8)):                                                    # checkerboard base
        x0 = -w / 2 + i * 0.8
        parts.append(box(DARK if i % 2 else WHITE, (x0, 0.0, -0.3), (x0 + 0.8, 0.6, 0.05), 0.02, 1))
    parts.append(box(DARK, (-w / 2, 0.0, -d), (w / 2, 0.6, -0.3), 0.05, 1))
    parts.append(box(ACCENT, (-w / 2 - 0.05, 1.0, -d - 0.05), (w / 2 + 0.05, 1.35, 0.1), 0.08, 2))    # stripe
    for i in range(7):                                                               # window row
        x = -w / 2 + 1.8 + i * 2.9
        parts.append(box(WINDOW, (x, 1.6, -0.1), (x + 2.3, 3.0, 0.08), 0.12, 2))
    parts.append(arch(ACCENT, w / 2 + 0.2, -d - 0.3, 0.3, h, 1.8))                  # barrel roof
    parts.append(box(CHROME, (-1.5, 0.0, 0.0), (1.5, 3.2, 1.8), 0.25, 3))           # vestibule
    parts.append(box(WINDOW, (-0.9, 0.1, 1.75), (0.9, 2.6, 1.86), 0.05, 1))
    # Rooftop sign: a big disc with a giant coffee cup.
    parts.append(box(DARK, (-0.15, h + 1.5, -d / 2 - 0.15), (0.15, h + 3.4, -d / 2 + 0.15), 0.05, 1))
    parts.append(cyl(ACCENT, (0, h + 4.6, -d / 2), "z", 1.7, 0.35, 0.12, 28))
    parts.append(cyl(WHITE, (0, h + 4.6, -d / 2 + 0.2), "z", 1.35, 0.1, 0.05, 28))
    parts.append(cyl(WHITE, (0, h + 6.7, -d / 2), "y", 0.75, 1.3, 0.12, 22))       # cup
    parts.append(cyl(BREAD, (0, h + 7.33, -d / 2), "y", 0.62, 0.05, 0.0, 22))      # coffee
    parts.append(cyl(WHITE, (0.95, h + 6.8, -d / 2), "z", 0.38, 0.14, 0.06, 16))    # handle
    return parts


def laundromat():
    w, d, h = 22, 12, 5
    # See-through glass (the washers should show), set into the wall.
    parts = shop_box(w, d, h, hollow_front=2.3) + [Part(CLEAR, 1).box((-w / 2 + 0.75, 0.4, 0.02), (w / 2 - 0.75, 3.4, 0.08)).bevel(0.02, 1),
                                 box(DARK, (-0.8, 0.05, 0.02), (0.8, 2.3, 0.1), 0.04, 1)]
    parts.append(box(DARK, (-w / 2 + 0.6, 0.3, -2.3), (w / 2 - 0.6, 3.5, -2.2), 0.02, 1))   # back of the washer room
    for i in range(7):                                                               # washers behind the glass
        x = -w / 2 + 2.2 + i * 2.8
        parts.append(box(WHITE, (x - 0.7, 0.0, -1.9), (x + 0.7, 1.3, -0.5), 0.15, 3))
        parts.append(cyl(DARK, (x, 0.75, -0.46), "z", 0.42, 0.08, 0.03, 20))
        parts.append(cyl(BLUE, (x, 0.75, -0.41), "z", 0.3, 0.04, 0.0, 18))
    # Giant T-shirt and bubbles on the roof.
    parts.append(box(WHITE, (-1.6, h + 0.5, -d / 2 - 0.2), (1.6, h + 3.6, -d / 2 + 0.2), 0.3, 3))
    for side in (-1, 1):
        parts.append(box(WHITE, (side * 1.6 - (1.1 if side > 0 else 0), h + 2.6, -d / 2 - 0.2), (side * 1.6 + (1.1 if side < 0 else 0) * 0 + (1.1 if side > 0 else 0), h + 3.6, -d / 2 + 0.2), 0.25, 3))
    for i, (x, y, r) in enumerate([(-4.5, 0.9, 0.7), (-3.4, 1.5, 0.45), (3.8, 1.0, 0.8), (4.9, 1.8, 0.5), (2.9, 2.0, 0.35)]):
        parts.append(ball((0.85, 0.95, 1.0, 1.0), (x, h + y, -d / 2), (r, r, r)))
    return parts


def carwash():
    w, d, h = 24, 10, 5.2
    parts = [box(ACCENT, (-w / 2, 0, -d), (w / 2, h, -d + 0.8), 0.25, 3),            # back wall
             box(ACCENT, (-w / 2, 0, -0.8), (w / 2, 1.2, 0), 0.2, 2),                # low front wall
             box(WHITE, (-w / 2 - 0.2, h, -d - 0.2), (w / 2 + 0.2, h + 0.6, 0.2), 0.25, 3)]
    for x in (-w / 2, w / 2):                                                        # end pillars (open tunnel ends)
        parts.append(box(ACCENT, (x - 0.5, 0, -d), (x + 0.5, h, 0), 0.2, 2))
    for i in range(12):                                                              # wavy trim of bubbles
        parts.append(ball(BLUE if i % 2 else WHITE, (-w / 2 + 1 + i * 2, h + 0.7, 0.1), (0.7, 0.55, 0.4)))
    for x, col in ((-4.0, BLUE), (-2.4, RED), (2.4, BLUE), (4.0, RED)):              # brushes inside
        parts.append(cyl(col, (x, 2.2, -d / 2), "y", 0.75, 3.6, 0.3, 20))
    parts.append(cyl(YELLOW, (0, 4.2, -d / 2), "x", 0.6, 7.0, 0.25, 20))             # top brush
    return parts


def pharmacy():
    w, d, h = 22, 12, 5.5
    parts = shop_box(w, d, h, WALL) + shopfront(w - 2, 0.4, 3.2)
    parts.append(box(ACCENT, (-w / 2 - 0.05, 3.6, -d - 0.05), (w / 2 + 0.05, 4.3, 0.12), 0.1, 2))
    cx, cy = w / 2 - 2.2, h + 1.9                                                     # big green cross
    parts.append(box(GREEN, (cx - 0.45, cy - 1.4, -0.4), (cx + 0.45, cy + 1.4, 0.2), 0.2, 3))
    parts.append(box(GREEN, (cx - 1.4, cy - 0.45, -0.4), (cx + 1.4, cy + 0.45, 0.2), 0.2, 3))
    parts.append(box(DARK, (cx - 0.15, h, -0.25), (cx + 0.15, cy - 1.3, 0.05), 0.04, 1))
    parts += rooftop_unit(-4, -d / 2, h)
    return parts


def bakery():
    w, d, h = 18, 11, 4.2
    parts = [box(ACCENT, (-w / 2, 0, -d), (w / 2, h, 0), 0.25, 3)] + shopfront(w - 3, 0.6, 2.6)
    parts.append(gable((0.62, 0.34, 0.24, 1.0), w / 2, -d, 0, h, 3.2))
    parts.append(box((0.7, 0.4, 0.3, 1.0), (w / 2 - 3, h + 1.0, -d / 2 - 1), (w / 2 - 1.8, h + 4.2, -d / 2 + 0.2), 0.1, 2))   # chimney
    parts += awning_stripes(w - 2, 3.4, 1.4, WHITE, RED)
    # A giant loaf on the ridge, with slashes.
    parts.append(ball(BREAD, (0, h + 3.6, -d / 2), (2.6, 1.15, 1.4), 24, 12))
    for i in range(4):
        parts.append(ball(CREAM, (-1.5 + i * 1.0, h + 4.65, -d / 2 + 0.2), (0.28, 0.1, 0.55), 10, 6))
    parts.append(box(WOOD, (-w / 2 + 1, 0, 1.6), (-w / 2 + 3.2, 0.5, 2.1), 0.08, 2))   # bench
    return parts


def florist():
    w, d, h = 18, 10, 3.8
    parts = [box(WALL, (-w / 2, 0, -d), (-w / 2 + 7, h, 0), 0.25, 3)] + [box(WINDOW, (-w / 2 + 0.5, 0.5, -0.05), (-w / 2 + 6.5, 2.8, 0.06), 0.05, 1)]
    parts.append(box(ACCENT, (-w / 2 - 0.05, h - 0.9, -0.2), (-w / 2 + 7.05, h - 0.2, 0.14), 0.1, 2))
    # Arched greenhouse.
    parts.append(box(WALL, (-w / 2 + 7, 0, -d), (w / 2, 1.2, 0), 0.15, 2))
    gh = arch(GLASS_GREEN, (w - 7) / 2, -d, 0, 1.2, 3.4, 12, 0.08)
    bmesh.ops.translate(gh.bm, vec=U((w / 2 + (-w / 2 + 7)) / 2, 0, 0), verts=gh.bm.verts)
    parts.append(gh)
    # Flower stands: tiered benches full of colour.
    palette = [RED, YELLOW, PINK, PURPLE, WHITE, ORANGE]
    for s, x0 in enumerate((-w / 2 + 1, 1.5)):
        for tier in range(3):
            y = 0.4 + tier * 0.4
            z = 1.6 - tier * 0.45
            parts.append(box(WOOD, (x0, y - 0.1, z - 0.2), (x0 + 5, y, z + 0.2), 0.03, 1))
            for k in range(6):
                parts.append(ball(palette[(k + tier + s) % len(palette)], (x0 + 0.45 + k * 0.82, y + 0.22, z), (0.28, 0.24, 0.28), 10, 6))
    for x in (-w / 2 + 7.5, w / 2 - 0.8):                                            # potted shrubs
        parts.append(cyl(TERRACOTTA, (x, 0.35, 0.8), "y", 0.4, 0.7, 0.08, 14))
        parts.append(ball(LEAF, (x, 1.2, 0.8), (0.6, 0.6, 0.6), 12, 8))
    return parts


def hardware():
    w, d, h = 26, 14, 6
    parts = shop_box(w, d, h) + [box(ORANGE, (-w / 2 - 0.05, h - 1.2, -d - 0.05), (w / 2 + 0.05, h - 0.5, 0.12), 0.1, 2)]
    parts += [box(WINDOW, (-w / 2 + 1, 0.5, -0.05), (-2, 3.0, 0.06), 0.05, 1), box(STEEL, (1, 0.0, -0.05), (w / 2 - 1, 4.2, 0.08), 0.06, 1)]
    for i in range(8):                                                               # roller door slats
        parts.append(box(ROOF, (1.1, 0.3 + i * 0.5, 0.07), (w / 2 - 1.1, 0.36 + i * 0.5, 0.1)))
    # Giant hammer on the roof.
    parts.append(box(WOOD, (-0.3, h + 0.3, -d / 2 - 0.3), (0.3, h + 4.5, -d / 2 + 0.3), 0.15, 2))
    parts.append(box(STEEL, (-1.6, h + 4.2, -d / 2 - 0.55), (1.6, h + 5.3, -d / 2 + 0.55), 0.2, 3))
    # Lumber stacks out front.
    for i in range(3):
        for j in range(3 - i):
            parts.append(box(BREAD, (-w / 2 + 1.2 + j * 1.1 + i * 0.55, i * 0.35, 1.2), (-w / 2 + 2.1 + j * 1.1 + i * 0.55, i * 0.35 + 0.3, 5.2), 0.03, 1))
    return parts


def bank():
    w, d, h = 22, 12, 7
    parts = [box(STONE, (-w / 2, 0.9, -d), (w / 2, h, -1.6), 0.15, 2)]
    for i in range(3):                                                               # steps
        parts.append(box(STONE, (-w / 2 + 1 + i * 0.4, i * 0.3, -1.6 + 1.2 - i * 0.4), (w / 2 - 1 - i * 0.4, (i + 1) * 0.3, 1.2 - i * 0.4), 0.05, 1))
    for i in range(6):                                                               # columns
        x = -w / 2 + 2 + i * (w - 4) / 5
        parts.append(cyl(WHITE, (x, 0.9 + (h - 0.9) / 2, -0.6), "y", 0.45, h - 0.9, 0.1, 18))
        parts.append(box(WHITE, (x - 0.6, h - 0.3, -1.2), (x + 0.6, h, 0.0), 0.05, 1))
    parts.append(box(WHITE, (-w / 2 - 0.3, h, -d), (w / 2 + 0.3, h + 0.7, 0.3), 0.1, 2))
    parts.append(Part(STONE).prism([(0.3, h + 0.7), (-d, h + 0.7), (-d / 2, h + 3.2)], w / 2 + 0.3).bevel(0.1, 2))
    parts.append(Part(WHITE).prism([(0.35, h + 0.7), (0.0, h + 0.7), (0.0, h + 3.0), (0.35, h + 0.95)], w / 2 + 0.2).bevel(0.05, 1))
    parts.append(cyl(GOLD, (0, h + 1.6, 0.4), "z", 0.8, 0.2, 0.06, 24))              # gold medallion
    parts.append(box(DARK, (-1.2, 0.9, -1.7), (1.2, 3.6, -1.5), 0.05, 1))            # door
    for x in (-6, 6):
        parts.append(box(WINDOW, (x - 1.2, 2.2, -1.7), (x + 1.2, 5.2, -1.5), 0.08, 1))
    return parts


def motel():
    w, d, h = 34, 9, 6.4
    parts = [box(ACCENT, (-w / 2, 0, -d), (w / 2 - 6, h, -1.5), 0.2, 3)]
    parts.append(box(DARK, (-w / 2 - 0.2, h, -d - 0.2), (w / 2 - 6 + 0.2, h + 0.4, -1.2), 0.1, 2))   # roof
    parts.append(box(WHITE, (-w / 2, 3.1, -1.5), (w / 2 - 6, 3.35, 0.2), 0.05, 1))   # walkway
    parts.append(box(WHITE, (-w / 2, 3.35, 0.1), (w / 2 - 6, 4.3, 0.2), 0.03, 1))    # railing
    for i in range(int((w - 6) / 1.6) + 1):
        parts.append(box(WHITE, (-w / 2 + i * 1.6, 0.0, 0.05), (-w / 2 + i * 1.6 + 0.15, 3.1, 0.2), 0.03, 1))
    doors = [BLUE, YELLOW, RED, GREEN]
    for floor, y0 in enumerate((0.0, 3.35)):
        for i in range(7):
            x = -w / 2 + 1.5 + i * 3.8
            parts.append(box(doors[(i + floor) % 4], (x, y0, -1.6), (x + 1.0, y0 + 2.2, -1.45), 0.03, 1))
            parts.append(box(WINDOW, (x + 1.4, y0 + 1.0, -1.6), (x + 2.8, y0 + 2.2, -1.45), 0.05, 1))
    for i in range(6):                                                               # stairs
        parts.append(box(WHITE, (w / 2 - 7.2, i * 0.52, -0.2 - i * 0.4), (w / 2 - 6.2, i * 0.52 + 0.2, 0.2 - i * 0.4)))
    parts += [box(WALL, (w / 2 - 6, 0, -d), (w / 2, 3.6, 0), 0.25, 3), box(WINDOW, (w / 2 - 5.5, 0.5, -0.05), (w / 2 - 0.5, 2.8, 0.06), 0.05, 1)]
    # Vintage arrow sign.
    parts.append(box(DARK, (w / 2 - 2.2, 3.6, -1.2), (w / 2 - 1.8, 9.8, -0.8), 0.05, 1))
    parts.append(box(ACCENT, (w / 2 - 4.5, 7.2, -1.3), (w / 2 + 0.5, 9.4, -0.7), 0.3, 3))
    parts.append(Part(YELLOW).prism([(-0.7, 6.4), (-1.3, 6.4), (-1.0, 5.4)], 0.6, w / 2 - 2.0).bevel(0.05, 1))
    parts.append(box(STEEL, (-w / 2 - 1.6, 0, -1.2), (-w / 2 - 0.4, 1.9, -0.2), 0.15, 2))   # vending machine
    return parts


def warehouse():
    w, d, h = 30, 22, 7
    parts = [box(ACCENT, (-w / 2, 0, -d), (w / 2, h, 0), 0.2, 3), arch(ROOF, w / 2 + 0.3, -d - 0.3, 0.3, h, 3.5, 16, 0.3)]
    for i in range(3):                                                               # loading docks
        x = -w / 2 + 5 + i * 10
        parts.append(box(STEEL, (x - 2, 1.2, -0.05), (x + 2, 5.0, 0.08), 0.05, 1))
        for k in range(7):
            parts.append(box(ROOF, (x - 1.9, 1.4 + k * 0.5, 0.07), (x + 1.9, 1.45 + k * 0.5, 0.1)))
        parts.append(box(DARK, (x - 2.3, 0.0, 0.0), (x + 2.3, 1.2, 1.0), 0.08, 2))
        for side in (-1, 1):
            parts.append(box(YELLOW, (x + side * 2.1 - 0.15, 0.7, 0.95), (x + side * 2.1 + 0.15, 1.2, 1.15), 0.04, 1))
    for i in range(4):                                                               # pallets and crates
        parts.append(box(WOOD, (-w / 2 + 1.5 + i * 1.5, 0, 3), (-w / 2 + 2.7 + i * 1.5, 0.15, 4.2), 0.02, 1))
        if i % 2 == 0:
            parts.append(box(BREAD, (-w / 2 + 1.6 + i * 1.5, 0.15, 3.1), (-w / 2 + 2.6 + i * 1.5, 1.1, 4.1), 0.06, 1))
    return parts


def depot():
    w, d, h = 30, 22, 6.5
    parts = [box(ACCENT, (-w / 2, 0, -d), (w / 2, h, 0), 0.2, 3), gable(ROOF, w / 2, -d, 0, h, 3.0, 0.4)]
    for i in range(3):                                                               # open garage bays
        x = -w / 2 + 5.5 + i * 9.5
        parts.append(box(DARK, (x - 3, 0.0, -0.05), (x + 3, 4.8, 0.06), 0.1, 2))
        parts.append(box(YELLOW, (x - 3.2, 4.8, -0.1), (x + 3.2, 5.1, 0.1), 0.05, 1))
    parts.append(cyl(WHITE, (w / 2 + 2.5, 2.2, -d / 2), "z", 1.5, 7.0, 0.5, 24))    # fuel tank
    for z in (-d / 2 - 2.5, -d / 2 + 2.5):
        parts.append(box(DARK, (w / 2 + 1.3, 0, z - 0.2), (w / 2 + 3.7, 0.9, z + 0.2)))
    for k in range(5):                                                               # cones
        parts.append(cone(ORANGE, (-w / 2 + 2 + k * 1.2, 0, 2.0), 0.28, 0.04, 0.75, 12))
    return parts


def scrapyard():
    w, d = 30, 20
    parts = [box(ACCENT, (w / 2 - 7, 0, -6), (w / 2, 3.2, 0), 0.2, 3), box(WINDOW, (w / 2 - 6.5, 1.0, -0.05), (w / 2 - 3.5, 2.4, 0.06), 0.05, 1),
             box(ROOF, (w / 2 - 7.3, 3.2, -6.3), (w / 2 + 0.3, 3.5, 0.3), 0.05, 1)]
    for x0 in range(-15, 15, 3):                                                     # fence along the back
        parts.append(box((0.6, 0.62, 0.64, 1.0), (x0, 0, -d), (x0 + 2.9, 2.4, -d + 0.1), 0.03, 1))
    crushed = [RED, BLUE, YELLOW, GREEN, (0.9, 0.9, 0.9, 1.0), ORANGE, PURPLE]
    for pile, (px, pz) in enumerate(((-10, -12), (-3, -14), (4, -11))):              # crushed-car stacks
        for layer in range(4):
            for k in range(3 - layer // 2):
                c = crushed[(pile * 5 + layer * 2 + k) % len(crushed)]
                x = px + k * 2.1 + layer * 0.35
                parts.append(box(c, (x - 1.0, layer * 0.75, pz - 0.8), (x + 1.0, layer * 0.75 + 0.7, pz + 0.8), 0.12, 2))
    # Crane with a magnet.
    parts.append(box(YELLOW, (-13.5, 0, -4.5), (-12.5, 9, -3.5), 0.15, 2))
    boom = Part(YELLOW).box((-0.4, -0.4, 0), (0.4, 0.4, 9)).bevel(0.12, 2)
    bmesh.ops.transform(boom.bm, matrix=Matrix.Translation(U(-13, 9, -4)) @ Matrix.Rotation(math.radians(-35), 4, "X").transposed(), verts=boom.bm.verts)
    parts.append(boom)
    parts.append(box(DARK, (-13.05, 5.5, 2.7), (-12.95, 14.0, 2.8)))                 # cable
    parts.append(cyl(DARK, (-13, 5.3, 2.75), "y", 1.0, 0.4, 0.1, 20))                # magnet
    for i in range(4):                                                               # tyre piles
        for j in range(3):
            parts.append(cyl(TYRE, (6 + i * 1.3, 0.2 + j * 0.32, 2.5), "y", 0.5, 0.28, 0.1, 16))
    return parts


def rentallot():
    parts = [box(ACCENT, (-5, 0, -6), (5, 3.4, 0), 0.25, 3), box(WHITE, (-5.3, 3.4, -6.3), (5.3, 3.8, 0.3), 0.15, 2),
             box(WINDOW, (-4.2, 0.6, -0.05), (1.5, 2.6, 0.06), 0.05, 1), box(DARK, (2.2, 0.05, -0.05), (3.6, 2.3, 0.08), 0.04, 1)]
    # Flag poles with bunting strung between them.
    poles = [(-14, 8.0), (-5, 8.0), (5, 8.0), (14, 8.0)]
    for x, z in poles:
        parts.append(cyl(WHITE, (x, 3.2, z), "y", 0.08, 6.4, 0.02, 10))
    flags = [RED, YELLOW, BLUE, GREEN, ORANGE, PINK]
    for (xa, za), (xb, zb) in zip(poles, poles[1:]):
        for k in range(8):
            t = (k + 0.5) / 8
            x = xa + (xb - xa) * t
            sag = 5.9 - math.sin(t * math.pi) * 0.8
            parts.append(Part(flags[k % len(flags)]).prism([(za + 0.02, sag), (za - 0.02, sag), (za, sag - 0.55)], 0.28, x).bevel(0.0))
    return parts


def chopshop():
    w, d, h = 22, 14, 6
    parts = shop_box(w, d, h, ACCENT) + [box(DARK, (-w / 2 + 1, 0, -0.05), (-1, 4.4, 0.06), 0.1, 2), box(DARK, (1, 0, -0.05), (w / 2 - 1, 4.4, 0.06), 0.1, 2)]
    # A car up on a lift in the left bay (primer grey), and the lift posts.
    # A primer-grey car up on a two-post lift out on the apron.
    for side in (-1, 1):
        parts.append(box(YELLOW, (-5.5 + side * 1.5 - 0.18, 0, 2.0), (-5.5 + side * 1.5 + 0.18, 2.6, 2.4), 0.05, 1))
        parts.append(box(YELLOW, (-5.5 + side * 1.5 - 0.1, 1.35, 1.2), (-5.5 + side * 1.5 + 0.1, 1.5, 4.8), 0.03, 1))
    parts.append(box((0.55, 0.57, 0.6, 1.0), (-6.4, 1.5, 0.9), (-4.6, 2.35, 5.1), 0.3, 3))
    parts.append(box((0.55, 0.57, 0.6, 1.0), (-6.25, 2.35, 1.8), (-4.75, 2.95, 3.9), 0.25, 3))
    for zz in (1.5, 4.5):
        for side in (-1, 1):
            parts.append(cyl(TYRE, (-5.5 + side * 0.95, 1.55, zz), "x", 0.33, 0.25, 0.08, 16))
    # Giant wrench on the roof.
    parts.append(box(STEEL, (-3.2, h + 1.0, -d / 2 - 0.25), (2.4, h + 1.7, -d / 2 + 0.25), 0.2, 3))
    parts.append(cyl(STEEL, (3.1, h + 1.35, -d / 2), "z", 1.0, 0.5, 0.15, 20))
    parts.append(cyl(DARK, (3.5, h + 1.35, -d / 2), "z", 0.5, 0.6, 0.05, 16))
    for i in range(4):
        parts.append(cyl(TYRE, (w / 2 + 1, 0.2 + i * 0.3, -2), "y", 0.45, 0.28, 0.1))
    return parts


BUILDERS = {
    "Petrol": petrol, "Diner": diner, "Laundromat": laundromat, "CarWash": carwash, "Pharmacy": pharmacy,
    "Bakery": bakery, "Florist": florist, "Hardware": hardware, "Bank": bank, "Motel": motel,
    "Warehouse": warehouse, "Depot": depot, "ScrapYard": scrapyard, "RentalLot": rentallot, "ChopShop": chopshop,
}
def column():
    return [cyl(WHITE, (0, 2.3, 0), "y", 0.22, 4.6, 0.06, 16), box(ACCENT, (-0.24, 3.8, -0.24), (0.24, 4.2, 0.24), 0.05, 1)]


PROPS = {"Prop_Canopy": canopy, "Prop_Pump": pump, "Prop_Column": column}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    only = argv[argv.index("--only") + 1] if "--only" in argv else None
    items = [("Poi_" + k, f) for k, f in BUILDERS.items()] + list(PROPS.items())
    for name, fn in items:
        if only and only not in name:
            continue
        clear_scene()
        obj = build_object(name, fn())
        export(obj, os.path.join(OUT, name + ".fbx"))
        print(f"[art] {name}: {len(obj.data.vertices)} verts")


if __name__ == "__main__":
    main()
