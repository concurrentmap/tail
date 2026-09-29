"""
TAILED art pipeline (Blender 5.x, headless):

    blender -b -P tools/blender/build_assets.py -- [--only NAME] [--out DIR]

Builds chunky, rounded "toy" vehicles to the exact dimensions and anchor points Unity writes to
tools/blender/specs.json (ArtPipeline.WriteVehicleSpecs), and exports FBX into
unity/Assets/Tailed/Resources/Art/Vehicles. Everything is vertex-coloured (no textures):

  * paint regions use the marker colour PAINT (magenta); Unity swaps in the car's colour
  * vertex alpha tags drive the vehicle shader's lamps (headlight / brake / indicators)
  * material slot 0 = body (Tailed/ToonVehicle), slot 1 = glass (Tailed/ToonGlass)

Coordinates: we model in Unity's frame (x right, y up, z forward; origin on the ground under the
car's centre) and map to Blender (X right, Y forward, Z up) with U(). Same physical axes.
"""
import bpy, bmesh, json, math, os, sys
from mathutils import Vector, Matrix

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SPECS = os.path.join(ROOT, "tools", "blender", "specs.json")
OUT = os.path.join(ROOT, "unity", "Assets", "Tailed", "Resources", "Art", "Vehicles")

PAINT = (1.0, 0.0, 1.0, 1.0)          # replaced by the car's colour at runtime
TRIM = (0.16, 0.16, 0.18, 1.0)
TYRE = (0.11, 0.11, 0.12, 1.0)
HUB = (0.80, 0.81, 0.84, 1.0)
GLASS = (0.42, 0.60, 0.78, 0.5)   # tinted enough to read as glass, clear enough to see the driver
BED = (0.25, 0.24, 0.24, 1.0)
BOX = (0.92, 0.92, 0.88, 1.0)
DOOR = (0.20, 0.22, 0.25, 1.0)
STRIPE = (0.20, 0.45, 0.75, 1.0)
SIGN = (0.10, 0.10, 0.10, 1.0)


def U(x, y, z):
    """Unity (x right, y up, z forward) -> Blender (X right, Y forward, Z up)."""
    return Vector((x, z, y))


class Part:
    """One bmesh part: geometry + a uniform colour + material slot; rounded with a bevel."""

    def __init__(self, color, mat=0):
        self.bm = bmesh.new()
        self.color = color
        self.mat = mat

    def box(self, lo, hi):
        """Axis-aligned box between two Unity-space corners."""
        (x0, y0, z0), (x1, y1, z1) = lo, hi
        vs = [self.bm.verts.new(U(x, y, z)) for x in (x0, x1) for y in (y0, y1) for z in (z0, z1)]
        idx = [(0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3)]
        for f in idx:
            self.bm.faces.new([vs[i] for i in f])
        return self

    def prism(self, profile, half_width, x0=0.0):
        """Side profile [(z, y), ...] (counter-clockwise seen from +x) extruded across ±half_width."""
        left = [self.bm.verts.new(U(x0 - half_width, y, z)) for z, y in profile]
        right = [self.bm.verts.new(U(x0 + half_width, y, z)) for z, y in profile]
        n = len(profile)
        self.bm.faces.new(right)
        self.bm.faces.new(list(reversed(left)))
        for i in range(n):
            j = (i + 1) % n
            self.bm.faces.new([left[i], left[j], right[j], right[i]])
        return self

    def cylinder(self, centre, axis, radius, depth, segments=24):
        """Cylinder along a Unity axis ('x' or 'z' or 'y') centred at a Unity point."""
        res = bmesh.ops.create_cone(self.bm, cap_ends=True, cap_tris=False, segments=segments,
                                    radius1=radius, radius2=radius, depth=depth)
        rot = {"x": Matrix.Rotation(math.pi / 2, 4, "Y"), "z": Matrix.Rotation(math.pi / 2, 4, "X"),
               "y": Matrix.Identity(4)}[axis]
        bmesh.ops.transform(self.bm, matrix=Matrix.Translation(U(*centre)) @ rot, verts=res["verts"])
        return self

    def sphere(self, centre, radii, segments=18, rings=10):
        res = bmesh.ops.create_uvsphere(self.bm, u_segments=segments, v_segments=rings, radius=1.0)
        s = Matrix.Diagonal((radii[0], radii[2], radii[1], 1.0))
        bmesh.ops.transform(self.bm, matrix=Matrix.Translation(U(*centre)) @ s, verts=res["verts"])
        return self

    def bevel(self, offset, segments=3):
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        if offset > 0:
            bmesh.ops.bevel(self.bm, geom=list(self.bm.edges), offset=offset, offset_type="OFFSET",
                            segments=segments, profile=0.5, affect="EDGES", clamp_overlap=True)
        return self


