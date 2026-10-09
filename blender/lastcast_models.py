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

# ---- building meshes ----------------------------------------------------------------------------------

class Kit:
    """A mesh under construction: parts are added with their own colour, then baked into one flat-shaded object."""

    def __init__(self, seed=0):
        self.bm = bmesh.new()
        self.col = self.bm.loops.layers.color.new('Col')
        self.rnd = random.Random(seed)
        self.seed = seed

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
# Parts the game colours itself (a shop's awning, an angler's jacket, a fish's body) are painted white
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
    for y in (-2.5, 1.5, 5.5):
        for side in (-1, 1):
            k.box((side * (B - 0.7), y, D + 0.25), (0.45, 2.4, 0.08), hexc('#8a5a33'))
            k.box((side * (B - 0.7), y, D + 0.12), (0.35, 2.2, 0.24), hexc('#5b3c22'))
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
EYE = hexc('#1e1e1e')

def angler(i):
    """Another player, as you see them: facing +Y, 1.85 m tall. Regions: A jacket, B boots, C waders.
    The right hand holds the rod at (0.32, 0.25, 1.15). Hats are separate (hat_*), from 1.76 m up."""
    k = Kit(80)
    J, Bt, W = region('A'), region('B'), region('C')
    for x in (-0.12, 0.12):
        k.ico((x, 0.04, 0.08), (0.1, 0.16, 0.085), Bt, subdiv=1, rough=0.04)
        k.tube([(x, 0, 0.12), (x * 1.02, 0.01, 0.5), (x * 1.08, 0, 0.88)], 0.095, 0.115, W, sides=7)
    k.ico((0, 0, 0.92), (0.23, 0.15, 0.13), W, subdiv=1, rough=0.02)
    k.ico((0, 0, 1.2), (0.26, 0.17, 0.3), J, subdiv=2, rough=0.02)
    k.ico((0, 0.01, 1.4), (0.3, 0.17, 0.11), J, subdiv=1, rough=0.02)
    # Wader bib and braces over the jacket; vest pockets.
    k.box((0, 0.155, 1.08), (0.26, 0.03, 0.26), W)
    for x in (-0.09, 0.09):
        k.box((x, 0.165, 1.32), (0.04, 0.025, 0.24), W)
        k.box((x * 1.6, 0.168, 1.24), (0.09, 0.02, 0.08), region('A', 0.8))
    belt = [(0.25 * math.cos(a), 0.165 * math.sin(a), 0.97) for a in [j / 12 * math.tau for j in range(13)]]
    k.tube(belt, 0.03, 0.03, hexc('#4a3424'), sides=4, cap=False)
    # Arms: the left hangs down, the right holds the rod out in front.
    k.tube([(-0.3, 0, 1.42), (-0.36, 0.03, 1.17), (-0.33, 0.09, 0.96)], 0.075, 0.06, J, sides=6)
    k.ico((-0.33, 0.11, 0.91), 0.055, SKIN, subdiv=1, rough=0.05)
    k.tube([(0.3, 0, 1.42), (0.38, 0.12, 1.22), (0.33, 0.24, 1.16)], 0.075, 0.06, J, sides=6)
    k.ico((0.32, 0.27, 1.15), 0.058, SKIN, subdiv=1, rough=0.05)
    # Neck, collar and head: eyes, nose, ears, hair.
    k.cyl((0, 0, 1.49), (0, 0, 1.57), 0.065, 0.065, SKIN, sides=8)
    k.tube([(0.1 * math.cos(a), 0.08 * math.sin(a) + 0.01, 1.51) for a in [j / 10 * math.tau for j in range(11)]], 0.025, 0.025, region('A', 0.9), sides=4, cap=False)
    k.ico((0, 0.01, 1.67), (0.145, 0.145, 0.165), SKIN, subdiv=2, rough=0.02)
    k.ico((0, 0.155, 1.655), (0.028, 0.04, 0.034), shade(SKIN, 0.9), subdiv=1, rough=0)
    for x in (-0.055, 0.055):
        k.ico((x, 0.135, 1.705), 0.021, EYE, subdiv=1, rough=0)
        k.box((x, 0.13, 1.745), (0.05, 0.012, 0.012), hexc('#5b3a1e'))
    for x in (-0.145, 0.145):
        k.ico((x, 0, 1.67), (0.025, 0.035, 0.04), shade(SKIN, 0.95), subdiv=1, rough=0)
    k.ico((0, -0.035, 1.71), (0.15, 0.13, 0.13), hexc('#5b3a1e'), subdiv=1, rough=0.05)
    return k.build('angler_0', ao=0.12)

HATS = ['bucket', 'souwester', 'straw', 'cap', 'captain', 'beanie', 'tricorn', 'crown', 'helm', 'halo']

