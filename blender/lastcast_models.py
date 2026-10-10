# Builds The Last Cast's 3D models in Blender and exports them as FBX into
# unity-src/Assets/LastCast/Resources/Models, where the game loads them (it falls
# back to its own simple code-built shapes for anything missing).
#
# The style matches the game: low-poly, flat-shaded, coloured per face with
# vertex colours (no textures), using the 2D game's palette. Each kind of model
# is one FBX holding its variants (tree_0, tree_1, ...), all built from seeds so
# every run gives the same models.
#
#   G:\Blender\blender.exe --background --factory-startup --python blender\lastcast_models.py
#   ... -- tree pine            only some kinds
#   ... -- --preview <folder>   also render a preview sheet of each kind there

import bpy, bmesh, math, os, random, sys
from contextlib import contextmanager
from mathutils import Vector, Matrix, noise

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, '..', 'unity-src', 'Assets', 'LastCast', 'Resources', 'Models'))

# ---- colours ------------------------------------------------------------------------------------------

def hexc(h, a=1.0):
    h = h.lstrip('#')
    return (int(h[0:2], 16) / 255, int(h[2:4], 16) / 255, int(h[4:6], 16) / 255, a)

def shade(c, k):
    return (min(1, c[0] * k), min(1, c[1] * k), min(1, c[2] * k), c[3])

def mix(a, b, t):
    return tuple(a[i] + (b[i] - a[i]) * t for i in range(4))

def smooth(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)

# ---- limbs (for animation) ------------------------------------------------------------------------------

def along(path):
    """How far along a path (0 at its start, 1 at its end) the nearest point to co is."""
    P = [Vector(p) for p in path]
    lens = [0.0]
    for a, b in zip(P, P[1:]):
        lens.append(lens[-1] + (b - a).length)
    total = max(1e-6, lens[-1])

    def t_of(co):
        best, bt = 1e9, 0.0
        for i, (a, b) in enumerate(zip(P, P[1:])):
            ab = b - a
            u = max(0.0, min(1.0, (co - a).dot(ab) / max(1e-9, ab.length_squared)))
            d = (a + ab * u - co).length_squared
            if d < best:
                best, bt = d, (lens[i] + ab.length * u) / total
        return bt
    return t_of

def jointed(a, joint, b):
    """Along a two-part limb (shoulder, elbow, hand): 0..0.5 on the first part, 0.5..1 on the second."""
    first, second = along([a, joint]), along([joint, b])
    A, J, B = Vector(a), Vector(joint), Vector(b)

    def dist(co, p, q):
        pq = q - p
        u = max(0.0, min(1.0, (co - p).dot(pq) / max(1e-9, pq.length_squared)))
        return (p + pq * u - co).length

    return lambda co: 0.5 * first(co) if dist(co, A, J) < dist(co, J, B) else 0.5 + 0.5 * second(co)

def axis_t(start, end, axis=1):
    """Along a straight line on one axis (y by default): 0 at start, 1 at end."""
    return lambda co: (co[axis] - start) / (end - start)

# ---- building meshes ----------------------------------------------------------------------------------

class Kit:
    """A mesh under construction: parts are added with their own colour, then baked into one flat-shaded object."""

    def __init__(self, seed=0):
        self.bm = bmesh.new()
        self.col = self.bm.loops.layers.color.new('Col')
        self.rnd = random.Random(seed)
        self.seed = seed
        # The UV channel carries animation data, not texture coordinates: u is the limb a face belongs to
        # (0 = the rigid body) and v how far along that limb it is (0 at its root, 1 at its tip). The game
        # builds bones from it (World/LimbRig.cs) to swing legs, curl tentacles and wag tails.
        self.uv = self.bm.loops.layers.uv.new('UVMap')

    @contextmanager
    def limb(self, index, along):
        """Everything built inside belongs to limb `index` (or index(co), point by point: a coat's skirt follows
        whichever leg it's beside); along(co) gives how far along the limb each point is (0..1)."""
        before = set(self.bm.faces)
        yield
        for f in self.bm.faces:
            if f in before:
                continue
            for l in f.loops:
                co = l.vert.co
                l[self.uv].uv = (index(co) if callable(index) else index, max(0.0, min(1.0, along(co))))

    def tube_r(self, points, radii, color, sides=8, jitter=0.05, cap=True):
        """A tube along a path with its own radius at each point (a leg: thigh, knee, ankle), faces outwards."""
        rings, n = [], len(points)
        P = [Vector(p) for p in points]
        for i, p in enumerate(P):
            d = (P[min(i + 1, n - 1)] - P[max(i - 1, 0)]).normalized()
            up = Vector((0, 0, 1)) if abs(d.z) < 0.95 else Vector((1, 0, 0))
            u = d.cross(up).normalized()
            w = d.cross(u)
            rings.append([self.bm.verts.new(p + (u * math.cos(a) + w * math.sin(a)) * radii[i])
                          for a in [s / sides * math.tau for s in range(sides)]])
        faces = []
        for i in range(n - 1):
            mid = (P[i] + P[i + 1]) / 2
            ring = []
            for s in range(sides):
                f = self.bm.faces.new((rings[i][s], rings[i][(s + 1) % sides], rings[i + 1][(s + 1) % sides], rings[i + 1][s]))
                ring.append(f)
            self.orient(ring, lambda f, m=mid: f.calc_center_median() - m)
            faces += ring
        if cap:
            for ring, p, q in ((rings[0], P[0], P[1]), (rings[-1], P[-1], P[-2])):
                f = self.bm.faces.new(ring)
                self.orient([f], lambda f, p=p, q=q: p - q)
                faces.append(f)
        self.paint(faces, color, jitter)
        return faces

    def cut(self, faces, pred):
        """Remove the faces that match (an opening in a hood)."""
        bmesh.ops.delete(self.bm, geom=[f for f in faces if f.is_valid and pred(f)], context='FACES_ONLY')

    def vloft(self, sections, colour, sides=16, bottom=True, top=True):
        """A body round the vertical (a torso, a coat, a helmet): sections [(z, half-width, half-depth, forward)]
        from the bottom up. colour is a colour, or colour(centre, normal) for each face."""
        rings = []
        for z, rx, ry, cy in sections:
            rings.append([self.bm.verts.new((rx * math.cos(a), cy + ry * math.sin(a), z)) for a in [s / sides * math.tau for s in range(sides)]])
        faces = []
        for i in range(len(rings) - 1):
            for s in range(sides):
                t = (s + 1) % sides
                faces.append(self.bm.faces.new((rings[i][s], rings[i][t], rings[i + 1][t], rings[i + 1][s])))
        cys = [(z, cy) for z, rx, ry, cy in sections]

        def out(f):
            c = f.calc_center_median()
            return Vector((c.x, c.y - interp(cys, c.z), 0))

        self.orient(faces, out)
        if bottom:
            f = self.bm.faces.new(rings[0])
            self.orient([f], lambda f: Vector((0, 0, -1)))
            faces.append(f)
        if top:
            f = self.bm.faces.new(rings[-1])
            self.orient([f], lambda f: Vector((0, 0, 1)))
            faces.append(f)
        for f in faces:
            c = colour(f.calc_center_median(), f.normal) if callable(colour) else colour
            k = 1 + (self.rnd.random() - 0.5) * 0.06
            for l in f.loops:
                l[self.col] = shade(c, k)
        return faces

    # -- painting --
    def paint(self, faces, color, jitter=0.06):
        for f in faces:
            k = 1 + (self.rnd.random() - 0.5) * 2 * jitter
            c = shade(color, k)
            for l in f.loops:
                l[self.col] = c

    def _faces_of(self, verts):
        faces = set()
        for v in verts:
            faces.update(v.link_faces)
        return list(faces)

    def _new(self, result_verts, color, jitter):
        faces = self._faces_of(result_verts)
        self.paint(faces, color, jitter)
        return result_verts, faces

    # -- primitives (all return (verts, faces)) --
    def ico(self, center, radius, color, subdiv=1, rough=0.15, freq=1.6, jitter=0.07, squash=None):
        r = Vector(radius) if not isinstance(radius, (int, float)) else Vector((radius, radius, radius))
        res = bmesh.ops.create_icosphere(self.bm, subdivisions=subdiv, radius=1.0, matrix=Matrix.Identity(4))
        verts = res['verts']
        off = Vector((self.rnd.random() * 100, self.rnd.random() * 100, self.rnd.random() * 100))
        for v in verts:
            d = v.co.normalized()
            n = noise.noise(d * freq + off)
            v.co = Vector((d.x * r.x, d.y * r.y, d.z * r.z)) * (1 + n * rough) + Vector(center)
        return self._new(verts, color, jitter)

    def cyl(self, a, b, r0, r1, color, sides=6, jitter=0.05, caps=True):
        a, b = Vector(a), Vector(b)
        axis = b - a
        rot = axis.to_track_quat('Z', 'Y').to_matrix().to_4x4()
        m = Matrix.Translation((a + b) / 2) @ rot
        res = bmesh.ops.create_cone(self.bm, cap_ends=caps, cap_tris=False, segments=sides, radius1=r0, radius2=max(r1, 0.0001),
                                    depth=axis.length, matrix=m)
        return self._new(res['verts'], color, jitter)

    def box(self, center, size, color, rot=None, jitter=0.03):
        m = Matrix.Translation(Vector(center))
        if rot is not None:
            m = m @ rot.to_matrix().to_4x4() if hasattr(rot, 'to_matrix') else m @ rot
        m = m @ Matrix.Diagonal(Vector((size[0], size[1], size[2], 1)))
        res = bmesh.ops.create_cube(self.bm, size=1.0, matrix=m)
        return self._new(res['verts'], color, jitter)

    def tube(self, points, r0, r1, color, sides=6, jitter=0.06, cap=True):
        """A tube along a path of points, tapering from r0 to r1."""
        rings = []
        n = len(points)
        for i, p in enumerate(points):
            p = Vector(p)
            d = (Vector(points[min(i + 1, n - 1)]) - Vector(points[max(i - 1, 0)])).normalized()
            up = Vector((0, 0, 1)) if abs(d.z) < 0.95 else Vector((1, 0, 0))
            u = d.cross(up).normalized()
            w = d.cross(u)
            r = r0 + (r1 - r0) * (i / (n - 1))
            ring = [self.bm.verts.new(p + (u * math.cos(a) + w * math.sin(a)) * r)
                    for a in [k / sides * math.tau for k in range(sides)]]
            rings.append(ring)
        faces = []
        for i in range(n - 1):
            for k in range(sides):
                a, b = rings[i][k], rings[i][(k + 1) % sides]
                c, d = rings[i + 1][(k + 1) % sides], rings[i + 1][k]
                faces.append(self.bm.faces.new((a, b, c, d)))
        if cap:
            faces.append(self.bm.faces.new(list(reversed(rings[0]))))
            faces.append(self.bm.faces.new(rings[-1]))
        self.paint(faces, color, jitter)
        return [v for r in rings for v in r], faces

    def poly(self, pts, color, jitter=0.0, both=True):
        """A flat polygon (fins, sails, petals), two-sided by default."""
        vs = [self.bm.verts.new(Vector(p)) for p in pts]
        faces = [self.bm.faces.new(vs)]
        if both:
            vs2 = [self.bm.verts.new(Vector(p)) for p in reversed(pts)]
            faces.append(self.bm.faces.new(vs2))
        self.paint(faces, color, jitter)
        return vs, faces

    def blob(self, parts, voxel, ratio, colorize):
        """Melt several lumps into one solid, faceted shape (a canopy, a boulder): voxel-remesh the lumps
        together, decimate them back to low-poly facets, then colour each face with colorize(face)."""
        tmp = Kit(self.seed + 7)
        tmp.rnd = self.rnd
        for p in parts:
            tmp.ico(*p)
        me = bpy.data.meshes.new('blob tmp')
        tmp.bm.to_mesh(me)
        tmp.bm.free()
        ob = bpy.data.objects.new('blob tmp', me)
        bpy.context.scene.collection.objects.link(ob)
        rm = ob.modifiers.new('remesh', 'REMESH')
        rm.mode = 'VOXEL'
        rm.voxel_size = voxel
        dec = ob.modifiers.new('decimate', 'DECIMATE')
        dec.ratio = ratio
        ev = ob.evaluated_get(bpy.context.evaluated_depsgraph_get())
        flat = bpy.data.meshes.new_from_object(ev)
        before = set(self.bm.faces)
        self.bm.from_mesh(flat)
        new = [f for f in self.bm.faces if f not in before]
        for f in new:
            c = colorize(f)
            k = 1 + (self.rnd.random() - 0.5) * 0.1
            for l in f.loops:
                l[self.col] = shade(c, k)
        bpy.data.objects.remove(ob, do_unlink=True)
        bpy.data.meshes.remove(me)
        bpy.data.meshes.remove(flat)
        return new

    def displace(self, verts, amp, freq=1.0, along=None):
        off = Vector((self.rnd.random() * 50, self.rnd.random() * 50, self.rnd.random() * 50))
        for v in verts:
            n = noise.noise(v.co * freq + off)
            v.co += (Vector(along) if along else v.co.normalized()) * n * amp

    def fin(self, pts, thickness, color, jitter=0.04):
        """A flat shape with some thickness (fins, flukes, wings, sails): the polygon pts, extruded both ways."""
        pts = [Vector(p) for p in pts]
        n = (pts[1] - pts[0]).cross(pts[2] - pts[0]).normalized()
        off = n * thickness / 2
        a = [self.bm.verts.new(p + off) for p in pts]
        b = [self.bm.verts.new(p - off) for p in pts]
        faces = [self.bm.faces.new(a), self.bm.faces.new(list(reversed(b)))]
        for i in range(len(pts)):
            j = (i + 1) % len(pts)
            faces.append(self.bm.faces.new((a[j], a[i], b[i], b[j])))
        centre = sum(pts, Vector()) / len(pts)
        self.orient(faces, lambda f: f.calc_center_median() - centre)
        self.paint(faces, color, jitter)
        return faces

    def loft(self, sections, color_fn, sides=8):
        """A smooth body through elliptical sections [(y, half-width, half-height, z-offset)], back to front,
        closed at both ends. color_fn(centre, normal) colours each face."""
        rings = []
        for y, rx, rz, zo in sections:
            rings.append([self.bm.verts.new((rx * math.cos(a), y, zo + rz * math.sin(a)))
                          for a in [s / sides * math.tau + math.pi / 2 for s in range(sides)]])
        faces = []
        for i in range(len(rings) - 1):
            for s in range(sides):
                t = (s + 1) % sides
                faces.append(self.bm.faces.new((rings[i][s], rings[i][t], rings[i + 1][t], rings[i + 1][s])))
        axis = {}
        for (y, rx, rz, zo) in sections:
            axis[y] = zo

        def centre_of(f):
            c = f.calc_center_median()
            ys = sorted(axis)
            zo = axis[ys[0]]
            for y0, y1 in zip(ys, ys[1:]):
                if y0 <= c.y <= y1:
                    t = (c.y - y0) / max(1e-6, y1 - y0)
                    zo = axis[y0] + (axis[y1] - axis[y0]) * t
            return Vector((c.x, 0, c.z - zo))

        self.orient(faces, centre_of)
        for end, out in ((rings[0], -1), (rings[-1], 1)):
            f = self.bm.faces.new(end)
            self.orient([f], lambda f, out=out: Vector((0, out, 0)))
            faces.append(f)
        for f in faces:
            c = color_fn(f.calc_center_median(), f.normal)
            k = 1 + (self.rnd.random() - 0.5) * 0.06
            for l in f.loops:
                l[self.col] = shade(c, k)
        return faces

    def orient(self, faces, outward):
        """Turn hand-built faces to face outwards (the game only draws the front of each face).
        outward(face) gives the direction the face should point."""
        flip = []
        for f in faces:
            f.normal_update()
            if f.normal.dot(outward(f)) < 0:
                flip.append(f)
        if flip:
            bmesh.ops.reverse_faces(self.bm, faces=flip)
            for f in flip:
                f.normal_update()

    def mirror_y(self):
        """Flip front and back (keeping the faces facing outwards)."""
        bmesh.ops.scale(self.bm, vec=(1, -1, 1), verts=self.bm.verts)
        bmesh.ops.reverse_faces(self.bm, faces=self.bm.faces)

    # -- finishing --
    def build(self, name, ao=0.3, ground=0.0, flip=False):
        """Bake into a flat-shaded object. ao darkens towards the bottom (fake ambient occlusion)."""
        bm = self.bm
        bmesh.ops.recalc_face_normals(bm, faces=[f for f in bm.faces if len(f.verts) > 2 and not getattr(f, '_keep', False)]) if flip else None
        if ao > 0 and bm.verts:
            zs = [v.co.z for v in bm.verts]
            z0, z1 = min(zs), max(zs)
            span = max(0.001, z1 - z0)
            for f in bm.faces:
                for l in f.loops:
                    t = smooth((l.vert.co.z - z0) / span)
                    c = l[self.col]
                    k = (1 - ao) + ao * t
                    l[self.col] = (c[0] * k, c[1] * k, c[2] * k, c[3])
        me = bpy.data.meshes.new(name)
        bm.to_mesh(me)
        bm.free()
        for p in me.polygons:
            p.use_smooth = False
        attr = me.color_attributes.get('Col')
        if attr:
            me.color_attributes.active_color = attr
            me.color_attributes.render_color_index = me.color_attributes.active_color_index
        ob = bpy.data.objects.new(name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob

# ---- export and preview ----------------------------------------------------------------------------------

def clear_scene():
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for me in list(bpy.data.meshes):
        bpy.data.meshes.remove(me)

def export(objs, kind):
    os.makedirs(OUT, exist_ok=True)
    for ob in bpy.context.scene.objects:
        ob.select_set(False)
    for ob in objs:
        ob.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    path = os.path.join(OUT, kind + '.fbx')
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={'MESH'},
        apply_scale_options='FBX_SCALE_UNITS', axis_forward='-Z', axis_up='Y', bake_space_transform=True,
        mesh_smooth_type='FACE', use_mesh_modifiers=True, colors_type='SRGB', prioritize_active_color=True,
        use_triangles=True, add_leaf_bones=False, bake_anim=False, use_custom_props=False, path_mode='STRIP')
    print(f'exported {len(objs)} to {path}')

def preview(objs, kind, folder):
    """Line the variants up and render them with Workbench (flat colours, like the game)."""
    os.makedirs(folder, exist_ok=True)
    gap = 0.6
    widths = [max(ob.dimensions.x, ob.dimensions.y) for ob in objs]
    x = 0
    for ob, w in zip(objs, widths):
        ob.location.x = x + w / 2
        x += w + gap
    total = x - gap
    height = max(ob.dimensions.z for ob in objs)
    scene = bpy.context.scene
    cam_data = bpy.data.cameras.new('cam')
    cam = bpy.data.objects.new('cam', cam_data)
    scene.collection.objects.link(cam)
    cam_data.type = 'ORTHO'
    depth = max(ob.dimensions.y for ob in objs)
    cam_data.ortho_scale = max(total * 1.1, depth * 1.2, height * 2.4) * 1.1
    # A three-quarter view from the front left (the models' front faces -Y... or +Y for boats; both show).
    from mathutils import Euler
    # Creatures and boats face +Y: look at them from the front (shops and the wagon face -Y).
    faces_back = kind.startswith(('stall', 'zoo', 'signpost'))
    rot = Euler((math.radians(68), 0, math.radians(-32 if faces_back else 148)), 'XYZ')
    forward = rot.to_matrix() @ Vector((0, 0, -1))
    target = Vector((total / 2, 0, height * 0.45))
    cam.location = target - forward * 120
    cam.rotation_euler = rot
    cam_data.clip_end = 1000
    scene.camera = cam
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'VERTEX'
    scene.display.shading.show_shadows = True
    scene.display.shading.show_cavity = False
    scene.display.shading.show_object_outline = False
    scene.render.resolution_x = 1600
    scene.render.resolution_y = int(1600 * max(0.32, min(1.0, (height * 1.3) / (total * 1.08))))
    scene.render.film_transparent = False
    world = scene.world or bpy.data.worlds.new('w')
    scene.world = world
    scene.display.shading.background_type = 'VIEWPORT'
    scene.render.filepath = os.path.join(folder, kind + '.png')
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam, do_unlink=True)
    for ob in objs:
        ob.location.x = 0

# ---- the models -------------------------------------------------------------------------------------------

BARK = hexc('#5b3c22')
BARK_LIGHT = hexc('#7a5434')
LEAF_PALETTES = [  # light, mid, dark, highlight (the 2D game's LEAF_PALETTES)
    ['#7fb35a', '#4f8a3a', '#2c5a25', '#b5d98a'],
    ['#8fbf5f', '#5a9440', '#30602a', '#c7e39a'],
    ['#6aa34f', '#3f7a33', '#244d22', '#a7d07f'],
]

def trunk(k, height, r0, lean, rnd, branches=2, top_r=None):
    """A slightly crooked trunk with a few branches; returns the trunk's top point and the branch tips."""
    pts = []
    segs = 5
    for i in range(segs + 1):
        t = i / segs
        pts.append((lean[0] * t * t + (rnd.random() - 0.5) * 0.08 * t, lean[1] * t * t + (rnd.random() - 0.5) * 0.08 * t, height * t))
    k.tube(pts, r0, top_r if top_r is not None else r0 * 0.45, BARK, sides=7)
    # Flared roots.
    for i in range(4):
        a = i / 4 * math.tau + rnd.random() * 0.6
        d = Vector((math.cos(a), math.sin(a), 0))
        k.tube([d * r0 * 0.5 + Vector((0, 0, 0.35)), d * (r0 * 1.6) + Vector((0, 0, -0.05))], r0 * 0.38, r0 * 0.12, shade(BARK, 0.9), sides=5)
    tips = []
    for i in range(branches):
        a = rnd.random() * math.tau
        z0 = height * (0.62 + rnd.random() * 0.25)
        base = Vector((lean[0] * 0.7, lean[1] * 0.7, z0))
        d = Vector((math.cos(a), math.sin(a), 0.9)).normalized()
        tip = base + d * (height * 0.35)
        k.tube([base, base + d * height * 0.18 + Vector((0, 0, 0.1)), tip], r0 * 0.4, r0 * 0.15, BARK, sides=5)
        tips.append(tip)
    return Vector((lean[0], lean[1], height)), tips

def leafy_tree(i):
    rnd = random.Random(100 + i)
    k = Kit(100 + i)
    k.rnd = rnd
    pal = [hexc(c) for c in LEAF_PALETTES[i % 3]]
    light, mid, dark, high = pal
    height = rnd.uniform(3.6, 4.6)
    R = rnd.uniform(1.9, 2.6)
    lean = (rnd.uniform(-0.35, 0.35), rnd.uniform(-0.35, 0.35))
    top, tips = trunk(k, height + R * 0.4, 0.3, lean, rnd, branches=3)
    centre = Vector((lean[0], lean[1], height + R * 0.75))
    # The canopy: a crown and lobes melted into one leafy mass, darker underneath, lighter on top,
    # with sunlit clumps on the upper faces.
    parts = [(centre, (R * 0.95, R * 0.95, R * 0.8), mid, 2, 0.2, 1.3)]
    lobes = 6 + rnd.randrange(3)
    for j in range(lobes):
        a = j / lobes * math.tau + rnd.random() * 0.5
        d = Vector((math.cos(a), math.sin(a), 0))
        r = R * rnd.uniform(0.5, 0.65)
        c = centre + d * R * rnd.uniform(0.55, 0.75) + Vector((0, 0, rnd.uniform(-0.4, 0.35) * R))
        parts.append((c, (r, r, r * 0.85), mid, 1, 0.25, 1.8))
    for tip in tips:
        parts.append((tip + Vector((0, 0, 0.25)), R * 0.42, mid, 1, 0.25, 1.8))
    parts.append((centre + Vector((0, 0, R * 0.45)), (R * 0.65, R * 0.65, R * 0.5), mid, 1, 0.25, 1.8))
    off = Vector((rnd.random() * 9, rnd.random() * 9, rnd.random() * 9))

    def leaf(f):
        c = f.calc_center_median()
        n = f.normal
        rel = (c.z - centre.z) / R
        blotch = noise.noise(c * 0.9 + off)
        if n.z < -0.35 or rel < -0.45:
            return dark
        if n.z > 0.45 and rel > 0.15 and blotch > 0.1:
            return high
        if rel > 0.05 or (n.z > 0.2 and blotch > 0):
            return light
        return mid

    k.blob(parts, voxel=R * 0.18, ratio=0.18, colorize=leaf)
    return k.build(f'tree_{i}', ao=0.3)

def pine(i):
    rnd = random.Random(200 + i)
    k = Kit(200 + i)
    k.rnd = rnd
    tiers = [hexc(c) for c in ('#1f3d2a', '#24452f', '#2b5236', '#326040', '#386a43', '#4c8452')]
    h = rnd.uniform(7.5, 10.5)
    bare = 2.9
    R = rnd.uniform(1.8, 2.4)
    k.tube([(0, 0, 0), (0, 0, bare + h * 0.5), (0, 0, bare + h * 0.9)], 0.3, 0.06, BARK, sides=7)
    for j in range(4):
        a = j / 4 * math.tau + rnd.random()
        d = Vector((math.cos(a), math.sin(a), 0))
        k.tube([d * 0.15 + Vector((0, 0, 0.3)), d * 0.55 + Vector((0, 0, -0.05))], 0.14, 0.05, shade(BARK, 0.9), sides=5)
    n = 6
    for t in range(n):
        f = t / (n - 1)
        y = bare + h * (0.02 + f * 0.7)
        r = R * (1.15 - f * 0.85)
        depth = h * 0.3 * (1 - f * 0.35)
        # A drooping, ragged cone of needles: the rim dips at each point.
        segs = 10
        verts = []
        tip = k.bm.verts.new((rnd.uniform(-0.05, 0.05), rnd.uniform(-0.05, 0.05), y + depth))
        rim = []
        twist = rnd.random()
        for s in range(segs * 2):
            a = s / (segs * 2) * math.tau + twist
            rr = r * (1 if s % 2 == 0 else 0.7) * rnd.uniform(0.9, 1.1)
            dz = -0.35 * r * 0.25 if s % 2 == 0 else 0.05
            rim.append(k.bm.verts.new((math.cos(a) * rr, math.sin(a) * rr, y + dz)))
        inner = k.bm.verts.new((0, 0, y + depth * 0.12))
        faces = []
        for s in range(len(rim)):
            a, b = rim[s], rim[(s + 1) % len(rim)]
            faces.append(k.bm.faces.new((a, b, tip)))
            faces.append(k.bm.faces.new((b, a, inner)))
        col = tiers[min(t, len(tiers) - 1)]
        top_faces = faces[0::2]
        k.paint(top_faces, col, 0.1)
        k.paint(faces[1::2], shade(col, 0.55), 0.05)
    return k.build(f'pine_{i}', ao=0.3)

def bush(i):
    rnd = random.Random(300 + i)
    k = Kit(300 + i)
    k.rnd = rnd
    R = rnd.uniform(0.7, 1.1)
    light, dark, high = hexc('#6a9e4c'), hexc('#3a6629'), hexc('#8fbd66')
    parts = []
    for j in range(5):
        a = rnd.random() * math.tau
        r = R * rnd.uniform(0.5, 0.65)
        parts.append(((math.cos(a) * R * 0.4, math.sin(a) * R * 0.4, R * 0.5 + rnd.uniform(-0.1, 0.25) * R), (r, r, r * 0.85), light, 1, 0.25, 1.8))
    off = Vector((rnd.random() * 9, rnd.random() * 9, 0))

    def leaf(f):
        n = f.normal
        c = f.calc_center_median()
        if n.z < -0.2 or c.z < R * 0.25:
            return dark
        if n.z > 0.5 and noise.noise(c * 2 + off) > 0.15:
            return high
        return light if noise.noise(c * 1.5 + off) > -0.2 else shade(light, 0.85)

    k.blob(parts, voxel=R * 0.22, ratio=0.2, colorize=leaf)
    if i % 5 < 2:
        berry = hexc('#d1495b') if i % 2 == 0 else hexc('#f4f1de')
        for j in range(10):
            a, e = rnd.random() * math.tau, rnd.uniform(0.2, 1.0)
            p = Vector((math.cos(a) * R * 0.62, math.sin(a) * R * 0.62, R * 0.4 + math.sin(e) * R * 0.4))
            k.ico(p, 0.06, berry, subdiv=1, rough=0)
    return k.build(f'bush_{i}', ao=0.35)

ROCK, ROCK_LIGHT, MOSS = hexc('#8b8f97'), hexc('#b0b4bc'), hexc('#6e9646')

def rock(i, small):
    seed = (450 if small else 400) + i
    rnd = random.Random(seed)
    k = Kit(seed)
    k.rnd = rnd
    R = rnd.uniform(0.3, 0.6) if small else rnd.uniform(0.8, 1.7)
    parts = [((0, 0, R * 0.3), (R, R * rnd.uniform(0.75, 0.95), R * 0.7), ROCK, 2, 0.35, 1.2)]
    if not small:
        for j in range(2):
            a = rnd.random() * math.tau
            parts.append(((math.cos(a) * R * 0.55, math.sin(a) * R * 0.55, R * 0.15), (R * 0.55, R * 0.5, R * 0.45), ROCK, 1, 0.3, 1.5))
    off = Vector((rnd.random() * 9, rnd.random() * 9, 0))
    mossy = not small and i % 5 < 3

    def stone(f):
        n = f.normal
        c = f.calc_center_median()
        if mossy and n.z > 0.6 and noise.noise(c * 1.3 + off) > -0.1:
            return MOSS
        if n.z > 0.35:
            return ROCK_LIGHT
        return ROCK if noise.noise(c * 2 + off) > -0.3 else shade(ROCK, 0.85)

    faces = k.blob(parts, voxel=R * 0.22, ratio=0.1 if not small else 0.16, colorize=stone)
    # Sit it in the ground: drop it so a little is buried.
    for v in {v for f in faces for v in f.verts}:
        v.co.z -= R * 0.12
    return k.build(f'{"stone" if small else "boulder"}_{i}', ao=0.25)

def boulder(i):
    return rock(i, False)

def stone(i):
    return rock(i, True)

def driftwood(i):
    rnd = random.Random(800 + i)
    k = Kit(800 + i)
    k.rnd = rnd
    wood, dark, end = hexc('#b39c7d'), hexc('#8a7558'), hexc('#d6c3a3')
    L = rnd.uniform(2.0, 2.8)
    pts = [(-L / 2 + L * t, math.sin(t * 3 + i) * 0.12, 0.18 + 0.04 * math.sin(t * 5)) for t in [j / 6 for j in range(7)]]
    verts, faces = k.tube(pts, 0.2, 0.14, wood, sides=7, jitter=0.12)
    k.paint(faces[-2:], end, 0.02)
    for f in faces[:-2]:
        if f.normal.z < -0.2:
            k.paint([f], dark, 0.04)
    for j in range(2):
        x = rnd.uniform(-L * 0.3, L * 0.3)
        k.tube([(x, 0, 0.3), (x + rnd.uniform(-0.2, 0.2), rnd.choice([-1, 1]) * 0.35, 0.55)], 0.06, 0.03, wood, sides=5)
    return k.build(f'driftwood_{i}', ao=0.2)

# ---- recolourable regions -----------------------------------------------------------------------------
# Parts the game colours itself (a shop's awning, an angler's jacket, a hat's band) are painted white
# with a region code in the alpha channel: 254 = A, 253 = B, 252 = C, 251 = D, 250 = E. The game swaps
# in the real colour, keeping the shading (Models.Recolour).

REGIONS = {'A': 254, 'B': 253, 'C': 252, 'D': 251, 'E': 250}

def region(name, brightness=1.0):
    return (brightness, brightness, brightness, REGIONS[name] / 255)

# ---- boats ----------------------------------------------------------------------------------------------

def hull(k, stations, deck_z, keel, colors, bulwark=0.5, rail=None):
    """A boat hull, bow towards +Y. stations: [(y, half-beam)] from stern to bow; keel(y) gives the keel's depth.
    colors: (topsides, boot stripe, bottom, deck, bulwark inside). rail: (height, colour) for a railing above
    the bulwark. Returns the deck outline (inside the bulwark)."""
    top_c, stripe_c, bottom_c, deck_c, inner_c = colors
    rings = []
    for y, w in stations:
        kz = keel(y)
        rise = 0.35 * max(0, (y - stations[-3][0]) / (stations[-1][0] - stations[-3][0] + 1e-6))  # sheer rises at the bow
        top = deck_z + bulwark + rise
        prof = [(w, top), (w * 0.99, 0.35), (w * 0.86, -0.35), (w * 0.5, kz * 0.75), (min(0.18, w), kz)]
        ring = [(x, y, z) for x, z in prof] + [(-x, y, z) for x, z in reversed(prof)]
        rings.append([k.bm.verts.new(p) for p in ring])
    n = len(rings[0])
    half = n // 2
    skin, inside, up = [], [], []
    for i in range(len(rings) - 1):
        a, b = rings[i], rings[i + 1]
        for j in range(n - 1):
            if j == half - 1:
                continue  # the keel line joins the two sides
            f = k.bm.faces.new((a[j], b[j], b[j + 1], a[j + 1]))
            seg = j if j < half else n - 2 - j
            k.paint([f], [top_c, stripe_c, bottom_c, bottom_c][min(seg, 3)], 0.04)
            skin.append(f)
        f = k.bm.faces.new((a[half - 1], b[half - 1], b[half], a[half]))
        k.paint([f], bottom_c, 0.02)
        skin.append(f)
    k.orient(skin, lambda f: Vector((f.calc_center_median().x, 0, min(0.0, f.calc_center_median().z) * 0.5 - 0.01)))
    # Transom at the stern, and the stem closed at the bow.
    for ring, out in ((rings[0], -1), (rings[-1], 1)):
        f = k.bm.faces.new(ring)
        k.paint([f], top_c if out < 0 else bottom_c, 0.02)
        k.orient([f], lambda f, out=out: Vector((0, out, 0)))
    # The deck, inside the bulwarks, and the bulwarks' inner faces and capping.
    inner = []
    for y, w in stations:
        inner.append((max(0.05, w - 0.15), y))
    outline = [(x, y) for x, y in inner] + [(-x, y) for x, y in reversed(inner)]
    deck = [k.bm.verts.new((x, y, deck_z)) for x, y in outline]
    f = k.bm.faces.new(deck)
    k.paint([f], deck_c, 0.0)
    up.append(f)
    for i in range(len(stations) - 1):
        for side in (1, -1):
            (y0, w0), (y1, w1) = stations[i], stations[i + 1]
            r0 = 0.35 * max(0, (y0 - stations[-3][0]) / (stations[-1][0] - stations[-3][0] + 1e-6))
            r1 = 0.35 * max(0, (y1 - stations[-3][0]) / (stations[-1][0] - stations[-3][0] + 1e-6))
            t0, t1 = deck_z + bulwark + r0, deck_z + bulwark + r1
            x0i, x1i = side * max(0.05, w0 - 0.15), side * max(0.05, w1 - 0.15)
            q = [(x0i, y0, deck_z), (x1i, y1, deck_z), (x1i, y1, t1), (x0i, y0, t0)]
            f = k.bm.faces.new([k.bm.verts.new(p) for p in q])
            k.paint([f], inner_c, 0.03)
            inside.append(f)
            cap = [(x0i, y0, t0), (x1i, y1, t1), (side * w1, y1, t1), (side * w0, y0, t0)]
            f = k.bm.faces.new([k.bm.verts.new(p) for p in cap])
            k.paint([f], shade(inner_c, 1.1), 0.0)
            up.append(f)
    k.orient(up, lambda f: Vector((0, 0, 1)))
    k.orient(inside, lambda f: Vector((-f.calc_center_median().x, 0, 0)))
    if rail:
        h, rc = rail
        for side in (1, -1):
            pts = []
            for y, w in stations[1:-1]:
                r = 0.35 * max(0, (y - stations[-3][0]) / (stations[-1][0] - stations[-3][0] + 1e-6))
                pts.append((side * (w - 0.07), y, deck_z + bulwark + r + h))
            k.tube(pts, 0.05, 0.05, rc, sides=5)
            for (y, w), p in zip(stations[1:-1], pts):
                k.tube([(p[0], p[1], p[2] - h), p], 0.035, 0.035, rc, sides=4, cap=False)
    return outline

