"""Compares two compiled game models array by array, then steps both and compares the motion."""
import ctypes as C, sys, numpy as np
P = C.CDLL(r"C:\Program Files (x86)\Steam\steamapps\common\Jibbers Playtest\Jibbers_Data\Plugins\x86_64\pcore.dll")
P.mj_versionString.restype = C.c_char_p
assert P.mj_versionString() == b"3.3.6", "This model-layout probe supports MuJoCo 3.3.6 only"
P.mj_loadModel.restype = C.c_void_p; P.mj_loadModel.argtypes = [C.c_char_p, C.c_void_p]
P.mj_makeData.restype = C.c_void_p; P.mj_makeData.argtypes = [C.c_void_p]
P.mj_step.argtypes = [C.c_void_p, C.c_void_p]
I = lambda m, o: C.c_int.from_address(m + o).value
def arr(m, off, n, dt):
    p = C.c_void_p.from_address(m + off).value
    return np.ctypeslib.as_array((C.c_double if dt == "d" else C.c_float if dt == "f" else C.c_int).from_address(p) if False else C.cast(p, C.POINTER(C.c_double if dt == "d" else C.c_float if dt == "f" else C.c_int)), shape=(n,)).copy()
a, b = (P.mj_loadModel(f.encode(), None) for f in sys.argv[1:3])
assert a and b, "Could not load both compiled models"
sizes = {k: (I(a, o), I(b, o)) for k, o in [("nbody", 16), ("ngeom", 60), ("nmesh", 116), ("nmeshvert", 120), ("nmeshnormal", 124), ("nmeshface", 132), ("nmeshgraph", 136), ("nbvh", 20), ("nhfielddata", 180)]}
print(sizes)
assert all(x == y for x, y in sizes.values()), "Model sizes differ"
nb, ng, nm = sizes["nbody"][0], sizes["ngeom"][0], sizes["nmesh"][0]
checks = [("body_mass", 1504, nb, "d"), ("body_inertia", 1520, 3 * nb, "d"), ("body_ipos", 1488, 3 * nb, "d"), ("geom_size", 2016, 3 * ng, "d"), ("geom_aabb", 2024, 6 * ng, "d"),
          ("geom_rbound", 2032, ng, "d"), ("geom_pos", 2040, 3 * ng, "d"), ("geom_quat", 2048, 4 * ng, "d"), ("geom_dataid", 1944, ng, "i"), ("mesh_vertnum", 2992, nm, "i"),
          ("mesh_vertadr", 2984, nm, "i"), ("mesh_pos", 3152, 3 * nm, "d"), ("mesh_quat", 3160, 4 * nm, "d"), ("mesh_vert", 3088, 3 * sizes["nmeshvert"][0], "f"),
          ("mesh_face", 3112, 3 * sizes["nmeshface"][0], "i"), ("mesh_graph", 3136, sizes["nmeshgraph"][0], "i"), ("bvh_aabb", 1624, 6 * sizes["nbvh"][0], "d")]
ok = True
for name, off, n, dt in checks:
    x, y = arr(a, off, n, dt), arr(b, off, n, dt)
    same = np.array_equal(x.view(np.uint8) if True else x, y.view(np.uint8))
    ok &= same
    print(f"  {name:13s} {'identical' if same else f'DIFFERENT ({int((x != y).sum())} of {n})'}")
da, db = P.mj_makeData(a), P.mj_makeData(b)
for _ in range(600):
    P.mj_step(a, da); P.mj_step(b, db)
qa, qb = arr(da, 161792, 3 * nb, "d"), arr(db, 161792, 3 * nb, "d")   # xpos
print("body positions after 600 steps identical:", np.array_equal(qa, qb), "| rider moved:", float(np.abs(qa).max()))
print("ALL IDENTICAL" if ok and np.array_equal(qa, qb) else "MISMATCH")

assert ok and np.array_equal(qa, qb), "Physics comparison failed"