def hat(i):
    """The hats from the armour sets' looks, in place on an angler's head. Regions: D hat, E trim."""
    name = HATS[i]
    k = Kit(90 + i)
    H, T = region('D'), region('E')
    z = 1.76
    if name == 'bucket':
        k.cyl((0, 0, z - 0.01), (0, 0, z + 0.03), 0.27, 0.22, H, sides=12)
        k.cyl((0, 0, z + 0.02), (0, 0, z + 0.16), 0.18, 0.15, H, sides=12)
        k.cyl((0, 0, z + 0.03), (0, 0, z + 0.06), 0.183, 0.18, T, sides=12)
    elif name == 'souwester':
        k.cyl((0, -0.03, z - 0.04), (0, -0.03, z + 0.02), 0.32, 0.22, H, sides=12)
        k.ico((0, 0, z + 0.06), (0.17, 0.17, 0.13), H, subdiv=1, rough=0.02)
    elif name == 'straw':
        k.cyl((0, 0, z), (0, 0, z + 0.025), 0.4, 0.37, H, sides=14)
        k.cyl((0, 0, z + 0.02), (0, 0, z + 0.17), 0.17, 0.12, H, sides=12)
        k.cyl((0, 0, z + 0.03), (0, 0, z + 0.07), 0.172, 0.16, T, sides=12)
    elif name in ('cap', 'captain'):
        if name == 'captain':
            k.cyl((0, 0, z), (0, 0, z + 0.13), 0.165, 0.19, H, sides=12)
            k.cyl((0, 0, z + 0.13), (0, 0, z + 0.15), 0.19, 0.19, H, sides=12)
            k.box((0, 0.17, z + 0.075), (0.06, 0.02, 0.05), T)
        else:
            k.ico((0, 0, z + 0.02), (0.165, 0.17, 0.11), H, subdiv=1, rough=0)
            k.ico((0, 0, z + 0.125), 0.02, T, subdiv=1, rough=0)
        k.box((0, 0.2, z + 0.01), (0.26, 0.15, 0.025), hexc('#1b1b1b') if name == 'captain' else H)
    elif name == 'beanie':
        k.ico((0, 0, z + 0.02), (0.165, 0.165, 0.15), H, subdiv=2, rough=0.03)
        k.cyl((0, 0, z - 0.02), (0, 0, z + 0.05), 0.172, 0.17, region('D', 0.85), sides=12)
        k.ico((0, 0, z + 0.18), 0.05, T, subdiv=1, rough=0.2)
    elif name == 'tricorn':
        k.cyl((0, 0, z), (0, 0, z + 0.13), 0.33, 0.12, H, sides=3)
        k.cyl((0, 0, z + 0.07), (0, 0, z + 0.09), 0.25, 0.2, T, sides=3)
    elif name == 'crown':
        k.cyl((0, 0, z), (0, 0, z + 0.1), 0.17, 0.19, H, sides=10)
        for j in range(5):
            a = j / 5 * math.tau
            p = Vector((math.cos(a) * 0.175, math.sin(a) * 0.175, z + 0.09))
            k.cyl(p, p + Vector((0, 0, 0.11)), 0.035, 0.0, H, sides=4)
            k.ico(Vector((math.cos(a + 0.6) * 0.185, math.sin(a + 0.6) * 0.185, z + 0.05)), 0.022, T, subdiv=1, rough=0)
    elif name == 'helm':
        k.ico((0, 0, z - 0.04), (0.2, 0.2, 0.2), H, subdiv=2, rough=0.01)
        k.box((0, 0.17, z - 0.06), (0.17, 0.05, 0.12), hexc('#78c8dc'))
        k.cyl((0, 0, z + 0.14), (0, -0.05, z + 0.26), 0.03, 0.06, T, sides=6)
    else:  # halo
        pts = [(0.19 * math.cos(a), 0.19 * math.sin(a), z + 0.2) for a in [j / 16 * math.tau for j in range(17)]]
        k.tube(pts, 0.025, 0.025, H, sides=5, cap=False)
    return k.build(f'hat_{name}', ao=0)

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

        k.loft(sections, skin, sides=12)
        k.fin([(0, 4.5, 4.4), (0, -1.5, 11.5), (0, -3.5, 4.0)], 0.5, dark)  # dorsal
        k.fin([(0, -14.5, 0.6), (0, -21.5, 8.5), (0, -19.5, 0.8), (0, -21, -6.5), (0, -15.5, -0.2)], 0.4, dark)  # tail
        for sx in (-1, 1):
            k.fin([(sx * 4.6, 7, -1.2), (sx * 11, 1.5, -3.2), (sx * 4.6, 3.5, -1.6)], 0.35, dark)
            for g in range(5):
                k.box((sx * 4.3, 8.4 - g * 0.55, 1.0), (0.08, 0.12, 1.6), shade(c, 0.45))
            k.ico((sx * 2.9, 12.0, 2.3), 0.38, hexc('#0b0b0b'), subdiv=1, rough=0)
        # The open mouth under the snout, with rows of teeth.
        k.ico((0, 13.6, -1.0), (2.6, 2.2, 1.0), hexc('#6a040f'), subdiv=1, rough=0.05)
        for j in range(11):
            a = (j / 10 - 0.5) * 2.4
            x, y = math.sin(a) * 2.4, 13.4 + math.cos(a) * 1.9
            k.cyl((x, y, -0.2), (x, y, -1.1), 0.22, 0.0, hexc('#f8f9fa'), sides=4)
            k.cyl((x * 0.9, y - 0.3, -2.0), (x * 0.9, y - 0.3, -1.2), 0.2, 0.0, hexc('#f8f9fa'), sides=4)
        k.fin([(0, -4, -2.6), (0, -7.5, -5.0), (0, -7, -2.4)], 0.25, dark)  # anal fin
    elif name == 'serpent':
        # Coils of green scale arching out of the water, spines along their backs, the head reared up.
        gold = hexc('#e9c46a')
        for j in range(4):
            y0 = -26 + j * 9.5
            h = 6 + j * 1.2
            pts = bezier((0, y0, -2.5), (0, y0 + 4.5, h * 1.9), (0, y0 + 9, -2.5), 14)
            r = 2.2 - j * 0.12
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

        k.loft(sections, skin, sides=12)
        for sx in (-1, 1):
            # Broad flukes and long flippers, crusted with a few barnacles.
            k.fin([(0, -21.5, 0.3), (sx * 3, -23, 0.4), (sx * 8.5, -27.5, 0.9), (sx * 9.5, -25.5, 0.8), (sx * 6, -22.8, 0.5), (sx * 1.2, -20.8, 0.3)], 0.45, pale)
            k.fin([(sx * 6.4, 8, -1.4), (sx * 9.5, 5.5, -2.6), (sx * 13.5, -1.5, -4.0), (sx * 12.8, -2.8, -3.9), (sx * 8.5, 1.5, -2.6), (sx * 6.2, 4, -1.8)], 0.5, pale)
            for b in range(3):
                k.ico((sx * (9 + b * 1.4), 3.0 - b * 1.6, -2.6 - b * 0.4), 0.32, hexc('#ced4da'), subdiv=1, rough=0.3)
            k.ico((sx * 5.1, 15.5, 1.6), 0.45, hexc('#1d3557'), subdiv=1, rough=0)
        k.box((0, 9, 4.9), (0.9, 0.4, 0.15), shade(pale, 0.7))  # the blowhole
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
    """What seizes the rail, for the rail on side (+1 / -1 along Y)."""
    k = Kit(700 + BOSSES.index(name) * 2 + (side > 0))
    c = hexc(BOSS_COLOURS[name])
    D = 1.6
    path = bezier((0, side * 4.5, -1.5), (0, side * 1.5, D + 5.5), (0, -side * 2.2, D + 0.3), 12)
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
        # Snapping jaws on the end of a thick, grey body.
        k.tube(path[:-2], 1.0, 0.85, c, sides=8)
        e = Vector(path[-1])
        k.ico(e + Vector((0, 0, 0.5)), (1.0, 1.3, 0.75), c, subdiv=2, rough=0.04)
        k.ico(e + Vector((0, -side * 0.6, 0.35)), (0.75, 0.6, 0.35), hexc('#6a040f'), subdiv=1, rough=0)
        for j in range(7):
            x = -0.6 + j * 0.2
            k.cyl((x, e.y - side * 0.9, e.z + 0.75), (x, e.y - side * 0.9, e.z + 0.25), 0.1, 0.0, hexc('#f8f9fa'), sides=4)
        k.ico(e + Vector((0.75, side * 0.2, 0.95)), 0.12, hexc('#0b0b0b'), subdiv=1, rough=0)
        k.ico(e + Vector((-0.75, side * 0.2, 0.95)), 0.12, hexc('#0b0b0b'), subdiv=1, rough=0)
    else:  # a spectral flipper crusted with barnacles
        k.tube(path, 0.9, 0.35, c, sides=8)
        for s in range(1, len(path) - 1):
            p = Vector(path[s])
            k.ico(p + Vector((k.rnd.uniform(-0.5, 0.5), 0, 0.6)), k.rnd.uniform(0.15, 0.3), hexc('#ced4da'), subdiv=1, rough=0.3)
    return k.build(f'grab_{name}_{"p" if side > 0 else "n"}', ao=0.1)