def lake_boat(i):
    """The voyage boat on the lake (26 x 7.6 m, deck 1.3 m up), bow towards +Y."""
    k = Kit(30)
    L, B, D = 13.0, 3.8, 1.3
    stations = [(-L, B * 0.92), (-L * 0.7, B), (-L * 0.2, B), (L * 0.25, B), (L * 0.55, B * 0.82), (L * 0.8, B * 0.5), (L, 0.05)]
    hull(k, stations, D, lambda y: -1.0 if y < L * 0.6 else -1.0 + (y - L * 0.6) / (L * 0.4) * 0.9,
         (hexc('#7a2e2e'), hexc('#efe0bd'), hexc('#4a1a1a'), hexc('#b5865a'), hexc('#efe0bd')), bulwark=0.35,
         rail=(0.55, hexc('#efe0bd')))
    # Wheelhouse at the stern: cream walls, windows, red roof.
    cy = -L * 0.45
    k.box((0, cy, D + 1.2), (B * 1.2, L * 0.4, 2.4), hexc('#f1ece0'))
    k.box((0, cy, D + 2.5), (B * 1.36, L * 0.46, 0.2), hexc('#a33a3a'))
    for w in (-1, 0, 1):
        k.box((w * B * 0.38, cy + L * 0.2 + 0.01, D + 1.6), (B * 0.28, 0.06, 0.7), hexc('#2b3a44'))
    for side in (-1, 1):
        for w in (-1, 1):
            k.box((side * (B * 0.6 + 0.01), cy + w * L * 0.09, D + 1.6), (0.06, L * 0.12, 0.6), hexc('#2b3a44'))
    k.box((0, cy + L * 0.2 + 0.02, D + 0.75), (0.8, 0.06, 1.5), hexc('#8a5a33'))
    # Mast and flag, a life ring, benches for the passengers.
    k.tube([(0, -L * 0.3, D + 2.6), (0, -L * 0.3, D + 6.2)], 0.09, 0.07, hexc('#5b3c22'), sides=6)
    k.poly([(0.05, -L * 0.3, D + 6.1), (0.05, -L * 0.3 + 0.9, D + 5.85), (0.05, -L * 0.3, D + 5.6)], hexc('#e63946'))
    # Benches along each side, inside the bulwark all the way along (the hull narrows towards the bow).
    def inner(y):
        return interp([(sy, w - 0.15) for sy, w in stations], y)

    for y, length in ((-2.5, 2.4), (1.5, 2.4), (4.6, 2.0)):
        x = min(inner(y - length / 2), inner(y + length / 2)) - 0.32
        for side in (-1, 1):
            k.box((side * x, y, D + 0.25), (0.45, length, 0.08), hexc('#8a5a33'))
            k.box((side * x, y, D + 0.12), (0.35, length - 0.2, 0.24), hexc('#5b3c22'))
    return k.build('boat_0', ao=0.12)

def trawler(i):
    """The trawler at sea, bow towards +Y. It matches the sea world's deck (shared/voyage.js): deck 1.6 m up,
    walkable from y = -17 to +23 within 4.8 m of the middle (and the bow to +27), the cabin at the stern."""
    k = Kit(40)
    D = 1.6
    stations = [(-28.2, 5.4), (-24, 5.6), (-16, 5.6), (-4, 5.6), (10, 5.6), (17, 5.3), (22, 4.7), (26, 3.4), (29.2, 1.8), (30.9, 0.05)]
    hull(k, stations, D, lambda y: -1.9 if y < 20 else -1.9 + (y - 20) / 11 * 1.6,
         (hexc('#1d3557'), hexc('#f1faee'), hexc('#9d0208'), hexc('#a07850'), hexc('#e9e4d8')), bulwark=0.45,
         rail=(0.6, hexc('#f1faee')))
    for y in [-16.5 + j * 1.4 for j in range(29)]:
        k.box((0, y, D + 0.004), (9.6, 0.05, 0.01), hexc('#7d5c3c'))
    # Cabin (the sea world's solid block) and wheelhouse, with windows, a door and portholes.
    cabin, roof, glass = hexc('#f1ece0'), hexc('#e63946'), hexc('#2b3a44')
    k.box((0, -22.6, D + 1.6), (11, 11, 3.2), cabin)
    k.box((0, -22.6, D + 3.3), (11.6, 11.6, 0.25), roof)
    k.box((0, -24, D + 4.3), (6, 5.5, 1.9), cabin)
    k.box((0, -24, D + 5.35), (6.6, 6.1, 0.22), roof)
    for w in (-1, 0, 1):
        k.box((w * 3.2, -17.08, D + 2.1), (2.0, 0.06, 0.9), glass)
        k.box((w * 1.9, -21.23, D + 4.5), (1.4, 0.06, 0.8), glass)
    for side in (-1, 1):
        for y in (-25.5, -22.5, -19.5):
            k.cyl((side * 5.5, y, D + 2.0), (side * 5.56, y, D + 2.0), 0.32, 0.32, glass, sides=8)
    k.box((0, -17.06, D + 1.05), (1.1, 0.06, 2.1), hexc('#8a5a33'))
    # Life rings either side of the door.
    for x in (-4.2, 4.2):
        pts = [(x + math.cos(a) * 0.45, -17.02, D + 1.8 + math.sin(a) * 0.45) for a in [j / 12 * math.tau for j in range(13)]]
        k.tube(pts, 0.11, 0.11, roof, sides=5, cap=False)
    # Mast on the cabin roof with a crosstree, lamps and a boom; a radar on the wheelhouse.
    wood = hexc('#5b3c22')
    k.tube([(0, -19.4, D + 3.4), (0, -19.4, D + 11.3)], 0.16, 0.11, wood, sides=6)
    k.tube([(-2, -19.4, D + 9.6), (2, -19.4, D + 9.6)], 0.08, 0.08, wood, sides=5)
    k.ico((0, -19.4, D + 11.4), 0.22, hexc('#ffd166'), subdiv=1, rough=0)
    k.tube([(0, -19.4, D + 4.2), (0, -14.0, D + 6.6)], 0.09, 0.06, wood, sides=5)
    k.box((0, -24, D + 5.7), (1.6, 0.2, 0.12), hexc('#d9d9d9'))
    k.box((0, -24, D + 5.55), (0.15, 0.15, 0.3), hexc('#6c757d'))
    # Fish crates stacked in the back corners of the deck (solid in the sea world), and a coiled net at the bow.
    crates = [hexc('#2a9d8f'), hexc('#f4a261'), hexc('#457b9d')]
    c = 0
    for x in (-4.4, 4.4):
        for h in range(2):
            k.box((x, -16.2, D + 0.3 + h * 0.6), (0.9, 0.7, 0.55), crates[c % 3])
            c += 1
    k.blob([((0, 24.6, D + 0.25), (1.0, 0.9, 0.35), hexc('#6b8f71'), 1, 0.3, 2.0)], 0.18, 0.3,
           lambda f: hexc('#6b8f71') if f.normal.z > 0.3 else hexc('#4f6b55'))
    # Name boards on the bow.
    for side in (-1, 1):
        k.box((side * 4.95, 22.5, D - 0.3), (0.05, 2.8, 0.4), hexc('#f1faee'))
    return k.build('trawler_0', ao=0.1)

# ---- shops, the zoo, signposts ------------------------------------------------------------------------------

def stall(i):
    """A shop stall: plank floor, counter (facing -Y, the customer's side), back wall, a striped awning
    (regions A and B: the shop's colours) and goods: 0 = bait buckets, 1 = rods and reels."""
    k = Kit(50 + i)
    wood, dark, light = hexc('#8a5a33'), hexc('#5b3c22'), hexc('#b5865a')
    for j in range(8):
        k.box((-2.65 + j * 0.76, 0, 0.08), (0.72, 4, 0.16), shade(wood, 0.8 + 0.08 * (j % 3)))
    # Counter: planked front, a lighter top.
    k.box((0, 1.4, 0.6), (5.6, 0.9, 1.1), wood)
    k.box((0, 1.4, 1.17), (5.8, 1.0, 0.08), light)
    for j in range(7):
        k.box((-2.4 + j * 0.8, 0.93, 0.6), (0.06, 0.04, 1.05), dark)
    k.box((0, -1.85, 1.45), (5.8, 0.2, 2.7), shade(wood, 0.85))
    for j in range(4):
        k.box((0, -1.74, 0.5 + j * 0.65), (5.7, 0.03, 0.05), dark)
    for x in (-2.8, 2.8):
        for y in (-1.85, 1.85):
            k.box((x, y, 1.5), (0.22, 0.22, 3.0), dark)
    # The awning, sloping down to the front, with a scalloped valance.
    stripes = 8
    for s in range(stripes):
        x0 = -3.2 + s * 6.4 / stripes
        x1 = x0 + 6.4 / stripes
        r = region('A' if s % 2 == 0 else 'B')
        _, top = k.poly([(x0, 2.4, 2.75), (x1, 2.4, 2.75), (x1, -2.1, 3.3), (x0, -2.1, 3.3)], r, both=False)
        k.orient(top, lambda f: Vector((0, 0, 1)))
        _, under = k.poly([(x0, -2.1, 3.28), (x1, -2.1, 3.28), (x1, 2.4, 2.73), (x0, 2.4, 2.73)], region('A' if s % 2 == 0 else 'B', 0.7), both=False)
        k.orient(under, lambda f: Vector((0, 0, -1)))
        k.poly([(x0, 2.4, 2.75), (x0, 2.4, 2.45), ((x0 + x1) / 2, 2.4, 2.3), (x1, 2.4, 2.45), (x1, 2.4, 2.75)], region('A' if s % 2 == 0 else 'B', 0.9))
    k.tube([(-3.25, 2.42, 2.76), (3.25, 2.42, 2.76)], 0.05, 0.05, dark, sides=5)
    if i == 1:
        # A rack of rods beside the counter, reels on the counter.
        k.box((3.6, 0.6, 0.9), (0.2, 1.6, 1.8), dark)
        for r in range(4):
            col = region('A') if r % 2 == 0 else hexc('#2b2d42')
            k.tube([(3.75, -0.0 + r * 0.4, 0.05), (3.75, 0.0 + r * 0.4 + 0.1, 3.2)], 0.035, 0.015, col, sides=4)
        for r in range(3):
            k.cyl((-1.6 + r * 0.8 - 0.15, 1.4, 1.36), (-1.6 + r * 0.8 + 0.15, 1.4, 1.36), 0.17, 0.17, region('B' if r == 1 else 'A'), sides=8)
        k.box((1.4, 1.4, 1.3), (0.9, 0.5, 0.18), hexc('#4a3b2a'))
    else:
        fills = [hexc('#d97c8a'), hexc('#ffd60a'), hexc('#7a2e3a')]
        for b in range(3):
            x = -1.6 + b * 0.8
            k.cyl((x, 1.4, 1.21), (x, 1.4, 1.56), 0.28, 0.32, hexc('#6c757d'), sides=10)
            k.cyl((x, 1.4, 1.5), (x, 1.4, 1.52), 0.3, 0.3, fills[b], sides=10)
            k.tube([(x - 0.3, 1.4, 1.55), (x, 1.4, 1.8), (x + 0.3, 1.4, 1.55)], 0.015, 0.015, hexc('#343a40'), sides=4, cap=False)
        k.cyl((3.6, 1.0, 0), (3.6, 1.0, 1.1), 0.45, 0.45, wood, sides=10)
        for z in (0.2, 0.9):
            k.cyl((3.6, 1.0, z - 0.04), (3.6, 1.0, z + 0.04), 0.47, 0.47, hexc('#4a4a4a'), sides=10)
        k.box((-3.4, 1.2, 0.35), (0.7, 0.6, 0.7), shade(wood, 0.9))
    k.mirror_y()  # built with the counter at +Y; the customer's side is -Y (south in the game)
    return k.build(f'stall_{i}', ao=0.18)

def zoo_wagon(i):
    """The Travelling Zoo's wagon, its sign side towards -Y: a gold-striped bed on four spoked wheels, a striped
    tent (purple and cream), barred windows and lanterns. The flag on top is added by the game (it flaps)."""
    k = Kit(60)
    bed, gold, wheel, hub = hexc('#7a3e1d'), hexc('#ffd166'), hexc('#3a2410'), hexc('#b5865a')
    purple, cream, pole = hexc('#9d4edd'), hexc('#fdfcdc'), hexc('#5a189a')
    k.box((0, 0, 1.25), (6.4, 3, 0.9), bed)
    k.box((0, 0, 1.3), (6.46, 3.06, 0.16), gold)
    k.box((0, -1.53, 1.25), (3.2, 0.04, 0.55), hexc('#3a2410'))  # the sign board (the game writes on it)
    for x in (-2.2, 2.2):
        for y in (-1.6, 1.6):
            # Spoked wheels turning about the Y axis.
            s = 1 if y > 0 else -1
            pts = [(x + math.cos(a) * 0.8, y, 0.8 + math.sin(a) * 0.8) for a in [j / 14 * math.tau for j in range(15)]]
            k.tube(pts, 0.09, 0.09, wheel, sides=5, cap=False)
            for j in range(6):
                a = j / 6 * math.tau
                k.tube([(x, y, 0.8), (x + math.cos(a) * 0.75, y, 0.8 + math.sin(a) * 0.75)], 0.035, 0.035, hub, sides=4, cap=False)
            k.cyl((x, y - s * 0.12, 0.8), (x, y + s * 0.12, 0.8), 0.14, 0.14, hub, sides=8)
    k.tube([(-3.2, -1.2, 1.0), (-4.6, 0, 0.55), (-3.2, 1.2, 1.0)], 0.06, 0.06, hub, sides=4, cap=False)  # the shafts
    # Striped tent up to a point, with a wooden frame along its edges.
    apex = Vector((0, 0, 5.6))
    corners = [Vector((-3.2, -1.5, 1.7)), Vector((3.2, -1.5, 1.7)), Vector((3.2, 1.5, 1.7)), Vector((-3.2, 1.5, 1.7))]
    stripe = 0
    for side in range(4):
        a, b = corners[side], corners[(side + 1) % 4]
        n = 4 if side % 2 == 0 else 2
        for s in range(n):
            p0, p1 = a.lerp(b, s / n), a.lerp(b, (s + 1) / n)
            c = purple if stripe % 2 == 0 else cream
            stripe += 1
            k.poly([p1, p0, apex], c)
        k.tube([a, apex], 0.05, 0.04, hub, sides=4, cap=False)
    k.tube([(0, 0, 5.5), (0, 0, 6.7)], 0.06, 0.05, pole, sides=5)
    # Barred windows on the long sides, lanterns on the corners.
    for y in (-1.52, 1.52):
        for x in (-1.6, 1.6):
            k.box((x, y, 2.4), (1.0, 0.04, 0.6), hexc('#2b1d10'))
            for b in range(4):
                k.box((x - 0.36 + b * 0.24, y + (0.02 if y > 0 else -0.02), 2.4), (0.04, 0.03, 0.62), gold)
    for x in (-3.1, 3.1):
        k.ico((x, -1.6, 2.0), 0.13, hexc('#ffd166'), subdiv=1, rough=0)
        k.box((x, -1.6, 2.17), (0.18, 0.18, 0.05), hexc('#3a2410'))
    return k.build('zoo_0', ao=0.15)

def signpost(i):
    """0: the post (the board goes 2.35 m up); 1: a board 1 m wide, scaled to fit each name."""
    k = Kit(70 + i)
    wood, dark, light = hexc('#8a5a33'), hexc('#5b3c22'), hexc('#b5865a')
    if i == 0:
        k.tube([(0, 0, -0.1), (0.02, 0.01, 1.5), (0, 0, 2.95)], 0.1, 0.08, dark, sides=6)
        k.cyl((0, 0, 2.95), (0, 0, 3.05), 0.11, 0.0, dark, sides=6)
        for j in range(4):
            a = j / 4 * math.tau + 0.4
            k.ico((math.cos(a) * 0.28, math.sin(a) * 0.28, 0.05), (0.2, 0.17, 0.13), ROCK, subdiv=1, rough=0.25)
        k.box((0, 0, 2.0), (0.5, 0.11, 0.06), dark)
        return k.build('signpost_0', ao=0.2)
    k.box((0, 0, 0), (1.0, 0.1, 0.6), wood)
    for z in (-0.15, 0.0, 0.15):
        k.box((0, 0, z), (1.0, 0.105, 0.012), shade(wood, 0.85))
    for z in (-0.32, 0.32):
        k.box((0, 0, z), (1.04, 0.12, 0.06), dark)
    return k.build('signpost_1', ao=0)

# ---- anglers and tackle -------------------------------------------------------------------------------------

SKIN = hexc('#f1c27d')
SKIN_DARK = hexc('#d9a066')
HAIR = hexc('#5b3a1e')
EYE = hexc('#1e1e1e')

# The angler's skeleton (the game rigs it with these joints: World/LimbRig.cs, Net/RemoteAngler.cs): facing +Y,
# feet at z = 0, hips 0.9, knees 0.5, shoulders 1.42, neck 1.52. Limbs: 1 left leg, 2 right leg, 3 left arm,
# 4 right arm (holding the rod out in front, the hand at (0.32, 0.27, 1.15)), 5 the head.
ARM = {-1: ((-0.3, 0, 1.42), (-0.36, 0.03, 1.17), (-0.33, 0.09, 0.96)),
       1: ((0.3, 0, 1.42), (0.38, 0.12, 1.22), (0.33, 0.24, 1.16))}
HAND = {-1: (-0.33, 0.11, 0.91), 1: (0.32, 0.27, 1.15)}

def leg_t(co):  # hip (z 0.9) 0, knee (z 0.5) 0.5, sole 1
    return 0.5 * (0.9 - co.z) / 0.4 if co.z > 0.5 else 0.5 + 0.5 * (0.5 - co.z) / 0.5

def leg_of(co):  # a coat's skirt follows the leg it's beside
    return 1 if co.x < 0 else 2

def arm_t(side):
    a, e, w = ARM[side]
    return jointed(a, e, HAND[side])

def head_t(co):
    return 0.0

def torso(k, colour, bottom=0.86, bulk=0.0, top=1.52, flare=0.0, skirt=None, sides=18):
    """A torso from the hips (or lower, a coat's skirt) to the neck. bulk: extra thickness; flare: how much a
    skirt below the hips widens. Anything below the hips is split between the legs (skirt: its lowest point)."""
    b = 1 + bulk
    secs = [(0.86, 0.205, 0.15, 0.0), (0.95, 0.21, 0.145, 0.0), (1.05, 0.215, 0.15, 0.005), (1.18, 0.245, 0.16, 0.01),
            (1.3, 0.265, 0.165, 0.01), (1.4, 0.28, 0.155, 0.0), (1.47, 0.2, 0.125, 0.0), (1.52, 0.1, 0.09, 0.0)]
    secs = [(z, rx * b, ry * b, cy) for z, rx, ry, cy in secs if z <= top + 1e-6]
    if skirt is not None:
        lower = []
        for j in range(1, 4):
            z = 0.86 - (0.86 - skirt) * j / 3
            lower.append((z, (0.21 + flare * j / 3) * b, (0.155 + flare * 0.7 * j / 3) * b, 0.0))
        secs = list(reversed(lower)) + secs
        with k.limb(lambda co: leg_of(co) if co.z < 0.84 else 0, lambda co: leg_t(co) if co.z < 0.84 else 0):
            return k.vloft(secs, colour, sides=sides)
    return k.vloft(secs, colour, sides=sides)

def sleeve(k, side, colour, r0=0.085, r1=0.065, cuff=None, cuff_r=0.0, shoulder=None, upto=1.0):
    """A sleeve down an arm (moving with it), with a cuff at the wrist and a rounded shoulder."""
    a, e, w = (Vector(p) for p in ARM[side])
    w = e.lerp(w, upto)
    with k.limb(4 if side > 0 else 3, arm_t(side)):
        k.tube_r([a, a.lerp(e, 0.5), e, e.lerp(w, 0.5), w], [r0, r0 * 0.97, (r0 + r1) / 2 + 0.005, r1 * 1.02, r1], colour, sides=8)
        k.ico(a + Vector((side * 0.01, 0, 0.005)), (r0 * 1.15, r0 * 1.05, r0 * 1.1), shoulder or colour, subdiv=1, rough=0.02)
        if cuff:
            d = (w - e).normalized()
            k.tube_r([w - d * 0.045, w + d * 0.01], [cuff_r or r1 * 1.15, cuff_r or r1 * 1.15], cuff, sides=8)

def trousers(k, colour, r=(0.11, 0.1, 0.085), bottom=0.1, pelvis=True, belt=None, knees=None, stripe=None):
    """Both legs from the hips down to the ankles (moving with them), and the seat."""
    for limb, x in ((1, -0.12), (2, 0.12)):
        with k.limb(limb, leg_t):
            faces = k.tube_r([(x, 0, 0.93), (x * 1.02, 0.012, 0.7), (x * 1.02, 0.015, 0.5), (x * 1.04, 0.005, 0.3), (x * 1.05, 0, bottom)],
                             [r[0], r[0] * 0.95, r[1], r[1] * 0.97, r[2]], colour, sides=9)
            if stripe:
                for f in faces:
                    c = f.calc_center_median()
                    if abs(c.x - x * 1.03) > r[1] * 0.7 and (c.x - x) * x > 0:
                        k.paint([f], stripe, 0.02)
            if knees:
                k.ico((x * 1.02, 0.08, 0.5), (0.075, 0.05, 0.08), knees, subdiv=1, rough=0.05)
    if pelvis:
        k.vloft([(0.8, 0.19, 0.13, 0.0), (0.88, 0.215, 0.15, 0.0), (1.0, 0.21, 0.148, 0.0)], colour, sides=16)
    if belt:
        pts = [(0.218 * math.cos(a), 0.155 * math.sin(a), 0.985) for a in [j / 16 * math.tau for j in range(17)]]
        k.tube(pts, 0.025, 0.025, belt, sides=4, cap=False)
        k.box((0, 0.158, 0.985), (0.07, 0.02, 0.055), hexc('#c9a227'))

def boots(k, colour, height=0.26, top_r=0.1, sole=None, toe=None, cuff=None, style='boot', trim=None):
    """A pair of boots (each moving with its leg's shin)."""
    sole = sole or shade(colour, 0.55)
    for limb, x in ((1, -0.12), (2, 0.12)):
        xx = x * 1.05
        with k.limb(limb, leg_t):
            big = 1.25 if style == 'diving' else 1.0
            k.box((xx, 0.05, 0.022), (0.13 * big, 0.31 * big, 0.044), sole)
            k.ico((xx, 0.065, 0.085), (0.072 * big, 0.155 * big, 0.068 * big), colour, subdiv=2, rough=0.02)
            if style == 'pointed':
                k.cyl((xx, 0.18, 0.06), (xx, 0.3, 0.05), 0.045, 0.0, colour, sides=6)
            k.tube_r([(xx, 0, 0.05), (xx, 0, height * 0.6), (xx, 0, height)], [0.085 * big, 0.09 * big, top_r * big], colour, sides=9)
            if cuff:
                k.tube_r([(xx, 0, height - 0.02), (xx, 0, height + 0.07)], [top_r * big + 0.02, top_r * big + 0.03], cuff, sides=9)
            if toe:
                k.ico((xx, 0.15 * big, 0.08), (0.07 * big, 0.06 * big, 0.055 * big), toe, subdiv=1, rough=0)
            if style == 'laced':
                for z in (0.12, 0.16, 0.2):
                    k.box((xx, 0.085, z), (0.07, 0.015, 0.012), trim or hexc('#e9dcc0'))
            if style == 'sneaker':
                k.box((xx, 0.05, 0.05), (0.135, 0.3, 0.02), trim or hexc('#e63946'))
            if style == 'hobnail':
                for j in range(6):
                    k.ico((xx + 0.065 * (1 if j % 2 else -1), -0.08 + j * 0.05, 0.02), 0.012, hexc('#c0c7cf'), subdiv=1, rough=0)
            if trim and style not in ('laced', 'sneaker'):
                k.tube_r([(xx, 0, height - 0.035), (xx, 0, height - 0.005)], [top_r + 0.008, top_r + 0.008], trim, sides=9)

def collar(k, colour, z=1.5, r=(0.12, 0.1), height=0.06, flare=0.02):
    k.vloft([(z - 0.01, r[0], r[1], 0.0), (z + height, r[0] + flare, r[1] + flare, 0.0)], colour, sides=14, bottom=False, top=False)

def buttons(k, colour, xs, zs, y=0.168, r=0.014):
    for x in xs:
        for z in zs:
            k.ico((x, y + 0.004 * (1 - (z - 1.0)), z), r, colour, subdiv=1, rough=0)

def cape(k, colour, lining, bottom=0.45, width=0.36, trim=None):
    """A cape from the shoulders, hanging behind (it stays with the body)."""
    pts_top = [(-width * 0.8, -0.13, 1.45), (width * 0.8, -0.13, 1.45)]
    pts_bot = [(width * 1.1, -0.3, bottom), (-width * 1.1, -0.3, bottom)]
    k.fin(pts_top + pts_bot, 0.02, colour)
    if trim:
        k.tube([(-width * 1.1, -0.3, bottom), (width * 1.1, -0.3, bottom)], 0.018, 0.018, trim, sides=4)
    for x in (-1, 1):
        k.ico((x * width * 0.8, -0.1, 1.45), 0.035, trim or lining, subdiv=1, rough=0)

def stars(k, count, area, colour, r=0.012, seed=0):
    """Little bright studs scattered over an area: [(x0, x1), (y0, y1), (z0, z1)] (stars on a robe, gems)."""
    rnd = random.Random(seed)
    for _ in range(count):
        k.ico((rnd.uniform(*area[0]), rnd.uniform(*area[1]), rnd.uniform(*area[2])), r * rnd.uniform(0.7, 1.3), colour, subdiv=1, rough=0)

def angler(i):
    """The angler underneath the armour: head and face, hair and beard, neck and hands (the clothes are gear_*)."""
    k = Kit(80)
    with k.limb(5, head_t):
        k.cyl((0, 0, 1.48), (0, 0.005, 1.58), 0.07, 0.068, SKIN, sides=10)
        # The head: a skull and a jaw, melted into one.
        k.blob([((0, 0.0, 1.69), (0.135, 0.14, 0.15), SKIN, 2, 0.02, 1.0), ((0, 0.035, 1.6), (0.105, 0.105, 0.075), SKIN, 2, 0.02, 1.0)],
               0.022, 0.3, lambda f: SKIN if f.normal.z > -0.6 else SKIN_DARK)
        k.ico((0, 0.15, 1.665), (0.026, 0.04, 0.036), shade(SKIN, 0.92), subdiv=1, rough=0)  # nose
        k.ico((0, 0.163, 1.65), (0.022, 0.02, 0.016), shade(SKIN, 0.88), subdiv=1, rough=0)
        for x in (-0.05, 0.05):
            k.ico((x, 0.127, 1.705), (0.024, 0.012, 0.016), hexc('#f8f9fa'), subdiv=1, rough=0)  # eyes
            k.ico((x, 0.136, 1.705), 0.011, hexc('#3d6b8a'), subdiv=1, rough=0)
            k.ico((x, 0.141, 1.705), 0.006, EYE, subdiv=1, rough=0)
            k.box((x, 0.13, 1.735), (0.055, 0.016, 0.014), HAIR)  # eyebrows
        for x in (-0.14, 0.14):
            k.ico((x, 0.0, 1.68), (0.02, 0.035, 0.045), shade(SKIN, 0.94), subdiv=1, rough=0)  # ears
        # Hair, a short beard and a moustache.
        k.blob([((0, -0.025, 1.745), (0.152, 0.15, 0.105), HAIR, 2, 0.08, 2.0), ((0, -0.075, 1.66), (0.145, 0.09, 0.085), HAIR, 1, 0.08, 2.0),
                ((0, 0.07, 1.8), (0.11, 0.06, 0.04), HAIR, 1, 0.1, 2.0)],
               0.025, 0.3, lambda f: shade(HAIR, 1.1) if f.normal.z > 0.5 else HAIR)
        k.blob([((0, 0.09, 1.585), (0.105, 0.075, 0.05), HAIR, 1, 0.12, 2.0), ((0.09, 0.05, 1.62), (0.03, 0.05, 0.05), HAIR, 1, 0.1, 2.0),
                ((-0.09, 0.05, 1.62), (0.03, 0.05, 0.05), HAIR, 1, 0.1, 2.0)], 0.02, 0.35, lambda f: HAIR)
        k.ico((0, 0.152, 1.625), (0.05, 0.018, 0.014), shade(HAIR, 0.9), subdiv=1, rough=0.05)  # moustache
        k.box((0, 0.15, 1.61), (0.04, 0.01, 0.008), hexc('#8a3a3a'))  # mouth
    # Hands: the right one gripping (the rod goes through it), the left relaxed.
    for side in (-1, 1):
        h = Vector(HAND[side])
        with k.limb(4 if side > 0 else 3, lambda co: 1.0):
            if side > 0:
                k.ico(h, (0.05, 0.055, 0.05), SKIN, subdiv=2, rough=0.03)
                for j in range(4):
                    k.ico(h + Vector((-0.035, 0.01 + j * 0.0, 0.03 - j * 0.02)), (0.022, 0.03, 0.012), shade(SKIN, 0.95), subdiv=1, rough=0)
                k.ico(h + Vector((0.0, 0.03, 0.04)), (0.016, 0.03, 0.014), shade(SKIN, 0.95), subdiv=1, rough=0)  # thumb on top
            else:
                k.ico(h, (0.04, 0.05, 0.06), SKIN, subdiv=2, rough=0.03)
                k.ico(h + Vector((0, 0.01, -0.06)), (0.035, 0.04, 0.03), shade(SKIN, 0.95), subdiv=1, rough=0)
                k.ico(h + Vector((0.025, 0.04, -0.01)), (0.014, 0.02, 0.03), shade(SKIN, 0.95), subdiv=1, rough=0)
    return k.build('angler_0', ao=0.0)

# ---- armour ------------------------------------------------------------------------------------------------------
# Every armour piece in the game (shared/armour.js, through GameData.json), modelled in its set's colours and
# style, to go on the angler above: gear_<slot>_<set> for the slots head, body, legs and feet, and
# gear_<slot>_default for what you wear with nothing in a slot (a shirt in your colour, jeans, trainers).
# The game puts together whatever four you're wearing (Net/RemoteAngler.cs AnglerMesh).

def set_looks():
    import json
    path = os.path.join(HERE, '..', 'unity-src', 'Assets', 'LastCast', 'Resources', 'GameData.json')
    with open(path, encoding='utf-8') as f:
        data = json.load(f)
    return {s['id']: s['look'] for s in data['sets']}

LOOKS = set_looks()
GEAR_SETS = ['default'] + list(LOOKS)
GEAR = [(slot, s) for s in GEAR_SETS for slot in ('head', 'body', 'legs', 'feet') if not (s == 'default' and slot == 'head')]

def look(set_id):
    lk = LOOKS[set_id]
    c = lambda key, d: hexc(lk[key]) if key in lk else d
    jacket, boot, hat = c('jacket', hexc('#888888')), c('boots', hexc('#333333')), c('hatColor', hexc('#888888'))
    return dict(jacket=jacket, boots=boot, hat=hat, trim=c('trim', shade(hat, 1.2)), glow=c('glow', None), dark=shade(jacket, 0.65))

def gear_head(s):
    L = look(s)
    k = Kit(1000 + GEAR_SETS.index(s))
    H, T, z = L['hat'], L['trim'], 1.76
    with k.limb(5, head_t):
        if s == 'canvas':  # bucket hat, with a band and flies pinned to it
            k.vloft([(z - 0.02, 0.27, 0.27, 0.0), (z + 0.01, 0.25, 0.25, 0.0), (z + 0.02, 0.18, 0.18, 0.0), (z + 0.15, 0.16, 0.16, 0.0), (z + 0.17, 0.12, 0.12, 0.0)], H, sides=16)
            k.vloft([(z + 0.02, 0.183, 0.183, 0.0), (z + 0.06, 0.177, 0.177, 0.0)], shade(H, 0.7), sides=16, bottom=False, top=False)
            for a, col in ((0.6, '#e63946'), (1.2, '#2a9d8f'), (2.2, '#f4a261')):
                p = Vector((math.cos(a) * 0.185, math.sin(a) * 0.185, z + 0.045))
                k.ico(p, 0.016, hexc(col), subdiv=1, rough=0.3)
                k.cyl(p, p + Vector((0, 0, -0.04)), 0.003, 0.003, hexc('#c0c7cf'), sides=3)
        elif s in ('oilskin',):  # sou'wester: brim sloping long at the back, a chin strap
            k.vloft([(z - 0.06, 0.25, 0.3, -0.06), (z - 0.02, 0.2, 0.22, -0.02), (z + 0.04, 0.17, 0.17, 0.0), (z + 0.12, 0.15, 0.15, 0.0), (z + 0.15, 0.08, 0.08, 0.0)], H, sides=16)
            for x in (-1, 1):
                k.tube([(x * 0.15, 0.0, z - 0.02), (x * 0.13, 0.06, 1.6), (0, 0.11, 1.55)], 0.006, 0.006, shade(H, 0.7), sides=3, cap=False)
        elif s == 'reedwalker':  # wide conical reed hat with tufts poking out
            k.vloft([(z - 0.01, 0.34, 0.34, 0.0), (z + 0.02, 0.3, 0.3, 0.0), (z + 0.16, 0.06, 0.06, 0.0), (z + 0.19, 0.01, 0.01, 0.0)],
                    lambda c, n: H if int((math.atan2(c.y, c.x) + 4) * 5) % 2 else shade(H, 0.85), sides=20)
            for j in range(9):
                a = j / 9 * math.tau
                p = Vector((math.cos(a) * 0.3, math.sin(a) * 0.3, z))
                k.tube([p, p + Vector((math.cos(a) * 0.06, math.sin(a) * 0.06, -0.05 + 0.02 * (j % 3)))], 0.006, 0.002, hexc('#c2b27a'), sides=3)
        elif s == 'clover':  # baseball cap with a four-leaf clover on the front
            k.vloft([(z - 0.03, 0.172, 0.175, 0.0), (z + 0.06, 0.16, 0.165, 0.0), (z + 0.11, 0.1, 0.1, 0.0), (z + 0.125, 0.02, 0.02, 0.0)], H, sides=16)
            k.box((0, 0.21, z - 0.02), (0.22, 0.14, 0.02), shade(H, 0.8))
            k.ico((0, 0, z + 0.13), 0.018, T, subdiv=1, rough=0)
            for a in range(4):
                ang = a * math.pi / 2 + math.pi / 4
                k.ico((math.cos(ang) * 0.017, 0.17, z + 0.05 + math.sin(ang) * 0.017), (0.016, 0.006, 0.016), hexc('#b7f5c5'), subdiv=1, rough=0)
        elif s == 'seadog':  # captain's cap: white crown, black peak, gold badge and cord
            k.vloft([(z - 0.03, 0.17, 0.172, 0.0), (z + 0.06, 0.17, 0.175, 0.0), (z + 0.1, 0.2, 0.21, 0.01), (z + 0.13, 0.2, 0.21, 0.01)],
                    lambda c, n: hexc('#f1f1f1') if c.z > z + 0.075 else H, sides=18)
            k.box((0, 0.2, z - 0.015), (0.24, 0.13, 0.02), hexc('#111111'))
            k.ico((0, 0.178, z + 0.045), (0.03, 0.01, 0.025), hexc('#ffd166'), subdiv=1, rough=0)
            k.tube([(-0.16, 0.08, z + 0.005), (0, 0.183, z + 0.01), (0.16, 0.08, z + 0.005)], 0.006, 0.006, hexc('#ffd166'), sides=4)
        elif s == 'trophy':  # wide-brimmed hunter's hat with a feather and badges
            k.vloft([(z - 0.02, 0.32, 0.32, 0.0), (z + 0.01, 0.3, 0.3, 0.0), (z + 0.02, 0.18, 0.18, 0.0), (z + 0.16, 0.16, 0.15, 0.0), (z + 0.18, 0.1, 0.09, 0.0)], H, sides=16)
            k.vloft([(z + 0.02, 0.183, 0.183, 0.0), (z + 0.055, 0.178, 0.178, 0.0)], hexc('#3b2410'), sides=16, bottom=False, top=False)
            k.fin([(0.16, -0.03, z + 0.05), (0.2, -0.12, z + 0.25), (0.19, -0.05, z + 0.07)], 0.006, hexc('#c1121f'))
            k.ico((-0.12, 0.13, z + 0.06), 0.018, hexc('#ffd166'), subdiv=1, rough=0)
        elif s == 'storm':  # a hood up over the head, drawstring and a cyan lining
            hood = k.vloft([(1.55, 0.17, 0.17, -0.02), (1.65, 0.19, 0.19, -0.03), (1.75, 0.185, 0.19, -0.03), (1.84, 0.14, 0.15, -0.03), (1.88, 0.06, 0.07, -0.03)],
                    lambda c, n: L['jacket'] if n.y < 0.6 or c.z > 1.82 else T, sides=18)
            k.cut(hood, lambda f: f.normal.y > 0.5 and 1.56 < f.calc_center_median().z < 1.8)  # open at the face
            k.tube([(0.19 * math.cos(a), 0.13 + 0.05 * math.sin(a), 1.69 + 0.17 * math.sin(a)) for a in [j / 12 * math.pi - math.pi / 2 for j in range(13)]], 0.018, 0.018, H, sides=5, cap=False)
            for x in (-0.05, 0.05):
                k.tube([(x, 0.17, 1.56), (x, 0.18, 1.48)], 0.005, 0.005, hexc('#f1f1f1'), sides=3)
        elif s == 'mariner':  # tricorn with gold edging and a cockade
            k.cyl((0, 0, z - 0.01), (0, 0, z + 0.14), 0.36, 0.13, H, sides=3)
            k.cyl((0, 0, z + 0.02), (0, 0, z + 0.06), 0.33, 0.3, T, sides=3)
            k.ico((0.16, 0.12, z + 0.08), (0.04, 0.02, 0.04), hexc('#f1f1f1'), subdiv=1, rough=0.1)
            k.fin([(-0.1, -0.1, z + 0.12), (-0.25, -0.25, z + 0.3), (-0.05, -0.15, z + 0.1)], 0.006, hexc('#f1f1f1'))
        elif s in ('legend', 'golden', 'emberforged'):  # crowns
            pts = 10 if s == 'golden' else 7
            base = [(z - 0.01, 0.17, 0.17, 0.0), (z + 0.06, 0.18, 0.18, 0.0)]
            k.vloft(base, H, sides=pts * 2, bottom=False, top=s != 'legend')
            for j in range(pts):
                a = j / pts * math.tau
                p = Vector((math.cos(a) * 0.178, math.sin(a) * 0.178, z + 0.05))
                tall = 0.18 if s == 'emberforged' and j % 2 == 0 else 0.11
                if s == 'emberforged':
                    k.cyl(p, p + Vector((math.cos(a) * 0.03, math.sin(a) * 0.03, tall)), 0.035, 0.0, H, sides=4)
                    k.cyl(p + Vector((0, 0, tall * 0.4)), p + Vector((math.cos(a) * 0.04, math.sin(a) * 0.04, tall * 1.15)), 0.02, 0.0, T, sides=4)
                elif s == 'legend':
                    if j == 0:
                        continue
                else:
                    k.cyl(p, p + Vector((0, 0, tall)), 0.03, 0.0, H, sides=4)
                    k.ico(p + Vector((0, 0, tall)), 0.014, T, subdiv=1, rough=0)
            gem = hexc('#e63946') if s == 'golden' else (L['glow'] or T)
            k.ico((0, 0.185, z + 0.03), (0.035, 0.015, 0.035), gem, subdiv=1, rough=0)
            if s == 'legend':  # a circlet rising to a point at the front, a gem on it
                k.cyl((0, 0.18, z + 0.04), (0, 0.19, z + 0.13), 0.035, 0.0, H, sides=4)
                k.ico((0, 0.19, z + 0.075), 0.02, L['glow'], subdiv=1, rough=0)
        elif s == 'abyssal':  # the brass diving helmet: a sphere with portholes and bolts, on a collar
            brass, glass = H, hexc('#7fc8d8')
            k.vloft([(1.47, 0.2, 0.17, 0.0), (1.52, 0.22, 0.2, 0.0)], shade(brass, 0.85), sides=16, bottom=False)
            k.ico((0, 0.01, 1.7), (0.2, 0.2, 0.21), brass, subdiv=3, rough=0.0)
            k.cyl((0, 0.17, 1.69), (0, 0.215, 1.69), 0.1, 0.1, shade(brass, 0.8), sides=14)
            k.cyl((0, 0.212, 1.69), (0, 0.222, 1.69), 0.085, 0.085, glass, sides=14)
            for x in (-1, 1):
                k.cyl((x * 0.17, 0.0, 1.7), (x * 0.215, 0.0, 1.7), 0.06, 0.06, shade(brass, 0.8), sides=12)
                k.cyl((x * 0.212, 0.0, 1.7), (x * 0.22, 0.0, 1.7), 0.048, 0.048, glass, sides=12)
            for j in range(10):
                a = j / 10 * math.tau
                k.ico((math.cos(a) * 0.215, math.sin(a) * 0.2, 1.5), 0.013, hexc('#e9dcc0'), subdiv=1, rough=0)
            k.cyl((0, -0.12, 1.86), (0, -0.16, 1.92), 0.03, 0.025, shade(brass, 0.8), sides=8)
        elif s == 'tidecaller':  # a hood with a fin-like crest and a shell clasp
            hood = k.vloft([(1.55, 0.17, 0.17, -0.02), (1.65, 0.19, 0.19, -0.03), (1.76, 0.185, 0.19, -0.03), (1.85, 0.14, 0.15, -0.03), (1.88, 0.05, 0.06, -0.03)],
                    lambda c, n: L['jacket'] if n.y < 0.6 or c.z > 1.82 else T, sides=18)
            k.cut(hood, lambda f: f.normal.y > 0.5 and 1.56 < f.calc_center_median().z < 1.8)  # open at the face
            k.fin([(0, 0.08, 1.86), (0, -0.05, 2.02), (0, -0.25, 1.92), (0, -0.2, 1.8)], 0.012, H)
            for j in range(4):
                k.box((0, -0.05 - j * 0.05, 1.93 - j * 0.02), (0.014, 0.008, 0.08), shade(H, 1.2))
            k.ico((0, 0.17, 1.52), (0.04, 0.02, 0.035), hexc('#f7d6c7'), subdiv=1, rough=0.15)
        elif s in ('celestial', 'mythweaver'):  # a floating halo (a star on it, or a second ring of runes)
            pts = [(0.19 * math.cos(a), -0.02 + 0.19 * math.sin(a), 1.98) for a in [j / 24 * math.tau for j in range(25)]]
            k.tube(pts, 0.018, 0.018, H, sides=6, cap=False)
            if s == 'celestial':
                k.fin([(0.05 * math.cos(a), 0.17, 1.98 + 0.05 * math.sin(a)) if j % 2 == 0 else (0.022 * math.cos(a), 0.17, 1.98 + 0.022 * math.sin(a))
                       for j, a in enumerate([j / 10 * math.tau + math.pi / 2 for j in range(10)])], 0.012, hexc('#fff3b0'))
            else:
                pts2 = [(0.13 * math.cos(a), -0.02 + 0.13 * math.sin(a), 2.03) for a in [j / 18 * math.tau for j in range(19)]]
                k.tube(pts2, 0.01, 0.01, T, sides=5, cap=False)
                for j in range(6):
                    a = j / 6 * math.tau
                    k.box((0.19 * math.cos(a), -0.02 + 0.19 * math.sin(a), 1.98), (0.03, 0.03, 0.03), hexc('#ffffff'))
        else:
            k.vloft([(z - 0.02, 0.27, 0.27, 0.0), (z + 0.02, 0.18, 0.18, 0.0), (z + 0.16, 0.15, 0.15, 0.0)], H, sides=14)
    return k.build(f'gear_head_{s}', ao=0.0)