def build_object(name, parts):
    """Join parts into one mesh object with colours, material slots and smooth-by-angle normals."""
    bm = bmesh.new()
    col = bm.loops.layers.float_color.new("Col")
    for p in parts:
        tmp = bpy.data.meshes.new("tmp")
        p.bm.to_mesh(tmp)
        before = set(bm.faces)
        bm.from_mesh(tmp)
        bpy.data.meshes.remove(tmp)
        for f in bm.faces:
            if f in before:
                continue
            f.material_index = p.mat
            f.smooth = True
            for l in f.loops:
                l[col] = p.color
        p.bm.free()
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.set_sharp_from_angle(angle=math.radians(40))
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    for mname in ("Body", "Glass"):
        mat = bpy.data.materials.get(mname) or bpy.data.materials.new(mname)
        mesh.materials.append(mat)
    return obj


def car_parts(s, tags, player):
    L, W, H = s["length"], s["width"], s["height"]
    hw, zr, zf = W * 0.5, -L * 0.5, L * 0.5
    clr, belt = s["clearance"], s["belt"]
    hood, trunk = s["hood"], s["trunk"]
    cw = s["cabinHalfWidth"]
    roof_y = s["cabRoofHeight"]
    parts = []

    # Lower body: the side profile, generously rounded (the toy look). Low cars keep a real nose
    # and tail face (at least 0.3 m) for the lamps and plates.
    nose_y = max(belt - hood * 1.5, clr + 0.34)
    tail_y = max(belt - trunk * 1.5, clr + 0.34)
    body = Part(PAINT).prism([
        (zr + 0.05, clr + 0.06), (zf - 0.05, clr + 0.06), (zf, nose_y),
        (zf - hood * 4, belt), (zr + trunk * 4, belt), (zr, tail_y)], hw - 0.02)
    body.bevel(min(0.16, (belt - clr) * 0.3), 4)
    if player and not s["bus"] and not s["boxBody"]:
        # The player's car is a tub, not a solid block: open the cabin so the interior (seats,
        # footwell, door cards) isn't hidden under the body's lid at belt height.
        cut_body_top(body, s["cabinRear"] + 0.04, s["cabinFront"] - 0.02, belt - 0.12, hw - 0.12)
    parts.append(body)

    # Bumpers: chunky rounded bars.
    # (Front faces stop short of the plate plane at ±(L/2 + 0.07): no z-fighting with the plates.)
    parts.append(Part(TRIM).box((-hw - 0.03, clr - 0.03, zf - 0.16), (hw + 0.03, clr + 0.2, zf + 0.035)).bevel(0.07, 3))
    parts.append(Part(TRIM).box((-hw - 0.03, clr - 0.03, zr - 0.035), (hw + 0.03, clr + 0.2, zr + 0.16)).bevel(0.07, 3))

    if s["bus"]:
        # Long glass band, thick roof, posts, doors, destination board, livery stripe.
        parts.append(Part(GLASS, 1).prism([(s["cabinRear"], belt), (s["cabinFront"], belt),
                                            (s["roofFront"], roof_y - 0.12), (s["roofRear"], roof_y - 0.12)], cw - 0.01).bevel(0.04, 2))
        parts.append(Part(PAINT).box((-cw - 0.02, roof_y - 0.2, s["roofRear"] - 0.05), (cw + 0.02, roof_y + 0.04, s["roofFront"] + 0.05)).bevel(0.1, 3))
        posts = max(2, round((s["cabinFront"] - s["cabinRear"]) / 1.3))
        for i in range(1, posts):
            z = s["cabinRear"] + (s["cabinFront"] - s["cabinRear"]) * i / posts
            for x in (-cw, cw):
                parts.append(Part(PAINT).box((x - 0.05, belt, z - 0.07), (x + 0.05, roof_y - 0.12, z + 0.07)).bevel(0.025, 2))
        for z0, z1 in ((zf - 2.2, zf - 1.1), (-0.4, 0.7)):
            parts.append(Part(DOOR).box((hw - 0.03, clr + 0.18, z0), (hw + 0.03, roof_y - 0.25, z1)).bevel(0.03, 2))
        parts.append(Part(SIGN).box((-cw * 0.7, roof_y - 0.45, zf - 0.02), (cw * 0.7, roof_y - 0.22, zf + 0.05)).bevel(0.03, 2))
        parts.append(Part(STRIPE).box((-hw - 0.01, belt - 0.36, zr + 0.3), (hw + 0.01, belt - 0.2, zf - 0.3)).bevel(0.02, 1))
    elif s["boxBody"]:
        # Lorry: cab up front, tall rounded box behind with a roller door.
        cab = [(s["cabinRear"], belt), (s["cabinFront"], belt), (s["roofFront"], roof_y - 0.06), (s["roofRear"], roof_y - 0.06)]
        parts.append(Part(GLASS, 1).prism(cab, cw).bevel(0.05, 2))
        parts.append(Part(PAINT).box((-cw - 0.02, roof_y - 0.1, s["roofRear"] - 0.02), (cw + 0.02, roof_y, s["roofFront"] + 0.02)).bevel(0.05, 3))
        box_front = s["cabinRear"] - 0.12
        parts.append(Part(BOX).box((-hw, clr + 0.35, zr), (hw, H, box_front)).bevel(0.12, 3))
        parts.append(Part((0.72, 0.72, 0.70, 1.0)).box((-hw * 0.85, clr + 0.45, zr - 0.04), (hw * 0.85, H - 0.14, zr + 0.02)).bevel(0.03, 2))
    elif s["cargoBox"]:
        # Van: opaque cargo box behind a glass cab.
        split = s["cabinRear"] + (s["roofFront"] - s["cabinRear"]) * 0.72
        parts.append(Part(PAINT).prism([(s["cabinRear"], belt - 0.05), (split, belt - 0.05), (split, roof_y), (s["roofRear"], roof_y)], cw).bevel(0.12, 3))
        parts.append(Part(GLASS, 1).prism([(split - 0.05, belt), (s["cabinFront"], belt), (s["roofFront"], roof_y - 0.06), (split - 0.05, roof_y - 0.06)], cw - 0.01).bevel(0.05, 2))
        parts.append(Part(PAINT).box((-cw, roof_y - 0.1, split - 0.1), (cw, roof_y, s["roofFront"] + 0.02)).bevel(0.05, 3))
    else:
        # Cars: a glass greenhouse with a thick rounded roof and chunky pillars.
        cab = [(s["cabinRear"], belt - 0.02), (s["cabinFront"], belt - 0.02), (s["roofFront"], roof_y - 0.1), (s["roofRear"], roof_y - 0.1)]
        parts.append(Part(GLASS, 1).prism(cab, cw - 0.01).bevel(0.07, 3))
        # A thick, pillowy roof cap overhanging the glass a little (reads as a toy car roof).
        parts.append(Part(PAINT).box((-cw - 0.05, roof_y - 0.16, s["roofRear"] - 0.06), (cw + 0.05, roof_y, s["roofFront"] + 0.06)).bevel(0.075, 4))
    if not s["bus"] and not s["boxBody"]:
        pt = 0.055
        mid_lo = (s["cabinRear"] + s["cabinFront"]) * 0.5
        mid_hi = (s["roofRear"] + s["roofFront"]) * 0.5
        for x in (-cw, cw):
            for (zb, zt, t) in ((s["cabinFront"], s["roofFront"], pt), (s["cabinRear"], s["roofRear"], pt * 1.5), (mid_lo, mid_hi, pt)):
                parts.append(beam((x, belt - 0.04, zb), (x, roof_y - 0.12, zt), t, PAINT))
    if s["roofRails"]:
        for x in (-cw * 0.8, cw * 0.8):
            parts.append(Part(TRIM).box((x - 0.035, roof_y, s["roofRear"] + 0.1), (x + 0.035, roof_y + 0.08, s["roofFront"] - 0.1)).bevel(0.02, 2))
    if s["pickupBed"]:
        floor = belt - 0.35
        parts.append(Part(BED).box((-hw + 0.1, floor, zr + 0.1), (hw - 0.1, floor + 0.03, s["cabinRear"])))
        for lo, hi in (((-hw, belt - 0.05, zr), (-hw + 0.1, belt + 0.12, s["cabinRear"])),
                       ((hw - 0.1, belt - 0.05, zr), (hw, belt + 0.12, s["cabinRear"])),
                       ((-hw, belt - 0.05, zr), (hw, belt + 0.12, zr + 0.1))):
            parts.append(Part(PAINT).box(lo, hi).bevel(0.035, 2))

    parts += lower_body(s, hw, zr, zf, clr, belt)

    # Wheels: chunky, slightly proud of the body, with bright hubs.
    r = s["wheelRadius"] * 1.06
    for z in (-s["wheelBase"] * 0.5, s["wheelBase"] * 0.5):
        for side in (-1, 1):
            x = side * (hw - 0.1)
            parts.append(Part(TYRE).cylinder((x, r, z), "x", r, 0.3, 28).bevel(0.07, 3))
            parts.append(Part(HUB).cylinder((x + side * 0.13, r, z), "x", r * 0.55, 0.06, 20).bevel(0.02, 2))

    # Lamps (vertex alpha = shader tag). Round headlamps give the cars a face.
    ly = max(nose_y - 0.12, clr + 0.26)
    head = (1.0, 0.97, 0.8, tags["tagHeadlight"])
    tail = (0.8, 0.08, 0.08, tags["tagBrake"])
    ind_l = (1.0, 0.55, 0.05, tags["tagLeft"])
    ind_r = (1.0, 0.55, 0.05, tags["tagRight"])
    lamp_r = min(0.14, hw * 0.16)
    for side in (-1, 1):
        parts.append(Part(head).cylinder((side * hw * 0.72, ly, zf - 0.02), "z", lamp_r, 0.1, 20).bevel(0.02, 2))
        ty = max(tail_y - 0.14, clr + 0.3)
        parts.append(Part(tail).box((side * hw * 0.78 - hw * 0.14, ty - 0.07, zr - 0.035),
                                    (side * hw * 0.78 + hw * 0.14, ty + 0.07, zr + 0.05)).bevel(0.03, 2))
        ind = ind_l if side < 0 else ind_r
        parts.append(Part(ind).sphere((side * hw * 0.95, ly, zf - 0.03), (0.06, 0.06, 0.05), 12, 8))
        parts.append(Part(ind).sphere((side * hw * 0.96, max(belt - 0.3, ty + 0.12), zr + 0.03), (0.055, 0.055, 0.05), 12, 8))

    # Plate brackets: dark backing exactly where Unity puts the (oversized) plates.
    pw, ph = s["plateSize"]["x"] * 0.5 + 0.03, s["plateSize"]["y"] * 0.5 + 0.03
    for key, inward in (("frontPlate", -1), ("rearPlate", 1)):
        p = s[key]
        z_out = p["z"] - inward * 0.006 * -1
        z_in = zf - 0.06 if inward < 0 else zr + 0.06
        z0, z1 = sorted((p["z"] + (0.006 if inward > 0 else -0.006), z_in))
        parts.append(Part(TRIM).box((-pw, p["y"] - ph, z0), (pw, p["y"] + ph, z1)).bevel(0.02, 2))

    if player:
        # The glass greenhouse's underside would be a tinted sheet across the cabin at belt height.
        for p in parts:
            if p.color == GLASS:
                p.bm.normal_update()
                bmesh.ops.delete(p.bm, geom=[f for f in p.bm.faces if f.normal.z < -0.5], context="FACES")
    if not player:
        # Wing mirror housings (the player's own car gets oriented ones from MirrorSystem).
        for key in ("leftMirror", "rightMirror"):
            m = s[key]
            parts.append(Part(TRIM).box((m["x"] - 0.11, m["y"] - 0.075, m["z"] - 0.03), (m["x"] + 0.11, m["y"] + 0.075, m["z"] + 0.09)).bevel(0.04, 2))
    return parts


