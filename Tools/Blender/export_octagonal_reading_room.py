"""Export the octagonal reading room for Unity.

Run from the project root (the .blend is never saved):

    blender -b Art/Blender/OctagonalReadingRoom_v1.blend --python Tools/Blender/export_octagonal_reading_room.py

Writes to Assets/Models/OctagonalReadingRoom/:
  OctagonalReadingRoom_v1.fbx  fixed room: parquet floor, 8 bookcases (with pulls), books per station, lamps, ceiling
  ReadingDesk_v1.fbx           Desk_01 with its pulls; pivot on the floor under the desk center
  ReadingStand_v1.fbx          Stand_01 riser + display panel and the empty WordAnchor; pivot at the worktop height
  OctagonalReadingRoom_v1.json materials (linear base color, roughness, metallic, emission, baked texture) and
                               measurements read from the model, for the Unity setup tool
  Textures/<material>.png      wood albedo baked from each walnut/oak material's own procedural node tree

Axes: the manifest gives positions in the Unity frame of the game (Blender +Y = station 01 = side 0 = Unity +Z,
Blender +Z = up). The FBX files keep Blender's own frame; Unity's import turns the multi-object room 180° and drops the
root transform of the single-object desk and stand, so the Unity setup tool (WordRoom/Shelf/독서실 프리팹·씬 구성)
reads each model's facing from its geometry and turns the model instance to match.

Wood: the original shaders read the per-vertex attribute TimberCoordinates (grain along its X). Each face gets UVs
(grain coordinate / GRAIN_U, cross coordinate / GRAIN_V), the cross axis being the timber axis that varies most over
the face. The albedo texture is the material's own noise → mix → color ramp, evaluated on a torus (4D noise) so it
tiles seamlessly over GRAIN_U × GRAIN_V meters. Bump, coat and the small ceiling emission are not carried over.
"""

import json
import math
import os
import re
import sys

import bpy
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(ROOT, "Assets", "Models", "OctagonalReadingRoom")
TEX = os.path.join(OUT, "Textures")

GRAIN_U = 4.0           # meters along the grain covered by one texture tile
GRAIN_V = 1.0           # meters across the grain
TEX_W, TEX_H = 512, 2048
BAKE_SAMPLES = 8

DESK = "Desk_01 | Walnut Writing Table"
DESK_PULLS = "Desk_01 | Drawer Pulls"
RISER = "Stand_01 | Low Angled Riser"
PANEL = "Stand_01 | Display Panel"
ANCHOR = "WordAnchor_01"


def safe(name):
    return re.sub(r"[^A-Za-z0-9]+", "_", name).strip("_")


def log(*a):
    print("[ReadingRoomExport]", *a)
    sys.stdout.flush()


# ---- scene preparation -----------------------------------------------------------------------------------------

def realize_meshes():
    """Applies every modifier (bevels) into the mesh data, keeping TimberCoordinates."""
    dg = bpy.context.evaluated_depsgraph_get()
    for o in list(bpy.data.objects):
        if o.type != "MESH" or not o.modifiers:
            continue
        me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
        o.modifiers.clear()
        o.data = me


def wood_materials():
    result = []
    for m in bpy.data.materials:
        if not m.node_tree:
            continue
        if any(n.type == "ATTRIBUTE" and n.attribute_name == "TimberCoordinates" for n in m.node_tree.nodes):
            result.append(m)
    return result


def add_timber_uvs(obj, wood_names):
    me = obj.data
    if "TimberCoordinates" not in me.attributes:
        return
    if not any(s.material and s.material.name in wood_names for s in obj.material_slots):
        return
    attr = me.attributes["TimberCoordinates"]
    if attr.domain != "POINT":
        log("skip UVs (TimberCoordinates not per point):", obj.name)
        return
    tc = [d.vector.copy() for d in attr.data]
    uv = me.uv_layers.new(name="UVMap") if "UVMap" not in me.uv_layers else me.uv_layers["UVMap"]
    loops = me.loops
    for p in me.polygons:
        idx = [loops[li].vertex_index for li in p.loop_indices]
        ys = [tc[i].y for i in idx]
        zs = [tc[i].z for i in idx]
        axis = 1 if (max(ys) - min(ys)) >= (max(zs) - min(zs)) else 2
        for li, vi in zip(p.loop_indices, idx):
            t = tc[vi]
            uv.data[li].uv = (t.x / GRAIN_U, t[axis] / GRAIN_V)


