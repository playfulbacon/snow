"""Generate nine editable low-poly stick objects for Snowfield. Run with Blender --background --python."""
import math
from pathlib import Path
import random
import sys

import bpy
from mathutils import Vector


def material(name, color):
    result = bpy.data.materials.new(name)
    result.diffuse_color = (*color, 1)
    result.use_nodes = True
    shader = result.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (*color, 1)
    shader.inputs["Roughness"].default_value = 0.9
    return result


def stick(name, branches, index, materials, snowy=False, broken=False):
    rng = random.Random(3100 + index)
    vertices, faces, face_materials = [], [], []
    sides = 8
    for branch_index, (points, radii) in enumerate(branches):
        points = [Vector(point) for point in points]
        rings = []
        for ring_index, (point, radius) in enumerate(zip(points, radii)):
            before = points[max(0, ring_index - 1)]
            after = points[min(len(points) - 1, ring_index + 1)]
            tangent = (after - before).normalized()
            reference = Vector((0, 1, 0))
            if abs(tangent.dot(reference)) > 0.95:
                reference = Vector((1, 0, 0))
            x_axis = tangent.cross(reference).normalized()
            y_axis = tangent.cross(x_axis).normalized()
            ring = []
            for side in range(sides):
                angle = side * math.tau / sides + 0.08 * math.sin(ring_index * 1.3)
                radial = x_axis * math.cos(angle) + y_axis * math.sin(angle)
                vertex = point + radial * radius * rng.uniform(0.92, 1.08)
                if broken and branch_index == 0 and ring_index == len(points) - 1:
                    vertex += tangent * (0.012 * math.sin(side * 2.1) + 0.004 * math.cos(side * 3.7))
                ring.append(len(vertices))
                vertices.append(tuple(vertex))
            rings.append(ring)
        faces.append(tuple(reversed(rings[0])))
        face_materials.append(2)
        for ring_index in range(len(rings) - 1):
            midpoint = (points[ring_index] + points[ring_index + 1]) * 0.5
            for side in range(sides):
                following = (side + 1) % sides
                face = (rings[ring_index][side], rings[ring_index][following],
                        rings[ring_index + 1][following], rings[ring_index + 1][side])
                faces.append(face)
                centre = sum((Vector(vertices[v]) for v in face), Vector()) * 0.25
                radial = centre - midpoint
                if snowy and radial.y > 0.003 and (side + ring_index) % 5 != 0:
                    face_materials.append(3)
                else:
                    face_materials.append(1 if side % 3 == 0 else 0)
        faces.append(tuple(rings[-1]))
        face_materials.append(2)
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    for entry in materials:
        mesh.materials.append(entry)
    for polygon, material_index in zip(mesh.polygons, face_materials):
        polygon.material_index = material_index
        polygon.use_smooth = False
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    # Arrange for editing in Blender; the Unity prefabs discard these collection offsets.
    obj.location = ((index % 3) * 1.2, (index // 3) * 1.2, 0)
    obj["snowfield_variant"] = name
    return obj


def main():
    output = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    bpy.context.scene.unit_settings.system = "METRIC"
    bpy.context.scene.unit_settings.scale_length = 1.0
    mats = [material("Stick_Bark", (0.29, 0.19, 0.105)),
            material("Stick_BarkWarm", (0.37, 0.25, 0.145)),
            material("Stick_CutWood", (0.62, 0.44, 0.25)),
            material("Stick_Frost", (0.82, 0.90, 0.94))]
    # Branches are centre-lines in metres, each with a taper radius at every point.
    variants = [
        ("Stick_01_Twig", [
            ([(0, 0, 0), (0.008, 0, .16), (-.008, .008, .31), (.018, .01, .45)], [.016, .014, .011, .004]),
            ([(-.006, .006, .28), (.055, .004, .35), (.12, .012, .40)], [.010, .007, .003]),
            ([(.003, 0, .18), (-.052, -.015, .25)], [.008, .003]),
        ], False, False),
        ("Stick_02_Short", [
            ([(0, 0, 0), (-.008, .005, .10), (.018, -.004, .18), (.035, 0, .26)], [.013, .012, .009, .004]),
            ([(.01, 0, .16), (-.045, .012, .21)], [.007, .003]),
        ], False, False),
        ("Stick_03_Long", [
            ([(0, 0, 0), (.022, 0, .22), (-.022, .012, .45), (.008, .02, .59), (.034, .025, .70)], [.020, .019, .015, .010, .004]),
            ([(.013, .003, .27), (.075, .01, .34)], [.011, .005]),
            ([(-.01, .014, .50), (-.065, -.008, .56)], [.008, .003]),
        ], False, False),
        ("Stick_04_Forked", [
            ([(0, 0, 0), (.012, 0, .18), (0, .008, .33), (.06, .01, .45), (.145, .02, .58)], [.020, .018, .015, .011, .004]),
            ([(0, .008, .32), (-.07, -.01, .44), (-.16, -.015, .55)], [.014, .010, .004]),
        ], False, False),
        ("Stick_05_Crooked", [
            ([(0, 0, 0), (-.032, 0, .14), (.015, .012, .26), (.082, .02, .36), (.05, .015, .45), (.014, .025, .54)], [.018, .017, .014, .012, .008, .003]),
            ([(.07, .02, .34), (.15, -.005, .41)], [.009, .003]),
        ], False, False),
        ("Stick_06_Branchlet", [
            ([(0, 0, 0), (.014, 0, .22), (-.012, .015, .43), (.025, .025, .65)], [.021, .019, .014, .004]),
            ([(.012, 0, .23), (-.09, .01, .33), (-.18, .025, .41)], [.012, .009, .003]),
            ([(-.01, .015, .41), (.075, -.025, .49), (.16, -.02, .58)], [.011, .008, .003]),
            ([(.015, .02, .57), (-.04, .03, .64)], [.007, .0025]),
        ], False, False),
        ("Stick_07_Thin", [
            ([(0, 0, 0), (.008, -.005, .15), (.025, 0, .32), (.055, .012, .47), (.075, .02, .59)], [.010, .009, .007, .005, .0025]),
            ([(.03, .003, .35), (-.024, .018, .42)], [.0055, .002]),
        ], False, False),
        ("Stick_08_Broken", [
            ([(0, 0, 0), (-.008, .004, .12), (.012, .012, .25), (.025, 0, .37)], [.024, .022, .020, .016]),
            ([(.012, .01, .24), (.075, -.01, .29)], [.012, .006]),
        ], False, True),
        ("Stick_09_Snowy", [
            ([(0, 0, 0), (-.025, 0, .19), (.018, .012, .40), (.005, .02, .61)], [.020, .018, .014, .004]),
            ([(-.015, .005, .27), (-.09, .015, .36), (-.14, .02, .44)], [.011, .008, .003]),
            ([(.014, .014, .44), (.09, 0, .52), (.13, .01, .57)], [.010, .007, .003]),
        ], True, False),
    ]
    for index, (name, branches, snowy, broken) in enumerate(variants):
        stick(name, branches, index, mats, snowy=snowy, broken=broken)
    bpy.ops.object.select_all(action="SELECT")
    bpy.context.view_layer.objects.active = bpy.context.scene.objects[0]
    output.parent.mkdir(parents=True, exist_ok=True)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(output))
    print(f"Created {len(variants)} stick mesh objects in {output}")


if __name__ == "__main__":
    main()