def boss_part(i):
    """All the boss parts in one file: boss_*, hump_*, grab_*_p and grab_*_n."""
    n = len(BOSSES)
    name = BOSSES[i % n]
    kind = i // n
    if kind == 0:
        return boss_body(name)
    if kind == 1:
        return boss_hump(name)
    return boss_grab(name, 1 if kind == 2 else -1)

# ---- fish ----------------------------------------------------------------------------------------------------
# One body per family, head towards +Y, about a metre long (the game scales each catch by its weight).
# Regions: A body, B belly, C fins; the game paints each species in its own colours.

FISH = ['carp', 'panfish', 'perch', 'whitefish', 'catfish', 'trout', 'bass', 'ancient', 'pike', 'sea', 'boot', 'treasure']
FISH_SHAPES = {
    'carp': dict(h=0.2, w=0.1, nose=0.42, tail=-0.38, dorsal='long', barbels=True),
    'panfish': dict(h=0.27, w=0.075, nose=0.36, tail=-0.32, dorsal='spiny'),
    'perch': dict(h=0.18, w=0.085, nose=0.42, tail=-0.38, dorsal='double', stripes=True),
    'whitefish': dict(h=0.15, w=0.075, nose=0.45, tail=-0.4, dorsal='small', adipose=True),
    'catfish': dict(h=0.13, w=0.13, nose=0.45, tail=-0.42, dorsal='small', whiskers=True, adipose=True),
    'trout': dict(h=0.15, w=0.075, nose=0.45, tail=-0.4, dorsal='small', adipose=True, spots=True),
    'bass': dict(h=0.21, w=0.1, nose=0.43, tail=-0.38, dorsal='double', bigmouth=True),
    'ancient': dict(h=0.12, w=0.1, nose=0.56, tail=-0.46, dorsal='back', snout=True, scutes=True),
    'pike': dict(h=0.11, w=0.07, nose=0.56, tail=-0.46, dorsal='back', duckbill=True),
    'sea': dict(h=0.17, w=0.09, nose=0.46, tail=-0.4, dorsal='double', forked=True, finlets=True),
}

def fish(i):
    name = FISH[i]
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
    p = FISH_SHAPES[name]
    H, W, nose, tail = p['h'], p['w'], p['nose'], p['tail']
    L = nose - tail
    hprof = [0.2, 0.45, 0.82, 1.0, 0.9, 0.62, 0.18]
    wprof = [0.28, 0.5, 0.85, 1.0, 0.9, 0.62, 0.16]
    ts = [0, 0.14, 0.34, 0.55, 0.75, 0.9, 1.0]
    sections = []
    for t, hp, wp in zip(ts, hprof, wprof):
        y = tail + L * t
        hh, ww = H * hp, W * wp
        if p.get('duckbill') and t > 0.85:
            hh *= 0.55
            ww *= 1.25
        if p.get('snout') and t > 0.85:
            hh *= 0.6
            ww *= 0.6
        sections.append((y, ww, hh, -H * 0.08 * math.sin(t * math.pi)))
    if p.get('snout'):
        sections.append((nose + 0.12, W * 0.08, H * 0.08, -H * 0.05))

    def skin(c, n):
        if c.z < -H * 0.3 or n.z < -0.55:
            return region('B')
        if p.get('stripes') and int((c.y - tail) / L * 7) % 2 == 1 and c.z > -H * 0.15:
            return region('A', 0.68)
        return region('A', 0.85 if n.z > 0.6 else 1.0)

    k.loft(sections, skin, sides=10)
    fin = region('C')
    # Tail.
    ty = tail - 0.01
    if p.get('forked'):
        k.fin([(0, ty + 0.03, 0), (0, ty - 0.2, H * 1.25), (0, ty - 0.12, 0), (0, ty - 0.2, -H * 1.25)], 0.012, fin)
    elif name == 'ancient':
        k.fin([(0, ty + 0.03, 0), (0, ty - 0.24, H * 1.4), (0, ty - 0.12, -H * 0.2), (0, ty - 0.1, -H * 0.8)], 0.012, fin)
    else:
        k.fin([(0, ty + 0.03, 0), (0, ty - 0.17, H * 1.05), (0, ty - 0.13, 0), (0, ty - 0.17, -H * 1.05)], 0.012, fin)
    # Dorsal fins.
    top = lambda y: H * 0.92
    d = p['dorsal']
    if d == 'long':
        k.fin([(0, tail + L * 0.6, top(0) * 0.9), (0, tail + L * 0.55, top(0) * 1.55), (0, tail + L * 0.25, top(0) * 1.1), (0, tail + L * 0.22, top(0) * 0.55)], 0.01, fin)
    elif d == 'spiny':
        k.fin([(0, tail + L * 0.7, top(0) * 0.9), (0, tail + L * 0.6, top(0) * 1.5), (0, tail + L * 0.45, top(0) * 1.35), (0, tail + L * 0.3, top(0) * 1.45), (0, tail + L * 0.22, top(0) * 0.6)], 0.01, fin)
    elif d == 'double':
        k.fin([(0, tail + L * 0.72, top(0) * 0.9), (0, tail + L * 0.62, top(0) * 1.6), (0, tail + L * 0.47, top(0) * 0.95)], 0.01, fin)
        k.fin([(0, tail + L * 0.45, top(0) * 0.85), (0, tail + L * 0.38, top(0) * 1.4), (0, tail + L * 0.25, top(0) * 0.65)], 0.01, fin)
    elif d == 'back':
        k.fin([(0, tail + L * 0.32, top(0) * 0.8), (0, tail + L * 0.26, top(0) * 1.6), (0, tail + L * 0.12, top(0) * 0.55)], 0.01, fin)
    else:
        k.fin([(0, tail + L * 0.62, top(0) * 0.9), (0, tail + L * 0.55, top(0) * 1.55), (0, tail + L * 0.42, top(0) * 0.85)], 0.01, fin)
    if p.get('adipose'):
        k.fin([(0, tail + L * 0.25, H * 0.5), (0, tail + L * 0.21, H * 0.75), (0, tail + L * 0.16, H * 0.4)], 0.008, fin)
    if p.get('finlets'):
        for j in range(4):
            y = tail + L * (0.08 + j * 0.05)
            k.fin([(0, y + 0.015, H * 0.3), (0, y, H * 0.45), (0, y - 0.015, H * 0.28)], 0.006, fin)
    # Anal fin, and pectoral and pelvic fins in pairs.
    k.fin([(0, tail + L * 0.3, -H * 0.6), (0, tail + L * 0.25, -H * 1.15), (0, tail + L * 0.15, -H * 0.45)], 0.01, fin)
    for s in (-1, 1):
        k.fin([(s * W * 0.8, tail + L * 0.72, -H * 0.2), (s * W * 2.4, tail + L * 0.58, -H * 0.55), (s * W * 0.9, tail + L * 0.6, -H * 0.35)], 0.008, fin)
        k.fin([(s * W * 0.5, tail + L * 0.5, -H * 0.75), (s * W * 1.4, tail + L * 0.4, -H * 1.15), (s * W * 0.5, tail + L * 0.42, -H * 0.8)], 0.008, fin)
    # Eyes, mouth, and extras.
    ey = nose - L * 0.13 if not p.get('snout') else nose - L * 0.22
    for s in (-1, 1):
        k.ico((s * W * 0.68, ey, H * 0.22), 0.026, hexc('#f1f1f1'), subdiv=1, rough=0)
        k.ico((s * W * 0.75, ey + 0.004, H * 0.22), 0.017, hexc('#111111'), subdiv=1, rough=0)
    mouth = 0.06 if p.get('bigmouth') else 0.035
    k.box((0, nose - 0.005, -H * 0.12), (W * 0.9, 0.02, mouth * 0.5), hexc('#3a1f1f'))
    if p.get('barbels') or p.get('whiskers'):
        n = 2 if p.get('barbels') else 3
        for s in (-1, 1):
            for j in range(n):
                k.tube([(s * W * 0.4, nose - 0.02, -H * 0.15 - j * 0.015), (s * (W * 0.9 + 0.08 * (j + 1)), nose - 0.06 - 0.03 * j, -H * 0.4 - j * 0.03)], 0.006, 0.003, region('A', 0.6), sides=3)
    if p.get('spots'):
        for j in range(16):
            y = tail + L * k.rnd.uniform(0.38, 0.72)  # the widest part of the body, so they sit on its skin
            s = k.rnd.choice((-1, 1))
            zf = k.rnd.uniform(0.05, 0.5)
            k.ico((s * W * 0.9 * math.sqrt(max(0.0, 1 - zf * zf)), y, H * zf), 0.012, region('A', 0.35), subdiv=1, rough=0)
    if p.get('scutes'):
        for j in range(8):
            y = tail + L * (0.2 + j * 0.08)
            k.ico((0, y, H * 0.85), (0.015, 0.025, 0.02), region('B', 0.9), subdiv=1, rough=0)
    return k.build(f'fish_{name}', ao=0.15)

