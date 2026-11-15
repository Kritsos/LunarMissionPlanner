import datetime as dt
import hashlib
import json
import math
import os
import struct
import sys
from pathlib import Path

import spiceypy as spice

MAGIC = b"LUNEPH1\0"
VERSION = 1
RECORD = struct.Struct("<qd6d6d4d")
HEADER = struct.Struct("<8sIIQ")
START = dt.datetime(1850, 1, 1)
END = dt.datetime(2150, 1, 1)

def ticks(value):
    delta = value - dt.datetime(1, 1, 1)
    return delta.days * 864000000000 + delta.seconds * 10000000 + delta.microseconds * 10

def matrix_to_xyzw(m):
    m = [[float(m[i][j]) for j in range(3)] for i in range(3)]
    trace = m[0][0] + m[1][1] + m[2][2]
    if trace > 0:
        s = math.sqrt(trace + 1.0) * 2.0
        w = 0.25 * s
        x = (m[2][1] - m[1][2]) / s
        y = (m[0][2] - m[2][0]) / s
        z = (m[1][0] - m[0][1]) / s
    elif m[0][0] > m[1][1] and m[0][0] > m[2][2]:
        s = math.sqrt(1.0 + m[0][0] - m[1][1] - m[2][2]) * 2.0
        w = (m[2][1] - m[1][2]) / s
        x = 0.25 * s
        y = (m[0][1] + m[1][0]) / s
        z = (m[0][2] + m[2][0]) / s
    elif m[1][1] > m[2][2]:
        s = math.sqrt(1.0 + m[1][1] - m[0][0] - m[2][2]) * 2.0
        w = (m[0][2] - m[2][0]) / s
        x = (m[0][1] + m[1][0]) / s
        y = 0.25 * s
        z = (m[1][2] + m[2][1]) / s
    else:
        s = math.sqrt(1.0 + m[2][2] - m[0][0] - m[1][1]) * 2.0
        w = (m[1][0] - m[0][1]) / s
        x = (m[0][2] + m[2][0]) / s
        y = (m[1][2] + m[2][1]) / s
        z = 0.25 * s
    norm = math.sqrt(x*x + y*y + z*z + w*w)
    return (x/norm, y/norm, z/norm, w/norm)

def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()

def main(project_root):
    project = Path(project_root)
    kernels = project / "Tools" / "Ephemeris" / "kernels"
    output = project / "Assets" / "StreamingAssets" / "CelestialEphemeris"
    output.mkdir(parents=True, exist_ok=True)
    kernel_files = [
        kernels / "naif0012.tls",
        kernels / "moon_pa_de440_200625.bpc",
        kernels / "moon_de440_250416.tf",
        kernels / "de440s.bsp",
    ]
    for path in kernel_files:
        if not path.is_file() or path.stat().st_size < 1000:
            raise RuntimeError("Missing or incomplete SPICE kernel: " + str(path))
        spice.furnsh(str(path))

    count = (END - START).days + 1
    final_path = output / "de440s_daily_v1.bin"
    temp_path = output / "de440s_daily_v1.bin.tmp"
    previous_q = None
    previous_et = None
    try:
        with open(temp_path, "wb") as f:
            f.write(HEADER.pack(MAGIC, VERSION, RECORD.size, count))
            for index in range(count):
                utc = START + dt.timedelta(days=index)
                utc_text = utc.strftime("%Y %b %d %H:%M:%S UTC")
                et = float(spice.str2et(utc_text))
                if previous_et is not None and et <= previous_et:
                    raise RuntimeError("Non-monotonic ET at " + utc_text)
                earth, _ = spice.spkezr("EARTH", et, "J2000", "NONE", "SUN")
                moon, _ = spice.spkezr("MOON", et, "J2000", "NONE", "SUN")
                q = matrix_to_xyzw(spice.pxform("MOON_ME", "J2000", et))
                if previous_q is not None and sum(a*b for a, b in zip(q, previous_q)) < 0:
                    q = tuple(-v for v in q)
                f.write(RECORD.pack(ticks(utc), et, *map(float, earth),
                                    *map(float, moon), *q))
                previous_q = q
                previous_et = et
                if index and index % 10000 == 0:
                    print("Processed", index, "of", count, "daily epochs")
        os.replace(temp_path, final_path)
    finally:
        spice.kclear()
        if os.path.exists(temp_path):
            os.remove(temp_path)

    manifest = {
        "format": "LUNEPH1",
        "version": VERSION,
        "binary_file": final_path.name,
        "header_bytes": HEADER.size,
        "record_bytes": RECORD.size,
        "record_count": count,
        "record_layout": [
            ["utc_dotnet_ticks", "int64"],
            ["et_seconds_past_j2000_tdb", "float64"],
            ["earth_heliocentric_state_j2000", "6 float64: km, km/s"],
            ["moon_heliocentric_state_j2000", "6 float64: km, km/s"],
            ["moon_me_to_j2000_quaternion", "4 float64: x,y,z,w"],
        ],
        "utc_start": START.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "utc_end": END.strftime("%Y-%m-%dT%H:%M:%SZ"),
        "sampling": "one UTC day",
        "position_origin": "Sun center",
        "inertial_frame": "J2000 (DE440 products use ICRF orientation)",
        "lunar_body_frame": "MOON_ME alias; current FK maps to MOON_ME_DE440_ME421",
        "lunar_frame_alignment": "closely aligned to LOLA ME/PA DE421 frame",
        "spice_toolkit": spice.tkvrsn("TOOLKIT"),
        "spiceypy_version": spice.__version__,
        "kernel_sha256": {p.name: sha256(p) for p in kernel_files},
        "cache_sha256": sha256(final_path),
        "utc_et_note": "Daily UTC-to-ET anchors are exact at sample epochs. Linear conversion between anchors is for visualization; evaluate exact ET through SPICE for precision work near leap-second boundaries.",
    }
    manifest_path = output / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print("Wrote", final_path, final_path.stat().st_size, "bytes")
    print("Wrote", manifest_path)
    print("Records:", count, "Record size:", RECORD.size)

if __name__ == "__main__":
    root = sys.argv[1] if len(sys.argv) > 1 else Path(__file__).resolve().parents[2]
    main(root)