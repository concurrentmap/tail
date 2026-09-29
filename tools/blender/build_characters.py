"""
TAILED character kit (Blender 5.x, headless) — the PEAK approach: one base character, dressed per
identity with swappable parts.

    blender -b -P tools/blender/build_characters.py

Exports one FBX per part into unity/Assets/Tailed/Resources/Art/Characters:
  DriverBase            pill body + big round head (+ ears)
  Eyes0..3              round · sleepy · dots · wide with brows
  Mouth0..3             smile · "O" · flat · toothy grin
  Hat1..8               cap · beanie · top hat · cowboy · party · crown · bucket · headband (HatType)
  Glasses1..3           round · sunglasses · chunky (GlassesType)

Marker colours are swapped at runtime (VehicleMeshFactory.Driver): SKIN, SHIRT, HAT (+ HAT_ACCENT, a
darker shade). Frame: Unity space, origin at the driver's seat pivot (head centre ~0.53 m up,
facing +z), the same frame the old procedural bean used, so car placement is unchanged.
"""
import math, os, sys
sys.path.insert(0, os.path.dirname(__file__))
from build_assets import Part, build_object, clear_scene, export, ROOT  # noqa: E402

OUT = os.path.join(ROOT, "unity", "Assets", "Tailed", "Resources", "Art", "Characters")

SKIN = (0.0, 1.0, 0.0, 1.0)
SHIRT = (0.0, 1.0, 1.0, 1.0)
HAT = (1.0, 1.0, 0.0, 1.0)
HAT_ACCENT = (0.0, 0.0, 1.0, 1.0)
WHITE = (0.97, 0.97, 0.97, 1.0)
INK = (0.08, 0.08, 0.10, 1.0)
MOUTH = (0.35, 0.13, 0.12, 1.0)
GOLD = (1.0, 0.8, 0.2, 1.0)
LENS_DARK = (0.12, 0.14, 0.18, 1.0)
LENS = (0.75, 0.88, 0.95, 1.0)

HEAD_C = (0.0, 0.53, 0.0)
HEAD_R = 0.19


def on_face(x, y, lift=0.004):
    """Point on the front of the head surface at (x, y), nudged outwards."""
    cx, cy, _ = HEAD_C
    dz = HEAD_R * HEAD_R - (x - cx) ** 2 - (y - cy) ** 2
    return (x, y, math.sqrt(max(dz, 0.0)) + lift)


def base():
    return [
        Part(SHIRT).sphere((0, 0.1, 0), (0.25, 0.3, 0.21), 24, 14),          # pill body
        Part(SKIN).sphere((0, 0.36, 0), (0.08, 0.06, 0.08), 12, 8),          # neck
        Part(SKIN).sphere(HEAD_C, (HEAD_R, HEAD_R * 1.02, HEAD_R), 28, 18),  # big round head
        Part(SKIN).sphere((-HEAD_R * 0.98, 0.52, 0), (0.035, 0.05, 0.03), 10, 8),
        Part(SKIN).sphere((HEAD_R * 0.98, 0.52, 0), (0.035, 0.05, 0.03), 10, 8),
    ]


def eyes(style):
    parts = []
    for side in (-1, 1):
        x = side * 0.07
        if style == 2:  # dots
            parts.append(Part(INK).sphere(on_face(x, 0.555, 0.0), (0.024, 0.03, 0.014), 12, 8))
            continue
        big = 1.2 if style == 3 else 1.0
        white = on_face(x, 0.56, -0.004)
        parts.append(Part(WHITE).sphere(white, (0.05 * big, 0.06 * big, 0.024), 16, 10))
        pupil = on_face(x + side * 0.004, 0.553, 0.012)
        parts.append(Part(INK).sphere(pupil, (0.026 * big, 0.032 * big, 0.012), 12, 8))
        parts.append(Part(WHITE).sphere(on_face(x + 0.012, 0.568, 0.02), (0.009, 0.009, 0.006), 8, 6))
        if style == 1:  # sleepy: skin-coloured lids over the top half
            parts.append(Part(SKIN).sphere(on_face(x, 0.585, 0.0), (0.057, 0.034, 0.03), 14, 8))
        if style == 3:  # brows, raised
            p = Part(INK).sphere(on_face(x, 0.645, 0.004), (0.045, 0.011, 0.012), 12, 6)
            parts.append(p)
    return parts


def mouth(style):
    if style == 0:  # smile: an arc of beads
        return [Part(MOUTH).sphere(on_face(math.sin(a) * 0.065, 0.465 - math.cos(a) * 0.022 + 0.022, 0.004), (0.013, 0.013, 0.009), 8, 6)
                for a in [(-0.9 + 1.8 * k / 8) for k in range(9)]]
    if style == 1:  # "O"
        return [Part(MOUTH).sphere(on_face(0, 0.46, 0.002), (0.028, 0.034, 0.014), 14, 8)]
    if style == 2:  # flat
        return [Part(MOUTH).sphere(on_face(0, 0.462, 0.002), (0.05, 0.009, 0.01), 12, 6)]
    # toothy grin: dark mouth with a white band of teeth
    return [Part(MOUTH).sphere(on_face(0, 0.458, 0.0), (0.068, 0.034, 0.016), 16, 8),
            Part(WHITE).sphere(on_face(0, 0.47, 0.01), (0.055, 0.013, 0.01), 14, 6)]