# ---- pets ---------------------------------------------------------------------------------------------------
# The 30 pets, in their own colours (World/PetMeshes.cs Looks): (plan, body, accent, extra, size, feature).
# Each faces +Y, standing on z = 0, about half a metre tall (s = 0.25 x size).

PETS = {
    'lilyfrog': ('frog', '#6ab04c', '#f6e58d', None, 1, None),
    'duckling': ('bird', '#ffd60a', '#f77f00', None, 0.7, None),
    'sandcrab': ('crab', '#e76f51', '#f4a261', None, 1, None),
    'snail': ('snail', '#d4c4a8', '#c08552', None, 1, None),
    'fieldmouse': ('quad', '#9e9e9e', '#ffafcc', None, 0.6, 'mouse'),
    'otterpup': ('quad', '#7b5b3a', '#c8a27c', None, 0.9, 'long'),
    'flamingo': ('bird', '#ff8fab', '#1b1b1b', None, 1.1, 'tall'),
    'hermit': ('hermit', '#e76f51', '#e9c46a', None, 1, None),
    'turtle': ('turtle', '#90a955', '#4f772d', None, 1, None),
    'kingfisher': ('bird', '#1e88e5', '#f77f00', None, 0.8, None),
    'raccoon': ('quad', '#8d8d8d', '#2b2b2b', '#e0e0e0', 1, 'mask'),
    'owl': ('bird', '#a47148', '#f1e3c8', None, 1, 'round'),
    'beaver': ('quad', '#6f4518', '#3b2410', None, 1, 'flat-tail'),
    'penguin': ('bird', '#1b1b1b', '#f1f1f1', '#f77f00', 1, 'upright'),
    'seal': ('seal', '#9aa5b1', '#6c757d', None, 1, None),
    'snowfox': ('quad', '#f1f1f1', '#cfd8dc', None, 1, 'bushy'),
    'eagle': ('bird', '#5c3d2e', '#f1f1f1', '#ffb703', 1.2, None),
    'grizzly': ('quad', '#6d4c41', '#4e342e', None, 1.3, 'bear'),
    'octopus': ('octopus', '#e56b6f', '#b5838d', None, 1, None),
    'parrot': ('bird', '#e63946', '#1e88e5', '#ffd166', 1, None),
    'dragon': ('quad', '#2a9d8f', '#e9c46a', '#e76f51', 1.1, 'wings'),
    'peacock': ('bird', '#1e88e5', '#2a9d8f', '#ffd166', 1.1, 'fan'),
    'kraken': ('octopus', '#6a0dad', '#c77dff', None, 1, None),
    'unicorn': ('quad', '#f8f9fa', '#ff8fab', '#ffd166', 1.2, 'horn'),
    'spiritkoi': ('fish', '#fdfcdc', '#f77f00', None, 1.1, None),
    'phoenix': ('bird', '#ff7b00', '#ffba08', '#d00000', 1.1, 'fan'),
    'wyrmling': ('serpent', '#3a86a8', '#90e0ef', None, 1.1, None),
    'qilin': ('quad', '#ffd166', '#e76f51', '#2a9d8f', 1.2, 'antlers'),
    'moonmoth': ('moth', '#bde0fe', '#a2d2ff', '#f1f1f1', 1, None),
    'starwhale': ('fish', '#3a0ca3', '#bde0fe', None, 1.4, 'spots'),
}
PET_IDS = list(PETS)

def pet_eyes(k, head, r, spread=0.55, up=0.3, size=0.24):
    """Big glossy eyes on the front of a round head of radius r."""
    head = Vector(head)
    for sx in (-1, 1):
        d = Vector((sx * spread, 1, up)).normalized()
        p = head + d * r * 0.86
        k.ico(p, r * size, hexc('#fdfdfd'), subdiv=1, rough=0)
        k.ico(p + d * r * size * 0.55, r * size * 0.62, hexc('#151515'), subdiv=1, rough=0)
        k.ico(p + d * r * size * 0.95 + Vector((sx * r * 0.04, 0, r * size * 0.3)), r * size * 0.2, hexc('#ffffff'), subdiv=1, rough=0)