def gear_body(s):
    k = Kit(1100 + GEAR_SETS.index(s))
    if s == 'default':  # a plain shirt in your own colour (region A), sleeves rolled
        A = region('A')
        torso(k, lambda c, n: region('A', 0.85) if c.z < 0.9 else A)
        collar(k, region('A', 0.9), r=(0.11, 0.095), height=0.04)
        buttons(k, hexc('#f1f1f1'), [0], [1.1, 1.22, 1.34])
        for side in (-1, 1):
            sleeve(k, side, A, cuff=region('A', 0.85), cuff_r=0.075, upto=0.55)
            with k.limb(4 if side > 0 else 3, arm_t(side)):  # bare forearms
                e, w = Vector(ARM[side][1]), Vector(ARM[side][2])
                k.tube_r([e.lerp(w, 0.5), w], [0.058, 0.052], SKIN, sides=7)
        return k.build('gear_body_default', ao=0.1)
    L = look(s)
    J, D, T, G = L['jacket'], L['dark'], L['trim'], L['glow']
    shirt = hexc('#e9e4d8')
    if s == 'canvas':  # canvas work jacket: chest flap pockets, a zip, a corduroy collar
        torso(k, lambda c, n: D if c.z < 0.9 else J, bulk=0.06)
        collar(k, hexc('#5b3a1e'), height=0.07, flare=0.04)
        k.box((0, 0.17, 1.17), (0.02, 0.012, 0.6), shade(J, 0.6))
        for x in (-0.11, 0.11):
            k.box((x, 0.175, 1.26), (0.11, 0.015, 0.11), shade(J, 0.9))
            k.box((x, 0.18, 1.31), (0.115, 0.02, 0.035), shade(J, 0.75))
        for side in (-1, 1):
            sleeve(k, side, J, cuff=D)
    elif s == 'oilskin':  # a long yellow oilskin coat to the thigh, toggles down the front, a high collar
        torso(k, lambda c, n: shade(J, 0.85) if abs(c.x) < 0.02 and n.y > 0.5 else J, bulk=0.1, skirt=0.55, flare=0.06)
        collar(k, J, height=0.11, flare=0.03)
        for z in (0.75, 0.95, 1.1, 1.25, 1.38):
            k.box((0.0, 0.19, z), (0.06, 0.02, 0.018), hexc('#2b2d42'))
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.095, r1=0.08, cuff=shade(J, 0.85), cuff_r=0.085)
    elif s == 'reedwalker':  # camouflage smock, the hood down on the back, reed tufts on the shoulders
        rnd = random.Random(7)
        off = Vector((rnd.random() * 9, rnd.random() * 9, rnd.random() * 9))
        camo = lambda c, n: (D if noise.noise(c * 9 + off) > 0.2 else hexc('#8a8f4a') if noise.noise(c * 13 + off) > 0.25 else J)
        torso(k, camo, bulk=0.07, skirt=0.75, flare=0.03)
        k.blob([((0, -0.17, 1.47), (0.15, 0.06, 0.08), J, 1, 0.1, 2.0)], 0.03, 0.35, lambda f: camo(f.calc_center_median(), f.normal))  # the hood, down
        for side in (-1, 1):
            sleeve(k, side, J, cuff=D)
            for j in range(5):
                p = Vector((side * (0.22 + j * 0.02), -0.02 + j * 0.02, 1.47))
                k.tube([p, p + Vector((side * 0.03, 0.01 * j, 0.08 + 0.02 * (j % 2)))], 0.006, 0.002, hexc('#9fae5a'), sides=3)
    elif s == 'clover':  # a padded vest over a white shirt, a clover badge and pockets
        torso(k, lambda c, n: shirt if abs(c.x) < 0.035 and n.y > 0.4 and c.z > 1.0 else J, bulk=0.08)
        collar(k, shirt, height=0.05)
        for z in (1.0, 1.12, 1.24, 1.36):
            k.box((0, 0.19, z), (0.42, 0.01, 0.012), shade(J, 0.85))  # quilting
        for x in (-0.13, 0.13):
            k.box((x, 0.19, 1.05), (0.1, 0.015, 0.08), shade(J, 0.8))
        for a in range(4):
            ang = a * math.pi / 2 + math.pi / 4
            k.ico((0.13 + math.cos(ang) * 0.018, 0.195, 1.3 + math.sin(ang) * 0.018), (0.017, 0.006, 0.017), hexc('#b7f5c5'), subdiv=1, rough=0)
        for side in (-1, 1):
            sleeve(k, side, shirt, r0=0.075, cuff=hexc('#d9d4c8'))
            k.ico((side * 0.27, 0, 1.4), (0.07, 0.08, 0.06), J, subdiv=1, rough=0.02)  # the vest's shoulders
    elif s == 'seadog':  # a double-breasted navy pea coat: brass buttons, broad lapels, to the hip
        torso(k, J, bulk=0.1, skirt=0.72, flare=0.02)
        collar(k, J, height=0.1, flare=0.06)
        k.fin([(-0.04, 0.2, 1.42), (-0.17, 0.19, 1.47), (-0.1, 0.19, 1.25)], 0.012, shade(J, 0.85))
        k.fin([(0.04, 0.2, 1.42), (0.17, 0.19, 1.47), (0.1, 0.19, 1.25)], 0.012, shade(J, 0.85))
        buttons(k, hexc('#c9a227'), [-0.07, 0.07], [0.88, 1.02, 1.16], y=0.19, r=0.016)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.09, cuff=shade(J, 0.85))
    elif s == 'trophy':  # a leather hunting vest full of pockets over a red check shirt, a gold fish badge
        check = lambda c, n: hexc('#a4161a') if (int(c.x * 30 + 50) + int(c.z * 30)) % 2 else hexc('#3a0d0d')
        torso(k, lambda c, n: check(c, n) if abs(c.x) < 0.05 and n.y > 0.4 and c.z > 1.0 else J, bulk=0.08)
        collar(k, hexc('#a4161a'), height=0.05)
        for x in (-0.13, 0.13):
            for z in (1.0, 1.18, 1.33):
                k.box((x, 0.19, z), (0.09, 0.02, 0.07), shade(J, 0.8))
        k.fin([(0.09, 0.2, 1.38), (0.15, 0.2, 1.4), (0.14, 0.2, 1.35)], 0.01, hexc('#ffd166'))
        for side in (-1, 1):
            sleeve(k, side, hexc('#a4161a'), r0=0.078, cuff=hexc('#3a0d0d'))
            with k.limb(4 if side > 0 else 3, arm_t(side)):  # check on the sleeves
                a, e = Vector(ARM[side][0]), Vector(ARM[side][1])
                for j in range(3):
                    p = a.lerp(e, 0.25 + j * 0.25)
                    k.tube_r([p + Vector((0, 0, 0.01)), p - Vector((0, 0, 0.01))], [0.087, 0.087], hexc('#3a0d0d'), sides=8, cap=False)
            k.ico((side * 0.27, 0, 1.4), (0.075, 0.08, 0.06), J, subdiv=1, rough=0.02)
    elif s == 'storm':  # a technical storm shell: high collar, cyan reflective bands, a cinched waist
        torso(k, lambda c, n: T if 1.2 < c.z < 1.24 else J, bulk=0.1, skirt=0.78, flare=0.03)
        collar(k, J, height=0.13, flare=0.01)
        k.box((0.04, 0.19, 1.15), (0.015, 0.012, 0.62), hexc('#1b1b1b'))
        k.vloft([(0.93, 0.25, 0.185, 0.0), (0.97, 0.245, 0.18, 0.0)], shade(J, 0.7), sides=16, bottom=False, top=False)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.095, r1=0.075, cuff=T, cuff_r=0.08)
    elif s == 'mariner':  # a long greatcoat open over a waistcoat: gold buttons, epaulettes with fringes, deep cuffs
        torso(k, lambda c, n: hexc('#f1e3c8') if abs(c.x) < 0.07 and n.y > 0.5 and c.z > 0.9 else J, bulk=0.09, skirt=0.42, flare=0.1)
        collar(k, T, height=0.08, flare=0.05)
        buttons(k, T, [-0.1, 0.1], [0.95, 1.07, 1.19, 1.31], y=0.19, r=0.016)
        k.tube([(-0.075, 0.2, 0.45), (-0.075, 0.19, 1.45)], 0.012, 0.012, T, sides=4)
        k.tube([(0.075, 0.2, 0.45), (0.075, 0.19, 1.45)], 0.012, 0.012, T, sides=4)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.09, cuff=T, cuff_r=0.095)
            with k.limb(4 if side > 0 else 3, lambda co: 0.0):  # epaulettes
                k.ico((side * 0.31, 0, 1.47), (0.08, 0.09, 0.025), T, subdiv=1, rough=0)
                for j in range(7):
                    k.cyl((side * (0.31 + 0.07 * math.cos(j / 6 * math.pi - math.pi / 2)), 0.07 * math.sin(j / 6 * math.pi - math.pi / 2), 1.46),
                          (side * (0.31 + 0.075 * math.cos(j / 6 * math.pi - math.pi / 2)), 0.075 * math.sin(j / 6 * math.pi - math.pi / 2), 1.4), 0.008, 0.006, T, sides=3)
    elif s in ('legend', 'mythweaver'):  # a tunic with a fur-trimmed mantle and a cape (runes glowing on the Mythweaver's)
        torso(k, lambda c, n: T if 0.92 < c.z < 0.97 else J, bulk=0.07, skirt=0.62, flare=0.06)
        k.vloft([(1.38, 0.3, 0.2, 0.0), (1.46, 0.27, 0.18, 0.0), (1.53, 0.14, 0.12, 0.0)], hexc('#f1e9f7') if s == 'legend' else T, sides=16, bottom=False)
        cape(k, shade(J, 0.85), D, bottom=0.42, trim=T)
        if s == 'mythweaver':
            for j in range(5):
                k.box((-0.25 + j * 0.12, -0.31, 0.6), (0.05, 0.01, 0.07), G)
            stars(k, 3, [(-0.35, 0.35), (-0.05, 0.1), (1.55, 1.7)], G, r=0.025, seed=3)  # floating rune-motes
        else:
            k.ico((0, 0.2, 1.3), (0.045, 0.02, 0.05), G, subdiv=1, rough=0)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.088, cuff=T, cuff_r=0.085)
    elif s == 'golden':  # a gilded coat: shining shoulder plates, coin buttons, scrollwork bands
        torso(k, lambda c, n: T if int(c.z * 12) % 3 == 0 and c.z < 1.4 else J, bulk=0.09, skirt=0.55, flare=0.06)
        collar(k, T, height=0.09, flare=0.05)
        buttons(k, hexc('#fff3b0'), [0], [0.75, 0.9, 1.05, 1.2, 1.35], y=0.195, r=0.02)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.09, cuff=T, cuff_r=0.09)
            with k.limb(4 if side > 0 else 3, lambda co: 0.0):
                k.ico((side * 0.31, 0, 1.45), (0.12, 0.12, 0.07), hexc('#ffd166'), subdiv=2, rough=0)
                k.ico((side * 0.34, 0.08, 1.45), 0.02, hexc('#e63946'), subdiv=1, rough=0)
    elif s == 'abyssal':  # a heavy diving suit: a brass breastplate with bolts, lead weights, an air hose
        brass = hexc('#c9a227')
        torso(k, J, bulk=0.14)
        k.vloft([(1.25, 0.28, 0.2, 0.0), (1.4, 0.3, 0.18, 0.0), (1.48, 0.21, 0.14, 0.0)], brass, sides=16, bottom=True)
        for j in range(8):
            a = j / 8 * math.pi
            k.ico((0.29 * math.cos(a), 0.16 * math.sin(a), 1.4), 0.014, hexc('#e9dcc0'), subdiv=1, rough=0)
        for y in (0.2, -0.2):
            k.box((0, y, 1.1), (0.3, 0.06, 0.16), hexc('#495057'))
        k.tube(curve([(0.12, -0.18, 1.6), (0.25, -0.3, 1.4), (0.2, -0.28, 1.0), (0.1, -0.22, 0.95)], 4), 0.025, 0.025, hexc('#2b2d42'), sides=6)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.1, r1=0.085, cuff=hexc('#3a3a3a'), cuff_r=0.09)
    elif s == 'tidecaller':  # a flowing robe with a wave-cut hem, fin-like shoulders and shells
        torso(k, lambda c, n: T if c.z < 0.62 + 0.05 * math.sin(math.atan2(c.y, c.x) * 6) else J, bulk=0.07, skirt=0.5, flare=0.09)
        collar(k, T, height=0.08, flare=0.03)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.09, r1=0.09, cuff=T, cuff_r=0.11)
            with k.limb(4 if side > 0 else 3, lambda co: 0.0):
                k.fin([(side * 0.27, 0.05, 1.47), (side * 0.45, -0.05, 1.58), (side * 0.38, -0.12, 1.4), (side * 0.27, -0.06, 1.38)], 0.015, L['hat'])
        for z in (1.3, 1.1, 0.9):
            k.ico((0, 0.2, z), (0.03, 0.015, 0.025), hexc('#f7d6c7'), subdiv=1, rough=0.2)
    elif s == 'emberforged':  # plate mail: a breastplate with glowing cracks, spiked pauldrons, a mail skirt
        torso(k, lambda c, n: G if abs(noise.noise(c * 14)) < 0.06 and c.z > 0.95 else (hexc('#5a5a5a') if c.z < 0.95 else J), bulk=0.12, skirt=0.7, flare=0.04)
        collar(k, hexc('#370617'), height=0.07, flare=0.03)
        for side in (-1, 1):
            sleeve(k, side, hexc('#5a5a5a'), r0=0.09, cuff=J, cuff_r=0.095)
            with k.limb(4 if side > 0 else 3, lambda co: 0.0):
                k.ico((side * 0.32, 0, 1.45), (0.13, 0.13, 0.09), J, subdiv=2, rough=0.03)
                for j in range(3):
                    p = Vector((side * (0.3 + j * 0.04), -0.04 + j * 0.04, 1.52))
                    k.cyl(p, p + Vector((side * 0.05, 0, 0.1)), 0.025, 0.0, T, sides=4)
            with k.limb(4 if side > 0 else 3, arm_t(side)):  # gauntlets
                k.ico(Vector(HAND[side]) + Vector((0, -0.03, 0.01)), (0.065, 0.065, 0.065), J, subdiv=1, rough=0.03)
    elif s == 'celestial':  # a starweave robe: deep indigo scattered with stars, a crescent moon on the chest
        torso(k, lambda c, n: T if c.z < 0.55 else J, bulk=0.07, skirt=0.48, flare=0.08)
        collar(k, T, height=0.06, flare=0.02)
        stars(k, 40, [(-0.28, 0.28), (0.17, 0.21), (0.6, 1.4)], hexc('#fff3b0'), r=0.008, seed=11)
        stars(k, 25, [(-0.28, 0.28), (-0.21, -0.17), (0.6, 1.4)], hexc('#fff3b0'), r=0.008, seed=12)
        pts = [(0.1 + 0.06 * math.cos(a), 0.2, 1.3 + 0.06 * math.sin(a)) for a in [j / 10 * math.pi * 1.4 + 0.5 for j in range(11)]]
        k.tube(pts, 0.012, 0.004, hexc('#fff3b0'), sides=4)
        for side in (-1, 1):
            sleeve(k, side, J, r0=0.09, r1=0.1, cuff=T, cuff_r=0.11)
    else:
        torso(k, J)
        for side in (-1, 1):
            sleeve(k, side, J)
    return k.build(f'gear_body_{s}', ao=0.1)

def gear_legs(s):
    k = Kit(1200 + GEAR_SETS.index(s))
    if s == 'default':  # jeans and a belt
        trousers(k, hexc('#3d5a80'), belt=hexc('#4a3424'))
        return k.build('gear_legs_default', ao=0.12)
    L = look(s)
    J, B, T, G = L['jacket'], L['boots'], L['trim'], L['glow']
    wader = mix(B, J, 0.35)
    if s == 'canvas':
        trousers(k, mix(hexc('#6b705c'), J, 0.3), belt=hexc('#2b2d42'), knees=hexc('#4a4e3c'))
    elif s == 'oilskin':
        trousers(k, J, r=(0.12, 0.11, 0.095), belt=hexc('#2b2d42'))
    elif s == 'reedwalker':  # marsh waders, mud up the shins
        trousers(k, mix(hexc('#4a5a2a'), J, 0.4), belt=hexc('#3b3a26'))
        for limb, x in ((1, -0.12), (2, 0.12)):
            with k.limb(limb, leg_t):
                k.tube_r([(x * 1.04, 0.005, 0.12), (x * 1.04, 0.005, 0.38)], [0.1, 0.095], hexc('#5a4632'), sides=9, cap=False)
    elif s == 'clover':  # green waders with clover patches on the knees
        trousers(k, mix(hexc('#1f5130'), J, 0.4), belt=hexc('#ffd166'))
        for x in (-0.123, 0.123):
            with k.limb(1 if x < 0 else 2, leg_t):
                for a in range(4):
                    ang = a * math.pi / 2 + math.pi / 4
                    k.ico((x + math.cos(ang) * 0.02, 0.108, 0.55 + math.sin(ang) * 0.02), (0.02, 0.006, 0.02), hexc('#b7f5c5'), subdiv=1, rough=0)
    elif s == 'seadog':  # straight navy deck trousers with a white piping stripe
        trousers(k, hexc('#1b1f2e'), belt=hexc('#111111'), stripe=hexc('#e9e4d8'))
    elif s == 'trophy':  # brown waders with leather knee patches
        trousers(k, mix(hexc('#5a4632'), J, 0.3), belt=hexc('#3b2410'), knees=hexc('#7a4a22'))
    elif s == 'storm':  # storm waders with reflective bands and knee pads
        trousers(k, J, belt=hexc('#111111'), knees=hexc('#1b263b'))
        for limb, x in ((1, -0.12), (2, 0.12)):
            with k.limb(limb, leg_t):
                k.tube_r([(x * 1.04, 0.003, 0.3), (x * 1.04, 0.003, 0.34)], [0.097, 0.097], T, sides=9, cap=False)
    elif s == 'mariner':  # cream breeches to the knee and white stockings
        trousers(k, hexc('#efe3c8'), r=(0.115, 0.1, 0.075), belt=T)
        for limb, x in ((1, -0.12), (2, 0.12)):
            with k.limb(limb, leg_t):
                k.tube_r([(x * 1.02, 0.015, 0.47), (x * 1.02, 0.015, 0.53)], [0.104, 0.104], T, sides=9, cap=False)
    elif s == 'abyssal':  # thick suit legs with leather straps and lead weights at the knees
        trousers(k, J, r=(0.125, 0.12, 0.11), belt=hexc('#3a2a1a'))
        for limb, x in ((1, -0.12), (2, 0.12)):
            with k.limb(limb, leg_t):
                for z in (0.72, 0.3):
                    k.tube_r([(x * 1.03, 0.01, z - 0.02), (x * 1.03, 0.01, z + 0.02)], [0.128, 0.128], hexc('#5a3e24'), sides=9, cap=False)
                k.box((x * 1.03, 0.1, 0.52), (0.1, 0.05, 0.1), hexc('#495057'))
    elif s == 'emberforged':  # plate greaves with spiked knee guards, glowing seams
        trousers(k, hexc('#4a4a4a'), belt=J)
        for limb, x in ((1, -0.12), (2, 0.12)):
            with k.limb(limb, leg_t):
                k.tube_r([(x * 1.03, 0.02, 0.85), (x * 1.03, 0.02, 0.58)], [0.115, 0.105], J, sides=9)
                k.tube_r([(x * 1.04, 0.01, 0.45), (x * 1.04, 0.01, 0.15)], [0.1, 0.092], J, sides=9)
                k.ico((x * 1.03, 0.09, 0.51), (0.07, 0.05, 0.07), shade(J, 1.2), subdiv=1, rough=0)
                k.cyl((x * 1.03, 0.13, 0.51), (x * 1.03, 0.2, 0.53), 0.025, 0.0, T, sides=4)
                k.box((x * 1.03, 0.118, 0.7), (0.012, 0.004, 0.2), G)
    elif s in ('legend', 'golden', 'mythweaver', 'tidecaller', 'celestial'):
        base = {'legend': J, 'golden': hexc('#c9a227'), 'mythweaver': J, 'tidecaller': J, 'celestial': hexc('#1d0b4f')}[s]
        trousers(k, base, belt=T, stripe=T if s in ('legend', 'golden', 'mythweaver') else None)
        for limb, x in ((1, -0.12), (2, 0.12)):
            with k.limb(limb, leg_t):
                if s == 'tidecaller':  # scales
                    for j in range(5):
                        k.tube_r([(x * 1.03, 0.01, 0.2 + j * 0.12), (x * 1.03, 0.01, 0.22 + j * 0.12)], [0.1, 0.1], T, sides=9, cap=False)
                if s == 'celestial':
                    stars(k, 8, [(x - 0.05, x + 0.05), (0.08, 0.11), (0.2, 0.85)], hexc('#fff3b0'), r=0.007, seed=int(20 + x * 10))
                if s == 'mythweaver':
                    for z in (0.35, 0.65):
                        k.tube_r([(x * 1.03, 0.01, z), (x * 1.03, 0.01, z + 0.025)], [0.103, 0.103], G, sides=9, cap=False)
    else:
        trousers(k, wader, belt=hexc('#2b2d42'))
    return k.build(f'gear_legs_{s}', ao=0.12)

def gear_feet(s):
    k = Kit(1300 + GEAR_SETS.index(s))
    if s == 'default':  # trainers
        boots(k, hexc('#e9ecef'), height=0.12, top_r=0.085, sole=hexc('#f8f9fa'), style='sneaker', trim=hexc('#e63946'))
        return k.build('gear_feet_default', ao=0.1)
    L = look(s)
    B, T, G = L['boots'], L['trim'], L['glow']
    if s == 'canvas':
        boots(k, B, height=0.24, style='laced', trim=hexc('#e9dcc0'))
    elif s == 'oilskin':
        boots(k, B, height=0.42, top_r=0.115, sole=hexc('#111111'))
    elif s == 'reedwalker':  # mud boots caked in mud
        boots(k, B, height=0.36, top_r=0.11)
        for limb, x in ((1, -0.126), (2, 0.126)):
            with k.limb(limb, leg_t):
                k.blob([((x, 0.06, 0.06), (0.085, 0.17, 0.06), hexc('#5a4632'), 1, 0.3, 2.0)], 0.03, 0.35, lambda f: hexc('#5a4632'))
    elif s == 'clover':  # with a gold horseshoe on each heel
        boots(k, B, height=0.3, cuff=hexc('#2d9d4a'))
        for limb, x in ((1, -0.126), (2, 0.126)):
            with k.limb(limb, leg_t):
                k.tube([(x - 0.04, -0.09, 0.03), (x - 0.045, -0.1, 0.09), (x, -0.105, 0.12), (x + 0.045, -0.1, 0.09), (x + 0.04, -0.09, 0.03)], 0.01, 0.01, hexc('#ffd166'), sides=4)
    elif s == 'seadog':
        boots(k, B, height=0.3, sole=hexc('#3a2a1a'), toe=shade(B, 1.6))
    elif s == 'trophy':
        boots(k, B, height=0.26, style='hobnail', trim=hexc('#c9a227'))
    elif s == 'storm':
        boots(k, B, height=0.34, top_r=0.11, sole=T, cuff=shade(B, 1.3))
    elif s == 'mariner':  # tall boots with folded tops and gold buckles
        boots(k, B, height=0.55, top_r=0.11, cuff=shade(B, 1.3), toe=None)
        for limb, x in ((1, -0.126), (2, 0.126)):
            with k.limb(limb, leg_t):
                k.box((x, 0.11, 0.14), (0.06, 0.02, 0.04), T)
    elif s == 'abyssal':  # lead diving boots with brass toecaps
        boots(k, B, height=0.3, top_r=0.12, style='diving', toe=hexc('#c9a227'), sole=hexc('#2b2b2b'))
    elif s == 'emberforged':  # pointed sabatons with glowing seams
        boots(k, B, height=0.3, style='pointed', trim=G, toe=shade(B, 1.4))
    elif s == 'tidecaller':  # coral boots, coral branching from the shafts
        boots(k, B, height=0.32, trim=T)
        for limb, x in ((1, -0.126), (2, 0.126)):
            with k.limb(limb, leg_t):
                for j, a in enumerate((0.5, 2.0, 3.6)):
                    p = Vector((x + math.cos(a) * 0.1, math.sin(a) * 0.1, 0.2 + j * 0.04))
                    q = p + Vector((math.cos(a) * 0.05, math.sin(a) * 0.05, 0.08))
                    k.tube([p, q], 0.012, 0.005, hexc('#ff8fab'), sides=4)
                    k.tube([p.lerp(q, 0.5), p.lerp(q, 0.5) + Vector((0, 0.03, 0.04))], 0.008, 0.003, hexc('#ff8fab'), sides=4)
    elif s == 'celestial':  # comet boots trailing light from the heels
        boots(k, B, height=0.3, trim=T)
        for limb, x in ((1, -0.126), (2, 0.126)):
            with k.limb(limb, leg_t):
                k.cyl((x, -0.1, 0.12), (x, -0.32, 0.18), 0.05, 0.0, G, sides=6)
                k.ico((x, -0.06, 0.12), 0.03, hexc('#fff3b0'), subdiv=1, rough=0)
    elif s in ('legend', 'golden', 'mythweaver'):
        boots(k, B, height=0.38, top_r=0.11, cuff=T, trim=None, toe=shade(B, 1.3))
        if G:
            for limb, x in ((1, -0.126), (2, 0.126)):
                with k.limb(limb, leg_t):
                    k.ico((x, 0.105, 0.3), (0.025, 0.012, 0.025), G, subdiv=1, rough=0)
    else:
        boots(k, B)
    return k.build(f'gear_feet_{s}', ao=0.1)

def gear(i):
    slot, s = GEAR[i]
    return {'head': gear_head, 'body': gear_body, 'legs': gear_legs, 'feet': gear_feet}[slot](s)

def rod(i):
    """The rod in your hands, along +Y from the butt (y = -0.35) to the tip (2.2): cork grip, reel, guides,
    and the blank in region A (each rod's own colour)."""
    k = Kit(95)
    cork, metal, dark = hexc('#c49a6c'), hexc('#c0c7cf'), hexc('#2b2d42')
    k.cyl((0, -0.36, 0), (0, 0.05, 0), 0.026, 0.024, cork, sides=8)
    k.cyl((0, 0.05, 0), (0, 0.15, 0), 0.02, 0.018, dark, sides=8)
    # The reel, hanging under the seat, with its handle.
    k.cyl((-0.05, 0.1, -0.075), (0.05, 0.1, -0.075), 0.055, 0.055, metal, sides=10)
    k.cyl((-0.035, 0.1, -0.075), (0.035, 0.1, -0.075), 0.06, 0.06, region('A', 0.8), sides=10)
    k.box((0, 0.1, -0.03), (0.02, 0.03, 0.05), metal)
    k.tube([(0.05, 0.1, -0.075), (0.08, 0.1, -0.075), (0.08, 0.13, -0.11)], 0.008, 0.008, metal, sides=4)
    k.ico((0.08, 0.13, -0.12), 0.012, dark, subdiv=1, rough=0)
    # The blank, tapering to the tip, and the guides under it.
    k.cyl((0, 0.15, 0), (0, 2.2, 0), 0.016, 0.004, region('A'), sides=6)
    for j, y in enumerate((0.55, 0.95, 1.3, 1.6, 1.85, 2.05)):
        r = 0.022 - j * 0.0025
        pts = [(r * math.cos(a), y, -0.02 - r + r * math.sin(a)) for a in [s / 8 * math.tau for s in range(9)]]
        k.tube(pts, 0.003, 0.003, metal, sides=3, cap=False)
    return k.build('rod_0', ao=0)

# ---- the voyage bosses -------------------------------------------------------------------------------------
# Each faces +Y (towards the boat) with its waterline at z = 0. hump_* is the part that breaches inside the
# golden ring; grab_*_p / _n is what seizes the rail on the +Y / -Y side of the trawler (in the game's
# space that's +Z / -Z): it rises out of the water beside the hull and hooks over the rail onto the deck.

BOSSES = ['kraken', 'megalodon', 'serpent', 'ghostwhale']
BOSS_COLOURS = {'kraken': '#9d4edd', 'megalodon': '#8d99ae', 'serpent': '#2a9d8f', 'ghostwhale': '#bde0fe'}

def bezier(a, b, c, n=12):
    a, b, c = Vector(a), Vector(b), Vector(c)
    return [(1 - t) ** 2 * a + 2 * (1 - t) * t * b + t * t * c for t in [j / n for j in range(n + 1)]]

def curve(points, n=6):
    """A smooth curve through points (Catmull-Rom), n samples per span."""
    P = [Vector(p) for p in points]
    out = []
    for i in range(len(P) - 1):
        p0, p1, p2, p3 = P[max(i - 1, 0)], P[i], P[i + 1], P[min(i + 2, len(P) - 1)]
        for s in range(n):
            t = s / n
            out.append(0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t * t * t))
    out.append(P[-1])
    return out

def tentacle(k, pts, r0, r1, colour, sucker, sides=8):
    """A tapering tentacle with a row of suckers along its underside."""
    k.tube(pts, r0, r1, colour, sides=sides, jitter=0.08)
    n = len(pts)
    for j in range(1, n - 1, 2):
        p, q = Vector(pts[j]), Vector(pts[j + 1])
        d = (q - p).normalized()
        side = d.cross(Vector((0, 0, 1)))
        if side.length < 0.1:
            side = Vector((1, 0, 0))
        down = d.cross(side.normalized()).normalized()
        if down.z > 0:
            down = -down
        r = r0 + (r1 - r0) * j / (n - 1)
        k.ico(p + down * r * 0.85, r * 0.32, sucker, subdiv=1, rough=0)