def hat(kind):
    top = HEAD_C[1] + HEAD_R * 0.78
    if kind == 1:  # cap: dome + forward brim + button
        return [Part(HAT).sphere((0, top - 0.04, 0), (0.2, 0.13, 0.2), 20, 10).bevel(0),
                Part(HAT).cylinder((0, top - 0.06, 0.2), "y", 0.12, 0.025, 20).bevel(0.01, 2),
                Part(HAT_ACCENT).sphere((0, top + 0.09, 0), (0.025, 0.02, 0.025), 8, 6)]
    if kind == 2:  # beanie: dome, folded rim, pom-pom
        return [Part(HAT).sphere((0, top - 0.03, 0), (0.205, 0.16, 0.205), 20, 12),
                Part(HAT_ACCENT).cylinder((0, top - 0.09, 0), "y", 0.21, 0.07, 24).bevel(0.025, 3),
                Part(WHITE).sphere((0, top + 0.14, 0), (0.055, 0.055, 0.055), 12, 8)]
    if kind == 3:  # top hat
        return [Part(INK).cylinder((0, top - 0.02, 0), "y", 0.27, 0.025, 28).bevel(0.01, 2),
                Part(INK).cylinder((0, top + 0.15, 0), "y", 0.14, 0.32, 24).bevel(0.02, 2),
                Part(HAT).cylinder((0, top + 0.03, 0), "y", 0.145, 0.05, 24)]
    if kind == 4:  # cowboy: wide brim, pinched crown
        return [Part(HAT).cylinder((0, top - 0.03, 0), "y", 0.33, 0.03, 32).bevel(0.012, 2),
                Part(HAT).sphere((0, top + 0.05, 0), (0.15, 0.13, 0.13), 18, 10),
                Part(HAT_ACCENT).cylinder((0, top + 0.0, 0), "y", 0.152, 0.035, 24)]
    if kind == 5:  # party hat: cone + pom-pom
        p = Part(HAT)
        import bmesh
        from mathutils import Matrix
        from build_assets import U
        res = bmesh.ops.create_cone(p.bm, cap_ends=True, cap_tris=False, segments=20, radius1=0.12, radius2=0.01, depth=0.3)
        bmesh.ops.transform(p.bm, matrix=Matrix.Translation(U(0, top + 0.12, 0)), verts=res["verts"])
        return [p, Part(HAT_ACCENT).sphere((0, top + 0.28, 0), (0.04, 0.04, 0.04), 10, 6)]
    if kind == 6:  # crown: gold band with points
        parts = [Part(GOLD).cylinder((0, top + 0.03, 0), "y", 0.16, 0.1, 20).bevel(0.01, 1)]
        for k in range(6):
            a = k * math.pi / 3
            parts.append(Part(GOLD).sphere((math.cos(a) * 0.15, top + 0.11, math.sin(a) * 0.15), (0.03, 0.05, 0.03), 8, 6))
        return parts
    if kind == 7:  # bucket hat: soft crown, drooping brim
        return [Part(HAT).sphere((0, top - 0.01, 0), (0.2, 0.12, 0.2), 20, 10),
                Part(HAT).cylinder((0, top - 0.07, 0), "y", 0.28, 0.04, 28).bevel(0.018, 3)]
    if kind == 8:  # headband
        return [Part(HAT).cylinder((0, HEAD_C[1] + 0.07, 0), "y", HEAD_R * 1.02, 0.05, 28).bevel(0.015, 2)]
    return []


def glasses(kind):
    parts = []
    y = 0.56
    for side in (-1, 1):
        x = side * 0.072
        c = on_face(x, y, 0.03)
        if kind == 1:  # round wire frames
            parts.append(Part(INK).cylinder(c, "z", 0.052, 0.012, 20))
            parts.append(Part(LENS).cylinder((c[0], c[1], c[2] + 0.004), "z", 0.043, 0.012, 20))
        elif kind == 2:  # sunglasses
            parts.append(Part(LENS_DARK).box((c[0] - 0.058, c[1] - 0.04, c[2] - 0.01), (c[0] + 0.058, c[1] + 0.035, c[2] + 0.01)).bevel(0.018, 2))
        elif kind == 3:  # chunky frames
            parts.append(Part(INK).box((c[0] - 0.062, c[1] - 0.048, c[2] - 0.012), (c[0] + 0.062, c[1] + 0.048, c[2] + 0.012)).bevel(0.02, 2))
            parts.append(Part(LENS).box((c[0] - 0.045, c[1] - 0.032, c[2] + 0.004), (c[0] + 0.045, c[1] + 0.032, c[2] + 0.016)).bevel(0.012, 2))
    if kind:
        b = on_face(0, y + 0.005, 0.035)
        parts.append(Part(INK if kind != 2 else LENS_DARK).box((-0.025, b[1] - 0.008, b[2] - 0.006), (0.025, b[1] + 0.008, b[2] + 0.006)))
        for side in (-1, 1):  # arms back to the ears
            parts.append(Part(INK).box((side * 0.125 - 0.006, y - 0.006, -0.08), (side * 0.125 + 0.006, y + 0.006, 0.13)))
    return parts


def main():
    items = [("DriverBase", base())]
    items += [(f"Eyes{i}", eyes(i)) for i in range(4)]
    items += [(f"Mouth{i}", mouth(i)) for i in range(4)]
    items += [(f"Hat{k}", hat(k)) for k in range(1, 9)]
    items += [(f"Glasses{k}", glasses(k)) for k in range(1, 4)]
    for name, parts in items:
        clear_scene()
        obj = build_object(name, parts)
        export(obj, os.path.join(OUT, name + ".fbx"))
        print(f"[art] {name}: {len(obj.data.vertices)} verts")


if __name__ == "__main__":
    main()