UNDER = (0.1, 0.1, 0.11, 1.0)
METAL = (0.42, 0.42, 0.44, 1.0)
WELL = (0.06, 0.06, 0.07, 1.0)


def cut_body_top(part, z0, z1, min_y, half_x):
    """Remove the body's top faces over the cabin (between z0 and z1, inside +-half_x)."""
    bm = part.bm
    for z in (z0, z1):
        geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
        bmesh.ops.bisect_plane(bm, geom=geom, plane_co=U(0, 0, z), plane_no=U(0, 0, 1))
    for x in (-half_x, half_x):
        geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
        bmesh.ops.bisect_plane(bm, geom=geom, plane_co=U(x, 0, 0), plane_no=U(1, 0, 0))
    bm.normal_update()
    doomed = []
    for f in bm.faces:
        c = f.calc_center_median()   # Blender coords: X = Unity x, Y = Unity z, Z = Unity y
        if f.normal.z > 0.5 and z0 < c.y < z1 and c.z > min_y and abs(c.x) < half_x:
            doomed.append(f)
    bmesh.ops.delete(bm, geom=doomed, context="FACES")


def arch_lip(side, hw, axle_y, z, R, a0=15.0, sweep=150.0):
    """A rounded fender flare: a small section spun about the axle from a0 through sweep degrees."""
    p = Part(PAINT)
    bm = p.bm
    a = math.radians(a0)
    xs = sorted((side * (hw - 0.01), side * (hw + 0.03)))
    ring = [(xs[0], R - 0.03), (xs[1], R - 0.03), (xs[1], R + 0.045), (xs[0], R + 0.045)]
    vs = [bm.verts.new(U(x, axle_y + math.sin(a) * rr, z + math.cos(a) * rr)) for x, rr in ring]
    face = bm.faces.new(vs)
    bmesh.ops.spin(bm, geom=[face] + vs + list(face.edges), cent=U(0, axle_y, z), axis=(1, 0, 0),
                   angle=math.radians(sweep), steps=14, use_duplicate=False)
    return p.bevel(0.012, 1)