def boss_body(name):
    """Limbs (World/LimbRig.cs animates them): the Kraken's eight tentacles (1-8); Megalodon's body, from the
    snout back to the tail (1), and its lower jaw (2); the Serpent's four coils (1-4) and its neck and head (5);
    the Ghost Whale's body back to the flukes (1) and its two flippers (2 left, 3 right)."""
    k = Kit(500 + BOSSES.index(name))
    rnd = k.rnd
    c = hexc(BOSS_COLOURS[name])
    dark, light = shade(c, 0.6), mix(c, hexc('#ffffff'), 0.3)
    off = Vector((rnd.random() * 9, rnd.random() * 9, rnd.random() * 9))
    if name == 'kraken':
        # A domed mantle leaning back, a broad head, lantern eyes, eight tentacles rearing all round.
        # The mantle: overlapping lumps along a backbone that curves back, melted smooth.
        spine = curve([(0, 1.0, 3.5), (0, -0.5, 8.5), (0, -2.8, 13), (0, -4.2, 16.5)], 3)
        parts = [((0, 0, 1.2), (7.2, 6.8, 2.8), c, 2, 0.08, 1.0)]
        for j, p in enumerate(spine):
            t = j / (len(spine) - 1)
            r = 6.4 - 2.6 * t
            parts.append((p, (r, r * 0.92, r * 0.85), c, 2, 0.05, 1.0))

        def skin(f):
            p, n = f.calc_center_median(), f.normal
            if n.z < -0.3 or p.z < 1.0:
                return dark
            spot = noise.noise(p * 0.35 + off)
            if spot > 0.35:
                return shade(c, 0.75)
            return light if n.y > 0.4 and p.z < 7 else c

        k.blob(parts, voxel=0.7, ratio=0.22, colorize=skin)
        for x in (-3.0, 3.0):
            k.ico((x, 6.0, 5.2), (1.45, 1.0, 1.35), hexc("#ffd166"), subdiv=2, rough=0)
            k.box((x, 6.95, 5.2), (0.45, 0.2, 1.6), hexc("#111111"))
            k.fin([(x - 1.6, 6.2, 6.7), (x + 1.6, 6.2, 6.9), (x, 5.6, 7.3)], 0.4, dark)
        for j in range(8):
            a = j / 8 * math.tau + 0.2
            d = Vector((math.cos(a), math.sin(a), 0))
            s = d.cross(Vector((0, 0, 1)))
            reach, height = rnd.uniform(12, 17), rnd.uniform(6, 13)
            curl = rnd.choice([-1, 1])
            pts = curve([d * 4.5 + Vector((0, 0, 0.6)), d * reach * 0.55 + Vector((0, 0, height * 0.55)),
                         d * reach * 0.85 + Vector((0, 0, height)), d * reach + Vector((0, 0, height * 0.6)) + s * curl * 2,
                         d * (reach - 1.5) + Vector((0, 0, height * 0.35)) + s * curl * 3.2], 4)
            with k.limb(j + 1, along(pts)):
                tentacle(k, pts, 1.35, 0.12, c if j % 2 else light, hexc('#e0aaff'))
    elif name == 'megalodon':
        # A shark the size of a whale rolling at the surface: dark back, white belly, open jaws.
        sections = [(-15.5, 0.6, 0.9, 0.5), (-12, 1.5, 1.9, 0.6), (-7, 2.9, 3.0, 0.7), (-1, 4.3, 4.0, 0.8), (5, 4.6, 4.2, 0.8),
                    (10, 4.0, 3.6, 0.6), (13.5, 3.0, 2.7, 0.4), (15.8, 1.8, 1.8, 0.5), (16.9, 0.4, 0.5, 0.7)]

        def skin(p, n):
            line = 0.18 * noise.noise(p * 0.4 + off)
            if n.z < -0.2 + line:
                return hexc('#e9ecef')
            return dark if n.z > 0.8 else c

        with k.limb(1, axis_t(16.9, -21.5)):
            k.loft(sections, skin, sides=12)
            k.fin([(0, 4.5, 4.4), (0, -1.5, 11.5), (0, -3.5, 4.0)], 0.5, dark)  # dorsal
            k.fin([(0, -14.5, 0.6), (0, -21.5, 8.5), (0, -19.5, 0.8), (0, -21, -6.5), (0, -15.5, -0.2)], 0.4, dark)  # tail
            for sx in (-1, 1):
                k.fin([(sx * 4.6, 7, -1.2), (sx * 11, 1.5, -3.2), (sx * 4.6, 3.5, -1.6)], 0.35, dark)
                for g in range(5):
                    k.box((sx * 4.3, 8.4 - g * 0.55, 1.0), (0.08, 0.12, 1.6), shade(c, 0.45))
                k.ico((sx * 2.9, 12.0, 2.3), 0.38, hexc('#0b0b0b'), subdiv=1, rough=0)
            # The open mouth under the snout, with the upper row of teeth.
            k.ico((0, 13.6, -1.0), (2.6, 2.2, 1.0), hexc('#6a040f'), subdiv=1, rough=0.05)
            for j in range(11):
                a = (j / 10 - 0.5) * 2.4
                x, y = math.sin(a) * 2.4, 13.4 + math.cos(a) * 1.9
                k.cyl((x, y, -0.2), (x, y, -1.1), 0.22, 0.0, hexc('#f8f9fa'), sides=4)
            k.fin([(0, -4, -2.6), (0, -7.5, -5.0), (0, -7, -2.4)], 0.25, dark)  # anal fin
        # The lower jaw, hinged at the back of the mouth (it gapes and snaps), with its own row of teeth.
        with k.limb(2, lambda co: 0.0):
            k.ico((0, 13.0, -2.1), (2.7, 2.6, 0.75), hexc('#e9ecef'), subdiv=2, rough=0.03)
            k.ico((0, 13.2, -1.55), (2.2, 2.0, 0.3), hexc('#6a040f'), subdiv=1, rough=0.03)
            for j in range(11):
                a = (j / 10 - 0.5) * 2.4
                x, y = math.sin(a) * 2.4, 13.1 + math.cos(a) * 1.9
                k.cyl((x * 0.9, y - 0.3, -2.0), (x * 0.9, y - 0.3, -1.2), 0.2, 0.0, hexc('#f8f9fa'), sides=4)
    elif name == 'serpent':
        # Coils of green scale arching out of the water, spines along their backs, the head reared up.
        gold = hexc('#e9c46a')
        for j in range(4):
            y0 = -26 + j * 9.5
            h = 6 + j * 1.2
            pts = bezier((0, y0, -2.5), (0, y0 + 4.5, h * 1.9), (0, y0 + 9, -2.5), 14)
            r = 2.2 - j * 0.12
            with k.limb(j + 1, along(pts)):
                _, faces = k.tube(pts, r, r * 0.92, c, sides=9, jitter=0.05)
                mid = Vector((0, y0 + 4.5, -2.5))
                for fi, f in enumerate(faces[:-2]):
                    band = (fi // 9) % 4 == 0
                    inward = f.normal.dot((mid - f.calc_center_median()).normalized()) > 0.55
                    k.paint([f], mix(gold, c, 0.35) if inward else (shade(c, 0.8) if band else c), 0.06)
                for s in range(2, len(pts) - 2, 2):
                    p = Vector(pts[s])
                    if p.z > 1:
                        k.cyl(p + Vector((0, 0, r * 0.8)), p + Vector((0, 0, r * 0.8 + 1.4)), 0.45, 0.0, gold, sides=4)
        neck = curve([(0, 10, -2.5), (0, 12.5, 6), (0, 13.5, 12), (0, 16, 14.5)], 5)
        with k.limb(5, along(neck + [Vector((0, 20.7, 14.6))])):
            k.tube(neck, 2.1, 1.7, c, sides=9)
            head = [((0, 18.2, 15), (1.9, 3.2, 1.5), c, 2, 0.05, 1.5), ((0, 16.5, 15.4), (2.0, 1.8, 1.7), c, 1, 0.05, 1.5)]
            k.blob(head, voxel=0.35, ratio=0.3, colorize=lambda f: gold if f.normal.z < -0.3 else c)
            for sx in (-1, 1):
                k.ico((sx * 1.45, 18.2, 15.8), (0.32, 0.45, 0.3), hexc('#ffd166'), subdiv=1, rough=0)
                k.cyl((sx * 1.0, 16.2, 16.3), (sx * 1.9, 13.6, 18.4), 0.35, 0.05, hexc('#f1e3c8'), sides=5)
                k.fin([(sx * 1.6, 16.6, 15.6), (sx * 4.6, 13.4, 17.6), (sx * 3.6, 12.6, 14.2), (sx * 1.6, 15.2, 14.6)], 0.15, gold)
            k.box((0, 20.7, 14.6), (1.6, 0.4, 0.15), hexc('#3a0a0a'))
    else:  # the Ghost Whale
        pale, white, glow = hexc('#bde0fe'), hexc('#e7f5ff'), hexc('#90e0ef')
        sections = [(-22.5, 0.7, 0.5, 0.3), (-18, 2.2, 1.6, 0.3), (-12, 4.4, 3.2, 0.4), (-4, 6.8, 4.8, 0.5), (4, 7.2, 5.2, 0.5),
                    (11, 6.4, 4.6, 0.4), (16, 4.8, 3.6, 0.2), (19, 2.8, 2.3, 0.0), (20.6, 0.8, 0.8, 0.0)]

        def skin(p, n):
            if n.z < -0.3:
                return white if int(p.x * 1.2 + 50) % 2 == 0 else shade(white, 0.86)  # throat grooves
            if n.z > 0.35 and noise.noise(p * 0.45 + off) > 0.3:
                return glow
            return pale

        with k.limb(1, axis_t(4, -27.5)):
            k.loft(sections, skin, sides=12)
            for sx in (-1, 1):
                # Broad flukes.
                k.fin([(0, -21.5, 0.3), (sx * 3, -23, 0.4), (sx * 8.5, -27.5, 0.9), (sx * 9.5, -25.5, 0.8), (sx * 6, -22.8, 0.5), (sx * 1.2, -20.8, 0.3)], 0.45, pale)
                k.ico((sx * 5.1, 15.5, 1.6), 0.45, hexc('#1d3557'), subdiv=1, rough=0)
            k.box((0, 9, 4.9), (0.9, 0.4, 0.15), shade(pale, 0.7))  # the blowhole
        for limb, sx in ((2, -1), (3, 1)):
            # Long flippers, crusted with barnacles.
            with k.limb(limb, along([(sx * 6.3, 6, -1.6), (sx * 13.5, -1.5, -4.0)])):
                k.fin([(sx * 6.4, 8, -1.4), (sx * 9.5, 5.5, -2.6), (sx * 13.5, -1.5, -4.0), (sx * 12.8, -2.8, -3.9), (sx * 8.5, 1.5, -2.6), (sx * 6.2, 4, -1.8)], 0.5, pale)
                for b in range(3):
                    k.ico((sx * (9 + b * 1.4), 3.0 - b * 1.6, -2.6 - b * 0.4), 0.32, hexc('#ced4da'), subdiv=1, rough=0.3)
    return k.build(f'boss_{name}', ao=0.18)

def boss_hump(name):
    """What breaches inside the golden ring: a scaly hump with spines, ringed with foam."""
    k = Kit(600 + BOSSES.index(name))
    c = hexc(BOSS_COLOURS[name])
    dark = shade(c, 0.6)
    k.blob([((0, 0, 0), (5, 7, 4.5), c, 2, 0.12, 1.2)], 0.45, 0.25,
           lambda f: dark if f.normal.z < 0 else (mix(c, hexc('#ffffff'), 0.25) if f.normal.z > 0.7 else c))
    if name == 'megalodon':
        k.fin([(0, 2.5, 3.8), (0, -2, 10), (0, -3.5, 3.6)], 0.45, dark)
    elif name == 'ghostwhale':
        for j in range(6):
            k.ico((k.rnd.uniform(-2.5, 2.5), k.rnd.uniform(-4, 4), 4.0), 0.4, hexc('#90e0ef'), subdiv=1, rough=0)
    else:
        for j in range(5):
            k.cyl((0, -4 + j * 2, 3.6), (0, -4 + j * 2, 3.6 + 2.2 - abs(j - 2) * 0.4), 0.6, 0.0, dark if name == 'kraken' else hexc('#e9c46a'), sides=4)
    for j in range(18):
        a = j / 18 * math.tau
        k.ico((math.cos(a) * 6.5, math.sin(a) * 7.8, 0), (1.4, 1.4, 0.5), hexc('#f1faee'), subdiv=1, rough=0.25)
    return k.build(f'hump_{name}', ao=0.15)

def boss_grab(name, side):
    """What seizes the rail, for the rail on side (+1 / -1 along Y). It's all one limb (1), from the water to its tip."""
    k = Kit(700 + BOSSES.index(name) * 2 + (side > 0))
    c = hexc(BOSS_COLOURS[name])
    D = 1.6
    path = bezier((0, side * 4.5, -1.5), (0, side * 1.5, D + 5.5), (0, -side * 2.2, D + 0.3), 12)
    with k.limb(1, along(path)):
        if name == 'kraken':
            tentacle(k, path, 0.9, 0.12, c, hexc('#e0aaff'))
        elif name == 'serpent':
            _, faces = k.tube(path, 1.2, 0.9, c, sides=9)
            for fi, f in enumerate(faces[:-2]):
                if (fi // 9) % 3 == 0:
                    k.paint([f], shade(c, 0.6))
            for s in range(2, len(path) - 2, 2):
                p = Vector(path[s])
                k.cyl(p + Vector((0, 0, 0.9)), p + Vector((0, 0, 1.8)), 0.3, 0.0, hexc('#e9c46a'), sides=4)
        elif name == 'megalodon':
            # The shark itself: its head and shoulders out of the water, jaws clamped over the rail, built
            # nose-forward along +Y and then turned to point from the water up onto the rail.
            start, end = Vector((0, side * 7.5, -3.5)), Vector((0, -side * 0.7, D + 0.8))
            before = set(k.bm.verts)
            dark, belly = shade(c, 0.65), hexc('#e9ecef')
            k.loft([(-7.5, 0.9, 1.0, 0.0), (-5.0, 1.45, 1.5, 0.05), (-2.6, 1.55, 1.5, 0.1), (-1.0, 1.25, 1.15, 0.2), (-0.25, 0.65, 0.6, 0.28), (0.0, 0.18, 0.2, 0.32)],
                   lambda p, n: belly if n.z < -0.25 else (dark if n.z > 0.7 else c), sides=12)
            k.ico((0, -0.95, -0.85), (0.95, 1.05, 0.35), belly, subdiv=2, rough=0.02)  # the lower jaw, dropped open
            k.ico((0, -0.85, -0.45), (0.8, 0.85, 0.32), hexc('#6a040f'), subdiv=1, rough=0)
            for j in range(9):
                a = (j / 8 - 0.5) * 2.6
                x, y = math.sin(a) * 0.8, -0.95 + math.cos(a) * 0.75
                k.cyl((x, y, -0.25), (x, y, -0.62), 0.09, 0.0, hexc('#f8f9fa'), sides=4)
                k.cyl((x * 0.92, y - 0.12, -0.95), (x * 0.92, y - 0.12, -0.6), 0.08, 0.0, hexc('#f8f9fa'), sides=4)
            for sx in (-1, 1):
                k.ico((sx * 0.86, -1.15, 0.38), 0.13, hexc('#0b0b0b'), subdiv=1, rough=0)
                for g in range(4):
                    k.box((sx * 1.38, -2.7 - g * 0.32, 0.0), (0.06, 0.08, 0.75), shade(c, 0.45))
                k.fin([(sx * 1.3, -3.2, -0.6), (sx * 3.4, -5.0, -1.7), (sx * 1.3, -4.4, -0.85)], 0.15, dark)
            k.fin([(0, -4.4, 1.3), (0, -6.2, 3.6), (0, -6.8, 1.2)], 0.2, dark)
            d = (end - start).normalized()
            m = d.to_track_quat('Y', 'Z').to_matrix()
            for v in set(k.bm.verts) - before:
                v.co = m @ v.co + end
            k.bm.normal_update()
        else:  # a spectral flipper crusted with barnacles
            k.tube(path, 0.9, 0.35, c, sides=8)
            for s in range(1, len(path) - 1):
                p = Vector(path[s])
                k.ico(p + Vector((k.rnd.uniform(-0.5, 0.5), 0, 0.6)), k.rnd.uniform(0.15, 0.3), hexc('#ced4da'), subdiv=1, rough=0.3)
    return k.build(f'grab_{name}_{"p" if side > 0 else "n"}', ao=0.1)


def boss_slam(name):
    """What a boss swings at the deck (it's one limb, 1, from its base in the water to its tip), standing up from
    its base at the origin: the Kraken's slamming tentacle, the Serpent's crushing coil (arching over towards +Y),
    the Ghost Whale's tail and flukes. Megalodon hits with its whole body, so this is just its dorsal fin."""
    k = Kit(900 + BOSSES.index(name))
    c = hexc(BOSS_COLOURS[name])
    dark, light = shade(c, 0.6), mix(c, hexc('#ffffff'), 0.3)
    if name == 'kraken':
        pts = curve([(0, 0, -3), (0, 0.4, 4), (0, -0.5, 9), (0, 0.6, 14), (0, 1.8, 17.5)], 5)
        with k.limb(1, along(pts)):
            tentacle(k, pts, 1.6, 0.14, c, hexc('#e0aaff'), sides=10)
    elif name == 'serpent':
        gold = hexc('#e9c46a')
        pts = bezier((0, 0, -3.0), (0, 9.5, 15.0), (0, 19, -3.0), 18)
        with k.limb(1, along(pts)):
            _, faces = k.tube(pts, 2.1, 1.9, c, sides=10, jitter=0.05)
            mid = Vector((0, 9.5, -3))
            for fi, f in enumerate(faces[:-2]):
                inward = f.normal.dot((mid - f.calc_center_median()).normalized()) > 0.55
                k.paint([f], mix(gold, c, 0.35) if inward else (shade(c, 0.8) if (fi // 10) % 4 == 0 else c), 0.06)
            for s in range(2, len(pts) - 2, 2):
                p = Vector(pts[s])
                k.cyl(p + Vector((0, 0, 1.6)), p + Vector((0, 0, 3.0)), 0.45, 0.0, gold, sides=4)
    elif name == 'ghostwhale':
        pale, glow = hexc('#bde0fe'), hexc('#90e0ef')
        with k.limb(1, axis_t(-3, 13, axis=2)):
            k.tube_r([(0, 0, -3), (0, 0, 2), (0, 0, 6), (0, 0, 9.5)], [2.8, 2.2, 1.5, 0.9], pale, sides=12)
            for sx in (-1, 1):
                k.fin([(0, 0, 9.0), (sx * 3.2, 0.2, 9.6), (sx * 8.5, 0.6, 12.8), (sx * 9.2, 0.5, 11.2), (sx * 6.0, 0.3, 9.4), (sx * 1.2, 0, 8.4)], 0.5, pale)
                for b in range(3):
                    k.ico((sx * (2.5 + b * 2.0), 0.4, 9.6 + b * 0.9), 0.3, hexc('#ced4da'), subdiv=1, rough=0.3)
            for z in (0.0, 3.0, 6.0):
                k.ico((0, 1.6 - z * 0.1, z), (0.5, 0.2, 0.5), glow, subdiv=1, rough=0)
    else:
        with k.limb(1, lambda co: 0.0):
            k.fin([(0, 2.5, -0.5), (0, -2.0, 6.0), (0, -3.5, -0.5)], 0.45, dark)
    return k.build(f'slam_{name}', ao=0.12)
def boss_part(i):
    """All the boss parts in one file: boss_*, hump_*, grab_*_p, grab_*_n and slam_*."""
    n = len(BOSSES)
    name = BOSSES[i % n]
    kind = i // n
    if kind == 0:
        return boss_body(name)
    if kind == 1:
        return boss_hump(name)
    if kind == 4:
        return boss_slam(name)
    return boss_grab(name, 1 if kind == 2 else -1)

# ---- fish ----------------------------------------------------------------------------------------------------
# One model per species, head towards +Y, about a metre from nose to tail (the game scales each catch by its
# weight). Each is built on the real fish (or, for the legends, the real fish they're based on) from field-guide
# references: its body plan (FISH_ARCH: profile, fins, tail, mouth, eye) and its own colours and markings
# (FISH: bars, stripes, spots with halos, vermiculations, ear flaps, barbels...). The whole fish is one limb
# (1) from the nose (0) to the tail (1), so it can flap when it's held up.

def interp(table, t):
    """Piecewise-linear lookup in [(t, value), ...]."""
    if t <= table[0][0]:
        return table[0][1]
    for (t0, v0), (t1, v1) in zip(table, table[1:]):
        if t <= t1:
            return v0 + (v1 - v0) * (t - t0) / max(1e-6, t1 - t0)
    return table[-1][1]

def resample(pts, n):
    """n points spread evenly along a polyline."""
    P = [Vector(p) for p in pts]
    L = [0.0]
    for a, b in zip(P, P[1:]):
        L.append(L[-1] + (b - a).length)
    out = []
    for i in range(n):
        s = L[-1] * i / (n - 1)
        j = 0
        while j < len(P) - 2 and L[j + 1] < s:
            j += 1
        u = (s - L[j]) / max(1e-9, L[j + 1] - L[j])
        out.append(P[j].lerp(P[j + 1], max(0.0, min(1.0, u))))
    return out

# Body outlines along the fish, from the tail end (t = 0) to the nose (1): half-height and half-width as
# fractions of the fish's H and W.
FISH_PROFILES = {
    'fusiform': dict(h=[(0, .24), (.1, .32), (.25, .6), (.45, .9), (.6, 1), (.75, .92), (.87, .72), (.95, .45), (1, .12)],
                     w=[(0, .22), (.1, .3), (.25, .6), (.45, .9), (.6, 1), (.75, .95), (.87, .78), (.95, .5), (1, .14)]),
    'deep': dict(h=[(0, .2), (.1, .32), (.25, .68), (.45, .96), (.58, 1), (.72, .9), (.85, .64), (.95, .36), (1, .1)],
                 w=[(0, .22), (.1, .3), (.25, .6), (.45, .9), (.6, 1), (.75, .95), (.87, .78), (.95, .5), (1, .14)]),
    'carp': dict(h=[(0, .25), (.1, .33), (.25, .64), (.45, .94), (.6, 1), (.75, .9), (.87, .7), (.95, .46), (1, .18)],
                 w=[(0, .22), (.1, .3), (.25, .6), (.45, .9), (.6, 1), (.75, .95), (.87, .8), (.95, .55), (1, .2)]),
    'long': dict(h=[(0, .42), (.1, .52), (.25, .74), (.45, .9), (.6, .96), (.75, 1), (.87, .92), (.95, .6), (1, .18)],
                 w=[(0, .4), (.1, .5), (.25, .72), (.45, .9), (.6, .97), (.75, 1), (.87, .92), (.95, .6), (1, .2)]),
    'eel': dict(h=[(0, .2), (.08, .5), (.2, .8), (.4, .95), (.6, 1), (.8, 1), (.92, .8), (1, .3)],
                w=[(0, .15), (.08, .4), (.2, .75), (.4, .95), (.6, 1), (.8, 1), (.92, .85), (1, .35)]),
    'flathead': dict(h=[(0, .3), (.15, .45), (.35, .78), (.55, .95), (.7, 1), (.82, .88), (.92, .66), (.98, .4), (1, .2)],
                     w=[(0, .35), (.15, .5), (.35, .8), (.55, .95), (.7, 1.05), (.82, 1.15), (.92, 1.05), (.98, .7), (1, .4)]),
    'torpedo': dict(h=[(0, .1), (.1, .22), (.25, .55), (.45, .9), (.6, 1), (.75, .95), (.87, .75), (.95, .45), (1, .1)]),
    'disc': dict(h=[(0, .15), (.1, .3), (.25, .7), (.45, .97), (.58, 1), (.72, .92), (.85, .7), (.95, .4), (1, .12)],
                 w=[(0, .2), (.1, .3), (.25, .55), (.45, .85), (.6, 1), (.75, .95), (.87, .75), (.95, .45), (1, .12)]),
    'ribbon': dict(h=[(0, .25), (.06, .6), (.2, .9), (.4, 1), (.8, 1), (.92, .9), (.98, .6), (1, .3)]),
    'angler': dict(h=[(0, .3), (.15, .45), (.35, .75), (.55, .95), (.7, 1), (.82, 1), (.92, .9), (.98, .7), (1, .5)]),
    'blunt': dict(h=[(0, .2), (.1, .3), (.25, .55), (.45, .8), (.6, .92), (.75, 1), (.87, 1), (.95, .92), (1, .6)],
                  w=[(0, .22), (.1, .3), (.25, .6), (.45, .85), (.6, 1), (.75, 1), (.87, .9), (.95, .7), (1, .4)]),
}

# Tail outlines, top to bottom: (how far back, in tail lengths; how high, in tail half-heights).
FISH_TAILS = {
    'forked': [(1.0, 1.0), (0.7, 0.55), (0.42, 0.0), (0.7, -0.55), (1.0, -1.0)],
    'emarginate': [(1.0, 1.0), (0.88, 0.45), (0.8, 0.0), (0.88, -0.45), (1.0, -1.0)],
    'truncate': [(0.95, 1.0), (0.98, 0.5), (1.0, 0.0), (0.98, -0.5), (0.95, -1.0)],
    'round': [(0.55, 0.95), (0.88, 0.62), (1.0, 0.0), (0.88, -0.62), (0.55, -0.95)],
    'lunate': [(1.0, 1.25), (0.62, 0.62), (0.3, 0.12), (0.3, -0.12), (0.62, -0.62), (1.0, -1.25)],
    'hetero': [(1.25, 1.1), (0.95, 0.6), (0.45, 0.08), (0.5, -0.35), (0.55, -0.62)],
    'eel': [(0.6, 0.6), (1.0, 0.0), (0.6, -0.6)],
    'lobed': [(0.55, 1.0), (0.62, 0.38), (1.15, 0.14), (1.25, 0.0), (1.15, -0.14), (0.62, -0.38), (0.55, -1.0)],
}

# Fin outlines along their base (u = 0 at the back, 1 at the front; i is the ray, for notched spiny fins).
FIN_SHAPES = {
    'tri': lambda u, i: 0.18 + 0.82 * u ** 1.2,
    'round': lambda u, i: 0.3 + 0.7 * math.sin(math.pi * (0.12 + 0.76 * u)),
    'long': lambda u, i: (0.6 + 0.4 * math.sin(math.pi * u)) * min(1.0, 0.3 + 5 * min(u, 1 - u)),
    'spiny': lambda u, i: (0.35 + 0.65 * math.sin(math.pi * min(1.0, 0.1 + u)) ** 0.7) * (0.72 if i % 2 else 1.0),
    'sail': lambda u, i: 0.35 + 0.65 * u ** 0.6,
    'falcate': lambda u, i: 0.12 + 0.88 * u ** 3,
}

# The body plans. H and W: the body's half-height and half-width (metres, for a metre-long fish); yn the nose and
# yp the tail end of the body (the tail fin goes on behind). Fins: (t from, t to, height in H, shape, sweep back).
FISH_ARCH = {
    'minnow': dict(profile='fusiform', H=0.1, W=0.055, yn=0.4, yp=-0.34, dorsal=[(0.42, 0.56, 1.1, 'tri', 0.4)],
                   anal=[(0.18, 0.28, 0.8, 'tri', 0.4)], tail=('forked', 0.17, 1.15), pect=(0.8, 0.11), pelv=(0.47, 0.08),
                   mouth=(0.3, 'term'), eye=(0.9, 0.3, 0.022), gill=0.79),
    'carp': dict(profile='carp', H=0.14, W=0.07, yn=0.42, yp=-0.34, dorsal=[(0.3, 0.64, 0.8, 'long', 0.3)],
                 anal=[(0.17, 0.26, 0.8, 'tri', 0.35)], tail=('forked', 0.2, 1.0), pect=(0.79, 0.13), pelv=(0.5, 0.1),
                 mouth=(0.3, 'term'), eye=(0.9, 0.32, 0.02), gill=0.78, scales=0.06),
    'sucker': dict(profile='fusiform', H=0.1, W=0.07, yn=0.42, yp=-0.34, dorsal=[(0.42, 0.6, 1.0, 'tri', 0.3)],
                   anal=[(0.16, 0.24, 0.9, 'tri', 0.3)], tail=('forked', 0.18, 1.1), pect=(0.8, 0.12), pelv=(0.45, 0.09),
                   mouth=(0.3, 'sub'), lips=True, eye=(0.88, 0.4, 0.019), gill=0.78, scales=0.08),
    'sunfish': dict(profile='deep', H=0.2, W=0.055, yn=0.34, yp=-0.3, dorsal=[(0.28, 0.68, 0.65, 'spiny', 0.15)],
                    anal=[(0.2, 0.42, 0.55, 'round', 0.2)], tail=('emarginate', 0.17, 0.8), pect=(0.74, 0.16, 'pointed'),
                    pelv=(0.64, 0.09), mouth=(0.2, 'term'), eye=(0.86, 0.42, 0.027), gill=0.74),
    'crappie': dict(profile='deep', H=0.17, W=0.05, yn=0.36, yp=-0.3, dorsal=[(0.2, 0.46, 1.1, 'round', 0.25)],
                    anal=[(0.15, 0.43, 1.0, 'round', 0.25)], tail=('emarginate', 0.17, 0.9), pect=(0.74, 0.14),
                    pelv=(0.64, 0.09), mouth=(0.55, 'up'), eye=(0.88, 0.35, 0.032), gill=0.74),
    'perch': dict(profile='fusiform', H=0.11, W=0.065, yn=0.42, yp=-0.34,
                  dorsal=[(0.5, 0.74, 1.1, 'spiny', 0.2), (0.27, 0.46, 0.9, 'tri', 0.25)], anal=[(0.2, 0.3, 0.8, 'tri', 0.3)],
                  tail=('forked', 0.17, 1.0), pect=(0.79, 0.12), pelv=(0.73, 0.11), mouth=(0.5, 'term'),
                  eye=(0.9, 0.35, 0.025), gill=0.77),
    'whitefish': dict(profile='fusiform', H=0.105, W=0.055, yn=0.42, yp=-0.35, dorsal=[(0.48, 0.6, 1.15, 'tri', 0.35)],
                      adipose=(0.17, 0.23), anal=[(0.18, 0.28, 0.7, 'tri', 0.35)], tail=('forked', 0.19, 1.25),
                      pect=(0.8, 0.12), pelv=(0.47, 0.09), mouth=(0.15, 'term'), eye=(0.9, 0.25, 0.024), gill=0.8),
    'trout': dict(profile='fusiform', H=0.11, W=0.06, yn=0.42, yp=-0.35, dorsal=[(0.46, 0.6, 1.0, 'tri', 0.3)],
                  adipose=(0.17, 0.23), anal=[(0.18, 0.29, 0.8, 'tri', 0.3)], tail=('emarginate', 0.17, 1.1),
                  pect=(0.8, 0.12), pelv=(0.46, 0.09), mouth=(0.5, 'term'), eye=(0.9, 0.3, 0.021), gill=0.79, scales=0.03),
    'bass': dict(profile='fusiform', H=0.135, W=0.07, yn=0.42, yp=-0.33,
                 dorsal=[(0.5, 0.74, 0.8, 'spiny', 0.2), (0.26, 0.5, 0.9, 'round', 0.2)], anal=[(0.17, 0.32, 0.75, 'round', 0.25)],
                 tail=('emarginate', 0.17, 1.0), pect=(0.78, 0.13), pelv=(0.71, 0.11), mouth=(0.8, 'term'),
                 eye=(0.9, 0.36, 0.024), gill=0.76),
    'pike': dict(profile='long', H=0.075, W=0.055, yn=0.48, yp=-0.36, head='duckbill', dorsal=[(0.11, 0.27, 1.15, 'round', 0.25)],
                 anal=[(0.09, 0.25, 1.0, 'round', 0.25)], tail=('forked', 0.16, 1.45), pect=(0.83, 0.1), pelv=(0.42, 0.08),
                 mouth=(0.8, 'term'), eye=(0.88, 0.55, 0.02), gill=0.76),
    'catfish': dict(profile='flathead', H=0.095, W=0.08, yn=0.44, yp=-0.34, head='flat', dorsal=[(0.6, 0.69, 1.3, 'tri', 0.25)],
                    adipose=(0.15, 0.3), anal=[(0.13, 0.38, 0.75, 'long', 0.1)], tail=('forked', 0.18, 1.15),
                    pect=(0.86, 0.14, 'pointed'), pelv=(0.5, 0.08), mouth=(0.5, 'wide'), eye=(0.94, 0.45, 0.013),
                    gill=0.82, barbels='whisker', scales=0),
    'burbot': dict(profile='eel', H=0.07, W=0.06, yn=0.45, yp=-0.4, head='flat',
                   dorsal=[(0.56, 0.66, 0.6, 'round', 0.1), (0.05, 0.54, 0.5, 'long', 0.05)], anal=[(0.05, 0.46, 0.5, 'long', 0.05)],
                   tail=('round', 0.1, 1.2), pect=(0.86, 0.08), pelv=(0.9, 0.06), mouth=(0.5, 'term'), eye=(0.94, 0.5, 0.014),
                   gill=0.84, barbels='chin', scales=0),
    'gar': dict(profile='long', H=0.06, W=0.05, yn=0.26, yp=-0.38,
                snout=[(0.04, 0.02, 0.016, -0.003), (0.12, 0.014, 0.011, -0.003), (0.22, 0.007, 0.006, -0.002)],
                dorsal=[(0.07, 0.2, 1.3, 'round', 0.25)], anal=[(0.06, 0.2, 1.3, 'round', 0.25)], tail=('round', 0.14, 1.7),
                pect=(0.86, 0.09), pelv=(0.45, 0.08), mouth=(0.0, 'none'), eye=(0.93, 0.4, 0.018), gill=0.86,
                teeth='beak', scales=0),
    'sturgeon': dict(profile='long', H=0.075, W=0.075, yn=0.36, yp=-0.32,
                     snout=[(0.05, 0.03, 0.022, -0.008), (0.11, 0.012, 0.01, -0.004)], dorsal=[(0.08, 0.2, 1.1, 'tri', 0.35)],
                     anal=[(0.06, 0.15, 0.9, 'tri', 0.3)], tail=('hetero', 0.24, 1.7), pect=(0.83, 0.15), pelv=(0.32, 0.08),
                     mouth=(0.3, 'sub'), lips=True, eye=(0.9, 0.5, 0.012), gill=0.82, barbels='sturgeon', scutes=True, scales=0),
    'paddlefish': dict(profile='long', H=0.085, W=0.07, yn=0.3, yp=-0.34, paddle=0.3, dorsal=[(0.1, 0.22, 1.1, 'tri', 0.35)],
                       anal=[(0.08, 0.2, 0.9, 'tri', 0.3)], tail=('hetero', 0.22, 1.7), pect=(0.8, 0.12), pelv=(0.4, 0.08),
                       mouth=(0.9, 'term'), eye=(0.95, 0.25, 0.01), gill=0.7, scales=0),
    'bowfin': dict(profile='long', H=0.095, W=0.07, yn=0.42, yp=-0.34, dorsal=[(0.18, 0.7, 0.55, 'long', 0.05)],
                   anal=[(0.13, 0.24, 0.8, 'round', 0.2)], tail=('round', 0.15, 1.25), pect=(0.82, 0.12), pelv=(0.5, 0.09),
                   mouth=(0.6, 'term'), eye=(0.91, 0.4, 0.016), gill=0.8, scales=0.05),
    'eel': dict(profile='eel', H=0.042, W=0.038, yn=0.5, yp=-0.47, dorsal=[(0.0, 0.58, 0.55, 'long', 0.04)],
                anal=[(0.0, 0.47, 0.5, 'long', 0.04)], tail=('eel', 0.04, 0.9), pect=(0.88, 0.05), pelv=None,
                mouth=(0.4, 'term'), eye=(0.95, 0.45, 0.009), gill=0.88, scales=0),
    'snakehead': dict(profile='long', H=0.08, W=0.068, yn=0.44, yp=-0.36, head='flat', dorsal=[(0.07, 0.68, 0.55, 'long', 0.05)],
                      anal=[(0.07, 0.44, 0.5, 'long', 0.05)], tail=('round', 0.13, 1.25), pect=(0.8, 0.11), pelv=(0.72, 0.07),
                      mouth=(0.8, 'term'), eye=(0.92, 0.5, 0.016), gill=0.78),
    'herring': dict(profile='fusiform', H=0.1, W=0.045, yn=0.4, yp=-0.34, dorsal=[(0.42, 0.56, 1.0, 'tri', 0.35)],
                    anal=[(0.15, 0.27, 0.45, 'long', 0.2)], tail=('forked', 0.2, 1.35), pect=(0.8, 0.1), pelv=(0.45, 0.07),
                    mouth=(0.3, 'up'), eye=(0.9, 0.3, 0.028), gill=0.8, scales=0.04),
    'mackerel': dict(profile='torpedo', H=0.095, W=0.075, yn=0.44, yp=-0.36,
                     dorsal=[(0.56, 0.72, 0.8, 'spiny', 0.25), (0.34, 0.45, 0.7, 'tri', 0.3)], anal=[(0.32, 0.42, 0.6, 'tri', 0.3)],
                     finlets=5, tail=('forked', 0.17, 1.6), pect=(0.79, 0.11), pelv=(0.73, 0.07), mouth=(0.5, 'term'),
                     eye=(0.9, 0.35, 0.022), gill=0.79, scales=0),
    'tuna': dict(profile='torpedo', H=0.14, W=0.12, yn=0.42, yp=-0.36,
                 dorsal=[(0.56, 0.72, 0.55, 'spiny', 0.25), (0.36, 0.47, 1.5, 'falcate', 0.6)], anal=[(0.33, 0.44, 1.4, 'falcate', 0.6)],
                 finlets=8, keel=True, tail=('lunate', 0.17, 1.2), pect=(0.79, 0.24, 'long'), pelv=(0.74, 0.07),
                 mouth=(0.4, 'term'), eye=(0.91, 0.3, 0.02), gill=0.79, scales=0),
    'mahi': dict(profile='blunt', H=0.13, W=0.055, yn=0.42, yp=-0.36, dorsal=[(0.07, 0.97, 0.7, 'long', 0.05)],
                 anal=[(0.07, 0.5, 0.55, 'long', 0.05)], tail=('forked', 0.21, 1.6), pect=(0.82, 0.12), pelv=(0.78, 0.1),
                 mouth=(0.4, 'term'), eye=(0.93, 0.1, 0.018), gill=0.83, scales=0),
    'cod': dict(profile='carp', H=0.12, W=0.08, yn=0.42, yp=-0.34,
                dorsal=[(0.6, 0.74, 0.9, 'round', 0.2), (0.39, 0.57, 0.8, 'round', 0.2), (0.12, 0.36, 0.7, 'round', 0.2)],
                anal=[(0.38, 0.55, 0.6, 'round', 0.2), (0.12, 0.34, 0.6, 'round', 0.2)], tail=('truncate', 0.15, 1.05),
                pect=(0.78, 0.12), pelv=(0.8, 0.1), mouth=(0.55, 'term'), eye=(0.9, 0.38, 0.022), gill=0.77, barbels='chin'),
    'seabass': dict(profile='fusiform', H=0.12, W=0.065, yn=0.42, yp=-0.34,
                    dorsal=[(0.5, 0.72, 1.0, 'spiny', 0.2), (0.28, 0.46, 0.9, 'tri', 0.3)], anal=[(0.2, 0.32, 0.8, 'tri', 0.3)],
                    tail=('forked', 0.17, 1.15), pect=(0.78, 0.12), pelv=(0.71, 0.1), mouth=(0.55, 'term'),
                    eye=(0.9, 0.36, 0.021), gill=0.77),
    'wrasse': dict(profile='fusiform', H=0.13, W=0.06, yn=0.42, yp=-0.33, dorsal=[(0.18, 0.78, 0.5, 'long', 0.05)],
                   anal=[(0.16, 0.42, 0.5, 'long', 0.05)], tail=('truncate', 0.14, 1.05), pect=(0.77, 0.12), pelv=(0.72, 0.08),
                   mouth=(0.35, 'term'), eye=(0.91, 0.42, 0.02), gill=0.77, scales=0.08),
    'flatfish': dict(profile='deep', H=0.25, W=0.028, yn=0.4, yp=-0.33, flat=True, dorsal=[(0.06, 0.96, 0.32, 'long', 0.05)],
                     anal=[(0.06, 0.72, 0.32, 'long', 0.05)], tail=('round', 0.13, 0.75), pect=(0.8, 0.07), pelv=(0.74, 0.05),
                     mouth=(0.35, 'term'), eye=(0.88, 0.5, 0.024), gill=0.76, scales=0.03),
    'grouper': dict(profile='carp', H=0.15, W=0.1, yn=0.42, yp=-0.32,
                    dorsal=[(0.45, 0.76, 0.55, 'spiny', 0.1), (0.2, 0.45, 0.75, 'round', 0.15)], anal=[(0.16, 0.32, 0.8, 'round', 0.2)],
                    tail=('round', 0.16, 1.05), pect=(0.75, 0.15, 'round'), pelv=(0.7, 0.11), mouth=(1.0, 'up'),
                    eye=(0.9, 0.42, 0.02), gill=0.73, scales=0.08),
    'billfish': dict(profile='torpedo', H=0.11, W=0.08, yn=0.34, yp=-0.36, dorsal=[(0.42, 0.82, 1.6, 'sail', 0.2)],
                     anal=[(0.3, 0.42, 1.1, 'falcate', 0.5)], keel=True, tail=('lunate', 0.17, 1.6), pect=(0.78, 0.2, 'long'),
                     pelv=(0.74, 0.14), mouth=(0.3, 'term'), eye=(0.94, 0.35, 0.019), gill=0.8, bill='round', scales=0),
    'oarfish': dict(profile='ribbon', H=0.06, W=0.014, yn=0.48, yp=-0.47, dorsal=[(0.0, 0.97, 0.55, 'long', 0.02)], anal=[],
                    tail=('eel', 0.03, 0.7), pect=(0.92, 0.04), pelv=None, mouth=(0.3, 'up'), eye=(0.95, 0.3, 0.012),
                    gill=0.9, crest=True, streamers=True, scales=0),
    'opah': dict(profile='disc', H=0.25, W=0.08, yn=0.38, yp=-0.28, dorsal=[(0.35, 0.72, 1.0, 'falcate', 0.45)],
                 anal=[(0.14, 0.4, 0.4, 'long', 0.1)], tail=('forked', 0.15, 0.85), pect=(0.72, 0.22, 'long'),
                 pelv=(0.6, 0.2, 'long'), mouth=(0.25, 'term'), eye=(0.88, 0.3, 0.03), gill=0.74, scales=0),
    'angler': dict(profile='angler', H=0.2, W=0.17, yn=0.4, yp=-0.3, dorsal=[(0.18, 0.34, 0.5, 'round', 0.1)],
                   anal=[(0.16, 0.3, 0.45, 'round', 0.1)], tail=('round', 0.16, 0.8), pect=(0.7, 0.1, 'round'), pelv=None,
                   mouth=(1.0, 'up'), eye=(0.82, 0.62, 0.022), gill=0.68, teeth='fang', lure=True, scales=0),
    'coelacanth': dict(profile='carp', H=0.14, W=0.08, yn=0.42, yp=-0.3, dorsal=[(0.58, 0.7, 0.9, 'tri', 0.3)], anal=[],
                       lobes=True, tail=('lobed', 0.2, 1.1), pect=None, pelv=None, mouth=(0.5, 'term'), eye=(0.9, 0.38, 0.025),
                       gill=0.74, scales=0.1),
}

def F(arch, back, side, belly, fin, **kw):
    """A species: its body plan, its back, side, belly and fin colours, and whatever else sets it apart."""
    sp = dict(FISH_ARCH[arch])
    sp.update(arch=arch, back=back, side=side, belly=belly, fin=fin)
    sp.update(kw)
    return sp

# Patterns (painted on the body): ('bars', colour, count, t0, t1, width, vmin, vmax), ('stripes', colour,
# [heights], half-width, t0, t1, broken), ('mottle', colour, scale, threshold, vmin, vmax), ('worms', colour,
# scale, width, vmin, vmax), ('region', colour, t0, t1, vmin, vmax), ('checker', colour, size), ('wavy', colour,
# count, vmin), ('cheek', colour, count), ('lateral', colour, height, half-width), ('rainbow', [colours]),
# ('tint', colour, t0, t1, amount). t runs from the tail (0) to the nose (1), v from the belly (-1) to the back (1).
# Spots (raised): ('one', colour, t, v, size, halo) or ('many', colour, count per side, size, t0, t1, vmin, vmax, halo).
# finspots: (colour, [fins], count, size).
FISH = {
    # -- carp and minnows --
    'shiner': F('minnow', '#6f7a3c', '#d9c06a', '#f3ead0', '#e6a24a', H=0.12, mouth=(0.25, 'up'), pat=[('lateral', '#b8963e', -0.05, 0.05)]),
    'rudd': F('minnow', '#4f5f3a', '#c9a548', '#efe3b8', '#d63a24', H=0.13, iris='#e8962a', dorsalc='#8a5a34'),
    'carp': F('carp', '#4e4a2a', '#a87d3a', '#e8d3a0', '#8a5a30', pelvc='#b0603a', analc='#b0603a', caudalc='#9a5a32', barbels='carp', scales=0.07),
    'chub': F('minnow', '#5a5e4c', '#b9baa2', '#f1f0e6', '#9c9a86', H=0.1, W=0.06, mouth=(0.55, 'term'),
              pat=[('stripes', '#3c3c30', [0.0], 0.07, 0.05, 0.8, 0)], spots=[('one', '#141414', 0.55, 0.97, 0.012, None)]),
    'sucker': F('sucker', '#55543f', '#a9a088', '#f0eee2', '#a08c6c'),
    'redhorse': F('sucker', '#6a6036', '#c2ad72', '#f2efe2', '#d03a2a', dorsalc='#6a6a60', pectc='#e07050', scales=0.1,
                  dorsal=[(0.42, 0.6, 1.0, 'falcate', 0.3)]),
    'koi': F('carp', '#f4f1ea', '#f4f1ea', '#f8f6f0', '#f2eee6', barbels='carp', caudalc='#f6e0d0', scales=0.05,
             pat=[('mottle', '#e8502a', 4.5, 0.12, -0.6, 1), ('mottle', '#1e1e1e', 6.5, 0.42, -0.3, 1)]),
    'emberkoi': F('carp', '#2a0e08', '#3a140a', '#4a1a0c', '#ff7a2a', edge='#ffd27a', barbels='carp', iris='#ffcf4a', scales=0.05,
                  dorsal=[(0.3, 0.64, 1.3, 'long', 0.5)], tail=('forked', 0.3, 1.35), pect=(0.79, 0.24), pelv=(0.5, 0.2),
                  pat=[('mottle', '#ff6a1a', 4.0, 0.0, -1, 1), ('mottle', '#ffcf4a', 7, 0.45, -1, 1)]),
    'goldfish': F('carp', '#e8761c', '#f08a24', '#f8c070', '#f5a040', H=0.15, iris='#f0c040', scales=0.08),
    'mudminnow': F('minnow', '#4e4028', '#8a7650', '#d8caa0', '#7a6844', dorsal=[(0.24, 0.38, 0.9, 'round', 0.15)],
                   anal=[(0.2, 0.32, 0.8, 'round', 0.15)], tail=('round', 0.14, 1.0),
                   pat=[('bars', '#4a3a22', 12, 0.1, 0.8, 0.35, -0.6, 0.9), ('region', '#2a2014', 0.0, 0.06, -0.8, 0.8)]),
    'tench': F('carp', '#2e4a26', '#5e7a3a', '#c8b870', '#3a5228', iris='#e0402a', barbels='carp', scales=0.03,
               dorsal=[(0.42, 0.58, 0.9, 'round', 0.15)], tail=('truncate', 0.17, 1.0), pect=(0.79, 0.13, 'round'), pelv=(0.5, 0.11, 'round')),
    'buffalo': F('carp', '#404c4c', '#8a8a78', '#d8d8cc', '#4a4a48', H=0.16, mouth=(0.5, 'up')),
    'bigheadcarp': F('carp', '#4e545a', '#7a8088', '#d4d6d8', '#5a5e62', head='big', eye=(0.86, -0.15, 0.016), iris='#c8c0a0',
                     mouth=(0.6, 'up'), dorsal=[(0.42, 0.58, 1.0, 'tri', 0.35)], pat=[('mottle', '#2e3236', 3.5, 0.25, -0.5, 1)]),
    'millstone': F('carp', '#6a6a64', '#8a8a82', '#b4b2a8', '#5a5a54', H=0.16, W=0.09, barbels='carp', scales=0.08,
                   pat=[('mottle', '#9aa070', 5, 0.35, -0.2, 1), ('mottle', '#55554e', 9, 0.4, -1, 1)], moss=('#7e8a52', 10)),
    'fallfish': F('minnow', '#5e6a6e', '#c6cace', '#f4f4f4', '#a8acac', scales=0.09, H=0.105),
    # -- sunfish and crappie --
    'pumpkinseed': F('sunfish', '#5e6e36', '#c8a040', '#f0a020', '#a89048', ear=('#141414', '#5a7a5a', '#e03020'),
                     pat=[('mottle', '#e07a2a', 14, 0.35, -0.5, 0.6), ('cheek', '#2ab0c0', 7), ('bars', '#4a5a2a', 7, 0.15, 0.72, 0.25, -0.3, 1)]),
    'bluegill': F('sunfish', '#2e3e4e', '#6e7e70', '#e8902a', '#5a6a6a', ear=('#0e0e0e', '#0e0e0e', None),
                  pat=[('bars', '#3a4a3a', 6, 0.12, 0.72, 0.35, -0.5, 1), ('region', '#e8902a', 0.55, 0.8, -1, -0.45)],
                  finspots=('#141414', ['dorsal'], 4, 0.01)),
    'crappie': F('crappie', '#3e4e3a', '#c8ccc4', '#eef0ea', '#cfd2c8', pat=[('mottle', '#262a22', 9, 0.2, -0.6, 1)],
                 finspots=('#2a2a24', ['dorsal', 'anal', 'caudal'], 10, 0.008)),
    'rockbass': F('sunfish', '#5e5030', '#8a7a48', '#d8caa0', '#6a5a38', mouth=(0.55, 'term'), eye=(0.86, 0.42, 0.03), iris='#c01818',
                  ear=('#1a1a1a', '#1a1a1a', None), pat=[('stripes', '#2a2418', [0.6, 0.35, 0.1, -0.15, -0.4], 0.07, 0.05, 0.75, 1)]),
    'warmouth': F('sunfish', '#5a4a2c', '#7a6a42', '#e0d0a0', '#6a5a38', mouth=(0.6, 'term'), iris='#b01818',
                  ear=('#2a1a10', '#c09040', None), pat=[('mottle', '#3a2e1e', 8, 0.1, -0.6, 1), ('cheek', '#4a3020', 4)]),
    'greensunfish': F('sunfish', '#3e5a4c', '#5a7a62', '#e8d070', '#4a6a5a', H=0.16, mouth=(0.6, 'term'), edge='#f0c040',
                      ear=('#101010', '#e0d8a0', None), pat=[('cheek', '#3ac0a0', 6), ('mottle', '#3a8a70', 12, 0.4, -0.4, 0.8)]),
    'dollarsunfish': F('sunfish', '#55552e', '#7a8a5a', '#f09030', '#c04a2a', H=0.22, ear=('#101010', '#40c0b0', None),
                       pat=[('mottle', '#e07a2a', 13, 0.25, -0.6, 0.5), ('mottle', '#3ab0a0', 11, 0.4, -0.4, 0.8),
                            ('lateral', '#e0602a', 0.25, 0.035), ('cheek', '#3ab0a0', 5)]),
    'whitecrappie': F('crappie', '#5e6e56', '#d8dcd4', '#f2f4ee', '#d4d8ce', pat=[('bars', '#5a6050', 8, 0.1, 0.75, 0.4, -0.4, 1)],
                      finspots=('#4a4e44', ['dorsal', 'anal', 'caudal'], 8, 0.007)),
    # -- perch and walleye --
    'perch': F('perch', '#3e4e22', '#d6be3c', '#f2f0e0', '#c8b860', pelvc='#f07020', analc='#f07020',
               pat=[('bars', '#2a361a', 7, 0.08, 0.75, 0.4, -0.45, 1)], finspots=('#141414', ['dorsal'], 3, 0.01)),
    'walleye': F('perch', '#40401f', '#b49e4c', '#f2efe0', '#a8964a', H=0.1, eye=(0.9, 0.38, 0.03), iris='#e0eaea', teeth='canine',
                 pat=[('mottle', '#5a5226', 8, 0.15, -0.3, 1)], tailtip='#f4f4f0'),
    'sauger': F('perch', '#5e5038', '#9a8a62', '#f0ece0', '#8a7a58', H=0.095, eye=(0.9, 0.38, 0.026), iris='#d8e0d8',
                pat=[('bars', '#3a3024', 4, 0.1, 0.8, 0.35, -0.2, 1), ('mottle', '#3a3024', 10, 0.3, -0.4, 1)],
                finspots=('#1a1a14', ['dorsal'], 14, 0.007)),
    'logperch': F('perch', '#a8a070', '#c8c08a', '#f2eee0', '#c0b88a', H=0.085, W=0.06, head='pig', mouth=(0.2, 'sub'),
                  pat=[('bars', '#2a2a1e', 18, 0.06, 0.78, 0.35, -0.75, 1)], spots=[('one', '#141414', 0.03, 0.0, 0.013, None)]),
    # -- whitefish --
    'cisco': F('whitefish', '#3e5e7e', '#d0d8e0', '#f6f7f8', '#c0c8d0', pat=[('tint', '#e0c0d0', 0.2, 0.8, 0.12)]),
    'mooneye': F('whitefish', '#5e7468', '#d8dde0', '#f6f7f8', '#c8ccd0', H=0.13, adipose=None, eye=(0.9, 0.3, 0.036), iris='#e8e8e0',
                 dorsal=[(0.38, 0.48, 1.0, 'tri', 0.35)], anal=[(0.12, 0.36, 0.6, 'long', 0.2)]),
    'whitefish': F('whitefish', '#4e5e5e', '#cfd3d3', '#f4f4f4', '#b8bcbc', mouth=(0.12, 'sub'), H=0.11),
    'willowwisp': F('whitefish', '#a8e8d0', '#e6fff4', '#f6fffa', '#c8fff0', edge='#60ffb0', iris='#80ffd0',
                    spots=[('many', '#a0ffd0', 14, 0.009, 0.15, 0.8, -0.3, 0.9, None)]),
    # -- catfish, bullheads and burbot --
    'bullhead': F('catfish', '#3e3020', '#6e5638', '#e8d8a8', '#4a3a26', tail=('truncate', 0.15, 1.0), barbelc='#2e2a24',
                  pat=[('mottle', '#2e2416', 7, 0.15, -0.4, 1)]),
    'catfish': F('catfish', '#4a5868', '#7a8a9a', '#f2f2f0', '#5a6878', barbelc='#3a3a3a',
                 spots=[('many', '#141414', 22, 0.008, 0.15, 0.85, -0.3, 0.8, None)]),
    'burbot': F('burbot', '#5e5028', '#8a7a48', '#d8cca0', '#6a5a34',
                pat=[('mottle', '#3a2e18', 9, 0.05, -0.5, 1), ('worms', '#c8b870', 10, 0.06, -0.3, 1)]),
    'dockmaster': F('catfish', '#2e2e2a', '#4a4a44', '#c8c4b0', '#2a2a26', H=0.11, W=0.095, tail=('truncate', 0.15, 1.0),
                    barbelc='#1e1e1c', whisk=1.3, hook=True, pat=[('worms', '#8a8a80', 6, 0.025, -0.5, 1)]),
    'yellowbullhead': F('catfish', '#7a6a2a', '#a8983e', '#f0d850', '#8a7a34', tail=('round', 0.14, 0.95), barbelc='#f0f0e0'),
    'flathead': F('catfish', '#6a5628', '#8a7038', '#efe4c0', '#6a5628', head='flatter', tail=('truncate', 0.15, 1.0), jut=True,
                  pat=[('mottle', '#4a3a20', 7, 0.05, -0.4, 1)]),
    'oldwhiskers': F('catfish', '#4e545c', '#6a7078', '#d8dad8', '#4a5058', whisk=2.2, barbelc='#e8e8e0', H=0.105),
    'blackbullhead': F('catfish', '#24241e', '#38382e', '#e8dcb0', '#26261e', tail=('truncate', 0.15, 1.0), barbelc='#1a1a16',
                       pat=[('region', '#a8a090', 0.0, 0.05, -0.7, 0.7)]),
    # -- trout, char and salmon --
    'trout': F('trout', '#46664a', '#d4d8cc', '#f6f6f2', '#9aa490', pat=[('stripes', '#e07a8a', [0.0], 0.17, 0.05, 0.85, 0)],
               spots=[('many', '#141414', 45, 0.007, 0.0, 0.75, -0.05, 1, None)],
               finspots=('#141414', ['dorsal', 'caudal', 'adipose'], 14, 0.006)),
    'brooktrout': F('trout', '#2e3e24', '#4e5e3a', '#e8602a', '#e86a30', dorsalc='#3a4a2a', caudalc='#4a5a34', tail=('truncate', 0.16, 1.05),
                    lead=('#f4f4f4', '#141414'), pat=[('worms', '#c8c080', 16, 0.07, 0.35, 1)],
                    spots=[('many', '#e8d060', 30, 0.007, 0.05, 0.8, -0.2, 0.5, None), ('many', '#e03030', 10, 0.006, 0.1, 0.75, -0.3, 0.3, '#4a7ad0')],
                    finspots=('#c8c080', ['dorsal'], 8, 0.006)),
    'grayling': F('trout', '#5a6070', '#a8b0bc', '#e8eaee', '#8a8aa0', dorsal=[(0.38, 0.72, 1.6, 'sail', 0.1)], dorsalc='#5a4a8a',
                  edges={'dorsal': '#e05050'}, finspots=('#b080e0', ['dorsal'], 14, 0.008), mouth=(0.25, 'term'),
                  tail=('forked', 0.18, 1.15), spots=[('many', '#141414', 10, 0.006, 0.55, 0.8, -0.2, 0.6, None)]),
    'laketrout': F('trout', '#4e5e5e', '#7a8a88', '#eeeeea', '#6a7a78', tail=('forked', 0.19, 1.2),
                   spots=[('many', '#e8e8d0', 60, 0.008, 0.0, 0.85, -0.3, 1, None)], finspots=('#e8e8d0', ['dorsal', 'caudal'], 10, 0.006)),
    'steelhead': F('trout', '#465a6e', '#d8dde2', '#f6f7f8', '#a8b0b8', pat=[('stripes', '#e8a0a8', [0.0], 0.1, 0.1, 0.8, 0)],
                   spots=[('many', '#141414', 25, 0.006, 0.0, 0.7, 0.3, 1, None)], finspots=('#141414', ['caudal', 'dorsal'], 14, 0.005)),
    'chinook': F('trout', '#365664', '#ccd5da', '#f6f7f8', '#8a9aa0', H=0.12, mouthc='#101010', tail=('emarginate', 0.18, 1.15),
                 spots=[('many', '#141414', 35, 0.007, 0.0, 0.75, 0.25, 1, None)], finspots=('#141414', ['caudal', 'dorsal'], 20, 0.006)),
    'goldentrout': F('trout', '#7a8a3a', '#e8b030', '#d84020', '#e8a040', lead=('#f4f4f4', None), edges={'dorsal': '#f4f4f4'},
                     pat=[('stripes', '#e03a2a', [0.0], 0.08, 0.05, 0.85, 0), ('bars', '#7a8a3a', 10, 0.08, 0.75, 0.35, -0.25, 0.25)],
                     spots=[('many', '#141414', 12, 0.006, 0.0, 0.4, 0.4, 1, None)], finspots=('#141414', ['dorsal', 'caudal'], 10, 0.005)),
    'stonejaw': F('trout', '#5a5e60', '#7a7e80', '#c8c8c4', '#5a5e60', H=0.125, kype=True, teeth='canine',
                  pat=[('mottle', '#4a4c4e', 8, 0.2, -1, 1)], spots=[('many', '#c8c8c0', 50, 0.009, 0.0, 0.85, -0.3, 1, None)]),
    'frostfin': F('trout', '#7aa8c8', '#cfe8f5', '#f4fbff', '#f4fbff', edge='#6ab8f0', iris='#a0e0ff',
                  spots=[('many', '#ffffff', 35, 0.008, 0.0, 0.85, -0.2, 1, None)]),
    'riverking': F('trout', '#4a2020', '#a82a2a', '#e8c8b0', '#6a2a2a', H=0.13, kype=True, pat=[('tint', '#ffcf4a', 0.85, 1.0, 0.15)],
                   spots=[('many', '#141414', 30, 0.008, 0.0, 0.75, 0.2, 1, None)], finspots=('#141414', ['caudal', 'dorsal'], 18, 0.006)),
    'aurora': F('trout', '#a04a70', '#d0607a', '#ff8a2a', '#ff7a3a', dorsalc='#7a3a58', caudalc='#9a4a5a', lead=('#f4f4f4', '#141414'),
                tail=('truncate', 0.16, 1.05)),
    'tigertrout': F('trout', '#4a4a24', '#c8b050', '#f0e0b0', '#e0903a', tail=('truncate', 0.16, 1.05),
                    pat=[('worms', '#3a3a20', 14, 0.09, -0.6, 1)]),
    'cutthroat': F('trout', '#6e6e40', '#c8b890', '#f0e8d8', '#a8a080', pat=[('tint', '#e8a0a0', 0.2, 0.8, 0.15), ('region', '#e0402a', 0.86, 0.97, -0.95, -0.7)],
                   spots=[('many', '#141414', 45, 0.006, 0.0, 0.55, -0.3, 1, None)], finspots=('#141414', ['caudal', 'dorsal'], 16, 0.005)),
    'splake': F('trout', '#2e3e30', '#5a6a58', '#eeeeea', '#c86a3a', lead=('#f4f4f4', None), tail=('emarginate', 0.17, 1.1),
                pat=[('worms', '#c8c8a0', 14, 0.06, 0.4, 1)], spots=[('many', '#e8e8c8', 40, 0.007, 0.0, 0.85, -0.3, 0.6, None)]),
    'arcticchar': F('trout', '#46565a', '#6a7a7e', '#e85a2a', '#e8702a', dorsalc='#4a5a5a', caudalc='#5a6060', lead=('#f4f4f4', None),
                    spots=[('many', '#f0c8c0', 35, 0.008, 0.0, 0.85, -0.4, 0.8, None)]),
    'crystalchar': F('trout', '#9ad0f0', '#bfe6ff', '#f0f8ff', '#d8f0ff', edge='#ffffff', iris='#ffb0d0',
                     pat=[('mottle', '#e8f8ff', 14, 0.4, -1, 1)], spots=[('many', '#ffb0d0', 30, 0.008, 0.0, 0.85, -0.4, 0.9, None)]),
    'glimmerfin': F('trout', '#7a70b0', '#b8b0e0', '#efeaff', '#c8a0f0', dorsal=[(0.38, 0.72, 1.6, 'sail', 0.1)], dorsalc='#8a60c0',
                    edges={'dorsal': '#ffe080'}, finspots=('#ffe080', ['dorsal'], 18, 0.008),
                    spots=[('many', '#ffe080', 20, 0.006, 0.1, 0.85, -0.2, 0.8, None)]),
    'browntrout': F('trout', '#5a4a2a', '#a07a3a', '#f0dca0', '#8a6a3a', adiposec='#e05020', tail=('truncate', 0.16, 1.05),
                    spots=[('many', '#141414', 40, 0.008, 0.15, 0.85, -0.1, 1, '#f0e0c0'), ('many', '#d03a20', 14, 0.007, 0.1, 0.75, -0.3, 0.3, '#f0e0c0')],
                    finspots=('#141414', ['dorsal'], 8, 0.006)),
    'atlanticsalmon': F('trout', '#465868', '#d6dbe0', '#f6f7f8', '#7a8a98', H=0.115,
                        spots=[('many', '#141414', 30, 0.007, 0.15, 0.85, 0.2, 1, None)]),
    'rapidsrunner': F('trout', '#3a4a5a', '#d8dde2', '#f6f7f8', '#a8c0d8', edge='#40c0ff', pat=[('stripes', '#40c0ff', [0.0], 0.1, 0.02, 0.9, 0)],
                      spots=[('many', '#141414', 20, 0.006, 0.0, 0.7, 0.3, 1, None)]),
    # -- black bass --
    'bass': F('bass', '#3a5a28', '#88a052', '#f2f2e6', '#6a8048', pat=[('stripes', '#283818', [0.05], 0.16, 0.05, 0.85, 1), ('mottle', '#4a6a30', 9, 0.3, 0.3, 1)]),
    'smallmouth': F('bass', '#6a5226', '#8a6a32', '#e8dcb8', '#7a6230', H=0.12, mouth=(0.6, 'term'), iris='#c01818',
                    pat=[('bars', '#4a3a18', 10, 0.08, 0.75, 0.4, -0.4, 1), ('cheek', '#4a3a18', 3)]),
    'emeraldjaw': F('bass', '#0e6a3a', '#1aa060', '#d8f0d0', '#1a8a50', iris='#ffe060', edge='#e8d060',
                    pat=[('stripes', '#0a4a28', [0.05], 0.14, 0.05, 0.85, 1), ('region', '#e8d060', 0.88, 1.0, -0.6, -0.15)]),
    'hybridbass': F('seabass', '#3a4a5a', '#d0d4d8', '#f6f6f6', '#a0a8b0', H=0.13,
                    pat=[('stripes', '#242424', [0.75, 0.5, 0.25, 0.0, -0.25], 0.06, 0.05, 0.8, 1)]),
    'spottedbass': F('bass', '#5a6a30', '#8a9a50', '#eeeedd', '#7a8a4a', mouth=(0.65, 'term'),
                     pat=[('mottle', '#2e3a1a', 6, 0.25, -0.15, 0.35), ('stripes', '#4a5a2a', [-0.35, -0.55], 0.06, 0.1, 0.75, 1)]),
    # -- ancient fish --
    'bowfin': F('bowfin', '#4e5a30', '#6a7a42', '#e0d8a8', '#5a7a40', pectc='#4a9a50', pelvc='#4a9a50', analc='#4a9a50',
                pat=[('mottle', '#3a4424', 7, 0.15, -0.4, 1)], spots=[('one', '#141414', 0.03, 0.6, 0.02, '#f08a2a')]),
    'gar': F('gar', '#5a5a36', '#9a9a74', '#e8e6d6', '#7a7a50', pat=[('checker', '#4a4a30', 0.02)],
             spots=[('many', '#24241a', 25, 0.007, 0.0, 0.4, -0.6, 1, None)], finspots=('#24241a', ['dorsal', 'anal', 'caudal'], 12, 0.006)),
    'sturgeon': F('sturgeon', '#5a5a48', '#7a7a66', '#ecece4', '#5a5a4a', scutec='#a8a890'),
    'paddlefish': F('paddlefish', '#4e5e6e', '#6a7a88', '#eef0f2', '#4e5e6e'),
    'alligatorgar': F('gar', '#4e4e30', '#7a7a54', '#e0dcc8', '#6a6a44', H=0.075, W=0.065,
                      snout=[(0.03, 0.03, 0.018, -0.002), (0.09, 0.022, 0.012, -0.002), (0.13, 0.014, 0.008, -0.001)],
                      pat=[('checker', '#3a3a24', 0.022)], spots=[('many', '#24241a', 25, 0.009, 0.0, 0.45, -0.6, 1, None)],
                      finspots=('#24241a', ['dorsal', 'anal', 'caudal'], 10, 0.007)),
    'ghost': F('sturgeon', '#d8dcd8', '#e8ece8', '#f8f8f6', '#d0d8dc', scutec='#a8c8e0', iris='#a8d8ff', pat=[('mottle', '#c8ccd0', 7, 0.35, -1, 1)]),
    'marshqueen': F('bowfin', '#3e7a30', '#4a8a3a', '#e8e0a0', '#3a7a40', edge='#e0c050', frill=('#e0c050', 0.78, 0.92),
                    pat=[('mottle', '#e0c050', 10, 0.42, -0.3, 1)], spots=[('one', '#e0c050', 0.03, 0.6, 0.024, '#1a3a10')]),
    'lakewyrm': F('gar', '#1e3a2e', '#2a4a3a', '#a8b890', '#3a6a50', H=0.07, frill=('#3a6a50', 0.25, 0.85), iris='#ffe040',
                  pat=[('checker', '#16301f', 0.022)]),
    'americaneel': F('eel', '#4e4628', '#7a6e40', '#d8c880', '#5a5030'),
    'spottedgar': F('gar', '#6a6a44', '#9a9a70', '#ece8d8', '#8a8a5a',
                    snout=[(0.03, 0.02, 0.015, -0.003), (0.08, 0.014, 0.01, -0.003), (0.14, 0.007, 0.006, -0.002)],
                    spots=[('many', '#1e1e14', 70, 0.008, 0.0, 1.0, -0.8, 1, None)],
                    finspots=('#1e1e14', ['dorsal', 'anal', 'caudal', 'pect', 'pelv'], 12, 0.006)),
    'snakehead': F('snakehead', '#6a5a38', '#8a7a50', '#d8ccaa', '#7a6a48', pat=[('mottle', '#3a2e1e', 5, 0.1, -0.5, 1)]),
    'nightbowfin': F('bowfin', '#101218', '#181c26', '#2a3040', '#1a2440', edge='#2a4a8a', iris='#40e0ff',
                     pat=[('mottle', '#2a3a6a', 8, 0.35, -0.5, 1)], spots=[('one', '#0a0a0a', 0.03, 0.6, 0.022, '#40e0ff')]),
    'mirefang': F('snakehead', '#2a3420', '#3a4a2a', '#8a8a60', '#2e3a24', iris='#e02020', teeth='fang',
                  pat=[('mottle', '#141a10', 5, 0.1, -0.6, 1)]),
    'shovelnose': F('sturgeon', '#8a7650', '#a08a60', '#f0ece2', '#8a7a58', snout=[(0.04, 0.04, 0.012, -0.006), (0.1, 0.025, 0.006, -0.004)],
                    tail=('hetero', 0.3, 1.8), scutec='#c0aa80'),
    # -- pike --
    'pickerel': F('pike', '#5a6a30', '#8a9a50', '#efeedc', '#7a8a4a', pat=[('worms', '#2a3018', 9, 0.12, -0.4, 1), ('region', '#141410', 0.86, 0.9, -0.5, 0.1)]),
    'pike': F('pike', '#2e4a22', '#4a6a32', '#f2f0e0', '#6a7a3a', pelvc='#b86a3a', analc='#9a6a3a',
              spots=[('many', '#d8d890', 60, 0.009, 0.0, 0.85, -0.4, 0.8, None)], finspots=('#1e2a14', ['dorsal', 'anal', 'caudal'], 12, 0.008)),
    'muskie': F('pike', '#7a7a50', '#a8a878', '#f2f0e2', '#9a8a5a', pat=[('bars', '#3a3a20', 11, 0.05, 0.85, 0.3, -0.5, 1)],
                finspots=('#3a3a20', ['dorsal', 'anal', 'caudal'], 12, 0.008)),
    'mossback': F('pike', '#1e3a12', '#3a5a2a', '#d8dcc0', '#4a6a2a', H=0.085, moss=('#5a7a2a', 14),
                  spots=[('many', '#c8d090', 50, 0.01, 0.0, 0.85, -0.4, 0.8, None)]),
    'redfin': F('pike', '#5e4e30', '#8a7a50', '#ece4cc', '#e0502a', dorsalc='#7a6a44', H=0.07,
                pat=[('bars', '#3a2e1a', 14, 0.05, 0.85, 0.35, -0.6, 1), ('region', '#141410', 0.86, 0.9, -0.5, 0.1)]),
    # -- the sea --
    'herring': F('herring', '#2a5a7a', '#d8e0e8', '#f8f9fa', '#a8b8c4'),
    'mackerel': F('mackerel', '#2a7a6a', '#d8e4e4', '#f8f9fa', '#7a9a94', finletc='#e0d040', pat=[('wavy', '#141a1a', 14, 0.3)]),
    'sardine': F('herring', '#2a4a8a', '#dce4ee', '#f8f9fa', '#a8b4c4', H=0.09, spots=[('many', '#1a1a2a', 7, 0.007, 0.35, 0.78, 0.25, 0.4, None)]),
    'mahimahi': F('mahi', '#1a8a7a', '#e0c040', '#f4e890', '#2a8ab0', dorsalc='#1a6aaa',
                  spots=[('many', '#2ab0a0', 30, 0.007, 0.05, 0.85, -0.3, 0.6, None)]),
    'cod': F('cod', '#7a6a3e', '#9a8a5a', '#f0ecdc', '#8a7a50', pat=[('mottle', '#5a4a2a', 11, 0.25, -0.3, 1), ('lateral', '#ece4c4', 0.25, 0.04)]),
    'seabass': F('seabass', '#4a5a5a', '#d8dce0', '#f6f7f8', '#9aa0a8',
                 pat=[('stripes', '#1e2224', [0.75, 0.5, 0.25, 0.0, -0.25, -0.5], 0.06, 0.05, 0.82, 0)]),
    'sheephead': F('wrasse', '#1a1a1a', '#d8505a', '#e86a6a', '#2a1a1a', head='hump', H=0.14, iris='#c01818', teeth='canine', caudalc='#1a1a1a',
                   pat=[('region', '#1a1a1a', 0.72, 1.0, -0.6, 1), ('region', '#1a1a1a', 0.0, 0.3, -1, 1), ('region', '#f4f4f4', 0.86, 1.0, -1, -0.55)]),
    'flounder': F('flatfish', '#c8dcd4', '#d8e8e0', '#f4f8f6', '#c8dcd8', edge='#e8f4f0', pat=[('mottle', '#a89880', 8, 0.35, -1, 1)],
                  spots=[('many', '#f4fffa', 12, 0.012, 0.1, 0.85, -0.7, 0.7, None)]),
    'parrotfish': F('wrasse', '#1aa090', '#2ab0a0', '#7ad0c0', '#f07aa0', H=0.14, W=0.07, beak='#e8f0e8', edge='#2a6ad0',
                    tail=('emarginate', 0.15, 1.15), pat=[('checker', '#f07aa0', 0.03), ('region', '#f0d040', 0.85, 1.0, -0.3, 0.4)]),
    'bluefin': F('tuna', '#1a2a5a', '#c8d0d8', '#f6f7f8', '#3a4a6a', dorsalc='#3a4a6a', analc='#c8c070', finletc='#f0d020',
                 pat=[('lateral', '#3a5aa0', 0.3, 0.05)]),
    'grouper': F('grouper', '#6a5a34', '#8a7a4a', '#c8b88a', '#6a5a34', H=0.16, W=0.11,
                 pat=[('bars', '#4a3e24', 5, 0.1, 0.8, 0.5, -0.6, 1), ('mottle', '#5a4a2a', 12, 0.3, -1, 1)],
                 finspots=('#3a3020', ['dorsal', 'anal', 'caudal', 'pect'], 12, 0.007)),
    'swordfish': F('billfish', '#3a2e3e', '#a8a0a8', '#d8d0d4', '#3a2e3e', bill='sword', dorsal=[(0.68, 0.8, 2.0, 'falcate', 0.5)], pelv=None),
    'oarfish': F('oarfish', '#c8ccd4', '#dce0e4', '#e8ecf0', '#d82a3a', pat=[('mottle', '#5a6a8a', 14, 0.45, -1, 1), ('stripes', '#a8b4d0', [0.5, -0.5], 0.06, 0.0, 0.95, 1)]),
    'opah': F('opah', '#3a5a8a', '#d85a4a', '#f0a8a0', '#e83a2a', iris='#f0c040', spots=[('many', '#f4f4f4', 45, 0.009, 0.05, 0.9, -0.8, 0.9, None)]),
    'marlin': F('billfish', '#1a2a6a', '#c8d0dc', '#f0f2f6', '#1a2a5a', pat=[('bars', '#8ab0e0', 12, 0.1, 0.8, 0.25, -0.3, 0.7)]),
    'kelpbeard': F('grouper', '#4a5a2e', '#5a6a3a', '#a8a880', '#4a6a2a', kelp=('#4a7a2a', 14), pat=[('mottle', '#3a4a22', 8, 0.15, -1, 1)]),
    'prismwrasse': F('wrasse', '#8a3ac0', '#e8c040', '#f4e8b0', '#40a0e0', edge='#ffffff',
                     pat=[('rainbow', ['#7a2ac0', '#2a6ae0', '#2ab0a0', '#5ac040', '#f0d040', '#f08a2a', '#e03a3a'])]),
    'thunderfin': F('tuna', '#1e2432', '#3a4258', '#b0b8c8', '#ffe040', finletc='#ffe040', edge='#fff6a0', pat=[('worms', '#ffe040', 7, 0.05, -0.4, 1)]),
    'drownedcaptain': F('grouper', '#3a4848', '#5a6a6a', '#8a9a98', '#3a4a4a', H=0.17, W=0.12, iris='#40e0d0', edge='#1a2424',
                        barnacles=('#d8d4c8', 18), pat=[('mottle', '#2a3434', 8, 0.1, -1, 1)]),
    'stormcaller': F('billfish', '#262c38', '#4a5262', '#a8b0c0', '#262c38', bill='sword', dorsal=[(0.62, 0.8, 1.9, 'falcate', 0.5)],
                     edge='#a0c8ff', pat=[('worms', '#e0f0ff', 6, 0.035, -0.6, 1)]),
    'glasshalibut': F('flatfish', '#a8cce0', '#cfe6f0', '#f4fbff', '#d8f0ff', H=0.26, edge='#ffffff', pat=[('mottle', '#b8dcf0', 10, 0.3, -1, 1)],
                      spots=[('many', '#ffffff', 20, 0.01, 0.1, 0.85, -0.7, 0.7, None)]),
    'leviathan': F('eel', '#141c34', '#1a2440', '#2a3a5a', '#1a2a50', H=0.06, W=0.05, frill=('#2a3a6a', 0.1, 0.9), iris='#40e0ff',
                   teeth='fang', edge='#40e0ff', spots=[('many', '#40e0ff', 40, 0.006, 0.0, 1.0, -0.2, 0.9, None)]),
    'silvermoon': F('opah', '#a8b0c4', '#e0e4ec', '#f4f6fa', '#c0b0e0', iris='#e0e0ff', spots=[('many', '#ffffff', 40, 0.01, 0.05, 0.9, -0.8, 0.9, None)]),
    'abyssking': F('angler', '#121418', '#1a1c22', '#2a2c34', '#1a1c22', iris='#80f0ff', lurec='#80f0ff', frill=('#d8b040', 0.62, 0.78)),
    'tidemother': F('coelacanth', '#1e5a6a', '#2a6a7a', '#8ab0b8', '#2a6a7a', iris='#c8f0f0',
                    spots=[('many', '#e8f0f0', 45, 0.01, 0.05, 0.9, -0.8, 0.9, None)]),
}
FISH_IDS = list(FISH) + ['boot', 'treasure']

class FishBody:
    """The body's outline: at(t) gives the cross-section (y, half-width, half-height, centre height) from the
    tail end (t = 0) to the nose (1); surf(t, v, side) a point on the skin and its outward direction."""

    def __init__(self, sp):
        prof = FISH_PROFILES[sp['profile']]
        self.hp, self.wp = prof['h'], prof.get('w', prof['h'])
        self.sp = sp
        self.H, self.W, self.yn, self.yp = sp['H'], sp['W'], sp['yn'], sp['yp']

    def at(self, t):
        t = max(0.0, min(1.0, t))
        H = self.H
        y = self.yp + (self.yn - self.yp) * t
        rz, rx = H * interp(self.hp, t), self.W * interp(self.wp, t)
        zo = -H * 0.1 * math.sin(math.pi * t)
        k = smooth((t - 0.8) / 0.2)
        head = self.sp.get('head')
        if head == 'duckbill':
            rz *= 1 - 0.45 * k
            rx *= 1 + 0.12 * k
        elif head == 'flat':
            rz *= 1 - 0.3 * k
        elif head == 'flatter':
            rz *= 1 - 0.45 * k
            rx *= 1 + 0.1 * k
        elif head == 'pig':  # the logperch's snout overhangs its mouth
            zo += rz * 0.4 * k
            rz *= 1 - 0.2 * k
        elif head == 'hump':  # a bulging forehead (a male sheephead)
            bump = math.exp(-((t - 0.82) / 0.07) ** 2)
            rz += H * 0.12 * bump
            zo += H * 0.1 * bump
        elif head == 'big':  # bighead carp: a huge head
            s = smooth((t - 0.6) / 0.2)
            rz *= 1 + 0.15 * s
            rx *= 1 + 0.12 * s
        return y, rx, rz, zo

    def surf(self, t, v, side=1, out=0.0):
        y, rx, rz, zo = self.at(t)
        a = math.asin(max(-1.0, min(1.0, v)))
        n = Vector((side * math.cos(a) / max(rx, 1e-4), 0, math.sin(a) / max(rz, 1e-4))).normalized()
        return Vector((side * rx * math.cos(a), y, zo + rz * math.sin(a))) + n * out, n

    def t_of(self, y):
        return (y - self.yp) / (self.yn - self.yp)

def fish_pattern(p, base, t, v, c, off):
    kind = p[0]
    if kind == 'bars':
        _, col, n, t0, t1, wd, vmin, vmax = p
        if t0 <= t <= t1 and vmin <= v <= vmax and ((t - t0) / (t1 - t0) * n + 0.06 * noise.noise(c * 12 + off)) % 1.0 < wd:
            return col
    elif kind == 'stripes':
        _, col, vs, hw, t0, t1, broken = p
        if t0 <= t <= t1:
            for vc in vs:
                if abs(v - vc) < hw and (not broken or noise.noise(c * 18 + off) > -0.25):
                    return col
    elif kind == 'mottle':
        _, col, sc, th, vmin, vmax = p
        if vmin <= v <= vmax and noise.noise(c * sc + off) > th:
            return col
    elif kind == 'worms':
        _, col, sc, wd, vmin, vmax = p
        if vmin <= v <= vmax and abs(noise.noise(c * sc + off)) < wd:
            return col
    elif kind == 'region':
        _, col, t0, t1, vmin, vmax = p
        if t0 <= t <= t1 and vmin <= v <= vmax:
            return col
    elif kind == 'checker':  # diamond scales (gars)
        _, col, size = p
        if (math.floor((c.y + c.z) / size) + math.floor((c.y - c.z) / size)) % 2 == 0:
            return mix(base, col, 0.55)
    elif kind == 'wavy':  # a mackerel's tiger stripes on its back
        _, col, n, vmin = p
        if v > vmin and t < 0.85 and (t * n + 0.3 * math.sin(v * 9 + t * 20)) % 1.0 < 0.42:
            return col
    elif kind == 'cheek':  # wavy lines on the cheek and gill cover
        _, col, n = p
        if 0.74 < t < 0.97 and -0.55 < v < 0.35 and (v * n + t * 9) % 1.0 < 0.35:
            return col
    elif kind == 'lateral':
        _, col, vc, hw = p
        if abs(v - vc) < hw and t < 0.8:
            return col
    elif kind == 'rainbow':
        cols = p[1]
        return cols[max(0, min(len(cols) - 1, int((v + 1) / 2 * len(cols))))]
    elif kind == 'tint':
        _, col, t0, t1, amt = p
        if t0 <= t <= t1:
            return mix(base, col, amt)
    return base

def fish_fan(k, base, tips, col, edge=None, spots=None, lead=None, lead_front=True, tip_low=None):
    """A ray fin: membrane between rays running from each base point to its tip, banded at the edge."""
    n = len(base)
    edge = edge or shade(col, 0.8)
    for i in range(n - 1):
        a0, a1, b0, b1 = Vector(base[i]), Vector(base[i + 1]), Vector(tips[i]), Vector(tips[i + 1])
        if (b0 - a0).length < 1e-4 and (b1 - a1).length < 1e-4:
            continue
        m0, m1 = a0.lerp(b0, 0.7), a1.lerp(b1, 0.7)
        c = col if i % 2 == 0 else shade(col, 0.86)
        e = edge
        j = i if lead_front else n - 2 - i  # which ray from the leading edge
        if lead and j < len(lead) and lead[j]:
            c = e = lead[j]
        if tip_low and i >= n - 4:
            e = tip_low
        k.poly([a0, a1, m1, m0], c)
        k.poly([m0, m1, b1, b0], e)
    if spots:
        scol, count, size = spots
        for _ in range(count):
            i = k.rnd.randrange(n - 1)
            f = k.rnd.uniform(0.2, 0.85)
            p = Vector(base[i]).lerp(Vector(tips[i]), f).lerp(Vector(base[i + 1]).lerp(Vector(tips[i + 1]), f), 0.5)
            k.ico(p, size, scol, subdiv=1, rough=0, jitter=0)

def fish(i):
    name = FISH_IDS[i]
    k = Kit(800 + i)
    if name == 'boot':
        leather, sole = hexc('#5b3a1e'), hexc('#2b2118')
        k.box((0, 0.05, -0.08), (0.22, 0.62, 0.14), leather)
        k.box((0, 0.05, -0.16), (0.24, 0.66, 0.05), sole)
        k.cyl((0, -0.16, -0.05), (0, -0.2, 0.42), 0.12, 0.13, leather, sides=8)
        k.ico((0, 0.32, -0.05), (0.11, 0.1, 0.09), leather, subdiv=1, rough=0.05)
        for z in (0.0, 0.12, 0.24):
            k.box((0, -0.04, z), (0.2, 0.02, 0.02), hexc('#c9a227'))
        k.ico((0.05, -0.2, 0.43), (0.06, 0.05, 0.02), hexc('#4c7a33'), subdiv=1, rough=0.3)  # pondweed
        return k.build('fish_boot', ao=0.15)
    if name == 'treasure':
        wood, band, gold = hexc('#7a4a22'), hexc('#c9a227'), hexc('#ffd166')
        k.box((0, 0, 0), (0.6, 0.4, 0.3), wood)
        for x in (-0.22, 0.22):
            k.box((x, 0, 0), (0.05, 0.42, 0.32), band)
        k.box((0, -0.03, 0.22), (0.6, 0.4, 0.06), wood)  # the lid, propped open a little
        k.box((0, 0.2, 0.06), (0.08, 0.03, 0.1), band)
        for j in range(7):  # coins spilling over the rim
            x0, y0 = k.rnd.uniform(-0.22, 0.22), k.rnd.uniform(-0.12, 0.12)
            k.cyl((x0, y0, 0.15), (x0, y0, 0.165), 0.04, 0.04, gold, sides=8)
        return k.build('fish_treasure', ao=0.15)

    sp = {key: (hexc(v) if isinstance(v, str) and v.startswith('#') else v) for key, v in FISH[name].items()}

    def col_of(v):
        if isinstance(v, str) and v.startswith('#'):
            return hexc(v)
        if isinstance(v, (list, tuple)):
            return type(v)(col_of(x) for x in v)
        if isinstance(v, dict):
            return {a: col_of(b) for a, b in v.items()}
        return v

    for key in ('pat', 'spots', 'finspots', 'lead', 'edges', 'ear', 'moss', 'frill', 'kelp', 'barnacles'):
        if key in sp:
            sp[key] = col_of(sp[key])
    B = FishBody(sp)
    H, W = sp['H'], sp['W']
    back, side, belly, fin = sp['back'], sp['side'], sp['belly'], sp['fin']
    rnd = k.rnd
    off = Vector((rnd.random() * 50, rnd.random() * 50, rnd.random() * 50))
    tail_shape, C, tail_h = sp['tail']
    L = B.yn - B.yp
    scales = sp.get('scales', 0.035)

    def fin_col(which):
        return sp.get(which + 'c', fin)

    def fin_edge(which):
        return sp.get('edges', {}).get(which) or sp.get('edge')

    def skin(c, n):
        t = B.t_of(c.y)
        if t > 1.0:  # the snout, bill or beak
            return sp.get('snoutc', shade(back, 0.9))
        if sp.get('flat') and c.x < -0.002:  # a flatfish's blind side is white
            return belly
        y, rx, rz, zo = B.at(t)
        v = max(-1.0, min(1.0, (c.z - zo) / max(rz, 1e-4)))
        w = v + 0.07 * noise.noise(c * 25 + off)
        if w > 0.55:
            base = back
        elif w > 0.28:
            base = mix(back, side, 0.5)
        elif w > -0.38:
            base = side
        elif w > -0.6:
            base = mix(side, belly, 0.5)
        else:
            base = belly
        for p in sp.get('pat', []):
            base = fish_pattern(p, base, t, v, c, off)
        if scales > 0 and 0.03 < t < 0.8:
            a = math.atan2(c.z - zo, abs(c.x) + 1e-6)
            if (int(t * L / 0.028) + int((a + 2) / 0.24)) % 2:
                base = shade(base, 1 - scales)
        return base

    with k.limb(1, axis_t(B.yn, B.yp - C)):
        # The body: elliptical sections, closer together at the head and tail, then the snout if it has one.
        N = 30
        sections = []
        for j in range(N):
            t = 0.5 - 0.5 * math.cos(math.pi * j / (N - 1))
            y, rx, rz, zo = B.at(t)
            sections.append((y, rx, rz, zo))
        y1, _, _, z1 = B.at(1.0)
        for dy, rx, rz, dz in sp.get('snout', []):
            sections.append((y1 + dy, rx, rz, z1 + dz))
        k.loft(sections, skin, sides=20)

        # The tail fin.
        y0, rx0, rz0, zo0 = B.at(0.0)
        Ht = H * tail_h * 0.75
        n = 14
        outline = resample([Vector((0, y0 - b * C, zo0 + u * Ht)) for b, u in FISH_TAILS[tail_shape]], n)
        base = [Vector((0, y0 + 0.012, zo0 + rz0 * 0.85 * (1 - 2 * j / (n - 1)))) for j in range(n)]
        fs = sp.get('finspots')
        fish_fan(k, base, outline, fin_col('caudal'), fin_edge('caudal'),
                 spots=(fs[0], fs[2], fs[3]) if fs and 'caudal' in fs[1] else None, tip_low=sp.get('tailtip'))

        # Fins along the back and belly.
        def mid_fin(which, t0, t1, height, shape, sweep, top):
            m = max(5, int((t1 - t0) * 50) + 2)
            base, tips = [], []
            for j in range(m + 1):
                u = j / m
                t = t0 + (t1 - t0) * u
                y, rx, rz, zo = B.at(t)
                z = zo + rz - 0.003 if top else zo - rz + 0.003
                h = max(0.004, height * H * FIN_SHAPES[shape](u, j))
                base.append(Vector((0, y, z)))
                tips.append(Vector((0, y - h * sweep, z + (h if top else -h))))
            fish_fan(k, base, tips, fin_col(which), fin_edge(which),
                     spots=(fs[0], fs[2], fs[3]) if fs and which in fs[1] else None,
                     lead=sp.get('lead') if which == 'anal' else None, lead_front=False)

        for f in sp.get('dorsal', []):
            mid_fin('dorsal', *f, top=True)
        for f in sp.get('anal', []):
            mid_fin('anal', *f, top=False)
        if sp.get('adipose'):
            mid_fin('adipose', sp['adipose'][0], sp['adipose'][1], 0.35, 'round', 0.5, True)

        # Paired fins: pectorals behind the gills, pelvics under the belly.
        def paired(which, spec, below):
            if not spec:
                return
            t, length = spec[0], spec[1]
            shape = spec[2] if len(spec) > 2 else 'plain'
            for s in (-1, 1):
                if below:
                    b1, b2 = B.surf(t + 0.012, -0.88, s, -0.003)[0], B.surf(t - 0.012, -0.93, s, -0.003)[0]
                    d = Vector((s * 0.35, -1, -0.75)).normalized()
                else:
                    b1, b2 = B.surf(t, -0.15, s, -0.002)[0], B.surf(t - 0.02, -0.45, s, -0.002)[0]
                    d = Vector((s * 0.55, -1, -0.35)).normalized()
                up = Vector((0, 0, 1))
                p = (up - d * up.dot(d)).normalized()
                mid = b1.lerp(b2, 0.5)
                Lf = length
                if shape in ('pointed', 'long'):
                    c0, c1, c2 = b1 + d * Lf * 1.0 + p * Lf * 0.12, mid + d * Lf * 0.75 - p * Lf * 0.1, b2 + d * Lf * 0.35 - p * Lf * 0.1
                elif shape == 'round':
                    c0, c1, c2 = b1 + d * Lf * 0.75 + p * Lf * 0.3, mid + d * Lf * 1.05, b2 + d * Lf * 0.6 - p * Lf * 0.3
                else:
                    c0, c1, c2 = b1 + d * Lf * 0.9 + p * Lf * 0.2, mid + d * Lf * 0.95, b2 + d * Lf * 0.55 - p * Lf * 0.2
                m = 7
                tips = bezier(c0, c1, c2, m - 1)
                base = [b1.lerp(b2, j / (m - 1)) for j in range(m)]
                fish_fan(k, base, tips, fin_col(which), fin_edge(which),
                         spots=(fs[0], max(1, fs[2] // 3), fs[3]) if fs and which in fs[1] else None, lead=sp.get('lead'))

        paired('pect', sp.get('pect'), False)
        paired('pelv', sp.get('pelv'), True)

        # Fleshy lobed fins (the coelacanth's).
        if sp.get('lobes'):
            for t, v, top in ((0.76, -0.3, False), (0.48, -0.92, False), (0.3, 1.0, True), (0.28, -1.0, False)):
                for s in ((1,) if top or v == -1.0 else (-1, 1)):
                    p, nrm = B.surf(t, v, s, -0.004)
                    d = (nrm * 0.8 + Vector((0, -1, 0))).normalized()
                    end = p + d * 0.06
                    k.tube([p, end], 0.014, 0.009, mix(back, side, 0.5), sides=6)
                    perp = d.cross(Vector((0, 0, 1)))
                    perp = perp.normalized() if perp.length > 0.1 else Vector((1, 0, 0))
                    base = [end + perp * 0.01 * (1 - 2 * j / 6) for j in range(7)]
                    tips = bezier(end + d * 0.05 + perp * 0.045, end + d * 0.1, end + d * 0.05 - perp * 0.045, 6)
                    fish_fan(k, base, tips, fin, sp.get('edge'))

        # Eyes (both on one side for a flatfish), gill covers and the mouth.
        et, ev, er = sp['eye']
        iris = sp.get('iris', hexc('#d8b860'))
        eyes = [(et, ev, 1), (et - 0.045, 0.08, 1)] if sp.get('flat') else [(et, ev, -1), (et, ev, 1)]
        for t, v, s in eyes:
            p, nrm = B.surf(t, v, s, 0)
            k.ico(p - nrm * er * 0.35, er, iris, subdiv=2, rough=0, jitter=0.02)
            k.ico(p + nrm * er * 0.38, er * 0.56, hexc('#0a0a0a'), subdiv=1, rough=0, jitter=0)
            k.ico(p + nrm * er * 0.66 + Vector((0, er * 0.15, er * 0.2)), er * 0.16, hexc('#ffffff'), subdiv=1, rough=0, jitter=0)
        tg = sp['gill']
        gill_col = shade(mix(side, back, 0.3), 0.6)
        for s in ((1,) if sp.get('flat') else (-1, 1)):
            pts = []
            for j in range(9):
                v = -0.75 + 1.4 * j / 8
                pts.append(B.surf(tg - 0.035 * (1 - v * v), v, s, 0.0015)[0])
            k.tube(pts, 0.0028, 0.0028, gill_col, sides=3, cap=False)

        size, kind = sp['mouth']
        mouth_col = sp.get('mouthc', hexc('#3a1a1a'))
        yN, rxN, rzN, zoN = B.at(1.0)
        tgape = 1 - 0.04 - 0.14 * size

        def vm(t):
            u = (1 - t) / max(1e-4, 1 - tgape)
            if kind == 'up':
                return 0.25 - 0.55 * u
            if kind == 'wide':
                return -0.3
            return -0.12 - 0.12 * u

        if kind == 'sub':
            p = B.surf(0.95, -0.95, 1, 0)[0]
            p.x = 0
            k.ico(p, (0.014 + 0.02 * size, 0.012, 0.005), mouth_col, subdiv=1, rough=0)
            if sp.get('lips'):
                lip = shade(belly, 0.85)
                for sx in (-1, 1):
                    k.ico(p + Vector((sx * 0.012, -0.002, -0.002)), (0.009, 0.013, 0.006), lip, subdiv=1, rough=0)
                k.ico(p + Vector((0, 0.008, -0.001)), (0.012, 0.006, 0.005), lip, subdiv=1, rough=0)
        elif kind != 'none':
            for s in (-1, 1):
                pts = [B.surf(1 - (1 - tgape) * j / 5, vm(1 - (1 - tgape) * j / 5), s, 0.001)[0] for j in range(6)]
                k.tube(pts, 0.0035 + 0.002 * size, 0.0025, mouth_col, sides=4)
            k.ico((0, yN + 0.002, zoN + rzN * vm(1.0)), (max(rxN, 0.01) * 1.05, 0.008, 0.006 + 0.012 * size), mouth_col, subdiv=1, rough=0)
        if sp.get('jut'):  # a lower jaw jutting past the upper (flathead catfish)
            k.ico((0, yN + 0.004, zoN - rzN * 0.6), (rxN * 1.1 + 0.01, 0.016, 0.008), shade(belly, 0.9), subdiv=1, rough=0)

        # Teeth.
        white = hexc('#f4f2ea')
        teeth = sp.get('teeth')
        if teeth == 'beak':
            ext = sp['snout'][-1][0]
            for j in range(12):
                y = yN + ext * (j + 0.5) / 12
                w = 0.02 - 0.013 * j / 12
                for s in (-1, 1):
                    k.cyl((s * w * 0.85, y, zoN - 0.003), (s * w * 0.85, y, zoN - 0.011), 0.0025, 0.0, white, sides=3)
        elif teeth in ('canine', 'fang'):
            count, long = (2, 0.012) if teeth == 'canine' else (6, 0.026)
            for j in range(count):
                t = 0.995 - (1 - tgape) * 0.8 * j / max(1, count - 1)
                for s in (-1, 1):
                    up = B.surf(t, vm(t) + 0.12, s, 0.001)[0]
                    k.cyl(up, up + Vector((0, 0.003, -long)), 0.003 + long * 0.12, 0.0, white, sides=4)
                    if teeth == 'fang':
                        lo = B.surf(t, vm(t) - 0.12, s, 0.001)[0]
                        k.cyl(lo, lo + Vector((0, 0.003, long * 0.8)), 0.003 + long * 0.1, 0.0, white, sides=4)

        # Barbels.
        bcol = sp.get('barbelc', shade(back, 0.75))
        bk = sp.get('barbels')
        if bk == 'carp':
            for s in (-1, 1):
                a = B.surf(0.985, -0.3, s, 0)[0]
                k.tube([a, a + Vector((s * 0.01, -0.01, -0.02)), a + Vector((s * 0.016, -0.03, -0.035))], 0.004, 0.0015, bcol, sides=4)
                b = B.surf(0.96, -0.45, s, 0)[0]
                k.tube([b, b + Vector((s * 0.01, -0.006, -0.012)), b + Vector((s * 0.014, -0.016, -0.02))], 0.003, 0.0012, bcol, sides=4)
        elif bk == 'whisker':
            Lw = 0.2 * sp.get('whisk', 1.0)
            for s in (-1, 1):
                a = B.surf(0.975, -0.1, s, 0)[0]
                k.tube(curve([a, a + Vector((s * Lw * 0.4, -Lw * 0.2, 0.01)), a + Vector((s * Lw * 0.75, -Lw * 0.55, -0.02)),
                              a + Vector((s * Lw * 0.9, -Lw * 0.95, -0.05))], 3), 0.005, 0.0015, bcol, sides=4)
                nb = B.surf(0.965, 0.55, s, 0)[0]
                k.tube([nb, nb + Vector((s * 0.008, -0.01, 0.025))], 0.003, 0.001, bcol, sides=3)
                for j, x in enumerate((0.25, 0.6)):
                    c = B.surf(0.95 - 0.02 * j, -0.9, s, 0)[0]
                    c.x = s * x * B.at(0.95)[1]
                    k.tube(curve([c, c + Vector((s * 0.01, -0.02, -0.035)), c + Vector((s * 0.015, -0.05, -0.06))], 3), 0.0035, 0.0012, bcol, sides=4)
        elif bk == 'chin':
            c = B.surf(0.95, -0.95, 1, 0)[0]
            c.x = 0
            k.tube([c, c + Vector((0, -0.008, -0.025)), c + Vector((0, -0.018, -0.04))], 0.003, 0.0012, bcol, sides=4)
        elif bk == 'sturgeon':
            for x in (-0.012, -0.004, 0.004, 0.012):
                p = Vector((x, yN + 0.025, zoN - 0.014))
                k.tube([p, p + Vector((0, -0.004, -0.016)), p + Vector((0, -0.01, -0.03))], 0.0025, 0.0012, bcol, sides=3)
        if sp.get('kelp'):  # Old Kelpbeard's beard of kelp
            kc, count = sp['kelp']
            for j in range(count):
                s = rnd.choice((-1, 1))
                a = B.surf(rnd.uniform(0.88, 0.97), rnd.uniform(-1, -0.7), s, 0)[0]
                Lk = rnd.uniform(0.1, 0.24)
                pts = [a + Vector((s * 0.01 * math.sin(q * 3), -Lk * 0.3 * q, -Lk * q)) for q in [j2 / 5 for j2 in range(6)]]
                k.tube(pts, 0.006, 0.002, shade(kc, rnd.uniform(0.8, 1.15)), sides=4)

        # Spots on the skin (raised, some with a pale or blue halo), an ear flap, and the rest.
        def spot(t, v, s, r, col, halo):
            p, nrm = B.surf(t, v, s, 0)
            if halo:
                k.ico(p - nrm * r * 1.15, r * 1.7, halo, subdiv=1, rough=0, jitter=0.02)
                k.ico(p - nrm * r * 0.3, r, col, subdiv=1, rough=0, jitter=0.03)
            else:
                k.ico(p - nrm * r * 0.55, r, col, subdiv=1, rough=0, jitter=0.03)

        for spec in sp.get('spots', []):
            if spec[0] == 'one':
                _, col, t, v, r, halo = spec
                for s in (-1, 1):
                    spot(t, v, s, r, col, halo)
            else:
                _, col, count, r, t0, t1, vmin, vmax, halo = spec
                for s in ((1,) if sp.get('flat') else (-1, 1)):
                    for _ in range(count):
                        spot(rnd.uniform(t0, t1), rnd.uniform(vmin, vmax), s, r * rnd.uniform(0.8, 1.2), col, halo)
        if sp.get('ear'):
            black, rim, red = sp['ear']
            for s in (-1, 1):
                p, nrm = B.surf(tg - 0.012, 0.28, s, 0)
                p += Vector((0, -0.012, 0))
                k.ico(p - nrm * 0.004, (0.012, 0.027, 0.02), rim, subdiv=1, rough=0)
                k.ico(p + nrm * 0.001, (0.01, 0.022, 0.016), black, subdiv=1, rough=0)
                if red:
                    k.ico(p + nrm * 0.003 + Vector((0, -0.012, 0)), 0.006, red, subdiv=1, rough=0)
        if sp.get('scutes'):
            sc = sp.get('scutec', mix(back, hexc('#ffffff'), 0.3))
            t = 0.2
            while t < 0.86:
                for v, s in ((1.0, 1), (0.05, 1), (0.05, -1), (-0.72, 1), (-0.72, -1)):
                    p, nrm = B.surf(t, v, s, 0)
                    k.cyl(p - nrm * 0.003, p + nrm * 0.013, 0.009, 0.0, sc, sides=4)
                t += 0.055
        if sp.get('finlets'):
            fc = sp.get('finletc', fin)
            n = sp['finlets']
            for j in range(n):
                t = 0.05 + 0.25 * j / max(1, n - 1)
                y, rx, rz, zo = B.at(t)
                for s in (1, -1):
                    z = zo + s * rz
                    h = rz * 0.6 + 0.008
                    k.poly([(0, y + 0.01, z - s * 0.002), (0, y - 0.012, z + s * h), (0, y - 0.012, z - s * 0.002)], fc)
        if sp.get('keel'):
            for s in (-1, 1):
                y_a, rx_a, _, zo_a = B.at(0.12)
                y_b, rx_b, _, zo_b = B.at(0.0)
                k.poly([(s * rx_a, y_a, zo_a), (s * (rx_b + 0.025), (y_a + y_b) / 2, (zo_a + zo_b) / 2), (s * rx_b, y_b, zo_b)], shade(back, 0.8))
        if sp.get('paddle'):  # the paddlefish's long, flat rostrum
            Lp = sp['paddle']
            half = [(0.016, 0.0), (0.03, 0.35), (0.045, 0.75), (0.04, 0.93), (0.018, 1.0)]
            outline = [(x, yN + Lp * f, zoN) for x, f in half] + [(-x, yN + Lp * f, zoN) for x, f in reversed(half)]
            k.fin(outline, 0.008, shade(back, 0.95))
        if sp.get('bill') == 'sword':
            outline = [(0.022, yN - 0.01, zoN), (0.014, yN + 0.2, zoN), (0.0, yN + 0.32, zoN), (-0.014, yN + 0.2, zoN), (-0.022, yN - 0.01, zoN)]
            k.fin(outline, 0.01, shade(back, 0.85))
        elif sp.get('bill') == 'round':
            k.cyl((0, yN - 0.01, zoN), (0, yN + 0.26, zoN + 0.004), 0.013, 0.001, shade(back, 0.85), sides=6)
        if sp.get('beak'):  # a parrotfish's fused teeth
            k.ico((0, yN + 0.004, zoN), (rxN * 1.6 + 0.01, 0.018, rzN * 1.4 + 0.008), sp['beak'], subdiv=1, rough=0)
        if sp.get('kype'):  # the hooked lower jaw of a big male salmon or trout
            k.cyl((0, yN + 0.004, zoN - rzN - 0.004), (0, yN + 0.014, zoN + rzN * 0.6), 0.009, 0.002, shade(back, 0.8), sides=5)
        if sp.get('hook'):  # The Dockmaster: an old hook still in its lip, with a length of broken line
            p = B.surf(1 - (1 - tgape), vm(tgape), 1, 0.002)[0]
            steel = hexc('#c0c7cf')
            arc = [p + Vector((0.004, 0.0, 0.012)), p + Vector((0.012, 0.006, 0.004)), p + Vector((0.01, 0.012, -0.006)), p + Vector((0.002, 0.014, -0.008))]
            k.tube(arc, 0.0018, 0.0018, steel, sides=4)
            k.tube([arc[0], arc[0] + Vector((0.02, 0.05, 0.03)), arc[0] + Vector((0.03, 0.11, 0.02))], 0.0008, 0.0008, hexc('#e8e8e0'), sides=3)
            k.ico(arc[-1] + Vector((0.004, 0.006, 0)), (0.006, 0.012, 0.005), hexc('#d62828'), subdiv=1, rough=0)
        if sp.get('lure'):  # an anglerfish's glowing lure, dangling over its mouth
            base = B.surf(0.78, 1.0, 1, 0)[0]
            base.x = 0
            tip = Vector((0, yN + 0.08, zoN + H * 1.1))
            k.tube(curve([base, base + Vector((0, 0.02, H * 0.9)), tip + Vector((0, -0.02, 0.05)), tip], 4), 0.004, 0.002, shade(back, 1.4), sides=4)
            k.ico(tip, 0.016, sp.get('lurec', hexc('#80f0ff')), subdiv=2, rough=0)
        if sp.get('frill'):  # a crest of spines along the back
            fc, t0, t1 = sp['frill']
            t = t0
            while t <= t1:
                y, rx, rz, zo = B.at(t)
                h = H * (0.5 + 0.4 * math.sin(math.pi * (t - t0) / max(0.01, t1 - t0)))
                k.cyl((0, y, zo + rz - 0.004), (0, y - h * 0.4, zo + rz + h), 0.008, 0.0, fc, sides=4)
                t += 0.04
        if sp.get('crest'):  # the oarfish's red crest
            for j in range(6):
                y, rx, rz, zo = B.at(0.97 - j * 0.012)
                h = 0.12 + 0.05 * (j % 3)
                k.tube([(0, y, zo + rz), (0, y - h * 0.3, zo + rz + h * 0.6), (0, y - h * 0.8, zo + rz + h)], 0.004, 0.002, fin, sides=4)
        if sp.get('streamers'):  # and its long pelvic rays, with paddles on the ends
            for s in (-1, 1):
                a = B.surf(0.88, -0.95, s, 0)[0]
                pts = [a + Vector((s * 0.01 * q, -0.3 * q, -0.08 * q - 0.04 * q * q)) for q in [j / 5 for j in range(6)]]
                k.tube(pts, 0.003, 0.002, fin, sides=4)
                k.ico(pts[-1], (0.004, 0.02, 0.01), fin, subdiv=1, rough=0)
        for key in ('moss', 'barnacles'):
            if sp.get(key):
                mc, count = sp[key]
                for j in range(count):
                    p, nrm = B.surf(rnd.uniform(0.2, 0.85), rnd.uniform(0.2, 1.0), rnd.choice((-1, 1)), 0)
                    r = rnd.uniform(0.008, 0.016)
                    if key == 'moss':
                        k.ico(p, (r * 1.3, r * 1.6, r), shade(mc, rnd.uniform(0.85, 1.2)), subdiv=1, rough=0.35)
                    else:
                        k.cyl(p - nrm * 0.004, p + nrm * r, r, r * 0.45, mc, sides=6)
                        k.ico(p + nrm * r, r * 0.35, hexc('#3a3a36'), subdiv=1, rough=0)
    return k.build(f'fish_{name}', ao=0.12)

# ---- pets ---------------------------------------------------------------------------------------------------
# The 30 pets, each modelled on the animal (the legends on the creature they're named for): a proper body,
# head and face (big glossy eyes with an iris and a glint, nose, mouth, whiskers), ears, legs and paws or
# hooves, tails, wings, fins and shells, in the pet's colours with its own markings. Each faces +Y, standing
# on z = 0, about half a metre tall (s = 0.25 x size). Limbs (the game animates them, Player/PetCompanion.cs):
#   1 front left leg, 2 front right, 3 back left (a bird's left leg), 4 back right, 5 left wing / flipper /
#   fin / claw, 6 right, 7 the tail (or a serpent's body), 8 the head and neck, 9-16 tentacles or crab legs.

PET_IDS = ['lilyfrog', 'duckling', 'sandcrab', 'snail', 'fieldmouse', 'otterpup', 'flamingo', 'hermit', 'turtle', 'kingfisher',
           'raccoon', 'owl', 'beaver', 'penguin', 'seal', 'snowfox', 'eagle', 'grizzly', 'octopus', 'parrot', 'dragon', 'peacock',
           'kraken', 'unicorn', 'spiritkoi', 'phoenix', 'wyrmling', 'qilin', 'moonmoth', 'starwhale']
PET_SIZE = {'duckling': 0.7, 'fieldmouse': 0.6, 'otterpup': 0.9, 'flamingo': 1.1, 'kingfisher': 0.8, 'eagle': 1.2, 'grizzly': 1.3,
            'dragon': 1.1, 'peacock': 1.1, 'unicorn': 1.2, 'spiritkoi': 1.1, 'phoenix': 1.1, 'wyrmling': 1.1, 'qilin': 1.2, 'starwhale': 1.4}

WHITE, BLACK, GLINT = hexc('#fbfbf6'), hexc('#121212'), hexc('#ffffff')

def pet_eye(k, p, n, r, iris, lid=None):
    """A big glossy eye on a surface point p facing n: the white, an iris, the pupil and a glint (and a lid)."""
    n = Vector(n).normalized()
    up = Vector((0, 0, 1))
    side = n.cross(up).normalized() if abs(n.z) < 0.95 else Vector((1, 0, 0))
    k.ico(p - n * r * 0.35, r, WHITE, subdiv=2, rough=0, jitter=0)
    k.ico(p + n * r * 0.32, r * 0.74, iris, subdiv=2, rough=0, jitter=0)
    k.ico(p + n * r * 0.6, r * 0.45, BLACK, subdiv=2, rough=0, jitter=0)
    k.ico(p + n * r * 0.88 + up * r * 0.3 + side * r * 0.2, r * 0.17, GLINT, subdiv=1, rough=0, jitter=0)
    if lid:
        k.ico(p - n * r * 0.25 + up * r * 0.35, (r * 1.08, r * 1.08, r * 0.7), lid, subdiv=1, rough=0)

def limb_leg(k, limb, top, knee, foot, r, col):
    with k.limb(limb, along([top, knee, foot])):
        k.tube_r([top, knee, foot], [r[0], r[1], r[2]], col, sides=8)

# -- four-legged animals ---------------------------------------------------------------------------------------

QUADS = {
    'fieldmouse': dict(L=0.95, W=0.42, H=0.4, leg=0.22, R=0.48, snout=1.05, ears='round', tail='thin', body='#9e9e9e', belly='#efe9e1',
                       accent='#ffafcc', nose='#ff8fab', iris='#2b1a10', whiskers=True),
    'otterpup': dict(L=1.65, W=0.42, H=0.38, leg=0.26, R=0.44, snout=0.75, ears='tiny', tail='otter', body='#7b5b3a', belly='#c8a27c',
                     accent='#5a3e24', nose='#2b1a10', iris='#2b1a10', whiskers=True, muzzle='#d8b892'),
    'raccoon': dict(L=1.2, W=0.55, H=0.5, leg=0.42, R=0.5, snout=0.85, ears='pointed', tail='ringed', body='#8d8d8d', belly='#bdbdbd',
                    accent='#2b2b2b', extra='#e9e9e9', nose='#1b1b1b', iris='#3b2a1a', whiskers=True, mask=True, muzzle='#e9e9e9'),
    'beaver': dict(L=1.25, W=0.66, H=0.6, leg=0.28, R=0.52, snout=0.75, ears='tiny', tail='flat', body='#6f4518', belly='#8b5a2b',
                   accent='#3b2410', nose='#1b1b1b', iris='#1b1b1b', whiskers=True, teeth=True, muzzle='#9a6a3a'),
    'snowfox': dict(L=1.25, W=0.45, H=0.45, leg=0.55, R=0.48, snout=1.15, ears='pointed', tail='bushy', body='#f1f1f1', belly='#ffffff',
                    accent='#cfd8dc', nose='#1b1b1b', iris='#c79a3a', whiskers=True, ruff=True),
    'grizzly': dict(L=1.45, W=0.75, H=0.68, leg=0.6, R=0.55, snout=0.95, ears='round', tail='stub', body='#6d4c41', belly='#5d4037',
                    accent='#4e342e', nose='#1b1b1b', iris='#2b1a10', muzzle='#a1887f', hump=True, claws=True),
    'unicorn': dict(L=1.35, W=0.5, H=0.52, leg=1.05, R=0.42, snout=1.5, ears='long', tail='flowing', body='#f8f9fa', belly='#eef0f2',
                    accent='#ff8fab', extra='#ffd166', nose='#d8c8d0', iris='#7b5cc7', neck=0.75, mane='#ff8fab', horn=True, hooves='#ffd166'),
    'qilin': dict(L=1.35, W=0.5, H=0.52, leg=1.0, R=0.42, snout=1.35, ears='long', tail='flowing', body='#ffd166', belly='#ffe8a8',
                  accent='#e76f51', extra='#2a9d8f', nose='#5a3e24', iris='#2a9d8f', neck=0.7, mane='#2a9d8f', antlers=True, hooves='#2a9d8f',
                  scales=True, spines=True),
    'dragon': dict(L=1.35, W=0.55, H=0.52, leg=0.5, R=0.48, snout=1.2, ears='horns', tail='spade', body='#2a9d8f', belly='#e9c46a',
                   accent='#e9c46a', extra='#e76f51', nose='#1b4a44', iris='#ffd166', wings=True, spines=True, scales=True, claws=True, muzzle='#2f8f83'),
}

def quad_pet(k, pid, q, s):
    c = {key: hexc(v) for key, v in q.items() if isinstance(v, str) and v.startswith('#')}
    body, belly, accent = c['body'], c['belly'], c['accent']
    extra = c.get('extra', accent)
    L, W, H, leg, R = q['L'] * s, q['W'] * s, q['H'] * s, q['leg'] * s, q['R'] * s
    z0 = leg + H * 0.85
    off = Vector((k.rnd.random() * 9, k.rnd.random() * 9, k.rnd.random() * 9))

    def coat(p, n):
        if n.z < -0.45:
            return belly
        if q.get('scales') and noise.noise(p * (9 / s) + off) > 0.25:
            return accent if pid == 'qilin' else shade(body, 0.85)
        return body if n.z < 0.75 else shade(body, 0.94)

    hump = H * 0.25 if q.get('hump') else 0
    k.loft([(-L * 0.5, W * 0.5, H * 0.6, z0 + H * 0.08), (-L * 0.32, W * 0.95, H * 0.95, z0), (0, W, H, z0),
            (L * 0.28, W * 0.96, H * 0.98 + hump, z0 + 0.06 * H + hump * 0.5), (L * 0.5, W * 0.62, H * 0.72, z0 + 0.15 * H)], coat, sides=14)
    if q.get('ruff'):  # a fluffy chest
        k.blob([((0, L * 0.45, z0 - H * 0.1), (W * 0.8, W * 0.6, H * 0.8), WHITE, 1, 0.12, 2.0)], 0.03 * s * 4, 0.35, lambda f: hexc('#ffffff'))
    # Neck and head (limb 8).
    neck_len = q.get('neck', 0.25) * s
    nb = Vector((0, L * 0.4, z0 + H * 0.35))
    hc = nb + Vector((0, R * 0.8 + neck_len * 0.55, R * 0.7 + neck_len * 0.9))
    snout = q['snout']
    mc = hc + Vector((0, R * (0.55 + 0.45 * snout), -R * 0.3))
    tip = mc + Vector((0, R * 0.42 * snout, R * 0.05))
    with k.limb(8, along([nb, hc, tip])):
        k.tube_r([nb, nb.lerp(hc, 0.55), hc - Vector((0, R * 0.3, R * 0.25))], [W * 0.62, R * 0.62, R * 0.6], body, sides=10)
        muzzle = c.get('muzzle', belly)

        def face(f):
            p, n = f.calc_center_median(), f.normal
            if q.get('mask') and abs(p.z - (hc.z + R * 0.12)) < R * 0.22 and p.y > hc.y and abs(p.x) > R * 0.12:
                return accent
            if p.y > hc.y + R * 0.45 or n.z < -0.55:
                return muzzle
            return body

        k.blob([(hc, (R * 0.92, R, R * 0.9), body, 2, 0.02, 1.0), (mc, (R * 0.48, R * 0.55 * max(1, snout * 0.9), R * 0.42), body, 2, 0.02, 1.0)],
               0.06 * s, 0.32, face)
        k.ico(tip, (R * 0.17, R * 0.12, R * 0.12), c['nose'], subdiv=2, rough=0)
        k.box(tip + Vector((0, -R * 0.05, -R * 0.22)), (R * 0.32, R * 0.03, R * 0.03), shade(c['nose'], 0.7))
        for sx in (-1, 1):
            n = Vector((sx * 0.62, 0.72, 0.28)).normalized()
            pet_eye(k, hc + n * R * 0.86 + Vector((0, R * 0.05, R * 0.06)), n, R * 0.24, c['iris'])
            if q.get('whiskers'):
                for j in range(3):
                    a = mc + Vector((sx * R * 0.4, R * 0.25 * snout, -R * 0.05 - j * R * 0.06))
                    k.tube([a, a + Vector((sx * R * 0.7, R * 0.08, 0.05 * R - j * R * 0.1))], R * 0.012, R * 0.006, hexc('#3a3a3a') if pid != 'snowfox' else hexc('#9aa0a6'), sides=3)
            ear = q['ears']
            base = hc + Vector((sx * R * 0.5, -R * 0.15, R * 0.68))
            if ear == 'pointed':
                k.fin([base + Vector((sx * -R * 0.2, 0, 0)), base + Vector((sx * R * 0.2, 0, 0)), base + Vector((sx * R * 0.18, -R * 0.05, R * 0.75))], R * 0.08, body)
                k.fin([base + Vector((sx * -R * 0.1, R * 0.05, R * 0.06)), base + Vector((sx * R * 0.12, R * 0.05, R * 0.06)),
                       base + Vector((sx * R * 0.14, R * 0.0, R * 0.55))], R * 0.04, accent if pid != 'raccoon' else extra)
            elif ear == 'round':
                k.ico(base + Vector((sx * R * 0.12, 0, R * 0.08)), (R * 0.36, R * 0.12, R * 0.36), body, subdiv=2, rough=0)
                k.ico(base + Vector((sx * R * 0.12, R * 0.07, R * 0.08)), (R * 0.24, R * 0.06, R * 0.24), accent, subdiv=1, rough=0)
            elif ear == 'long':
                k.fin([base, base + Vector((sx * R * 0.25, 0, 0)), base + Vector((sx * R * 0.2, -R * 0.1, R * 0.7))], R * 0.07, body)
            elif ear == 'tiny':
                k.ico(base + Vector((sx * R * 0.12, 0, -R * 0.08)), (R * 0.16, R * 0.08, R * 0.14), shade(body, 0.85), subdiv=1, rough=0)
            elif ear == 'horns':
                k.tube(curve([base, base + Vector((sx * R * 0.15, -R * 0.35, R * 0.45)), base + Vector((sx * R * 0.1, -R * 0.85, R * 0.6))], 3), R * 0.11, R * 0.015, extra, sides=6)
        if q.get('teeth'):
            for sx in (-1, 1):
                k.box(tip + Vector((sx * R * 0.06, -R * 0.06, -R * 0.33)), (R * 0.1, R * 0.05, R * 0.2), hexc('#f4a261'))
        if q.get('horn'):  # a spiral horn
            hb = hc + Vector((0, R * 0.35, R * 0.75))
            pts = [hb + Vector((0, 0.18 * R * t, 1.5 * R * t)) for t in [j / 8 for j in range(9)]]
            k.tube(pts, R * 0.16, R * 0.01, extra, sides=6)
            for j in range(6):
                t = (j + 0.5) / 7
                k.tube_r([pts[0].lerp(pts[-1], t) - Vector((0, 0, R * 0.03)), pts[0].lerp(pts[-1], t) + Vector((0, 0, R * 0.03))],
                         [R * 0.17 * (1 - t) + R * 0.02, R * 0.17 * (1 - t) + R * 0.02], shade(extra, 0.85), sides=6, cap=False)
        if q.get('antlers'):
            for sx in (-1, 1):
                b0 = hc + Vector((sx * R * 0.35, -R * 0.05, R * 0.75))
                pts = curve([b0, b0 + Vector((sx * R * 0.25, -R * 0.2, R * 0.6)), b0 + Vector((sx * R * 0.55, -R * 0.55, R * 1.1))], 3)
                k.tube(pts, R * 0.08, R * 0.025, extra, sides=5)
                for j, t in enumerate((0.35, 0.65)):
                    p = Vector(pts[int(t * (len(pts) - 1))])
                    k.tube([p, p + Vector((sx * R * 0.25, R * 0.15, R * 0.3))], R * 0.05, R * 0.015, extra, sides=4)
        if q.get('mane'):
            m = hexc(q['mane'])
            for j in range(7):
                p = nb.lerp(hc - Vector((0, R * 0.4, -R * 0.4)), j / 6) + Vector((0, -R * 0.2, R * 0.62))
                k.fin([p + Vector((0, R * 0.2, -R * 0.1)), p + Vector((0, -R * 0.55, R * 0.5)), p + Vector((0, -R * 0.45, -R * 0.35))], R * 0.14, m if j % 2 else shade(m, 0.85))
    # Legs (1-4), with paws, hooves or claws.
    for limb, sx, y in ((1, -1, 0.3), (2, 1, 0.3), (3, -1, -0.3), (4, 1, -0.3)):
        top = Vector((sx * W * 0.58, y * L, z0 - H * 0.25))
        foot = Vector((sx * W * 0.62, y * L + 0.03 * s, 0.07 * s))
        knee = top.lerp(foot, 0.5) + Vector((0, (0.06 if y > 0 else -0.08) * s, 0))
        r0 = W * (0.42 if q.get('hump') else 0.34)
        with k.limb(limb, along([top, knee, foot])):
            k.tube_r([top, knee, foot], [r0, r0 * 0.65, r0 * 0.5], body if not q.get('hooves') else body, sides=8)
            if q.get('hooves'):
                k.cyl(foot + Vector((0, 0, -0.05 * s)), foot + Vector((0, 0.01 * s, 0.07 * s)), r0 * 0.58, r0 * 0.5, hexc(q['hooves']), sides=8)
            else:
                k.ico(foot + Vector((0, 0.05 * s, -0.02 * s)), (r0 * 0.75, r0 * 0.95, r0 * 0.45), shade(body, 0.9) if pid != 'raccoon' else accent, subdiv=2, rough=0)
                if q.get('claws'):
                    for j in (-1, 0, 1):
                        p = foot + Vector((j * r0 * 0.35, r0 * 0.9, -0.01 * s))
                        k.cyl(p, p + Vector((0, r0 * 0.4, -0.02 * s)), r0 * 0.12, 0.0, hexc('#efe9e1'), sides=4)
    # The tail (7).
    tr = Vector((0, -L * 0.48, z0 + H * 0.25))
    kind = q['tail']
    if kind == 'thin':
        pts = curve([tr, tr + Vector((0, -L * 0.6, -H * 0.2)), tr + Vector((L * 0.25, -L * 1.1, H * 0.2)), tr + Vector((L * 0.4, -L * 1.3, H * 0.6))], 4)
        with k.limb(7, along(pts)):
            k.tube(pts, 0.05 * s, 0.015 * s, accent, sides=5)
    elif kind == 'otter':
        pts = curve([tr, tr + Vector((0, -L * 0.4, -H * 0.3)), tr + Vector((0, -L * 0.75, -H * 0.5))], 4)
        with k.limb(7, along(pts)):
            k.tube(pts, W * 0.45, 0.04 * s, body, sides=8)
    elif kind == 'ringed':
        pts = curve([tr, tr + Vector((0, -L * 0.35, H * 0.15)), tr + Vector((0, -L * 0.75, H * 0.05)), tr + Vector((0, -L * 1.0, -H * 0.15))], 4)
        with k.limb(7, along(pts)):
            faces = k.tube_r(pts, [W * 0.3 + W * 0.12 * math.sin(j / (len(pts) - 1) * math.pi) for j in range(len(pts))], body, sides=8)
            for f in faces:
                t = along(pts)(f.calc_center_median())
                if int(t * 8) % 2 == 1:
                    k.paint([f], accent, 0.03)
    elif kind == 'flat':
        pts = [tr, tr + Vector((0, -L * 0.35, -H * 0.45))]
        with k.limb(7, along(pts + [tr + Vector((0, -L * 0.85, -H * 0.6))])):
            k.tube(pts, W * 0.25, W * 0.2, accent, sides=6)
            paddle = [(x * W * 0.55, -L * 0.35 - (1 - x * x) ** 0.5 * L * 0.0 + y * L * 0.5, 0) for x, y in ((-0.6, 0), (-1, -0.3), (-0.9, -0.8), (0, -1), (0.9, -0.8), (1, -0.3), (0.6, 0))]
            k.fin([tr + Vector((p[0], p[1], -H * 0.48)) for p in paddle], 0.05 * s, accent)
            for j in range(6):
                k.box(tr + Vector((0, -L * 0.42 - j * L * 0.07, -H * 0.45)), (W * 0.9, 0.01 * s, 0.01 * s), shade(accent, 0.7))
    elif kind == 'bushy':
        pts = curve([tr, tr + Vector((0, -L * 0.4, H * 0.1)), tr + Vector((0, -L * 0.85, H * 0.45)), tr + Vector((0, -L * 1.1, H * 0.8))], 4)
        with k.limb(7, along(pts)):
            k.blob([(Vector(p), (W * 0.42 * (0.6 + math.sin(j / (len(pts) - 1) * math.pi * 0.9)), W * 0.5, W * 0.42 * (0.6 + math.sin(j / (len(pts) - 1) * math.pi * 0.9))), body, 1, 0.1, 2.0)
                    for j, p in enumerate(pts) if j % 2 == 0], 0.05 * s, 0.35,
                   lambda f: hexc('#ffffff') if along(pts)(f.calc_center_median()) > 0.8 else body)
    elif kind == 'stub':
        with k.limb(7, lambda co: 0.5):
            k.ico(tr + Vector((0, -0.05 * s, 0)), 0.14 * s, body, subdiv=1, rough=0.1)
    elif kind == 'flowing':
        m = hexc(q.get('mane', q['accent']))
        pts = curve([tr, tr + Vector((0, -L * 0.25, -H * 0.1)), tr + Vector((0, -L * 0.45, -H * 0.8)), tr + Vector((0, -L * 0.55, -H * 1.6))], 4)
        with k.limb(7, along(pts)):
            k.tube(pts[:3], W * 0.18, W * 0.14, body, sides=6)
            for j in range(5):
                a = (j - 2) * 0.25
                p0 = Vector(pts[2])
                k.tube([p0, p0 + Vector((a * L * 0.2, -L * 0.15, -H * 0.6)), p0 + Vector((a * L * 0.35, -L * 0.25, -H * 1.3))], W * 0.12, W * 0.02, m if j % 2 else shade(m, 0.85), sides=5)
    elif kind == 'spade':
        pts = curve([tr, tr + Vector((0, -L * 0.5, -H * 0.2)), tr + Vector((L * 0.2, -L * 1.0, 0)), tr + Vector((L * 0.3, -L * 1.4, H * 0.3))], 4)
        with k.limb(7, along(pts)):
            k.tube(pts, W * 0.4, 0.04 * s, body, sides=8)
            e = Vector(pts[-1])
            k.fin([e + Vector((0, 0.05 * s, 0)), e + Vector((-0.16 * s, -0.08 * s, 0.05 * s)), e + Vector((0, -0.25 * s, 0.1 * s)), e + Vector((0.16 * s, -0.08 * s, 0.05 * s))], 0.03 * s, extra)
    if q.get('spines'):
        for j in range(7):
            y = L * (0.45 - j * 0.15)
            k.cyl((0, y, z0 + H * 0.92), (0, y - 0.05 * s, z0 + H * 0.92 + 0.18 * s), 0.08 * s, 0.0, extra if pid == 'dragon' else hexc(q['mane']), sides=4)
    if q.get('wings'):  # leathery wings (5, 6): bony fingers with membrane between
        for limb, sx in ((5, -1), (6, 1)):
            root = Vector((sx * W * 0.55, L * 0.15, z0 + H * 0.6))
            tip = root + Vector((sx * 1.9 * s, -0.2 * s, 0.9 * s))
            with k.limb(limb, along([root, tip])):
                k.tube([root, root + Vector((sx * 0.8 * s, 0.05 * s, 0.75 * s)), tip], 0.05 * s, 0.02 * s, shade(body, 0.85), sides=5)
                elbow = root + Vector((sx * 0.8 * s, 0.05 * s, 0.75 * s))
                fingers = [tip, root + Vector((sx * 1.6 * s, -0.55 * s, 0.4 * s)), root + Vector((sx * 1.1 * s, -0.8 * s, 0.1 * s))]
                for fgr in fingers[1:]:
                    k.tube([elbow, fgr], 0.025 * s, 0.01 * s, shade(body, 0.85), sides=4)
                pts = [root, elbow, tip, fingers[1], fingers[2], root + Vector((sx * 0.4 * s, -0.75 * s, -0.05 * s))]
                for a, b_ in zip(pts[1:-1], pts[2:]):
                    k.fin([elbow if a is not elbow else root, a, b_], 0.02 * s, extra)
                k.fin([root, elbow, pts[-1]], 0.02 * s, extra)

# -- birds -----------------------------------------------------------------------------------------------------

BIRDS = {
    'duckling': dict(L=0.85, W=0.55, H=0.5, leg=0.28, R=0.46, beak='bill', tail='stub', body='#ffd60a', belly='#ffe680', wing='#f2c200',
                     beakc='#f77f00', legc='#f77f00', iris='#1b1b1b', fluffy=True),
    'flamingo': dict(L=0.9, W=0.42, H=0.42, leg=2.0, R=0.3, beak='bent', tail='short', body='#ff8fab', belly='#ffb3c6', wing='#ff7096',
                     wingtip='#1b1b1b', beakc='#f8e1e7', beaktip='#1b1b1b', legc='#ff7096', iris='#f4d35e', neck=1.6),
    'kingfisher': dict(L=0.85, W=0.42, H=0.42, leg=0.25, R=0.42, beak='dagger', tail='short', body='#1e88e5', belly='#f77f00', wing='#1565c0',
                       beakc='#1b1b1b', legc='#f77f00', iris='#1b1b1b', throat='#ffffff', flies=True, sheen='#4fc3f7'),
    'owl': dict(L=0.75, W=0.6, H=0.75, leg=0.2, R=0.58, beak='hook', tail='short', body='#a47148', belly='#e9d8b8', wing='#8b5e3c',
                beakc='#4a3a2a', legc='#d9c49a', iris='#f4a300', upright=True, disc='#f1e3c8', tufts=True, flies=True, speckle='#5a3e24'),
    'penguin': dict(L=0.7, W=0.55, H=0.85, leg=0.12, R=0.45, beak='cone', tail='stub', body='#1b1b1b', belly='#f4f4f4', wing='#1b1b1b',
                    beakc='#f77f00', legc='#f77f00', iris='#1b1b1b', upright=True, flippers=True, cheek='#ffd166'),
    'eagle': dict(L=1.0, W=0.48, H=0.48, leg=0.35, R=0.42, beak='hook', tail='fan', body='#5c3d2e', belly='#4a2f22', wing='#4a2f22',
                  beakc='#ffb703', legc='#ffb703', iris='#ffb703', head='#f1f1f1', tailc='#f1f1f1', flies=True, brow=True),
    'parrot': dict(L=0.9, W=0.42, H=0.45, leg=0.3, R=0.42, beak='hook', tail='long', body='#e63946', belly='#e63946', wing='#1e88e5',
                   wingband='#ffd166', beakc='#efe9e1', legc='#6c757d', iris='#f4d35e', face='#ffffff', flies=True),
    'peacock': dict(L=0.95, W=0.45, H=0.48, leg=0.6, R=0.36, beak='cone', tail='peacock', body='#1e88e5', belly='#1565c0', wing='#8d6e4a',
                    beakc='#9e9e9e', legc='#9e9e9e', iris='#1b1b1b', crest='#1e88e5', neck=0.6, face='#ffffff'),
    'phoenix': dict(L=0.95, W=0.45, H=0.45, leg=0.32, R=0.4, beak='hook', tail='flame', body='#ff7b00', belly='#ffba08', wing='#d00000',
                    wingtip='#ffba08', beakc='#ffd166', legc='#ffba08', iris='#ffba08', crest='#ffba08', flies=True),
}

def bird_pet(k, pid, q, s):
    c = {key: hexc(v) for key, v in q.items() if isinstance(v, str) and v.startswith('#')}
    body, belly, wingc = c['body'], c['belly'], c['wing']
    L, W, H, leg, R = q['L'] * s, q['W'] * s, q['H'] * s, q['leg'] * s, q['R'] * s
    up = q.get('upright')
    z0 = leg + H * (1.0 if up else 0.75)
    off = Vector((k.rnd.random() * 9, k.rnd.random() * 9, k.rnd.random() * 9))

    def plumage(p, n):
        if n.y > 0.35 and (p.z < z0 + H * 0.4 or up):
            if q.get('speckle') and noise.noise(p * (14 / s) + off) > 0.3:
                return c['speckle']
            return belly
        if q.get('sheen') and n.z > 0.5:
            return c['sheen']
        return body

    if up:
        k.vloft([(leg * 0.6, W * 0.6, W * 0.55, 0.0), (leg + H * 0.3, W, W * 0.92, 0.02 * s), (leg + H * 1.0, W * 0.95, W * 0.88, 0.03 * s),
                 (leg + H * 1.6, W * 0.7, W * 0.66, 0.02 * s), (leg + H * 1.9, W * 0.35, W * 0.35, 0.0)], plumage, sides=16)
    else:
        k.loft([(-L * 0.55, W * 0.35, H * 0.35, z0 + H * 0.15), (-L * 0.25, W * 0.85, H * 0.85, z0), (0.1 * L, W, H, z0),
                (L * 0.4, W * 0.8, H * 0.82, z0 + H * 0.2), (L * 0.55, W * 0.45, H * 0.5, z0 + H * 0.45)], plumage, sides=14)
    if q.get('fluffy'):
        k.blob([((0, 0, z0), (W * 1.08, L * 0.55, H * 1.05), body, 1, 0.2, 3.0)], 0.04 * s * 3, 0.3, lambda f: body if f.normal.z > -0.3 else belly)
    # Neck and head (8).
    neck = q.get('neck', 0.0) * s
    nb = Vector((0, 0.1 * s if up else L * 0.45, (leg + H * 1.75) if up else z0 + H * 0.4))
    if neck > 0:
        hc = nb + Vector((0, 0.25 * s, neck))
        npts = curve([nb, nb + Vector((0, 0.45 * s, neck * 0.35)), nb + Vector((0, -0.05 * s, neck * 0.7)), hc], 4)
    else:
        hc = nb + Vector((0, R * 0.35, R * (0.15 if up else 0.75)))
        npts = [nb, hc]
    beak = q['beak']
    bd = Vector((0, 1, -1.1 if beak == 'bent' else -0.15)).normalized()
    with k.limb(8, along(npts + [hc + bd * R * 1.5])):
        if neck > 0:
            k.tube(npts, R * 0.42, R * 0.38, body, sides=8)
        headc = c.get('head', body)

        def headpaint(f):
            p, n = f.calc_center_median(), f.normal
            if 'disc' in c and n.y > 0.3:
                return c['disc']
            if 'face' in c and n.y > 0.55 and abs(p.z - hc.z) < R * 0.4:
                return c['face']
            if 'throat' in c and n.z < -0.3 and n.y > 0:
                return c['throat']
            if 'cheek' in c and n.y > 0.2 and abs(n.x) > 0.6 and p.z < hc.z:
                return c['cheek']
            return headc

        k.blob([(hc, (R, R * 0.98, R * 0.95), headc, 2, 0.02, 1.0)], 0.05 * s, 0.32, headpaint)
        tipc = c['beakc']
        base = hc + bd * R * 0.82
        if beak == 'bill':  # a duck's flat bill
            k.ico(base + bd * R * 0.35, (R * 0.42, R * 0.5, R * 0.14), tipc, subdiv=2, rough=0)
        elif beak == 'hook':  # a hooked beak: upper mandible curving over
            pts = curve([base, base + Vector((0, R * 0.4, R * 0.02)), base + Vector((0, R * 0.55, -R * 0.35))], 3)
            k.tube(pts, R * 0.3, R * 0.03, tipc, sides=6)
            k.ico(base + Vector((0, R * 0.12, -R * 0.15)), (R * 0.2, R * 0.25, R * 0.12), shade(tipc, 0.85), subdiv=1, rough=0)
        elif beak == 'bent':  # a flamingo's beak, bent down, black-tipped
            pts = curve([base, base + bd * R * 0.6, base + bd * R * 0.8 + Vector((0, 0, -R * 0.55))], 3)
            k.tube(pts[:len(pts) // 2 + 1], R * 0.24, R * 0.16, tipc, sides=6)
            k.tube(pts[len(pts) // 2:], R * 0.16, R * 0.03, c.get('beaktip', BLACK), sides=6)
        elif beak == 'dagger':  # a kingfisher's long dagger beak
            k.cyl(base, base + bd * R * 1.6, R * 0.2, R * 0.01, tipc, sides=6)
        else:
            k.cyl(base, base + bd * R * 0.7, R * 0.22, R * 0.01, tipc, sides=6)
        for sx in (-1, 1):
            n = Vector((sx * (0.42 if 'disc' in c else 0.75), 0.62 if 'disc' in c else 0.45, 0.25)).normalized()
            pet_eye(k, hc + n * R * 0.88, n, R * (0.3 if 'disc' in c else 0.2), c['iris'],
                    lid=shade(headc, 0.85) if q.get('brow') else None)
            if q.get('tufts'):
                k.fin([hc + Vector((sx * R * 0.35, 0, R * 0.7)), hc + Vector((sx * R * 0.75, -R * 0.1, R * 1.25)), hc + Vector((sx * R * 0.65, -R * 0.15, R * 0.65))], R * 0.08, body)
        if 'crest' in c:
            for j in range(5 if pid == 'peacock' else 4):
                a = (j - 2) * 0.25
                p = hc + Vector((a * R * 0.3, -R * 0.1, R * 0.9))
                if pid == 'peacock':
                    k.tube([p, p + Vector((a * R * 0.3, -R * 0.05, R * 0.55))], R * 0.025, R * 0.02, body, sides=3)
                    k.ico(p + Vector((a * R * 0.3, -R * 0.05, R * 0.6)), R * 0.07, c['crest'], subdiv=1, rough=0)
                else:
                    k.cyl(p, p + Vector((0, -R * 0.55, R * (0.6 + 0.2 * (j % 2)))), R * 0.12, 0.0, c['crest'] if j % 2 else hexc('#d00000'), sides=4)
    # Wings (5, 6): folded along the body, or spread for the fliers.
    fly = q.get('flies')
    for limb, sx in ((5, -1), (6, 1)):
        root = Vector((sx * W * 0.82, (0.05 * s if up else L * 0.2), (leg + H * 1.45) if up else z0 + H * 0.45))
        if q.get('flippers'):
            tip = root + Vector((sx * 0.15 * s, 0.05 * s, -H * 1.0))
            with k.limb(limb, along([root, tip])):
                k.fin([root + Vector((0, 0.15 * s, 0)), root + Vector((0, -0.15 * s, 0)), tip], 0.07 * s, wingc)
            continue
        if fly:
            tip = root + Vector((sx * 1.7 * s, -0.25 * s, 0.35 * s))
        else:
            tip = root + Vector((sx * 0.15 * s, -L * 0.95, -H * 0.15))
        with k.limb(limb, along([root, tip])):
            shape = [root + Vector((0, 0.18 * s, 0.05 * s)), tip]
            if fly:
                shape = [root + Vector((0, 0.2 * s, 0)), root + Vector((sx * 0.9 * s, 0.12 * s, 0.25 * s)), tip,
                         tip + Vector((0, -0.35 * s, -0.1 * s)), root + Vector((sx * 0.9 * s, -0.55 * s, -0.05 * s)), root + Vector((0, -0.35 * s, -0.05 * s))]
            else:
                shape = [root + Vector((0, 0.2 * s, 0.05 * s)), root + Vector((sx * 0.12 * s, -L * 0.3, 0.1 * s)), tip, root + Vector((sx * 0.05 * s, -L * 0.45, -H * 0.4))]
            k.fin(shape, 0.06 * s, wingc)
            # Flight feathers along the trailing edge (and a band of colour on a parrot's wing).
            n_f = 6 if fly else 4
            for j in range(n_f):
                t = (j + 0.5) / n_f
                if fly:
                    a = root.lerp(tip, 0.25 + 0.75 * t) + Vector((0, -0.2 * s, -0.04 * s))
                    b_ = a + Vector((sx * 0.08 * s, -0.4 * s, -0.08 * s))
                else:
                    a = root.lerp(tip, 0.3 + 0.6 * t) + Vector((0, 0, -0.15 * s))
                    b_ = a + Vector((0, -0.3 * s, -0.08 * s))
                col = c.get('wingtip', shade(wingc, 0.8)) if t > 0.5 else c.get('wingband', shade(wingc, 0.9))
                k.fin([a + Vector((0, 0.05 * s, 0)), a + Vector((0, -0.05 * s, 0)), b_], 0.02 * s, col)
    # The tail (7).
    tb = Vector((0, -0.25 * s, leg + H * 0.35)) if up else Vector((0, -L * 0.5, z0 + H * 0.2))
    tk = q['tail']
    with k.limb(7, along([tb, tb + Vector((0, -1.0 * s, 0))])):
        if tk == 'stub':
            k.fin([tb + Vector((-0.12 * s, 0, 0)), tb + Vector((0.12 * s, 0, 0)), tb + Vector((0, -0.25 * s, 0.05 * s))], 0.04 * s, shade(body, 0.85))
        elif tk in ('short', 'fan', 'long'):
            ln, spread = {'short': (0.4, 0.2), 'fan': (0.6, 0.45), 'long': (1.1, 0.15)}[tk]
            tc = c.get('tailc', shade(wingc, 0.9))
            for j in range(5):
                a = (j - 2) / 2 * spread
                d = Vector((a, -1, 0.1)).normalized()
                k.fin([tb + Vector((-0.04 * s, 0, 0)), tb + Vector((0.04 * s, 0, 0)), tb + d * ln * s + Vector((a * 0.1 * s, 0, 0))], 0.02 * s,
                      tc if tk != 'long' or j % 2 else c['wing'])
        elif tk == 'peacock':  # the train, fanned up behind, with eye-spots
            for j in range(13):
                a = (j / 12 - 0.5) * 2.6
                d = Vector((math.sin(a), -0.4, math.cos(a) * 0.9 + 0.2)).normalized()
                tip = tb + d * 1.9 * s
                sd = d.cross(Vector((0, 1, 0))).normalized() * 0.13 * s
                k.fin([tb, tip + sd, tip + d * 0.12 * s, tip - sd], 0.02 * s, hexc('#2a9d8f') if j % 2 else hexc('#1f7a6f'))
                k.ico(tip, 0.1 * s, hexc('#ffd166'), subdiv=1, rough=0)
                k.ico(tip + Vector((0, 0.02 * s, 0)), 0.065 * s, hexc('#1e3a8a'), subdiv=1, rough=0)
                k.ico(tip + Vector((0, 0.04 * s, 0)), 0.035 * s, hexc('#2a9d8f'), subdiv=1, rough=0)
        elif tk == 'flame':  # long flame feathers, red to gold
            for j in range(7):
                a = (j - 3) * 0.22
                pts = curve([tb, tb + Vector((a * 0.4 * s, -0.6 * s, 0.1 * s)), tb + Vector((a * 0.9 * s, -1.2 * s, 0.35 * s * (1 + abs(a))))], 3)
                k.tube(pts, 0.07 * s, 0.01 * s, hexc('#d00000') if j % 2 else hexc('#ff7b00'), sides=4)
                k.ico(Vector(pts[-1]), 0.07 * s, hexc('#ffba08'), subdiv=1, rough=0.2)
    # Legs (3, 4) and feet.
    for limb, sx in ((3, -1), (4, 1)):
        hip = Vector((sx * W * 0.35, 0.0, (leg + H * 0.15) if up else z0 - H * 0.6))
        foot = Vector((sx * W * 0.38, 0.06 * s, 0.03 * s))
        knee = hip.lerp(foot, 0.5) + Vector((0, -0.08 * s if q['leg'] > 1 else 0.02 * s, 0))
        with k.limb(limb, along([hip, knee, foot])):
            k.tube_r([hip, knee, foot], [0.07 * s, 0.045 * s, 0.04 * s], c['legc'], sides=5)
            if beak == 'bill' or q.get('flippers'):
                k.fin([foot + Vector((-0.1 * s, -0.03 * s, 0)), foot + Vector((0.1 * s, -0.03 * s, 0)), foot + Vector((0.12 * s, 0.22 * s, 0)),
                       foot + Vector((-0.12 * s, 0.22 * s, 0))], 0.025 * s, c['legc'])
            else:
                for t in (-0.45, 0, 0.45):
                    k.cyl(foot, foot + Vector((t * 0.18 * s, 0.2 * s, -0.01 * s)), 0.025 * s, 0.01 * s, c['legc'], sides=3)
                k.cyl(foot, foot + Vector((0, -0.12 * s, -0.01 * s)), 0.02 * s, 0.008 * s, c['legc'], sides=3)

# -- the rest ---------------------------------------------------------------------------------------------------

def pet(i):
    pid = PET_IDS[i]
    k = Kit(1000 + i)
    s = 0.25 * PET_SIZE.get(pid, 1.0)
    if pid in QUADS:
        quad_pet(k, pid, QUADS[pid], s)
    elif pid in BIRDS:
        bird_pet(k, pid, BIRDS[pid], s)
    elif pid == 'lilyfrog':
        green, belly, dark = hexc('#6ab04c'), hexc('#f6e58d'), hexc('#3d7a2a')
        off = Vector((3.1, 1.7, 2.2))
        z0 = 0.42 * s
        k.loft([(-0.65 * s, 0.35 * s, 0.25 * s, z0), (-0.35 * s, 0.75 * s, 0.42 * s, z0 + 0.05 * s), (0.1 * s, 0.85 * s, 0.45 * s, z0 + 0.1 * s),
                (0.5 * s, 0.7 * s, 0.32 * s, z0 + 0.12 * s), (0.75 * s, 0.35 * s, 0.15 * s, z0 + 0.08 * s)],
               lambda p, n: belly if n.z < -0.2 or (n.y > 0.5 and p.z < z0) else (dark if noise.noise(p * (8 / s) + off) > 0.3 else green), sides=16)
        with k.limb(8, lambda co: 0.0):
            for sx in (-1, 1):
                bump = Vector((sx * 0.38 * s, 0.42 * s, z0 + 0.38 * s))
                k.ico(bump, 0.22 * s, green, subdiv=2, rough=0)
                pet_eye(k, bump + Vector((sx * 0.06 * s, 0.1 * s, 0.1 * s)), Vector((sx * 0.4, 0.6, 0.6)), 0.16 * s, hexc('#d4a017'))
            k.tube([(0.55 * s * math.cos(a), 0.58 * s + 0.12 * s * math.sin(a), z0 + 0.04 * s) for a in [math.pi * (0.15 + 0.7 * j / 8) for j in range(9)]],
                   0.012 * s, 0.012 * s, hexc('#2d5a1e'), sides=3, cap=False)  # the wide mouth
        for limb, sx in ((1, -1), (2, 1)):
            top = Vector((sx * 0.45 * s, 0.35 * s, z0 - 0.1 * s))
            foot = Vector((sx * 0.6 * s, 0.6 * s, 0.03 * s))
            with k.limb(limb, along([top, foot])):
                k.tube([top, foot], 0.08 * s, 0.06 * s, green, sides=6)
                k.fin([foot, foot + Vector((sx * 0.15 * s, 0.12 * s, 0)), foot + Vector((0, 0.18 * s, 0)), foot + Vector((-sx * 0.1 * s, 0.12 * s, 0))], 0.02 * s, belly)
        for limb, sx in ((3, -1), (4, 1)):  # big folded back legs
            hip = Vector((sx * 0.6 * s, -0.35 * s, z0))
            knee = Vector((sx * 0.85 * s, 0.05 * s, z0 + 0.1 * s))
            foot = Vector((sx * 0.75 * s, -0.25 * s, 0.03 * s))
            with k.limb(limb, along([hip, knee, foot])):
                k.tube_r([hip, knee, foot], [0.2 * s, 0.13 * s, 0.07 * s], green, sides=8)
                k.fin([foot, foot + Vector((sx * 0.25 * s, 0.2 * s, 0)), foot + Vector((0, 0.3 * s, 0)), foot + Vector((-sx * 0.15 * s, 0.2 * s, 0))], 0.02 * s, belly)
    elif pid in ('sandcrab', 'hermit'):
        shellc, clawc = (hexc('#e76f51'), hexc('#f4a261')) if pid == 'sandcrab' else (hexc('#e76f51'), hexc('#e76f51'))
        z0 = 0.45 * s
        k.blob([((0, 0, z0), (0.95 * s, 0.68 * s, 0.36 * s), shellc, 2, 0.05, 1.5)], 0.05 * s, 0.35,
               lambda f: shade(shellc, 1.15) if f.normal.z > 0.6 else (shade(shellc, 0.8) if f.normal.z < -0.3 else shellc))
        with k.limb(8, lambda co: 0.0):  # eyes on stalks
            for sx in (-1, 1):
                stalk = Vector((sx * 0.22 * s, 0.55 * s, z0 + 0.15 * s))
                k.tube([stalk, stalk + Vector((sx * 0.03 * s, 0.05 * s, 0.32 * s))], 0.045 * s, 0.04 * s, shellc, sides=5)
                pet_eye(k, stalk + Vector((sx * 0.03 * s, 0.09 * s, 0.4 * s)), Vector((sx * 0.3, 1, 0.3)), 0.11 * s, hexc('#1b1b1b'))
            k.box((0, 0.66 * s, z0 - 0.05 * s), (0.3 * s, 0.03 * s, 0.03 * s), shade(shellc, 0.55))
        for limb, sx in ((5, -1), (6, 1)):  # claws
            sh = Vector((sx * 0.7 * s, 0.35 * s, z0))
            el = sh + Vector((sx * 0.3 * s, 0.35 * s, 0.05 * s))
            cl = el + Vector((sx * 0.05 * s, 0.45 * s, 0.15 * s))
            with k.limb(limb, along([sh, el, cl])):
                k.tube([sh, el, cl], 0.11 * s, 0.1 * s, shellc, sides=6)
                k.ico(cl, (0.27 * s, 0.38 * s, 0.22 * s), clawc, subdiv=2, rough=0.04)
                k.cyl(cl + Vector((0, 0.25 * s, 0.07 * s)), cl + Vector((sx * 0.06 * s, 0.62 * s, 0.14 * s)), 0.1 * s, 0.01 * s, clawc, sides=5)
                k.cyl(cl + Vector((0, 0.22 * s, -0.08 * s)), cl + Vector((sx * 0.03 * s, 0.5 * s, -0.04 * s)), 0.08 * s, 0.01 * s, shade(clawc, 0.85), sides=5)
        for j in range(6):  # legs (9-14)
            sx = -1 if j < 3 else 1
            y = (0.2 - (j % 3) * 0.28) * s
            hip = Vector((sx * 0.7 * s, y, z0 - 0.05 * s))
            knee = Vector((sx * 1.15 * s, y, z0 + 0.15 * s))
            foot = Vector((sx * 1.35 * s, y - 0.05 * s, 0.02 * s))
            with k.limb(9 + j, along([hip, knee, foot])):
                k.tube_r([hip, knee, foot], [0.06 * s, 0.05 * s, 0.02 * s], shellc, sides=5)
        if pid == 'hermit':  # a spiral whelk shell on its back, banded
            pts = [Vector((math.cos(a) * 0.32 * s * (1 - t), -0.3 * s + math.sin(a) * 0.32 * s * (1 - t), 0.7 * s + t * 1.0 * s))
                   for t, a in [(j / 30, j / 30 * math.tau * 2.4) for j in range(31)]]
            faces = k.tube_r(pts, [0.5 * s * (1 - t * 0.85) for t in [j / 30 for j in range(31)]], hexc('#e9c46a'), sides=12)
            for f in faces:
                if int(f.calc_center_median().z / (0.12 * s)) % 2:
                    k.paint([f], hexc('#c97f3a'), 0.04)
    elif pid == 'snail':
        bodyc, shellc = hexc('#d4c4a8'), hexc('#c08552')
        k.loft([(-0.85 * s, 0.1 * s, 0.06 * s, 0.07 * s), (-0.4 * s, 0.3 * s, 0.14 * s, 0.13 * s), (0.4 * s, 0.32 * s, 0.17 * s, 0.16 * s),
                (0.85 * s, 0.25 * s, 0.25 * s, 0.32 * s), (1.05 * s, 0.18 * s, 0.2 * s, 0.42 * s)],
               lambda p, n: shade(bodyc, 0.85) if n.z < -0.3 else bodyc, sides=12)
        pts = [Vector((0.05 * s, -0.15 * s + math.cos(a) * 0.4 * s * (1 - t * 0.9), 0.75 * s + math.sin(a) * 0.4 * s * (1 - t * 0.9)))
               for t, a in [(j / 36, j / 36 * math.tau * 2.6) for j in range(37)]]
        faces = k.tube_r([p + Vector((t * 0.25 * s, 0, 0)) for p, t in zip(pts, [j / 36 for j in range(37)])],
                         [0.32 * s * (1 - t * 0.8) for t in [j / 36 for j in range(37)]], shellc, sides=12)
        for fi, f in enumerate(faces):
            if (fi // 12) % 3 == 0:
                k.paint([f], hexc('#8a5a33'), 0.03)
        with k.limb(8, lambda co: max(0.0, (co.y - 0.7 * s) / (0.6 * s))):
            for sx in (-1, 1):
                base = Vector((sx * 0.08 * s, 0.95 * s, 0.5 * s))
                tip = base + Vector((sx * 0.08 * s, 0.15 * s, 0.4 * s))
                k.tube([base, tip], 0.035 * s, 0.025 * s, bodyc, sides=5)
                pet_eye(k, tip + Vector((0, 0.02 * s, 0.05 * s)), Vector((sx * 0.4, 1, 0.3)), 0.07 * s, hexc('#1b1b1b'))
            k.box((0, 1.12 * s, 0.36 * s), (0.12 * s, 0.02 * s, 0.015 * s), shade(bodyc, 0.6))
    elif pid == 'turtle':
        skin, shellc, dark = hexc('#90a955'), hexc('#4f772d'), hexc('#31572c')
        k.blob([((0, 0, 0.42 * s), (0.95 * s, 1.08 * s, 0.55 * s), shellc, 2, 0.02, 1.0)], 0.06 * s, 0.22,
               lambda f: (dark if (int((f.calc_center_median().x / s + 5) / 0.45) + int((f.calc_center_median().y / s + 5) / 0.45)) % 2 else shellc)
               if f.normal.z > -0.2 else hexc('#d4c486'))
        k.vloft([(0.2 * s, 0.98 * s, 1.1 * s, 0.0), (0.3 * s, 1.0 * s, 1.12 * s, 0.0)], hexc('#d4c486'), sides=20, bottom=False, top=False)
        head = Vector((0, 1.2 * s, 0.45 * s))
        with k.limb(8, along([Vector((0, 0.8 * s, 0.35 * s)), head])):
            k.tube([Vector((0, 0.8 * s, 0.35 * s)), head], 0.2 * s, 0.22 * s, skin, sides=8)
            k.ico(head, (0.28 * s, 0.36 * s, 0.26 * s), skin, subdiv=2, rough=0.02)
            for sx in (-1, 1):
                n = Vector((sx * 0.75, 0.55, 0.3)).normalized()
                pet_eye(k, head + n * 0.27 * s, n, 0.09 * s, hexc('#4a3a1a'))
            k.box(head + Vector((0, 0.3 * s, -0.08 * s)), (0.25 * s, 0.02 * s, 0.015 * s), shade(skin, 0.6))
        for limb, sx, y in ((1, -1, 0.65), (2, 1, 0.65), (3, -1, -0.65), (4, 1, -0.65)):
            top = Vector((sx * 0.7 * s, y * s, 0.32 * s))
            foot = Vector((sx * 0.85 * s, y * 1.05 * s, 0.05 * s))
            with k.limb(limb, along([top, foot])):
                k.tube([top, foot], 0.16 * s, 0.14 * s, skin, sides=7)
                k.ico(foot, (0.17 * s, 0.2 * s, 0.07 * s), skin, subdiv=1, rough=0)
        with k.limb(7, along([Vector((0, -1.0 * s, 0.3 * s)), Vector((0, -1.35 * s, 0.22 * s))])):
            k.cyl((0, -1.0 * s, 0.3 * s), (0, -1.35 * s, 0.22 * s), 0.1 * s, 0.0, skin, sides=6)
    elif pid == 'seal':
        grey, light = hexc('#9aa5b1'), hexc('#cfd6dd')
        z0 = 0.45 * s
        off = Vector((2.2, 7.1, 1.3))
        k.loft([(-1.1 * s, 0.15 * s, 0.12 * s, 0.2 * s), (-0.7 * s, 0.38 * s, 0.32 * s, z0 - 0.08 * s), (0.0, 0.58 * s, 0.48 * s, z0),
                (0.5 * s, 0.5 * s, 0.48 * s, z0 + 0.12 * s), (0.85 * s, 0.32 * s, 0.33 * s, z0 + 0.3 * s)],
               lambda p, n: light if n.z < -0.3 else (shade(grey, 0.75) if noise.noise(p * (7 / s) + off) > 0.35 else grey), sides=14)
        hc = Vector((0, 1.0 * s, z0 + 0.45 * s))
        with k.limb(8, along([Vector((0, 0.75 * s, z0 + 0.25 * s)), hc])):
            k.blob([(hc, (0.36 * s, 0.38 * s, 0.34 * s), grey, 2, 0.02, 1.0), (hc + Vector((0, 0.3 * s, -0.08 * s)), (0.2 * s, 0.18 * s, 0.15 * s), grey, 1, 0.02, 1.0)],
                   0.05 * s, 0.32, lambda f: light if f.calc_center_median().y > hc.y + 0.2 * s else grey)
            k.ico(hc + Vector((0, 0.47 * s, -0.04 * s)), (0.07 * s, 0.04 * s, 0.05 * s), BLACK, subdiv=1, rough=0)
            for sx in (-1, 1):
                n = Vector((sx * 0.55, 0.75, 0.3)).normalized()
                pet_eye(k, hc + n * 0.34 * s, n, 0.11 * s, hexc('#1b1b1b'))
                for j in range(3):
                    a = hc + Vector((sx * 0.12 * s, 0.42 * s, -0.08 * s - j * 0.025 * s))
                    k.tube([a, a + Vector((sx * 0.3 * s, 0.05 * s, 0.03 * s - j * 0.04 * s))], 0.008 * s, 0.004 * s, hexc('#ececec'), sides=3)
        for limb, sx in ((5, -1), (6, 1)):  # front flippers
            root = Vector((sx * 0.45 * s, 0.4 * s, z0 - 0.3 * s))
            tip = root + Vector((sx * 0.4 * s, 0.05 * s, -0.25 * s))
            with k.limb(limb, along([root, tip])):
                k.fin([root + Vector((0, 0.12 * s, 0)), tip + Vector((0, 0.08 * s, 0)), tip + Vector((0, -0.08 * s, 0)), root + Vector((0, -0.12 * s, 0))], 0.05 * s, shade(grey, 0.8))
        with k.limb(7, along([Vector((0, -1.0 * s, 0.2 * s)), Vector((0, -1.5 * s, 0.15 * s))])):  # rear flippers
            for sx in (-1, 1):
                k.fin([Vector((0, -1.0 * s, 0.2 * s)), Vector((sx * 0.4 * s, -1.45 * s, 0.12 * s)), Vector((sx * 0.1 * s, -1.5 * s, 0.15 * s))], 0.05 * s, shade(grey, 0.8))
    elif pid in ('octopus', 'kraken'):
        body, accent = (hexc('#e56b6f'), hexc('#b5838d')) if pid == 'octopus' else (hexc('#6a0dad'), hexc('#c77dff'))
        head = Vector((0, 0, 1.5 * s))
        off = Vector((4.4, 1.1, 3.3))
        with k.limb(8, lambda co: 0.0):
            k.blob([(head, (0.72 * s, 0.7 * s, 0.85 * s), body, 2, 0.02, 1.0), (head + Vector((0, -0.2 * s, 0.45 * s)), (0.58 * s, 0.6 * s, 0.62 * s), body, 2, 0.02, 1.0)],
                   0.06 * s, 0.35, lambda f: accent if noise.noise(f.calc_center_median() * (5 / s) + off) > 0.3 else body)
            for sx in (-1, 1):
                n = Vector((sx * 0.45, 0.85, -0.05)).normalized()
                pet_eye(k, head + Vector((0, 0, -0.2 * s)) + n * 0.68 * s, n, 0.2 * s, hexc('#f4d35e') if pid == 'kraken' else hexc('#2b2b2b'), lid=shade(body, 0.85))
            if pid == 'kraken':
                for j in range(5):
                    a = (j - 2) * 0.35
                    p = head + Vector((math.sin(a) * 0.4 * s, -0.1 * s, 0.95 * s))
                    k.cyl(p, p + Vector((math.sin(a) * 0.1 * s, -0.05 * s, 0.32 * s)), 0.07 * s, 0.0, hexc('#ffd166'), sides=4)
        for t in range(8):
            a = t / 8 * math.tau + 0.2
            d = Vector((math.cos(a), math.sin(a), 0))
            side = d.cross(Vector((0, 0, 1)))
            pts = curve([head + d * 0.35 * s + Vector((0, 0, -0.55 * s)), head + d * 0.75 * s + Vector((0, 0, -1.05 * s)),
                         head + d * 1.1 * s + Vector((0, 0, -1.3 * s)), head + d * 1.45 * s + side * 0.25 * s + Vector((0, 0, -1.05 * s))], 4)
            with k.limb(9 + t, along(pts)):
                tentacle(k, pts, 0.17 * s, 0.025 * s, body if t % 2 == 0 else shade(body, 0.9), accent, sides=7)
    elif pid in ('spiritkoi', 'starwhale'):
        koi = pid == 'spiritkoi'
        body, accent = (hexc('#fdfcdc'), hexc('#f77f00')) if koi else (hexc('#3a0ca3'), hexc('#bde0fe'))
        z0 = 1.2 * s
        L = 1.4 * s
        off = Vector((1.2, 5.5, 3.3))

        def skin(p, n):
            if n.z < -0.35:
                return accent if not koi else WHITE
            if koi and noise.noise(p * (4 / s) + off) > 0.12:
                return accent
            if not koi and n.z > 0.2 and noise.noise(p * (7 / s) + off) > 0.35:
                return hexc('#fdfdfd')
            return body

        tail_t = axis_t(L * 1.1, -L * 1.6)
        with k.limb(7, tail_t):
            k.loft([(-L, 0.14 * s, 0.16 * s, z0), (-L * 0.6, 0.34 * s, 0.4 * s, z0), (-L * 0.1, 0.56 * s, 0.6 * s, z0), (L * 0.4, 0.56 * s, 0.6 * s, z0 + 0.05 * s),
                    (L * 0.85, 0.4 * s, 0.45 * s, z0 + 0.08 * s), (L * 1.1, 0.14 * s, 0.16 * s, z0 + 0.05 * s)], skin, sides=14)
            tail = Vector((0, -L, z0))
            if koi:  # long flowing fins, a butterfly koi
                k.fin([tail + Vector((0, 0.1 * s, 0)), tail + Vector((0, -0.6 * s, 0.85 * s)), tail + Vector((0, -1.05 * s, 0.4 * s)), tail + Vector((0, -0.7 * s, 0)),
                       tail + Vector((0, -1.05 * s, -0.4 * s)), tail + Vector((0, -0.6 * s, -0.85 * s))], 0.03 * s, hexc('#ffd9a8'))
                k.fin([Vector((0, L * 0.4, z0 + 0.52 * s)), Vector((0, -L * 0.1, z0 + 0.95 * s)), Vector((0, -L * 0.55, z0 + 0.4 * s))], 0.03 * s, hexc('#ffd9a8'))
                for sx in (-1, 1):
                    k.tube([Vector((sx * 0.15 * s, L * 1.08, z0)), Vector((sx * 0.45 * s, L * 1.25, z0 - 0.3 * s))], 0.025 * s, 0.01 * s, accent, sides=3)
            else:  # whale flukes and a glowing dorsal hump
                for sx in (-1, 1):
                    k.fin([tail, tail + Vector((sx * 0.75 * s, -0.4 * s, 0.05 * s)), tail + Vector((sx * 0.6 * s, -0.15 * s, 0.03 * s))], 0.05 * s, accent)
        for limb, sx in ((5, -1), (6, 1)):
            root = Vector((sx * 0.5 * s, L * 0.4, z0 - 0.2 * s))
            tip = root + Vector((sx * (0.75 if koi else 0.6) * s, -0.3 * s, -0.35 * s))
            with k.limb(limb, along([root, tip])):
                k.fin([root + Vector((0, 0.1 * s, 0)), tip, root + Vector((0, -0.2 * s, -0.05 * s))], 0.03 * s, hexc('#ffd9a8') if koi else accent)
        with k.limb(8, lambda co: 0.0):
            for sx in (-1, 1):
                n = Vector((sx * 0.85, 0.45, 0.2)).normalized()
                pet_eye(k, Vector((sx * 0.4 * s, L * 0.75, z0 + 0.12 * s)), n, 0.12 * s, hexc('#1b1b1b') if koi else hexc('#bde0fe'))
            k.box((0, L * 1.1, z0 - 0.08 * s), (0.2 * s, 0.02 * s, 0.02 * s), shade(body, 0.6))
        if not koi:
            for j in range(14):
                a = j / 14 * math.tau
                k.ico((math.cos(a) * 0.55 * s, L * 0.1 + math.sin(a) * 0.4 * s, z0 + 0.55 * s), 0.04 * s, hexc('#fff3b0'), subdiv=1, rough=0)
    elif pid == 'wyrmling':
        body, belly = hexc('#3a86a8'), hexc('#90e0ef')
        pts = [Vector((math.sin(t * 6) * 0.45 * s, s * (1.5 - t * 3.6), s * (1.0 + math.sin(t * 4 + 1) * 0.3))) for t in [j / 14 for j in range(15)]]
        pts.reverse()
        path = curve(pts, 3)
        with k.limb(7, lambda co: 1 - along(path)(co)):
            faces = k.tube_r(path, [0.05 * s + 0.33 * s * (j / (len(path) - 1)) ** 0.7 for j in range(len(path))], body, sides=10)
            for f in faces:
                if f.normal.z < -0.3:
                    k.paint([f], belly, 0.03)
            for j in range(2, len(path) - 2, 3):
                p = Vector(path[j])
                k.fin([p + Vector((0, 0.08 * s, 0.2 * s)), p + Vector((0, -0.05 * s, 0.42 * s)), p + Vector((0, -0.12 * s, 0.22 * s))], 0.03 * s, belly)
        head = Vector(path[-1]) + Vector((0, 0.35 * s, 0.15 * s))
        with k.limb(8, along([Vector(path[-1]), head])):
            k.blob([(head, (0.36 * s, 0.5 * s, 0.32 * s), body, 2, 0.02, 1.0), (head + Vector((0, 0.38 * s, -0.08 * s)), (0.22 * s, 0.25 * s, 0.17 * s), body, 1, 0.02, 1.0)],
                   0.05 * s, 0.33, lambda f: belly if f.normal.z < -0.4 else body)
            for sx in (-1, 1):
                n = Vector((sx * 0.7, 0.55, 0.35)).normalized()
                pet_eye(k, head + n * 0.34 * s + Vector((0, 0.05 * s, 0)), n, 0.12 * s, hexc('#ffd166'))
                k.tube(curve([head + Vector((sx * 0.15 * s, -0.15 * s, 0.22 * s)), head + Vector((sx * 0.25 * s, -0.45 * s, 0.45 * s)), head + Vector((sx * 0.2 * s, -0.75 * s, 0.5 * s))], 3), 0.06 * s, 0.01 * s, belly, sides=5)
                k.fin([head + Vector((sx * 0.3 * s, -0.2 * s, 0.02 * s)), head + Vector((sx * 0.75 * s, -0.45 * s, 0.2 * s)), head + Vector((sx * 0.35 * s, -0.5 * s, -0.08 * s))], 0.03 * s, belly)
                k.tube(curve([head + Vector((sx * 0.1 * s, 0.6 * s, -0.12 * s)), head + Vector((sx * 0.4 * s, 0.75 * s, -0.25 * s)), head + Vector((sx * 0.55 * s, 0.6 * s, -0.4 * s))], 3), 0.015 * s, 0.006 * s, belly, sides=3)
    elif pid == 'moonmoth':
        body, wing, wing2, spot = hexc('#bde0fe'), hexc('#a2d2ff'), hexc('#cde7ff'), hexc('#f1f1f1')
        torso = Vector((0, 0, 1.2 * s))
        k.blob([(torso, (0.24 * s, 0.75 * s, 0.24 * s), body, 2, 0.15, 3.0)], 0.04 * s, 0.35, lambda f: shade(body, 0.85) if f.normal.z < 0 else body)
        with k.limb(8, lambda co: 0.0):
            head = torso + Vector((0, 0.7 * s, 0.05 * s))
            k.blob([(head, (0.22 * s, 0.2 * s, 0.2 * s), WHITE, 1, 0.2, 3.0)], 0.03 * s, 0.35, lambda f: hexc('#f6f6f6'))
            for sx in (-1, 1):
                n = Vector((sx * 0.8, 0.5, 0.2)).normalized()
                pet_eye(k, head + n * 0.18 * s, n, 0.1 * s, hexc('#1b1b1b'))
                ant = [head + Vector((sx * 0.06 * s, 0.1 * s, 0.15 * s)), head + Vector((sx * 0.25 * s, 0.35 * s, 0.55 * s))]
                k.tube(ant, 0.015 * s, 0.01 * s, spot, sides=3)
                for j in range(5):  # feathery antennae
                    p = Vector(ant[0]).lerp(Vector(ant[1]), (j + 1) / 6)
                    k.fin([p, p + Vector((sx * 0.08 * s, 0.05 * s, -0.03 * s)), p + Vector((0, 0.04 * s, 0.02 * s))], 0.01 * s, spot)
        for limb, sx in ((5, -1), (6, 1)):
            root = torso + Vector((sx * 0.15 * s, 0.1 * s, 0.05 * s))
            tip = root + Vector((sx * 1.4 * s, 0.1 * s, 0.35 * s))
            with k.limb(limb, along([root, tip])):
                for (y, sz, col) in ((0.25, 1.3, wing), (-0.45, 0.95, wing2)):
                    r0 = root + Vector((0, y * s, 0))
                    k.fin([r0, r0 + Vector((sx * sz * 0.6 * s, 0.5 * sz * s, 0.3 * s)), r0 + Vector((sx * sz * s, 0.15 * sz * s, 0.3 * s)),
                           r0 + Vector((sx * sz * 0.9 * s, -0.55 * sz * s, 0.12 * s)), r0 + Vector((sx * 0.25 * s, -0.4 * sz * s, 0.0))], 0.025 * s, col)
                    k.ico(r0 + Vector((sx * sz * 0.6 * s, 0.0, 0.24 * s)), (0.13 * s * sz, 0.13 * s * sz, 0.03 * s), spot, subdiv=1, rough=0)
                    k.ico(r0 + Vector((sx * sz * 0.6 * s, 0.0, 0.26 * s)), (0.07 * s * sz, 0.07 * s * sz, 0.02 * s), hexc('#7fa8d8'), subdiv=1, rough=0)
                # A moon moth's long trailing tails on the hind wings.
                r1 = root + Vector((sx * 0.5 * s, -0.75 * s, 0.05 * s))
                k.tube(curve([r1, r1 + Vector((sx * 0.15 * s, -0.5 * s, -0.05 * s)), r1 + Vector((sx * 0.1 * s, -0.95 * s, 0.05 * s))], 3), 0.05 * s, 0.03 * s, wing2, sides=4)
    return k.build(f'pet_{pid}', ao=0.12)

# ---- small scenery ---------------------------------------------------------------------------------------

def flowers(i):
    """A patch of five-petalled flowers in one colour, among a few leaves."""
    colours = ['#f4f1de', '#ffd166', '#c77dff', '#ff8fab', '#90e0ef']
    k = Kit(500 + i)
    rnd = k.rnd
    petal, centre, stem, leaf = hexc(colours[i % 5]), hexc('#f9c74f'), hexc('#4c7a33'), hexc('#5b8f3e')
    for j in range(7):
        c = Vector((rnd.uniform(-0.55, 0.55), rnd.uniform(-0.55, 0.55), rnd.uniform(0.2, 0.36)))
        k.tube([(c.x, c.y, 0), (c.x + 0.02, c.y, c.z * 0.6), c], 0.012, 0.01, stem, sides=3, cap=False)
        tilt = Vector((rnd.uniform(-0.3, 0.3), rnd.uniform(-0.3, 0.3), 1)).normalized()
        u = tilt.cross(Vector((1, 0, 0))).normalized()
        w = tilt.cross(u)
        for p in range(5):
            a = p / 5 * math.tau
            d = (u * math.cos(a) + w * math.sin(a))
            side = d.cross(tilt).normalized() * 0.03
            k.fin([c, c + d * 0.08 + side, c + d * 0.1 + tilt * 0.01, c + d * 0.08 - side], 0.006, petal)
        k.ico(c + tilt * 0.012, 0.022, centre, subdiv=1, rough=0)
        a = rnd.random() * math.tau
        d = Vector((math.cos(a), math.sin(a), 0.25))
        k.fin([(c.x, c.y, 0.02), (c.x + d.x * 0.18, c.y + d.y * 0.18, 0.08), (c.x + d.x * 0.08 + d.y * 0.04, c.y + d.y * 0.08 - d.x * 0.04, 0.03)], 0.005, leaf)
    return k.build(f'flowers_{i}', ao=0.25)

def tuft(i):
    """A tuft of curved, tapering grass blades."""
    k = Kit(600 + i)
    rnd = k.rnd
    col = hexc('#3d6b2c') if i % 2 == 0 else hexc('#5b8f3e')
    for j in range(9):
        a = rnd.random() * math.tau
        d = Vector((math.cos(a), math.sin(a), 0))
        h = rnd.uniform(0.3, 0.62)
        lean = rnd.uniform(0.08, 0.28)
        base = d * rnd.uniform(0, 0.08)
        side = Vector((-d.y, d.x, 0)) * 0.028
        mid = base + d * lean * 0.4 + Vector((0, 0, h * 0.55))
        tip = base + d * lean + Vector((0, 0, h))
        c = col if j % 2 else shade(col, 1.18)
        k.fin([base - side, base + side, mid + side * 0.6, tip, mid - side * 0.6], 0.004, c)
    return k.build(f'tuft_{i}', ao=0.4)

def pebbles(i):
    k = Kit(700 + i)
    rnd = k.rnd
    for j in range(5):
        g = rnd.uniform(0.55, 0.8)
        c = (g, g * 0.96, g * 0.9, 1)
        k.ico((rnd.uniform(-0.5, 0.5), rnd.uniform(-0.5, 0.5), 0.02), (rnd.uniform(0.07, 0.15), rnd.uniform(0.06, 0.12), 0.05), c, subdiv=1, rough=0.2)
    return k.build(f'pebbles_{i}', ao=0.2)

def lily(i):
    """A lily pad with its notch and veins; one in three has a flower."""
    k = Kit(900 + i)
    rnd = k.rnd
    r = rnd.uniform(0.5, 1.0)
    rot = rnd.random() * math.tau
    inner, outer, vein = hexc('#7dbb5a'), hexc('#4f8a3a'), hexc('#a7d07f')
    n = 14
    gap = 0.45
    centre = k.bm.verts.new((0, 0, 0.035))
    rim = []
    for j in range(n + 1):
        a = rot + gap / 2 + j / n * (math.tau - gap)
        rr = r * (1 + 0.04 * math.sin(j * 2.3))
        rim.append(k.bm.verts.new((math.cos(a) * rr, math.sin(a) * rr, 0.02 + 0.01 * math.sin(j))))
    faces = []
    for j in range(n):
        f = k.bm.faces.new((centre, rim[j], rim[j + 1]))
        k.paint([f], inner if j % 3 else outer, 0.08)
        faces.append(f)
    k.orient(faces, lambda f: Vector((0, 0, 1)))
    for j in range(5):
        a = rot + gap / 2 + (j + 0.5) / 5 * (math.tau - gap)
        k.box((math.cos(a) * r * 0.45, math.sin(a) * r * 0.45, 0.04), (r * 0.8, 0.02, 0.005), vein,
              rot=Matrix.Rotation(a, 4, 'Z'))
    if i % 3 == 0:
        petal = hexc('#ffd6e5') if i % 2 == 0 else hexc('#ffffff')
        for ring, (n_, ln, up) in enumerate(((8, 0.24, 0.12), (6, 0.17, 0.2))):
            for j in range(n_):
                a = j / n_ * math.tau + ring * 0.3
                d = Vector((math.cos(a), math.sin(a), 0))
                side = Vector((-d.y, d.x, 0)) * 0.06
                base = Vector((0, 0, 0.05))
                k.fin([base + side, base + d * ln * 0.6 + side * 1.2 + Vector((0, 0, up * 0.5)), base + d * ln + Vector((0, 0, up)), base + d * ln * 0.6 - side * 1.2 + Vector((0, 0, up * 0.5)), base - side], 0.008, shade(petal, 1 - ring * 0.06))
        k.ico((0, 0, 0.1), 0.05, hexc('#ffd166'), subdiv=1, rough=0)
    return k.build(f'lily_{i}', ao=0)

def reeds(i):
    """A clump of reeds standing in the shallows (its base 1.5 m under the surface); some with cattails."""
    k = Kit(1100 + i)
    rnd = k.rnd
    blade, dark, head = hexc('#6f9c4a'), hexc('#4c7a33'), hexc('#6b4423')
    h = rnd.uniform(1.0, 2.4)
    for b in range(5 + rnd.randrange(3)):
        base = Vector((rnd.uniform(-0.3, 0.3), rnd.uniform(-0.3, 0.3), -1.5))
        lean = Vector((rnd.uniform(-0.3, 0.3), rnd.uniform(-0.3, 0.3), 0))
        top = base + Vector((0, 0, 1.5 + h * rnd.uniform(0.7, 1.0))) + lean
        side = lean.cross(Vector((0, 0, 1))).normalized() * 0.035 if lean.length > 0.01 else Vector((0.035, 0, 0))
        mid = base.lerp(top, 0.65) + lean * 0.2
        k.fin([base - side, base + side, mid + side * 0.7, top, mid - side * 0.7], 0.006, blade if b % 2 else dark)
        if b < 2 and i % 3 == 0:
            p = base.lerp(top, 0.82)
            k.cyl(p, p + Vector((lean.x * 0.08, lean.y * 0.08, 0.32)), 0.055, 0.05, head, sides=6)
    return k.build(f'reeds_{i}', ao=0.3)

def bucket(i):
    """A chum bucket: a galvanised pail of red chum, with a wire handle."""
    k = Kit(1200)
    metal, rim, chum, wire = hexc('#8d99a6'), hexc('#adb5bd'), hexc('#9d0208'), hexc('#343a40')
    k.cyl((0, 0, 0), (0, 0, 0.4), 0.21, 0.26, metal, sides=12)
    for z in (0.1, 0.25):
        k.cyl((0, 0, z), (0, 0, z + 0.025), 0.22 + z * 0.12 + 0.005, 0.22 + z * 0.12 + 0.008, rim, sides=12)
    k.cyl((0, 0, 0.38), (0, 0, 0.42), 0.265, 0.27, rim, sides=12)
    k.ico((0, 0, 0.37), (0.24, 0.24, 0.05), chum, subdiv=2, rough=0.3)
    k.tube(bezier((-0.26, 0, 0.38), (0, 0, 0.75), (0.26, 0, 0.38), 8), 0.008, 0.008, wire, sides=4)
    return k.build('bucket_0', ao=0.2)

def orientation_test(i=0):
    """Markers to check how Blender's axes land in Unity: a block towards +X and a taller one towards +Y."""
    k = Kit(1)
    k.box((2, 0, 0.5), (1, 1, 1), hexc('#ff0000'))
    k.box((0, 3, 1), (1, 1, 2), hexc('#00ff00'))
    k.box((0, 0, 0.25), (0.5, 0.5, 0.5), hexc('#808080'))
    return k.build('axes_0', ao=0)

KINDS = {
    'tree': (leafy_tree, 6),
    'pine': (pine, 4),
    'bush': (bush, 5),
    'boulder': (boulder, 5),
    'stone': (stone, 4),
    'driftwood': (driftwood, 2),
    'boat': (lake_boat, 1),
    'trawler': (trawler, 1),
    'stall': (stall, 2),
    'zoo': (zoo_wagon, 1),
    'signpost': (signpost, 2),
    'angler': (angler, 1),
    'gear': (gear, len(GEAR)),
    'rod': (rod, 1),
    'boss': (boss_part, len(BOSSES) * 5),
    'fish': (fish, len(FISH_IDS)),
    'pet': (pet, len(PET_IDS)),
    'flowers': (flowers, 5),
    'tuft': (tuft, 4),
    'pebbles': (pebbles, 3),
    'lily': (lily, 6),
    'reeds': (reeds, 6),
    'bucket': (bucket, 1),
}

def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    preview_dir = None
    if '--preview' in argv:
        j = argv.index('--preview')
        preview_dir = argv[j + 1]
        argv = argv[:j] + argv[j + 2:]
    each = '--each' in argv
    argv = [a for a in argv if a != '--each']
    kinds = argv or list(KINDS)
    for kind in kinds:
        make, count = KINDS[kind]
        clear_scene()
        objs = []
        for i in range(count):
            ob = make(i)
            ob.data.name = ob.name
            objs.append(ob)
        bpy.context.view_layer.update()
        export(objs, kind)
        if preview_dir and each:
            # One picture per model: hide the others while each is rendered.
            for ob in objs:
                for other in objs:
                    other.hide_render = other is not ob
                preview([ob], ob.name, preview_dir)
            for ob in objs:
                ob.hide_render = False
        elif preview_dir:
            preview(objs, kind, preview_dir)

if __name__ == '__main__':
    main()