def pet(i):
    pid = PET_IDS[i]
    plan, body_h, accent_h, extra_h, size, feature = PETS[pid]
    k = Kit(1000 + i)
    rnd = k.rnd
    body, accent = hexc(body_h), hexc(accent_h)
    extra = hexc(extra_h) if extra_h else accent
    s = 0.25 * size
    vox = s * 0.14
    if plan == 'quad':
        ln = 1.5 if feature == 'long' else 1.25 if feature == 'bear' else 1.1
        torso = Vector((0, 0, 1.08 * s))
        head = torso + Vector((0, (ln + 0.3) * s, 0.65 * s))
        hr = 0.62 * s * (1.15 if feature == 'mouse' else 1)
        legs = 0.85 * s
        k.blob([(torso, (0.78 * s, ln * s, 0.72 * s), body, 2, 0.04, 1.0),
                (head, (hr, hr * 0.95, hr * 0.92), body, 2, 0.03, 1.0),
                (torso + Vector((0, (ln - 0.2) * s, 0.35 * s)), (0.5 * s, 0.5 * s, 0.5 * s), body, 1, 0.03, 1.0)],
               vox, 0.35, lambda f: accent if (f.normal.z < -0.5 and f.calc_center_median().y > -0.2 * s and feature not in ('mask',)) else body)
        snout = head + Vector((0, hr * 0.85, -hr * 0.22))
        k.ico(snout, (hr * 0.45, hr * 0.42, hr * 0.33), accent if feature != 'bear' else shade(body, 1.25), subdiv=1, rough=0.02)
        k.ico(snout + Vector((0, hr * 0.4, hr * 0.12)), hr * 0.12, hexc('#2b2b2b'), subdiv=1, rough=0)
        pet_eyes(k, head, hr, spread=0.5, up=0.28)
        for sx in (-1, 1):
            ear = head + Vector((sx * hr * 0.55, -hr * 0.05, hr * 0.7))
            if feature in ('mouse', 'bear'):
                k.ico(ear, (hr * 0.38, hr * 0.12, hr * 0.38), body, subdiv=1, rough=0)
                k.ico(ear + Vector((0, hr * 0.06, 0)), (hr * 0.25, hr * 0.08, hr * 0.25), accent if feature == 'mouse' else shade(body, 0.8), subdiv=1, rough=0)
            elif feature != 'wings':
                k.cyl(ear, ear + Vector((sx * hr * 0.12, 0, hr * 0.55)), hr * 0.26, 0.0, extra if feature == 'mask' else body, sides=5)
        for sx in (-1, 1):
            for sy in (-1, 1):
                top = torso + Vector((sx * 0.45 * s, sy * 0.6 * ln * s, -0.2 * s))
                foot = Vector((top.x, top.y + 0.05 * s, 0.08 * s))
                k.tube([top, foot], 0.24 * s, 0.2 * s, shade(body, 0.9) if feature != 'wings' else body, sides=6)
                k.ico(foot + Vector((0, 0.07 * s, -0.02 * s)), (0.24 * s, 0.3 * s, 0.12 * s), accent if feature != 'bear' else shade(body, 0.7), subdiv=1, rough=0)
        tail_root = torso + Vector((0, -ln * s * 0.95, 0.2 * s))
        if feature == 'flat-tail':
            k.ico(tail_root + Vector((0, -0.55 * s, -0.55 * s)), (0.4 * s, 0.6 * s, 0.1 * s), accent, subdiv=1, rough=0.05)
            k.box(snout + Vector((0, hr * 0.3, -hr * 0.35)), (hr * 0.3, hr * 0.05, hr * 0.25), hexc('#fdfdfd'))
        elif feature == 'bushy':
            k.blob([(tail_root + Vector((0, -0.6 * s, 0.25 * s)), (0.38 * s, 0.75 * s, 0.38 * s), body, 1, 0.08, 1.0)], vox, 0.4,
                   lambda f: hexc('#ffffff') if f.calc_center_median().y < tail_root.y - 1.0 * s else body)
        elif feature == 'mask':
            pts = curve([tail_root, tail_root + Vector((0, -0.5 * s, 0.3 * s)), tail_root + Vector((0, -0.95 * s, 0.6 * s))], 4)
            _, faces = k.tube(pts, 0.2 * s, 0.12 * s, body, sides=7)
            for fi, f in enumerate(faces):
                if (fi // 7) % 2 == 1:
                    k.paint([f], accent)
            for sx in (-1, 1):
                k.ico(head + Vector((sx * hr * 0.42, hr * 0.62, hr * 0.28)), (hr * 0.32, hr * 0.2, hr * 0.2), accent, subdiv=1, rough=0)
            pet_eyes(k, head, hr, spread=0.5, up=0.28)
        elif feature == 'bear':
            k.ico(tail_root, 0.15 * s, body, subdiv=1, rough=0.1)
        elif feature == 'mouse':
            k.tube(curve([tail_root, tail_root + Vector((0, -0.8 * s, 0)), tail_root + Vector((0.3 * s, -1.3 * s, 0.4 * s))], 4), 0.05 * s, 0.02 * s, accent, sides=4)
        else:
            k.tube(curve([tail_root, tail_root + Vector((0, -0.6 * s, 0.35 * s)), tail_root + Vector((0, -1.0 * s, 0.25 * s))], 4), 0.14 * s, 0.05 * s,
                   extra if feature in ('horn', 'antlers') else body, sides=6)
        if feature == 'horn':
            pts = [head + Vector((0, hr * 0.35, hr * 0.75)) + Vector((0, 0.12 * s * t, 0.8 * s * t)) for t in [j / 6 for j in range(7)]]
            k.tube(pts, 0.1 * s, 0.01 * s, extra, sides=5)
            for j in range(5):
                k.fin([torso + Vector((0, (ln * 0.7 - j * 0.25) * s, 0.65 * s)), torso + Vector((0, (ln * 0.55 - j * 0.25) * s, 1.05 * s)),
                       torso + Vector((0, (ln * 0.35 - j * 0.25) * s, 0.6 * s))], 0.12 * s, accent)
        if feature == 'antlers':
            for sx in (-1, 1):
                base = head + Vector((sx * hr * 0.4, 0, hr * 0.75))
                tip = base + Vector((sx * 0.35 * s, -0.1 * s, 0.75 * s))
                k.tube([base, base + Vector((sx * 0.12 * s, 0, 0.4 * s)), tip], 0.06 * s, 0.03 * s, extra, sides=5)
                k.tube([base + Vector((sx * 0.1 * s, 0, 0.35 * s)), base + Vector((sx * 0.45 * s, 0.1 * s, 0.5 * s))], 0.04 * s, 0.02 * s, extra, sides=4)
            for j in range(4):
                k.fin([torso + Vector((0, (ln * 0.7 - j * 0.3) * s, 0.65 * s)), torso + Vector((0, (ln * 0.55 - j * 0.3) * s, 1.0 * s)),
                       torso + Vector((0, (ln * 0.4 - j * 0.3) * s, 0.6 * s))], 0.1 * s, accent)
        if feature == 'wings':
            for sx in (-1, 1):
                root = torso + Vector((sx * 0.5 * s, 0.2 * s, 0.55 * s))
                k.fin([root, root + Vector((sx * 0.9 * s, 0.3 * s, 0.9 * s)), root + Vector((sx * 1.8 * s, -0.1 * s, 0.85 * s)),
                       root + Vector((sx * 1.5 * s, -0.5 * s, 0.35 * s)), root + Vector((sx * 0.9 * s, -0.6 * s, 0.25 * s)),
                       root + Vector((sx * 0.3 * s, -0.7 * s, 0.0))], 0.05 * s, extra)
                k.cyl(head + Vector((sx * hr * 0.35, -hr * 0.2, hr * 0.7)), head + Vector((sx * hr * 0.45, -hr * 0.6, hr * 1.35)), 0.08 * s, 0.0, accent, sides=5)
            for j in range(6):
                y = (ln * 0.8 - j * 0.38) * s
                k.cyl(torso + Vector((0, y, 0.62 * s)), torso + Vector((0, y - 0.05 * s, 0.98 * s)), 0.1 * s, 0.0, accent, sides=4)
    elif plan == 'bird':
        upright, tall = feature == 'upright', feature == 'tall'
        leg_h = 2.2 * s if tall else 0.42 * s
        torso = Vector((0, 0, leg_h + 0.7 * s))
        tr = (0.62 * s, 0.6 * s, 1.05 * s) if upright else (0.62 * s, 0.9 * s, 0.62 * s)
        if upright:
            head = torso + Vector((0, 0.1 * s, 1.15 * s))
        elif tall:
            head = torso + Vector((0, 0.65 * s, 1.75 * s))
        else:
            head = torso + Vector((0, 0.7 * s, 0.75 * s))
        hr = (0.62 if feature == 'round' else 0.45) * s
        parts = [(torso, tr, body, 2, 0.03, 1.0), (head, (hr, hr, hr), body, 2, 0.03, 1.0)]
        if not tall:
            parts.append((torso + (head - torso) * 0.55, (hr * 0.85, hr * 0.85, hr * 0.85), body, 1, 0.03, 1.0))
        belly_col = accent if pid not in ('parrot', 'phoenix') else extra
        k.blob(parts, vox, 0.35, lambda f: belly_col if (f.normal.y > 0.35 and f.calc_center_median().z < head.z - hr * 0.6) else body)
        if tall:
            k.tube(curve([torso + Vector((0, 0.4 * s, 0.35 * s)), torso + Vector((0, 0.75 * s, 0.9 * s)), torso + Vector((0, 0.4 * s, 1.4 * s)), head], 4), 0.13 * s, 0.12 * s, body, sides=6)
        beak = hexc(extra_h) if (extra_h and pid not in ('parrot', 'phoenix')) else (accent if tall else hexc('#f77f00'))
        bd = Vector((0, 1, -0.6 if tall else -0.15)).normalized()
        k.cyl(head + bd * hr * 0.75, head + bd * hr * (1.55 if feature != 'round' else 1.15), hr * 0.25, 0.0, beak, sides=5)
        pet_eyes(k, head, hr, spread=0.62 if feature == 'round' else 0.55, up=0.25, size=0.3 if feature == 'round' else 0.24)
        if feature == 'round':
            for sx in (-1, 1):
                k.cyl(head + Vector((sx * hr * 0.5, 0, hr * 0.7)), head + Vector((sx * hr * 0.7, -hr * 0.1, hr * 1.25)), hr * 0.22, 0.0, body, sides=4)
        wing = shade(body, 0.82) if pid != 'parrot' else accent
        for sx in (-1, 1):
            root = torso + Vector((sx * tr[0] * 0.85, 0.15 * s, 0.15 * s))
            k.fin([root, root + Vector((sx * 0.25 * s, -0.25 * s, 0.25 * s)), root + Vector((sx * 0.3 * s, -1.15 * s, -0.05 * s)),
                   root + Vector((sx * 0.1 * s, -0.5 * s, -0.4 * s))], 0.07 * s, wing)
            foot = Vector((sx * 0.22 * s, 0.05 * s, 0.03 * s))
            k.tube([torso + Vector((sx * 0.22 * s, 0, -tr[2] * 0.8)), foot], 0.05 * s, 0.04 * s, beak, sides=4)
            for t in (-0.5, 0, 0.5):
                k.cyl(foot, foot + Vector((t * 0.15 * s, 0.22 * s, -0.01 * s)), 0.03 * s, 0.015 * s, beak, sides=3)
        if feature == 'fan':
            base = torso + Vector((0, -0.75 * s, 0.25 * s))
            for f_ in range(9):
                a = (f_ / 8 - 0.5) * 2.4
                d = Vector((math.sin(a), -0.35, math.cos(a) * 0.95 + 0.25)).normalized()
                tip = base + d * 2.1 * s
                side = d.cross(Vector((0, 1, 0))).normalized() * 0.2 * s
                k.fin([base, tip + side, tip + d * 0.15 * s, tip - side], 0.03 * s, accent if f_ % 2 == 0 else extra)
                k.ico(tip, 0.1 * s, extra if f_ % 2 == 0 else hexc('#1e3a8a'), subdiv=1, rough=0)
        else:
            base = torso + Vector((0, -tr[1] * 0.85, 0.1 * s))
            k.fin([base + Vector((0.2 * s, 0, 0)), base + Vector((0.32 * s, -0.55 * s, 0.25 * s)), base + Vector((-0.32 * s, -0.55 * s, 0.25 * s)),
                   base + Vector((-0.2 * s, 0, 0))], 0.06 * s, shade(body, 0.85))
    elif plan == 'frog':
        torso = Vector((0, 0, 0.5 * s))
        k.blob([(torso, (0.85 * s, 0.85 * s, 0.5 * s), body, 2, 0.03, 1.0),
                (torso + Vector((0, 0.4 * s, 0.15 * s)), (0.75 * s, 0.55 * s, 0.45 * s), body, 1, 0.03, 1.0)], vox, 0.35,
               lambda f: accent if f.normal.z < -0.2 or (f.normal.y > 0.6 and f.calc_center_median().z < 0.45 * s) else body)
        for sx in (-1, 1):
            bump = torso + Vector((sx * 0.38 * s, 0.5 * s, 0.5 * s))
            k.ico(bump, 0.26 * s, body, subdiv=1, rough=0)
            k.ico(bump + Vector((0, 0.12 * s, 0.08 * s)), 0.17 * s, hexc('#151515'), subdiv=1, rough=0)
            k.ico(bump + Vector((sx * 0.03 * s, 0.22 * s, 0.14 * s)), 0.05 * s, hexc('#ffffff'), subdiv=1, rough=0)
            k.ico(Vector((sx * 0.75 * s, -0.25 * s, 0.25 * s)), (0.28 * s, 0.55 * s, 0.22 * s), body, subdiv=1, rough=0.02)
            k.ico(Vector((sx * 0.85 * s, 0.25 * s, 0.07 * s)), (0.22 * s, 0.32 * s, 0.07 * s), accent, subdiv=1, rough=0)
            k.ico(Vector((sx * 0.5 * s, 0.75 * s, 0.07 * s)), (0.15 * s, 0.2 * s, 0.07 * s), accent, subdiv=1, rough=0)
        k.box(torso + Vector((0, 0.95 * s, 0.05 * s)), (0.6 * s, 0.03 * s, 0.03 * s), shade(body, 0.5))
    elif plan in ('crab', 'hermit'):
        torso = Vector((0, 0, 0.45 * s))
        k.blob([(torso, (0.9 * s, 0.65 * s, 0.38 * s), body, 2, 0.05, 1.5)], vox, 0.4,
               lambda f: shade(body, 1.15) if f.normal.z > 0.6 else body)
        for sx in (-1, 1):
            arm = torso + Vector((sx * 0.75 * s, 0.4 * s, 0.05 * s))
            claw = arm + Vector((sx * 0.35 * s, 0.45 * s, 0.15 * s))
            k.tube([torso + Vector((sx * 0.6 * s, 0.25 * s, 0)), arm, claw], 0.1 * s, 0.09 * s, body, sides=5)
            k.ico(claw, (0.28 * s, 0.35 * s, 0.22 * s), accent, subdiv=1, rough=0.05)
            k.cyl(claw + Vector((0, 0.25 * s, 0.05 * s)), claw + Vector((sx * 0.05 * s, 0.6 * s, 0.12 * s)), 0.1 * s, 0.01 * s, accent, sides=4)
            for l in range(3):
                y = (0.15 - l * 0.3) * s
                knee = torso + Vector((sx * 1.1 * s, y, 0.15 * s))
                k.tube([torso + Vector((sx * 0.7 * s, y, 0)), knee, Vector((sx * 1.3 * s, y - 0.05 * s, 0.02 * s))], 0.05 * s, 0.03 * s, body, sides=4)
            stalk = torso + Vector((sx * 0.22 * s, 0.45 * s, 0.25 * s))
            k.tube([stalk, stalk + Vector((0, 0.05 * s, 0.3 * s))], 0.04 * s, 0.04 * s, body, sides=4)
            k.ico(stalk + Vector((0, 0.08 * s, 0.38 * s)), 0.1 * s, hexc('#151515'), subdiv=1, rough=0)
        if plan == 'hermit':
            # A spiral shell on its back.
            for j in range(10):
                t = j / 9
                a = t * math.tau * 1.6
                c = Vector((math.cos(a) * 0.35 * s * (1 - t), -0.25 * s + math.sin(a) * 0.35 * s * (1 - t), 0.75 * s + t * 0.9 * s))
                k.ico(c, 0.55 * s * (1 - t * 0.75), accent if j % 2 == 0 else shade(accent, 0.85), subdiv=1, rough=0.03)
    elif plan == 'snail':
        k.blob([((0, 0.15 * s, 0.18 * s), (0.36 * s, 1.05 * s, 0.2 * s), body, 2, 0.03, 1.0),
                ((0, 0.95 * s, 0.35 * s), (0.25 * s, 0.25 * s, 0.25 * s), body, 1, 0.03, 1.0)], vox, 0.35, lambda f: body)
        for j in range(14):
            t = j / 13
            a = t * math.tau * 2.2
            c = Vector((0.05 * s, -0.2 * s + math.cos(a) * 0.38 * s * (1 - t * 0.85), 0.8 * s + math.sin(a) * 0.38 * s * (1 - t * 0.85)))
            k.ico(c + Vector((t * 0.12 * s, 0, 0)), 0.5 * s * (1 - t * 0.8), accent if j % 2 == 0 else shade(accent, 0.82), subdiv=1, rough=0.02)
        for sx in (-1, 1):
            base = Vector((sx * 0.1 * s, 1.0 * s, 0.5 * s))
            k.tube([base, base + Vector((sx * 0.05 * s, 0.1 * s, 0.4 * s))], 0.035 * s, 0.03 * s, body, sides=4)
            k.ico(base + Vector((sx * 0.05 * s, 0.12 * s, 0.45 * s)), 0.08 * s, hexc('#151515'), subdiv=1, rough=0)
    elif plan == 'turtle':
        shell = Vector((0, 0, 0.5 * s))
        k.blob([(shell, (0.95 * s, 1.05 * s, 0.55 * s), accent, 2, 0.04, 1.0)], vox * 1.2, 0.18,
               lambda f: shade(accent, 0.75) if int((f.calc_center_median().x + 5) / (0.4 * s)) % 2 == int((f.calc_center_median().y + 5) / (0.4 * s)) % 2 else accent)
        k.ico((0, 0, 0.18 * s), (0.95 * s, 1.05 * s, 0.12 * s), shade(accent, 1.25), subdiv=1, rough=0)
        head = Vector((0, 1.15 * s, 0.4 * s))
        k.ico(head, (0.3 * s, 0.38 * s, 0.3 * s), body, subdiv=2, rough=0.02)
        pet_eyes(k, head, 0.3 * s, spread=0.7, up=0.3)
        for sx in (-1, 1):
            for sy in (-1, 1):
                k.ico(Vector((sx * 0.75 * s, sy * 0.6 * s, 0.15 * s)), (0.28 * s, 0.32 * s, 0.13 * s), body, subdiv=1, rough=0.02)
        k.cyl((0, -1.0 * s, 0.25 * s), (0, -1.35 * s, 0.2 * s), 0.1 * s, 0.0, body, sides=5)
    elif plan == 'octopus':
        head = Vector((0, 0, 1.5 * s))
        k.blob([(head, (0.72 * s, 0.72 * s, 0.85 * s), body, 2, 0.03, 1.0), (head + Vector((0, -0.15 * s, 0.4 * s)), (0.55 * s, 0.55 * s, 0.6 * s), body, 1, 0.03, 1.0)],
               vox, 0.35, lambda f: shade(body, 0.8) if noise.noise(f.calc_center_median() * (3 / s)) > 0.35 else body)
        pet_eyes(k, head + Vector((0, 0, -0.15 * s)), 0.72 * s, spread=0.45, up=0.0, size=0.26)
        for t in range(8):
            a = t / 8 * math.tau
            d = Vector((math.cos(a), math.sin(a), 0))
            side = d.cross(Vector((0, 0, 1)))
            pts = curve([head + d * 0.4 * s + Vector((0, 0, -0.55 * s)), head + d * 0.75 * s + Vector((0, 0, -1.05 * s)),
                         head + d * 1.1 * s + Vector((0, 0, -1.3 * s)), head + d * 1.35 * s + side * 0.2 * s + Vector((0, 0, -1.0 * s))], 4)
            tentacle(k, pts, 0.16 * s, 0.03 * s, body if t % 2 == 0 else accent, accent, sides=6)
    elif plan in ('fish', 'seal'):
        seal = plan == 'seal'
        z0 = (0.55 if seal else 1.2) * s
        L = 1.4 * s
        sections = [(-L, 0.12 * s, 0.14 * s, z0), (-L * 0.6, 0.32 * s, 0.38 * s, z0), (-L * 0.1, 0.55 * s, 0.58 * s, z0),
                    (L * 0.4, 0.55 * s, 0.6 * s, z0 + 0.05 * s), (L * 0.85, 0.4 * s, 0.45 * s, z0 + 0.08 * s), (L * 1.1, 0.12 * s, 0.15 * s, z0 + 0.05 * s)]

        def skin(c, n):
            if n.z < -0.35:
                return accent
            if feature == 'spots' and n.z > 0.3 and noise.noise(c * (6 / s)) > 0.3:
                return hexc('#fdfdfd')
            if pid == 'spiritkoi' and noise.noise(c * (4 / s) + Vector((3, 3, 3))) > 0.15:
                return accent
            return body

        k.loft(sections, skin, sides=10)
        pet_eyes(k, Vector((0, L * 0.6, z0 + 0.12 * s)), 0.48 * s, spread=0.85, up=0.2, size=0.22)
        tail = Vector((0, -L, z0))
        if seal:
            for sx in (-1, 1):
                k.fin([tail, tail + Vector((sx * 0.55 * s, -0.45 * s, -0.15 * s)), tail + Vector((sx * 0.15 * s, -0.55 * s, -0.1 * s))], 0.06 * s, accent)
                k.fin([Vector((sx * 0.45 * s, L * 0.35, z0 - 0.35 * s)), Vector((sx * 0.85 * s, L * 0.1, z0 - 0.55 * s)), Vector((sx * 0.45 * s, 0, z0 - 0.4 * s))], 0.06 * s, accent)
            for sx in (-1, 1):
                for j in range(3):
                    k.tube([Vector((sx * 0.25 * s, L * 1.05, z0 - 0.05 * s + j * 0.04 * s)), Vector((sx * 0.6 * s, L * 1.0, z0 - 0.1 * s + j * 0.08 * s))], 0.01 * s, 0.006 * s, hexc('#2b2b2b'), sides=3)
        else:
            k.fin([tail + Vector((0, 0.1 * s, 0)), tail + Vector((0, -0.6 * s, 0.75 * s)), tail + Vector((0, -0.35 * s, 0)), tail + Vector((0, -0.6 * s, -0.75 * s))], 0.05 * s, accent)
            k.fin([Vector((0, L * 0.3, z0 + 0.5 * s)), Vector((0, -L * 0.1, z0 + 1.0 * s)), Vector((0, -L * 0.4, z0 + 0.35 * s))], 0.05 * s, accent)
            for sx in (-1, 1):
                k.fin([Vector((sx * 0.45 * s, L * 0.4, z0 - 0.2 * s)), Vector((sx * 0.95 * s, L * 0.1, z0 - 0.4 * s)), Vector((sx * 0.45 * s, L * 0.15, z0 - 0.35 * s))], 0.04 * s, accent)
            if pid == 'spiritkoi':
                for sx in (-1, 1):
                    k.tube([Vector((sx * 0.15 * s, L * 1.08, z0)), Vector((sx * 0.5 * s, L * 1.25, z0 - 0.3 * s))], 0.025 * s, 0.01 * s, accent, sides=3)
    elif plan == 'serpent':
        pts = [Vector((math.sin(t * 5) * 0.5 * s, s * (1.6 - t * 3.2), s * (1.1 + math.sin(t * 3 + 1) * 0.3))) for t in [j / 10 for j in range(11)]]
        pts.reverse()
        _, faces = k.tube(curve(pts, 3), 0.12 * s, 0.4 * s, body, sides=8)
        for fi, f in enumerate(faces):
            if f.normal.z < -0.3:
                k.paint([f], accent)
        head = Vector((0, 1.9 * s, 1.35 * s))
        k.blob([(head, (0.38 * s, 0.55 * s, 0.33 * s), body, 2, 0.03, 1.0)], vox, 0.4, lambda f: accent if f.normal.z < -0.4 else body)
        pet_eyes(k, head + Vector((0, 0.1 * s, 0)), 0.36 * s, spread=0.7, up=0.35)
        for sx in (-1, 1):
            k.cyl(head + Vector((sx * 0.15 * s, -0.15 * s, 0.25 * s)), head + Vector((sx * 0.25 * s, -0.55 * s, 0.6 * s)), 0.07 * s, 0.0, accent, sides=4)
            k.fin([head + Vector((sx * 0.3 * s, -0.25 * s, 0.05 * s)), head + Vector((sx * 0.75 * s, -0.55 * s, 0.25 * s)), head + Vector((sx * 0.35 * s, -0.5 * s, -0.1 * s))], 0.04 * s, accent)
    elif plan == 'moth':
        torso = Vector((0, 0, 1.2 * s))
        k.ico(torso, (0.22 * s, 0.7 * s, 0.22 * s), shade(body, 0.7), subdiv=2, rough=0.03)
        k.ico(torso + Vector((0, 0.65 * s, 0.05 * s)), 0.2 * s, shade(body, 0.8), subdiv=2, rough=0.02)
        pet_eyes(k, torso + Vector((0, 0.65 * s, 0.05 * s)), 0.2 * s, spread=0.8, up=0.2, size=0.35)
        for sx in (-1, 1):
            for (y, sz, col) in ((0.15, 1.25, body), (-0.4, 0.85, accent)):
                root = torso + Vector((sx * 0.15 * s, y * s, 0))
                k.fin([root, root + Vector((sx * sz * 0.6 * s, 0.45 * sz * s, 0.35 * s)), root + Vector((sx * sz * s, 0.1 * sz * s, 0.3 * s)),
                       root + Vector((sx * sz * 0.85 * s, -0.55 * sz * s, 0.15 * s))], 0.03 * s, col)
                k.ico(root + Vector((sx * sz * 0.6 * s, 0.05 * s, 0.26 * s)), 0.1 * s * sz, extra, subdiv=1, rough=0)
            k.tube([torso + Vector((sx * 0.08 * s, 0.75 * s, 0.15 * s)), torso + Vector((sx * 0.3 * s, 1.1 * s, 0.55 * s))], 0.02 * s, 0.015 * s, extra, sides=3)
            k.fin([torso + Vector((sx * 0.3 * s, 1.1 * s, 0.55 * s)), torso + Vector((sx * 0.45 * s, 1.0 * s, 0.75 * s)), torso + Vector((sx * 0.2 * s, 1.15 * s, 0.7 * s))], 0.02 * s, extra)
    return k.build(f'pet_{pid}', ao=0.18)

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
    'hat': (hat, len(HATS)),
    'rod': (rod, 1),
    'boss': (boss_part, len(BOSSES) * 4),
    'fish': (fish, len(FISH)),
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