def lower_body(s, hw, zr, zf, clr, belt):
    """Everything below the belt that stops the body reading as a flat slab: dark wheel wells with
    flared arch lips, sills, door shut-lines and handles, and an underside (floor pan, axles,
    sump, fuel tank, exhaust) for low cameras, kerbs and roll-overs."""
    parts = []
    r = s["wheelRadius"] * 1.06
    wb = s["wheelBase"] * 0.5
    for z in (-wb, wb):
        for side in (-1, 1):
            # Wheel well: a dark disc just proud of the body side, a little bigger than the tyre.
            parts.append(Part(WELL).cylinder((side * (hw - 0.005), r, z), "x", r + 0.07, 0.03, 24))
            # Flared lip over the top half of the arch: one profile swept round the axle.
            parts.append(arch_lip(side, hw, r, z, r + 0.1))
            # Inner arch liner, so you don't see through to the sky above the tyre.
            parts.append(Part(WELL).box((side * (hw - 0.34), r, z - r - 0.05), (side * (hw - 0.02), r + r + 0.08, z + r + 0.05)).bevel(0.03, 1))
    # Sills between the arches.
    sill_lo, sill_hi = clr + 0.02, clr + 0.16
    for side in (-1, 1):
        x0, x1 = sorted((side * (hw - 0.06), side * (hw + 0.02)))
        parts.append(Part(TRIM).box((x0, sill_lo, -wb + r + 0.12), (x1, sill_hi, wb - r - 0.12)).bevel(0.03, 2))
    # Door shut-lines and handles (cars, vans, pickups; not the bus or lorry box).
    if not s["bus"]:
        cf, cr = s["cabinFront"], s["cabinRear"]
        lines = [cf - 0.05, (cf + cr) * 0.5] if s["cargoBox"] or s["pickupBed"] or s["boxBody"] else [cf - 0.05, (cf + cr) * 0.5 + 0.05, cr + 0.08]
        for side in (-1, 1):
            x = side * (hw - 0.012)
            for z in lines:
                parts.append(Part(WELL).box((x - 0.012, sill_hi + 0.02, z - 0.008), (x + 0.012, belt - 0.03, z + 0.008)))
            for z0, z1 in zip(lines, lines[1:]):
                hz = z1 + 0.15
                hx0, hx1 = sorted((side * (hw - 0.01), side * (hw + 0.025)))
                parts.append(Part(TRIM).box((hx0, belt - 0.14, hz - 0.1), (hx1, belt - 0.1, hz + 0.02)).bevel(0.01, 1))
    # Underside: dark floor pan inset from the body, axles, sump, tank, exhaust.
    parts.append(Part(UNDER).box((-hw + 0.1, clr - 0.03, zr + 0.25), (hw - 0.1, clr + 0.08, zf - 0.25)).bevel(0.04, 1))
    for z in (-wb, wb):
        parts.append(Part(METAL).cylinder((0, r, z), "x", 0.045, hw * 2 - 0.35, 10))
        for side in (-1, 1):   # suspension arm/strut
            parts.append(beam((side * 0.35, clr, z - 0.15), (side * (hw - 0.3), r, z), 0.03, METAL))
    parts.append(Part(UNDER).box((-0.35, clr - 0.08, wb - 0.55), (0.35, clr + 0.02, wb - 0.05)).bevel(0.04, 1))          # sump
    parts.append(Part(METAL).box((-0.55, clr - 0.06, -wb + 0.35), (0.35, clr + 0.03, -wb + 0.9)).bevel(0.05, 2))         # fuel tank
    ex_x = hw * 0.45
    ez0, ez1 = zr + 0.1, wb - 0.6
    parts.append(Part(METAL).cylinder((ex_x, clr + 0.02, (ez0 + ez1) * 0.5), "z", 0.035, ez1 - ez0, 10))
    parts.append(Part(METAL).cylinder((ex_x, clr + 0.02, -wb * 0.2), "z", 0.1, 0.55, 14).bevel(0.03, 1))                  # silencer
    parts.append(Part(METAL).cylinder((ex_x, clr + 0.02, zr + 0.02), "z", 0.045, 0.14, 12))                               # tailpipe
    return parts