def select_only(objs, active=None):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or (objs[0] if objs else None)


def join(objs, name):
    """Joins `objs` into one object named `name` (world placement kept); returns it."""
    objs = [o for o in objs if o.type == "MESH"]
    target = objs[0]
    for o in objs:                                   # parents may be joined away: keep world transforms
        mw = o.matrix_world.copy()
        o.parent = None
        o.matrix_world = mw
    if len(objs) > 1:
        select_only(objs, target)
        with bpy.context.temp_override(active_object=target, object=target, selected_objects=objs,
                                       selected_editable_objects=objs):
            bpy.ops.object.join()
    target.name = name
    target.data.name = name
    return target


def bake_origin(obj, origin):
    """Moves the object's world origin to `origin` (mesh stays in place) with no rotation or scale."""
    mw = obj.matrix_world.copy()
    obj.data.transform(Matrix.Translation(-origin) @ mw)
    obj.matrix_world = Matrix.Identity(4)


# ---- wood textures -------------------------------------------------------------------------------------------

def bake_wood(materials):
    """Bakes each wood material's ramp color into a seamless GRAIN_U × GRAIN_V tile (TEX_W × TEX_H)."""
    os.makedirs(TEX, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = BAKE_SAMPLES
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "None"
    scene.view_settings.exposure = 0.0
    scene.view_settings.gamma = 1.0

    bpy.ops.mesh.primitive_plane_add(size=1.0)
    plane = bpy.context.active_object
    plane.name = "__bake_plane"
    baked = {}
    for m in materials:
        mat = m.copy()
        mat.name = "__bake_" + m.name
        nt = mat.node_tree
        nodes, links = nt.nodes, nt.links
        ramp = next(n for n in nodes if n.type == "VALTORGB")
        uvn = nodes.new("ShaderNodeTexCoord")
        sep = nodes.new("ShaderNodeSeparateXYZ")
        links.new(uvn.outputs["UV"], sep.inputs["Vector"])
        for vm in [n for n in nodes if n.type == "VECT_MATH" and n.operation == "MULTIPLY"]:
            s = vm.inputs[1].default_value
            a = s[0] * GRAIN_U / (2.0 * math.pi)          # along the grain
            b = 0.5 * (s[1] + s[2]) * GRAIN_V / (2.0 * math.pi)
            for link in list(vm.outputs["Vector"].links):
                noise = link.to_node
                if noise.type != "TEX_NOISE":
                    continue
                noise.noise_dimensions = "4D"
                comb = nodes.new("ShaderNodeCombineXYZ")
                parts = []
                for comp, radius, func in (("X", a, "COSINE"), ("X", a, "SINE"), ("Y", b, "COSINE"), ("Y", b, "SINE")):
                    ang = nodes.new("ShaderNodeMath")
                    ang.operation = "MULTIPLY"
                    ang.inputs[1].default_value = 2.0 * math.pi
                    links.new(sep.outputs[comp], ang.inputs[0])
                    trig = nodes.new("ShaderNodeMath")
                    trig.operation = func
                    links.new(ang.outputs[0], trig.inputs[0])
                    mul = nodes.new("ShaderNodeMath")
                    mul.operation = "MULTIPLY"
                    mul.inputs[1].default_value = radius
                    links.new(trig.outputs[0], mul.inputs[0])
                    parts.append(mul)
                links.new(parts[0].outputs[0], comb.inputs["X"])
                links.new(parts[1].outputs[0], comb.inputs["Y"])
                links.new(parts[2].outputs[0], comb.inputs["Z"])
                links.new(comb.outputs["Vector"], noise.inputs["Vector"])
                links.new(parts[3].outputs[0], noise.inputs["W"])
        out = next(n for n in nodes if n.type == "OUTPUT_MATERIAL")
        emit = nodes.new("ShaderNodeEmission")
        links.new(ramp.outputs["Color"], emit.inputs["Color"])
        links.new(emit.outputs["Emission"], out.inputs["Surface"])
        img = bpy.data.images.new("__bake_" + safe(m.name), TEX_W, TEX_H, alpha=False, float_buffer=False)
        img.colorspace_settings.name = "sRGB"
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = img
        for n in nodes:
            n.select = False
        tex.select = True
        nodes.active = tex
        plane.data.materials.clear()
        plane.data.materials.append(mat)
        select_only([plane])
        with bpy.context.temp_override(active_object=plane, object=plane, selected_objects=[plane]):
            bpy.ops.object.bake(type="EMIT", margin=0, use_clear=True)
        path = os.path.join(TEX, safe(m.name) + ".png")
        img.filepath_raw = path
        img.file_format = "PNG"
        img.save()
        baked[m.name] = os.path.basename(path)
        log("baked", m.name, "->", path)
    bpy.data.objects.remove(plane, do_unlink=True)
    return baked


# ---- material and measurement manifest ------------------------------------------------------------------------

def principled(m):
    if not m.node_tree:
        return None
    return next((n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None)


def ramp_mid(m):
    r = next((n for n in m.node_tree.nodes if n.type == "VALTORGB"), None) if m.node_tree else None
    if r is None:
        return None
    els = r.color_ramp.elements
    return list(els[len(els) // 2].color)[:3]


def material_entry(m, baked):
    b = principled(m)
    entry = {"source": m.name, "name": safe(m.name), "baseColor": [0.8, 0.8, 0.8], "roughness": 0.5, "metallic": 0.0,
             "emission": [0.0, 0.0, 0.0], "emissionStrength": 0.0, "texture": baked.get(m.name, "")}
    if b is None:
        return entry
    bc = b.inputs["Base Color"]
    color = list(bc.default_value)[:3]
    if bc.is_linked:
        color = ramp_mid(m) or color
    entry["baseColor"] = color
    entry["roughness"] = b.inputs["Roughness"].default_value
    entry["metallic"] = b.inputs["Metallic"].default_value
    strength = b.inputs["Emission Strength"].default_value
    if strength > 0.5:                               # the lamp glass; the ceiling's faint self-light is dropped
        entry["emission"] = list(b.inputs["Emission Color"].default_value)[:3]
        entry["emissionStrength"] = strength
    return entry


def world_bounds(objs):
    pts = [o.matrix_world @ Vector(c) for o in objs for c in o.bound_box]
    return [min(p[i] for p in pts) for i in range(3)], [max(p[i] for p in pts) for i in range(3)]


def to_unity(v):
    """Blender world/local vector → Unity axes (x, z-up → y, y → z)."""
    return [v[0], v[2], v[1]]


def point(v):
    u = to_unity(v)
    return {"x": u[0], "y": u[1], "z": u[2]}


def measurements():
    o = bpy.data.objects
    desk = o[DESK]
    worktop = float(desk.get("Worktop height (m)", 0.75))
    dmin, dmax = world_bounds([desk])
    riser = o[RISER]
    panel = o[PANEL]
    smin, smax = world_bounds([riser, panel])
    anchor = o[ANCHOR]
    stand_origin = Vector((riser.matrix_world.translation.x, riser.matrix_world.translation.y, worktop))
    a = anchor.matrix_world.translation - stand_origin
    case = o["Bookcase_01 | Crown, Flutes & Paneled Base"]
    cmin, _ = world_bounds([case])
    floor_objs = [x for x in bpy.data.collections["01 | Parquet Floor"].all_objects if x.type == "MESH"]
    fmin, fmax = world_bounds(floor_objs)
    lamps = [x for x in bpy.data.collections["07 | Brass Lamps"].all_objects if x.type == "MESH"]
    lamp_pos = []
    for x in lamps:
        lo, hi = world_bounds([x])
        lamp_pos.append(point([(lo[i] + hi[i]) * 0.5 for i in range(3)]))
    reading = [point(list(x.matrix_world.translation)) for x in bpy.data.objects if x.type == "LIGHT" and x.name.startswith("Reading light")]
    return {
        "worktopHeight": worktop,
        "deskRadius": desk.matrix_world.translation.y,
        "deskWidth": dmax[0] - dmin[0],
        "deskDepth": dmax[1] - dmin[1],
        "deskHeight": dmax[2] - dmin[2],
        "standOffsetOnDesk": stand_origin.y - desk.matrix_world.translation.y,   # toward the bookcase
        "standWidth": smax[0] - smin[0],
        "standDepth": smax[1] - smin[1],
        "standHeight": smax[2] - worktop,
        "panelTiltDegrees": float(panel.get("Tilt (degrees)", 32.0)),
        "textClearWidth": float(panel.get("Text clear area (m)", [0.6, 0.258])[0]),
        "textClearHeight": float(panel.get("Text clear area (m)", [0.6, 0.258])[1]),
        "wordAnchor": point(list(a)),
        "bookcaseInnerApothem": cmin[1],
        "floorTop": fmax[2],
        "floorHalfExtent": max(fmax[0], fmax[1]),
        "ceilingHeight": 3.0,
        "lamps": lamp_pos,
        "readingLights": reading,
    }


# ---- export ----------------------------------------------------------------------------------------------------

def export(objs, filename):
    path = os.path.join(OUT, filename)
    select_only(objs)
    with bpy.context.temp_override(selected_objects=objs, active_object=objs[0]):
        bpy.ops.export_scene.fbx(
            filepath=path, use_selection=True, object_types={"MESH", "EMPTY"},
            apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", global_scale=1.0,
            axis_forward="Z", axis_up="Y", bake_space_transform=True,
            use_mesh_modifiers=True, mesh_smooth_type="FACE", use_tspace=False,
            add_leaf_bones=False, bake_anim=False, path_mode="STRIP", embed_textures=False)
    log("exported", path, "objects", len(objs))


def main():
    os.makedirs(OUT, exist_ok=True)
    meta = measurements()                            # before anything moves
    realize_meshes()
    wood = wood_materials()
    wood_names = {m.name for m in wood}
    for o in bpy.data.objects:
        if o.type == "MESH":
            add_timber_uvs(o, wood_names)
    for m in bpy.data.materials:                     # FBX/Unity-friendly names
        if not m.name.startswith("__"):
            m["source_name"] = m.name

    baked = bake_wood(wood)
    used = {}
    for o in bpy.data.objects:
        if o.type == "MESH":
            for s in o.material_slots:
                if s.material is not None and not s.material.name.startswith("__"):
                    used[s.material.name] = s.material
    manifest_materials = [material_entry(m, baked) for m in sorted(used.values(), key=lambda x: x.name)]
    for m in used.values():
        m.name = safe(m.name)

    col = bpy.data.collections
    o = bpy.data.objects
    # Desk and stand first: they are taken out of the room and exported alone.
    anchor = o[ANCHOR]
    anchor_world = anchor.matrix_world.copy()
    riser = o[RISER]
    stand_origin = Vector((riser.matrix_world.translation.x, riser.matrix_world.translation.y, meta["worktopHeight"]))
    stand = join([riser, o[PANEL]], "ReadingStand")
    bake_origin(stand, stand_origin)
    anchor.parent = None
    anchor.matrix_world = Matrix.Translation(-stand_origin) @ anchor_world
    anchor.name = "WordAnchor"
    anchor.parent = stand
    anchor.matrix_parent_inverse = Matrix.Identity(4)
    desk_origin = o[DESK].matrix_world.translation.copy()
    desk = join([o[DESK], o[DESK_PULLS]], "ReadingDesk")
    bake_origin(desk, desk_origin)

    floor = join(list(col["01 | Parquet Floor"].all_objects), "Floor")
    cases = []
    for i in range(1, 9):
        cases.append(join([o[f"Bookcase_{i:02d} | Crown, Flutes & Paneled Base"], o[f"Bookcase_{i:02d} | Aged Brass Pulls"]], f"Bookcase_{i:02d}"))
    books = [join(list(col[f"Station {i:02d} | Shelf Volumes"].all_objects), f"Books_{i:02d}") for i in range(1, 9)]
    lamps = [join([o[f"Lamp_{i:02d} | Brass Library Light"]], f"Lamp_{i:02d}") for i in range(1, 9)]
    ceiling = join(list(col["10 | Ceiling Underside"].all_objects), "Ceiling")

    export([stand, anchor], "ReadingStand_v1.fbx")
    export([desk], "ReadingDesk_v1.fbx")
    export([floor] + cases + books + lamps + [ceiling], "OctagonalReadingRoom_v1.fbx")

    manifest = {"source": "Art/Blender/OctagonalReadingRoom_v1.blend", "grainU": GRAIN_U, "grainV": GRAIN_V,
                "materials": manifest_materials, "measurements": meta}
    with open(os.path.join(OUT, "OctagonalReadingRoom_v1.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    log("measurements", json.dumps(meta))
    log("done")


main()