def beam(a, b, thickness, color):
    """Rounded bar between two Unity points (pillars)."""
    a, b = Vector(a), Vector(b)
    p = Part(color)
    length = (b - a).length
    p.box((-thickness, 0, -thickness), (thickness, length, thickness)).bevel(thickness * 0.5, 2)
    # Orient +y (Unity) along a→b.
    d = (b - a).normalized()
    up = Vector((0, 1, 0))
    rot = up.rotation_difference(d).to_matrix().to_4x4()
    # Build in Unity space then map: transform verts (they are in Blender coords via U()).
    to_unity = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))  # Blender <-> Unity swap (involution)
    m = to_unity @ Matrix.Translation(a) @ rot @ to_unity
    bmesh.ops.transform(p.bm, matrix=m, verts=p.bm.verts)
    return p


def clear_scene():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for m in list(bpy.data.meshes):
        bpy.data.meshes.remove(m)


def export(obj, path):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={"MESH"}, use_mesh_modifiers=True,
                             colors_type="LINEAR", mesh_smooth_type="OFF", axis_forward="Z", axis_up="Y",
                             apply_scale_options="FBX_SCALE_UNITS", bake_space_transform=True, add_leaf_bones=False)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    only = argv[argv.index("--only") + 1] if "--only" in argv else None
    out = argv[argv.index("--out") + 1] if "--out" in argv else OUT
    with open(SPECS) as f:
        spec_file = json.load(f)
    tags = {k: spec_file[k] for k in ("tagHeadlight", "tagBrake", "tagLeft", "tagRight")}
    for s in spec_file["models"]:
        if only and s["name"] != only:
            continue
        for player in (False, True):
            clear_scene()
            name = s["name"] + ("_Player" if player else "")
            obj = build_object(name, car_parts(s, tags, player))
            export(obj, os.path.join(out, name + ".fbx"))
            print(f"[art] {name}: {len(obj.data.vertices)} verts, {len(obj.data.polygons)} faces")


if __name__ == "__main__":
    main()
